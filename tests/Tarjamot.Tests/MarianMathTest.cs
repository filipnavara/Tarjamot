using System.Runtime.Intrinsics;
using Tarjamot.Engine;
using NUnit.Framework;

namespace Tarjamot.Tests;

[TestFixture]
public class MarianMathTest
{
	[Test]
	public void AttentionValuesPreservesSummationOrder([Values(0, 1, 3, 16, 63)] int keys, [Values(1, 4, 16, 17, 20, 32, 64)] int width)
	{
		const int stride = 512;
		var random = new Random(42);
		var scores = Enumerable.Range(0, keys).Select(_ => (float)random.NextDouble()).ToArray();
		var values = Enumerable.Range(0, keys * stride).Select(_ => (float)(random.NextDouble() * 200 - 100)).ToArray();
		var expected = new float[width];
		int c = 0;
		for (; c + 4 <= width; c += 4)
		{
			var sum = Vector128<float>.Zero;
			for (int s = 0; s < keys; s++)
				sum = sum + Vector128.Create(scores[s]) * Vector128.Create(values[s * stride + c], values[s * stride + c + 1], values[s * stride + c + 2], values[s * stride + c + 3]);
			for (int lane = 0; lane < 4; lane++)
				expected[c + lane] = sum[lane];
		}
		for (; c < width; c++)
		{
			float sum = 0f;
			for (int s = 0; s < keys; s++)
				sum += scores[s] * values[s * stride + c];
			expected[c] = sum;
		}
		var actual = new float[width];

		MarianMath.AttentionValues(scores, values, stride, actual);

		Assert.That(actual.Select(BitConverter.SingleToInt32Bits), Is.EqualTo(expected.Select(BitConverter.SingleToInt32Bits)));
	}

	[Test]
	public void AttentionValuesPairMatchesOneHeadAtATime([Values(0, 1, 3, 16, 63)] int keys, [Values(32, 48, 64)] int headDim)
	{
		if (!MarianMath.SupportsAttentionValuesPair(headDim))
			Assert.Ignore("512-bit vectors are not accelerated.");
		const int stride = 512;
		var random = new Random(headDim + keys);
		var scores0 = Enumerable.Range(0, keys).Select(_ => (float)random.NextDouble()).ToArray();
		var scores1 = Enumerable.Range(0, keys).Select(_ => (float)random.NextDouble()).ToArray();
		var values = Enumerable.Range(0, keys * stride + 2 * headDim).Select(_ => (float)(random.NextDouble() * 200 - 100)).ToArray();
		if (keys > 2)
		{
			values[stride + 3] = float.NaN;
			values[2 * stride + headDim + 5] = float.PositiveInfinity;
			values[headDim + 1] = -0f;
		}
		var expected = new float[2 * headDim];
		MarianMath.AttentionValues(scores0, values, stride, expected.AsSpan(0, headDim));
		MarianMath.AttentionValues(scores1, values.AsSpan(headDim), stride, expected.AsSpan(headDim));
		var actual = new float[2 * headDim];

		MarianMath.AttentionValuesPair(scores0, scores1, values, stride, headDim, actual);

		Assert.That(actual.Select(BitConverter.SingleToInt32Bits), Is.EqualTo(expected.Select(BitConverter.SingleToInt32Bits)));
	}

	[Test]
	public void AttentionScoresPairSumsEveryKeyInOrder([Values(1, 3, 4, 5, 12, 17, 31, 32, 33, 48, 60)] int keyLength, [Values(32, 48, 64)] int headDim)
	{
		int padded = (keyLength + 3) & ~3;
		var random = new Random(keyLength * 100 + headDim);
		var q = Enumerable.Range(0, 2 * headDim).Select(_ => (float)(random.NextDouble() * 4 - 2)).ToArray();
		var keys = Enumerable.Range(0, 2 * headDim * padded).Select(_ => (float)(random.NextDouble() * 4 - 2)).ToArray();
		keys[padded + keyLength - 1] = float.NaN;
		keys[headDim * padded] = float.NegativeInfinity;
		float scale = 1f / MathF.Sqrt(headDim);
		var expected = new float[2 * padded];
		for (int h = 0; h < 2; h++)
		{
			for (int j = 0; j < padded; j++)
			{
				float sum = 0f;
				for (int d = 0; d < headDim; d++)
					sum = sum + q[h * headDim + d] * keys[(h * headDim + d) * padded + j];
				expected[h * padded + j] = scale * sum;
			}
		}
		var actual = new float[2 * padded];

		MarianMath.AttentionScoresPair(q, keys, headDim, padded, scale, actual);

		Assert.That(actual.Select(BitConverter.SingleToInt32Bits), Is.EqualTo(expected.Select(BitConverter.SingleToInt32Bits)));
	}
}
