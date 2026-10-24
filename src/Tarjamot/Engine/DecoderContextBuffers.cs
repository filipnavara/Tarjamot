namespace Tarjamot.Engine;

/// <summary>Worker-owned context for every decoder layer; the first key buffer also serves the encoder.</summary>
internal sealed class DecoderContextBuffers
{
	public DecoderContextBuffers(int layers)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(layers);
		Keys = new float[layers][];
		Values = new float[layers][];
		Array.Fill(Keys, Array.Empty<float>());
		Array.Fill(Values, Array.Empty<float>());
	}

	public float[][] Keys { get; }
	public float[][] Values { get; }

	// Conservative 64-bit headers/alignment, including the outer reference arrays.
	public long RetainedBytes
	{
		get
		{
			long bytes = 48 + ((Keys.LongLength * IntPtr.Size + 7) & ~7L) + ((Values.LongLength * IntPtr.Size + 7) & ~7L);
			foreach (var buffer in Keys) bytes += BufferBytes(buffer);
			foreach (var buffer in Values) bytes += BufferBytes(buffer);
			return bytes;
		}
	}

	public float[] EnsureEncoderKeys(int elements)
	{
		if (Keys[0].Length < elements) Keys[0] = new float[elements];
		return Keys[0];
	}

	public void Ensure(int keyElements, int valueElements)
	{
		for (int l = 0; l < Keys.Length; l++)
		{
			// Exact growth avoids retaining a second batch's worth of every layer's contexts.
			if (Keys[l].Length < keyElements) Keys[l] = new float[keyElements];
			if (Values[l].Length < valueElements) Values[l] = new float[valueElements];
		}
	}

	// Per sentence segment: [dim][padded length], with the key length padded to a multiple of 4.
	public static void TransposeKeys(float[] source, int[] offsets, int[] lengths, float[] destination, int[] transposedOffsets, int dim)
	{
		for (int s = 0; s < lengths.Length; s++)
		{
			int padded = (lengths[s] + 3) & ~3;
			if (padded != lengths[s])
			{
				for (int c = 0; c < dim; c++)
					destination.AsSpan(transposedOffsets[s] + c * padded + lengths[s], padded - lengths[s]).Clear();
			}
			for (int j = 0; j < lengths[s]; j++)
			{
				var row = source.AsSpan((offsets[s] + j) * dim, dim);
				for (int c = 0; c < dim; c++)
					destination[transposedOffsets[s] + c * padded + j] = row[c];
			}
		}
	}

	static long BufferBytes(float[] buffer) => 24 + ((buffer.LongLength * sizeof(float) + 7) & ~7L);
}
