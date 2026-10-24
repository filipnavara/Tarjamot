namespace Tarjamot;

/// <summary>How a translation uses several threads.</summary>
public enum TranslationParallelism
{
	/// <summary>One thread.</summary>
	None,

	/// <summary>
	/// Large matrix products are split across threads; batches of sentences run one after another.
	/// About 1.8x faster than one thread for about 1.3x its CPU time.
	/// </summary>
	WithinProducts,

	/// <summary>
	/// Batches of sentences run on all threads at once, each single-threaded (a single batch is
	/// split within its products). Another 20-40% faster than <see cref="WithinProducts"/> for
	/// texts of several sentences, but for about 1.7x its CPU time: with all cores busy, memory
	/// bandwidth, efficiency cores and lower clock speeds limit the gain to about 2.7x over one
	/// thread on an Apple M5, the same as independent processes.
	/// </summary>
	AcrossSentences,
}

/// <summary>Settings of the translation engine. The defaults match Firefox.</summary>
public sealed record TranslationOptions
{
	public static TranslationOptions Default { get; } = new();

	/// <summary>
	/// Restricts the output layer to the lexical shortlist, which roughly halves the translation
	/// time. Firefox disables it (browser.translations.useLexicalShortlist) because it makes the
	/// models invent words, see https://bugzilla.mozilla.org/show_bug.cgi?id=1940972.
	/// Requires the shortlist file to be downloaded.
	/// </summary>
	public bool UseShortlist { get; init; }

	/// <summary>How the translation uses several threads.</summary>
	public TranslationParallelism Parallelism { get; init; } = TranslationParallelism.WithinProducts;

	/// <summary>Maximum number of source tokens translated together (Bergamot's mini-batch-words).</summary>
	public int MaxBatchTokens { get; init; } = 1024;

	/// <summary>Splits lines into sentences; when false, each line is translated as one sentence.</summary>
	public bool SplitSentences { get; init; } = true;

	/// <summary>
	/// How HTML fragments keep their markup: <see cref="ProjectedMarkupTranslator"/> (the default),
	/// or BergamotMarkupTranslator from the Tarjamot.Markup package for Firefox's exact output.
	/// </summary>
	public MarkupTranslator MarkupTranslator { get; init; } = ProjectedMarkupTranslator.Instance;

	/// <summary>Uses the portable int8 kernel even where a platform-specific one is available.</summary>
	public bool UsePortableKernel { get; init; }

	/// <summary>
	/// Maximum idle workers retained by each model, across all execution modes. When a pool limit
	/// is reached, the least recently returned idle workers are evicted.
	/// </summary>
	public int MaxIdleWorkers { get; init; } = 16;

	/// <summary>Maximum owned scratch bytes retained by one idle worker; larger workers are discarded.</summary>
	public long MaxIdleWorkerBytes { get; init; } = 96L * 1024 * 1024;

	/// <summary>Total owned scratch byte budget for the idle workers of each model.</summary>
	public long MaxIdleWorkerPoolBytes { get; init; } = 128L * 1024 * 1024;

	/// <summary>Maximum cached models when idle. In-use models may temporarily exceed this limit.</summary>
	public int MaxCachedModels { get; init; } = 4;

	/// <summary>
	/// Models idle this long are released by a background sweep, which runs at least once per minute
	/// while idle models exist.
	/// </summary>
	public TimeSpan ModelIdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

	internal void ValidateRetention()
	{
		ArgumentOutOfRangeException.ThrowIfNegative(MaxIdleWorkers);
		ArgumentOutOfRangeException.ThrowIfNegative(MaxIdleWorkerBytes);
		ArgumentOutOfRangeException.ThrowIfNegative(MaxIdleWorkerPoolBytes);
		ArgumentOutOfRangeException.ThrowIfNegative(MaxCachedModels);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ModelIdleTimeout, TimeSpan.Zero);
	}
}
