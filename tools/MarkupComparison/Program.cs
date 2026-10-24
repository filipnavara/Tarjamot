using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tarjamot;

// Compares the two ways of translating HTML on a corpus: BergamotMarkupTranslator (Firefox's
// algorithm, Tarjamot.Markup) and ProjectedMarkupTranslator (detag and project, Tarjamot).
const string Usage = """
	Usage:
	  MarkupComparison <models directory> <corpus.json> <output directory>

	Translates every fragment of the corpus into each target of its source language with both
	translators (the models must be downloaded, see Translate download), and writes results.json
	(every output) and report.md (metrics, timings and the outputs side by side).
	""";

if (args.Length != 3)
{
	Console.Error.WriteLine(Usage);
	return 1;
}
Console.OutputEncoding = new UTF8Encoding(false);

var targets = new Dictionary<string, string[]>
{
	["en"] = ["cs", "de", "ja"],
	["cs"] = ["en", "de", "ja"],
	["de"] = ["en", "cs"],
};

// Firefox's output for the same models (WASM build), from LocalTranslatorTest.
var firefox = new Dictionary<(string Html, string Target), string>
{
	[("Klikn\u011Bte <a href=\"x\">sem</a> pro odhl\u00E1\u0161en\u00ED odb\u011Bru.", "en")] = "Click <a href=\"x\">here</a> to unsubscribe.",
	[("<p>Prvn\u00ED odstavec.</p><p>Druh\u00FD odstavec s <i>kurz\u00EDvou</i>.</p>", "en")] = "<p>The first paragraph.</p><p>Second paragraph with <i>italics</i>.</p>",
	[("<p>The first paragraph.</p><p>The second paragraph has a <a href=x>link</a>.</p>", "ja")] = "<p>\u6700\u521D\u306E\u6BB5\u843D\u3002</p><p><a href=\"x\"></a>2\u756A\u76EE\u306E\u6BB5\u843D\u306B\u306F\u30EA\u30F3\u30AF\u304C\u3042\u308A\u307E\u3059\u3002</p>",
	[("Two sentences. <b>The second one is bold. And so is the third.</b> The fourth is not.", "ja")] = "2\u6587\u3002<b>2\u3064\u76EE\u306F\u5927\u80C6\u3067\u3059\u3002\u305D\u3057\u3066\u7B2C\u4E09\u3082\u540C\u69D8\u3067\u3042\u308B\u3002</b>\u7B2C\u56DB\u306F\u305D\u3046\u3067\u306F\u306A\u3044\u3002",
	[("Klikn\u011Bte <a href=\"x\">sem</a> pro odhl\u00E1\u0161en\u00ED odb\u011Bru.", "ja")] = "<a href=\"x\"></a>\u3053\u3053\u3092\u30AF\u30EA\u30C3\u30AF\u3057\u3066\u914D\u4FE1\u3092\u89E3\u9664\u3057\u3066\u304F\u3060\u3055\u3044\u3002",
	[("<p>Prvn\u00ED odstavec.</p><p>Druh\u00FD odstavec s <i>kurz\u00EDvou</i>.</p>", "ja")] = "<p>\u6700\u521D\u306E\u6BB5\u843D\u3002</p><p><i></i>\u30A4\u30BF\u30EA\u30A2\u8A9E\u306B\u3088\u308B\u7B2C2\u6BB5\u843D\u3002</p>",
};

