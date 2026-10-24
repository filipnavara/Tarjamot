using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace Tarjamot.Engine;

internal static partial class Int8Gemm
{
	// Each int32 lane is an output column. Packing four bytes from each of four weight rows
	// lets SDOT's indexed form share one activation quadruplet across those columns, with no
	// horizontal reduction. Activation rows and output columns are handled in 4 x 4 tiles.
	static void MultiplyPacked(QuantizedActivations a, int rows, QuantizedMatrix w, int outputs, float unquant, float[] preparedBias, float[] y, bool parallel)
	{
		int width = w.Cols;
		int panels = (outputs + 3) / 4;
		long work = (long)rows * width * outputs;
		int chunks = ParallelChunks(work, panels, Int8Kernel.ArmDotProduct, parallel);
		if (chunks > 1)
		{
			Parallel.For(0, chunks, c => ComputeRange(panels * c / chunks * 4, Math.Min(outputs, panels * (c + 1) / chunks * 4)));
		}
		else
		{
			ComputeRange(0, outputs);
		}

		void ComputeRange(int start, int end)
		{
			int rowBlock = Math.Max(4, 16384 / width & ~3);
			var scale = Vector128.Create(unquant);
			for (int blockStart = 0; blockStart < rows; blockStart += rowBlock)
			{
				int blockEnd = Math.Min(rows, blockStart + rowBlock);
				int j = start;
				// Four panels at a time, so 16 accumulators hide the SDOT latency.
				for (; j + 16 <= end; j += 16)
					ComputeWide(blockStart, blockEnd, j, scale);
				for (; j < end; j += 4)
				{
					int count = Math.Min(4, end - j);
					var columns = Vector128.Create(j, count > 1 ? j + 1 : -1, count > 2 ? j + 2 : -1, count > 3 ? j + 3 : -1);
					var shift = Vector128.Create(RowSum(columns[0]), RowSum(columns[1]), RowSum(columns[2]), RowSum(columns[3])) * Vector128.Create(127);
					var bias = Vector128.Create(Bias(columns[0]), Bias(columns[1]), Bias(columns[2]), Bias(columns[3]));
					bool correct = HasPairs(columns[0]) || HasPairs(columns[1]) || HasPairs(columns[2]) || HasPairs(columns[3]);
					var tile = new PackedOutput(w, columns, shift, bias, scale, correct);
					var panel = w.Data.AsSpan(j * width, 4 * width);
					int r = blockStart;
					for (; r + 4 <= blockEnd; r += 4)
					{
						DotPacked4(a.Values.AsSpan(r * width, 4 * width), panel, out var s0, out var s1, out var s2, out var s3);
						tile.Store(s0, a.Values.AsSpan(r * width, width), y.AsSpan(r * outputs + j, count));
						tile.Store(s1, a.Values.AsSpan((r + 1) * width, width), y.AsSpan((r + 1) * outputs + j, count));
						tile.Store(s2, a.Values.AsSpan((r + 2) * width, width), y.AsSpan((r + 2) * outputs + j, count));
						tile.Store(s3, a.Values.AsSpan((r + 3) * width, width), y.AsSpan((r + 3) * outputs + j, count));
					}
					for (; r < blockEnd; r++)
						tile.Store(DotPacked1(a.Values.AsSpan(r * width, width), panel),
							a.Values.AsSpan(r * width, width), y.AsSpan(r * outputs + j, count));
				}
			}

			void ComputeWide(int blockStart, int blockEnd, int j, Vector128<float> scale)
			{
				var tile0 = DenseTile(j, scale);
				var tile1 = DenseTile(j + 4, scale);
				var tile2 = DenseTile(j + 8, scale);
				var tile3 = DenseTile(j + 12, scale);
				var panels = w.Data.AsSpan(j * width, 16 * width);
				int r = blockStart;
				for (; r + 4 <= blockEnd; r += 4)
				{
					DotPacked4x4(a.Values.AsSpan(r * width, 4 * width), panels, out var sums);
					for (int i = 0; i < 4; i++)
					{
						var input = a.Values.AsSpan((r + i) * width, width);
						var row = y.AsSpan((r + i) * outputs + j, 16);
						tile0.Store(sums[i * 4], input, row[..4]);
						tile1.Store(sums[i * 4 + 1], input, row.Slice(4, 4));
						tile2.Store(sums[i * 4 + 2], input, row.Slice(8, 4));
						tile3.Store(sums[i * 4 + 3], input, row.Slice(12, 4));
					}
				}
				for (; r < blockEnd; r++)
				{
					var input = a.Values.AsSpan(r * width, width);
					DotPacked1x4(input, panels, out var s0, out var s1, out var s2, out var s3);
					var row = y.AsSpan(r * outputs + j, 16);
					tile0.Store(s0, input, row[..4]);
					tile1.Store(s1, input, row.Slice(4, 4));
					tile2.Store(s2, input, row.Slice(8, 4));
					tile3.Store(s3, input, row.Slice(12, 4));
				}
			}

			PackedOutput DenseTile(int j, Vector128<float> scale) => Int8Gemm.DenseTile(w, preparedBias, j, scale);

			int RowSum(int row) => row >= 0 ? w.RowSums[row] : 0;

			float Bias(int row) => row >= 0 ? preparedBias[row] : 0f;

			bool HasPairs(int row) => row >= 0 && w.SaturationOffsets[row] != w.SaturationOffsets[row + 1];
		}
	}

