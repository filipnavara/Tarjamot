using NUnit.Framework;

namespace Tarjamot.Tests;

/// <summary>
/// End-to-end translation with models downloaded from Mozilla's Remote Settings. The expected
/// translations are the output of Firefox's Bergamot WASM build for the same models.
/// </summary>
[TestFixture]
[Category("ExternalServices")]
public class LocalTranslatorTest
{
	// TARJAMOT_TEST_MODELS lets CI keep the downloaded models in a cached directory.
	static readonly string ModelsDirectory = Environment.GetEnvironmentVariable("TARJAMOT_TEST_MODELS") is { Length: > 0 } directory
		? directory
		: Path.Combine(Path.GetTempPath(), "Tarjamot.Tests");

	// Several sentences of different lengths, so that they form several batches across threads.
	static readonly string Paragraph = string.Join(" ", Enumerable.Range(1, 40).Select(i =>
		i % 3 == 0 ? $"Dobr\u00FD den, pos\u00EDl\u00E1m v\u00E1m smlouvu \u010D\u00EDslo {i} k podpisu." : i % 3 == 1 ? $"Sch\u016Fzka {i} se kon\u00E1 v p\u00E1tek." : $"Pros\u00EDm o vyj\u00E1d\u0159en\u00ED k bodu {i} do konce t\u00FDdne."));

	HttpClient httpClient = null!;
	TranslationModelStore store = null!;

	[OneTimeSetUp]
	public async Task DownloadModelsAsync()
	{
		httpClient = new HttpClient();
		store = new TranslationModelStore(ModelsDirectory, httpClient);
		var catalog = await TranslationModelCatalog.LoadAsync(httpClient, TranslationPlatform.Desktop);
		foreach (var pair in new[] { new LanguagePair("cs", "en"), new LanguagePair("en", "ja"), new LanguagePair("en", "ru") })
		{
			var files = catalog.GetFiles(pair);
			if (!store.IsInstalled(pair, files))
				await store.InstallAsync(pair, files);
		}
	}

	[OneTimeTearDown]
	public void DisposeHttpClient() => httpClient.Dispose();

	[TestCase("cs", "en", "Dobr\u00FD den, jak se m\u00E1te?", "Hello, how are you?")]
	[TestCase("cs", "en", "Pos\u00EDl\u00E1m v\u00E1m smlouvu. Pros\u00EDm o podpis do p\u00E1tku.", "I'm sending you a contract. Please sign by Friday.")]
	[TestCase("cs", "en", "  Ahoj.\n   Jak se m\u00E1\u0161?  ", "  Oh, hey.\n   How are you?  ")]
	[TestCase("en", "ja", "Hello. How are you?\nFine.  Thanks.", "\u3053\u3093\u306B\u3061\u306F\u3002\u304A\u5143\u6C17\u3067\u3059\u304B?\n\u5927\u4E08\u592B\u3002\u3042\u308A\u304C\u3068\u3046\u3002")]
	[TestCase("cs", "ja", "Dobr\u00FD den, pos\u00EDl\u00E1m v\u00E1m smlouvu k podpisu. Pros\u00EDm o vyj\u00E1d\u0159en\u00ED do p\u00E1tku.", "\u3053\u3093\u306B\u3061\u306F\u3001\u5951\u7D04\u66F8\u3092\u9001\u4ED8\u3057\u3066\u7F72\u540D\u3057\u307E\u3059\u3002\u91D1\u66DC\u65E5\u307E\u3067\u306B\u30B3\u30E1\u30F3\u30C8\u3057\u3066\u304F\u3060\u3055\u3044\u3002")]
	public async Task TranslatesLikeFirefox(string source, string target, string text, string expected)
	{
		using var translator = new LocalTranslator(store);
		Assert.That(await translator.TranslateAsync(source, target, text), Is.EqualTo(expected));
	}

