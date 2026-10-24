using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Tarjamot.Engine;

/// <summary>Float32 parameter (biases, layer normalization).</summary>
internal sealed record Tensor(float[] Data);

/// <summary>
/// Int8 matrix stored row-major as [out][in], or packed in panels of PanelWidth outputs in which
/// each output's four consecutive inputs are adjacent (four-input tiles).
/// The real value is Data / QuantMult; Alpha is the precomputed activation
/// quantization multiplier.
/// </summary>
internal sealed record QuantizedMatrix(sbyte[] Data, int Rows, int Cols, float QuantMult, float Alpha)
{
	/// <summary>Sum of each row's int8 values (intgemm's shift correction); empty for embeddings.</summary>
	public int[] RowSums { get; private init; } = ComputeRowSums(Data, Rows, Cols);

	/// <summary>
	/// Weight pairs (2k, 2k + 1) whose int16 pair sum can overflow in intgemm's Int8Shift multiply,
	/// stored per row as offsets into <see cref="SaturationPairs"/> (CSR layout). Empty for embeddings
	/// and when the matrix was loaded for the x86 kernel, whose PMADDUBSW saturates the pairs itself.
	/// </summary>
	public int[] SaturationOffsets { get; private init; } = [];

	public int[] SaturationPairs { get; private init; } = [];

	public bool HasSaturationMetadata => SaturationOffsets.Length == Rows + 1;

	/// <summary>Whether the matrix has row sums: matrices loaded only to be looked up (embeddings) do not.</summary>
	public bool HasRowSums => RowSums.Length == Rows;

	/// <summary>
	/// Outputs per packed panel: 4 for the ARM dot-product kernel, 16 for the x86 kernel, 0 for
	/// row-major data.
	/// </summary>
	public int PanelWidth { get; private init; }

	public bool IsPacked => PanelWidth != 0;

	public ReadOnlySpan<sbyte> Row(int row) => !IsPacked ? Data.AsSpan(row * Cols, Cols) : throw new InvalidOperationException("Packed matrices do not have contiguous rows.");

	/// <summary>Returns a packed replacement, sharing row sums and saturation metadata.</summary>
	public QuantizedMatrix Pack(int panelWidth)
	{
		if (IsPacked || !CanPack(Cols, panelWidth))
			return this;
		var packed = new sbyte[PackedLength(Rows, Cols, panelWidth)];
		for (int row = 0; row < Rows; row++)
			PackRow(Data.AsSpan(row * Cols, Cols), row, packed, panelWidth);
		return this with { Data = packed, PanelWidth = panelWidth };
	}

	/// <summary>The offset of weight (row, k) in packed data.</summary>
	public int PackedOffset(int row, int k) => PackedOffset(row, k, Cols, PanelWidth);

	static int PackedOffset(int row, int k, int cols, int panelWidth) =>
		row / panelWidth * panelWidth * cols + (k & ~3) * panelWidth + row % panelWidth * 4 + (k & 3);

	// The ARM kernel reads 16 inputs per step; the x86 kernel broadcasts four at a time.
	static bool CanPack(int cols, int panelWidth) => panelWidth != 0 && cols % (panelWidth == 4 ? 16 : 4) == 0;

	static int PackedLength(int rows, int cols, int panelWidth) => (rows + panelWidth - 1) / panelWidth * panelWidth * cols;

