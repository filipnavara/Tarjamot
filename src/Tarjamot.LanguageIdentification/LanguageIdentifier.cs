using System.Text;

namespace Tarjamot.LanguageIdentification;

/// <summary>
/// Naive Bayes language identifier over character n-grams. It reads NTextCat language profiles in their
/// binary form and scores like NTextCat's naive Bayes identifier with add-one smoothing.
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
/// </remarks>
public sealed class LanguageIdentifier
{
	private const double PrimaryScriptShare = 0.2;
	private const double MinimumCoverage = 0.5;

	private readonly string[] languages;
	private readonly ulong[] keys;
	private readonly int[] postingStarts;
	private readonly byte[] postingLanguages;
	private readonly byte[] postingWeights;
	private readonly double weightStep;
	private readonly double[] biases;
	private readonly double[] penalties;
	private readonly double[] representedTotals;
	private readonly ScriptSet[] primaryScripts;
	private readonly bool[] decidedByScript;

	public int MaxNGramLength { get; }

	/// <summary>
	/// Maximum number of n-grams kept in the profile of the analyzed text by <see cref="CalculateDistance"/>.
	/// </summary>
	public int MaximumSizeOfDistribution { get; }

	/// <summary>
	/// Language codes from the profile (for example "en" or "zh"), indexed by the language numbers returned from <see cref="Identify"/>.
	/// </summary>
	public IReadOnlyList<string> Languages => languages;

	/// <summary>
	/// Approximate number of bytes held by the lookup tables.
	/// </summary>
	public long TableSize =>
		keys.Length * sizeof(ulong) + postingStarts.Length * sizeof(int) + postingLanguages.Length + postingWeights.Length;

	private LanguageIdentifier(LanguageProfile[] profiles, int maxNGramLength, int maximumSizeOfDistribution, bool dropScriptDecidedLanguages)
	{
		if (maxNGramLength is < 1 or > 4)
			throw new ArgumentOutOfRangeException(nameof(maxNGramLength), "Only n-grams of 1 to 4 characters fit into the packed key");
		if (profiles.Length > byte.MaxValue)
			throw new ArgumentException("Too many languages", nameof(profiles));

		MaxNGramLength = maxNGramLength;
		MaximumSizeOfDistribution = maximumSizeOfDistribution;
		languages = profiles.Select(p => p.Language).ToArray();
		primaryScripts = profiles.Select(GetPrimaryScripts).ToArray();
		decidedByScript = new bool[profiles.Length];
		if (dropScriptDecidedLanguages)
		{
			for (int i = 0; i < profiles.Length; i++)
				decidedByScript[i] = primaryScripts[i] != ScriptSet.None && IsScriptUnique(i);
		}

		// Same quantities as NaiveBayesClassifier with kSmoothFactor = 1.
		double globalTotal = profiles.Sum(p => (double)p.TotalCountWithNoise);
		double globalDistinct = profiles.Sum(p => (double)p.DistinctCountWithNoise);
		biases = profiles.Select(p => Math.Log(p.TotalCountWithNoise) - Math.Log(globalTotal)).ToArray();
		penalties = profiles.Select(p => Math.Log(p.TotalCountWithNoise + globalDistinct)).ToArray();
		representedTotals = profiles.Select(p => (double)p.RepresentedCount).ToArray();

		var postings = new List<(ulong Key, byte Language, long Count)>();
		for (int i = 0; i < profiles.Length; i++)
		{
			if (decidedByScript[i])
				continue;
			foreach (var (ngram, count) in profiles[i].NGrams)
			{
				if (TryPackKey(ngram, out var key))
					postings.Add((key, (byte)i, count));
			}
		}
		postings.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Language.CompareTo(b.Language));

		double maxWeight = postings.Count > 0 ? postings.Max(p => Math.Log(p.Count + 1)) : 1;
		weightStep = maxWeight / byte.MaxValue;

		var keyList = new List<ulong>();
		var startList = new List<int>();
		postingLanguages = new byte[postings.Count];
		postingWeights = new byte[postings.Count];
		for (int i = 0; i < postings.Count; i++)
		{
			var (key, language, count) = postings[i];
			if (keyList.Count == 0 || keyList[^1] != key)
			{
				keyList.Add(key);
				startList.Add(i);
			}
			postingLanguages[i] = language;
			postingWeights[i] = (byte)Math.Clamp(Math.Round(Math.Log(count + 1) / weightStep), 1, byte.MaxValue);
		}
		startList.Add(postings.Count);
		keys = keyList.ToArray();
		postingStarts = startList.ToArray();