	// Bergamot's HTML mode; the expected translations come from Firefox's WASM build.
	[TestCase("cs", "en", "Klikn\u011Bte <a href=\"x\">sem</a> pro odhl\u00E1\u0161en\u00ED odb\u011Bru.", "Click <a href=\"x\">here</a> to unsubscribe.")]
	[TestCase("cs", "en", "<p>Prvn\u00ED odstavec.</p><p>Druh\u00FD odstavec s <i>kurz\u00EDvou</i>.</p>", "<p>The first paragraph.</p><p>Second paragraph with <i>italics</i>.</p>")]
	[TestCase("en", "ja", "<p>The first paragraph.</p><p>The second paragraph has a <a href=x>link</a>.</p>", "<p>\u6700\u521D\u306E\u6BB5\u843D\u3002</p><p><a href=\"x\"></a>2\u756A\u76EE\u306E\u6BB5\u843D\u306B\u306F\u30EA\u30F3\u30AF\u304C\u3042\u308A\u307E\u3059\u3002</p>")]
	[TestCase("en", "ja", "Two sentences. <b>The second one is bold. And so is the third.</b> The fourth is not.", "2\u6587\u3002<b>2\u3064\u76EE\u306F\u5927\u80C6\u3067\u3059\u3002\u305D\u3057\u3066\u7B2C\u4E09\u3082\u540C\u69D8\u3067\u3042\u308B\u3002</b>\u7B2C\u56DB\u306F\u305D\u3046\u3067\u306F\u306A\u3044\u3002")]
	[TestCase("cs", "ja", "Klikn\u011Bte <a href=\"x\">sem</a> pro odhl\u00E1\u0161en\u00ED odb\u011Bru.", "<a href=\"x\"></a>\u3053\u3053\u3092\u30AF\u30EA\u30C3\u30AF\u3057\u3066\u914D\u4FE1\u3092\u89E3\u9664\u3057\u3066\u304F\u3060\u3055\u3044\u3002")]
	[TestCase("cs", "ja", "<p>Prvn\u00ED odstavec.</p><p>Druh\u00FD odstavec s <i>kurz\u00EDvou</i>.</p>", "<p>\u6700\u521D\u306E\u6BB5\u843D\u3002</p><p><i></i>\u30A4\u30BF\u30EA\u30A2\u8A9E\u306B\u3088\u308B\u7B2C2\u6BB5\u843D\u3002</p>")]
	public async Task TranslatesMarkupLikeFirefox(string source, string target, string html, string expected)
	{
		using var translator = new LocalTranslator(store, new TranslationOptions { MarkupTranslator = BergamotMarkupTranslator.Instance });
		Assert.That(await translator.TranslateMarkupAsync(source, target, [html]), Is.EqualTo(new[] { expected }));
	}

	// ProjectedMarkupTranslator, the default: the same fragments as above and a few more. These are
	// Tarjamot's own outputs, not Firefox's; see docs/MARKUP-COMPARISON.md.
	[TestCase("cs", "en", "Klikn\u011Bte <a href=\"x\">sem</a> pro odhl\u00E1\u0161en\u00ED odb\u011Bru.", "Click <a href=\"x\">here</a> to unsubscribe.")]
	[TestCase("cs", "en", "<p>Prvn\u00ED odstavec.</p><p>Druh\u00FD odstavec s <i>kurz\u00EDvou</i>.</p>", "<p>The first paragraph.</p><p>Second paragraph with <i>italics</i>.</p>")]
	[TestCase("en", "ja", "<p>The first paragraph.</p><p>The second paragraph has a <a href=x>link</a>.</p>", "<p>\u6700\u521D\u306E\u6BB5\u843D\u3002</p><p>2\u756A\u76EE\u306E\u6BB5\u843D<a href=x>\u306B\u306F\u30EA\u30F3\u30AF</a>\u304C\u3042\u308A\u307E\u3059\u3002</p>")]
	[TestCase("en", "ja", "Two sentences. <b>The second one is bold. And so is the third.</b> The fourth is not.", "2\u6587\u3002<b>2\u3064\u76EE\u306F\u5927\u80C6\u3067\u3059\u3002\u305D\u3057\u3066\u7B2C\u4E09\u3082\u540C\u69D8\u3067\u3042\u308B\u3002</b>\u7B2C\u56DB\u306F\u305D\u3046\u3067\u306F\u306A\u3044\u3002")]
	[TestCase("cs", "ja", "Klikn\u011Bte <a href=\"x\">sem</a> pro odhl\u00E1\u0161en\u00ED odb\u011Bru.", "<a href=\"x\">\u3053\u3053</a>\u3092\u30AF\u30EA\u30C3\u30AF\u3057\u3066\u914D\u4FE1\u3092\u89E3\u9664\u3057\u3066\u304F\u3060\u3055\u3044\u3002")]
	[TestCase("cs", "ja", "<p>Prvn\u00ED odstavec.</p><p>Druh\u00FD odstavec s <i>kurz\u00EDvou</i>.</p>", "<p>\u6700\u521D\u306E\u6BB5\u843D\u3002</p><p><i>\u30A4\u30BF\u30EA\u30A2\u8A9E</i>\u306B\u3088\u308B\u7B2C2\u6BB5\u843D\u3002</p>")]
	[TestCase("en", "ja", "Run <code>git push</code> to upload your changes to the server.", "\u5909\u66F4\u5185\u5BB9\u3092\u30B5\u30FC\u30D0\u30FC\u306B\u30A2\u30C3\u30D7\u30ED\u30FC\u30C9<code>git push</code>\u3059\u308B\u306B\u306F\u5B9F\u884C\u3057\u3066\u304F\u3060\u3055\u3044\u3002")]
	[TestCase("en", "ja", "Tom &amp; Jerry &lt;3 forever&hellip; and the price is &euro;5&nbsp;per month.", "\u30C8\u30E0&amp;\u30B8\u30A7\u30EA\u30FC &lt;3 \u6C38\u4E45...\u305D\u3057\u3066\u6708\u984D5\u30E6\u30FC\u30ED\u3067\u3059\u3002")]
	[TestCase("en", "ja", "<b>Prague</b> is the capital and largest city of the <a href=\"/wiki/Czech_Republic\">Czech Republic</a> and the historical capital of <a href=\"/wiki/Bohemia\">Bohemia</a>.", "<b>\u30D7\u30E9\u30CF</b>\u306F<a href=\"/wiki/Czech_Republic\">\u30C1\u30A7\u30B3\u5171\u548C\u56FD</a>\u306E\u9996\u90FD\u3067\u3042\u308A\u6700\u5927\u306E\u90FD\u5E02\u3067\u3042\u308A\u3001<a href=\"/wiki/Bohemia\">\u30DC\u30D8\u30DF\u30A2</a>\u306E\u6B74\u53F2\u7684\u9996\u90FD\u3067\u3059\u3002")]
	[TestCase("cs", "ja", "Toto je <i>velmi <b>d\u016Fle\u017Eit\u00E1</b> zpr\u00E1va</i> pro v\u0161echny.", "\u3053\u308C\u306F\u8AB0\u306B\u3068\u3063\u3066\u3082<i><b>\u975E\u5E38\u306B\u91CD\u8981\u306A</b>\u30E1\u30C3\u30BB\u30FC\u30B8</i>\u3067\u3059\u3002")]
	public async Task TranslatesMarkupByProjection(string source, string target, string html, string expected)
	{
		using var translator = new LocalTranslator(store);
		Assert.That(await translator.TranslateMarkupAsync(source, target, [html]), Is.EqualTo(new[] { expected }));
	}

