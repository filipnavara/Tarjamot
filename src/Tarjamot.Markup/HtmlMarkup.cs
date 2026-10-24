// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy
// of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from bergamot-translator's src/translator/html.{h,cpp}.

using System.Text;

namespace Tarjamot.Markup;

/// <summary>Bergamot's Response for markup: source and target text with soft alignments.</summary>
/// <param name="Alignments">Per sentence, per target token (including EOS), the probability of each source token (including EOS).</param>
internal sealed record MarkupResponse(AnnotatedText Source, AnnotatedText Target, List<List<float[]>> Alignments);

/// <summary>
/// Port of Bergamot's HTML class (html.cpp), which Firefox uses to translate markup: the HTML is
/// parsed into plain text whose spans of text carry the stack of tags open around them. After
/// translation, each source token takes the tags of the span it falls in, each target token the
/// tags of its hard-aligned source token, and the tags are written back into the translation
/// wherever the stack changes between tokens. Block elements break sentences (a "\n\n" with a
/// whitespace marker), inline elements break words with a space where needed. All text is UTF-8.
/// </summary>
internal sealed class HtmlMarkup
{
	internal enum NodeType { Element, VoidElement, Comment, ProcessingInstruction, Whitespace }

	/// <summary>A tag; stacks compare tags by reference.</summary>
	internal sealed class Tag(NodeType type, byte[] name)
	{
		public NodeType Type { get; } = type;
		public byte[] Name { get; } = name;
		// Raw attributes, each as ` name="value"`.
		public List<byte> Attributes { get; } = [];
		// Raw content copied as is, e.g. of <script> or a comment.
		public byte[] Data { get; set; } = [];
	}

	/// <summary>A span of plain text and the tags applied to it; empty for void elements and the like.</summary>
	internal readonly record struct Span(int Begin, int End, Tag[] Tags)
	{
		public int Size => End - Begin;
	}

	static readonly HashSet<string> VoidTags = ["area", "base", "basefont", "bgsound", "br", "col", "embed", "frame", "hr",
		"img", "input", "keygen", "link", "meta", "param", "source", "track", "wbr"];

	// Elements that do not break sentences: Bergamot's (html.h), and the other inline elements that
	// are common in email (<font> above all).
	static readonly HashSet<string> InlineTags = ["abbr", "a", "b", "em", "i", "kbd", "mark", "math",
		"output", "q", "ruby", "small", "span", "strong", "sub", "sup", "time", "u", "var", "wbr", "ins", "del", "img",
		"bdi", "bdo", "big", "cite", "data", "dfn", "font", "label", "s", "strike", "tt"];

	// Elements that do not break words either.
	static readonly HashSet<string> InWordTags = ["wbr"];

	// Elements copied as they are, like void elements, but parsed because they can be nested.
	static readonly HashSet<string> IgnoredTags = ["code", "kbd", "samp", "var", "dir", "acronym", "math"];

	// A token starting with one of these, or following one, is not a continuation of a word.
	static readonly byte[] ContinuationDelimiters = "\n ,.(){}[]"u8.ToArray();

	readonly List<Span> spans = [];

