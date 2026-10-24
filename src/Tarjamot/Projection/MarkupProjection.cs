using System.Text;
using Tarjamot.Engine;

namespace Tarjamot.Projection;

/// <summary>
/// The algorithm of <see cref="ProjectedMarkupTranslator"/>. A fragment is divided into structure
/// (block elements, &lt;br&gt; and the like), copied as written, and runs of inline content
/// between it. A run is translated as plain text, sentence by sentence, and its inline elements
/// are projected onto the translation: each element takes the target tokens whose hard alignment
/// (the most probable source token) falls inside it, the best contiguous group of them, widened
/// to whole words. Children are kept inside their parents and siblings in order, so the result
/// is well-formed. With two models the alignments are composed through the English text.
/// </summary>
internal static partial class MarkupProjection
{
	[System.Text.RegularExpressions.GeneratedRegex(@"\sid\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
	private static partial System.Text.RegularExpressions.Regex IdAttribute();

	// Phrasing elements that format text: they are projected when they contain only text and
	// other inline content, and are structure otherwise.
	static readonly HashSet<string> InlineElements = ["a", "abbr", "acronym", "b", "bdi", "bdo", "big", "cite", "data", "del",
		"dfn", "em", "font", "i", "img", "ins", "label", "mark", "nobr", "q", "s", "small", "span", "strike", "strong", "sub",
		"sup", "time", "tt", "u", "wbr"];

	// Elements whose content is not translated: copied as written, in place within a run.
	static readonly HashSet<string> OpaqueElements = ["audio", "canvas", "code", "iframe", "kbd", "math", "noscript", "object",
		"samp", "script", "style", "svg", "template", "textarea", "var", "video"];

	/// <summary>An inline element of a run, or an anchor: markup without text, which takes a position.</summary>
	sealed class Item
	{
		public required string Open { get; set; }
		public string? Close { get; set; }
		// The whole element as written, for one that becomes an anchor.
		public string Source { get; init; } = "";
		public int Parent { get; init; }
		public int SourceStart { get; init; }
		public int SourceEnd { get; set; }
		public int TargetStart { get; set; }
		public int TargetEnd { get; set; }
		public bool IsAnchor => Close is null;
	}

	sealed class Run
	{
		public required string Text { get; init; }
		public required List<Item> Items { get; init; }
		public List<Sentence> Sentences { get; } = [];
		// Gaps between sentences: source range and where they went in the target.
		public List<(int SourceStart, int SourceEnd, int TargetStart, int TargetLength)> Gaps { get; } = [];
		public string Target { get; set; } = "";
	}

	sealed class Sentence
	{
		public required Run Run { get; init; }
		public required Range Range { get; init; }
		// Source tokens in run coordinates, without EOS.
		public required List<(int Begin, int End)> SourceTokens { get; init; }
		public required List<int> Ids { get; init; }
		public string Target { get; set; } = "";
		// Target tokens in sentence coordinates, without EOS.
		public List<(int Begin, int End)> TargetTokens { get; set; } = [];
		// Per target token, the probability of each source token and EOS.
		public float[][] Alignment { get; set; } = [];
		public int TargetOffset { get; set; }
	}

	public static string[] Translate(IReadOnlyList<string> fragments, IReadOnlyList<TranslationModel> models, CancellationToken cancellationToken)
	{
		var documents = fragments.Select(Divide).ToArray();
		var runs = documents.SelectMany(pieces => pieces.OfType<Run>()).ToList();
		var sentences = Segment(runs, models[0].Engine);
		TranslateSentences(sentences, models, cancellationToken);
		bool omitSpaces = TextTranslation.OmitsSpacesBetweenSentences(models[^1].TargetLanguage);
		foreach (var run in runs)
		{
			Assemble(run, omitSpaces);
			Project(run);
		}
		return documents.Select(pieces => string.Concat(pieces.Select(piece => piece as string ?? Render((Run)piece)))).ToArray();
	}

	// The fragment as markup copied as written (strings) and runs to translate.
	static List<object> Divide(string html)
	{
		var pieces = new List<object>();
		Walk(HtmlFragmentParser.Parse(html));
		return pieces;

		void Walk(List<HtmlNode> nodes)
		{
			var run = new List<HtmlNode>();
			foreach (var node in nodes)
			{
				if (node is HtmlElement element && !IsInline(element))
				{
					Flush(run);
					pieces.Add(html[element.Start..element.StartTagEnd]);
					Walk(element.Children);
					pieces.Add(html[element.EndTagStart..element.End]);
				}
				else
				{
					run.Add(node);
				}
			}
			Flush(run);
		}

		void Flush(List<HtmlNode> nodes)
		{
			if (nodes.Count == 0)
				return;
			pieces.Add((object?)CreateRun(html, nodes) ?? html[nodes[0].Start..nodes[^1].End]);
			nodes.Clear();
		}
	}

	static bool IsOpaque(HtmlElement element) => OpaqueElements.Contains(element.Name);

	static bool IsInline(HtmlElement element) =>
		IsOpaque(element) || (InlineElements.Contains(element.Name) && element.Children.All(child => child is not HtmlElement inner || IsInline(inner)));

	// The plain text of a run of inline nodes and its elements, or null if it has nothing to translate.
	static Run? CreateRun(string html, List<HtmlNode> nodes)
	{
		var text = new StringBuilder();
		var items = new List<Item>();
		foreach (var node in nodes)
			Add(node, -1);
		var plain = text.ToString();
		if (!plain.Any(char.IsLetterOrDigit))
			return null;
		return new Run { Text = plain, Items = items };

		void Add(HtmlNode node, int parent)
		{
			switch (node)
			{
				case HtmlText textNode:
					// A non-breaking space is a space to the model.
					text.Append(textNode.Text.Replace('\u00A0', ' '));
					break;
				case HtmlElement element when !IsOpaque(element) && !element.IsEmpty && element.Children.Count > 0:
					int index = items.Count;
					items.Add(new Item
					{
						Open = html[element.Start..element.StartTagEnd],
						Close = html[element.EndTagStart..element.End],
						Source = html[element.Start..element.End],
						Parent = parent,
						SourceStart = text.Length,
					});
					foreach (var child in element.Children)
						Add(child, index);
					items[index].SourceEnd = text.Length;
					break;
				default:
					items.Add(new Item { Open = html[node.Start..node.End], Parent = parent, SourceStart = text.Length, SourceEnd = text.Length });
					break;
			}
		}
	}

	// Splits the runs into sentences (as in plain text translation) and tokenizes them with the first model.
	static List<Sentence> Segment(List<Run> runs, MarianEngine engine)
	{
		var sentences = new List<Sentence>();
		foreach (var run in runs)
		{
			foreach (var range in SentenceSplitter.SplitRanges(run.Text))
			{
				int offset = range.Start.Value;
				var (ids, tokens) = Encode(engine.SourceVocab, run.Text[range]);
				if (ids.Count == 0)
					continue;
				var sentence = new Sentence
				{
					Run = run,
					Range = range,
					SourceTokens = tokens.Select(token => (token.Begin + offset, token.End + offset)).ToList(),
					Ids = [.. ids, engine.SourceVocab.EosId],
				};
				run.Sentences.Add(sentence);
				sentences.Add(sentence);
			}
		}
		return sentences;
	}

	static void TranslateSentences(List<Sentence> sentences, IReadOnlyList<TranslationModel> models, CancellationToken cancellationToken)
	{
		if (sentences.Count == 0)
			return;
		var engine = models[0].Engine;
		var outputs = Translate(engine, sentences.Select(sentence => sentence.Ids).ToList(), out var alignments, cancellationToken);
		for (int i = 0; i < sentences.Count; i++)
		{
			(sentences[i].Target, sentences[i].TargetTokens) = Decode(engine.TargetVocab, outputs[i]);
			sentences[i].Alignment = alignments[i];
		}

		// Through English: the next model translates the English sentences, and the alignment of
		// its English tokens is carried over to the first model's English tokens by their overlap.
		for (int leg = 1; leg < models.Count; leg++)
		{
			engine = models[leg].Engine;
			var pivotTokens = new List<(int Begin, int End)>[sentences.Count];
			var sources = new List<List<int>>(sentences.Count);
			for (int i = 0; i < sentences.Count; i++)
			{
				var (ids, tokens) = Encode(engine.SourceVocab, sentences[i].Target);
				pivotTokens[i] = tokens;
				sources.Add([.. ids, engine.SourceVocab.EosId]);
			}
			outputs = Translate(engine, sources, out alignments, cancellationToken);
			for (int i = 0; i < sentences.Count; i++)
			{
				var sentence = sentences[i];
				sentence.Alignment = Compose(sentence.Alignment, sentence.TargetTokens, pivotTokens[i], alignments[i], sentence.Ids.Count);
				(sentence.Target, sentence.TargetTokens) = Decode(engine.TargetVocab, outputs[i]);
			}
		}
	}

	// The target ids of each source, and per emitted token (without EOS) its soft alignment.
	static List<int>[] Translate(MarianEngine engine, List<List<int>> sources, out float[][][] alignments, CancellationToken cancellationToken)
	{
		var soft = sources.Select(_ => new List<float[]>()).ToArray();
		var outputs = engine.TranslateTokens(sources, soft, cancellationToken);
		alignments = outputs.Select((ids, i) => soft[i].Take(ids.Count).ToArray()).ToArray();
		return outputs;
	}

	// p(source | target) = sum over the second model's English tokens q of p(q | target) times the
	// first model's p(source | q'), averaged over the first model's English tokens q' by how many
	// of q's characters they share.
	static float[][] Compose(float[][] first, List<(int Begin, int End)> firstPivots, List<(int Begin, int End)> secondPivots, float[][] second, int sourceCount)
	{
		var byPivot = new float[secondPivots.Count][];
		for (int q = 0; q < secondPivots.Count; q++)
		{
			byPivot[q] = new float[sourceCount];
			var (begin, end) = secondPivots[q];
			if (end <= begin)
				continue;
			for (int p = 0; p < firstPivots.Count; p++)
			{
				int overlap = Math.Min(end, firstPivots[p].End) - Math.Max(begin, firstPivots[p].Begin);
				if (overlap <= 0)
					continue;
				float weight = (float)overlap / (end - begin);
				for (int s = 0; s < sourceCount; s++)
					byPivot[q][s] += weight * first[p][s];
			}
		}

		var result = new float[second.Length][];
		for (int t = 0; t < second.Length; t++)
		{
			result[t] = new float[sourceCount];
			for (int q = 0; q < secondPivots.Count; q++)
			{
				float weight = second[t][q];
				for (int s = 0; s < sourceCount; s++)
					result[t][s] += weight * byPivot[q][s];
			}
		}
		return result;
	}

	static (List<int> Ids, List<(int Begin, int End)> Tokens) Encode(SentencePieceVocab vocab, string text)
	{
		var byteRanges = new List<(int Begin, int End)>();
		var ids = vocab.Encode(Encoding.UTF8.GetBytes(text), byteRanges);
		var chars = CharIndexOfBytes(text);
		return (ids, byteRanges.Select(range => (chars[range.Begin], chars[range.End])).ToList());
	}

	static (string Text, List<(int Begin, int End)> Tokens) Decode(SentencePieceVocab vocab, List<int> ids)
	{
		var byteRanges = new List<(int Begin, int End)>();
		var text = Encoding.UTF8.GetString(vocab.DecodeWithRanges(ids, byteRanges));
		var chars = CharIndexOfBytes(text);
		return (text, byteRanges.Select(range => (chars[range.Begin], chars[range.End])).ToList());
	}

	// The UTF-16 index of the character each UTF-8 byte of the text belongs to, and the length at the end.
	static int[] CharIndexOfBytes(string text)
	{
		var map = new int[Encoding.UTF8.GetByteCount(text) + 1];
		int b = 0;
		for (int c = 0; c < text.Length;)
		{
			System.Text.Rune.DecodeFromUtf16(text.AsSpan(c), out var rune, out int consumed);
			for (int k = 0; k < rune.Utf8SequenceLength; k++)
				map[b++] = c;
			c += consumed;
		}
		map[b] = text.Length;
		return map;
	}

	// The translated text of a run: the sentences with the source text between them, as in plain text translation.
	static void Assemble(Run run, bool omitSpaces)
	{
		var target = new StringBuilder();
		int previousEnd = 0;
		foreach (var sentence in run.Sentences)
		{
			AppendGap(previousEnd, sentence.Range.Start.Value, betweenSentences: true);
			sentence.TargetOffset = target.Length;
			target.Append(sentence.Target);
			previousEnd = sentence.Range.End.Value;
		}
		AppendGap(previousEnd, run.Text.Length, betweenSentences: false);
		run.Target = target.ToString();

		void AppendGap(int start, int end, bool betweenSentences)
		{
			int targetStart = target.Length;
			TextTranslation.AppendGap(target, run.Text.AsSpan(start, end - start), betweenSentences, omitSpaces);
			run.Gaps.Add((start, end, targetStart, target.Length - targetStart));
		}
	}

	readonly record struct Token(Sentence Sentence, int Index, int Begin, int End);

	static void Project(Run run)
	{
		// Source and target tokens of the whole run by their text without surrounding whitespace,
		// and the hard alignment: for each target token, the most probable source token.
		var sourceTokens = new List<Token>();
		var targetTokens = new List<Token>();
		var hard = new List<int>();
		foreach (var sentence in run.Sentences)
		{
			int sourceBase = sourceTokens.Count;
			for (int s = 0; s < sentence.SourceTokens.Count; s++)
			{
				var (begin, end) = Trim(run.Text, sentence.SourceTokens[s]);
				sourceTokens.Add(new Token(sentence, s, begin, end));
			}
			for (int t = 0; t < sentence.TargetTokens.Count; t++)
			{
				var (begin, end) = Trim(sentence.Target, sentence.TargetTokens[t]);
				targetTokens.Add(new Token(sentence, t, begin + sentence.TargetOffset, end + sentence.TargetOffset));
				hard.Add(sourceBase + ArgMax(sentence.Alignment[t], sentence.SourceTokens.Count));
			}
		}

		var copies = new List<Item>();
		foreach (var item in run.Items)
		{
			if (item.IsAnchor)
				continue;
			var covered = new HashSet<int>();
			for (int s = 0; s < sourceTokens.Count; s++)
			{
				if (sourceTokens[s].End > sourceTokens[s].Begin && sourceTokens[s].Begin < item.SourceEnd && sourceTokens[s].End > item.SourceStart)
					covered.Add(s);
			}
			var spans = ProjectElement(item, covered);
			if (spans.Count == 0)
			{
				// Nothing to project (whitespace, images): the element is copied as written, with
				// its children, as an anchor.
				item.Open = item.Source;
				item.Close = null;
				continue;
			}
			(item.TargetStart, item.TargetEnd) = spans[0];
			foreach (var (start, end) in spans.Skip(1))
				copies.Add(new Item { Open = item.Open, Close = item.Close, Parent = item.Parent, SourceStart = item.SourceStart, SourceEnd = item.SourceEnd, TargetStart = start, TargetEnd = end });
		}
		run.Items.AddRange(copies);

		foreach (var item in run.Items)
		{
			if (item.IsAnchor)
				item.TargetStart = item.TargetEnd = ProjectPosition(item.SourceStart);
		}

		// Children stay inside their parents (parents come first).
		foreach (var item in run.Items)
		{
			if (item.Parent < 0)
				continue;
			var parent = run.Items[item.Parent];
			item.TargetStart = Math.Clamp(item.TargetStart, parent.TargetStart, parent.TargetEnd);
			item.TargetEnd = Math.Clamp(item.TargetEnd, item.TargetStart, parent.TargetEnd);
		}

		// Markup that stood between two words, such as "run <code>x</code> now", stays separated
		// from the translated words by spaces.
		foreach (var item in run.Items)
		{
			int position = item.TargetStart;
			if (!item.IsAnchor || item.SourceStart == 0 || item.SourceStart >= run.Text.Length
				|| !char.IsWhiteSpace(run.Text[item.SourceStart - 1]) || !char.IsWhiteSpace(run.Text[item.SourceStart]))
				continue;
			if (position > 0 && IsWordCharacter(run.Target[position - 1]))
				item.Open = " " + item.Open;
			if (position < run.Target.Length && IsWordCharacter(run.Target[position]))
				item.Open += " ";
		}

		// The target spans of an element: each contiguous group of target tokens aligned with it
		// whose mass is at least half of the best group's, the best first. Only elements without
		// children and without an id are copied over several groups (a verb and its separated
		// particle, a word and its translation split around another word).
		List<(int Start, int End)> ProjectElement(Item item, HashSet<int> covered)
		{
			if (covered.Count == 0)
				return [];

			// Mass of the element in each target token of its sentences.
			var mass = new float[targetTokens.Count];
			for (int t = 0; t < targetTokens.Count; t++)
			{
				var token = targetTokens[t];
				foreach (int s in covered)
				{
					if (sourceTokens[s].Sentence == token.Sentence)
						mass[t] += token.Sentence.Alignment[token.Index][sourceTokens[s].Index];
				}
			}

			var aligned = Enumerable.Range(0, targetTokens.Count).Where(t => covered.Contains(hard[t]) && targetTokens[t].End > targetTokens[t].Begin).ToList();
			if (aligned.Count == 0)
			{
				// No target token is aligned with the element: the one it contributes most to.
				int best = -1;
				for (int t = 0; t < targetTokens.Count; t++)
				{
					if (targetTokens[t].End > targetTokens[t].Begin && mass[t] > 0 && (best < 0 || mass[t] > mass[best]))
						best = t;
				}
				if (best < 0)
					return [];
				aligned.Add(best);
			}

			var groups = new List<(int First, int Last, float Mass)>();
			for (int start = 0; start < aligned.Count;)
			{
				int end = start + 1;
				while (end < aligned.Count && aligned[end] == aligned[end - 1] + 1)
					end++;
				groups.Add((aligned[start], aligned[end - 1], aligned[start..end].Sum(t => mass[t])));
				start = end;
			}
			groups.Sort((a, b) => b.Mass.CompareTo(a.Mass));
			bool copy = !run.Items.Any(child => child.Parent == run.Items.IndexOf(item)) && !IdAttribute().IsMatch(item.Open);
			var selected = copy ? groups.Where(group => group.Mass >= groups[0].Mass / 2).ToList() : [groups[0]];

			var spans = new List<(int Start, int End)>();
			foreach (var group in selected)
			{
				if (Span(item, targetTokens[group.First].Begin, targetTokens[group.Last].End) is { } span && !spans.Any(other => other.Start < span.End && span.Start < other.End))
					spans.Add(span);
			}
			if (spans.Count == 0)
				spans.Add((targetTokens[selected[0].First].Begin, targetTokens[selected[0].Last].End));
			return spans;
		}

		// A group of target tokens as a span: an element that starts or ends at a word boundary does
		// so in the translation too, and does not take punctuation at its edges unless it had some.
		(int Start, int End)? Span(Item item, int start, int end)
		{
			if (AtWordBoundary(run.Text, item.SourceStart))
			{
				while (start > 0 && IsWordCharacter(run.Target[start - 1]) && IsWordCharacter(run.Target[start]))
					start--;
			}
			if (AtWordBoundary(run.Text, item.SourceEnd))
			{
				while (end < run.Target.Length && IsWordCharacter(run.Target[end - 1]) && IsWordCharacter(run.Target[end]))
					end++;
			}
			var source = run.Text.AsSpan(item.SourceStart, item.SourceEnd - item.SourceStart).Trim();
			if (source.IsEmpty || !char.IsPunctuation(source[0]))
			{
				while (start < end && (char.IsPunctuation(run.Target[start]) || char.IsWhiteSpace(run.Target[start])))
					start++;
			}
			if (source.IsEmpty || !char.IsPunctuation(source[^1]))
			{
				while (end > start && (char.IsPunctuation(run.Target[end - 1]) || char.IsWhiteSpace(run.Target[end - 1])))
					end--;
			}
			return end > start ? (start, end) : null;
		}

		// Where markup without text goes: before the translation of the next source word when it
		// is inside a sentence, otherwise at the same place in the text between sentences.
		int ProjectPosition(int position)
		{
			foreach (var sentence in run.Sentences)
			{
				var tokens = sourceTokens.Where(token => token.Sentence == sentence && token.End > token.Begin).ToList();
				if (tokens.Count == 0 || position < sentence.Range.Start.Value || position > sentence.Range.End.Value)
					continue;
				if (position <= tokens[0].Begin)
					return sentence.TargetOffset;
				if (position >= tokens[^1].End)
					return sentence.TargetOffset + sentence.Target.Length;
				int next = tokens.FindIndex(token => token.Begin >= position);
				return next >= 0 ? TargetPosition(tokens[next], start: true) : TargetPosition(tokens.Last(token => token.Begin < position), start: false);
			}
			foreach (var (sourceStart, sourceEnd, targetStart, targetLength) in run.Gaps)
			{
				if (position >= sourceStart && position <= sourceEnd)
					return targetStart + Math.Min(position - sourceStart, targetLength);
			}
			return run.Target.Length;
		}

		// The start (or end) of the translation of a source token: the target tokens aligned with
		// it, or the one it contributes most to.
		int TargetPosition(Token source, bool start)
		{
			var sentence = source.Sentence;
			int result = -1;
			int best = -1;
			for (int t = 0; t < targetTokens.Count; t++)
			{
				var token = targetTokens[t];
				if (token.Sentence != sentence || token.End <= token.Begin)
					continue;
				if (sourceTokens[hard[t]].Equals(source))
					result = result < 0 ? (start ? token.Begin : token.End) : start ? Math.Min(result, token.Begin) : Math.Max(result, token.End);
				if (best < 0 || sentence.Alignment[token.Index][source.Index] > sentence.Alignment[targetTokens[best].Index][source.Index])
					best = t;
			}
			if (result >= 0)
				return result;
			return best < 0 ? sentence.TargetOffset : start ? targetTokens[best].Begin : targetTokens[best].End;
		}
	}

	static (int Begin, int End) Trim(string text, (int Begin, int End) range)
	{
		var (begin, end) = range;
		while (begin < end && char.IsWhiteSpace(text[begin]))
			begin++;
		while (end > begin && char.IsWhiteSpace(text[end - 1]))
			end--;
		return (begin, end);
	}

	// The most probable of the first count source tokens (EOS excluded).
	static int ArgMax(float[] probabilities, int count)
	{
		int best = 0;
		for (int i = 1; i < count; i++)
		{
			if (probabilities[i] > probabilities[best])
				best = i;
		}
		return best;
	}

	static bool AtWordBoundary(string text, int position) =>
		position <= 0 || position >= text.Length || !IsWordCharacter(text[position - 1]) || !IsWordCharacter(text[position]);

	// Letters and digits of scripts that separate words with spaces: in Chinese, Japanese or Thai
	// a run of letters is not a word.
	static bool IsWordCharacter(char c) =>
		char.IsLetterOrDigit(c) && c is not (>= '\u0E00' and <= '\u0EFF') and not (>= '\u1000' and <= '\u109F')
			and not (>= '\u1780' and <= '\u17FF') and not (>= '\u3040' and <= '\u30FF') and not (>= '\u3400' and <= '\u4DBF')
			and not (>= '\u4E00' and <= '\u9FFF') and not (>= '\uF900' and <= '\uFAFF') and not (>= '\uFF66' and <= '\uFF9F');

	static string Render(Run run)
	{
		var html = new StringBuilder(run.Target.Length * 2);
		var children = new List<int>[run.Items.Count + 1];
		for (int i = 0; i < children.Length; i++)
			children[i] = [];
		for (int i = 0; i < run.Items.Count; i++)
			children[run.Items[i].Parent + 1].Add(i);
		RenderChildren(-1, 0, run.Target.Length);
		return html.ToString();

		void RenderChildren(int parent, int from, int to)
		{
			int position = from;
			// Siblings in target order; ties keep the source order.
			foreach (int index in children[parent + 1].OrderBy(i => run.Items[i].TargetStart))
			{
				var item = run.Items[index];
				int start = Math.Clamp(item.TargetStart, position, to);
				AppendText(position, start);
				position = start;
				html.Append(item.Open);
				if (item.IsAnchor)
					continue;
				int end = Math.Clamp(item.TargetEnd, start, to);
				RenderChildren(index, start, end);
				html.Append(item.Close);
				position = end;
			}
			AppendText(position, to);
		}

		void AppendText(int start, int end)
		{
			foreach (char c in run.Target.AsSpan(start, end - start))
			{
				switch (c)
				{
					case '&':
						html.Append("&amp;");
						break;
					case '<':
						html.Append("&lt;");
						break;
					case '>':
						html.Append("&gt;");
						break;
					default:
						html.Append(c);
						break;
				}
			}
		}
	}
}
