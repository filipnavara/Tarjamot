using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Tarjamot.Engine;

/// <summary>Implementation of the int8 dot products.</summary>
internal enum Int8Kernel
{
	/// <summary>Vector128/Vector256 code for any platform.</summary>
	Portable,

	/// <summary>ARM SDOT (dot product extension).</summary>
	ArmDotProduct,

	/// <summary>x86 PMADDUBSW (AVX2, AVX-512BW where available).</summary>
	X86,
}

/// <summary>
/// Quantized activations of one matrix product: int8 values and, for the portable kernel, the
/// same values widened to int16. The x86 kernel has only the values shifted to unsigned (value + 127).
/// </summary>
internal sealed class QuantizedActivations
{
	public sbyte[] Values = [];
	public short[] Widened = [];
	public byte[] Shifted = [];

	internal long RetainedBytes => 3 * 24L + ((Values.LongLength + 7) & ~7L) + ((sizeof(short) * Widened.LongLength + 7) & ~7L) + ((Shifted.LongLength + 7) & ~7L);
}

/// <summary>
/// Int8 matrix products with exact int32 accumulation.
/// <para>
/// There is no cross-platform widening dot product (TensorPrimitives.Dot&lt;sbyte&gt; and
/// Vector128.Dot accumulate in sbyte and overflow), so the portable kernel builds one from
/// Vector128/Vector256 operations: an int8 product fits in int16 and so does the sum of two of
/// them (at most 2 * 127 * 127), which is then widened to int32. On ARM with the dot product
/// extension, SDOT does this in one instruction and is used instead.
/// </para>
/// </summary>
internal static partial class Int8Gemm
{
	// Parallel.For costs far more than a small product, and idle thread pool workers spin between
	// bursts of work, so only large products are split: the encoder's and the full-vocabulary output
	// layer's, not the decoder's per-step products. Each chunk gets at least MinChunkWork
	// multiply-adds. Compared with splitting every product above 2^20 into 4 chunks per core, this
	// cut the CPU time by 27-28% (an email and a Wikipedia article on an Apple M5), with wall time
	// within measurement noise.
	const long MinParallelWork = 1L << 24;
	const long MinChunkWork = 1L << 23;

	// The x86 kernel computes a chunk in a fraction of the time, so smaller products of the decoder's
	// sentence batches are split too, into at most one chunk per physical core (SMT siblings share
	// the vector multiply units). On a Ryzen 9 9950X this reduced both the wall and the CPU time of
	// WithinProducts compared with the thresholds above (Czech article -10% / -4%, Chinese article
	// -7% / -11%). Smaller chunks alone, with up to 4 chunks per logical processor, cost more CPU time.
	const long X86MinParallelWork = 1L << 22;
	const long X86MinChunkWork = 1L << 21;

	/// <summary>The number of chunks to split a product into, 1 to compute it on the calling thread.</summary>
	static int ParallelChunks(long work, int partitions, Int8Kernel kernel, bool parallel)
	{
		bool x86 = Avx2.IsSupported && kernel == Int8Kernel.X86;
		if (!parallel || work < (x86 ? X86MinParallelWork : MinParallelWork))
			return 1;
		long maximum = x86 ? ProcessorTopology.PhysicalCores : Environment.ProcessorCount * 4L;
		return (int)Math.Max(1, Math.Min(Math.Min(work / (x86 ? X86MinChunkWork : MinChunkWork), maximum), partitions));
	}

	/// <summary>The fastest kernel for this machine: SDOT or AVX2 where available, otherwise portable.</summary>
	public static Int8Kernel DefaultKernel => Dp.IsSupported ? Int8Kernel.ArmDotProduct : Avx2.IsSupported ? Int8Kernel.X86 : Int8Kernel.Portable;