	// Short cells of a table row are translated together, so that each has the others as context:
	// alone, "Sun" is the star. Markup around a cell's words goes back around its translation. A
	// cell with an element among its words ("Save <i>all</i>") is translated alone and projected.
	// Alone, "Faktura.pdf" comes out as nonsense in katakana. The last two rows do not split into
	// one part per cell ("Home \u30FB \u9023\u7D61\u5148\u306B\u3064\u3044\u3066", and a middle dot inside "Active minutes"), so
	// their cells are translated one by one, the same as the paragraphs.
	[TestCase("en", "ru", "<table><tr><td>Mon</td><td>Tue</td><td>Wed</td><td>Thu</td><td>Fri</td><td>Sat</td><td>Sun</td></tr></table>", "<table><tr><td>\u041F\u043D</td><td>\u0412\u0442</td><td>\u0421\u0440\u0434</td><td>\u0427\u0442</td><td>\u041F\u0442</td><td>\u0421\u0431</td><td>\u0412\u0441</td></tr></table>")]
	[TestCase("en", "ru", "<p>Mon</p><p>Tue</p><p>Wed</p><p>Thu</p><p>Fri</p><p>Sat</p><p>Sun</p>", "<p>\u041F\u043D</p><p>\u0412\u0442</p><p>\u0421\u0440</p><p>\u0422\u0445\u0443</p><p>\u041F\u0442</p><p>\u0421\u0431</p><p>\u0421\u043E\u043B\u043D\u0446\u0435</p>")]
	[TestCase("en", "ja", "<table><tr><td>Mon</td><td>Tue</td><td>Wed</td><td>Thu</td><td>Fri</td><td>Sat</td><td>Sun</td></tr></table>", "<table><tr><td>\u6708\u66DC\u65E5</td><td>\u706B\u66DC\u65E5</td><td>\u6C34\u66DC\u65E5</td><td>\u6728\u66DC\u65E5</td><td>\u91D1\u66DC\u65E5</td><td>\u571F\u66DC\u65E5</td><td>\u65E5\u66DC\u65E5</td></tr></table>")]
	[TestCase("en", "ja", "<p>Mon</p><p>Tue</p><p>Wed</p><p>Thu</p><p>Fri</p><p>Sat</p><p>Sun</p>", "<p>\u6708\u66DC\u65E5</p><p>\u706B\u66DC\u65E5</p><p>\u6C34\u66DC\u65E5</p><p>\u6728\u66DC\u65E5</p><p>\u91D1\u66DC\u65E5</p><p>\u571F\u66DC\u65E5</p><p>\u30B5\u30F3</p>")]
	[TestCase("en", "ja", "<table><tr><td><a href=\"/o\">Open</a></td><td><b>Close</b></td><td>Save <i>all</i></td></tr></table>", "<table><tr><td><a href=\"/o\">\u30AA\u30FC\u30D7\u30F3</a></td><td><b>\u30AF\u30ED\u30FC\u30BA</b></td><td><i>\u3059\u3079\u3066</i>\u4FDD\u5B58\u3057\u3066\u304F\u3060\u3055\u3044</td></tr></table>")]
	[TestCase("en", "ja", "<table><tr><td><span style=\"x\"><font>Mon</font></span></td><td><span style=\"x\"><font>Sun</font></span></td></tr></table>", "<table><tr><td><span style=\"x\"><font>\u6708\u66DC\u65E5</font></span></td><td><span style=\"x\"><font>\u65E5\u66DC\u65E5</font></span></td></tr></table>")]
	[TestCase("en", "ja", "<table><tr><td>Movies</td><td><img src=\"x.png\"> Episodes</td></tr></table>", "<table><tr><td>\u6620\u753B</td><td><img src=\"x.png\">\u30A8\u30D4\u30BD\u30FC\u30C9</td></tr></table>")]
	[TestCase("cs", "ja", "<table><tr><td>Faktura.pdf</td><td>1. 11. 2026</td><td>120 kB</td></tr></table>", "<table><tr><td>Invoice.pdf</td><td>1.11.2026\u5E74</td><td>120 KB</td></tr></table>")]
	[TestCase("en", "ja", "<table><tr><td>Home</td><td>About</td><td>Contact</td></tr></table>", "<table><tr><td>\u30DB\u30FC\u30E0</td><td>\u306B\u3064\u3044\u3066</td><td>\u9023\u7D61\u3059\u308B</td></tr></table>")]
	[TestCase("en", "ja", "<p>Home</p><p>About</p><p>Contact</p>", "<p>\u30DB\u30FC\u30E0</p><p>\u306B\u3064\u3044\u3066</p><p>\u9023\u7D61\u3059\u308B</p>")]
	[TestCase("en", "ja", "<table><tr><td>Steps</td><td>Distance</td><td>Calories</td><td>Active minutes</td></tr></table>", "<table><tr><td>\u624B\u9806</td><td>\u8DDD\u96E2</td><td>\u30AB\u30ED\u30EA\u30FC</td><td>\u30A2\u30AF\u30C6\u30A3\u30D6\u30FB\u30DF\u30CB\u30C3\u30C4</td></tr></table>")]
	public async Task TranslatesShortCellsOfARowTogether(string source, string target, string html, string expected)
	{
		using var translator = new LocalTranslator(store);
		Assert.That(await translator.TranslateMarkupAsync(source, target, [html]), Is.EqualTo(new[] { expected }));
	}

