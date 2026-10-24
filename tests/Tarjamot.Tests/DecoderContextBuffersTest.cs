using Tarjamot.Engine;
using NUnit.Framework;

namespace Tarjamot.Tests;

[TestFixture]
public class DecoderContextBuffersTest
{
	[TestCase(1)]
	[TestCase(2)]
	[TestCase(6)]
	public void ContextsAreDistinctAndGrowExactlyWithoutShrinking(int layers)
	{
		var contexts = new DecoderContextBuffers(layers);
		contexts.Ensure(17, 13);
		var keys = contexts.Keys.ToArray();
		var values = contexts.Values.ToArray();
		Assert.That(keys.Concat(values).Distinct(ReferenceEqualityComparer.Instance).Count(), Is.EqualTo(layers * 2));
		Assert.That(keys.All(buffer => buffer.Length == 17), Is.True);
		Assert.That(values.All(buffer => buffer.Length == 13), Is.True);
		foreach (var buffer in keys) Array.Fill(buffer, 123f);
		contexts.Ensure(9, 7);
		for (int l = 0; l < layers; l++)
		{
			Assert.That(contexts.Keys[l], Is.SameAs(keys[l]));
			Assert.That(contexts.Values[l], Is.SameAs(values[l]));
			Assert.That(contexts.Keys[l], Is.All.EqualTo(123f));
		}
		contexts.Ensure(18, 13);
		Assert.That(contexts.Keys.All(buffer => buffer.Length == 18), Is.True);
		for (int l = 0; l < layers; l++) Assert.That(contexts.Values[l], Is.SameAs(values[l]));
		long expected = 48 + 2 * ((layers * (long)IntPtr.Size + 7) & ~7L) + layers * (24 + 72 + 24 + 56);
		Assert.That(contexts.RetainedBytes, Is.EqualTo(expected));
	}

	[Test]
	public void EncoderAllocatesOnlyFirstKeyAndDecoderKeepsItsOwnership()
	{
		var contexts = new DecoderContextBuffers(3);
		var encoder = contexts.EnsureEncoderKeys(17);
		Assert.That(encoder, Is.SameAs(contexts.Keys[0]));
		Assert.That(contexts.Keys.Skip(1).All(buffer => buffer.Length == 0), Is.True);
		Assert.That(contexts.Values.All(buffer => buffer.Length == 0), Is.True);
		contexts.Ensure(17, 13);
		Assert.That(contexts.Keys[0], Is.SameAs(encoder));
		Assert.That(contexts.EnsureEncoderKeys(5), Is.SameAs(encoder));
	}

	[Test]
	public void ContextCapacitiesParticipateInPoolBudgetAndAreReleasedTogether()
	{
		var contexts = new DecoderContextBuffers(3);
		contexts.Ensure(100, 80);
		long bytes = contexts.RetainedBytes;
		var pool = new IdleWorkerPool<DecoderContextBuffers>(2, bytes * 2, bytes);
		pool.Return(contexts, false, contexts.RetainedBytes);
		Assert.That(pool.Take(false), Is.SameAs(contexts));
		contexts.Ensure(101, 80);
		pool.Return(contexts, false, contexts.RetainedBytes);
		Assert.That(pool.Retained, Is.EqualTo((0, 0L)));
		Assert.That(pool.Take(false), Is.Null);
	}

	[TestCase(1)]
	[TestCase(8)]
	[TestCase(64)]
	public void TransposeOverwritesValidKeysAndClearsReusedPaddingAcrossLayouts(int dim)
	{
		var contexts = new DecoderContextBuffers(2);
		contexts.Ensure(64 * dim, 48 * dim);
		foreach (var lengths in new[] { new[] { 7, 2, 5 }, new[] { 1, 9 }, new[] { 4, 3, 1, 2 }, new[] { 7, 2, 5 } })
		{
			var offsets = new int[lengths.Length];
			var transposedOffsets = new int[lengths.Length];
			int tokens = 0, size = 0;
			for (int s = 0; s < lengths.Length; s++)
			{
				offsets[s] = tokens;
				transposedOffsets[s] = size;
				tokens += lengths[s];
				size += ((lengths[s] + 3) & ~3) * dim;
			}
			for (int l = 0; l < contexts.Keys.Length; l++)
			{
				var input = Enumerable.Range(1, tokens * dim).Select(value => (float)(value + l * 10000)).ToArray();
				Array.Fill(contexts.Keys[l], float.NaN);
				DecoderContextBuffers.TransposeKeys(input, offsets, lengths, contexts.Keys[l], transposedOffsets, dim);
				var expected = new List<float>();
				for (int s = 0; s < lengths.Length; s++)
				{
					int padded = (lengths[s] + 3) & ~3;
					for (int c = 0; c < dim; c++)
					{
						for (int t = 0; t < padded; t++)
							expected.Add(t < lengths[s] ? input[(offsets[s] + t) * dim + c] : 0f);
					}
				}
				Assert.That(contexts.Keys[l].AsSpan(0, size).ToArray(), Is.EqualTo(expected));
				Assert.That(contexts.Keys[l].Skip(size), Is.All.NaN);
			}
		}
	}
}
