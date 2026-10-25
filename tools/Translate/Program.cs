using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Tarjamot;

// Command-line front end for Tarjamot: local translation with the
// Firefox Translations models.
const string Usage = """
	Usage:
	  Translate languages [--mobile]
	      Lists the language pairs published by Mozilla.
	  Translate download <from> <to> [--shortlist] [--mobile]
	      Downloads the models for a direction (two models when pivoting through English).
	  Translate installed
	      Lists the downloaded language pairs.
	  Translate <from> <to> [text...] [--input <file>] [--html | --html-json] [--projected] [--shortlist] [--single-thread] [--across-sentences] [--no-split] [--benchmark <runs>]
	      Translates the text, the input file, or standard input when neither is given (UTF-8).
	      Models must be downloaded.

	Options:
	  --models <directory>   Model directory (default: the user's local application data).
	  --input <file>         Read the text from a UTF-8 file.
	  --shortlist            Restrict the vocabulary with the lexical shortlist (faster, Firefox does not).
	  --single-thread        Run the matrix products on one thread.
	  --across-sentences     Translate batches of sentences on all threads at once (faster, more CPU).
	  --no-split             Translate each input line as one sentence.
	  --html                 The text is an HTML fragment; its markup is kept around the translated words.
	  --html-json            The text is a JSON array of HTML fragments; prints a JSON array of translations.
	  --projected            Place the markup with ProjectedMarkupTranslator instead of Firefox's
	                         algorithm (BergamotMarkupTranslator from Tarjamot.BergamotMarkup).
	  --benchmark <runs>     Translate repeatedly and report the speed.
	  --mobile               Select the models published for Android.

	The portable int8 kernel runs with DOTNET_EnableArm64Dp=0 (ARM) or DOTNET_EnableAVX2=0 (x86).
	""";

// Windows consoles default to the OEM code page, which garbles redirected input and output.
Console.InputEncoding = Console.OutputEncoding = new UTF8Encoding(false);

