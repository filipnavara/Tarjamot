namespace Tarjamot;

/// <summary>
/// Translates with the models installed in a <see cref="TranslationModelStore"/>, without network
/// access. Pairs without a direct model are translated through English with two models, as in
/// Firefox. Idle models are bounded by the cache count and idle timeout in <see cref="TranslationOptions"/>.
/// </summary>
public sealed class LocalTranslator : IDisposable
{
	readonly TranslationModelStore store;
	readonly TranslationOptions options;
	readonly ModelCache<LanguagePair, TranslationModel> models;

	public LocalTranslator(TranslationModelStore store, TranslationOptions? options = null)
	{
		this.store = store;
		this.options = options ?? TranslationOptions.Default;
		this.options.ValidateRetention();
		models = new ModelCache<LanguagePair, TranslationModel>(
			pair => TranslationModel.Load(store.GetDirectory(pair), pair.Target, this.options),
			this.options.MaxCachedModels, this.options.ModelIdleTimeout);
	}

	/// <summary>The installed models for translating between two languages, or null if they are not installed.</summary>
	public LanguagePair[]? FindRoute(string source, string target) =>
		LanguagePair.FindRoute(store.GetInstalledPairs(), source, target);

	/// <summary>
	/// Translates plain text sentence by sentence, through English when there is no direct model.
	/// Whitespace and line breaks between sentences are kept, as in Firefox.
	/// </summary>
	public async Task<string> TranslateAsync(string source, string target, string text, CancellationToken cancellationToken = default) =>
		(await TranslateAsync(source, target, [text], cancellationToken).ConfigureAwait(false))[0];

	/// <summary>Translates several plain texts, batching their sentences together.</summary>
	public Task<string[]> TranslateAsync(string source, string target, IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
		RunAsync(source, target, models => TextTranslation.Translate(texts, models, target, options.SplitSentences, cancellationToken), cancellationToken);

	/// <summary>
	/// Translates HTML fragments as Firefox does: the text of each fragment is translated in whole
	/// sentences, and its markup (links, emphasis, images and so on) is placed around the translated words
	/// they align with. Block elements such as &lt;p&gt; or &lt;li&gt; separate sentences. The
	/// fragments must be well-formed (every element closed, except void elements like &lt;br&gt;).
	/// </summary>
	public Task<string[]> TranslateMarkupAsync(string source, string target, IReadOnlyList<string> fragments, CancellationToken cancellationToken = default) =>
		RunAsync(source, target, models => Markup.MarkupTranslation.Translate(fragments, models, cancellationToken), cancellationToken);

	// Runs the translation on the thread pool with the route's models leased from the cache.
	Task<string[]> RunAsync(string source, string target, Func<TranslationModel[], string[]> translate, CancellationToken cancellationToken)
	{
		var route = FindRoute(source, target) ?? throw new NotSupportedException($"No installed models translate from {source} to {target}.");
		return Task.Run(() =>
		{
			var leases = new List<ModelCache<LanguagePair, TranslationModel>.Lease>(route.Length);
			try
			{
				foreach (var pair in route)
					leases.Add(models.Borrow(pair));
				var loaded = new TranslationModel[leases.Count];
				for (int i = 0; i < loaded.Length; i++)
				{
					cancellationToken.ThrowIfCancellationRequested();
					loaded[i] = leases[i].Value;
				}
				cancellationToken.ThrowIfCancellationRequested();
				return translate(loaded);
			}
			finally
			{
				foreach (var lease in leases)
					lease.Dispose();
			}
		}, cancellationToken);
	}

	/// <summary>Invalidates cached models immediately; active translations finish with their current models.</summary>
	public void UnloadModels() => models.Clear();

	/// <summary>Releases cached models and stops the idle sweep; active translations can finish.</summary>
	public void Dispose() => models.Dispose();
}