	/// <summary>
	/// Whether a kernel's products need <see cref="QuantizedMatrix.SaturationPairs"/>: the x86 kernel's
	/// PMADDUBSW saturates the pairs itself, except with exact VPDPBUSD sums (X86ExactSums); the others
	/// compute exact sums and correct them.
	/// </summary>
	public static bool UsesSaturationMetadata(Int8Kernel kernel) => !(Avx2.IsSupported && kernel == Int8Kernel.X86) || X86ExactSums;

	static void RequireProductMetadata(QuantizedMatrix w, Int8Kernel kernel)
	{
		if (!w.HasRowSums)
			throw new NotSupportedException("The matrix was loaded only to be looked up, without row sums.");
		if (UsesSaturationMetadata(kernel) && !w.HasSaturationMetadata)
			throw new NotSupportedException("The matrix was loaded for the x86 kernel, without saturation metadata.");
	}

	/// <summary>Outputs per packed weight panel for a kernel, or 0 when it uses row-major weights.</summary>
	public static int PanelWidth(Int8Kernel kernel) => kernel switch
	{
		Int8Kernel.ArmDotProduct => 4,
		Int8Kernel.X86 => X86PanelWidth,
		_ => 0,
	};

	/// <summary>Int8Shift::PrepareA with a fixed alpha: round to nearest even, saturate to [-127, 127].</summary>
	/// <remarks>The x86 kernel reads only the shifted values, so only those are stored for it.</remarks>
	public static void Quantize(ReadOnlySpan<float> x, float alpha, QuantizedActivations destination, Int8Kernel kernel)
	{
		if (Avx2.IsSupported && kernel == Int8Kernel.X86)
		{
			if (destination.Shifted.Length < x.Length)
				destination.Shifted = new byte[x.Length];
			// a + 127 lies in [0, 254].
			QuantizeBytes(x, alpha, 127, destination.Shifted.AsSpan(0, x.Length));
			return;
		}

		if (destination.Values.Length < x.Length)
			destination.Values = new sbyte[x.Length];
		var values = destination.Values.AsSpan(0, x.Length);
		QuantizeBytes(x, alpha, 0, MemoryMarshal.AsBytes(values));
		if (kernel != Int8Kernel.Portable)
			return;
		if (destination.Widened.Length < x.Length)
			destination.Widened = new short[x.Length];
		TensorPrimitives.ConvertChecked<sbyte, short>(values, destination.Widened.AsSpan(0, x.Length));
	}

	// Stores the low byte of each quantized value plus the offset.
	static void QuantizeBytes(ReadOnlySpan<float> x, float alpha, int offset, Span<byte> output)
	{
		int i = 0;
		if (Vector128.IsHardwareAccelerated)
		{
			ref float input = ref MemoryMarshal.GetReference(x);
			ref byte destination = ref MemoryMarshal.GetReference(output);
			var multiplier = Vector128.Create(alpha);
			var minimum = Vector128.Create(-127f);
			var maximum = Vector128.Create(127f);
			var shift = Vector128.Create(offset);
			for (; i + 16 <= x.Length; i += 16)
			{
				var a = QuantizeVector(Vector128.LoadUnsafe(ref input, (nuint)i), multiplier, minimum, maximum) + shift;
				var b = QuantizeVector(Vector128.LoadUnsafe(ref input, (nuint)i + 4), multiplier, minimum, maximum) + shift;
				var c = QuantizeVector(Vector128.LoadUnsafe(ref input, (nuint)i + 8), multiplier, minimum, maximum) + shift;
				var d = QuantizeVector(Vector128.LoadUnsafe(ref input, (nuint)i + 12), multiplier, minimum, maximum) + shift;
				Vector128.Narrow(Vector128.Narrow(a, b), Vector128.Narrow(c, d)).AsByte().StoreUnsafe(ref destination, (nuint)i);
			}
		}
		for (; i < x.Length; i++)
			output[i] = (byte)((sbyte)Math.Clamp(MathF.Round(x[i] * alpha), -127f, 127f) + offset);
	}

