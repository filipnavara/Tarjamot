using Tarjamot.Engine;
using NUnit.Framework;

namespace Tarjamot.Tests;

/// <summary>
/// Whitespace between translated sentences, as Bergamot's AnnotatedText produces it. The cases
/// were checked against Firefox's WASM build.
/// </summary>
[TestFixture]
public class TextTranslationTest
{
	[TestCase(false)]
	[TestCase(true)]
	public void PreparationTokenizesEachCandidateOnceAndKeepsMixedTextRanges(bool splitSentences)
	{
		string[] texts = ["  First. Second! \n\u200B\nThird?  ", " \t\n", "\u00AD\n Last."];
		var candidates = texts.SelectMany(text =>
			(splitSentences ? SentenceSplitter.SplitRanges(text) : SentenceSplitter.SplitLines(text)).Select(range => text[range])).ToArray();
		var calls = new List<string>();
		var expectedTokens = new List<int>();
		var retainedLists = new List<List<int>>();
		var input = TextTranslation.PrepareInput(texts, Tokenize, splitSentences);
		Assert.That(calls, Is.EqualTo(candidates));
		Assert.That(input.Ranges[1], Is.Empty);
		Assert.That(input.Sentences.Select(sentence => sentence.Tokens[0]), Is.EqualTo(expectedTokens));
		for (int i = 0; i < input.Sentences.Count; i++)
		{
			Assert.That(input.Sentences[i].Tokens, Is.SameAs(retainedLists[i]));
			Assert.That(input.Sentences[i].Tokens, Has.Count.EqualTo(2));
			Assert.That(input.Sentences[i].Tokens[^1], Is.EqualTo(99));
		}
		Assert.That(texts.SelectMany((text, i) => input.Ranges[i].Select(range => text[range])),
			Is.EqualTo(candidates.Where(candidate => candidate is not "\u200B" and not "\u00AD")));

		TokenizedSentence Tokenize(string sentence)
		{
			calls.Add(sentence);
			var ids = new List<int>();
			if (sentence is not "\u200B" and not "\u00AD")
			{
				ids.Add(calls.Count);
				expectedTokens.Add(calls.Count);
				retainedLists.Add(ids);
			}
			return new TokenizedSentence(ids, 99);
		}
	}

	[Test]
	public void EmptyCandidateStatusIsCapturedBeforeEosAndRequestsOwnTheirTokens()
	{
		var empty = new TokenizedSentence([], 99);
		Assert.That(empty.HasTokens, Is.False);
		Assert.That(empty.Tokens, Is.EqualTo(new[] { 99 }));
		var first = TextTranslation.PrepareInput(["A."], _ => new TokenizedSentence([7], 99), true);
		var second = TextTranslation.PrepareInput(["A."], _ => new TokenizedSentence([7], 99), true);
		Assert.That(first.Sentences[0].Tokens, Is.Not.SameAs(second.Sentences[0].Tokens));
	}

	[TestCase("  A.\n   B?  ", "en", "  a.\n   b?  ")]
	[TestCase("A.\n\nB.", "en", "a.\n\nb.")]
	[TestCase("A.  B.", "de", "a.  b.")]
	[TestCase("A.B.", "en", "a. b.")]
	[TestCase("A. B.\nC.  D.", "ja", "ab\ncd")]
	[TestCase("A. B.", "zh-Hans", "ab")]
	public void Assemble(string text, string targetLanguage, string expected)
	{
		var ranges = new List<Range>();
		for (int i = 0; i < text.Length; i++)
		{
			if (char.IsAsciiLetterUpper(text[i]))
			{
				int end = i + 1 < text.Length && text[i + 1] is '.' or '?' ? i + 2 : i + 1;
				ranges.Add(i..end);
			}
		}
		// The fake translation lowercases the sentence; Japanese and Chinese drop the punctuation
		// so that only the separators are visible.
		bool cjk = targetLanguage is "ja" or "zh-Hans";
		var sentences = ranges.Select(range => cjk ? text[range][..1].ToLowerInvariant() : text[range].ToLowerInvariant()).ToArray();
		Assert.That(TextTranslation.Assemble(text, ranges.ToArray(), sentences, targetLanguage), Is.EqualTo(expected));
	}
}
