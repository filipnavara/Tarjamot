using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Tarjamot.Engine;

// Single-thread microbenchmarks of Tarjamot internals, see ../README.md.
const string Usage = """
	Usage:
	  TarjamotMicrobenchmark <bin directory> gemm [seconds]   int8 products and argmax on the model shapes (GMAC/s)
	  TarjamotMicrobenchmark <bin directory> load <model>     model loading: first and later loads, live bytes
	  TarjamotMicrobenchmark <bin directory> output <model>   output-layer conversion of the embeddings, per call
	  TarjamotMicrobenchmark <bin directory> sizes <model>    weight, row-sum and saturation metadata sizes
	  TarjamotMicrobenchmark <bin directory> values           attention values: one head versus two at a time
	  TarjamotMicrobenchmark <bin directory> scores           attention scores: one query versus two at a time
	  TarjamotMicrobenchmark <bin directory> cores            Environment.ProcessorCount and the physical cores

	Loads Tarjamot.dll from the bin directory. A model is a
	model.*.intgemm.alphas.bin file. Run under start /affinity <mask> to check the core count.
	""";

if (args.Length < 2)
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
switch (args[1..])
{
	case ["gemm"]:
		Benchmarks.Gemm(0.5);
		break;
	case ["gemm", var seconds]:
		Benchmarks.Gemm(double.Parse(seconds));
		break;
	case ["load", var model]:
		Benchmarks.Load(model);
		break;
	case ["output", var model]:
		Benchmarks.OutputWeights(model);
		break;
	case ["sizes", var model]:
		Benchmarks.Sizes(model);
		break;
	case ["values"]:
		Benchmarks.AttentionValues();
		break;
	case ["scores"]:
		Benchmarks.AttentionScores();
		break;
	case ["cores"]:
		Benchmarks.Cores();
		break;
	default:
		Console.Error.WriteLine(Usage);
		return 1;
}
return 0;

static class Benchmarks
{
	// The shapes of the cs-en (384/1,536/32,000) and zh-Hans-en (512/2,048/64,000) models.
	public static void Gemm(double seconds)
	{
		Console.Error.WriteLine($"Avx512BW={Avx512BW.IsSupported} V512={Vector512.IsHardwareAccelerated} AvxVnni={AvxVnni.IsSupported} kernel={Int8Gemm.DefaultKernel}");
		(string Name, int Width, int Outputs, bool ArgMax)[] shapes =
		[
			("cs ffn1", 384, 1536, false), ("cs ffn2", 1536, 384, false), ("cs attn", 384, 384, false), ("cs vocab", 384, 32000, true),
			("zh ffn1", 512, 2048, false), ("zh ffn2", 2048, 512, false), ("zh vocab", 512, 64000, true),
		];
		var kernel = Int8Gemm.DefaultKernel;
		foreach (var (name, width, outputs, argMax) in shapes)
		{
			var random = new Random(width * 7 + outputs);
			// Weights in [-64, 63], so that no pair can saturate (|w0 + w1| < 130), as nearly none do in the
			// models (1-367 pairs per matrix). With uniform int8 weights about a quarter of the pairs could,
			// and the exact-sum kernel (VPDPBUSD) would spend its time correcting them.
			var weights = new sbyte[(long)outputs * width];
			for (long i = 0; i < weights.LongLength; i++)
				weights[i] = (sbyte)random.Next(-64, 64);
			var matrix = QuantizedMatrix.Create(weights, outputs, width, 40f, 0f).Pack(Int8Gemm.PanelWidth(kernel));
			var prepared = Int8Gemm.PrepareBias(matrix, 100f, null);
			foreach (int rows in new[] { 1, 4, 6, 16, 42, 300 })
			{
				if (argMax && rows > 64)
					continue;
				var x = Enumerable.Range(0, rows * width).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
				var activations = new QuantizedActivations();
				Int8Gemm.Quantize(x, 100f, activations, kernel);
				var y = new float[rows * outputs];
				var scratch = new ArgMaxScratch();
				double nanoseconds = Time(() =>
				{
					if (argMax)
						Int8Gemm.MultiplyArgMax(activations, rows, matrix, 1f / 4000f, prepared, 1, scratch, kernel, false);
					else
						Int8Gemm.Multiply(activations, rows, matrix, null, 1f / 4000f, prepared, y, kernel, false);
				}, 0.3, seconds);
				Console.WriteLine($"{name,-9} w={width,4} o={outputs,5} rows={rows,3}: {(double)rows * width * outputs / nanoseconds,6:F1} GMAC/s");
			}
		}
	}

	// MarianModel plus MarianWeights, seven times; the first load includes JIT, as in a new process.
	public static void Load(string modelFile)
	{
		var kernel = Int8Gemm.DefaultKernel;
		var times = new List<double>();
		object? weights = null;
		long before = GC.GetTotalMemory(true);
		for (int i = 0; i < 7; i++)
		{
			weights = null;
			GC.Collect();
			var stopwatch = Stopwatch.StartNew();
			using var stream = File.OpenRead(modelFile);
			weights = new MarianWeights(new MarianModel(stream, kernel), kernel, false);
			times.Add(stopwatch.Elapsed.TotalMilliseconds);
		}
		long live = GC.GetTotalMemory(true) - before;
		GC.KeepAlive(weights);
		Console.WriteLine($"kernel={kernel} first_ms={times[0]:F1} median_rest_ms={times.Skip(1).Order().ElementAt(3):F1} live_model_bytes={live}");
	}

