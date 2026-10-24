// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy
// of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from bergamot-translator's src/translator/annotation.{h,cpp}.

using System.Runtime.InteropServices;

namespace Tarjamot.BergamotMarkup;

/// <summary>
/// Port of Bergamot's AnnotatedText: UTF-8 text divided into gaps and sentences of tokens,
///   gap sentence gap sentence gap,
/// where the tokens are contiguous byte ranges and every gap is one token. A sentence's last
/// token is the empty end-of-sentence token. Offsets are UTF-8 byte offsets.
/// </summary>
internal sealed class AnnotatedText
{
	readonly List<byte> text;
	// Token i is [tokenBegin[i], tokenBegin[i + 1]); padded so that indices stay valid at the end.
	readonly List<int> tokenBegin = [0, 0];
	// Token indices of the gaps; gaps.Count == NumSentences + 1.
	readonly List<int> gaps = [0];
	readonly string? targetLanguage;

	/// <summary>Empty text, built with <see cref="AppendSentence"/> and <see cref="HandleEndingWhitespace"/>.</summary>
	public AnnotatedText(string? targetLanguage)
	{
		text = [];
		this.targetLanguage = targetLanguage;
	}

	/// <summary>Existing text, one gap until <see cref="RecordExistingSentence"/> divides it.</summary>
	public AnnotatedText(ReadOnlySpan<byte> existing, string? targetLanguage)
	{
		text = [.. existing];
		tokenBegin[^1] = existing.Length;
		this.targetLanguage = targetLanguage;
	}

	/// <summary>The text without copying it; valid until the text changes.</summary>
	public ReadOnlySpan<byte> Text => CollectionsMarshal.AsSpan(text);

	public int NumSentences => gaps.Count - 1;

	public int NumWords(int sentence) => gaps[sentence + 1] - gaps[sentence] - 1;

	public (int Begin, int End) WordRange(int sentence, int word)
	{
		int token = gaps[sentence] + 1 + word;
		return (tokenBegin[token], tokenBegin[token + 1]);
	}

	public (int Begin, int End) GapRange(int gap) => (tokenBegin[gaps[gap]], tokenBegin[gaps[gap] + 1]);

	public (int Begin, int End) SentenceRange(int sentence) => (tokenBegin[gaps[sentence] + 1], tokenBegin[gaps[sentence + 1]]);

	public ReadOnlySpan<byte> Slice((int Begin, int End) range) => Text[range.Begin..range.End];

	public ReadOnlySpan<byte> Word(int sentence, int word) => Slice(WordRange(sentence, word));

	public ReadOnlySpan<byte> Gap(int gap) => Slice(GapRange(gap));

	/// <summary>Appends a sentence after the gap <paramref name="prefix"/>; its tokens are contiguous in <paramref name="sentence"/>.</summary>
	public void AppendSentence(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> sentence, IReadOnlyList<(int Begin, int End)> tokens)
	{
		HandleEndingWhitespace(prefix, betweenSentences: true);

		int offset = text.Count;
		foreach (var token in tokens)
		{
			offset += token.End - token.Begin;
			tokenBegin.Add(offset);
		}
		if (tokens.Count > 0)
			text.AddRange(sentence[tokens[0].Begin..tokens[^1].End]);

		// The gap after the sentence, extended by HandleEndingWhitespace or the next AppendSentence.
		gaps.Add(tokenBegin.Count - 1);
		tokenBegin.Add(offset);
	}

	/// <summary>
	/// Appends a gap. A line break is always kept. Japanese and Chinese targets drop the gap but
	/// keep any HTML tags in it; other targets keep it, and get a space between sentences that had
	/// none (see AnnotatedText::handleEndingWhitespace).
	/// </summary>
	public void HandleEndingWhitespace(ReadOnlySpan<byte> gap, bool betweenSentences)
	{
		if (gap.Contains((byte)'\n'))
			text.AddRange(gap);
		else if (ShouldOmitSpaceBetweenSentences)
			AppendTagsFromGap(gap);
		else if (!gap.IsEmpty)
			text.AddRange(gap);
		else if (betweenSentences && targetLanguage is { Length: > 0 } && text.Count > 0)
			text.Add((byte)' ');

		tokenBegin[^1] = text.Count;
	}

	/// <summary>
	/// Records a sentence of <paramref name="tokens"/> (ending with the empty EOS token) that is
	/// already in the text, in order.
	/// </summary>
	public void RecordExistingSentence(IReadOnlyList<(int Begin, int End)> tokens, int sentenceBegin)
	{
		tokenBegin.RemoveAt(tokenBegin.Count - 1);
		foreach (var token in tokens)
			tokenBegin.Add(token.Begin);
		gaps.Add(tokenBegin.Count);
		tokenBegin.Add(tokens.Count > 0 ? tokens[^1].End : sentenceBegin);
		tokenBegin.Add(text.Count);
	}

	/// <summary>Writes the replacement of a gap or token (<paramref name="token"/>, at <paramref name="range"/>) to <paramref name="output"/>.</summary>
	public delegate void TokenMap((int Begin, int End) range, ReadOnlySpan<byte> token, bool last, List<byte> output);

	/// <summary>
	/// Rebuilds the text by calling <paramref name="map"/> on every gap and token in order (the
	/// last call is the final gap), as AnnotatedText::apply.
	/// </summary>
	public AnnotatedText Apply(TokenMap map)
	{
		var result = new AnnotatedText(targetLanguage);
		// Reused for every sentence: the gap before it, its tokens and their ranges in the content.
		var prefix = new List<byte>();
		var content = new List<byte>();
		var tokens = new List<(int Begin, int End)>();
		for (int sentence = 0; sentence < NumSentences; sentence++)
		{
			prefix.Clear();
			map(GapRange(sentence), Gap(sentence), false, prefix);
			content.Clear();
			tokens.Clear();
			for (int word = 0; word < NumWords(sentence); word++)
			{
				int begin = content.Count;
				map(WordRange(sentence, word), Word(sentence, word), false, content);
				tokens.Add((begin, content.Count));
			}
			result.AppendSentence(CollectionsMarshal.AsSpan(prefix), CollectionsMarshal.AsSpan(content), tokens);
		}
		prefix.Clear();
		map(GapRange(NumSentences), Gap(NumSentences), true, prefix);
		result.HandleEndingWhitespace(CollectionsMarshal.AsSpan(prefix), betweenSentences: false);
		return result;
	}

	bool ShouldOmitSpaceBetweenSentences =>
		targetLanguage is { Length: > 0 } && (targetLanguage.StartsWith("ja", StringComparison.Ordinal) || targetLanguage.StartsWith("zh", StringComparison.Ordinal));

	// The gap is whitespace, HTML tags or both; only the tags are kept.
	void AppendTagsFromGap(ReadOnlySpan<byte> gap)
	{
		int current = 0;
		while (current < gap.Length)
		{
			int tagStart = gap[current..].IndexOf((byte)'<');
			if (tagStart < 0)
				return;
			tagStart += current;
			int tagEnd = gap[(tagStart + 1)..].IndexOf((byte)'>');
			if (tagEnd < 0)
				return;
			tagEnd += tagStart + 1;
			text.AddRange(gap[tagStart..(tagEnd + 1)]);
			current = tagEnd + 1;
		}
	}
}
