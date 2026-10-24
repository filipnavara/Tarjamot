using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using static Tarjamot.Engine.MarianWeights;

namespace Tarjamot.Engine;

/// <summary>
/// Greedy decoder for the Bergamot "student" models: Transformer encoder, decoder with SSRU
/// auto-regressive layers, post-norm residuals, tied output embeddings and a lexical shortlist.
/// Models have either one shared vocabulary ("Wemb") or separate source and target vocabularies
/// ("encoder_Wemb", "decoder_Wemb"); the output layer is tied to the target embeddings.
/// Mirrors marian-fork/src/models/transformer.h for the configuration Firefox ships, including
/// int8 matrix products with the model's precomputed activation alphas (int8shiftAlphaAll).
/// A worker: it translates one batch at a time with its own work buffers; the weights are shared
/// (see <see cref="MarianEngine"/>).
/// </summary>
internal sealed record MarianTranslatorOptions(Int8Kernel Kernel, bool Parallel, bool UseShortlist);

internal sealed class MarianTranslator
{
	const float LayerNormEpsilon = 1e-6f;
	const float MaxLengthFactor = 2f;

	readonly MarianWeights weights;
	readonly SentencePieceVocab targetVocab;
	readonly Shortlist? shortlist;
	readonly MarianTranslatorOptions options;
	readonly QuantizedMatrix sourceEmbeddings;
	readonly QuantizedMatrix targetEmbeddings;
	readonly QuantizedMatrix outputWeights;
	readonly QuantizedMatrix outputWeightsShortlist;
	readonly Tensor outputBias;
	readonly EncoderLayer[] encoderLayers;
	readonly DecoderLayer[] decoderLayers;
	readonly int dim;
	readonly int heads;

	// Work buffers, grown on demand and reused across layers, decoder steps and batches.
	readonly QuantizedActivations quantized = new();
	readonly ArgMaxScratch outputWinners = new();
	readonly DecoderContextBuffers contexts;
	float[] bufferA = [], bufferB = [], bufferC = [], bufferD = [], bufferE = [], bufferHidden = [], bufferLogits = [], scores = [];

	public MarianTranslator(MarianWeights weights, SentencePieceVocab targetVocab, Shortlist? shortlist, MarianTranslatorOptions options)
	{
		this.weights = weights;
		this.targetVocab = targetVocab;
		this.shortlist = shortlist;
		this.options = options;
		sourceEmbeddings = weights.SourceEmbeddings;
		targetEmbeddings = weights.TargetEmbeddings;
		outputWeights = weights.OutputWeights;
		outputWeightsShortlist = weights.OutputWeightsShortlist;
		outputBias = weights.OutputBias;
		encoderLayers = weights.EncoderLayers;
		decoderLayers = weights.DecoderLayers;
		contexts = new DecoderContextBuffers(decoderLayers.Length);
		dim = weights.Dim;
		heads = weights.Heads;
	}

	// Owned arrays only: weights/vocabularies are shared by the engine.
	// Array headers/alignment use a conservative 64-bit estimate; capacities are actual lengths.
	internal long RetainedBytes => quantized.RetainedBytes + outputWinners.RetainedBytes + contexts.RetainedBytes + BufferBytes(bufferA) + BufferBytes(bufferB) +
		BufferBytes(bufferC) + BufferBytes(bufferD) + BufferBytes(bufferE) + BufferBytes(bufferHidden) +
		BufferBytes(bufferLogits) + BufferBytes(scores);

	static long BufferBytes(float[] buffer) => 24 + ((buffer.LongLength * sizeof(float) + 7) & ~7L);

