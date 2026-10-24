using System.Text;
using Tarjamot.Engine;

namespace Tarjamot;

/// <summary>
/// Text-level translation as Bergamot's WASM build does it: the text is segmented into sentences
/// once, the sentences go through one model or two (pivoting keeps the source segmentation), and
/// the result is assembled with the source text between the sentences ("gaps", see
/// AnnotatedText::handleEndingWhitespace in Bergamot).
/// </summary>
internal static class TextTranslation
{
	public static string Translate(string text, IReadOnlyList<TranslationModel> models, string targetLanguage, bool splitSentences, CancellationToken cancellationToken) =>
		Translate([text], models, targetLanguage, splitSentences, cancellationToken)[0];

	/// <summary>Translates several texts; their sentences are batched together.</summary>
	public static string[] Translate(IReadOnlyList<string> texts, IReadOnlyList<TranslationModel> models, string targetLanguage, bool splitSentences, CancellationToken cancellationToken)
	{
		var input = PrepareInput(texts, models[0].TokenizeSentence, splitSentences);
		var ranges = input.Ranges;
		IReadOnlyList<string> sentences = [];
		if (input.Sentences.Count > 0)
		{
			sentences = models[0].TranslateTokenizedSentences(input.Sentences, cancellationToken);
			for (int leg = 1; leg < models.Count; leg++)
				sentences = models[leg].TranslateSentences(sentences, cancellationToken);
		}

		var results = new string[texts.Count];
		int offset = 0;
		for (int i = 0; i < texts.Count; i++)
		{
			results[i] = ranges[i].Length == 0 ? "" : Assemble(texts[i], ranges[i], sentences.Skip(offset).Take(ranges[i].Length).ToArray(), targetLanguage);
			offset += ranges[i].Length;
		}
		return results;
	}

	internal sealed record PreparedInput(Range[][] Ranges, IReadOnlyList<TokenizedSentence> Sentences);

	/// <summary>Splits and filters while retaining each candidate's single tokenization for the first model.</summary>
	internal static PreparedInput PrepareInput(IReadOnlyList<string> texts, Func<string, TokenizedSentence> tokenize, bool splitSentences)
	{
		var ranges = new Range[texts.Count][];
		var sentences = new List<TokenizedSentence>();
		for (int i = 0; i < texts.Count; i++)
		{
			var selected = new List<Range>();
			var candidates = splitSentences ? SentenceSplitter.SplitRanges(texts[i]) : SentenceSplitter.SplitLines(texts[i]);
			foreach (var range in candidates)
			{
				var tokens = tokenize(texts[i][range]);
				if (!tokens.HasTokens)
					continue;
				selected.Add(range);
				sentences.Add(tokens);
			}
			ranges[i] = selected.ToArray();
		}
		return new PreparedInput(ranges, sentences);
	}

	internal static string Assemble(string text, Range[] ranges, IReadOnlyList<string> sentences, string targetLanguage)
	{
		// Japanese and Chinese use full-width punctuation and no space between sentences.
		bool omitSpaces = targetLanguage.StartsWith("ja", StringComparison.OrdinalIgnoreCase) || targetLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
		var result = new StringBuilder(text.Length);
		int previousEnd = 0;
		for (int i = 0; i < ranges.Length; i++)
		{
			AppendGap(text.AsSpan(previousEnd, ranges[i].Start.Value - previousEnd), betweenSentences: true);
			result.Append(sentences[i]);
			previousEnd = ranges[i].End.Value;
		}
		AppendGap(text.AsSpan(previousEnd), betweenSentences: false);
		return result.ToString();

		void AppendGap(ReadOnlySpan<char> gap, bool betweenSentences)
		{
			if (gap.Contains('\n'))
				result.Append(gap);
			else if (omitSpaces)
				return;
			else if (!gap.IsEmpty)
				result.Append(gap);
			else if (betweenSentences && result.Length > 0)
				result.Append(' ');
		}
	}
}