	static Vector128<int> QuantizeVector(Vector128<float> value, Vector128<float> multiplier, Vector128<float> minimum, Vector128<float> maximum) =>
		Vector128.ConvertToInt32(Vector128.Clamp(Vector128.Round(value * multiplier), minimum, maximum));

	/// <summary>
	/// y[r, j] = S * unquant + preparedBias[row(j)] for r &lt; rows, where row(j) is weightRows[j]
	/// (or j) and S is the integer product as intgemm's Int8Shift computes it: activations shifted
	/// to unsigned (a + 127), pairs of products added with int16 saturation. The shift is undone by
	/// the prepared bias (bias - 127 * rowSum / (alpha * quantMult), see <see cref="PrepareBias"/>).
	/// </summary>
	public static void Multiply(QuantizedActivations a, int rows, QuantizedMatrix w, int[]? weightRows, int outputs, float unquant, float[] preparedBias, float[] y, Int8Kernel kernel, bool parallel)
	{
		RequireProductMetadata(w, kernel);
		// The IsSupported checks are JIT-time constants, so each platform keeps only its own branch.
		if (w.IsPacked)
		{
			if (w.PanelWidth != PanelWidth(kernel))
				throw new NotSupportedException("The matrix is packed for a different kernel.");
			if (Avx2.IsSupported && kernel == Int8Kernel.X86)
			{
				if (weightRows != null)
					throw new NotSupportedException("The x86 kernel requires row-major weights for selected rows.");
				MultiplyX86(a, rows, w, outputs, unquant, preparedBias, y, parallel);
			}
			else if (Dp.IsSupported && kernel == Int8Kernel.ArmDotProduct)
				MultiplyPacked(a, rows, w, weightRows, outputs, unquant, preparedBias, y, parallel);
			else
				throw new PlatformNotSupportedException();
			return;
		}
		MultiplyRowMajor(a, rows, w, weightRows, outputs, unquant, preparedBias, y, kernel, parallel);
	}

	static void MultiplyRowMajor(QuantizedActivations a, int rows, QuantizedMatrix w, int[]? weightRows, int outputs, float unquant, float[] preparedBias, float[] y, Int8Kernel kernel, bool parallel)
	{
		int width = w.Cols;
		long work = (long)rows * width * outputs;
		int chunks = ParallelChunks(work, outputs, kernel, parallel);
		if (chunks > 1)
		{
			Parallel.For(0, chunks, c => ComputeRange(outputs * c / chunks, outputs * (c + 1) / chunks));
		}
		else
		{
			ComputeRange(0, outputs);
		}

		// Rows are processed in blocks small enough to stay in L1 cache while the weight rows
		// stream through; each weight vector load is shared by up to four activation rows.
		void ComputeRange(int start, int end)
		{
			Span<int> sums = stackalloc int[8];
			int rowBlock = Math.Max(4, 16384 / width & ~3);
			for (int blockStart = 0; blockStart < rows; blockStart += rowBlock)
			{
				int blockEnd = Math.Min(rows, blockStart + rowBlock);
				int j = start;
				// A 4 x 2 tile shares activation loads between two output columns as well.
				if (Dp.IsSupported && kernel == Int8Kernel.ArmDotProduct)
				{
					for (; j + 1 < end; j += 2)
					{
						int row0 = weightRows?[j] ?? j, row1 = weightRows?[j + 1] ?? j + 1;
						var w0 = w.Data.AsSpan(row0 * width, width);
						var w1 = w.Data.AsSpan(row1 * width, width);
						var pairs0 = w.SaturationPairs.AsSpan(w.SaturationOffsets[row0], w.SaturationOffsets[row0 + 1] - w.SaturationOffsets[row0]);
						var pairs1 = w.SaturationPairs.AsSpan(w.SaturationOffsets[row1], w.SaturationOffsets[row1 + 1] - w.SaturationOffsets[row1]);
						for (int r = blockStart; r < blockEnd; r += 4)
						{
							int count = Math.Min(4, blockEnd - r);
							if (count == 4)
								DotArm4x2(a.Values.AsSpan(r * width, 4 * width), w0, w1, sums);
							else
							{
								Dot(a, r, count, w0, sums, kernel);
								Dot(a, r, count, w1, sums[4..], kernel);
							}
							for (int k = 0; k < count; k++)
							{
								var input = a.Values.AsSpan((r + k) * width, width);
								int sum0 = sums[k] + 127 * w.RowSums[row0] + SaturationCorrection(input, w0, pairs0);
								int sum1 = sums[4 + k] + 127 * w.RowSums[row1] + SaturationCorrection(input, w1, pairs1);
								y[(r + k) * outputs + j] = sum0 * unquant + preparedBias[row0];
								y[(r + k) * outputs + j + 1] = sum1 * unquant + preparedBias[row1];
							}
						}
					}
				}
				for (; j < end; j++)
				{
					int row = weightRows?[j] ?? j;
					var weights = w.Data.AsSpan(row * width, width);
					float b = preparedBias[row];
					int shift = 127 * w.RowSums[row];
					var pairs = RowPairs(w, row, kernel);
					for (int r = blockStart; r < blockEnd; r += 4)
					{
						int count = Math.Min(4, blockEnd - r);
						Dot(a, r, count, weights, sums, kernel);
						for (int k = 0; k < count; k++)
						{
							int sum = Avx2.IsSupported && kernel == Int8Kernel.X86 ? sums[k] : sums[k] + shift + SaturationCorrection(a.Values.AsSpan((r + k) * width, width), weights, pairs);
							y[(r + k) * outputs + j] = sum * unquant + b;
						}
					}
				}
			}
		}
	}