	// The four outputs of panel j, with vector loads of the contiguous row sums and biases.
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	static PackedOutput DenseTile(QuantizedMatrix w, float[] preparedBias, int j, Vector128<float> scale) => new(w,
		Vector128.Create(j, j + 1, j + 2, j + 3),
		Vector128.Create(w.RowSums.AsSpan(j, 4)) * Vector128.Create(127),
		Vector128.Create(preparedBias.AsSpan(j, 4)),
		scale,
		w.SaturationOffsets[j] != w.SaturationOffsets[j + 4]);

	readonly record struct PackedOutput(QuantizedMatrix Weights, Vector128<int> Columns, Vector128<int> Shift, Vector128<float> Bias, Vector128<float> Scale, bool Correct)
	{
		public void Store(Vector128<int> sum, ReadOnlySpan<sbyte> input, Span<float> output)
		{
			sum += Shift;
			if (Correct)
				sum += Vector128.Create(PackedSaturationCorrection(input, Weights, Columns[0]), PackedSaturationCorrection(input, Weights, Columns[1]),
					PackedSaturationCorrection(input, Weights, Columns[2]), PackedSaturationCorrection(input, Weights, Columns[3]));
			var result = Vector128.ConvertToSingle(sum) * Scale + Bias;
			if (output.Length == 4)
				result.StoreUnsafe(ref MemoryMarshal.GetReference(output));
			else
			{
				for (int lane = 0; lane < output.Length; lane++)
					output[lane] = result[lane];
			}
		}
	}

	static int PackedSaturationCorrection(ReadOnlySpan<sbyte> a, QuantizedMatrix w, int row)
	{
		if (row < 0)
			return 0;
		int correction = 0;
		int rowBase = (row & ~3) * w.Cols + (row & 3) * 4;
		foreach (int k in w.SaturationPairs.AsSpan(w.SaturationOffsets[row], w.SaturationOffsets[row + 1] - w.SaturationOffsets[row]))
		{
			int offset = rowBase + (k & ~3) * 4 + (k & 3);
			int pair = (a[k] + 127) * w.Data[offset] + (a[k + 1] + 127) * w.Data[offset + 1];
			correction += Math.Clamp(pair, short.MinValue, short.MaxValue) - pair;
		}
		return correction;
	}