	/// <summary>Parses <paramref name="html"/> and returns its plain text in <paramref name="plainText"/>.</summary>
	public HtmlMarkup(byte[] html, out byte[] plainText)
	{
		var scanner = new HtmlScanner(html);
		var source = new List<byte>();
		Tag? tag = null;
		var stack = new List<Tag>();
		bool addSentenceBreak = false;
		bool addWordBreak = false;

		// Starting point: an empty span with no open tags.
		spans.Add(new Span(0, 0, []));

		bool stop = false;
		while (!stop)
		{
			switch (scanner.Next())
			{
				case HtmlScanner.TokenType.Error:
					throw new FormatException("HTML parse error.");

				case HtmlScanner.TokenType.Eof:
					stop = true;
					break;

				case HtmlScanner.TokenType.Text:
				{
					// Text after the open or close tag of a block element is a new sentence.
					if (addSentenceBreak)
					{
						if (source.Count >= 2 && !(source[^2] == (byte)'\n' && source[^1] == (byte)'\n'))
						{
							// An empty span positioned before the "\n\n", so it can be removed again.
							stack.Add(new Tag(NodeType.Whitespace, []));
							spans.Add(new Span(source.Count, source.Count, [.. stack]));
							source.Add((byte)'\n');
							source.Add((byte)'\n');
							stack.RemoveAt(stack.Count - 1);
						}
						addSentenceBreak = false;
					}

					// Text after an inline tag gets a space if it would otherwise continue a word.
					if (addWordBreak)
					{
						if (IsContinuation(source.Count > 0 ? source[^1] : null, scanner.Value))
							source.Add((byte)' ');
						addWordBreak = false;
					}

					int begin = source.Count;
					AppendText(source, scanner.Value);
					spans.Add(new Span(begin, source.Count, [.. stack]));
					break;
				}

				case HtmlScanner.TokenType.TagStart:
				{
					var name = Lower(scanner.Tag);
					tag = new Tag(VoidTags.Contains(name) ? NodeType.VoidElement : NodeType.Element, scanner.Tag.ToArray());
					stack.Add(tag);

					// Void elements apply to no text: an empty span, and off the stack again.
					if (tag.Type == NodeType.VoidElement)
					{
						spans.Add(new Span(source.Count, source.Count, [.. stack]));
						stack.RemoveAt(stack.Count - 1);
					}

					// Ignored elements move with the content like void elements.
					if (IgnoredTags.Contains(name))
					{
						ConsumeIgnoredTag(scanner, tag, name);
						spans.Add(new Span(source.Count, source.Count, [.. stack]));
						stack.RemoveAt(stack.Count - 1);
					}

					if (!InlineTags.Contains(name))
						addSentenceBreak = true;
					else if (!InWordTags.Contains(name))
						addWordBreak = true;
					break;
				}

				case HtmlScanner.TokenType.TagEnd:
				{
					var name = Lower(scanner.Tag);
					// The "/>" of a void element.
					if (VoidTags.Contains(name))
						break;
					if (stack.Count == 0)
						throw new FormatException($"More closing tags than opening tags: </{Encoding.UTF8.GetString(scanner.Tag)}>.");
					if (Lower(stack[^1].Name) != name)
						throw new FormatException($"Unexpected closing tag </{Encoding.UTF8.GetString(scanner.Tag)}>.");

					// An element closed right away ("<u></u>") is recorded with an empty span.
					if (spans.Count == 0 || !spans[^1].Tags.Contains(stack[^1]))
						spans.Add(new Span(source.Count, source.Count, [.. stack]));
					stack.RemoveAt(stack.Count - 1);

					if (!InlineTags.Contains(name))
						addSentenceBreak = true;
					else if (!InWordTags.Contains(name))
						addWordBreak = true;
					break;
				}

				case HtmlScanner.TokenType.Attribute:
					AppendAttribute(tag!, scanner);
					break;

				case HtmlScanner.TokenType.CommentStart:
					tag = new Tag(NodeType.Comment, []);
					stack.Add(tag);
					spans.Add(new Span(source.Count, source.Count, [.. stack]));
					stack.RemoveAt(stack.Count - 1);
					break;

				case HtmlScanner.TokenType.ProcessingInstructionStart:
					tag = new Tag(NodeType.ProcessingInstruction, []);
					stack.Add(tag);
					spans.Add(new Span(source.Count, source.Count, [.. stack]));
					stack.RemoveAt(stack.Count - 1);
					break;

				case HtmlScanner.TokenType.CommentEnd:
				case HtmlScanner.TokenType.ProcessingInstructionEnd:
					tag = null;
					break;

				case HtmlScanner.TokenType.Data:
					tag!.Data = scanner.Value.ToArray();
					break;

				default:
					throw new FormatException("Unsupported scanner token type.");
			}
		}

		if (stack.Count > 0)
			throw new FormatException("Not all tags were closed.");

		// A trailing empty span with all tags closed.
		spans.Add(new Span(source.Count, source.Count, [.. stack]));
		plainText = source.ToArray();
	}

