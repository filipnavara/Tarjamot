using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Tarjamot;

// Translation benchmark of one build of Tarjamot, see ../README.md.
const string Usage = """
	Usage:
	  TarjamotHarness <bin directory> <from> <to> <input file> <none|within|across> <shortlist 0|1> <html 0|1|2> <warmup seconds> <iterations>

	Loads Tarjamot.dll from the bin directory (for example a copy of
	tools/Translate/bin/Release/net10.0) and the models from the user's local application data.
	Prints the active instruction sets to standard error and one line of key=value results:
	the first translation (including model loading and JIT), then after the warmup the median,
	minimum and maximum of the timed translations, the median process CPU time per translation,
	process-wide allocated bytes per translation, live managed bytes after a full collection with
	the translator idle, and the peak working set after the first translation and at the end.
	HTML 1 places the markup as Firefox does (Tarjamot.BergamotMarkup, built in before it was split off),
	2 with ProjectedMarkupTranslator.
	""";

if (args.Length != 9)
{
	Console.Error.WriteLine(Usage);
	return 1;
}
var binDirectory = Path.GetFullPath(args[0]);
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
	var path = Path.Combine(binDirectory, new AssemblyName(e.Name).Name + ".dll");
	return File.Exists(path) ? Assembly.LoadFrom(path) : null;
};
// The engine is resolved when this method is compiled, after the handler is registered.
return await Benchmark.RunAsync(args);

static class Benchmark
{
	public static async Task<int> RunAsync(string[] args)
	{
		string from = args[1], to = args[2];
		var text = await File.ReadAllTextAsync(args[3]);
		var parallelism = args[4] switch
		{
			"none" => TranslationParallelism.None,
			"across" => TranslationParallelism.AcrossSentences,
			_ => TranslationParallelism.WithinProducts,
		};
		bool shortlist = args[5] == "1", html = args[6] is "1" or "2";
		double warmupSeconds = double.Parse(args[7]);
		int iterations = int.Parse(args[8]);
		Console.Error.WriteLine($"Avx2={Avx2.IsSupported} AvxVnni={AvxVnni.IsSupported} Avx512BW={Avx512BW.IsSupported} V512={Vector512.IsHardwareAccelerated} dll={typeof(LocalTranslator).Assembly.Location}");

		var models = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tarjamot", "Models");
		using var httpClient = new HttpClient();
		var store = new TranslationModelStore(models, httpClient);
		var options = new TranslationOptions { UseShortlist = shortlist, Parallelism = parallelism };
		if (args[6] == "2")
			options = Markup.Projected(options);
		else if (args[6] == "1" && File.Exists(Path.Combine(args[0], "Tarjamot.BergamotMarkup.dll")))
			options = Markup.Bergamot(options);
		using var translator = new LocalTranslator(store, options);
		string[] fragments = [text];
		Task<string[]> TranslateAsync() => html ? translator.TranslateMarkupAsync(from, to, fragments) : translator.TranslateAsync(from, to, fragments);

		var process = Process.GetCurrentProcess();
		var stopwatch = Stopwatch.StartNew();
		await TranslateAsync();
		double cold = stopwatch.Elapsed.TotalMilliseconds;
		process.Refresh();
		long peakAfterCold = process.PeakWorkingSet64;

		stopwatch.Restart();
		int warmupRuns = 0;
		while (stopwatch.Elapsed.TotalSeconds < warmupSeconds)
		{
			await TranslateAsync();
			warmupRuns++;
		}

		var times = new List<double>();
		var cpuTimes = new List<double>();
		long allocated = GC.GetTotalAllocatedBytes(true);
		for (int i = 0; i < iterations; i++)
		{
			var cpu = process.TotalProcessorTime;
			stopwatch.Restart();
			await TranslateAsync();
			times.Add(stopwatch.Elapsed.TotalMilliseconds);
			process.Refresh();
			cpuTimes.Add((process.TotalProcessorTime - cpu).TotalMilliseconds);
		}
		long allocatedPerRun = (GC.GetTotalAllocatedBytes(true) - allocated) / iterations;
		await Task.Delay(200);
		long live = GC.GetTotalMemory(true);
		process.Refresh();
		times.Sort();
		cpuTimes.Sort();
		Console.WriteLine($"cold_ms={cold:F1} warm_runs={warmupRuns} median_ms={times[times.Count / 2]:F2} min_ms={times[0]:F2} max_ms={times[^1]:F2} median_cpu_ms={cpuTimes[cpuTimes.Count / 2]:F1} alloc_per_run={allocatedPerRun} live_bytes={live} peak_ws_cold={peakAfterCold} peak_ws={process.PeakWorkingSet64}");
		return 0;
	}
}

// Separate methods, so that builds from before ProjectedMarkupTranslator and Tarjamot.BergamotMarkup
// existed resolve these types only when they are asked for.
static class Markup
{
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static TranslationOptions Bergamot(TranslationOptions options) => options with { MarkupTranslator = BergamotMarkupTranslator.Instance };

	[MethodImpl(MethodImplOptions.NoInlining)]
	public static TranslationOptions Projected(TranslationOptions options) => options with { MarkupTranslator = ProjectedMarkupTranslator.Instance };
}
