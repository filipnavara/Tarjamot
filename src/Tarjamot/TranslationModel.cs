using Tarjamot.Engine;

namespace Tarjamot;

/// <summary>
/// A loaded Firefox Translations (Bergamot) model for one direction. Translation is CPU-bound
/// and synchronous. The model is thread-safe; concurrent calls use separate work buffers.
/// </summary>
public sealed class TranslationModel
{
	readonly MarianEngine engine;
	readonly string targetLanguage;
	readonly TranslationOptions options;

	TranslationModel(MarianEngine engine, string targetLanguage, TranslationOptions options)
	{
		this.engine = engine;
		this.targetLanguage = targetLanguage;
		this.options = options;
	}

	/// <summary>
	/// Loads the model files from a directory: "model.*.bin", either "vocab.*.spm" or
	/// "srcvocab.*.spm" and "trgvocab.*.spm", and optionally the shortlist "lex.*.bin". The target
	/// language decides whether sentences are separated by spaces (not in Japanese and Chinese).
	/// </summary>
	public static TranslationModel Load(string directory, string targetLanguage, TranslationOptions? options = null)
	{
		options ??= TranslationOptions.Default;
		options.ValidateRetention();
		var kernel = options.UsePortableKernel ? Int8Kernel.Portable : Int8Gemm.DefaultKernel;
		using var modelStream = File.OpenRead(FindFile(directory, "model.*.bin")!);
		var model = new MarianModel(modelStream, kernel);
		var sharedVocab = FindFile(directory, "vocab.*.spm", required: false);
		var sourceVocab = new SentencePieceVocab(File.ReadAllBytes(sharedVocab ?? FindFile(directory, "srcvocab.*.spm")!));
		var targetVocab = sharedVocab is null ? new SentencePieceVocab(File.ReadAllBytes(FindFile(directory, "trgvocab.*.spm")!)) : sourceVocab;
		var shortlistPath = options.UseShortlist ? FindFile(directory, "lex.*.bin") : null;
		var shortlist = shortlistPath is null ? null : new Shortlist(File.ReadAllBytes(shortlistPath));
		var engineOptions = new MarianEngineOptions(
			kernel,
			options.Parallelism,
			options.MaxBatchTokens,
			options.MaxIdleWorkers,
			options.MaxIdleWorkerBytes,
			options.MaxIdleWorkerPoolBytes);
		return new TranslationModel(new MarianEngine(model, sourceVocab, targetVocab, shortlist, engineOptions), targetLanguage, options);
	}

	/// <summary>
	/// Translates plain text sentence by sentence. Whitespace and line breaks between sentences
	/// are kept, as in Firefox.
	/// </summary>
	public string Translate(string text, CancellationToken cancellationToken = default) =>
		TextTranslation.Translate(text, [this], targetLanguage, options.SplitSentences, cancellationToken);

	/// <summary>Translates HTML fragments, see <see cref="LocalTranslator.TranslateMarkupAsync"/>.</summary>
	public string[] TranslateMarkup(IReadOnlyList<string> fragments, CancellationToken cancellationToken = default) =>
		options.MarkupTranslator.Translate(fragments, [this], cancellationToken);

	internal MarianEngine Engine => engine;

	internal string TargetLanguage => targetLanguage;

	internal TokenizedSentence TokenizeSentence(string sentence) => engine.TokenizeSentence(sentence);

	internal IReadOnlyList<string> TranslateTokenizedSentences(IReadOnlyList<TokenizedSentence> sentences, CancellationToken cancellationToken) =>
		engine.TranslateTokenizedSentences(sentences, cancellationToken);

	internal IReadOnlyList<string> TranslateSentences(IReadOnlyList<string> sentences, CancellationToken cancellationToken) =>
		engine.TranslateSentences(sentences, cancellationToken);

	static string? FindFile(string directory, string pattern, bool required = true)
	{
		var files = Directory.GetFiles(directory, pattern);
		return files.Length switch
		{
			1 => files[0],
			0 when !required => null,
			0 => throw new FileNotFoundException($"No file matching {pattern} in {directory}."),
			_ => throw new InvalidDataException($"More than one file matches {pattern} in {directory}."),
		};
	}
}