	// Elements with translate="no" are copied as written: a block element in place, an inline one
	// next to the word it preceded, like <code>.
	[Test]
	public async Task KeepsContentWithTranslateNo()
	{
		using var translator = new LocalTranslator(store);
		Assert.That(await translator.TranslateMarkupAsync("en", "ja", ["<p>Watch <span translate=\"no\">Raw</span> tonight.</p><div translate=no><p>Hello world.</p></div>"]),
			Is.EqualTo(new[] { "<p><span translate=\"no\">Raw</span>\u4ECA\u591C\u306F\u898B\u3066\u304F\u3060\u3055\u3044\u3002</p><div translate=no><p>Hello world.</p></div>" }));
	}

	[Test]
	public async Task DisabledIdleRetentionPreservesPivotAndCancellation()
	{
		using var translator = new LocalTranslator(store, new TranslationOptions { MaxCachedModels = 0, MaxIdleWorkers = 0 });
		const string text = "Dobr\u00FD den, jak se m\u00E1te?";
		var first = await translator.TranslateAsync("cs", "ja", text);
		Assert.That(await translator.TranslateAsync("cs", "ja", text), Is.EqualTo(first));
		await Assert.ThatAsync(() => translator.TranslateAsync("cs", "ja", text, new CancellationToken(canceled: true)), Throws.TypeOf<TaskCanceledException>());
		Assert.That(await translator.TranslateAsync("cs", "en", text), Is.EqualTo("Hello, how are you?"));
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task PretokenizationPreservesNormalizedEmptyTextsAndRanges(bool splitSentences)
	{
		var options = new TranslationOptions { SplitSentences = splitSentences, Parallelism = TranslationParallelism.None };
		var model = TranslationModel.Load(store.GetDirectory(new LanguagePair("cs", "en")), "en", options);
		Assert.That(model.TokenizeSentence("\u200B").HasTokens, Is.False);
		using var translator = new LocalTranslator(store, options);
		string[] texts = ["\u200B", " \t", "  Dobr\u00FD den, jak se m\u00E1te?\n \u200B\n", "Pos\u00EDl\u00E1m v\u00E1m smlouvu."];
		Assert.That(await translator.TranslateAsync("cs", "en", texts), Is.EqualTo(new[]
		{
			"", "", "  Hello, how are you?\n \u200B\n", "I'm sending you a contract."
		}));
	}

	[TestCase(false)]
	[TestCase(true)]
	public void PretokenizedPivotMatchesRetokenizingEachLegWithItsOwnVocabulary(bool splitSentences)
	{
		var options = new TranslationOptions { SplitSentences = splitSentences, Parallelism = TranslationParallelism.None };
		var source = TranslationModel.Load(store.GetDirectory(new LanguagePair("cs", "en")), "en", options);
		var target = TranslationModel.Load(store.GetDirectory(new LanguagePair("en", "ja")), "ja", options);
		string[] texts = ["\u200B", "  Dobr\u00FD den, jak se m\u00E1te?\n", "Pos\u00EDl\u00E1m v\u00E1m smlouvu. Pros\u00EDm o podpis do p\u00E1tku."];
		var ranges = texts.Select(text => (splitSentences ? SentenceSplitter.SplitRanges(text) : SentenceSplitter.SplitLines(text))
			.Where(range => source.TokenizeSentence(text[range]).HasTokens).ToArray()).ToArray();
		IReadOnlyList<string> sentences = texts.SelectMany((text, i) => ranges[i].Select(range => text[range])).ToArray();
		// Reproduce the previous boundary: filtering discards tokens, then each leg encodes strings.
		sentences = source.TranslateSentences(sentences, CancellationToken.None);
		sentences = target.TranslateSentences(sentences, CancellationToken.None);
		var expected = new string[texts.Length];
		int offset = 0;
		for (int i = 0; i < texts.Length; i++)
		{
			expected[i] = ranges[i].Length == 0 ? "" : TextTranslation.Assemble(texts[i], ranges[i], sentences.Skip(offset).Take(ranges[i].Length).ToArray(), "ja");
			offset += ranges[i].Length;
		}
		Assert.That(TextTranslation.Translate(texts, [source, target], "ja", splitSentences, CancellationToken.None), Is.EqualTo(expected));
	}

	[Test]
	public void ParallelismDoesNotChangeTheResult()
	{
		var directory = store.GetDirectory(new LanguagePair("cs", "en"));
		var results = Enum.GetValues<TranslationParallelism>()
			.Select(parallelism => TranslationModel.Load(directory, "en", new TranslationOptions { Parallelism = parallelism }).Translate(Paragraph))
			.ToArray();

		Assert.That(results, Is.All.EqualTo(results[0]));
	}

	[Test]
	public async Task ConcurrentTranslationsWithOneModel()
	{
		var model = TranslationModel.Load(store.GetDirectory(new LanguagePair("cs", "en")), "en", new TranslationOptions { Parallelism = TranslationParallelism.None });
		var expected = model.Translate(Paragraph);

		var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => model.Translate(Paragraph))));

		Assert.That(results, Is.All.EqualTo(expected));
	}

	[TestCase(TranslationParallelism.None)]
	[TestCase(TranslationParallelism.WithinProducts)]
	[TestCase(TranslationParallelism.AcrossSentences)]
	public void ReusingWorkersForDifferentBatchSizesPreservesTranslation(TranslationParallelism parallelism)
	{
		var model = TranslationModel.Load(store.GetDirectory(new LanguagePair("cs", "en")), "en", new TranslationOptions { Parallelism = parallelism });
		const string greeting = "Dobr\u00FD den, jak se m\u00E1te?";
		var paragraph = model.Translate(Paragraph);

		Assert.That(model.Translate(greeting), Is.EqualTo("Hello, how are you?"));
		Assert.That(model.Translate(Paragraph), Is.EqualTo(paragraph));
		Assert.Throws<OperationCanceledException>(() => model.Translate(Paragraph, new CancellationToken(canceled: true)));
		Assert.That(model.Translate(greeting), Is.EqualTo("Hello, how are you?"));
		Assert.That(model.Translate(Paragraph), Is.EqualTo(paragraph));
	}
}