	public static void OutputWeights(string modelFile)
	{
		var kernel = Int8Gemm.DefaultKernel;
		using var stream = File.OpenRead(modelFile);
		var model = new MarianModel(stream, kernel);
		var embeddings = model.Matrix(model.HasMatrix("Wemb") ? "Wemb" : "decoder_Wemb");
		var times = new List<double>();
		for (int i = 0; i < 8; i++)
		{
			var stopwatch = Stopwatch.StartNew();
			GC.KeepAlive(MarianWeights.CreateOutputWeights(embeddings, 1f, Int8Gemm.PanelWidth(kernel), Int8Gemm.UsesSaturationMetadata(kernel)));
			times.Add(stopwatch.Elapsed.TotalMilliseconds);
		}
		Console.WriteLine("output ms: " + string.Join(", ", times.Select(time => time.ToString("F1"))));
	}

	// Reads MarianModel's private matrix dictionary, so it breaks when that field is renamed.
	public static void Sizes(string modelFile)
	{
		var kernel = Int8Gemm.DefaultKernel;
		using var stream = File.OpenRead(modelFile);
		var model = new MarianModel(stream, kernel);
		var weights = new MarianWeights(model, kernel, false);
		var field = typeof(MarianModel).GetField("matrices", BindingFlags.NonPublic | BindingFlags.Instance)
			?? throw new MissingFieldException(nameof(MarianModel), "matrices");
		var matrices = ((IDictionary<string, QuantizedMatrix>)field.GetValue(model)!).ToList();
		matrices.Add(new("(output)", weights.OutputWeights));
		long data = 0, rowSums = 0, offsets = 0, pairs = 0;
		foreach (var (name, matrix) in matrices)
		{
			data += matrix.Data.Length;
			rowSums += 4L * matrix.RowSums.Length;
			offsets += 4L * matrix.SaturationOffsets.Length;
			pairs += 4L * matrix.SaturationPairs.Length;
			if (name.Contains("Wemb") || name == "(output)" || name.Contains("l1_ffn_W1"))
				Console.WriteLine($"{name,-24} {matrix.Rows}x{matrix.Cols} packed={matrix.PanelWidth} data={matrix.Data.Length} rowSums={4L * matrix.RowSums.Length} offsets={4L * matrix.SaturationOffsets.Length} pairs={matrix.SaturationPairs.Length}");
		}
		Console.WriteLine($"kernel={kernel} matrices={matrices.Count} data={data} rowSums={rowSums} offsets={offsets} pairBytes={pairs}");
	}

	public static void AttentionValues()
	{
		foreach (int headDim in new[] { 48, 64 })
		foreach (int keys in new[] { 12, 30, 60 })
		{
			int dim = 8 * headDim;
			var random = new Random(5);
			var values = Enumerable.Range(0, keys * dim).Select(_ => (float)random.NextDouble()).ToArray();
			var scores0 = Enumerable.Range(0, keys).Select(_ => (float)random.NextDouble()).ToArray();
			var scores1 = Enumerable.Range(0, keys).Select(_ => (float)random.NextDouble()).ToArray();
			var output = new float[dim];
			double single = Time(() =>
			{
				for (int h = 0; h < 8; h += 2)
				{
					MarianMath.AttentionValues(scores0, values.AsSpan(h * headDim), dim, output.AsSpan(h * headDim, headDim));
					MarianMath.AttentionValues(scores1, values.AsSpan((h + 1) * headDim), dim, output.AsSpan((h + 1) * headDim, headDim));
				}
			});
			if (!MarianMath.SupportsAttentionValuesPair(headDim))
			{
				Console.WriteLine($"values headDim={headDim} keys={keys}: one head at a time {single:F0} ns, two heads unsupported here");
				continue;
			}
			double pair = Time(() =>
			{
				for (int h = 0; h < 8; h += 2)
					MarianMath.AttentionValuesPair(scores0, scores1, values.AsSpan(h * headDim), dim, headDim, output.AsSpan(h * headDim, 2 * headDim));
			});
			Console.WriteLine($"values headDim={headDim} keys={keys}: one head at a time {single:F0} ns, two heads {pair:F0} ns per query (8 heads)");
		}
	}