	// The saturating pairs of a row for the kernels that correct them; none for the x86 kernel, whose
	// matrices may have no metadata.
	static ReadOnlySpan<int> RowPairs(QuantizedMatrix w, int row, Int8Kernel kernel) => UsesSaturationMetadata(kernel)
		? w.SaturationPairs.AsSpan(w.SaturationOffsets[row], w.SaturationOffsets[row + 1] - w.SaturationOffsets[row])
		: [];

	// Difference between the int16-saturated and the exact sum for the pairs that can overflow.
	static int SaturationCorrection(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> w, ReadOnlySpan<int> pairs)
	{
		int correction = 0;
		foreach (int k in pairs)
		{
			int pair = (a[k] + 127) * w[k] + (a[k + 1] + 127) * w[k + 1];
			correction += Math.Clamp(pair, short.MinValue, short.MaxValue) - pair;
		}
		return correction;
	}

	/// <summary>
	/// intgemm's Int8Shift::PrepareBias: bias + rowSum * (-(127 / alpha) * (127 / quantMult) / 127),
	/// computed in the same float order.
	/// </summary>
	public static float[] PrepareBias(QuantizedMatrix w, float alpha, float[]? bias)
	{
		if (!w.HasRowSums)
			throw new NotSupportedException("The matrix was loaded only to be looked up, without row sums.");
		float unquant = -1f * ((127f / alpha) * (127f / w.QuantMult)) / 127f;
		var prepared = new float[w.Rows];
		for (int j = 0; j < w.Rows; j++)
		{
			float value = w.RowSums[j] * unquant;
			prepared[j] = bias == null ? value : value + bias[j];
		}
		return prepared;
	}

