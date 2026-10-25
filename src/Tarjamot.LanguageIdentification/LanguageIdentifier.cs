using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Tarjamot.LanguageIdentification;

/// <summary>
/// Naive Bayes language identifier over character n-grams. It reads language profiles in eM Client's binary
/// profile format and scores like NTextCat's naive Bayes identifier with add-one smoothing.
/// </summary>
/// <remarks>
/// N-grams are packed into <see cref="ulong"/> keys (16 bits per character) stored in one sorted array shared by all
/// languages. Each key points to a run of (language, weight) postings where the weight is log(count + 1) quantized
/// to a byte. With add-one smoothing the naive Bayes score of a language decomposes into
/// <c>bias - featureCount * penalty + sum(log(count + 1))</c>, so only n-grams present in a language contribute and
/// the sum can be accumulated in integers.
///
/// Before scoring, the input's Unicode scripts restrict the candidate languages. Languages that are the only ones
/// written in their script (Greek, Hebrew, Thai, ...) are decided by the script alone and their n-grams are dropped.
///
/// All of it is kept in one block of memory in the format of <see cref="WriteTables"/>, which
/// <see cref="LoadTables"/> uses in place, so tables embedded in an assembly are not copied to the heap.
/// </remarks>
public sealed class LanguageIdentifier
{
	private const double PrimaryScriptShare = 0.2;
	private const double MinimumCoverage = 0.5;

	// The tables: a header, the per-language values, the n-gram tables and the language codes, little-endian. The
	// offsets are multiples of 8 within the tables, but the tables themselves may start anywhere, so every value is
	// read unaligned.
	private const uint TablesMagic = 0x494C4A54; // "TJLI"
	private const int TablesVersion = 1;
	private const int HeaderSize = 40;

	private readonly ReadOnlyMemory<byte> tables;
	private readonly TablesLayout layout;
	private readonly string[] languages;
	private readonly double weightStep;

	public int MaxNGramLength { get; }

	/// <summary>
	/// Maximum number of n-grams kept in the profile of the analyzed text by <see cref="CalculateDistance"/>.
	/// </summary>
	public int MaximumSizeOfDistribution { get; }

	/// <summary>
	/// Language codes from the profile (for example "en" or "zh"), indexed by the language numbers returned from <see cref="Identify(ReadOnlySpan{char})"/>.
	/// </summary>
	public IReadOnlyList<string> Languages => languages;

	/// <summary>
	/// Number of bytes of the tables (see <see cref="WriteTables"/>).
	/// </summary>
	public long TableSize => tables.Length;

	private LanguageIdentifier(ReadOnlyMemory<byte> tables)
	{
		var data = tables.Span;
		if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != TablesMagic)
			throw new InvalidDataException("The data are not language identification tables.");
		if (BinaryPrimitives.ReadInt32LittleEndian(data[4..]) != TablesVersion)
			throw new InvalidDataException("Unsupported version of the language identification tables.");

		MaxNGramLength = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
		MaximumSizeOfDistribution = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
		int languageCount = BinaryPrimitives.ReadInt32LittleEndian(data[16..]);
		int keyCount = BinaryPrimitives.ReadInt32LittleEndian(data[20..]);
		int postingCount = BinaryPrimitives.ReadInt32LittleEndian(data[24..]);
		weightStep = BinaryPrimitives.ReadDoubleLittleEndian(data[32..]);
		if (MaxNGramLength is < 1 or > 4 || languageCount is < 0 or > byte.MaxValue || keyCount < 0 || postingCount < 0)
			throw new InvalidDataException("Invalid language identification tables.");

		layout = TablesLayout.Of(languageCount, keyCount, postingCount);
		if (layout.Codes > data.Length
			|| BinaryPrimitives.ReadInt32LittleEndian(data[layout.PostingStarts..]) != 0
			|| BinaryPrimitives.ReadInt32LittleEndian(data[(layout.PostingStarts + keyCount * sizeof(int))..]) != postingCount)
			throw new InvalidDataException("Invalid language identification tables.");

