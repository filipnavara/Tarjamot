using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Tarjamot.Engine;

// x86-specific acceleration. PMADDUBSW multiplies the shifted (unsigned) activations by the signed
// weights and adds adjacent pairs with int16 saturation, which is exactly intgemm's Int8Shift
// product: the sums need neither the 127 * rowSum shift nor the saturation correction. PMADDWD
// (or VPDPWSSD) then widens the pairs to int32.
//
// With AVX-VNNI but without AVX-512 (X86ExactSums), VPDPBUSD instead multiplies four activation
// bytes by four weights and adds them to int32 in one instruction, half the instructions of
// PMADDUBSW + VPDPWSSD. Its sums are exact, without the int16 saturation of pairs, so the few
// pairs that can saturate (the matrix's saturation metadata) are corrected after each tile,
// as the ARM and portable kernels correct theirs (CorrectSaturation).
//
// Weights are packed in panels of 16 output columns. Each step broadcasts four activation bytes
// against 64 weight bytes (16 columns x 4 inputs), so every int32 lane is one output column and
// no horizontal reduction is needed.
internal static partial class Int8Gemm
{
	const int X86PanelWidth = 16;

	/// <summary>Whether the x86 panel kernels compute exact sums (VPDPBUSD) and correct the saturating pairs.</summary>
	static bool X86ExactSums => AvxVnni.IsSupported && !(Vector512.IsHardwareAccelerated && Avx512BW.IsSupported);

	// The products of up to eight activation rows with one panel. Unlike stackalloc, a local of this
	// type lets the JIT move the loops that use it to tier 1 (OSR does not support stackalloc), so
	// they are optimized with dynamic PGO and the small helpers below are inlined.
	[InlineArray(8 * X86PanelWidth)]
	struct PanelSums
	{
		int element;
	}

