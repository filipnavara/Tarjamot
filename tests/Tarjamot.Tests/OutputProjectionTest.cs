using Tarjamot.Engine;
using NUnit.Framework;

namespace Tarjamot.Tests;

[TestFixture]
public class OutputProjectionTest
{
	static IEnumerable<object[]> Shapes()
	{
		foreach (var shape in new[] { (1, 1, 1), (1, 15, 7), (3, 16, 5), (4, 32, 9), (7, 48, 17), (2, 64, 20), (6, 384, 33), (8, 384, 33), (11, 384, 33), (15, 512, 33), (33, 1536, 33), (65, 17, 31), (17, 512, 4099) })
		foreach (var mode in Modes)
		foreach (bool parallel in new[] { false, true })
			yield return [shape.Item1, shape.Item2, shape.Item3, mode, parallel];
	}

	[TestCaseSource(nameof(Shapes))]
	public void WinnersMatchMultiplyIncludingSaturationAndTails(int rows, int width, int outputs, string mode, bool parallel)
	{
		var kernel = Kernel(mode);
		var random = new Random(137 + rows + width + outputs);
		var input = Enumerable.Range(0, rows * width).Select(_ => (sbyte)random.Next(-127, 128)).ToArray();
		var data = Enumerable.Range(0, outputs * width).Select(_ => (sbyte)random.Next(-128, 128)).ToArray();
		// Force many overflowing adjacent pairs in both directions.
		for (int j = 0; j < outputs; j++)
		{
			for (int k = 0; k + 1 < width; k += 4)
				data[j * width + k] = data[j * width + k + 1] = (sbyte)(j % 2 == 0 ? 127 : -128);
		}
		var weights = Layout(QuantizedMatrix.Create(data, outputs, width, 23.5f, 37.25f), mode);
		var activations = Activations(input);
		var bias = Int8Gemm.PrepareBias(weights, weights.Alpha, Enumerable.Range(0, outputs).Select(_ => random.NextSingle() - 0.5f).ToArray());
		float unquant = 1f / (weights.Alpha * weights.QuantMult);
		var logits = new float[rows * outputs];
		Int8Gemm.Multiply(activations, rows, weights, null, outputs, unquant, bias, logits, kernel, parallel);
		var scratch = new ArgMaxScratch();
		foreach (int excluded in new[] { 0, outputs - 1, -1 })
		{
			Assert.That(Int8Gemm.MultiplyArgMax(activations, rows, weights, unquant, bias, excluded, scratch, kernel, parallel).ToArray(),
				Is.EqualTo(ReferenceWinners(logits, rows, outputs, excluded)));
		}
		// Reuse after a larger invocation must reset every winner and only expose logical rows.
		Array.Fill(bias, float.NegativeInfinity);
		Assert.That(Int8Gemm.MultiplyArgMax(activations, 1, weights, unquant, bias, -1, scratch, kernel, parallel).ToArray(), Is.EqualTo(new[] { -1 }));
		Assert.That(scratch.RetainedBytes, Is.LessThan(48 + 16L * rows * Math.Max(1, Environment.ProcessorCount * 4)));
	}

	static IEnumerable<object[]> ExceptionalCases()
	{
		foreach (var scenario in new[] { ("negativeInfinity", -1), ("nan", -1), ("ties", 0), ("excludedFirst", 1), ("excludedInfinity", 4096), ("infinityTies", 1023), ("partitionTies", 1023) })
		foreach (var mode in Modes)
		foreach (bool parallel in new[] { false, true })
			yield return [scenario.Item1, scenario.Item2, mode, parallel];
	}

	[TestCaseSource(nameof(ExceptionalCases))]
	public void ExceptionalScoresAndParallelTiesPreserveFirstWinner(string scenario, int expected, string mode, bool parallel)
	{
		var kernel = Kernel(mode);
		const int rows = 17, width = 512, outputs = 4099;
		var weights = Layout(QuantizedMatrix.Create(new sbyte[outputs * width], outputs, width, 1f, 1f), mode);
		var activations = Activations(new sbyte[rows * width]);
		var bias = new float[outputs];
		int excluded = -1;
		switch (scenario)
		{
			case "negativeInfinity": Array.Fill(bias, float.NegativeInfinity); break;
			case "nan": Array.Fill(bias, float.NaN); break;
			case "excludedFirst": excluded = 0; break;
			case "excludedInfinity": excluded = 4095; bias[4095] = bias[4096] = float.PositiveInfinity; break;
			case "infinityTies": bias[1023] = bias[1024] = float.PositiveInfinity; break;
			case "partitionTies": bias[1023] = bias[1024] = 10f; break;
		}
		var logits = new float[rows * outputs];
		Int8Gemm.Multiply(activations, rows, weights, null, outputs, 1f, bias, logits, kernel, parallel);
		var winners = Int8Gemm.MultiplyArgMax(activations, rows, weights, 1f, bias, excluded, new ArgMaxScratch(), kernel, parallel).ToArray();
		Assert.That(winners, Is.All.EqualTo(expected));
		Assert.That(winners, Is.EqualTo(ReferenceWinners(logits, rows, outputs, excluded)));
	}

	[Test]
	public void EmptyVocabularyAndRowsHaveNoWinners()
	{
		var weights = QuantizedMatrix.Create([], 0, 16, 1f, 1f);
		var activations = new QuantizedActivations { Values = new sbyte[48], Widened = new short[48] };
		var scratch = new ArgMaxScratch();
		Assert.That(Int8Gemm.MultiplyArgMax(activations, 3, weights, 1f, [], -1, scratch, Int8Kernel.Portable, true).ToArray(), Is.EqualTo(new[] { -1, -1, -1 }));
		Assert.That(Int8Gemm.MultiplyArgMax(activations, 0, weights, 1f, [], -1, scratch, Int8Kernel.Portable, true).ToArray(), Is.Empty);
	}

	static readonly string[] Modes = ["portable", "raw", "packed", "x86raw", "x86packed"];

	static Int8Kernel Kernel(string mode)
	{
		if (mode == "portable") return Int8Kernel.Portable;
		if (mode.StartsWith("x86"))
		{
			if (Int8Gemm.DefaultKernel != Int8Kernel.X86) Assert.Ignore("x86 AVX2 instructions are unavailable.");
			return Int8Kernel.X86;
		}
		if (Int8Gemm.DefaultKernel != Int8Kernel.ArmDotProduct) Assert.Ignore("ARM dot-product instructions are unavailable.");
		return Int8Kernel.ArmDotProduct;
	}

	static QuantizedMatrix Layout(QuantizedMatrix weights, string mode) =>
		mode.EndsWith("packed") ? weights.Pack(Int8Gemm.PanelWidth(Kernel(mode))) : weights;

	static QuantizedActivations Activations(sbyte[] input) => new()
	{
		Values = input,
		Widened = input.Select(value => (short)value).ToArray(),
		Shifted = input.Select(value => (byte)(value + 127)).ToArray(),
	};

	static int[] ReferenceWinners(float[] logits, int rows, int outputs, int excluded)
	{
		var result = new int[rows];
		for (int r = 0; r < rows; r++)
		{
			float best = float.NegativeInfinity;
			result[r] = -1;
			for (int j = 0; j < outputs; j++)
			{
				if (j != excluded && logits[r * outputs + j] > best)
				{
					best = logits[r * outputs + j];
					result[r] = j;
				}
			}
		}
		return result;
	}
}