	/// <summary>
	/// Translates token sequences (each ending with EOS) together. With <paramref name="alignments"/>,
	/// also returns for each emitted token (including EOS) Marian's soft alignment over the source
	/// tokens: the first attention head of the last decoder layer.
	/// </summary>
	public List<int>[] TranslateBatch(List<int>[] sources, CancellationToken cancellationToken, List<float[]>[]? alignments = null)
	{
		int count = sources.Length;
		var offsets = new int[count];
		int totalTokens = 0;
		for (int s = 0; s < count; s++)
		{
			offsets[s] = totalTokens;
			totalTokens += sources[s].Count;
		}

		var lengths = sources.Select(source => source.Count).ToArray();
		var transposedOffsets = new int[count];
		int transposedSize = 0;
		for (int s = 0; s < count; s++)
		{
			transposedOffsets[s] = transposedSize;
			transposedSize += dim * ((lengths[s] + 3) & ~3);
		}
		// Encoder layers share one transpose buffer. Once encoding is complete, the first
		// decoder context takes it over, so it needs no additional allocation.
		var transposed = contexts.EnsureEncoderKeys(transposedSize);
		var encoderOutput = Encode(sources, offsets, lengths, totalTokens, transposed, transposedOffsets);
		contexts.Ensure(transposedSize, totalTokens * dim);
		var contextKeys = contexts.Keys;
		var contextValues = contexts.Values;
		var keys = Ensure(ref bufferA, totalTokens * dim);
		for (int l = 0; l < decoderLayers.Length; l++)
		{
			var context = decoderLayers[l].Context;
			Linear(encoderOutput, totalTokens, context.Wk, context.Bk, keys);
			Linear(encoderOutput, totalTokens, context.Wv, context.Bv, contextValues[l]);
			DecoderContextBuffers.TransposeKeys(keys, offsets, lengths, contextKeys[l], transposedOffsets, dim);
		}

		var candidates = options.UseShortlist ? sources.Select(CreateCandidates).ToArray() : null;
		var cells = new float[count * decoderLayers.Length * dim];
		var previous = Enumerable.Repeat(-1, count).ToArray();
		var finished = new bool[count];
		var outputs = sources.Select(_ => new List<int>()).ToArray();
		var active = new List<int>(count);

		for (int step = 0; ; step++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			active.Clear();
			for (int s = 0; s < count; s++)
			{
				if (!finished[s] && step < (int)(sources[s].Count * MaxLengthFactor))
					active.Add(s);
			}
			if (active.Count == 0)
				break;

			var hidden = DecoderStep(active, previous, step, cells, contextKeys, transposedOffsets, contextValues, offsets, lengths, alignments);
			ReadOnlySpan<int> winners = options.UseShortlist ? [] : ProjectWinners(hidden, active.Count);
			for (int r = 0; r < active.Count; r++)
			{
				int s = active[r];
				int next;
				if (candidates != null)
				{
					// Each sentence scores only its own shortlist. Bergamot scores the union of the
					// batch's shortlists, which costs more and does not change the argmax.
					var logits = Ensure(ref bufferLogits, candidates[s].Length);
					hidden.AsSpan(r * dim, dim).CopyTo(Ensure(ref bufferA, dim));
					Linear(bufferA, 1, outputWeightsShortlist, outputBias, logits, candidates[s]);
					next = ArgMax(logits.AsSpan(0, candidates[s].Length), candidates[s]);
				}
				else
				{
					next = winners[r];
				}
				if (next == targetVocab.EosId)
				{
					finished[s] = true;
					continue;
				}
				outputs[s].Add(next);
				previous[s] = next;
			}
		}
		return outputs;
	}

	ReadOnlySpan<int> ProjectWinners(float[] hidden, int rows)
	{
		var input = hidden.AsSpan(0, rows * outputWeights.Cols);
		float alpha = outputWeights.Alpha != 0f ? outputWeights.Alpha : 127f / TensorPrimitives.MaxMagnitude<float>(input);
		Int8Gemm.Quantize(input, alpha, quantized, options.Kernel);
		var prepared = outputWeights.Alpha != 0f
			? weights.GetPreparedBias(outputWeights)
			: Int8Gemm.PrepareBias(outputWeights, alpha, outputBias.Data);
		return Int8Gemm.MultiplyArgMax(quantized, rows, outputWeights, 1f / (alpha * outputWeights.QuantMult),
			prepared, targetVocab.UnkId, outputWinners, options.Kernel, options.Parallel);
	}

	int[] CreateCandidates(List<int> source) => options.UseShortlist && shortlist != null
		? shortlist.Generate(source, targetEmbeddings.Rows, sharedVocab: ReferenceEquals(sourceEmbeddings, targetEmbeddings))
		: Enumerable.Range(0, targetEmbeddings.Rows).ToArray();