	// Each group of four inputs is 4 * panelWidth bytes after the previous one (CanPack ensures
	// that the row length is a multiple of 4).
	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	static void PackRow(ReadOnlySpan<sbyte> row, int rowIndex, sbyte[] packed, int panelWidth)
	{
		int first = PackedOffset(rowIndex, 0, row.Length, panelWidth);
		int step = 4 * panelWidth;
		if (row.Length == 0)
			return;
		if ((long)first + (long)(row.Length / 4 - 1) * step + 4 > packed.Length)
			throw new ArgumentOutOfRangeException(nameof(rowIndex));
		ref sbyte destination = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(packed), first);
		ref sbyte source = ref MemoryMarshal.GetReference(row);
		for (int k = 0; k < row.Length; k += 4)
		{
			Unsafe.WriteUnaligned(ref Unsafe.As<sbyte, byte>(ref destination), Unsafe.ReadUnaligned<int>(ref Unsafe.As<sbyte, byte>(ref Unsafe.Add(ref source, k))));
			destination = ref Unsafe.Add(ref destination, step);
		}
	}

	public static QuantizedMatrix Create(sbyte[] data, int rows, int cols, float quantMult, float alpha)
	{
		// With shifted activations in [0, 254], a pair (u0 * w0 + u1 * w1) can exceed the int16
		// range only when both weights have the same sign and 254 * |w0 + w1| > 32767.
		var offsets = new int[rows + 1];
		var pairs = new List<int>();
		for (int r = 0; r < rows; r++)
		{
			FindSaturationPairs(data.AsSpan(r * cols, cols), pairs);
			offsets[r + 1] = pairs.Count;
		}
		return new QuantizedMatrix(data, rows, cols, quantMult, alpha) { SaturationOffsets = offsets, SaturationPairs = pairs.ToArray() };
	}

	/// <summary>Builds the retained layout directly, with at most one row of scratch.</summary>
	internal delegate void ReadRow(Span<sbyte> row, int rowIndex);

	/// <param name="saturation">Whether to find the saturating pairs, which only the ARM and portable kernels use.</param>
	/// <param name="rowSums">Whether to compute the row sums, which products need (embeddings are only looked up).</param>
	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	internal static QuantizedMatrix CreateRows(int rows, int cols, float quantMult, float alpha, int panelWidth, ReadRow readRow, bool saturation = true, bool rowSums = true)
	{
		if (!CanPack(cols, panelWidth))
			panelWidth = 0;
		bool packed = panelWidth != 0;
		var data = new sbyte[packed ? PackedLength(rows, cols, panelWidth) : rows * cols];
		var rowBuffer = packed ? new sbyte[cols] : [];
		var sums = rowSums ? new int[rows] : [];
		var offsets = saturation ? new int[rows + 1] : [];
		var pairs = new List<int>();
		for (int r = 0; r < rows; r++)
		{
			var row = packed ? rowBuffer.AsSpan() : data.AsSpan(r * cols, cols);
			readRow(row, r);
			if (rowSums)
				sums[r] = RowSum(row);
			if (saturation)
			{
				FindSaturationPairs(row, pairs);
				offsets[r + 1] = pairs.Count;
			}
			if (packed)
				PackRow(row, r, data, panelWidth);
		}
		// Construct against an empty matrix so its row-major initializer never scans packed data.
		return new QuantizedMatrix([], 0, 0, quantMult, alpha) with
		{
			Data = data, Rows = rows, Cols = cols, PanelWidth = panelWidth,
			RowSums = sums, SaturationOffsets = offsets, SaturationPairs = pairs.ToArray()
		};
	}

	// A pair can saturate only when both weights have the same sign and 254 * |w0 + w1| > 32767,
	// that is |w0 + w1| >= 130; weights of different signs sum to at most 128 in magnitude, so the
	// sum alone decides. Read as one little-endian short v, a pair is w0 = (v << 8) >> 8 and
	// w1 = v >> 8, which lets a vector test 8 or 16 pairs at once; few rows have any such pair.
	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	static void FindSaturationPairs(ReadOnlySpan<sbyte> row, List<int> pairs)
	{
		var shorts = MemoryMarshal.Cast<sbyte, short>(row);
		int p = 0;
		if (!BitConverter.IsLittleEndian)
			shorts = [];
		if (Vector256.IsHardwareAccelerated)
		{
			var limit = Vector256.Create((short)129);
			for (; p + 16 <= shorts.Length; p += 16)
			{
				var v = Vector256.Create(shorts.Slice(p, 16));
				if (Vector256.GreaterThanAny(Vector256.Abs(PairSums(v)), limit))
					FindSaturationPairsScalar(row, 2 * p, 2 * (p + 16), pairs);
			}
		}
		if (Vector128.IsHardwareAccelerated)
		{
			var limit = Vector128.Create((short)129);
			for (; p + 8 <= shorts.Length; p += 8)
			{
				var v = Vector128.Create(shorts.Slice(p, 8));
				if (Vector128.GreaterThanAny(Vector128.Abs(PairSums(v)), limit))
					FindSaturationPairsScalar(row, 2 * p, 2 * (p + 8), pairs);
			}
		}
		FindSaturationPairsScalar(row, 2 * p, row.Length, pairs);
	}

	static Vector256<short> PairSums(Vector256<short> v) => Vector256.ShiftRightArithmetic(Vector256.ShiftLeft(v, 8), 8) + Vector256.ShiftRightArithmetic(v, 8);

	static Vector128<short> PairSums(Vector128<short> v) => Vector128.ShiftRightArithmetic(Vector128.ShiftLeft(v, 8), 8) + Vector128.ShiftRightArithmetic(v, 8);

	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	static void FindSaturationPairsScalar(ReadOnlySpan<sbyte> row, int start, int end, List<int> pairs)
	{
		for (int k = start; k + 1 < end; k += 2)
		{
			int w0 = row[k], w1 = row[k + 1];
			if ((w0 >= 0) == (w1 >= 0) && 254 * Math.Abs(w0 + w1) > short.MaxValue)
				pairs.Add(k);
		}
	}

	static int[] ComputeRowSums(sbyte[] data, int rows, int cols)
	{
		var sums = new int[rows];
		for (int r = 0; r < rows; r++)
			sums[r] = RowSum(data.AsSpan(r * cols, cols));
		return sums;
	}

	// Loading runs once per model, mostly before tiered compilation would optimize these loops (the
	// first output-layer conversion took 80 ms instead of 18-23 ms in unoptimized code), so the
	// per-row loops of loading are compiled optimized from the start.
	[MethodImpl(MethodImplOptions.AggressiveOptimization)]
	static int RowSum(ReadOnlySpan<sbyte> row)
	{
		int i = 0;
		int sum = 0;
		if (Vector256.IsHardwareAccelerated)
		{
			var acc = Vector256<int>.Zero;
			for (; i + 32 <= row.Length; i += 32)
			{
				var (lo, hi) = Vector256.Widen(Vector256.Create(row.Slice(i, 32)));
				var (a, b) = Vector256.Widen(lo + hi);
				acc += a + b;
			}
			sum = Vector256.Sum(acc);
		}
		else if (Vector128.IsHardwareAccelerated)
		{
			var acc = Vector128<int>.Zero;
			for (; i + 16 <= row.Length; i += 16)
			{
				var (lo, hi) = Vector128.Widen(Vector128.Create(row.Slice(i, 16)));
				var (a, b) = Vector128.Widen(lo + hi);
				acc += a + b;
			}
			sum = Vector128.Sum(acc);
		}
		for (; i < row.Length; i++)
			sum += row[i];
		return sum;
	}
}