	/// <summary>Writes the markup into <c>response.Target</c> and returns the resulting text.</summary>
	public byte[] Restore(MarkupResponse response)
	{
		var sourceTokenSpans = new List<int>();
		RestoreSource(response.Source, sourceTokenSpans);
		var alignments = HardAlignments(response, sourceTokenSpans);
		var targetTokenSpans = CopyTagStack(response, alignments, sourceTokenSpans);
		var targetTokenTags = targetTokenSpans.Select(span => spans[span].Tags).ToList();
		return RestoreTarget(response.Target, targetTokenSpans, targetTokenTags).Text;
	}

	// Assigns each source token (and gap) the span it falls in: the last span that overlaps it.
	void RestoreSource(AnnotatedText source, List<int> sourceTokenSpans)
	{
		int span = 0;
		int previous = 0; // the first span is always empty and has no tags
		source.Apply((range, token, last) =>
		{
			var formatter = new TokenFormatter(token);
			while (true)
			{
				formatter.Append(spans[previous].Tags, spans[span].Tags);
				previous = span;
				if (span + 1 != spans.Count && (spans[span + 1].Begin < range.End || last))
				{
					span++;
					continue;
				}
				break;
			}
			// Only the tags of the last span, not of the ones in between (as in Bergamot).
			sourceTokenSpans.Add(previous);
			return formatter.Html;
		});
	}

	AnnotatedText RestoreTarget(AnnotatedText target, List<int> targetTokenSpans, List<Tag[]> targetTokenTags)
	{
		var previousTags = spans[0].Tags;
		int straggler = 0;
		int index = 0;
		var assigned = targetTokenSpans.ToHashSet();

		var result = target.Apply((range, token, last) =>
		{
			var formatter = new TokenFormatter(token);

			// Catch up with the span of this token, writing out empty spans (void elements and
			// the like) and spans that would otherwise be lost because no token aligned with them.
			for (; straggler < targetTokenSpans[index]; straggler++)
			{
				if (spans[straggler].Size != 0 && assigned.Contains(straggler))
					continue;
				formatter.Append(previousTags, spans[straggler].Tags);
				previousTags = spans[straggler].Tags;
			}

			formatter.Append(previousTags, targetTokenTags[index]);
			// The last token closes all open tags.
			if (last)
				formatter.Append(targetTokenTags[index], []);

			previousTags = targetTokenTags[index];
			index++;
			return formatter.Html;
		});
		return result;
	}

	// The source span of every target token, and of the gaps, in the order of AnnotatedText.Apply.
	List<int> CopyTagStack(MarkupResponse response, List<List<int>> alignments, List<int> sourceTokenSpans)
	{
		var targetTokenSpans = new List<int>();
		int offset = 0;
		for (int sentence = 0; sentence < response.Target.NumSentences; sentence++)
		{
			targetTokenSpans.Add(sourceTokenSpans[offset]); // the gap before the sentence
			for (int t = 0; t < response.Target.NumWords(sentence); t++)
				targetTokenSpans.Add(sourceTokenSpans[offset + 1 + alignments[sentence][t]]);
			offset += response.Source.NumWords(sentence) + 1;
		}
		targetTokenSpans.Add(sourceTokenSpans[offset]); // the final gap
		return targetTokenSpans;
	}

