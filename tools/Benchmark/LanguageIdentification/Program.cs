using System.Diagnostics;
using Tarjamot.LanguageIdentification;

// Load time, allocations and retained memory of the language identification with the embedded Wikipedia profile.
const string Usage = """
	Usage:
	  LanguageIdentificationBenchmark load               loading the embedded tables: time, allocated and retained bytes
	  LanguageIdentificationBenchmark load <profile.dat> loading a profile in eM Client's format from a file
	  LanguageIdentificationBenchmark identify [profile.dat]
	                                                     Identify on short and long texts, returning an array and
	                                                     writing to a span: time and allocated bytes per call
	""";

switch (args)
{
	case ["load"]:
		Benchmarks.Load(() => WikipediaLanguageProfile.Load());
		break;
	case ["load", var profile]:
		var bytes = File.ReadAllBytes(profile);
		Benchmarks.Load(() => LanguageIdentifier.Load(new MemoryStream(bytes)));
		break;
	case ["identify"]:
		Benchmarks.Identify(WikipediaLanguageProfile.Load());
		break;
	case ["identify", var profile]:
		using (var stream = File.OpenRead(profile))
			Benchmarks.Identify(LanguageIdentifier.Load(stream));
		break;
	default:
		Console.Error.WriteLine(Usage);
		return 1;
}
return 0;

static class Benchmarks
{
	private static readonly (string Name, string Text)[] Texts =
	[
		("en", "Thank you for your message, I will reply as soon as possible."),
		("de", "Vielen Dank f\u00FCr Ihre Nachricht, ich antworte so schnell wie m\u00F6glich."),
		("cs", "D\u011Bkuji za zpr\u00E1vu, odpov\u00EDm co nejd\u0159\u00EDve."),
		("ja", "\u30E1\u30C3\u30BB\u30FC\u30B8\u3092\u3042\u308A\u304C\u3068\u3046\u3054\u3056\u3044\u307E\u3059\u3002\u3067\u304D\u308B\u3060\u3051\u65E9\u304F\u8FD4\u4FE1\u3057\u307E\u3059\u3002"),
		("ko", "\uBA54\uC2DC\uC9C0 \uAC10\uC0AC\uD569\uB2C8\uB2E4. \uCD5C\uB300\uD55C \uBE68\uB9AC \uB2F5\uC7A5\uD558\uACA0\uC2B5\uB2C8\uB2E4."),
		("en-long", string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog while the committee discusses the annual report. ", 40))),
	];

	public static void Load(Func<LanguageIdentifier> load)
	{
		var times = new List<double>();
		LanguageIdentifier? identifier = null;
		long allocated = 0;
		long before = GC.GetTotalMemory(true);
		for (int i = 0; i < 21; i++)
		{
			identifier = null;
			GC.Collect();
			long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
			var stopwatch = Stopwatch.StartNew();
			identifier = load();
			times.Add(stopwatch.Elapsed.TotalMilliseconds);
			allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
		}
		long live = GC.GetTotalMemory(true) - before;
		GC.KeepAlive(identifier);
		Console.WriteLine($"first_ms={times[0]:F3} median_rest_ms={Median(times.Skip(1)):F3} allocated_bytes={allocated} " +
			$"live_bytes={live} table_bytes={identifier!.TableSize}");
	}

	public static void Identify(LanguageIdentifier identifier)
	{
		var destination = new (int Language, double Score)[identifier.Languages.Count];
		foreach (var (name, text) in Texts)
		{
			Measure(name, "array", text, () => identifier.Identify(text));
			Measure(name, "span", text, () => identifier.Identify(text, destination));
		}

		static void Measure(string name, string kind, string text, Action identify)
		{
			for (int i = 0; i < 1000; i++)
				identify();

			int iterations = Math.Max(1000, 2_000_000 / text.Length);
			long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
			var stopwatch = Stopwatch.StartNew();
			for (int i = 0; i < iterations; i++)
				identify();
			double microseconds = stopwatch.Elapsed.TotalMicroseconds / iterations;
			long allocated = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / iterations;
			Console.WriteLine($"{name,-8} {kind,-5} chars={text.Length,5} us_per_call={microseconds,8:F3} allocated_bytes_per_call={allocated}");
		}
	}

	private static double Median(IEnumerable<double> values)
	{
		var sorted = values.Order().ToList();
		return sorted[sorted.Count / 2];
	}
}