		languages = new string[languageCount];
		int position = layout.Codes;
		for (int i = 0; i < languageCount; i++)
		{
			if (position >= data.Length || position + 1 + data[position] > data.Length)
				throw new InvalidDataException("Invalid language identification tables.");
			int length = data[position];
			languages[i] =Encoding.UTF8.GetString(data.Slice(position + 1, length));
			position += 1 + length;
		}
		this.tables = tables[..position];
	}

	/// <summary>
	/// Loads language profiles in eM Client's binary profile format: the maximum profile size, the maximum n-gram
	/// length and the number of languages (32-bit integers), then for each language its ISO 639-2/T and
	/// ISO 639-3 codes (length-prefixed strings), the total and distinct counts of the n-grams left out of the
	/// profile, and groups of n-grams of one length (the length as a byte, the n-gram count, then each n-gram's
	/// UTF-8 characters and occurrence count), ending with a zero byte. The language codes come from the
	/// ISO 639-3 field when it is not empty.
	/// </summary>
	public static LanguageIdentifier Load(Stream stream, bool dropScriptDecidedLanguages = true)
	{
		using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
		int maximumSizeOfDistribution = reader.ReadInt32();
		int maxNGramLength = reader.ReadInt32();
		if (maxNGramLength is < 1 or > 4)
			throw new ArgumentOutOfRangeException(nameof(stream), "Only n-grams of 1 to 4 characters fit into the packed key");
		var profiles = new LanguageProfile[reader.ReadInt32()];
		if (profiles.Length > byte.MaxValue)
			throw new ArgumentException("Too many languages", nameof(stream));

		var postings = new List<Posting>();
		Span<char> buffer = stackalloc char[byte.MaxValue];
		Span<long> scriptCounts = stackalloc long[ScriptCount];
		for (int i = 0; i < profiles.Length; i++)
		{
			string iso639_2T = reader.ReadString();
			string iso639_3 = reader.ReadString();
			long totalNoise = reader.ReadInt32();
			long distinctNoise = reader.ReadInt32();
			long represented = 0;
			long distinct = 0;
			scriptCounts.Clear();
			byte length;
			while ((length = reader.ReadByte()) > 0)
			{
				var ngram = buffer[..length];
				int count = reader.ReadInt32();
				postings.EnsureCapacity(postings.Count + count);
				for (int j = 0; j < count; j++)
				{
					reader.Read(ngram);
					int occurrences = reader.ReadInt32();
					represented += occurrences;
					distinct++;
					if (length == 1)
						scriptCounts[(int)GetScript(ngram[0])] += occurrences;
					if (TryPackKey(ngram, out var key))
						postings.Add(new Posting(key, (byte)i, occurrences));
				}
			}
			var language = string.IsNullOrEmpty(iso639_3) ? iso639_2T : iso639_3;
			profiles[i] = new LanguageProfile(language, GetPrimaryScripts(scriptCounts), represented, represented + totalNoise, distinct + distinctNoise);
		}
		return new LanguageIdentifier(BuildTables(profiles, postings, maxNGramLength, maximumSizeOfDistribution, dropScriptDecidedLanguages));
	}

	/// <summary>
	/// Uses tables written by <see cref="WriteTables"/> in place, without copying them: the memory must not change
	/// while the identifier is used. Only the language codes are allocated.
	/// </summary>
	public static LanguageIdentifier LoadTables(ReadOnlyMemory<byte> tables) => new(tables);

	/// <summary>
	/// Writes the tables of the identifier for <see cref="LoadTables"/>. They hold only what scoring needs: the
	/// n-grams of the languages decided by their script are not included unless the profile was loaded with
	/// <c>dropScriptDecidedLanguages: false</c>.
	/// </summary>
	public void WriteTables(Stream stream) => stream.Write(tables.Span);

	/// <summary>
	/// Returns candidate languages ordered from the most likely, with the naive Bayes log-score
	/// (<see cref="double.PositiveInfinity"/> when the script alone decided the language).
	/// </summary>
	public (int Language, double Score)[] Identify(ReadOnlySpan<char> text)
	{
		Span<(int Language, double Score)> result = stackalloc (int, double)[languages.Length];
		return result[..Identify(text, result)].ToArray();
	}

	/// <summary>
	/// Writes candidate languages ordered from the most likely to <paramref name="destination"/>, like
	/// <see cref="Identify(ReadOnlySpan{char})"/> without allocating, and returns their number. The destination
	/// needs at most <see cref="Languages"/>.Count elements.
	/// </summary>
	public int Identify(ReadOnlySpan<char> text, Span<(int Language, double Score)> destination)
	{
		var data = tables.Span;
		Span<int> inputScripts = stackalloc int[ScriptCount];
		CountScripts(text, inputScripts);
		Span<int> candidates = stackalloc int[languages.Length];
		int candidateCount = GetCandidates(data, inputScripts, candidates);
		if (candidateCount > destination.Length)
			throw new ArgumentException("The destination is shorter than the number of candidate languages.", nameof(destination));
		if (candidateCount == 0)
			return 0;
		if (candidateCount == 1 && IsDecidedByScript(data, candidates[0]))
		{
			destination[0] = (candidates[0], double.PositiveInfinity);
			return 1;
		}

		Span<long> sums = stackalloc long[languages.Length];
		int featureCount = SumWeights(data, text, sums);
		for (int i = 0; i < candidateCount; i++)
		{
			int language = candidates[i];
			destination[i] = (language, ReadDouble(data, layout.Biases, language) - featureCount * ReadDouble(data, layout.Penalties, language) + sums[language] * weightStep);
		}
		destination[..candidateCount].Sort(static (a, b) => b.Score.CompareTo(a.Score));
		return candidateCount;
	}

	/// <summary>
	/// Adds the weights of the text's n-grams to the sums of the languages and returns the number of n-grams.
	/// </summary>
	private int SumWeights(ReadOnlySpan<byte> data, ReadOnlySpan<char> text, Span<long> sums)
	{
		var keys = data.Slice(layout.Keys, layout.KeyCount * sizeof(ulong));
		var postingStarts = data[layout.PostingStarts..];
		var postingLanguages = data[layout.PostingLanguages..];
		var postingWeights = data[layout.PostingWeights..];
		int featureCount = 0;
		foreach (var key in new NGramEnumerator(text, MaxNGramLength))
		{
			featureCount++;
			int index = FindKey(keys, key);
			if (index < 0)
				continue;
			int end = ReadInt32(postingStarts, index + 1);
			for (int p = ReadInt32(postingStarts, index); p < end; p++)
				sums[postingLanguages[p]] += postingWeights[p];
		}
		return featureCount;
	}

	/// <summary>
	/// Distance between the n-gram frequencies of a language and of the text: 1 minus the sum of the smaller of the
	/// two frequencies over the shared n-grams (0 = identical distributions, 1 = nothing in common).
	/// </summary>
	public double CalculateDistance(int language, ReadOnlySpan<char> text)
	{
		var data = tables.Span;
		if (IsDecidedByScript(data, language))
			return 0;

		var counts = new Dictionary<ulong, int>();
		foreach (var key in new NGramEnumerator(text, MaxNGramLength))
			counts[key] = counts.GetValueOrDefault(key) + 1;

		IEnumerable<KeyValuePair<ulong, int>> profile = counts;
		if (counts.Count > MaximumSizeOfDistribution)
			profile = counts.OrderByDescending(kv => kv.Value).Take(MaximumSizeOfDistribution);
		var profileList = profile.ToList();
		double total = profileList.Sum(kv => (double)kv.Value);

		var keys = data.Slice(layout.Keys, layout.KeyCount * sizeof(ulong));
		var postingStarts = data[layout.PostingStarts..];
		var postingLanguages = data[layout.PostingLanguages..];
		var postingWeights = data[layout.PostingWeights..];
		double representedTotal = ReadDouble(data, layout.RepresentedTotals, language);
		double overlap = 0;
		foreach (var (key, count) in profileList)
		{
			int index = FindKey(keys, key);
			if (index < 0)
				continue;
			int end = ReadInt32(postingStarts, index + 1);
			for (int p = ReadInt32(postingStarts, index); p < end; p++)
			{
				if (postingLanguages[p] != language)
					continue;
				double languageFrequency = (Math.Exp(postingWeights[p] * weightStep) - 1) / representedTotal;
				overlap += Math.Min(languageFrequency, count / total);
				break;
			}
		}
		return 1.0 - overlap;
	}

	/// <summary>
	/// The n-grams extracted from the text, in the order they are scored.
	/// </summary>
	internal List<string> GetNGrams(ReadOnlySpan<char> text)
	{
		var result = new List<string>();
		foreach (var key in new NGramEnumerator(text, MaxNGramLength))
			result.Add(UnpackKey(key));
		return result;
	}

	internal static string UnpackKey(ulong key)
	{
		Span<char> buffer = stackalloc char[4];
		int length = 0;
		for (var remaining = key; remaining != 0; remaining >>= 16)
			buffer[length++] = (char)remaining;
		buffer[..length].Reverse();
		return new string(buffer[..length]);
	}

	private static byte[] BuildTables(LanguageProfile[] profiles, List<Posting> postings, int maxNGramLength, int maximumSizeOfDistribution, bool dropScriptDecidedLanguages)
	{
		var decidedByScript = new bool[profiles.Length];
		if (dropScriptDecidedLanguages)
		{
			for (int i = 0; i < profiles.Length; i++)
				decidedByScript[i] = profiles[i].PrimaryScripts != ScriptSet.None && IsScriptUnique(i);
			postings.RemoveAll(p => decidedByScript[p.Language]);
		}
		postings.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Language.CompareTo(b.Language));

		int keyCount = 0;
		for (int i = 0; i < postings.Count; i++)
		{
			if (i == 0 || postings[i - 1].Key != postings[i].Key)
				keyCount++;
		}
		var codes = profiles.Select(p => Encoding.UTF8.GetBytes(p.Language)).ToArray();
		if (codes.Any(c => c.Length > byte.MaxValue))
			throw new ArgumentException("A language code is too long.");
		var layout = TablesLayout.Of(profiles.Length, keyCount, postings.Count);
		var tables = new byte[layout.Codes + codes.Sum(c => 1 + c.Length)];
		var data = tables.AsSpan();

		double maxWeight = postings.Count > 0 ? postings.Max(p => Math.Log(p.Count + 1L)) : 1;
		double weightStep = maxWeight / byte.MaxValue;
		BinaryPrimitives.WriteUInt32LittleEndian(data, TablesMagic);
		BinaryPrimitives.WriteInt32LittleEndian(data[4..], TablesVersion);
		BinaryPrimitives.WriteInt32LittleEndian(data[8..], maxNGramLength);
		BinaryPrimitives.WriteInt32LittleEndian(data[12..], maximumSizeOfDistribution);
		BinaryPrimitives.WriteInt32LittleEndian(data[16..], profiles.Length);
		BinaryPrimitives.WriteInt32LittleEndian(data[20..], keyCount);
		BinaryPrimitives.WriteInt32LittleEndian(data[24..], postings.Count);
		BinaryPrimitives.WriteDoubleLittleEndian(data[32..], weightStep);

		// Same quantities as NaiveBayesClassifier with kSmoothFactor = 1.
		double globalTotal = profiles.Sum(p => (double)p.TotalCountWithNoise);
		double globalDistinct = profiles.Sum(p => (double)p.DistinctCountWithNoise);
		for (int i = 0; i < profiles.Length; i++)
		{
			var profile = profiles[i];
			BinaryPrimitives.WriteDoubleLittleEndian(data[(layout.Biases + i * sizeof(double))..], Math.Log(profile.TotalCountWithNoise) - Math.Log(globalTotal));
			BinaryPrimitives.WriteDoubleLittleEndian(data[(layout.Penalties + i * sizeof(double))..], Math.Log(profile.TotalCountWithNoise + globalDistinct));
			BinaryPrimitives.WriteDoubleLittleEndian(data[(layout.RepresentedTotals + i * sizeof(double))..], profile.RepresentedCount);
			BinaryPrimitives.WriteInt32LittleEndian(data[(layout.PrimaryScripts + i * sizeof(int))..], (int)profile.PrimaryScripts);
			data[layout.DecidedByScript + i] = decidedByScript[i] ? (byte)1 : (byte)0;
		}

		int keyIndex = 0;
		for (int i = 0; i < postings.Count; i++)
		{
			var (key, language, count) = postings[i];
			if (i == 0 || postings[i - 1].Key != key)
			{
				BinaryPrimitives.WriteUInt64LittleEndian(data[(layout.Keys + keyIndex * sizeof(ulong))..], key);
				BinaryPrimitives.WriteInt32LittleEndian(data[(layout.PostingStarts + keyIndex * sizeof(int))..], i);
				keyIndex++;
			}
			data[layout.PostingLanguages + i] = language;
			data[layout.PostingWeights + i] = (byte)Math.Clamp(Math.Round(Math.Log(count + 1L) / weightStep), 1, byte.MaxValue);
		}
		BinaryPrimitives.WriteInt32LittleEndian(data[(layout.PostingStarts + keyCount * sizeof(int))..], postings.Count);

		int position = layout.Codes;
		foreach (var code in codes)
		{
			data[position] = (byte)code.Length;
			code.CopyTo(data[(position + 1)..]);
			position += 1 + code.Length;
		}
		return tables;

		bool IsScriptUnique(int index)
		{
			for (int j = 0; j < profiles.Length; j++)
			{
				if (j != index && (profiles[j].PrimaryScripts & profiles[index].PrimaryScripts) != 0)
					return false;
			}
			return true;
		}
	}

	private static int FindKey(ReadOnlySpan<byte> keys, ulong key)
	{
		// The search stays within the span, so it reads without bounds checks.
		ref byte start = ref MemoryMarshal.GetReference(keys);
		int low = 0;
		int high = keys.Length / sizeof(ulong) - 1;
		while (low <= high)
		{
			int middle = (int)((uint)(low + high) >> 1);
			ulong value = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, middle * sizeof(ulong)));
			if (!BitConverter.IsLittleEndian)
				value = BinaryPrimitives.ReverseEndianness(value);
			if (value == key)
				return middle;
			if (value < key)
				low = middle + 1;
			else
				high = middle - 1;
		}
		return -1;
	}

	private static int ReadInt32(ReadOnlySpan<byte> data, int index) =>
		BinaryPrimitives.ReadInt32LittleEndian(data[(index * sizeof(int))..]);

	private static double ReadDouble(ReadOnlySpan<byte> data, int offset, int index) =>
		BinaryPrimitives.ReadDoubleLittleEndian(data[(offset + index * sizeof(double))..]);

	private bool IsDecidedByScript(ReadOnlySpan<byte> data, int language) => data[layout.DecidedByScript + language] != 0;

	private int GetCandidates(ReadOnlySpan<byte> data, ReadOnlySpan<int> inputScripts, Span<int> candidates)
	{
		int letters = 0;
		foreach (var count in inputScripts)
			letters += count;

		int candidateCount = 0;
		if (letters == 0)
			return candidateCount;

		double bestCoverage = MinimumCoverage;
		for (int i = 0; i < languages.Length; i++)
		{
			var primaryScripts = (ScriptSet)BinaryPrimitives.ReadInt32LittleEndian(data[(layout.PrimaryScripts + i * sizeof(int))..]);
			int covered = 0;
			for (int s = 1; s < inputScripts.Length; s++)
			{
				if ((primaryScripts & (ScriptSet)(1 << s)) != 0)
					covered += inputScripts[s];
			}
			double coverage = covered / (double)letters;
			if (coverage > bestCoverage + 1e-9)
			{
				bestCoverage = coverage;
				candidateCount = 0;
			}
			if (coverage >= bestCoverage - 1e-9)
				candidates[candidateCount++] = i;
		}
		return candidateCount;
	}

	private static ScriptSet GetPrimaryScripts(ReadOnlySpan<long> counts)
	{
		long total = 0;
		foreach (var count in counts)
			total += count;

		var result = ScriptSet.None;
		for (int s = 1; s < ScriptCount; s++)
		{
			if (total > 0 && counts[s] >= total * PrimaryScriptShare)
				result |= (ScriptSet)(1 << s);
		}
		return result;
	}

	private static void CountScripts(ReadOnlySpan<char> text, Span<int> counts)
	{
		foreach (var c in text)
		{
			if (char.IsLetter(c))
				counts[(int)GetScript(c)]++;
		}
		counts[(int)Script.Other] = 0;
	}

	private static bool TryPackKey(ReadOnlySpan<char> ngram, out ulong key)
	{
		key = 0;
		if (ngram.Length is 0 or > 4)
			return false;
		foreach (var c in ngram)
			key = key << 16 | c;
		return true;
	}

	private sealed record LanguageProfile(string Language, ScriptSet PrimaryScripts, long RepresentedCount, long TotalCountWithNoise, long DistinctCountWithNoise);

	private readonly record struct Posting(ulong Key, byte Language, int Count);

	/// <summary>
	/// Offsets of the parts of the tables, which follow the header: the bias, penalty and represented n-gram count
	/// (doubles), the primary scripts (an int) and whether the script decides the language (a byte) of each language,
	/// then, 8-aligned, the sorted keys, the start of each key's postings (ints, one more than the keys), the
	/// postings' languages and weights (bytes), and the language codes (a byte length and UTF-8).
	/// </summary>
	private readonly record struct TablesLayout(int KeyCount, int Biases, int Penalties, int RepresentedTotals, int PrimaryScripts, int DecidedByScript,
		int Keys, int PostingStarts, int PostingLanguages, int PostingWeights, int Codes)
	{
		public static TablesLayout Of(int languageCount, int keyCount, int postingCount)
		{
			long biases = HeaderSize;
			long penalties = biases + (long)languageCount * sizeof(double);
			long representedTotals = penalties + (long)languageCount * sizeof(double);
			long primaryScripts = representedTotals + (long)languageCount * sizeof(double);
			long decidedByScript = primaryScripts + (long)languageCount * sizeof(int);
			long keys = (decidedByScript + languageCount + 7) & ~7L;
			long postingStarts = keys + (long)keyCount * sizeof(ulong);
			long postingLanguages = postingStarts + ((long)keyCount + 1) * sizeof(int);
			long postingWeights = postingLanguages + postingCount;
			long codes = postingWeights + postingCount;
			if (codes > Array.MaxLength)
				throw new InvalidDataException("The language identification tables are too large.");
			return new(keyCount, (int)biases, (int)penalties, (int)representedTotals, (int)primaryScripts, (int)decidedByScript,
				(int)keys, (int)postingStarts, (int)postingLanguages, (int)postingWeights, (int)codes);
		}
	}

	private const int ScriptCount = (int)Script.Telugu + 1;

	// The tables store the primary scripts as bits of these numbers: add new scripts at the end and change
	// ScriptCount.
	private enum Script
	{
		Other,
		Latin,
		Greek,
		Cyrillic,
		Armenian,
		Hebrew,
		Arabic,
		Devanagari,
		Bengali,
		Tamil,
		Thai,
		Georgian,
		Hangul,
		Kana,
		Han,
		Telugu,
	}

	[Flags]
	private enum ScriptSet
	{
		None = 0,
	}

	private static Script GetScript(char c) => c switch
	{
		<= '\u024F' => Script.Latin,
		>= '\u0370' and <= '\u03FF' => Script.Greek,
		>= '\u0400' and <= '\u052F' => Script.Cyrillic,
		>= '\u0530' and <= '\u058F' => Script.Armenian,
		>= '\u0590' and <= '\u05FF' => Script.Hebrew,
		>= '\u0600' and <= '\u06FF' or >= '\u0750' and <= '\u077F' or >= '\uFB50' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF' => Script.Arabic,
		>= '\u0900' and <= '\u097F' => Script.Devanagari,
		>= '\u0980' and <= '\u09FF' => Script.Bengali,
		>= '\u0B80' and <= '\u0BFF' => Script.Tamil,
		>= '\u0C00' and <= '\u0C7F' => Script.Telugu,
		>= '\u0E00' and <= '\u0E7F' => Script.Thai,
		>= '\u10A0' and <= '\u10FF' => Script.Georgian,
		>= '\u1E00' and <= '\u1EFF' => Script.Latin,
		>= '\u1100' and <= '\u11FF' or >= '\u3130' and <= '\u318F' or >= '\uAC00' and <= '\uD7AF' => Script.Hangul,
		>= '\u3040' and <= '\u30FF' or >= '\u31F0' and <= '\u31FF' or >= '\uFF66' and <= '\uFF9F' => Script.Kana,
		>= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF' => Script.Han,
		>= '\uFF21' and <= '\uFF5A' => Script.Latin,
		_ => Script.Other,
	};

	/// <summary>
	/// Allocation-free n-gram extraction producing packed keys. Every maximal run of letters is wrapped in
	/// underscores ("_word_") and all its n-grams of 1 to <see cref="MaxNGramLength"/> characters are produced,
	/// except the "_" unigram. These are the n-grams of NTextCat, which the format's profiles were first trained with.
	/// </summary>
	internal ref struct NGramEnumerator
	{
		private readonly ReadOnlySpan<char> text;
		private readonly int maxNGramLength;
		private int position;
		private ulong window;
		private int windowLength;
		private int pendingLength;
		private bool insideWord;
		private bool flushTrailing;

		public NGramEnumerator(ReadOnlySpan<char> text, int maxNGramLength)
		{
			this.text = text;
			this.maxNGramLength = maxNGramLength;
			position = 0;
			Current = 0;
		}

		public ulong Current { get; private set; }

		public readonly NGramEnumerator GetEnumerator() => this;

		public bool MoveNext()
		{
			while (true)
			{
				// Emit the n-grams ending at the last pushed character.
				if (pendingLength > 0)
				{
					int length = pendingLength--;
					Current = window & (length == 4 ? ulong.MaxValue : (1UL << (16 * length)) - 1);
					if (length == 1 && Current == '_')
						continue;
					return true;
				}

				if (flushTrailing)
				{
					flushTrailing = false;
					window = 0;
					windowLength = 0;
				}

				if (position >= text.Length)
				{
					if (!insideWord)
						return false;
					insideWord = false;
					Push('_');
					flushTrailing = true;
					continue;
				}

				char c = text[position++];
				if (char.IsLetter(c))
				{
					if (!insideWord)
					{
						insideWord = true;
						Push('_');
						// The leading '_' only produces a filtered unigram, so push the letter right away as well.
						pendingLength = 0;
					}
					Push(c);
				}
				else if (insideWord)
				{
					insideWord = false;
					Push('_');
					flushTrailing = true;
				}
			}
		}

		private void Push(char c)
		{
			window = window << 16 | c;
			if (windowLength < maxNGramLength)
				windowLength++;
			pendingLength = windowLength;
		}
	}
}