	/// <summary>
	/// Picks the most probable source token for every target token. All tokens of a target word
	/// take the source token of its token with more markup or the highest probability, and EOS
	/// aligns with EOS.
	/// </summary>
	List<List<int>> HardAlignments(MarkupResponse response, List<int> sourceTokenSpans)
	{
		var result = new List<List<int>>();
		int offset = 0;
		for (int sentence = 0; sentence < response.Target.NumSentences; sentence++)
		{
			var hard = new List<int>();
			var soft = response.Alignments[sentence];
			int words = response.Target.NumWords(sentence);

			for (int t = 0; t + 1 < words; t++)
				hard.Add(ArgMax(soft[t]));

			for (int t = 1; t + 1 < words; t++)
			{
				if (!IsContinuation(response.Target.Word(sentence, t - 1), response.Target.Word(sentence, t)))
					continue;
				int current = hard[t];
				int previous = hard[t - 1];
				float currentScore = soft[t][current];
				float previousScore = soft[t - 1][previous];
				var currentTags = spans[sourceTokenSpans[offset + 1 + current]].Tags;
				var previousTags = spans[sourceTokenSpans[offset + 1 + previous]].Tags;
				if (Extends(currentTags, previousTags) || currentScore >= previousScore)
				{
					// Applies to all previous tokens of the word.
					for (int i = t; ; i--)
					{
						hard[i] = current;
						if (i == 0 || !IsContinuation(response.Target.Word(sentence, i - 1), response.Target.Word(sentence, i)))
							break;
					}
				}
				else
				{
					hard[t] = previous;
				}
			}

			hard.Add(response.Source.NumWords(sentence) - 1);
			result.Add(hard);
			offset += response.Source.NumWords(sentence) + 1;
		}
		return result;

		// std::max_element: the first maximum.
		static int ArgMax(float[] values)
		{
			int best = 0;
			for (int i = 1; i < values.Length; i++)
			{
				if (values[best] < values[i])
					best = i;
			}
			return best;
		}
	}

	static bool IsContinuation(byte? previous, ReadOnlySpan<byte> current) =>
		previous is { } last && !current.IsEmpty && !ContinuationDelimiters.Contains(current[0]) && !ContinuationDelimiters.Contains(last);

	static bool IsContinuation(byte[] previous, byte[] current) =>
		IsContinuation(previous.Length > 0 ? previous[^1] : null, current);

	// Whether stack b is stack a with possibly more tags nested inside.
	static bool Extends(Tag[] b, Tag[] a)
	{
		if (a.Length > b.Length)
			return false;
		for (int i = 0; i < a.Length; i++)
		{
			if (a[i] != b[i])
				return false;
		}
		return true;
	}

	// A non-breaking space becomes a space, as "&nbsp;" does: Firefox gets the HTML from innerHTML,
	// which writes every U+00A0 as "&nbsp;".
	static void AppendText(List<byte> source, ReadOnlySpan<byte> text)
	{
		int nbsp;
		while ((nbsp = text.IndexOf("\u00A0"u8)) >= 0)
		{
			source.AddRange(text[..nbsp]);
			source.Add((byte)' ');
			text = text[(nbsp + 2)..];
		}
		source.AddRange(text);
	}

	static string Lower(ReadOnlySpan<byte> name)
	{
		var lower = new byte[name.Length];
		for (int i = 0; i < name.Length; i++)
			lower[i] = HtmlScanner.ToLower(name[i]);
		return Encoding.UTF8.GetString(lower);
	}

	static void AppendAttribute(Tag tag, HtmlScanner scanner)
	{
		tag.Attributes.Add((byte)' ');
		tag.Attributes.AddRange(scanner.Attribute);
		tag.Attributes.AddRange("=\""u8);
		tag.Attributes.AddRange(scanner.Value);
		tag.Attributes.Add((byte)'"');
	}

	// Consumes an ignored element as a whole, counting nested elements of the same name. The
	// content between the open and the final close tag becomes the tag's raw data.
	static void ConsumeIgnoredTag(HtmlScanner scanner, Tag tag, string name)
	{
		HtmlScanner.TokenType token;
		int inside = 0;

		// The rest of the open tag: its attributes.
		do
		{
			token = scanner.Next();
			switch (token)
			{
				case HtmlScanner.TokenType.Error:
					throw new FormatException("HTML parse error.");
				case HtmlScanner.TokenType.Eof:
					throw new FormatException($"Did not find closing tag </{name}>.");
				case HtmlScanner.TokenType.Attribute:
					AppendAttribute(tag, scanner);
					break;
				default:
					inside++;
					break;
			}
		}
		while (inside == 0);

		int start = scanner.Start;
		while (inside > 0)
		{
			switch (token)
			{
				case HtmlScanner.TokenType.Error:
					throw new FormatException("HTML parse error.");
				case HtmlScanner.TokenType.Eof:
					throw new FormatException($"Did not find closing tag </{name}>.");
				case HtmlScanner.TokenType.TagStart:
					if (Lower(scanner.Tag) == name)
						inside++;
					break;
				case HtmlScanner.TokenType.TagEnd:
					if (Lower(scanner.Tag) == name)
						inside--;
					break;
			}
			if (inside > 0)
				token = scanner.Next();
		}
		tag.Data = scanner.Input[start..scanner.Start];
	}

