using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;
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

	// SHA-256 of the embeddings of positions 0-1023, computed with musl's expf, sinf and cosf called
	// separately per value, as Marian does.
	[TestCase(256, "1D2A9E67B884370F28ADCF0C1EB5736772C11C9588D7A1A6E604A71EB2AF6690")]
	[TestCase(384, "3423C53E4C9C5FB3AFC3FBD9BA65DF6C65BF55FF0723C46EEA374D0B088858F5")]
	[TestCase(512, "0E8E4CA6F1850FF18FE8049A968650D17F5A2EA8D2C2AB1D14EF0A9C1FC6DD45")]
	public void PositionEmbeddingsHaveMuslBits(int dim, string expected)
	{
		if (typeof(MarianMath).Assembly.GetType("Tarjamot.Engine.MuslMath") is null)
			Assert.Ignore("Built with the platform's math (TarjamotPlatformMath).");
		var scales = MarianMath.PositionScales(dim);
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		for (int position = 0; position < 1024; position++)
			hash.AppendData(MemoryMarshal.AsBytes(MarianMath.PositionEmbedding(position, scales).AsSpan()));

		Assert.That(Convert.ToHexString(hash.GetHashAndReset()), Is.EqualTo(expected));
	}

	[Test]
	public void SinCosOfSpanMatchesScalar()
	{
		// A spread of float bit patterns up to the large-argument limit, and both sides of every
		// threshold of musl's sincosf.
		var bits = new List<uint>();
		for (uint b = 0; b < 0x4dc90fdb; b += 4099)
			bits.Add(b);
		foreach (uint threshold in new uint[] { 0x39800000, 0x3f490fda, 0x4016cbe3, 0x407b53d1, 0x40afeddf, 0x40e231d5 })
			bits.AddRange([threshold - 1, threshold, threshold + 1]);
		bits.AddRange([0x4dc90fd9, 0x4dc90fda]);
		var x = bits.Select(BitConverter.UInt32BitsToSingle).ToArray();
		var expectedSin = new float[x.Length];
		var expectedCos = new float[x.Length];
		for (int i = 0; i < x.Length; i++)
			MarianMath.SinCos(x[i], out expectedSin[i], out expectedCos[i]);
		var sin = new float[x.Length];
		var cos = new float[x.Length];

		MarianMath.SinCos(x, sin, cos);

		Assert.That(sin.Select(BitConverter.SingleToInt32Bits), Is.EqualTo(expectedSin.Select(BitConverter.SingleToInt32Bits)));
		Assert.That(cos.Select(BitConverter.SingleToInt32Bits), Is.EqualTo(expectedCos.Select(BitConverter.SingleToInt32Bits)));
	}
}
