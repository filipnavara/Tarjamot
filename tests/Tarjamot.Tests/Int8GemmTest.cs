using Tarjamot.Engine;
using NUnit.Framework;

namespace Tarjamot.Tests;

/// <summary>
/// The int8 kernels against a scalar implementation of intgemm's Int8Shift product: activations
/// shifted to unsigned and each pair of products summed with int16 saturation.
/// </summary>
[TestFixture]
public class Int8GemmTest
{
	// True: the portable kernel; false: the platform's default kernel (the same one where there is
	// no platform-specific kernel).
	static Int8Kernel Kernel(bool portable) => portable ? Int8Kernel.Portable : Int8Gemm.DefaultKernel;

	[TestCase(1, 4)]
	[TestCase(15, 4)]
	[TestCase(40, 4)]
	[TestCase(1, 16)]
	[TestCase(15, 16)]
	[TestCase(18, 16)]
	[TestCase(512, 0)]
	public void UnsupportedPackedWidthsKeepRowMajorLayout(int width, int panelWidth)
	{
		var matrix = QuantizedMatrix.Create(new sbyte[7 * width], 7, width, 40f, 60f);

		Assert.That(matrix.Pack(panelWidth), Is.SameAs(matrix));
		Assert.That(matrix.IsPacked, Is.False);
		Assert.That(matrix.Row(0).Length, Is.EqualTo(width));
	}

	[Test]
	public void PackingPreservesValuesAndSharesMetadata([Values(1, 2, 3, 4, 7, 17)] int rows, [Values(16, 32, 48, 512)] int width, [Values(4, 16)] int panelWidth)
	{
		var data = new sbyte[rows * width];
		new Random(42).NextBytes((byte[])(Array)data);
		var matrix = QuantizedMatrix.Create(data, rows, width, 40f, 60f);

		var packed = matrix.Pack(panelWidth);

		Assert.That(packed.IsPacked, Is.True);
		Assert.That(packed.PanelWidth, Is.EqualTo(panelWidth));
		Assert.That(packed.Pack(panelWidth), Is.SameAs(packed));
		Assert.That(packed.RowSums, Is.SameAs(matrix.RowSums));
		Assert.That(packed.SaturationOffsets, Is.SameAs(matrix.SaturationOffsets));
		Assert.That(packed.SaturationPairs, Is.SameAs(matrix.SaturationPairs));
		Assert.That(packed.Data, Has.Length.EqualTo((rows + panelWidth - 1) / panelWidth * panelWidth * width));
		for (int row = 0; row < (rows + panelWidth - 1) / panelWidth * panelWidth; row++)
		{
			for (int k = 0; k < width; k++)
			{
				int offset = row / panelWidth * panelWidth * width + (k & ~3) * panelWidth + row % panelWidth * 4 + (k & 3);
				Assert.That(packed.Data[offset], Is.EqualTo(row < rows ? data[row * width + k] : 0));
			}
		}
	}

	[Test]
	public void SaturationPairsCoverEveryWeightPair([Values(0, 1, 2, 31, 32, 33)] int extra)
	{
		// Every (w0, w1) combination once, in a row long enough for all vector widths, plus a tail.
		var data = new sbyte[2 * 65536 + extra];
		for (int i = 0; i < 65536; i++)
		{
			data[2 * i] = (sbyte)(i & 0xFF);
			data[2 * i + 1] = (sbyte)(i >> 8);
		}
		for (int i = 0; i < extra; i++)
			data[2 * 65536 + i] = (sbyte)(i % 2 == 0 ? 127 : 100);
		var expected = new List<int>();
		for (int k = 0; k + 1 < data.Length; k += 2)
		{
			int w0 = data[k], w1 = data[k + 1];
			if ((w0 >= 0) == (w1 >= 0) && 254 * Math.Abs(w0 + w1) > short.MaxValue)
				expected.Add(k);
		}

		var created = QuantizedMatrix.Create(data, 1, data.Length, 1f, 1f);
		var streamed = QuantizedMatrix.CreateRows(1, data.Length, 1f, 1f, 0, (row, _) => data.CopyTo(row));

		Assert.That(created.SaturationPairs, Is.EqualTo(expected));
		Assert.That(created.SaturationOffsets, Is.EqualTo(new[] { 0, expected.Count }));
		Assert.That(streamed.SaturationPairs, Is.EqualTo(expected));
	}

	[Test]
	public void QuantizeMatchesScalarRoundingAndSaturation([Values] bool portable, [Values(0, 1, 15, 16, 17, 31, 32, 513)] int length, [Values(1f, 60f, 127f)] float alpha)
	{
		var random = new Random(42);
		var input = new float[length];
		float[] edges = [-200f, -127.5f, -126.5f, -1.5f, -0.5f, 0f, 0.5f, 1.5f, 126.5f, 127.5f, 200f, float.NegativeInfinity, float.PositiveInfinity, float.NaN];
		for (int i = 0; i < length; i++)
			input[i] = i < edges.Length ? edges[i] / alpha : (float)(random.NextDouble() * 8 - 4);
		var expected = input.Select(x => (sbyte)Math.Clamp(MathF.Round(x * alpha), -127f, 127f)).ToArray();
		var activations = new QuantizedActivations();

		Int8Gemm.Quantize(input, alpha, activations, Kernel(portable));

		if (Kernel(portable) == Int8Kernel.X86)
		{
			Assert.That(activations.Shifted.AsSpan(0, length).ToArray(), Is.EqualTo(expected.Select(x => (byte)(x + 127))));
			Assert.That(activations.Values, Is.Empty);
			return;
		}
		Assert.That(activations.Values.AsSpan(0, length).ToArray(), Is.EqualTo(expected));
		if (Kernel(portable) == Int8Kernel.Portable)
			Assert.That(activations.Widened.AsSpan(0, length).ToArray(), Is.EqualTo(expected.Select(x => (short)x)));
	}