	int ArgMax(ReadOnlySpan<float> logits, int[]? candidates)
	{
		int best = -1;
		float bestLogit = float.NegativeInfinity;
		for (int i = 0; i < logits.Length; i++)
		{
			int token = candidates?[i] ?? i;
			if (token == targetVocab.UnkId)
				continue;
			if (logits[i] > bestLogit)
			{
				bestLogit = logits[i];
				best = token;
			}
		}
		return best;
	}

	float[] Encode(List<int>[] sources, int[] offsets, int[] lengths, int totalTokens, float[] transposed, int[] transposedOffsets)
	{
		// Decoder hidden states overwrite this buffer only after all context projections are ready.
		var x = Ensure(ref bufferE, totalTokens * dim);
		for (int s = 0; s < sources.Length; s++)
		{
			for (int t = 0; t < sources[s].Count; t++)
				Embed(sourceEmbeddings, sources[s][t], t, x.AsSpan((offsets[s] + t) * dim, dim));
		}

		foreach (var layer in encoderLayers)
		{
			var self = layer.Self;
			var q = Ensure(ref bufferA, totalTokens * dim);
			var k = Ensure(ref bufferB, totalTokens * dim);
			var v = Ensure(ref bufferC, totalTokens * dim);
			Linear(x, totalTokens, self.Wq, self.Bq, q);
			Linear(x, totalTokens, self.Wk, self.Bk, k);
			Linear(x, totalTokens, self.Wv, self.Bv, v);
			var attention = Ensure(ref bufferD, totalTokens * dim);
			DecoderContextBuffers.TransposeKeys(k, offsets, lengths, transposed, transposedOffsets, dim);
			for (int s = 0; s < sources.Length; s++)
			{
				for (int t = 0; t < lengths[s]; t++)
					Attend(q, offsets[s] + t, transposed, transposedOffsets[s], lengths[s], v, offsets[s], attention);
			}
			// The Wo projection reuses the query buffer, then x = LN(Wo + x).
			Linear(attention, totalTokens, self.Wo, self.Bo, q);
			AddAndNorm(x, q, totalTokens, self.LnScale, self.LnBias);
			FeedForward(x, totalTokens, layer.FeedForward);
		}
		return x;
	}

	// One decoder step for the active sentences; returns the top hidden states (one row each).
	float[] DecoderStep(List<int> active, int[] previous, int step, float[] cells, float[][] contextKeys, int[] transposedOffsets, float[][] contextValues, int[] offsets, int[] lengths, List<float[]>[]? alignments)
	{
		int rows = active.Count;
		var y = Ensure(ref bufferE, rows * dim);
		for (int r = 0; r < rows; r++)
			Embed(targetEmbeddings, previous[active[r]], step, y.AsSpan(r * dim, dim));

		for (int l = 0; l < decoderLayers.Length; l++)
		{
			var layer = decoderLayers[l];

			// SSRU: c = sigmoid(f) * c' + (1 - sigmoid(f)) * xW, h = relu(c), y = LN(h + y)
			var x = Ensure(ref bufferA, rows * dim);
			var f = Ensure(ref bufferB, rows * dim);
			Linear(y, rows, layer.RnnW, null, x);
			Linear(y, rows, layer.RnnWf, layer.RnnBf, f);
			for (int r = 0; r < rows; r++)
				MarianMath.Ssru(cells.AsSpan((active[r] * decoderLayers.Length + l) * dim, dim), x.AsSpan(r * dim, dim), f.AsSpan(r * dim, dim));
			AddAndNorm(y, x, rows, layer.RnnLnScale, layer.RnnLnBias);

			var context = layer.Context;
			var q = Ensure(ref bufferA, rows * dim);
			Linear(y, rows, context.Wq, context.Bq, q);
			var attention = Ensure(ref bufferC, rows * dim);
			bool align = alignments != null && l == decoderLayers.Length - 1;
			for (int r = 0; r < rows; r++)
			{
				int s = active[r];
				var alignment = align ? new float[lengths[s]] : null;
				Attend(q, r, contextKeys[l], transposedOffsets[s], lengths[s], contextValues[l], offsets[s], attention, alignment);
				if (alignment != null)
					alignments![s].Add(alignment);
			}
			var projected = Ensure(ref bufferB, rows * dim);
			Linear(attention, rows, context.Wo, context.Bo, projected);
			AddAndNorm(y, projected, rows, context.LnScale, context.LnBias);

			FeedForward(y, rows, layer.FeedForward);
		}
		return y;
	}