	static void DotPacked4(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> weights,
		out Vector128<int> s0, out Vector128<int> s1, out Vector128<int> s2, out Vector128<int> s3)
	{
		int width = a.Length / 4;
		ref sbyte ra = ref MemoryMarshal.GetReference(a);
		ref sbyte rw = ref MemoryMarshal.GetReference(weights);
		Vector128<int> acc0 = default, acc1 = default, acc2 = default, acc3 = default;
		for (int k = 0; k < width; k += 16)
		{
			var x0 = Vector128.LoadUnsafe(ref ra, (nuint)k);
			var x1 = Vector128.LoadUnsafe(ref ra, (nuint)(width + k));
			var x2 = Vector128.LoadUnsafe(ref ra, (nuint)(2 * width + k));
			var x3 = Vector128.LoadUnsafe(ref ra, (nuint)(3 * width + k));
			var w0 = Vector128.LoadUnsafe(ref rw, (nuint)(k * 4));
			acc0 = Dp.DotProductBySelectedQuadruplet(acc0, w0, x0, 0);
			acc1 = Dp.DotProductBySelectedQuadruplet(acc1, w0, x1, 0);
			acc2 = Dp.DotProductBySelectedQuadruplet(acc2, w0, x2, 0);
			acc3 = Dp.DotProductBySelectedQuadruplet(acc3, w0, x3, 0);
			var w1 = Vector128.LoadUnsafe(ref rw, (nuint)(k * 4 + 16));
			acc0 = Dp.DotProductBySelectedQuadruplet(acc0, w1, x0, 1);
			acc1 = Dp.DotProductBySelectedQuadruplet(acc1, w1, x1, 1);
			acc2 = Dp.DotProductBySelectedQuadruplet(acc2, w1, x2, 1);
			acc3 = Dp.DotProductBySelectedQuadruplet(acc3, w1, x3, 1);
			var w2 = Vector128.LoadUnsafe(ref rw, (nuint)(k * 4 + 32));
			acc0 = Dp.DotProductBySelectedQuadruplet(acc0, w2, x0, 2);
			acc1 = Dp.DotProductBySelectedQuadruplet(acc1, w2, x1, 2);
			acc2 = Dp.DotProductBySelectedQuadruplet(acc2, w2, x2, 2);
			acc3 = Dp.DotProductBySelectedQuadruplet(acc3, w2, x3, 2);
			var w3 = Vector128.LoadUnsafe(ref rw, (nuint)(k * 4 + 48));
			acc0 = Dp.DotProductBySelectedQuadruplet(acc0, w3, x0, 3);
			acc1 = Dp.DotProductBySelectedQuadruplet(acc1, w3, x1, 3);
			acc2 = Dp.DotProductBySelectedQuadruplet(acc2, w3, x2, 3);
			acc3 = Dp.DotProductBySelectedQuadruplet(acc3, w3, x3, 3);
		}
		s0 = acc0;
		s1 = acc1;
		s2 = acc2;
		s3 = acc3;
	}

	[InlineArray(16)]
	struct Sums16
	{
		Vector128<int> element;
	}