	[Test]
	public void MatchesIntgemmSemantics([Values] bool packed, [Values] bool portable, [Values(1, 2, 3, 4, 6, 8, 9, 11, 15, 17, 33)] int rows, [Values(16, 36, 40, 512, 1536)] int width, [Values(false, true)] bool parallel)
	{
		var kernel = Kernel(portable);
		var random = new Random(rows * 1000 + width);
		const int outputs = 37;
		// Large same-sign weight pairs make the int16 pair sums saturate.
		var weights = new sbyte[outputs * width];
		for (int i = 0; i < weights.Length; i++)
			weights[i] = (sbyte)(i % 7 == 0 ? 127 : random.Next(-127, 128));
		var matrix = QuantizedMatrix.Create(weights, outputs, width, quantMult: 50f, alpha: 0f);
		if (packed)
			matrix = matrix.Pack(Int8Gemm.PanelWidth(kernel));
		var x = new float[rows * width];
		for (int i = 0; i < x.Length; i++)
			x[i] = (float)(random.NextDouble() * 4 - 2);
		const float alpha = 60f;
		var bias = Enumerable.Range(0, outputs).Select(j => j * 0.25f).ToArray();

		var activations = new QuantizedActivations();
		Int8Gemm.Quantize(x, alpha, activations, kernel);
		float unquant = 1f / (alpha * matrix.QuantMult);
		var prepared = Int8Gemm.PrepareBias(matrix, alpha, bias);
		var y = new float[rows * outputs];
		Int8Gemm.Multiply(activations, rows, matrix, null, outputs, unquant, prepared, y, kernel, parallel);

		for (int r = 0; r < rows; r++)
		{
			for (int j = 0; j < outputs; j++)
			{
				int sum = 0;
				for (int k = 0; k < width; k += 2)
				{
					int a0 = Quantized(x[r * width + k]) + 127, a1 = Quantized(x[r * width + k + 1]) + 127;
					sum += Math.Clamp(a0 * weights[j * width + k] + a1 * weights[j * width + k + 1], short.MinValue, short.MaxValue);
				}
				Assert.That(BitConverter.SingleToInt32Bits(y[r * outputs + j]), Is.EqualTo(BitConverter.SingleToInt32Bits(sum * unquant + prepared[j])), $"row {r}, output {j}");
			}
		}

		static sbyte Quantized(float value) => (sbyte)Math.Clamp(MathF.Round(value * alpha), -127f, 127f);
	}

	[Test]
	public void LargeProductIsSplitAcrossThreadsWithIdenticalResults([Values] bool packed, [Values] bool portable)
	{
		// 8 x 512 x 8003 multiply-adds is above the size from which products run in parallel.
		var kernel = Kernel(portable);
		var random = new Random(2);
		const int rows = 8, width = 512, outputs = 8003;
		var weights = new sbyte[outputs * width];
		random.NextBytes((byte[])(Array)weights);
		var matrix = QuantizedMatrix.Create(weights, outputs, width, 40f, 0f);
		var original = matrix;
		if (packed)
			matrix = matrix.Pack(Int8Gemm.PanelWidth(kernel));
		var x = Enumerable.Range(0, rows * width).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
		var activations = new QuantizedActivations();
		Int8Gemm.Quantize(x, 100f, activations, kernel);
		var prepared = Int8Gemm.PrepareBias(matrix, 100f, null);
		var serial = new float[rows * outputs];
		var parallel = new float[rows * outputs];

		Int8Gemm.Multiply(activations, rows, original, null, outputs, 1f / 4000f, prepared, serial, kernel, parallel: false);
		Int8Gemm.Multiply(activations, rows, matrix, null, outputs, 1f / 4000f, prepared, parallel, kernel, parallel: true);

		Assert.That(parallel.Select(BitConverter.SingleToInt32Bits), Is.EqualTo(serial.Select(BitConverter.SingleToInt32Bits)));
	}

	[Test]
	public void SelectedRows([Values] bool packed, [Values] bool portable, [Values(1, 2, 4, 5)] int rows, [Values(1, 2, 3, 4, 7)] int selectedCount)
	{
		var kernel = Kernel(portable);
		var random = new Random(1);
		const int width = 64, outputs = 50;
		var weights = new sbyte[outputs * width];
		random.NextBytes((byte[])(Array)weights);
		var matrix = QuantizedMatrix.Create(weights, outputs, width, 40f, 0f);
		var original = matrix;
		// The x86 kernel keeps selected rows (the shortlist) row-major.
		if (packed && kernel == Int8Kernel.ArmDotProduct)
			matrix = matrix.Pack(Int8Gemm.PanelWidth(kernel));
		var x = Enumerable.Range(0, rows * width).Select(_ => (float)random.NextDouble()).ToArray();
		var activations = new QuantizedActivations();
		Int8Gemm.Quantize(x, 100f, activations, kernel);
		var prepared = Int8Gemm.PrepareBias(matrix, 100f, null);
		var all = new float[rows * outputs];
		Int8Gemm.Multiply(activations, rows, original, null, outputs, 1f / 4000f, prepared, all, kernel, parallel: false);
		int[] selected = [49, 3, 17, 3, 0, 48, 7];
		selected = selected[..selectedCount];
		var some = new float[rows * selected.Length];
		Int8Gemm.Multiply(activations, rows, matrix, selected, selected.Length, 1f / 4000f, prepared, some, kernel, parallel: false);

		for (int r = 0; r < rows; r++)
		{
			for (int j = 0; j < selected.Length; j++)
				Assert.That(some[r * selected.Length + j], Is.EqualTo(all[r * outputs + selected[j]]));
		}
	}
}