		bool IsScriptUnique(int index)
		{
			for (int j = 0; j < primaryScripts.Length; j++)
			{
				if (j != index && (primaryScripts[j] & primaryScripts[index]) != 0)
					return false;
			}
			return true;
		}
	}

	/// <summary>
	/// Loads language profiles in the binary NTextCat format: the maximum profile size, the maximum n-gram
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
		var profiles = new LanguageProfile[reader.ReadInt32()];
		for (int i = 0; i < profiles.Length; i++)
		{
			string iso639_2T = reader.ReadString();
			string iso639_3 = reader.ReadString();
			long totalNoise = reader.ReadInt32();
			long distinctNoise = reader.ReadInt32();
			var ngrams = new List<(string, long)>();
			long represented = 0;
			byte length;
			while ((length = reader.ReadByte()) > 0)
			{
				var buffer = new char[length];
				int count = reader.ReadInt32();
				for (int j = 0; j < count; j++)
				{
					reader.Read(buffer, 0, length);
					int occurrences = reader.ReadInt32();
					ngrams.Add((new string(buffer), occurrences));
					represented += occurrences;
				}
			}
			var language = string.IsNullOrEmpty(iso639_3) ? iso639_2T : iso639_3;
			profiles[i] = new LanguageProfile(language, ngrams, represented, represented + totalNoise, ngrams.Count + distinctNoise);
		}
		return new LanguageIdentifier(profiles, maxNGramLength, maximumSizeOfDistribution, dropScriptDecidedLanguages);
	}

	/// <summary>
	/// Returns candidate languages ordered from the most likely, with the naive Bayes log-score
	/// (<see cref="double.PositiveInfinity"/> when the script alone decided the language).
	/// </summary>
	public (int Language, double Score)[] Identify(ReadOnlySpan<char> text)
	{
		var inputScripts = CountScripts(text);
		var candidates = GetCandidates(inputScripts);
		if (candidates.Count == 0)
			return [];
		if (candidates.Count == 1 && decidedByScript[candidates[0]])
			return [(candidates[0], double.PositiveInfinity)];

		var sums = new long[languages.Length];
		int featureCount = 0;
		foreach (var key in new NGramEnumerator(text, MaxNGramLength))
		{
			featureCount++;
			int index = Array.BinarySearch(keys, key);
			if (index < 0)
				continue;
			for (int p = postingStarts[index]; p < postingStarts[index + 1]; p++)
				sums[postingLanguages[p]] += postingWeights[p];
		}

		var result = new (int Language, double Score)[candidates.Count];
		for (int i = 0; i < candidates.Count; i++)
		{
			int language = candidates[i];
			result[i] = (language, biases[language] - featureCount * penalties[language] + sums[language] * weightStep);
		}
		Array.Sort(result, (a, b) => b.Score.CompareTo(a.Score));
		return result;
	}

	/// <summary>
	/// Distance between the n-gram frequencies of a language and of the text: 1 minus the sum of the smaller of the
	/// two frequencies over the shared n-grams (0 = identical distributions, 1 = nothing in common).
	/// </summary>
	public double CalculateDistance(int language, ReadOnlySpan<char> text)
	{
		if (decidedByScript[language])
			return 0;

		var counts = new Dictionary<ulong, int>();
		foreach (var key in new NGramEnumerator(text, MaxNGramLength))
			counts[key] = counts.GetValueOrDefault(key) + 1;

		IEnumerable<KeyValuePair<ulong, int>> profile = counts;
		if (counts.Count > MaximumSizeOfDistribution)
			profile = counts.OrderByDescending(kv => kv.Value).Take(MaximumSizeOfDistribution);
		var profileList = profile.ToList();
		double total = profileList.Sum(kv => (double)kv.Value);

		double overlap = 0;
		foreach (var (key, count) in profileList)
		{
			int index = Array.BinarySearch(keys, key);
			if (index < 0)
				continue;
			for (int p = postingStarts[index]; p < postingStarts[index + 1]; p++)
			{
				if (postingLanguages[p] != language)
					continue;
				double languageFrequency = (Math.Exp(postingWeights[p] * weightStep) - 1) / representedTotals[language];
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

	private List<int> GetCandidates(Span<int> inputScripts)
	{
		int letters = 0;
		foreach (var count in inputScripts)
			letters += count;

		var candidates = new List<int>();
		if (letters == 0)
			return candidates;

		double bestCoverage = MinimumCoverage;
		for (int i = 0; i < languages.Length; i++)
		{
			int covered = 0;
			for (int s = 1; s < inputScripts.Length; s++)
			{
				if ((primaryScripts[i] & (ScriptSet)(1 << s)) != 0)
					covered += inputScripts[s];
			}
			double coverage = covered / (double)letters;
			if (coverage > bestCoverage + 1e-9)
			{
				bestCoverage = coverage;
				candidates.Clear();
			}
			if (coverage >= bestCoverage - 1e-9)
				candidates.Add(i);
		}
		return candidates;
	}

	private static ScriptSet GetPrimaryScripts(LanguageProfile profile)
	{
		Span<long> counts = stackalloc long[ScriptCount];
		long total = 0;
		foreach (var (ngram, count) in profile.NGrams)
		{
			if (ngram.Length != 1)
				continue;
			counts[(int)GetScript(ngram[0])] += count;
			total += count;
		}

		var result = ScriptSet.None;
		for (int s = 1; s < ScriptCount; s++)
		{
			if (total > 0 && counts[s] >= total * PrimaryScriptShare)
				result |= (ScriptSet)(1 << s);
		}
		return result;
	}

	private static int[] CountScripts(ReadOnlySpan<char> text)
	{
		var counts = new int[ScriptCount];
		foreach (var c in text)
		{
			if (char.IsLetter(c))
				counts[(int)GetScript(c)]++;
		}
		counts[(int)Script.Other] = 0;
		return counts;
	}

	private static bool TryPackKey(string ngram, out ulong key)
	{
		key = 0;
		if (ngram.Length is 0 or > 4)
			return false;
		foreach (var c in ngram)
			key = key << 16 | c;
		return true;
	}

	private sealed record LanguageProfile(string Language, List<(string NGram, long Count)> NGrams, long RepresentedCount, long TotalCountWithNoise, long DistinctCountWithNoise);

	private const int ScriptCount = (int)Script.Han + 1;

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
	/// except the "_" unigram. This matches the n-grams the NTextCat profiles were trained on.
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
