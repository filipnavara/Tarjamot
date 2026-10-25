using System.Text;

namespace Tarjamot.LanguageIdentification.Tests;

/// <summary>
/// Builds small profiles in eM Client's binary profile format and computes reference naive Bayes scores, with its own
/// n-gram counting so the tests do not depend on <see cref="LanguageIdentifier"/>'s extraction.
/// </summary>
internal static class TestProfile
{
	public const int MaximumSizeOfDistribution = 4000;

	public static byte[] Write(IEnumerable<(string Iso639_2T, string Iso639_3, string Text)> languages, int maxNGramLength)
	{
		var list = languages.ToList();
		using var stream = new MemoryStream();
		using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
		{
			writer.Write(MaximumSizeOfDistribution);
			writer.Write(maxNGramLength);
			writer.Write(list.Count);
			foreach (var (iso639_2T, iso639_3, text) in list)
			{
				writer.Write(iso639_2T);
				writer.Write(iso639_3);
				writer.Write(0); // total count of n-grams left out of the profile
				writer.Write(0); // distinct count of n-grams left out of the profile
				foreach (var group in CountNGrams(text, maxNGramLength).GroupBy(kv => kv.Key.Length))
				{
					writer.Write((byte)group.Key);
					writer.Write(group.Count());
					foreach (var (ngram, count) in group)
					{
						writer.Write(ngram.ToCharArray());
						writer.Write(count);
					}
				}
				writer.Write((byte)0);
			}
		}
		return stream.ToArray();
	}

	public static Dictionary<string, int> CountNGrams(string text, int maxNGramLength)
	{
		var counts = new Dictionary<string, int>(StringComparer.Ordinal);
		var word = new StringBuilder();
		foreach (var c in text + " ")
		{
			if (char.IsLetter(c))
			{
				word.Append(c);
				continue;
			}
			if (word.Length == 0)
				continue;
			var wrapped = "_" + word + "_";
			for (var length = 1; length <= maxNGramLength; length++)
			{
				for (var start = 0; start + length <= wrapped.Length; start++)
				{
					var ngram = wrapped.Substring(start, length);
					if (ngram != "_")
						counts[ngram] = counts.GetValueOrDefault(ngram) + 1;
				}
			}
			word.Clear();
		}
		return counts;
	}

	public static int MaxCount(IEnumerable<(string Iso639_2T, string Iso639_3, string Text)> languages) =>
		languages.Max(l => CountNGrams(l.Text, 3).Values.Max());

	/// <summary>
	/// NTextCat's naive Bayes score with add-one smoothing: the log prior of the language plus, for every n-gram of
	/// the text, log((count + 1) / (language total + distinct n-grams of all languages)).
	/// </summary>
	public static Dictionary<string, double> NaiveBayesScores(IEnumerable<(string Iso639_2T, string Iso639_3, string Text)> languages, string text)
	{
		var profiles = languages.ToDictionary(l => l.Iso639_3, l => CountNGrams(l.Text, 3));
		double globalTotal = profiles.Values.Sum(p => p.Values.Sum());
		double globalDistinct = profiles.Values.Sum(p => p.Count);
		var features = CountNGrams(text, 3);
		var scores = new Dictionary<string, double>();
		foreach (var (language, counts) in profiles)
		{
			double total = counts.Values.Sum();
			var score = Math.Log(total) - Math.Log(globalTotal);
			foreach (var (ngram, occurrences) in features)
				score += occurrences * (Math.Log(counts.GetValueOrDefault(ngram) + 1) - Math.Log(total + globalDistinct));
			scores[language] = score;
		}
		return scores;
	}
}