	/// <summary>
	/// Builds the HTML of one token: the token with &amp;, &lt; and &gt; encoded, and the tags that
	/// get from one stack to the next. Closing tags go before the token's leading whitespace,
	/// opening tags after it (Bergamot's TokenFormatter).
	/// </summary>
	sealed class TokenFormatter
	{
		static readonly byte[] TagStart = "<"u8.ToArray(), CloseStart = "</"u8.ToArray(), TagEnd = ">"u8.ToArray();
		static readonly byte[] CommentStart = "<!--"u8.ToArray(), CommentEnd = "-->"u8.ToArray();
		static readonly byte[] InstructionStart = "<?"u8.ToArray(), InstructionEnd = "?>"u8.ToArray();

		readonly List<byte> html = [];
		int offset;
		int whitespaceOffset;
		int whitespaceSize;
		// Close tags go left of the whitespace until the first open tag has been written.
		bool closeLeft = true;

		public TokenFormatter(byte[] token)
		{
			while (whitespaceSize < token.Length && IsSpace(token[whitespaceSize]))
				whitespaceSize++;
			foreach (var b in token)
			{
				switch (b)
				{
					case (byte)'&':
						html.AddRange("&amp;"u8);
						break;
					case (byte)'<':
						html.AddRange("&lt;"u8);
						break;
					case (byte)'>':
						html.AddRange("&gt;"u8);
						break;
					default:
						html.Add(b);
						break;
				}
			}
		}

		public byte[] Html => html.ToArray();

		public void Append(Tag[] previous, Tag[] current)
		{
			int i = 0;
			while (i < previous.Length && i < current.Length && previous[i] == current[i])
				i++;

			// Only elements have content and so a closing tag.
			for (int j = previous.Length - 1; j >= i; j--)
			{
				var tag = previous[j];
				if (tag.Type != NodeType.Element)
					continue;
				var closeTag = Concat(CloseStart, tag.Name, TagEnd);
				html.InsertRange(offset + (closeLeft ? 0 : whitespaceSize), closeTag);
				offset += closeTag.Length;
				if (closeLeft)
					whitespaceOffset += closeTag.Length;
			}

			for (int j = i; j < current.Length; j++)
			{
				var tag = current[j];
				byte[] openTag;
				switch (tag.Type)
				{
					case NodeType.Element:
					case NodeType.VoidElement:
						openTag = Concat(TagStart, tag.Name, tag.Attributes.ToArray(), TagEnd, tag.Data);
						break;
					case NodeType.Comment:
						openTag = Concat(CommentStart, tag.Data, CommentEnd);
						break;
					case NodeType.ProcessingInstruction:
						openTag = Concat(InstructionStart, tag.Data, InstructionEnd);
						break;
					default:
					{
						// A sentence break: try to eat its "\n\n" from the token's whitespace.
						openTag = [];
						int position = IndexOf(html, "\n\n"u8, whitespaceOffset);
						if (position >= 0 && position < whitespaceOffset + whitespaceSize)
						{
							html.RemoveRange(position, 2);
							whitespaceSize -= 2;
						}
						break;
					}
				}
				html.InsertRange(offset + whitespaceSize, openTag);
				offset += openTag.Length;
				closeLeft = closeLeft && openTag.Length == 0;
			}
		}

		// std::isspace in the "C" locale.
		static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r';

		static int IndexOf(List<byte> bytes, ReadOnlySpan<byte> value, int start)
		{
			for (int i = start; i + value.Length <= bytes.Count; i++)
			{
				bool match = true;
				for (int j = 0; j < value.Length && match; j++)
					match = bytes[i + j] == value[j];
				if (match)
					return i;
			}
			return -1;
		}

		static byte[] Concat(params ReadOnlySpan<byte[]> parts)
		{
			var result = new List<byte>();
			foreach (var part in parts)
				result.AddRange(part);
			return result.ToArray();
		}
	}
}
