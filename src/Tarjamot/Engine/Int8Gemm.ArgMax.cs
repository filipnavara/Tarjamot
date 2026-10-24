using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Tarjamot.Engine;

/// <summary>Reusable winners for each output partition and activation row, independent of vocabulary size.</summary>
internal sealed class ArgMaxScratch
{
	public int[] Tokens = [];
	public float[] Scores = [];

	internal long RetainedBytes => 48 + ((Tokens.LongLength * sizeof(int) + 7) & ~7L) + ((Scores.LongLength * sizeof(float) + 7) & ~7L);

	public void Initialize(int rows, int chunks)
	{
		int count = checked(rows * chunks);
		if (Tokens.Length < count) Tokens = new int[count];
		if (Scores.Length < count) Scores = new float[count];
		Tokens.AsSpan(0, count).Fill(-1);
		Scores.AsSpan(0, count).Fill(float.NegativeInfinity);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Consider(int index, int token, float score, int excluded)
	{
		if (token != excluded && score > Scores[index])
		{
			Scores[index] = score;
			Tokens[index] = token;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Consider(int index, int firstToken, Vector128<float> scores, int count, int excluded)
	{
		if (!Vector128.GreaterThanAny(scores, Vector128.Create(Scores[index])))
			return;
		for (int lane = 0; lane < count; lane++)
			Consider(index, firstToken + lane, scores[lane], excluded);
	}
}

internal static partial class Int8Gemm
{
	/// <summary>
	/// Full-vocabulary projection fused with strict-greater winning-token selection. UNK is
	/// excluded, NaNs cannot win, and ties retain the earliest vocabulary index.
	/// </summary>
	public static ReadOnlySpan<int> MultiplyArgMax(QuantizedActivations a, int rows, QuantizedMatrix w,
		float unquant, float[] preparedBias, int excluded, ArgMaxScratch scratch, Int8Kernel kernel, bool parallel)
	{
		if (w.IsPacked && w.PanelWidth != PanelWidth(kernel))
			throw new NotSupportedException("The matrix is packed for a different kernel.");
		RequireProductMetadata(w, kernel);
		int panelWidth = Math.Max(1, w.PanelWidth);
		int partitions = (w.Rows + panelWidth - 1) / panelWidth;
		long work = (long)rows * w.Cols * w.Rows;
		int chunks = ParallelChunks(work, partitions, kernel, parallel);
		scratch.Initialize(rows, chunks);
		if (chunks == 1)
			ArgMaxRange(a, rows, w, unquant, preparedBias, excluded, scratch, kernel, 0, 0, w.Rows);
		else
			ArgMaxParallel(a, rows, w, unquant, preparedBias, excluded, scratch, kernel, chunks, partitions);
		// Increasing partition order is increasing vocabulary order, preserving first-wins ties.
		for (int c = 1; c < chunks; c++)
		{
			for (int r = 0; r < rows; r++)
				scratch.Consider(r, scratch.Tokens[c * rows + r], scratch.Scores[c * rows + r], excluded);
		}
		return scratch.Tokens.AsSpan(0, rows);
	}

	static void ArgMaxParallel(QuantizedActivations a, int rows, QuantizedMatrix w, float unquant,
		float[] preparedBias, int excluded, ArgMaxScratch scratch, Int8Kernel kernel, int chunks, int partitions)
	{
		Parallel.For(0, chunks, c =>
		{
			int start = partitions * c / chunks;
			int end = partitions * (c + 1) / chunks;
			int panelWidth = Math.Max(1, w.PanelWidth);
			ArgMaxRange(a, rows, w, unquant, preparedBias, excluded, scratch, kernel, c * rows,
				start * panelWidth, Math.Min(w.Rows, end * panelWidth));
		});
	}

	static void ArgMaxRange(QuantizedActivations a, int rows, QuantizedMatrix w, float unquant,
		float[] preparedBias, int excluded, ArgMaxScratch scratch, Int8Kernel kernel, int chunkBase, int start, int end)
	{
		if (Avx2.IsSupported && kernel == Int8Kernel.X86 && w.IsPacked)
			ArgMaxX86Range(a, rows, w, unquant, preparedBias, excluded, scratch, chunkBase, start, end);
		else if (w.IsPacked)
		{
			if (!Dp.IsSupported)
				throw new PlatformNotSupportedException();
			ArgMaxPackedRange(a, rows, w, unquant, preparedBias, excluded, scratch, chunkBase, start, end);
		}
		else
			ArgMaxRowMajorRange(a, rows, w, unquant, preparedBias, excluded, scratch, kernel, chunkBase, start, end);
	}

	static void ArgMaxPackedRange(QuantizedActivations a, int rows, QuantizedMatrix w, float unquant,
		float[] preparedBias, int excluded, ArgMaxScratch scratch, int chunkBase, int start, int end)
	{
		int width = w.Cols;
		int rowBlock = Math.Max(4, 16384 / width & ~3);
		var scale = Vector128.Create(unquant);
		for (int blockStart = 0; blockStart < rows; blockStart += rowBlock)
		{
			int blockEnd = Math.Min(rows, blockStart + rowBlock);
			int j = start;
			// Four panels at a time, so 16 accumulators hide the SDOT latency.
			for (; j + 16 <= end; j += 16)
				ArgMaxWide(a, w, scale, preparedBias, excluded, scratch, chunkBase, blockStart, blockEnd, j);
			for (; j < end; j += 4)
			{
				int count = Math.Min(4, end - j);
				var columns = Vector128.Create(j, count > 1 ? j + 1 : -1, count > 2 ? j + 2 : -1, count > 3 ? j + 3 : -1);
				var shift = Vector128.Create(w.RowSums[j], count > 1 ? w.RowSums[j + 1] : 0,
					count > 2 ? w.RowSums[j + 2] : 0, count > 3 ? w.RowSums[j + 3] : 0) * Vector128.Create(127);
				var bias = Vector128.Create(preparedBias[j], count > 1 ? preparedBias[j + 1] : 0f,
					count > 2 ? preparedBias[j + 2] : 0f, count > 3 ? preparedBias[j + 3] : 0f);
				var tile = new PackedWinner(w, columns, shift, bias, scale, w.SaturationOffsets[j] != w.SaturationOffsets[j + count]);
				var panel = w.Data.AsSpan(j * width, 4 * width);
				int r = blockStart;
				for (; r + 4 <= blockEnd; r += 4)
				{
					DotPacked4(a.Values.AsSpan(r * width, 4 * width), panel, out var s0, out var s1, out var s2, out var s3);
					scratch.Consider(chunkBase + r, j, tile.Score(s0, a.Values.AsSpan(r * width, width)), count, excluded);
					scratch.Consider(chunkBase + r + 1, j, tile.Score(s1, a.Values.AsSpan((r + 1) * width, width)), count, excluded);
					scratch.Consider(chunkBase + r + 2, j, tile.Score(s2, a.Values.AsSpan((r + 2) * width, width)), count, excluded);
					scratch.Consider(chunkBase + r + 3, j, tile.Score(s3, a.Values.AsSpan((r + 3) * width, width)), count, excluded);
				}
				for (; r < blockEnd; r++)
				{
					var input = a.Values.AsSpan(r * width, width);
					scratch.Consider(chunkBase + r, j, tile.Score(DotPacked1(input, panel), input), count, excluded);
				}
			}
		}
	}

	// Rows consider the four panels in increasing vocabulary order, preserving first-wins ties.
	static void ArgMaxWide(QuantizedActivations a, QuantizedMatrix w, Vector128<float> scale, float[] preparedBias, int excluded,
		ArgMaxScratch scratch, int chunkBase, int blockStart, int blockEnd, int j)
	{
		int width = w.Cols;
		var tile0 = Winner(j);
		var tile1 = Winner(j + 4);
		var tile2 = Winner(j + 8);
		var tile3 = Winner(j + 12);
		var panels = w.Data.AsSpan(j * width, 16 * width);
		int r = blockStart;
		for (; r + 4 <= blockEnd; r += 4)
		{
			DotPacked4x4(a.Values.AsSpan(r * width, 4 * width), panels, out var sums);
			for (int i = 0; i < 4; i++)
			{
				var input = a.Values.AsSpan((r + i) * width, width);
				int index = chunkBase + r + i;
				scratch.Consider(index, j, tile0.Score(sums[i * 4], input), 4, excluded);
				scratch.Consider(index, j + 4, tile1.Score(sums[i * 4 + 1], input), 4, excluded);
				scratch.Consider(index, j + 8, tile2.Score(sums[i * 4 + 2], input), 4, excluded);
				scratch.Consider(index, j + 12, tile3.Score(sums[i * 4 + 3], input), 4, excluded);
			}
		}
		for (; r < blockEnd; r++)
		{
			var input = a.Values.AsSpan(r * width, width);
			DotPacked1x4(input, panels, out var s0, out var s1, out var s2, out var s3);
			int index = chunkBase + r;
			scratch.Consider(index, j, tile0.Score(s0, input), 4, excluded);
			scratch.Consider(index, j + 4, tile1.Score(s1, input), 4, excluded);
			scratch.Consider(index, j + 8, tile2.Score(s2, input), 4, excluded);
			scratch.Consider(index, j + 12, tile3.Score(s3, input), 4, excluded);
		}

		PackedWinner Winner(int column)
		{
			var tile = DenseTile(w, preparedBias, column, scale);
			return new PackedWinner(w, tile.Columns, tile.Shift, tile.Bias, scale, tile.Correct);
		}
	}

	readonly record struct PackedWinner(QuantizedMatrix Weights, Vector128<int> Columns, Vector128<int> Shift,
		Vector128<float> Bias, Vector128<float> Scale, bool Correct)
	{
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public Vector128<float> Score(Vector128<int> sum, ReadOnlySpan<sbyte> input)
		{
			sum += Shift;
			if (Correct)
				sum += Vector128.Create(PackedSaturationCorrection(input, Weights, Columns[0]), PackedSaturationCorrection(input, Weights, Columns[1]),
					PackedSaturationCorrection(input, Weights, Columns[2]), PackedSaturationCorrection(input, Weights, Columns[3]));
			return Vector128.ConvertToSingle(sum) * Scale + Bias;
		}
	}

	// Rows are processed in blocks small enough to stay in L1 cache while the weight rows
	// stream through; each weight vector load is shared by up to four activation rows.
	static void ArgMaxRowMajorRange(QuantizedActivations a, int rows, QuantizedMatrix w, float unquant, float[] preparedBias,
		int excluded, ArgMaxScratch scratch, Int8Kernel kernel, int chunkBase, int start, int end)
	{
		int width = w.Cols;
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
					int row0 = j, row1 = j + 1;
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
							scratch.Consider(chunkBase + r + k, j, sum0 * unquant + preparedBias[row0], excluded);
							scratch.Consider(chunkBase + r + k, j + 1, sum1 * unquant + preparedBias[row1], excluded);
						}
					}
				}
			}
			for (; j < end; j++)
			{
				int row = j;
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
						scratch.Consider(chunkBase + r + k, j, sum * unquant + b, excluded);
					}
				}
			}
		}
	}
}
