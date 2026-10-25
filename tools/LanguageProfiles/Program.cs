using System.Diagnostics;
using Tarjamot.LanguageIdentification;
using Tarjamot.LanguageProfiles;

// Builds language identification profiles for Tarjamot.LanguageIdentification from Wikipedia text.
//
//   LanguageProfiles download <corpus> <language>... [--characters N] [--parallel N]
//   LanguageProfiles build <corpus> <profile.dat> [--max-ngram-length N] [--profile-size N] [--characters N]
//   LanguageProfiles languages

const string usage = """
	Usage:
	  LanguageProfiles download <corpus> <language>... [--characters N] [--parallel N]
	      Downloads random Wikipedia articles (at most 10,000 characters of each) until <corpus>/<language>.txt has N
	      characters (default 500000; existing files are extended). <language> is a tag such as "de",
	      a known tag such as "zh-Hant" (see "languages"), or "tag=wiki" / "tag=wiki:variant".
	  LanguageProfiles build <corpus> <profile.dat> [--max-ngram-length N] [--profile-size N] [--characters N]
	      Counts the n-grams of every <corpus>/*.txt (the file name is the language tag), optionally of
	      the first N characters of each, and writes the binary profile (defaults: 3, 2500).
	  LanguageProfiles languages
	      Lists the tags that come from another wiki or from a script variant.
	""";

if (args.Length == 0)
	return Fail(null);

try
{
	return args[0] switch
	{
		"download" when args.Length >= 3 => await DownloadAsync(args[1], args[2..]),
		"build" when args.Length >= 3 => Build(args[1], args[2], args[3..]),
		"languages" => ListLanguages(),
		_ => Fail(null),
	};
}
catch (Exception exception) when (exception is ArgumentException or FormatException or IOException or HttpRequestException or InvalidOperationException)
{
	return Fail(exception.Message);
}

static async Task<int> DownloadAsync(string corpus, string[] arguments)
{
	var (languages, options) = ParseOptions(arguments, "--characters", "--parallel");
	if (languages.Count == 0)
		return Fail("No languages given.");

	using var httpClient = new HttpClient();
	// Wikimedia's API etiquette asks for a descriptive User-Agent with a contact.
	httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Tarjamot-LanguageProfiles/1.0 (https://github.com/filipnavara/Tarjamot)");
	httpClient.Timeout = TimeSpan.FromMinutes(2);

	using var cancellation = new CancellationTokenSource();
	Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

	var downloader = new WikipediaCorpus(httpClient, corpus, Console.Error);
	await downloader.DownloadAsync(
		languages.Select(CorpusLanguage.Parse).ToList(),
		options.GetValueOrDefault("--characters", 500_000),
		(int)options.GetValueOrDefault("--parallel", 4),
		cancellation.Token);
	return 0;
}

static int Build(string corpus, string output, string[] arguments)
{
	var (rest, options) = ParseOptions(arguments, "--max-ngram-length", "--profile-size", "--characters");
	if (rest.Count > 0)
		return Fail($"Unexpected argument {rest[0]}.");

	var files = Directory.GetFiles(corpus, "*.txt").Order(StringComparer.Ordinal).ToList();
	if (files.Count == 0)
		return Fail($"No *.txt files in {corpus}.");

	var builder = new LanguageProfileBuilder(
		(int)options.GetValueOrDefault("--max-ngram-length", 3),
		(int)options.GetValueOrDefault("--profile-size", 2500));
	var characters = options.GetValueOrDefault("--characters", long.MaxValue);
	var stopwatch = Stopwatch.StartNew();
	foreach (var file in files)
	{
		var language = Path.GetFileNameWithoutExtension(file);
		using var reader = new StreamReader(file);
		var text = characters == long.MaxValue ? reader.ReadToEnd() : ReadPrefix(reader, characters);
		builder.Add(language, text);
		Console.Error.WriteLine($"{language}: {text.Length:N0} characters");
	}

	using (var stream = File.Create(output))
		builder.Write(stream);

	using (var stream = File.OpenRead(output))
	{
		var identifier = LanguageIdentifier.Load(stream);
		Console.Error.WriteLine($"Wrote {output}: {identifier.Languages.Count} languages, {new FileInfo(output).Length / 1024} KB, " +
			$"{identifier.TableSize / 1024} KB of tables in memory, in {stopwatch.Elapsed.TotalSeconds:F1} s");
	}
	return 0;

	static string ReadPrefix(StreamReader reader, long characters)
	{
		var buffer = new char[(int)Math.Min(characters, int.MaxValue)];
		var read = reader.ReadBlock(buffer, 0, buffer.Length);
		return new string(buffer, 0, read);
	}
}

static int ListLanguages()
{
	foreach (var (tag, (wiki, variant)) in CorpusLanguage.KnownLanguages)
		Console.WriteLine($"{tag,-8} {wiki}.wikipedia.org{(variant is null ? "" : ", variant " + variant)}");
	return 0;
}

static (List<string> Arguments, Dictionary<string, long> Options) ParseOptions(string[] arguments, params string[] names)
{
	var rest = new List<string>();
	var options = new Dictionary<string, long>();
	for (var i = 0; i < arguments.Length; i++)
	{
		if (!arguments[i].StartsWith("--", StringComparison.Ordinal))
		{
			rest.Add(arguments[i]);
			continue;
		}
		if (!names.Contains(arguments[i]) || i + 1 == arguments.Length)
			throw new ArgumentException($"Unknown option or missing value: {arguments[i]}");
		options[arguments[i]] = long.Parse(arguments[++i]);
	}
	return (rest, options);
}

static int Fail(string? message)
{
	if (message is not null)
		Console.Error.WriteLine(message);
	Console.Error.WriteLine(usage);
	return 1;
}