	// Four activation rows times four panels (16 output columns): 16 independent accumulators,
	// enough to keep every SDOT pipe busy, and each weight load feeds four rows.
	static void DotPacked4x4(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> panels, out Sums16 sums)
	{
		int width = a.Length / 4;
		int panel = 4 * width;
		ref sbyte ra = ref MemoryMarshal.GetReference(a);
		ref sbyte rw = ref MemoryMarshal.GetReference(panels);
		Vector128<int> c00 = default, c01 = default, c02 = default, c03 = default, c10 = default, c11 = default, c12 = default, c13 = default, c20 = default, c21 = default, c22 = default, c23 = default, c30 = default, c31 = default, c32 = default, c33 = default;
		for (int k = 0; k < width; k += 16)
		{
			var x0 = Vector128.LoadUnsafe(ref ra, (nuint)k);
			var x1 = Vector128.LoadUnsafe(ref ra, (nuint)(width + k));
			var x2 = Vector128.LoadUnsafe(ref ra, (nuint)(2 * width + k));
			var x3 = Vector128.LoadUnsafe(ref ra, (nuint)(3 * width + k));
			var w0 = Vector128.LoadUnsafe(ref rw, (nuint)(k * 4));
			var w1 = Vector128.LoadUnsafe(ref rw, (nuint)(panel + k * 4));
			var w2 = Vector128.LoadUnsafe(ref rw, (nuint)(2 * panel + k * 4));
			var w3 = Vector128.LoadUnsafe(ref rw, (nuint)(3 * panel + k * 4));
			c00 = Dp.DotProductBySelectedQuadruplet(c00, w0, x0, 0);
			c10 = Dp.DotProductBySelectedQuadruplet(c10, w0, x1, 0);
			c20 = Dp.DotProductBySelectedQuadruplet(c20, w0, x2, 0);
			c30 = Dp.DotProductBySelectedQuadruplet(c30, w0, x3, 0);
			c01 = Dp.DotProductBySelectedQuadruplet(c01, w1, x0, 0);
			c11 = Dp.DotProductBySelectedQuadruplet(c11, w1, x1, 0);
			c21 = Dp.DotProductBySelectedQuadruplet(c21, w1, x2, 0);
			c31 = Dp.DotProductBySelectedQuadruplet(c31, w1, x3, 0);
			c02 = Dp.DotProductBySelectedQuadruplet(c02, w2, x0, 0);
			c12 = Dp.DotProductBySelectedQuadruplet(c12, w2, x1, 0);
			c22 = Dp.DotProductBySelectedQuadruplet(c22, w2, x2, 0);
			c32 = Dp.DotProductBySelectedQuadruplet(c32, w2, x3, 0);
			c03 = Dp.DotProductBySelectedQuadruplet(c03, w3, x0, 0);
			c13 = Dp.DotProductBySelectedQuadruplet(c13, w3, x1, 0);
			c23 = Dp.DotProductBySelectedQuadruplet(c23, w3, x2, 0);
			c33 = Dp.DotProductBySelectedQuadruplet(c33, w3, x3, 0);
			w0 = Vector128.LoadUnsafe(ref rw, (nuint)(k * 4 + 16));
			w1 = Vector128.LoadUnsafe(ref rw, (nuint)(panel + k * 4 + 16));
			w2 = Vector128.LoadUnsafe(ref rw, (nuint)(2 * panel + k * 4 + 16));
			w3 = Vector128.LoadUnsafe(ref rw, (nuint)(3 * panel + k * 4 + 16));
			c00 = Dp.DotProductBySelectedQuadruplet(c00, w0, x0, 1);
			c10 = Dp.DotProductBySelectedQuadruplet(c10, w0, x1, 1);
			c20 = Dp.DotProductBySelectedQuadruplet(c20, w0, x2, 1);
			c30 = Dp.DotProductBySelectedQuadruplet(c30, w0, x3, 1);
			c01 = Dp.DotProductBySelectedQuadruplet(c01, w1, x0, 1);
			c11 = Dp.DotProductBySelectedQuadruplet(c11, w1, x1, 1);
			c21 = Dp.DotProductBySelectedQuadruplet(c21, w1, x2, 1);
			c31 = Dp.DotProductBySelectedQuadruplet(c31, w1, x3, 1);
			c02 = Dp.DotProductBySelectedQuadruplet(c02, w2, x0, 1);
			c12 = Dp.DotProductBySelectedQuadruplet(c12, w2, x1, 1);
			c22 = Dp.DotProductBySelectedQuadruplet(c22, w2, x2, 1);
			c32 = Dp.DotProductBySelectedQuadruplet(c32, w2, x3, 1);
			c03 = Dp.DotProductBySelectedQuadruplet(c03, w3, x0, 1);
			c13 = Dp.DotProductBySelectedQuadruplet(c13, w3, x1, 1);
			c23 = Dp.DotProductBySelectedQuadruplet(c23, w3, x2, 1);
			c33 = Dp.DotProductBySelectedQuadruplet(c33, w3, x3, 1);
			w0 = Vector128.LoadUnsafe(ref rw, (nuint)(k * 4 + 32));
			w1 = Vector128.LoadUnsafe(ref rw, (nuint)(panel + k * 4 + 32));
			w2 = Vector128.LoadUnsafe(ref rw, (nuint)(2 * panel + k * 4 + 32));
			w3 = Vector128.LoadUnsafe(ref rw, (nuint)(3 * panel + k * 4 + 32));
			c00 = Dp.DotProductBySelectedQuadruplet(c00, w0, x0, 2);
			c10 = Dp.DotProductBySelectedQuadruplet(c10, w0, x1, 2);
			c20 = Dp.DotProductBySelectedQuadruplet(c20, w0, x2, 2);
			c30 = Dp.DotProductBySelectedQuadruplet(c30, w0, x3, 2);
			c01 = Dp.DotProductBySelectedQuadruplet(c01, w1, x0, 2);
			c11 = Dp.DotProductBySelectedQuadruplet(c11, w1, x1, 2);
			c21 = Dp.DotProductBySelectedQuadruplet(c21, w1, x2, 2);
			c31 = Dp.DotProductBySelectedQuadruplet(c31, w1, x3, 2);
			c02 = Dp.DotProductBySelectedQuadruplet(c02, w2, x0, 2);
			c12 = Dp.DotProductBySelectedQuadruplet(c12, w2, x1, 2);
			c22 = Dp.DotProductBySelectedQuadruplet(c22, w2, x2, 2);
			c32 = Dp.DotProductBySelectedQuadruplet(c32, w2, x3, 2);
			c03 = Dp.DotProductBySelectedQuadruplet(c03, w3, x0, 2);
			c13 = Dp.DotProductBySelectedQuadruplet(c13, w3, x1, 2);
			c23 = Dp.DotProductBySelectedQuadruplet(c23, w3, x2, 2);
			c33 = Dp.DotProductBySelectedQuadruplet(c33, w3, x3, 2);
			w0 = Vector128.LoadUnsafe(ref rw, (nuint)(k * 4 + 48));
			w1 = Vector128.LoadUnsafe(ref rw, (nuint)(panel + k * 4 + 48));
			w2 = Vector128.LoadUnsafe(ref rw, (nuint)(2 * panel + k * 4 + 48));
			w3 = Vector128.LoadUnsafe(ref rw, (nuint)(3 * panel + k * 4 + 48));
			c00 = Dp.DotProductBySelectedQuadruplet(c00, w0, x0, 3);
			c10 = Dp.DotProductBySelectedQuadruplet(c10, w0, x1, 3);
			c20 = Dp.DotProductBySelectedQuadruplet(c20, w0, x2, 3);
			c30 = Dp.DotProductBySelectedQuadruplet(c30, w0, x3, 3);
			c01 = Dp.DotProductBySelectedQuadruplet(c01, w1, x0, 3);
			c11 = Dp.DotProductBySelectedQuadruplet(c11, w1, x1, 3);
			c21 = Dp.DotProductBySelectedQuadruplet(c21, w1, x2, 3);
			c31 = Dp.DotProductBySelectedQuadruplet(c31, w1, x3, 3);
			c02 = Dp.DotProductBySelectedQuadruplet(c02, w2, x0, 3);
			c12 = Dp.DotProductBySelectedQuadruplet(c12, w2, x1, 3);
			c22 = Dp.DotProductBySelectedQuadruplet(c22, w2, x2, 3);
			c32 = Dp.DotProductBySelectedQuadruplet(c32, w2, x3, 3);
			c03 = Dp.DotProductBySelectedQuadruplet(c03, w3, x0, 3);
			c13 = Dp.DotProductBySelectedQuadruplet(c13, w3, x1, 3);
			c23 = Dp.DotProductBySelectedQuadruplet(c23, w3, x2, 3);
			c33 = Dp.DotProductBySelectedQuadruplet(c33, w3, x3, 3);
		}
		sums = default;
		sums[0] = c00;
		sums[1] = c01;
		sums[2] = c02;
		sums[3] = c03;
		sums[4] = c10;
		sums[5] = c11;
		sums[6] = c12;
		sums[7] = c13;
		sums[8] = c20;
		sums[9] = c21;
		sums[10] = c22;
		sums[11] = c23;
		sums[12] = c30;
		sums[13] = c31;
		sums[14] = c32;
		sums[15] = c33;
	}