var corpus = JsonSerializer.Deserialize<List<Fragment>>(await File.ReadAllTextAsync(args[1]), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
Directory.CreateDirectory(args[2]);
using var httpClient = new HttpClient();
var store = new TranslationModelStore(args[0], httpClient);
var options = new TranslationOptions { Parallelism = TranslationParallelism.None };
using var bergamot = new LocalTranslator(store, options with { MarkupTranslator = BergamotMarkupTranslator.Instance });
using var projected = new LocalTranslator(store, options with { MarkupTranslator = ProjectedMarkupTranslator.Instance });

var results = new List<Result>();
var timings = new List<Timing>();
foreach (var group in corpus.GroupBy(fragment => fragment.Source))
{
	var fragments = group.Select(fragment => fragment.Html).ToArray();
	foreach (var target in targets[group.Key])
	{
		var route = string.Join(" + ", bergamot.FindRoute(group.Key, target) ?? throw new InvalidOperationException($"Download {group.Key}-{target}."));
		var bergamotOutputs = await bergamot.TranslateMarkupAsync(group.Key, target, fragments);
		var projectedOutputs = await projected.TranslateMarkupAsync(group.Key, target, fragments);
		var plainOutputs = await bergamot.TranslateAsync(group.Key, target, fragments.Select(PlainText).ToArray());
		var plainTexts = fragments.Select(PlainText).ToArray();
		var times = await MedianAsync(
			() => bergamot.TranslateMarkupAsync(group.Key, target, fragments),
			() => projected.TranslateMarkupAsync(group.Key, target, fragments),
			() => bergamot.TranslateAsync(group.Key, target, plainTexts));
		timings.Add(new Timing($"{group.Key}-{target}", route, fragments.Length, times[0], times[1], times[2]));
		int i = 0;
		foreach (var fragment in group)
		{
			firefox.TryGetValue((fragment.Html, target), out var reference);
			results.Add(new Result(fragment.Id, group.Key, target, fragment.Html, bergamotOutputs[i], projectedOutputs[i], plainOutputs[i], reference,
				Measure(fragment.Html, bergamotOutputs[i]), Measure(fragment.Html, projectedOutputs[i])));
			i++;
		}
		Console.Error.WriteLine($"{group.Key}-{target}: {group.Count()} fragments");
	}
}

var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
await File.WriteAllTextAsync(Path.Combine(args[2], "results.json"), JsonSerializer.Serialize(new { timings, results }, json));
await File.WriteAllTextAsync(Path.Combine(args[2], "report.md"), Report(results, timings));
Console.WriteLine(Summary(results));
return 0;

// The median time of each translation, run in turns so that they see the same machine state.
static async Task<double[]> MedianAsync(params Func<Task<string[]>>[] translations)
{
	var times = translations.Select(_ => new List<double>()).ToArray();
	for (int run = -2; run < 15; run++)
	{
		for (int i = 0; i < translations.Length; i++)
		{
			var stopwatch = Stopwatch.StartNew();
			await translations[i]();
			if (run >= 0)
				times[i].Add(stopwatch.Elapsed.TotalMilliseconds);
		}
	}
	return times.Select(list => list.Order().ElementAt(list.Count / 2)).ToArray();
}

static string PlainText(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<br\\s*/?>|</p>|</li>|</td>|</h1>", "\n").Let(text => Regex.Replace(text, "<[^>]*>", "")));

static Metrics Measure(string source, string output)
{
	var sourceTags = StartTags(source);
	var outputTags = StartTags(output);
	int missing = 0, extra = 0;
	foreach (var name in sourceTags.Select(tag => tag.Name).Concat(outputTags.Select(tag => tag.Name)).Distinct())
	{
		int difference = sourceTags.Count(tag => tag.Name == name) - outputTags.Count(tag => tag.Name == name);
		if (difference > 0)
			missing += difference;
		else
			extra -= difference;
	}
	int rewritten = outputTags.Count(tag => !source.Contains(tag.Text, StringComparison.Ordinal));
	int empty = Math.Max(0, EmptyInline(output) - EmptyInline(source));
	return new Metrics(WellFormed(output), missing, extra, empty, rewritten);
}

static List<(string Name, string Text)> StartTags(string html) =>
	Regex.Matches(Regex.Replace(html, "<!--.*?-->", "", RegexOptions.Singleline), "<([A-Za-z][A-Za-z0-9]*)\\b[^>]*>")
		.Select(match => (match.Groups[1].Value.ToLowerInvariant(), match.Value)).ToList();

static int EmptyInline(string html) =>
	Regex.Matches(html, "<(a|b|i|u|s|em|strong|span|font|q|abbr|sub|sup|mark|small)\\b[^>]*>\\s*</\\1>", RegexOptions.IgnoreCase).Count;

static bool WellFormed(string html)
{
	string[] voids = ["br", "img", "hr", "input", "meta", "link", "wbr", "col", "area", "source"];
	var stack = new Stack<string>();
	foreach (Match match in Regex.Matches(Regex.Replace(html, "<!--.*?-->", "", RegexOptions.Singleline), "<(/?)([A-Za-z][A-Za-z0-9]*)\\b[^>]*?(/?)>"))
	{
		var name = match.Groups[2].Value.ToLowerInvariant();
		if (match.Groups[1].Value == "/")
		{
			if (stack.Count == 0 || stack.Pop() != name)
				return false;
		}
		else if (match.Groups[3].Value != "/" && !voids.Contains(name))
		{
			stack.Push(name);
		}
	}
	return stack.Count == 0;
}

static string Summary(List<Result> results)
{
	var text = new StringBuilder();
	text.AppendLine("| | Bergamot (Firefox) | Projected |");
	text.AppendLine("| --- | --- | --- |");
	Row("Translations", r => true);
	Row("Well-formed output", r => r.Well);
	Row("All elements kept", r => r.Missing == 0 && r.Extra == 0);
	Row("No new empty inline elements", r => r.Empty == 0);
	Row("Tags copied as written", r => r.Rewritten == 0);
	var withReference = results.Where(r => r.Firefox is not null).ToList();
	text.AppendLine($"| Identical to Firefox (of {withReference.Count}) | {withReference.Count(r => r.Bergamot == r.Firefox)} | {withReference.Count(r => r.Projected == r.Firefox)} |");
	text.AppendLine($"| Same text as plain translation | {results.Count(r => Same(PlainText(r.Bergamot), r.Plain))} | {results.Count(r => Same(PlainText(r.Projected), r.Plain))} |");
	text.AppendLine($"| Same output in both | {results.Count(r => r.Bergamot == r.Projected)} | |");
	return text.ToString();

	void Row(string name, Func<(bool Well, int Missing, int Extra, int Empty, int Rewritten), bool> predicate) =>
		text.AppendLine($"| {name} | {results.Count(r => predicate(r.BergamotMetrics.Tuple))} | {results.Count(r => predicate(r.ProjectedMetrics.Tuple))} |");
}

static bool Same(string a, string b) => Normalize(a) == Normalize(b);

static string Normalize(string text) => Regex.Replace(text, "\\s+", " ").Trim();

static string Report(List<Result> results, List<Timing> timings)
{
	var text = new StringBuilder();
	text.AppendLine("# Markup translation comparison").AppendLine();
	text.AppendLine(Summary(results));
	text.AppendLine("Median of 15 interleaved warm calls, each with all fragments of the direction, on one thread (ms). Plain text is the same text without markup through TranslateAsync:").AppendLine();
	text.AppendLine("| Direction | Models | Fragments | Bergamot | Projected | Plain text |");
	text.AppendLine("| --- | --- | --- | --- | --- | --- |");
	foreach (var timing in timings)
		text.AppendLine($"| {timing.Direction} | {timing.Route} | {timing.Fragments} | {timing.Bergamot:F0} | {timing.Projected:F0} | {timing.Plain:F0} |");
	text.AppendLine();
	foreach (var result in results)
	{
		text.AppendLine($"## {result.Id} {result.Source}\u2192{result.Target}").AppendLine();
		text.AppendLine("```html");
		text.AppendLine(result.Html);
		text.AppendLine("```").AppendLine();
		text.AppendLine(result.Bergamot == result.Projected ? "Both:" : $"Bergamot {Flags(result.BergamotMetrics)}:").AppendLine();
		text.AppendLine("```html").AppendLine(result.Bergamot).AppendLine("```").AppendLine();
		if (result.Bergamot != result.Projected)
			text.AppendLine($"Projected {Flags(result.ProjectedMetrics)}:").AppendLine().AppendLine("```html").AppendLine(result.Projected).AppendLine("```").AppendLine();
		if (result.Firefox is not null)
			text.AppendLine($"Firefox: {(result.Bergamot == result.Firefox ? "identical to Bergamot" : result.Firefox)}").AppendLine();
	}
	return text.ToString();

	static string Flags(Metrics metrics)
	{
		var flags = new List<string>();
		if (!metrics.WellFormed)
			flags.Add("not well-formed");
		if (metrics.Missing > 0)
			flags.Add($"{metrics.Missing} missing");
		if (metrics.Extra > 0)
			flags.Add($"{metrics.Extra} extra");
		if (metrics.EmptyInline > 0)
			flags.Add($"{metrics.EmptyInline} empty");
		if (metrics.Rewritten > 0)
			flags.Add($"{metrics.Rewritten} rewritten");
		return flags.Count == 0 ? "" : $"({string.Join(", ", flags)})";
	}
}

record Fragment(string Id, string Source, string Html);

record Metrics(bool WellFormed, int Missing, int Extra, int EmptyInline, int Rewritten)
{
	public (bool, int, int, int, int) Tuple => (WellFormed, Missing, Extra, EmptyInline, Rewritten);
}

record Result(string Id, string Source, string Target, string Html, string Bergamot, string Projected, string Plain, string? Firefox,
	Metrics BergamotMetrics, Metrics ProjectedMetrics);

record Timing(string Direction, string Route, int Fragments, double Bergamot, double Projected, double Plain);

static class Extensions
{
	public static TResult Let<T, TResult>(this T value, Func<T, TResult> function) => function(value);
}