	static void MultiplyX86(QuantizedActivations a, int rows, QuantizedMatrix w, int outputs, float unquant, float[] preparedBias, float[] y, bool parallel)
	{
		int width = w.Cols;
		int panels = (outputs + X86PanelWidth - 1) / X86PanelWidth;
		long work = (long)rows * width * outputs;
		int chunks = ParallelChunks(work, panels, Int8Kernel.X86, parallel);
		if (chunks > 1)
		{
			Parallel.For(0, chunks, c => ComputeRange(panels * c / chunks, panels * (c + 1) / chunks));
		}
		else
		{
			ComputeRange(0, panels);
		}

		void ComputeRange(int startPanel, int endPanel)
		{
			PanelSums panelSums = default;
			Span<int> sums = panelSums;
			var scale = Vector256.Create(unquant);
			int rowTile = Vector512.IsHardwareAccelerated && Avx512BW.IsSupported ? 8 : 4;
			int rowBlock = Math.Max(rowTile, 16384 / width & ~(rowTile - 1));
			for (int blockStart = 0; blockStart < rows; blockStart += rowBlock)
			{
				int blockEnd = Math.Min(rows, blockStart + rowBlock);
				for (int p = startPanel; p < endPanel; p++)
				{
					int j = p * X86PanelWidth;
					int count = Math.Min(X86PanelWidth, outputs - j);
					ref sbyte panel = ref w.Data[j * width];
					var (bias0, bias1) = LoadBias(preparedBias, j, count);
					bool correct = X86ExactSums && w.SaturationOffsets[j] != w.SaturationOffsets[j + count];
					int r = blockStart;
					if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
					{
						for (; r + 8 <= blockEnd; r += 8)
						{
							DotAvx512x8(ref a.Shifted[r * width], width, ref panel, sums);
							for (int k = 0; k < 8; k++)
								StoreX86(sums.Slice(k * X86PanelWidth, X86PanelWidth), scale, bias0, bias1, y.AsSpan((r + k) * outputs + j, count));
						}
					}
					for (; r + 4 <= blockEnd; r += 4)
					{
						DotX86(ref a.Shifted[r * width], width, 4, ref panel, sums);
						if (correct)
							CorrectSaturation(w, a.Shifted, r, 4, j, count, sums);
						for (int k = 0; k < 4; k++)
							StoreX86(sums.Slice(k * X86PanelWidth, X86PanelWidth), scale, bias0, bias1, y.AsSpan((r + k) * outputs + j, count));
					}
					if (r + 2 <= blockEnd)
					{
						DotX86(ref a.Shifted[r * width], width, 2, ref panel, sums);
						if (correct)
							CorrectSaturation(w, a.Shifted, r, 2, j, count, sums);
						StoreX86(sums[..X86PanelWidth], scale, bias0, bias1, y.AsSpan(r * outputs + j, count));
						StoreX86(sums.Slice(X86PanelWidth, X86PanelWidth), scale, bias0, bias1, y.AsSpan((r + 1) * outputs + j, count));
						r += 2;
					}
					if (r < blockEnd)
					{
						DotX86(ref a.Shifted[r * width], width, 1, ref panel, sums);
						if (correct)
							CorrectSaturation(w, a.Shifted, r, 1, j, count, sums);
						StoreX86(sums[..X86PanelWidth], scale, bias0, bias1, y.AsSpan(r * outputs + j, count));
					}
				}
			}
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	static (Vector256<float>, Vector256<float>) LoadBias(float[] preparedBias, int j, int count) =>
		count == X86PanelWidth
			? (Vector256.Create(preparedBias.AsSpan(j, 8)), Vector256.Create(preparedBias.AsSpan(j + 8, 8)))
			: LoadPartialBias(preparedBias, j, count);

	static (Vector256<float>, Vector256<float>) LoadPartialBias(float[] preparedBias, int j, int count)
	{
		Span<float> bias = stackalloc float[X86PanelWidth];
		bias.Clear();
		preparedBias.AsSpan(j, count).CopyTo(bias);
		return (Vector256.Create(bias[..8]), Vector256.Create(bias[8..]));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	static (Vector256<float>, Vector256<float>) ScoreX86(ReadOnlySpan<int> sums, Vector256<float> scale, Vector256<float> bias0, Vector256<float> bias1)
	{
		ref int s = ref MemoryMarshal.GetReference(sums);
		return (Vector256.ConvertToSingle(Vector256.LoadUnsafe(ref s)) * scale + bias0,
			Vector256.ConvertToSingle(Vector256.LoadUnsafe(ref s, 8)) * scale + bias1);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	static void StoreX86(ReadOnlySpan<int> sums, Vector256<float> scale, Vector256<float> bias0, Vector256<float> bias1, Span<float> output)
	{
		var (y0, y1) = ScoreX86(sums, scale, bias0, bias1);
		if (output.Length == X86PanelWidth)
		{
			ref float o = ref MemoryMarshal.GetReference(output);
			y0.StoreUnsafe(ref o);
			y1.StoreUnsafe(ref o, 8);
			return;
		}
		StorePartial(y0, y1, output);
	}

	static void StorePartial(Vector256<float> y0, Vector256<float> y1, Span<float> output)
	{
		for (int lane = 0; lane < output.Length; lane++)
			output[lane] = lane < 8 ? y0[lane] : y1[lane - 8];
	}

	static void ArgMaxX86Range(QuantizedActivations a, int rows, QuantizedMatrix w, float unquant,
		float[] preparedBias, int excluded, ArgMaxScratch scratch, int chunkBase, int start, int end)
	{
		int width = w.Cols;
		PanelSums panelSums = default;
		Span<int> sums = panelSums;
		var scale = Vector256.Create(unquant);
		int rowTile = Vector512.IsHardwareAccelerated && Avx512BW.IsSupported ? 8 : 4;
		int rowBlock = Math.Max(rowTile, 16384 / width & ~(rowTile - 1));
		for (int blockStart = 0; blockStart < rows; blockStart += rowBlock)
		{
			int blockEnd = Math.Min(rows, blockStart + rowBlock);
			for (int j = start; j < end; j += X86PanelWidth)
			{
				int count = Math.Min(X86PanelWidth, end - j);
				ref sbyte panel = ref w.Data[j * width];
				var (bias0, bias1) = LoadBias(preparedBias, j, count);
				bool correct = X86ExactSums && w.SaturationOffsets[j] != w.SaturationOffsets[j + count];
				int r = blockStart;
				if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
				{
					for (; r + 8 <= blockEnd; r += 8)
					{
						DotAvx512x8(ref a.Shifted[r * width], width, ref panel, sums);
						for (int k = 0; k < 8; k++)
							Consider(chunkBase + r + k, sums.Slice(k * X86PanelWidth, X86PanelWidth), j, count, bias0, bias1);
					}
				}
				for (; r + 4 <= blockEnd; r += 4)
				{
					DotX86(ref a.Shifted[r * width], width, 4, ref panel, sums);
					if (correct)
						CorrectSaturation(w, a.Shifted, r, 4, j, count, sums);
					for (int k = 0; k < 4; k++)
						Consider(chunkBase + r + k, sums.Slice(k * X86PanelWidth, X86PanelWidth), j, count, bias0, bias1);
				}
				if (r + 2 <= blockEnd)
				{
					DotX86(ref a.Shifted[r * width], width, 2, ref panel, sums);
					if (correct)
						CorrectSaturation(w, a.Shifted, r, 2, j, count, sums);
					Consider(chunkBase + r, sums[..X86PanelWidth], j, count, bias0, bias1);
					Consider(chunkBase + r + 1, sums.Slice(X86PanelWidth, X86PanelWidth), j, count, bias0, bias1);
					r += 2;
				}
				if (r < blockEnd)
				{
					DotX86(ref a.Shifted[r * width], width, 1, ref panel, sums);
					if (correct)
						CorrectSaturation(w, a.Shifted, r, 1, j, count, sums);
					Consider(chunkBase + r, sums[..X86PanelWidth], j, count, bias0, bias1);
				}
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		void Consider(int index, ReadOnlySpan<int> rowSums, int j, int count, Vector256<float> bias0, Vector256<float> bias1)
		{
			var (s0, s1) = ScoreX86(rowSums, scale, bias0, bias1);
			// Most panels cannot beat the row's current winner (NaNs compare false).
			var best = Vector256.Create(scratch.Scores[index]);
			if ((Vector256.GreaterThan(s0, best) | Vector256.GreaterThan(s1, best)) != Vector256<float>.Zero)
				ConsiderPanel(scratch, index, j, count, s0, s1, excluded);
		}
	}

	static void ConsiderPanel(ArgMaxScratch scratch, int index, int j, int count, Vector256<float> s0, Vector256<float> s1, int excluded)
	{
		scratch.Consider(index, j, s0.GetLower(), Math.Min(count, 4), excluded);
		if (count > 4)
			scratch.Consider(index, j + 4, s0.GetUpper(), Math.Min(count - 4, 4), excluded);
		if (count > 8)
			scratch.Consider(index, j + 8, s1.GetLower(), Math.Min(count - 8, 4), excluded);
		if (count > 12)
			scratch.Consider(index, j + 12, s1.GetUpper(), count - 12, excluded);
	}

	// Turns exact sums into intgemm's: each pair of the panel's outputs that can saturate is replaced
	// by its int16-saturated value, for each of the tile's activation rows. Pairs are adjacent inputs
	// (k even), so both weights lie in the same four-input group of the packed panel.
	static void CorrectSaturation(QuantizedMatrix w, byte[] shifted, int firstRow, int rows, int j, int count, Span<int> sums)
	{
		int width = w.Cols;
		for (int lane = 0; lane < count; lane++)
		{
			int output = j + lane;
			for (int p = w.SaturationOffsets[output]; p < w.SaturationOffsets[output + 1]; p++)
			{
				int k = w.SaturationPairs[p];
				int offset = w.PackedOffset(output, k);
				int w0 = w.Data[offset], w1 = w.Data[offset + 1];
				for (int r = 0; r < rows; r++)
				{
					int input = (firstRow + r) * width + k;
					int pair = shifted[input] * w0 + shifted[input + 1] * w1;
					sums[r * X86PanelWidth + lane] += Math.Clamp(pair, short.MinValue, short.MaxValue) - pair;
				}
			}
		}
	}

	// sums[k * 16 + c] = product of activation row k (of count: 1, 2 or 4) with panel column c.
	static void DotX86(ref byte u, int width, int count, ref sbyte panel, Span<int> sums)
	{
		if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
		{
			if (count == 4)
				DotAvx512x4(ref u, width, ref panel, sums);
			else if (count == 2)
				DotAvx512x2(ref u, width, ref panel, sums);
			else
				DotAvx512x1(ref u, width, ref panel, sums);
		}
		else
		{
			if (count == 4)
				DotAvx2x4(ref u, width, ref panel, sums);
			else if (count == 2)
				DotAvx2x2(ref u, width, ref panel, sums);
			else
				DotAvx2x1(ref u, width, ref panel, sums);
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	static Vector512<byte> Broadcast512(ref byte u, nuint offset) => Vector512.Create(Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref u, offset))).AsByte();

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	static Vector256<byte> Broadcast256(ref byte u, nuint offset) => Vector256.Create(Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref u, offset))).AsByte();

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	static Vector512<int> Accumulate512(Vector512<int> acc, Vector512<byte> x, Vector512<sbyte> w, Vector512<short> ones) =>
		acc + Avx512BW.MultiplyAddAdjacent(Avx512BW.MultiplyAddAdjacent(x, w), ones);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	static Vector256<int> Accumulate256(Vector256<int> acc, Vector256<byte> x, Vector256<sbyte> w, Vector256<short> ones) =>
		AvxVnni.IsSupported
			? AvxVnni.MultiplyWideningAndAdd(acc, Avx2.MultiplyAddAdjacent(x, w), ones)
			: acc + Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(x, w), ones);

	// The AVX2 panel kernels' step: exact with X86ExactSums (corrected by CorrectSaturation), otherwise
	// saturating like Accumulate256. The condition is written out rather than calling X86ExactSums, as
	// tier-0 code (the first translations) does not inline, and this runs for every four inputs.
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	static Vector256<int> AccumulatePanel256(Vector256<int> acc, Vector256<byte> x, Vector256<sbyte> w, Vector256<short> ones) =>
		AvxVnni.IsSupported && !(Vector512.IsHardwareAccelerated && Avx512BW.IsSupported)
			? AvxVnni.MultiplyWideningAndAdd(acc, x, w)
			: Accumulate256(acc, x, w, ones);

	// The loops keep one reference per activation row and advance the panel reference, so each
	// step needs no address arithmetic beyond the loop counter: the products are bound by the
	// vector multiply ports, but with per-row offset computations the loop came close to the
	// front end's limit as well.
	// Eight AVX-512 accumulators share each weight load across twice as many rows.
	static void DotAvx512x8(ref byte u, int width, ref sbyte panel, Span<int> sums)
	{
		var ones = Vector512<short>.One;
		ref byte u1 = ref Unsafe.Add(ref u, width);
		ref byte u2 = ref Unsafe.Add(ref u, 2 * width);
		ref byte u3 = ref Unsafe.Add(ref u, 3 * width);
		ref byte u4 = ref Unsafe.Add(ref u, 4 * width);
		ref byte u5 = ref Unsafe.Add(ref u, 5 * width);
		ref byte u6 = ref Unsafe.Add(ref u, 6 * width);
		ref byte u7 = ref Unsafe.Add(ref u, 7 * width);
		Vector512<int> acc0 = default, acc1 = default, acc2 = default, acc3 = default;
		Vector512<int> acc4 = default, acc5 = default, acc6 = default, acc7 = default;
		for (nuint k = 0, end = (nuint)width; k < end; k += 4)
		{
			var wv = Vector512.LoadUnsafe(ref panel);
			panel = ref Unsafe.Add(ref panel, 4 * X86PanelWidth);
			acc0 = Accumulate512(acc0, Broadcast512(ref u, k), wv, ones);
			acc1 = Accumulate512(acc1, Broadcast512(ref u1, k), wv, ones);
			acc2 = Accumulate512(acc2, Broadcast512(ref u2, k), wv, ones);
			acc3 = Accumulate512(acc3, Broadcast512(ref u3, k), wv, ones);
			acc4 = Accumulate512(acc4, Broadcast512(ref u4, k), wv, ones);
			acc5 = Accumulate512(acc5, Broadcast512(ref u5, k), wv, ones);
			acc6 = Accumulate512(acc6, Broadcast512(ref u6, k), wv, ones);
			acc7 = Accumulate512(acc7, Broadcast512(ref u7, k), wv, ones);
		}
		ref int s = ref MemoryMarshal.GetReference(sums);
		acc0.StoreUnsafe(ref s);
		acc1.StoreUnsafe(ref s, 16);
		acc2.StoreUnsafe(ref s, 32);
		acc3.StoreUnsafe(ref s, 48);
		acc4.StoreUnsafe(ref s, 64);
		acc5.StoreUnsafe(ref s, 80);
		acc6.StoreUnsafe(ref s, 96);
		acc7.StoreUnsafe(ref s, 112);
	}

	static void DotAvx512x4(ref byte u, int width, ref sbyte panel, Span<int> sums)
	{
		var ones = Vector512<short>.One;
		ref byte u1 = ref Unsafe.Add(ref u, width);
		ref byte u2 = ref Unsafe.Add(ref u, 2 * width);
		ref byte u3 = ref Unsafe.Add(ref u, 3 * width);
		Vector512<int> acc0 = default, acc1 = default, acc2 = default, acc3 = default;
		for (nuint k = 0, end = (nuint)width; k < end; k += 4)
		{
			var wv = Vector512.LoadUnsafe(ref panel);
			panel = ref Unsafe.Add(ref panel, 4 * X86PanelWidth);
			acc0 = Accumulate512(acc0, Broadcast512(ref u, k), wv, ones);
			acc1 = Accumulate512(acc1, Broadcast512(ref u1, k), wv, ones);
			acc2 = Accumulate512(acc2, Broadcast512(ref u2, k), wv, ones);
			acc3 = Accumulate512(acc3, Broadcast512(ref u3, k), wv, ones);
		}
		ref int s = ref MemoryMarshal.GetReference(sums);
		acc0.StoreUnsafe(ref s);
		acc1.StoreUnsafe(ref s, 16);
		acc2.StoreUnsafe(ref s, 32);
		acc3.StoreUnsafe(ref s, 48);
	}

	// The two rows left over after four-row steps share each weight load as well.
	static void DotAvx512x2(ref byte u, int width, ref sbyte panel, Span<int> sums)
	{
		var ones = Vector512<short>.One;
		ref byte u1 = ref Unsafe.Add(ref u, width);
		Vector512<int> acc0 = default, acc1 = default;
		for (nuint k = 0, end = (nuint)width; k < end; k += 4)
		{
			var wv = Vector512.LoadUnsafe(ref panel);
			panel = ref Unsafe.Add(ref panel, 4 * X86PanelWidth);
			acc0 = Accumulate512(acc0, Broadcast512(ref u, k), wv, ones);
			acc1 = Accumulate512(acc1, Broadcast512(ref u1, k), wv, ones);
		}
		ref int s = ref MemoryMarshal.GetReference(sums);
		acc0.StoreUnsafe(ref s);
		acc1.StoreUnsafe(ref s, 16);
	}

	// A single row has no reuse; two accumulators hide the multiply latency.
	static void DotAvx512x1(ref byte u, int width, ref sbyte panel, Span<int> sums)
	{
		var ones = Vector512<short>.One;
		Vector512<int> acc0 = default, acc1 = default;
		nuint k = 0, end = (nuint)width;
		for (; k + 8 <= end; k += 8)
		{
			acc0 = Accumulate512(acc0, Broadcast512(ref u, k), Vector512.LoadUnsafe(ref panel), ones);
			acc1 = Accumulate512(acc1, Broadcast512(ref u, k + 4), Vector512.LoadUnsafe(ref panel, 4 * X86PanelWidth), ones);
			panel = ref Unsafe.Add(ref panel, 8 * X86PanelWidth);
		}
		if (k < end)
			acc0 = Accumulate512(acc0, Broadcast512(ref u, k), Vector512.LoadUnsafe(ref panel), ones);
		(acc0 + acc1).StoreUnsafe(ref MemoryMarshal.GetReference(sums));
	}

	static void DotAvx2x4(ref byte u, int width, ref sbyte panel, Span<int> sums)
	{
		var ones = Vector256<short>.One;
		ref byte u1 = ref Unsafe.Add(ref u, width);
		ref byte u2 = ref Unsafe.Add(ref u, 2 * width);
		ref byte u3 = ref Unsafe.Add(ref u, 3 * width);
		Vector256<int> acc0 = default, acc1 = default, acc2 = default, acc3 = default;
		Vector256<int> acc4 = default, acc5 = default, acc6 = default, acc7 = default;
		for (nuint k = 0, end = (nuint)width; k < end; k += 4)
		{
			var w0 = Vector256.LoadUnsafe(ref panel);
			var w1 = Vector256.LoadUnsafe(ref panel, 32);
			panel = ref Unsafe.Add(ref panel, 4 * X86PanelWidth);
			var x0 = Broadcast256(ref u, k);
			var x1 = Broadcast256(ref u1, k);
			var x2 = Broadcast256(ref u2, k);
			var x3 = Broadcast256(ref u3, k);
			acc0 = AccumulatePanel256(acc0, x0, w0, ones);
			acc1 = AccumulatePanel256(acc1, x0, w1, ones);
			acc2 = AccumulatePanel256(acc2, x1, w0, ones);
			acc3 = AccumulatePanel256(acc3, x1, w1, ones);
			acc4 = AccumulatePanel256(acc4, x2, w0, ones);
			acc5 = AccumulatePanel256(acc5, x2, w1, ones);
			acc6 = AccumulatePanel256(acc6, x3, w0, ones);
			acc7 = AccumulatePanel256(acc7, x3, w1, ones);
		}
		ref int s = ref MemoryMarshal.GetReference(sums);
		acc0.StoreUnsafe(ref s);
		acc1.StoreUnsafe(ref s, 8);
		acc2.StoreUnsafe(ref s, 16);
		acc3.StoreUnsafe(ref s, 24);
		acc4.StoreUnsafe(ref s, 32);
		acc5.StoreUnsafe(ref s, 40);
		acc6.StoreUnsafe(ref s, 48);
		acc7.StoreUnsafe(ref s, 56);
	}

	static void DotAvx2x2(ref byte u, int width, ref sbyte panel, Span<int> sums)
	{
		var ones = Vector256<short>.One;
		ref byte u1 = ref Unsafe.Add(ref u, width);
		Vector256<int> acc0 = default, acc1 = default, acc2 = default, acc3 = default;
		for (nuint k = 0, end = (nuint)width; k < end; k += 4)
		{
			var w0 = Vector256.LoadUnsafe(ref panel);
			var w1 = Vector256.LoadUnsafe(ref panel, 32);
			panel = ref Unsafe.Add(ref panel, 4 * X86PanelWidth);
			var x0 = Broadcast256(ref u, k);
			var x1 = Broadcast256(ref u1, k);
			acc0 = AccumulatePanel256(acc0, x0, w0, ones);
			acc1 = AccumulatePanel256(acc1, x0, w1, ones);
			acc2 = AccumulatePanel256(acc2, x1, w0, ones);
			acc3 = AccumulatePanel256(acc3, x1, w1, ones);
		}
		ref int s = ref MemoryMarshal.GetReference(sums);
		acc0.StoreUnsafe(ref s);
		acc1.StoreUnsafe(ref s, 8);
		acc2.StoreUnsafe(ref s, 16);
		acc3.StoreUnsafe(ref s, 24);
	}

	static void DotAvx2x1(ref byte u, int width, ref sbyte panel, Span<int> sums)
	{
		var ones = Vector256<short>.One;
		Vector256<int> acc0 = default, acc1 = default;
		for (nuint k = 0, end = (nuint)width; k < end; k += 4)
		{
			var x = Broadcast256(ref u, k);
			acc0 = AccumulatePanel256(acc0, x, Vector256.LoadUnsafe(ref panel), ones);
			acc1 = AccumulatePanel256(acc1, x, Vector256.LoadUnsafe(ref panel, 32), ones);
			panel = ref Unsafe.Add(ref panel, 4 * X86PanelWidth);
		}
		ref int s = ref MemoryMarshal.GetReference(sums);
		acc0.StoreUnsafe(ref s);
		acc1.StoreUnsafe(ref s, 8);
	}

	// Row-major weights (the shortlisted output layer and widths that cannot be packed): up to
	// four activation rows share each weight load, and each row is reduced horizontally at the end.
	static void DotX86RowMajor(ReadOnlySpan<byte> u, int count, ReadOnlySpan<sbyte> w, Span<int> sums)
	{
		int width = w.Length;
		ref byte ru = ref MemoryMarshal.GetReference(u);
		ref sbyte rw = ref MemoryMarshal.GetReference(w);
		var ones = Vector256<short>.One;
		Vector256<int> acc0 = default, acc1 = default, acc2 = default, acc3 = default;
		int i = 0;
		for (; i + 32 <= width; i += 32)
		{
			var wv = Vector256.LoadUnsafe(ref rw, (nuint)i);
			acc0 = Accumulate256(acc0, Vector256.LoadUnsafe(ref ru, (nuint)i), wv, ones);
			if (count > 1)
				acc1 = Accumulate256(acc1, Vector256.LoadUnsafe(ref ru, (nuint)(width + i)), wv, ones);
			if (count > 2)
				acc2 = Accumulate256(acc2, Vector256.LoadUnsafe(ref ru, (nuint)(2 * width + i)), wv, ones);
			if (count > 3)
				acc3 = Accumulate256(acc3, Vector256.LoadUnsafe(ref ru, (nuint)(3 * width + i)), wv, ones);
		}
		sums[0] = Vector256.Sum(acc0) + SaturatedTail(u[..width], w, i);
		if (count > 1)
			sums[1] = Vector256.Sum(acc1) + SaturatedTail(u.Slice(width, width), w, i);
		if (count > 2)
			sums[2] = Vector256.Sum(acc2) + SaturatedTail(u.Slice(2 * width, width), w, i);
		if (count > 3)
			sums[3] = Vector256.Sum(acc3) + SaturatedTail(u.Slice(3 * width, width), w, i);
	}

	static int SaturatedTail(ReadOnlySpan<byte> u, ReadOnlySpan<sbyte> w, int start)
	{
		int sum = 0;
		int i = start;
		for (; i + 1 < w.Length; i += 2)
			sum += Math.Clamp(u[i] * w[i] + u[i + 1] * w[i + 1], short.MinValue, short.MaxValue);
		if (i < w.Length)
			sum += u[i] * w[i];
		return sum;
	}
}