	// One activation row times four panels, again with 16 independent accumulators.
	static void DotPacked1x4(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> panels,
		out Vector128<int> s0, out Vector128<int> s1, out Vector128<int> s2, out Vector128<int> s3)
	{
		int width = a.Length;
		int panel = 4 * width;
		ref sbyte ra = ref MemoryMarshal.GetReference(a);
		ref sbyte rw = ref MemoryMarshal.GetReference(panels);
		Vector128<int> c00 = default, c01 = default, c02 = default, c03 = default, c10 = default, c11 = default, c12 = default, c13 = default, c20 = default, c21 = default, c22 = default, c23 = default, c30 = default, c31 = default, c32 = default, c33 = default;
		for (int k = 0; k < width; k += 16)
		{
			var x = Vector128.LoadUnsafe(ref ra, (nuint)k);
			c00 = Dp.DotProductBySelectedQuadruplet(c00, Vector128.LoadUnsafe(ref rw, (nuint)(k * 4)), x, 0);
			c01 = Dp.DotProductBySelectedQuadruplet(c01, Vector128.LoadUnsafe(ref rw, (nuint)(k * 4 + 16)), x, 1);
			c02 = Dp.DotProductBySelectedQuadruplet(c02, Vector128.LoadUnsafe(ref rw, (nuint)(k * 4 + 32)), x, 2);
			c03 = Dp.DotProductBySelectedQuadruplet(c03, Vector128.LoadUnsafe(ref rw, (nuint)(k * 4 + 48)), x, 3);
			c10 = Dp.DotProductBySelectedQuadruplet(c10, Vector128.LoadUnsafe(ref rw, (nuint)(panel + k * 4)), x, 0);
			c11 = Dp.DotProductBySelectedQuadruplet(c11, Vector128.LoadUnsafe(ref rw, (nuint)(panel + k * 4 + 16)), x, 1);
			c12 = Dp.DotProductBySelectedQuadruplet(c12, Vector128.LoadUnsafe(ref rw, (nuint)(panel + k * 4 + 32)), x, 2);
			c13 = Dp.DotProductBySelectedQuadruplet(c13, Vector128.LoadUnsafe(ref rw, (nuint)(panel + k * 4 + 48)), x, 3);
			c20 = Dp.DotProductBySelectedQuadruplet(c20, Vector128.LoadUnsafe(ref rw, (nuint)(2 * panel + k * 4)), x, 0);
			c21 = Dp.DotProductBySelectedQuadruplet(c21, Vector128.LoadUnsafe(ref rw, (nuint)(2 * panel + k * 4 + 16)), x, 1);
			c22 = Dp.DotProductBySelectedQuadruplet(c22, Vector128.LoadUnsafe(ref rw, (nuint)(2 * panel + k * 4 + 32)), x, 2);
			c23 = Dp.DotProductBySelectedQuadruplet(c23, Vector128.LoadUnsafe(ref rw, (nuint)(2 * panel + k * 4 + 48)), x, 3);
			c30 = Dp.DotProductBySelectedQuadruplet(c30, Vector128.LoadUnsafe(ref rw, (nuint)(3 * panel + k * 4)), x, 0);
			c31 = Dp.DotProductBySelectedQuadruplet(c31, Vector128.LoadUnsafe(ref rw, (nuint)(3 * panel + k * 4 + 16)), x, 1);
			c32 = Dp.DotProductBySelectedQuadruplet(c32, Vector128.LoadUnsafe(ref rw, (nuint)(3 * panel + k * 4 + 32)), x, 2);
			c33 = Dp.DotProductBySelectedQuadruplet(c33, Vector128.LoadUnsafe(ref rw, (nuint)(3 * panel + k * 4 + 48)), x, 3);
		}
		s0 = (c00 + c01) + (c02 + c03);
		s1 = (c10 + c11) + (c12 + c13);
		s2 = (c20 + c21) + (c22 + c23);
		s3 = (c30 + c31) + (c32 + c33);
	}

	static Vector128<int> DotPacked1(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> weights)
	{
		ref sbyte ra = ref MemoryMarshal.GetReference(a);
		ref sbyte rw = ref MemoryMarshal.GetReference(weights);
		Vector128<int> acc0 = default, acc1 = default, acc2 = default, acc3 = default;
		for (int k = 0; k < a.Length; k += 16)
		{
			var x = Vector128.LoadUnsafe(ref ra, (nuint)k);
			acc0 = Dp.DotProductBySelectedQuadruplet(acc0, Vector128.LoadUnsafe(ref rw, (nuint)(k * 4)), x, 0);
			acc1 = Dp.DotProductBySelectedQuadruplet(acc1, Vector128.LoadUnsafe(ref rw, (nuint)(k * 4 + 16)), x, 1);
			acc2 = Dp.DotProductBySelectedQuadruplet(acc2, Vector128.LoadUnsafe(ref rw, (nuint)(k * 4 + 32)), x, 2);
			acc3 = Dp.DotProductBySelectedQuadruplet(acc3, Vector128.LoadUnsafe(ref rw, (nuint)(k * 4 + 48)), x, 3);
		}
		return (acc0 + acc1) + (acc2 + acc3);
	}
}
