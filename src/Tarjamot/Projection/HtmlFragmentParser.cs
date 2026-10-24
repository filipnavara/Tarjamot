using System.Net;

namespace Tarjamot.Projection;

/// <summary>A node of an HTML fragment, with its range in the fragment's source.</summary>
internal abstract class HtmlNode
{
	public int Start { get; init; }
	public int End { get; set; }
}

/// <summary>Text, with its character references decoded.</summary>
internal sealed class HtmlText : HtmlNode
{
	public required string Text { get; init; }
}

/// <summary>A comment, a declaration, a processing instruction or the content of a raw text element.</summary>
internal sealed class HtmlOther : HtmlNode;

internal sealed class HtmlElement : HtmlNode
{
	/// <summary>The lower-case tag name.</summary>
	public required string Name { get; init; }

	/// <summary>The end of the start tag.</summary>
	public int StartTagEnd { get; init; }

	/// <summary>The start of the end tag; equal to <see cref="HtmlNode.End"/> for void and self-closed elements.</summary>
	public int EndTagStart { get; set; }

	public List<HtmlNode> Children { get; } = [];

	public bool IsEmpty => EndTagStart == End;
}

/// <summary>
/// Parses a well-formed HTML fragment into a tree that keeps the source range of every node, so
/// that the markup can be copied exactly as it was written. Every element must be closed except
/// void elements and self-closed ones ("&lt;b/&gt;"); the content of raw text elements such as
/// &lt;script&gt; is not parsed. A "&lt;" that does not start markup is text.
/// </summary>
internal static class HtmlFragmentParser
{
	static readonly HashSet<string> VoidElements = ["area", "base", "basefont", "bgsound", "br", "col", "embed", "frame", "hr", "img",
		"input", "keygen", "link", "meta", "param", "source", "track", "wbr"];

	static readonly HashSet<string> RawTextElements = ["script", "style", "textarea", "title", "xmp", "iframe", "noembed",
		"noframes", "noscript"];

	public static List<HtmlNode> Parse(string html)
	{
		var root = new List<HtmlNode>();
		var open = new Stack<HtmlElement>();
		int position = 0;
		while (position < html.Length)
		{
			var nodes = open.Count > 0 ? open.Peek().Children : root;
			int markup = FindMarkup(html, position);
			if (markup > position)
			{
				nodes.Add(new HtmlText { Start = position, End = markup, Text = WebUtility.HtmlDecode(html[position..markup]) });
				position = markup;
				continue;
			}

			switch (html[position + 1])
			{
				case '!' when html.AsSpan(position).StartsWith("<!--"):
				{
					int close = html.IndexOf("-->", position + 4, StringComparison.Ordinal);
					if (close < 0)
						throw new FormatException("Unterminated comment.");
					nodes.Add(new HtmlOther { Start = position, End = close + 3 });
					position = close + 3;
					break;
				}

				case '!':
				case '?':
				{
					int close = html.IndexOf('>', position);
					if (close < 0)
						throw new FormatException("Unterminated declaration.");
					nodes.Add(new HtmlOther { Start = position, End = close + 1 });
					position = close + 1;
					break;
				}

				case '/':
				{
					int nameEnd = NameEnd(html, position + 2);
					var name = html[(position + 2)..nameEnd].ToLowerInvariant();
					int close = html.IndexOf('>', nameEnd);
					if (close < 0 || !html.AsSpan(nameEnd, close - nameEnd).IsWhiteSpace())
						throw new FormatException($"Malformed closing tag </{name}>.");
					if (open.Count == 0)
						throw new FormatException($"More closing tags than opening tags: </{name}>.");
					if (open.Peek().Name != name)
						throw new FormatException($"Unexpected closing tag </{name}>, expected </{open.Peek().Name}>.");
					var element = open.Pop();
					element.EndTagStart = position;
					element.End = close + 1;
					position = close + 1;
					break;
				}

				default:
				{
					int nameEnd = NameEnd(html, position + 1);
					var name = html[(position + 1)..nameEnd].ToLowerInvariant();
					int end = nameEnd;
					bool selfClosed = false;
					while (true)
					{
						if (end >= html.Length)
							throw new FormatException($"Unterminated tag <{name}>.");
						char c = html[end];
						if (c == '>')
						{
							end++;
							break;
						}
						if (c == '/' && end + 1 < html.Length && html[end + 1] == '>')
						{
							selfClosed = true;
							end += 2;
							break;
						}
						if (c is '"' or '\'')
						{
							int quote = html.IndexOf(c, end + 1);
							if (quote < 0)
								throw new FormatException($"Unterminated attribute value in <{name}>.");
							end = quote;
						}
						end++;
					}

					var element = new HtmlElement { Name = name, Start = position, StartTagEnd = end, EndTagStart = end, End = end };
					nodes.Add(element);
					position = end;
					if (selfClosed || VoidElements.Contains(name))
						break;
					if (RawTextElements.Contains(name))
					{
						int close = IndexOfEndTag(html, end, name);
						if (close < 0)
							throw new FormatException($"Did not find closing tag </{name}>.");
						if (close > end)
							element.Children.Add(new HtmlOther { Start = end, End = close });
						position = close;
					}
					open.Push(element);
					break;
				}
			}
		}

		if (open.Count > 0)
			throw new FormatException($"Not all elements were closed: <{open.Peek().Name}>.");
		return root;
	}

	// The next "<" that starts a tag, a comment or a declaration, or the end of the text.
	static int FindMarkup(string html, int start)
	{
		for (int i = html.IndexOf('<', start); i >= 0 && i + 1 < html.Length; i = html.IndexOf('<', i + 1))
		{
			char next = html[i + 1];
			if (char.IsAsciiLetter(next) || next is '!' or '?' || (next == '/' && i + 2 < html.Length && char.IsAsciiLetter(html[i + 2])))
				return i;
		}
		return html.Length;
	}

	static int NameEnd(string html, int start)
	{
		int end = start;
		while (end < html.Length && (char.IsAsciiLetterOrDigit(html[end]) || html[end] is '-' or ':' or '_' or '.'))
			end++;
		return end;
	}

	static int IndexOfEndTag(string html, int start, string name)
	{
		for (int i = html.IndexOf("</", start, StringComparison.Ordinal); i >= 0; i = html.IndexOf("</", i + 2, StringComparison.Ordinal))
		{
			int nameEnd = i + 2 + name.Length;
			if (nameEnd <= html.Length && html.AsSpan(i + 2, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase)
				&& (nameEnd == html.Length || html[nameEnd] is '>' or '/' || char.IsWhiteSpace(html[nameEnd])))
				return i;
		}
		return -1;
	}
}
