using System.Collections.Concurrent;

namespace Tarjamot.Engine;

internal sealed record MarianEngineOptions(TranslationParallelism Parallelism, int MaxBatchTokens,
	int MaxIdleWorkers, long MaxIdleWorkerBytes, long MaxIdleWorkerPoolBytes);

/// <summary>
/// Translates sentences with a model, splitting them into batches by length (Bergamot's
/// mini-batch-words) and running the batches on a pool of <see cref="MarianTranslator"/> workers
/// that share the weights. Thread-safe.
/// </summary>
internal sealed class MarianEngine
{
	// Across sentences, the text is cut into about two batches per thread (but not below
	// MinBatchTokens) so that the threads finish at about the same time. There is one thread per
	// physical core: on a Ryzen 9 9950X (16 cores, 32 threads), SMT siblings cost twice the CPU
	// time for the same wall time, and slowed the longest batch of a Chinese article by 15%.
	const int BatchesPerThread = 2;
	const int MinBatchTokens = 64;

	readonly MarianWeights weights;
	readonly SentencePieceVocab sourceVocab;
	readonly SentencePieceVocab targetVocab;
	readonly Shortlist? shortlist;
	readonly MarianEngineOptions options;
	readonly IdleWorkerPool<MarianTranslator> workers;

	public MarianEngine(MarianModel model, SentencePieceVocab sourceVocab, SentencePieceVocab targetVocab, Shortlist? shortlist, MarianEngineOptions options)
	{
		weights = new MarianWeights(model, useShortlist: shortlist != null);
		this.sourceVocab = sourceVocab;
		this.targetVocab = targetVocab;
		this.shortlist = shortlist;
		this.options = options;
		workers = new IdleWorkerPool<MarianTranslator>(options.MaxIdleWorkers, options.MaxIdleWorkerPoolBytes, options.MaxIdleWorkerBytes);
	}

	/// <summary>Encodes once, retaining the pre-EOS empty status for initial sentence filtering.</summary>
	public TokenizedSentence TokenizeSentence(string sentence) => new(sourceVocab.Encode(sentence), sourceVocab.EosId);

	/// <summary>Later pivot legs use their own vocabulary, keeping the original sentence segmentation.</summary>
	public string[] TranslateSentences(IReadOnlyList<string> sentences, CancellationToken cancellationToken) =>
		TranslateTokenizedSentences(sentences.Select(TokenizeSentence).ToArray(), cancellationToken);

	/// <summary>Consumes request-owned sequences that already contain exactly one appended EOS.</summary>
	public string[] TranslateTokenizedSentences(IReadOnlyList<TokenizedSentence> sentences, CancellationToken cancellationToken) =>
		TranslateTokens(sentences.Select(sentence => sentence.Tokens).ToList(), null, cancellationToken).Select(targetVocab.Decode).ToArray();

	internal SentencePieceVocab SourceVocab => sourceVocab;

	internal SentencePieceVocab TargetVocab => targetVocab;

	/// <summary>
	/// Translates token sequences (each ending with EOS) into target token ids, without EOS. With
	/// <paramref name="alignments"/> (one list per source), also collects the soft alignment of every
	/// emitted token including EOS, see <see cref="MarianTranslator.TranslateBatch"/>.
	/// </summary>
	public List<int>[] TranslateTokens(List<List<int>> sources, List<float[]>[]? alignments, CancellationToken cancellationToken)
	{
		var outputs = new List<int>[sources.Count];
		int totalTokens = sources.Sum(source => source.Count);

		if (options.Parallelism != TranslationParallelism.AcrossSentences)
		{
			RunSequentially(CreateBatches(sources, options.MaxBatchTokens), options.Parallelism == TranslationParallelism.WithinProducts);
		}
		else
		{
			int threads = ProcessorTopology.PhysicalCores;
			int batchTokens = Math.Clamp((totalTokens + threads * BatchesPerThread - 1) / (threads * BatchesPerThread), MinBatchTokens, options.MaxBatchTokens);
			var batches = CreateBatches(sources, batchTokens);
			if (batches.Count == 1)
			{
				RunSequentially(batches, parallel: true);
			}
			else
			{
				// Longest batches first, handed out one at a time as threads become free.
				var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = cancellationToken };
				Parallel.ForEach(Partitioner.Create(batches, loadBalance: true), parallelOptions, batch => Run(batch, parallel: false));
			}
		}
		return outputs;

		void RunSequentially(List<int[]> batches, bool parallel)
		{
			foreach (var batch in batches)
				Run(batch, parallel);
		}

		void Run(int[] batch, bool parallel)
		{
			var worker = workers.Take(parallel) ?? new MarianTranslator(weights, targetVocab, shortlist, new MarianTranslatorOptions(parallel));
			try
			{
				var batchAlignments = alignments is null ? null : batch.Select(i => alignments[i]).ToArray();
				var batchOutputs = worker.TranslateBatch(batch.Select(i => sources[i]).ToArray(), cancellationToken, batchAlignments);
				for (int i = 0; i < batch.Length; i++)
					outputs[batch[i]] = batchOutputs[i];
			}
			finally
			{
				workers.Return(worker, parallel, worker.RetainedBytes);
			}
		}
	}

	// Sentences sorted by length, longest first, grouped up to the token limit.
	static List<int[]> CreateBatches(List<List<int>> sources, int maxTokens)
	{
		var order = Enumerable.Range(0, sources.Count).OrderByDescending(i => sources[i].Count).ToArray();
		var batches = new List<int[]>();
		int position = 0;
		while (position < order.Length)
		{
			int end = position;
			int tokens = 0;
			while (end < order.Length && (end == position || tokens + sources[order[end]].Count <= maxTokens))
				tokens += sources[order[end++]].Count;
			batches.Add(order[position..end]);
			position = end;
		}
		return batches;
	}
}