	// The encoder's queries of a sentence share its keys; ScoresTwoQueries is a prototype of scoring
	// two of them per pass (see "Encoder attention for several queries" in X86-PERFORMANCE.md).
	public static void AttentionScores()
	{
		if (!Vector512.IsHardwareAccelerated)
		{
			Console.Error.WriteLine("The prototype needs Vector512.");
			return;
		}
		foreach (int headDim in new[] { 48, 64 })
		foreach (int keys in new[] { 32, 64 })
		{
			int dim = 8 * headDim;
			var random = new Random(1);
			var q = Enumerable.Range(0, 2 * dim).Select(_ => (float)random.NextDouble()).ToArray();
			var k = Enumerable.Range(0, dim * keys).Select(_ => (float)random.NextDouble()).ToArray();
			var scores0 = new float[2 * keys];
			var scores1 = new float[2 * keys];
			const float scale = 0.125f;
			double one = Time(() =>
			{
				for (int query = 0; query < 2; query++)
				{
					for (int h = 0; h < 8; h += 2)
						MarianMath.AttentionScoresPair(q.AsSpan(query * dim + h * headDim, 2 * headDim), k.AsSpan(h * headDim * keys), headDim, keys, scale, scores0);
				}
			});
			double two = Time(() =>
			{
				for (int h = 0; h < 8; h += 2)
					ScoresTwoQueries(q.AsSpan(h * headDim, 2 * headDim), q.AsSpan(dim + h * headDim, 2 * headDim), k.AsSpan(h * headDim * keys), headDim, keys, scale, scores0, scores1);
			});
			Console.WriteLine($"scores headDim={headDim} keys={keys}: one query at a time {one / 2:F0} ns, two queries {two / 2:F0} ns per query (8 heads)");
		}
	}

	// Two queries x two heads, blocks of 32 keys only (padded must be a multiple of 32).
	static void ScoresTwoQueries(ReadOnlySpan<float> queryA, ReadOnlySpan<float> queryB, ReadOnlySpan<float> keys, int headDim, int padded, float scale, Span<float> scoresA, Span<float> scoresB)
	{
		ref float a0 = ref MemoryMarshal.GetReference(queryA);
		ref float a1 = ref Unsafe.Add(ref a0, headDim);
		ref float b0 = ref MemoryMarshal.GetReference(queryB);
		ref float b1 = ref Unsafe.Add(ref b0, headDim);
		ref float k0 = ref MemoryMarshal.GetReference(keys);
		ref float k1 = ref Unsafe.Add(ref k0, headDim * padded);
		var scale512 = Vector512.Create(scale);
		for (int j = 0; j + 32 <= padded; j += 32)
		{
			Vector512<float> p0 = default, p1 = default, p2 = default, p3 = default;
			Vector512<float> r0 = default, r1 = default, r2 = default, r3 = default;
			ref float x = ref Unsafe.Add(ref k0, j);
			ref float y = ref Unsafe.Add(ref k1, j);
			for (int d = 0; d < headDim; d++)
			{
				var x0 = Vector512.LoadUnsafe(ref x);
				var x1 = Vector512.LoadUnsafe(ref x, 16);
				var y0 = Vector512.LoadUnsafe(ref y);
				var y1 = Vector512.LoadUnsafe(ref y, 16);
				var qa0 = Vector512.Create(Unsafe.Add(ref a0, d));
				var qa1 = Vector512.Create(Unsafe.Add(ref a1, d));
				var qb0 = Vector512.Create(Unsafe.Add(ref b0, d));
				var qb1 = Vector512.Create(Unsafe.Add(ref b1, d));
				p0 = p0 + qa0 * x0;
				p1 = p1 + qa0 * x1;
				p2 = p2 + qa1 * y0;
				p3 = p3 + qa1 * y1;
				r0 = r0 + qb0 * x0;
				r1 = r1 + qb0 * x1;
				r2 = r2 + qb1 * y0;
				r3 = r3 + qb1 * y1;
				x = ref Unsafe.Add(ref x, padded);
				y = ref Unsafe.Add(ref y, padded);
			}
			(scale512 * p0).CopyTo(scoresA[j..]);
			(scale512 * p1).CopyTo(scoresA[(j + 16)..]);
			(scale512 * p2).CopyTo(scoresA[(padded + j)..]);
			(scale512 * p3).CopyTo(scoresA[(padded + j + 16)..]);
			(scale512 * r0).CopyTo(scoresB[j..]);
			(scale512 * r1).CopyTo(scoresB[(j + 16)..]);
			(scale512 * r2).CopyTo(scoresB[(padded + j)..]);
			(scale512 * r3).CopyTo(scoresB[(padded + j + 16)..]);
		}
	}

	public static void Cores() => Console.WriteLine($"ProcessorCount={Environment.ProcessorCount} PhysicalCores={ProcessorTopology.PhysicalCores}");

	// Nanoseconds per call after a warmup into tier 1.
	static double Time(Action action, double warmupSeconds = 0.2, double seconds = 0.4)
	{
		var stopwatch = Stopwatch.StartNew();
		while (stopwatch.Elapsed.TotalSeconds < warmupSeconds)
			action();
		long calls = 0;
		stopwatch.Restart();
		while (stopwatch.Elapsed.TotalSeconds < seconds)
		{
			action();
			calls++;
		}
		return stopwatch.Elapsed.TotalMilliseconds * 1e6 / calls;
	}
}