	void Embed(QuantizedMatrix embeddings, int token, int position, Span<float> destination)
	{
		if (token >= 0)
		{
			TensorPrimitives.ConvertChecked<sbyte, float>(embeddings.Row(token), destination);
			TensorPrimitives.Multiply(destination, 1f / embeddings.QuantMult, destination);
			TensorPrimitives.Multiply(destination, MathF.Sqrt(dim), destination);
		}
		else
		{
			// The first decoder step uses an all-zero embedding (no BOS token).
			destination.Clear();
		}

		TensorPrimitives.Add(destination, weights.GetPosition(position), destination);
	}

	// x = LN(FFN(x) + x), in place.
	void FeedForward(float[] x, int rows, FeedForwardLayer layer)
	{
		var hidden = Ensure(ref bufferHidden, rows * layer.W1.Rows);
		Linear(x, rows, layer.W1, layer.B1, hidden);
		var activations = hidden.AsSpan(0, rows * layer.W1.Rows);
		TensorPrimitives.Max(activations, 0f, activations);
		var output = Ensure(ref bufferD, rows * dim);
		Linear(hidden, rows, layer.W2, layer.B2, output);
		AddAndNorm(x, output, rows, layer.LnScale, layer.LnBias);
	}

	// Post-processing "dan": dropout (no-op), add, layer normalization; the result goes to x.
	void AddAndNorm(float[] x, float[] added, int rows, Tensor scale, Tensor bias)
	{
		for (int t = 0; t < rows; t++)
		{
			var row = x.AsSpan(t * dim, dim);
			TensorPrimitives.Add(added.AsSpan(t * dim, dim), row, row);
			MarianMath.LayerNorm(row, scale.Data, bias.Data, LayerNormEpsilon);
		}
	}