	// Up to four activation rows starting at firstRow against one weight row, so each weight
	// vector is loaded once for several rows.
	// For the x86 kernel, the sums are intgemm's complete Int8Shift products; otherwise they are
	// exact signed products, without the shift and the saturation.
	static void Dot(QuantizedActivations a, int firstRow, int count, ReadOnlySpan<sbyte> w, Span<int> sums, Int8Kernel kernel)
	{
		int width = w.Length;
		if (Avx2.IsSupported && kernel == Int8Kernel.X86)
		{
			DotX86RowMajor(a.Shifted.AsSpan(firstRow * width, count * width), count, w, sums);
			return;
		}
		if (Dp.IsSupported && kernel == Int8Kernel.ArmDotProduct)
		{
			var values = a.Values.AsSpan(firstRow * width, count * width);
			if (count == 4)
			{
				DotArm4(values, w, sums);
				return;
			}
			for (int k = 0; k < count; k++)
				sums[k] = DotArm(values.Slice(k * width, width), w);
			return;
		}
		var widened = a.Widened.AsSpan(firstRow * width, count * width);
		if (count == 4)
		{
			DotPortable4(widened, w, sums);
			return;
		}
		for (int k = 0; k < count; k++)
			sums[k] = DotPortable(widened.Slice(k * width, width), w);
	}

	static int DotPortable(ReadOnlySpan<short> a, ReadOnlySpan<sbyte> w)
	{
		ref short ra = ref MemoryMarshal.GetReference(a);
		ref sbyte rw = ref MemoryMarshal.GetReference(w);
		int i = 0;
		int sum = 0;
		if (Vector256.IsHardwareAccelerated)
		{
			var acc = Vector256<int>.Zero;
			for (; i + 32 <= w.Length; i += 32)
			{
				var (wLo, wHi) = Vector256.Widen(Vector256.LoadUnsafe(ref rw, (nuint)i));
				acc += PairSum(Vector256.LoadUnsafe(ref ra, (nuint)i), Vector256.LoadUnsafe(ref ra, (nuint)i + 16), wLo, wHi);
			}
			sum = Vector256.Sum(acc);
		}
		var acc128 = Vector128<int>.Zero;
		for (; i + 16 <= w.Length; i += 16)
		{
			var (wLo, wHi) = Vector128.Widen(Vector128.LoadUnsafe(ref rw, (nuint)i));
			acc128 += PairSum(Vector128.LoadUnsafe(ref ra, (nuint)i), Vector128.LoadUnsafe(ref ra, (nuint)i + 8), wLo, wHi);
		}
		return sum + Vector128.Sum(acc128) + DotScalar(a, w, i);
	}

	static void DotPortable4(ReadOnlySpan<short> a, ReadOnlySpan<sbyte> w, Span<int> sums)
	{
		int width = w.Length;
		ref short ra = ref MemoryMarshal.GetReference(a);
		ref sbyte rw = ref MemoryMarshal.GetReference(w);
		int i = 0;
		sums.Clear();
		if (Vector256.IsHardwareAccelerated)
		{
			Vector256<int> acc0 = default, acc1 = default, acc2 = default, acc3 = default;
			for (; i + 32 <= width; i += 32)
			{
				var (wLo, wHi) = Vector256.Widen(Vector256.LoadUnsafe(ref rw, (nuint)i));
				acc0 += PairSum(Vector256.LoadUnsafe(ref ra, (nuint)i), Vector256.LoadUnsafe(ref ra, (nuint)i + 16), wLo, wHi);
				acc1 += PairSum(Vector256.LoadUnsafe(ref ra, (nuint)(width + i)), Vector256.LoadUnsafe(ref ra, (nuint)(width + i + 16)), wLo, wHi);
				acc2 += PairSum(Vector256.LoadUnsafe(ref ra, (nuint)(2 * width + i)), Vector256.LoadUnsafe(ref ra, (nuint)(2 * width + i + 16)), wLo, wHi);
				acc3 += PairSum(Vector256.LoadUnsafe(ref ra, (nuint)(3 * width + i)), Vector256.LoadUnsafe(ref ra, (nuint)(3 * width + i + 16)), wLo, wHi);
			}
			sums[0] = Vector256.Sum(acc0);
			sums[1] = Vector256.Sum(acc1);
			sums[2] = Vector256.Sum(acc2);
			sums[3] = Vector256.Sum(acc3);
		}
		Vector128<int> b0 = default, b1 = default, b2 = default, b3 = default;
		for (; i + 16 <= width; i += 16)
		{
			var (wLo, wHi) = Vector128.Widen(Vector128.LoadUnsafe(ref rw, (nuint)i));
			b0 += PairSum(Vector128.LoadUnsafe(ref ra, (nuint)i), Vector128.LoadUnsafe(ref ra, (nuint)i + 8), wLo, wHi);
			b1 += PairSum(Vector128.LoadUnsafe(ref ra, (nuint)(width + i)), Vector128.LoadUnsafe(ref ra, (nuint)(width + i + 8)), wLo, wHi);
			b2 += PairSum(Vector128.LoadUnsafe(ref ra, (nuint)(2 * width + i)), Vector128.LoadUnsafe(ref ra, (nuint)(2 * width + i + 8)), wLo, wHi);
			b3 += PairSum(Vector128.LoadUnsafe(ref ra, (nuint)(3 * width + i)), Vector128.LoadUnsafe(ref ra, (nuint)(3 * width + i + 8)), wLo, wHi);
		}
		sums[0] += Vector128.Sum(b0) + DotScalar(a[..width], w, i);
		sums[1] += Vector128.Sum(b1) + DotScalar(a.Slice(width, width), w, i);
		sums[2] += Vector128.Sum(b2) + DotScalar(a.Slice(2 * width, width), w, i);
		sums[3] += Vector128.Sum(b3) + DotScalar(a.Slice(3 * width, width), w, i);
	}

