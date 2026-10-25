using System.Text;

namespace Tarjamot.LanguageIdentification;

/// <summary>
/// Counts the character n-grams of training text per language and writes the profiles in the binary NTextCat
/// format that <see cref="LanguageIdentifier.Load"/> reads.
/// </summary>
/// <remarks>
/// The n-grams are extracted exactly as <see cref="LanguageIdentifier"/> extracts them from the text it identifies.
/// Each language keeps its <see cref="MaximumSizeOfDistribution"/> most frequent n-grams; the others are written
/// only as the total and distinct counts the naive Bayes prior and smoothing use.
/// </remarks>
public sealed class LanguageProfileBuilder
{
	private readonly List<(string Language, Dictionary<ulong, long> Counts)> languages = [];

	public LanguageProfileBuilder(int maxNGramLength = 3, int maximumSizeOfDistribution = 4000)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(maxNGramLength, 1);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(maxNGramLength, 4);
		ArgumentOutOfRangeException.ThrowIfLessThan(maximumSizeOfDistribution, 1);
		MaxNGramLength = maxNGramLength;
		MaximumSizeOfDistribution = maximumSizeOfDistribution;
	}

	public int MaxNGramLength { get; }

	public int MaximumSizeOfDistribution { get; }

	/// <summary>
	/// Languages in the order they were first added, which is also their order in the written profile.
	/// </summary>
	public IReadOnlyList<string> Languages => languages.Select(l => l.Language).ToList();

	/// <summary>
	/// Adds training text of a language, for example "en", "zh-Hans" or "sr-Latn".
	/// </summary>
	public void Add(string language, ReadOnlySpan<char> text)
	{
		var counts = GetCounts(language);
		foreach (var key in new LanguageIdentifier.NGramEnumerator(text, MaxNGramLength))
			counts[key] = counts.GetValueOrDefault(key) + 1;
	}

	/// <summary>
	/// Adds training text of a language line by line. Line breaks separate words, so this counts the same n-grams
	/// as adding the whole text at once.
	/// </summary>
	public void Add(string language, TextReader reader)
	{
		while (reader.ReadLine() is { } line)
			Add(language, line);
	}

	public void Write(Stream stream)
	{
		using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
		writer.Write(MaximumSizeOfDistribution);
		writer.Write(MaxNGramLength);
		writer.Write(languages.Count);
		foreach (var (language, counts) in languages)
		{
			// Ties are broken by the n-gram so that the same text always gives the same profile.
			var ranked = counts
				.Select(kv => (NGram: LanguageIdentifier.UnpackKey(kv.Key), Count: kv.Value))
				.OrderByDescending(e => e.Count)
				.ThenBy(e => e.NGram, StringComparer.Ordinal)
				.ToList();
			var kept = ranked.Take(MaximumSizeOfDistribution).ToList();
			var noise = ranked.Skip(MaximumSizeOfDistribution).ToList();

			// NTextCat stores an ISO 639-2/T and an ISO 639-3 code; both hold the language tag here.
			writer.Write(language);
			writer.Write(language);
			writer.Write(checked((int)noise.Sum(e => e.Count)));
			writer.Write(noise.Count);
			foreach (var group in kept.GroupBy(e => e.NGram.Length).OrderBy(g => g.Key))
			{
				writer.Write((byte)group.Key);
				writer.Write(group.Count());
				foreach (var (ngram, count) in group.OrderBy(e => e.NGram, StringComparer.Ordinal))
				{
					writer.Write(ngram.ToCharArray());
					writer.Write(checked((int)count));
				}
			}
			writer.Write((byte)0);
		}
	}

	private Dictionary<ulong, long> GetCounts(string language)
	{
		ArgumentException.ThrowIfNullOrEmpty(language);
		foreach (var (existing, counts) in languages)
		{
			if (existing == language)
				return counts;
		}
		var added = new Dictionary<ulong, long>();
		languages.Add((language, added));
		return added;
	}
}