var arguments = args.ToList();
var inputFile = TakeOption(arguments, "--input");
var modelsDirectory = TakeOption(arguments, "--models") ?? Path.Combine(
	Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tarjamot", "Models");
var benchmarkRuns = int.Parse(TakeOption(arguments, "--benchmark") ?? "0");
bool useShortlist = TakeFlag(arguments, "--shortlist");
bool singleThread = TakeFlag(arguments, "--single-thread");
bool acrossSentences = TakeFlag(arguments, "--across-sentences");
bool noSplit = TakeFlag(arguments, "--no-split");
bool html = TakeFlag(arguments, "--html");
bool htmlJson = TakeFlag(arguments, "--html-json");
bool projected = TakeFlag(arguments, "--projected");
var platform = TakeFlag(arguments, "--mobile") ? TranslationPlatform.Mobile : TranslationPlatform.Desktop;

using var httpClient = new HttpClient();
var store = new TranslationModelStore(modelsDirectory, httpClient);

try
{
	switch (arguments.ToArray())
	{
		case ["languages"]:
			await ListLanguagesAsync();
			return 0;
		case ["installed"]:
			foreach (var pair in store.GetInstalledPairs().OrderBy(pair => pair.ToString()))
				Console.WriteLine(pair);
			return 0;
		case ["download", var from, var to]:
			await DownloadAsync(from, to);
			return 0;
		case [var from, var to, .. var text] when !from.StartsWith('-'):
			await TranslateAsync(from, to, text.Length > 0 ? string.Join(' ', text)
				: inputFile != null ? await File.ReadAllTextAsync(inputFile)
				: await Console.In.ReadToEndAsync());
			return 0;
		default:
			Console.Error.WriteLine(Usage);
			return 1;
	}
}
catch (Exception e) when (e is NotSupportedException or HttpRequestException or InvalidDataException or KeyNotFoundException)
{
	Console.Error.WriteLine(e.Message);
	return 2;
}

async Task ListLanguagesAsync()
{
	var catalog = await TranslationModelCatalog.LoadAsync(httpClient, platform);
	foreach (var pair in catalog.LanguagePairs.OrderBy(pair => pair.ToString()))
	{
		var files = catalog.GetFiles(pair);
		var model = files.Single(file => file.FileType == "model");
		Console.WriteLine($"{pair,-12} {model.Version,-6} {model.Architecture,-12} {files.Sum(file => file.Size) / 1048576.0,6:F1} MB");
	}
}

async Task DownloadAsync(string from, string to)
{
	var catalog = await TranslationModelCatalog.LoadAsync(httpClient, platform);
	var route = catalog.FindRoute(from, to) ?? throw new NotSupportedException($"Mozilla publishes no models from {from} to {to}.");
	foreach (var pair in route)
	{
		var files = catalog.GetFiles(pair, includeShortlist: useShortlist);
		if (store.IsInstalled(pair, files))
		{
			Console.Error.WriteLine($"{pair}: up to date");
			continue;
		}
		long total = files.Sum(file => file.Size);
		var stopwatch = Stopwatch.StartNew();
		var progress = new Progress<long>(received => Console.Error.Write($"\r{pair}: {received * 100 / total,3}% of {total / 1048576.0:F1} MB"));
		var directory = await store.InstallAsync(pair, files, progress);
		Console.Error.WriteLine($"\r{pair}: installed in {stopwatch.Elapsed.TotalSeconds:F1} s to {directory}");
	}
}

async Task TranslateAsync(string from, string to, string text)
{
	var options = new TranslationOptions
	{
		UseShortlist = useShortlist,
		Parallelism = singleThread ? TranslationParallelism.None : acrossSentences ? TranslationParallelism.AcrossSentences : TranslationParallelism.WithinProducts,
		SplitSentences = !noSplit,
		MarkupTranslator = projected ? ProjectedMarkupTranslator.Instance : BergamotMarkupTranslator.Instance,
	};
	using var translator = new LocalTranslator(store, options);
	if (translator.FindRoute(from, to) is not { } route)
		throw new NotSupportedException($"The models from {from} to {to} are not downloaded; run: Translate download {from} {to}");

	var fragments = htmlJson ? JsonSerializer.Deserialize<string[]>(text) ?? [] : [text];
	var stopwatch = Stopwatch.StartNew();
	var result = await TranslateOnceAsync();
	Console.Error.WriteLine($"Translated with {string.Join(" + ", route)} in {stopwatch.ElapsedMilliseconds} ms (including model loading)");
	Console.WriteLine(htmlJson ? JsonSerializer.Serialize(result, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true }) : result[0]);

	if (benchmarkRuns <= 0)
		return;
	var cpu = Process.GetCurrentProcess().TotalProcessorTime;
	stopwatch.Restart();
	for (int run = 0; run < benchmarkRuns; run++)
		await TranslateOnceAsync();
	double milliseconds = stopwatch.Elapsed.TotalMilliseconds / benchmarkRuns;
	double cpuMilliseconds = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds / benchmarkRuns;
	int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
	int characters = text.Count(c => !char.IsWhiteSpace(c));
	Console.Error.WriteLine($"Warm: {milliseconds:F0} ms per run ({cpuMilliseconds:F0} ms CPU), {words / milliseconds * 1000:F0} source words/s, {characters / milliseconds * 1000:F0} source characters/s");

	Task<string[]> TranslateOnceAsync() => html || htmlJson
		? translator.TranslateMarkupAsync(from, to, fragments)
		: translator.TranslateAsync(from, to, fragments);
}

static string? TakeOption(List<string> arguments, string name)
{
	int index = arguments.IndexOf(name);
	if (index < 0 || index + 1 >= arguments.Count)
		return null;
	var value = arguments[index + 1];
	arguments.RemoveRange(index, 2);
	return value;
}

static bool TakeFlag(List<string> arguments, string name) => arguments.Remove(name);