	// aLo * wLo + aHi * wHi stays within int16 for int8 inputs; widen the result to int32.
	static Vector256<int> PairSum(Vector256<short> aLo, Vector256<short> aHi, Vector256<short> wLo, Vector256<short> wHi)
	{
		var (lo, hi) = Vector256.Widen(aLo * wLo + aHi * wHi);
		return lo + hi;
	}

	static Vector128<int> PairSum(Vector128<short> aLo, Vector128<short> aHi, Vector128<short> wLo, Vector128<short> wHi)
	{
		var (lo, hi) = Vector128.Widen(aLo * wLo + aHi * wHi);
		return lo + hi;
	}

	// ARM-specific acceleration: SDOT accumulates four int8 products per int32 lane.
	static int DotArm(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> w)
	{
		ref sbyte ra = ref MemoryMarshal.GetReference(a);
		ref sbyte rw = ref MemoryMarshal.GetReference(w);
		var acc0 = Vector128<int>.Zero;
		var acc1 = Vector128<int>.Zero;
		int i = 0;
		for (; i + 32 <= w.Length; i += 32)
		{
			acc0 = Dp.DotProduct(acc0, Vector128.LoadUnsafe(ref ra, (nuint)i), Vector128.LoadUnsafe(ref rw, (nuint)i));
			acc1 = Dp.DotProduct(acc1, Vector128.LoadUnsafe(ref ra, (nuint)i + 16), Vector128.LoadUnsafe(ref rw, (nuint)i + 16));
		}
		for (; i + 16 <= w.Length; i += 16)
			acc0 = Dp.DotProduct(acc0, Vector128.LoadUnsafe(ref ra, (nuint)i), Vector128.LoadUnsafe(ref rw, (nuint)i));
		return Vector128.Sum(acc0 + acc1) + DotScalar(a, w, i);
	}