	// Multi-head attention of one query row over keyLength keys. Keys are transposed per head
	// (see TransposeKeys), so each score is a sequential sum over the head dimension, as in
	// Eigen's product, computed for four keys per vector. The weighted sum is sequential over the
	// keys and vectorized over the head dimension. Softmax follows Marian (MarianMath.Softmax).
	void Attend(float[] q, int queryRow, float[] keysTransposed, int keysOffset, int keyLength, float[] v, int valueStart, float[] output, float[]? alignment = null)
	{
		int headDim = dim / heads;
		int padded = (keyLength + 3) & ~3;
		float scale = 1f / MathF.Sqrt(headDim);
		var scale128 = Vector128.Create(scale);
		var scale256 = Vector256.Create(scale);
		var scale512 = Vector512.Create(scale);
		var headScores = Ensure(ref scores, 2 * padded);
		ref float rk = ref MemoryMarshal.GetArrayDataReference(keysTransposed);
		ref float rs = ref MemoryMarshal.GetArrayDataReference(headScores);
		// Two heads at a time keep twice as many independent sums in flight; see MarianMath.
		int h = 0;
		for (; h + 1 < heads; h += 2)
		{
			int queryBase = queryRow * dim + h * headDim;
			MarianMath.AttentionScoresPair(q.AsSpan(queryBase, 2 * headDim), keysTransposed.AsSpan(keysOffset + h * headDim * padded, 2 * headDim * padded),
				headDim, padded, scale, headScores);
			var scores0 = headScores.AsSpan(0, keyLength);
			var scores1 = headScores.AsSpan(padded, keyLength);
			MarianMath.Softmax(scores0, keyLength);
			MarianMath.Softmax(scores1, keyLength);
			if (h == 0 && alignment != null)
				scores0.CopyTo(alignment);
			var values = v.AsSpan(valueStart * dim + h * headDim);
			var destination = output.AsSpan(queryRow * dim + h * headDim, 2 * headDim);
			if (MarianMath.SupportsAttentionValuesPair(headDim))
			{
				MarianMath.AttentionValuesPair(scores0, scores1, values, dim, headDim, destination);
			}
			else
			{
				MarianMath.AttentionValues(scores0, values, dim, destination[..headDim]);
				MarianMath.AttentionValues(scores1, values[headDim..], dim, destination[headDim..]);
			}
		}
		for (; h < heads; h++)
		{
			int queryBase = queryRow * dim + h * headDim;
			int headBase = keysOffset + h * headDim * padded;
			// Each lane is one key, so wider vectors and independent blocks keep every key's sum order.
			int j = 0;
			if (Vector512.IsHardwareAccelerated)
			{
				for (; j + 32 <= padded; j += 32)
				{
					Vector512<float> acc0 = default, acc1 = default;
					for (int d = 0; d < headDim; d++)
					{
						var query = Vector512.Create(q[queryBase + d]);
						nuint keyOffset = (nuint)(headBase + j + d * padded);
						acc0 = acc0 + query * Vector512.LoadUnsafe(ref rk, keyOffset);
						acc1 = acc1 + query * Vector512.LoadUnsafe(ref rk, keyOffset + 16);
					}
					(scale512 * acc0).StoreUnsafe(ref rs, (nuint)j);
					(scale512 * acc1).StoreUnsafe(ref rs, (nuint)j + 16);
				}
				for (; j + 16 <= padded; j += 16)
				{
					var acc = Vector512<float>.Zero;
					for (int d = 0; d < headDim; d++)
						acc = acc + Vector512.Create(q[queryBase + d]) * Vector512.LoadUnsafe(ref rk, (nuint)(headBase + j + d * padded));
					(scale512 * acc).StoreUnsafe(ref rs, (nuint)j);
				}
			}
			if (Vector256.IsHardwareAccelerated)
			{
				for (; j + 8 <= padded; j += 8)
				{
					var acc = Vector256<float>.Zero;
					for (int d = 0; d < headDim; d++)
						acc = acc + Vector256.Create(q[queryBase + d]) * Vector256.LoadUnsafe(ref rk, (nuint)(headBase + j + d * padded));
					(scale256 * acc).StoreUnsafe(ref rs, (nuint)j);
				}
			}
			for (; j < padded; j += 4)
			{
				var acc = Vector128<float>.Zero;
				for (int d = 0; d < headDim; d++)
					acc = acc + Vector128.Create(q[queryBase + d]) * Vector128.LoadUnsafe(ref rk, (nuint)(headBase + j + d * padded));
				(scale128 * acc).StoreUnsafe(ref rs, (nuint)j);
			}
			MarianMath.Softmax(headScores.AsSpan(0, keyLength), keyLength);
			if (h == 0 && alignment != null)
				headScores.AsSpan(0, keyLength).CopyTo(alignment);
			MarianMath.AttentionValues(headScores.AsSpan(0, keyLength),
				v.AsSpan(valueStart * dim + h * headDim), dim,
				output.AsSpan(queryRow * dim + h * headDim, headDim));
		}
	}

	// y[t] = x[t] * W + b with int8 activations and weights, optionally computing only the
	// selected weight rows (output columns).
	void Linear(float[] x, int rows, QuantizedMatrix matrix, Tensor? bias, float[] y, int[]? weightRows = null)
	{
		var input = x.AsSpan(0, rows * matrix.Cols);
		// Without a precomputed alpha, Marian derives the multiplier from the activations.
		float alpha = matrix.Alpha != 0f ? matrix.Alpha : 127f / TensorPrimitives.MaxMagnitude<float>(input);
		Int8Gemm.Quantize(input, alpha, quantized, options.Kernel);
		var prepared = matrix.Alpha != 0f
			? weights.GetPreparedBias(matrix)
			: Int8Gemm.PrepareBias(matrix, alpha, bias?.Data);
		Int8Gemm.Multiply(quantized, rows, matrix, weightRows, weightRows?.Length ?? matrix.Rows, 1f / (alpha * matrix.QuantMult), prepared, y, options.Kernel, options.Parallel);
	}

	static float[] Ensure(ref float[] buffer, int size)
	{
		if (buffer.Length < size)
			buffer = new float[Math.Max(size, buffer.Length * 2)];
		return buffer;
	}
}