	static void DotArm4(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> w, Span<int> sums)
	{
		int width = w.Length;
		ref sbyte ra = ref MemoryMarshal.GetReference(a);
		ref sbyte rw = ref MemoryMarshal.GetReference(w);
		Vector128<int> acc0 = default, acc1 = default, acc2 = default, acc3 = default;
		int i = 0;
		for (; i + 16 <= width; i += 16)
		{
			var wv = Vector128.LoadUnsafe(ref rw, (nuint)i);
			acc0 = Dp.DotProduct(acc0, Vector128.LoadUnsafe(ref ra, (nuint)i), wv);
			acc1 = Dp.DotProduct(acc1, Vector128.LoadUnsafe(ref ra, (nuint)(width + i)), wv);
			acc2 = Dp.DotProduct(acc2, Vector128.LoadUnsafe(ref ra, (nuint)(2 * width + i)), wv);
			acc3 = Dp.DotProduct(acc3, Vector128.LoadUnsafe(ref ra, (nuint)(3 * width + i)), wv);
		}
		sums[0] = Vector128.Sum(acc0) + DotScalar(a[..width], w, i);
		sums[1] = Vector128.Sum(acc1) + DotScalar(a.Slice(width, width), w, i);
		sums[2] = Vector128.Sum(acc2) + DotScalar(a.Slice(2 * width, width), w, i);
		sums[3] = Vector128.Sum(acc3) + DotScalar(a.Slice(3 * width, width), w, i);
	}

	// Four activation rows against two weight rows; sums[0..4] and sums[4..8] are the two columns.
	static void DotArm4x2(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> w0, ReadOnlySpan<sbyte> w1, Span<int> sums)
	{
		int width = w0.Length;
		ref sbyte ra = ref MemoryMarshal.GetReference(a);
		ref sbyte rw0 = ref MemoryMarshal.GetReference(w0);
		ref sbyte rw1 = ref MemoryMarshal.GetReference(w1);
		Vector128<int> acc0 = default, acc1 = default, acc2 = default, acc3 = default;
		Vector128<int> acc4 = default, acc5 = default, acc6 = default, acc7 = default;
		int i = 0;
		for (; i + 16 <= width; i += 16)
		{
			var x0 = Vector128.LoadUnsafe(ref ra, (nuint)i);
			var x1 = Vector128.LoadUnsafe(ref ra, (nuint)(width + i));
			var x2 = Vector128.LoadUnsafe(ref ra, (nuint)(2 * width + i));
			var x3 = Vector128.LoadUnsafe(ref ra, (nuint)(3 * width + i));
			var v0 = Vector128.LoadUnsafe(ref rw0, (nuint)i);
			var v1 = Vector128.LoadUnsafe(ref rw1, (nuint)i);
			acc0 = Dp.DotProduct(acc0, x0, v0);
			acc1 = Dp.DotProduct(acc1, x1, v0);
			acc2 = Dp.DotProduct(acc2, x2, v0);
			acc3 = Dp.DotProduct(acc3, x3, v0);
			acc4 = Dp.DotProduct(acc4, x0, v1);
			acc5 = Dp.DotProduct(acc5, x1, v1);
			acc6 = Dp.DotProduct(acc6, x2, v1);
			acc7 = Dp.DotProduct(acc7, x3, v1);
		}
		sums[0] = Vector128.Sum(acc0) + DotScalar(a[..width], w0, i);
		sums[1] = Vector128.Sum(acc1) + DotScalar(a.Slice(width, width), w0, i);
		sums[2] = Vector128.Sum(acc2) + DotScalar(a.Slice(2 * width, width), w0, i);
		sums[3] = Vector128.Sum(acc3) + DotScalar(a.Slice(3 * width, width), w0, i);
		sums[4] = Vector128.Sum(acc4) + DotScalar(a[..width], w1, i);
		sums[5] = Vector128.Sum(acc5) + DotScalar(a.Slice(width, width), w1, i);
		sums[6] = Vector128.Sum(acc6) + DotScalar(a.Slice(2 * width, width), w1, i);
		sums[7] = Vector128.Sum(acc7) + DotScalar(a.Slice(3 * width, width), w1, i);
	}

	static int DotScalar(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> w, int start)
	{
		int sum = 0;
		for (int i = start; i < w.Length; i++)
			sum += a[i] * w[i];
		return sum;
	}

	static int DotScalar(ReadOnlySpan<short> a, ReadOnlySpan<sbyte> w, int start)
	{
		int sum = 0;
		for (int i = start; i < w.Length; i++)
			sum += a[i] * w[i];
		return sum;
	}
}
