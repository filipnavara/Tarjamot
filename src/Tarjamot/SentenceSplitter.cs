using System.Globalization;
using System.Text;

namespace Tarjamot;

/// <summary>
/// Sentence boundaries as Firefox finds them. The WASM build of Bergamot does not use ssplit; it
/// calls Intl.Segmenter with sentence granularity, i.e. ICU's implementation of the Unicode
/// sentence boundary rules (UAX #29, SB1-SB11) without abbreviation suppressions. .NET has no
/// sentence break iterator, so the rules are implemented here with Sentence_Break property values
/// derived from general categories and the property's explicit code point lists.
/// </summary>
internal static class SentenceSplitter
{
	enum SentenceBreak { Other, CR, LF, Sep, Sp, Lower, Upper, OLetter, Numeric, ATerm, STerm, Close, SContinue, Extend, Format }

	public static IEnumerable<string> Split(string text) => SplitRanges(text).Select(range => text[range]);

	/// <summary>Sentence spans without surrounding whitespace; the text between them is the "gap".</summary>
	public static IEnumerable<Range> SplitRanges(string text)
	{
		var runes = new List<(int Index, SentenceBreak Type)>();
		int position = 0;
		foreach (var rune in text.EnumerateRunes())
		{
			runes.Add((position, Classify(rune)));
			position += rune.Utf16SequenceLength;
		}

		int start = 0;
		int i = 0;
		while (i < runes.Count)
		{
			var type = runes[i].Type;
			if (type is SentenceBreak.CR or SentenceBreak.LF or SentenceBreak.Sep)
			{
				// SB3, SB4: break after a paragraph separator (CR LF counts as one).
				i += type == SentenceBreak.CR && i + 1 < runes.Count && runes[i + 1].Type == SentenceBreak.LF ? 2 : 1;
				if (Cut(text, runes, ref start, i) is { } sentence)
					yield return sentence;
				continue;
			}
			if (type is not (SentenceBreak.ATerm or SentenceBreak.STerm))
			{
				i++;
				continue;
			}

			int next = Skip(runes, i + 1);
			if (type == SentenceBreak.ATerm && next < runes.Count)
			{
				// SB6: ATerm x Numeric. SB7: (Upper | Lower) ATerm x Upper.
				if (runes[next].Type == SentenceBreak.Numeric)
				{
					i = next;
					continue;
				}
				int previous = Previous(runes, i);
				if (previous >= 0 && runes[previous].Type is SentenceBreak.Upper or SentenceBreak.Lower && runes[next].Type == SentenceBreak.Upper)
				{
					i = next;
					continue;
				}
			}
			// SB9, SB10: the terminator keeps trailing Close*, Sp* and one paragraph separator.
			int k = next;
			while (k < runes.Count && runes[k].Type == SentenceBreak.Close)
				k = Skip(runes, k + 1);
			while (k < runes.Count && runes[k].Type == SentenceBreak.Sp)
				k = Skip(runes, k + 1);
			if (k < runes.Count && runes[k].Type is SentenceBreak.CR or SentenceBreak.LF or SentenceBreak.Sep)
			{
				k += runes[k].Type == SentenceBreak.CR && k + 1 < runes.Count && runes[k + 1].Type == SentenceBreak.LF ? 2 : 1;
			}
			else if (k < runes.Count)
			{
				// SB8: ATerm Close* Sp* x (not OLetter, Upper, Lower, ParaSep, SATerm)* Lower.
				if (type == SentenceBreak.ATerm && FollowedByLower(runes, k))
				{
					i = k;
					continue;
				}
				// SB8a: SATerm Close* Sp* x (SContinue | SATerm).
				if (runes[k].Type is SentenceBreak.SContinue or SentenceBreak.ATerm or SentenceBreak.STerm)
				{
					i = k;
					continue;
				}
			}
			// SB11: break.
			if (Cut(text, runes, ref start, k) is { } result)
				yield return result;
			i = k;
		}
		if (Trim(text, start, text.Length) is { } last)
			yield return last;
	}

	/// <summary>Each line as one sentence, without surrounding whitespace.</summary>
	public static IEnumerable<Range> SplitLines(string text)
	{
		int start = 0;
		while (start <= text.Length)
		{
			int end = text.IndexOf('\n', start);
			if (end < 0)
				end = text.Length;
			if (Trim(text, start, end) is { } line)
				yield return line;
			start = end + 1;
		}
	}

	// Returns the sentence ending before rune `end` (without surrounding whitespace) and moves start.
	static Range? Cut(string text, List<(int Index, SentenceBreak Type)> runes, ref int start, int end)
	{
		int endIndex = end < runes.Count ? runes[end].Index : text.Length;
		var sentence = Trim(text, start, endIndex);
		start = endIndex;
		return sentence;
	}

	static Range? Trim(string text, int start, int end)
	{
		while (start < end && char.IsWhiteSpace(text[start]))
			start++;
		while (end > start && char.IsWhiteSpace(text[end - 1]))
			end--;
		return start < end ? start..end : null;
	}

	// SB5: Extend and Format characters attach to the preceding character.
	static int Skip(List<(int Index, SentenceBreak Type)> runes, int i)
	{
		while (i < runes.Count && runes[i].Type is SentenceBreak.Extend or SentenceBreak.Format)
			i++;
		return i;
	}

	static int Previous(List<(int Index, SentenceBreak Type)> runes, int i)
	{
		i--;
		while (i >= 0 && runes[i].Type is SentenceBreak.Extend or SentenceBreak.Format)
			i--;
		return i;
	}

	static bool FollowedByLower(List<(int Index, SentenceBreak Type)> runes, int k)
	{
		for (; k < runes.Count; k++)
		{
			switch (runes[k].Type)
			{
				case SentenceBreak.Lower:
					return true;
				case SentenceBreak.OLetter or SentenceBreak.Upper or SentenceBreak.CR or SentenceBreak.LF or SentenceBreak.Sep or SentenceBreak.ATerm or SentenceBreak.STerm:
					return false;
			}
		}
		return false;
	}

	static SentenceBreak Classify(Rune rune)
	{
		int c = rune.Value;
		switch (c)
		{
			case '\r':
				return SentenceBreak.CR;
			case '\n':
				return SentenceBreak.LF;
			case 0x85 or 0x2028 or 0x2029:
				return SentenceBreak.Sep;
			case '.' or 0x2024 or 0xFE52 or 0xFF0E:
				return SentenceBreak.ATerm;
			case '!' or '?' or 0x0589 or 0x061D or 0x061E or 0x061F or 0x06D4 or 0x0700 or 0x0701 or 0x0702 or 0x07F9 or 0x0837 or 0x0839 or 0x083D or 0x083E
				or 0x0964 or 0x0965 or 0x104A or 0x104B or 0x1362 or 0x1367 or 0x1368 or 0x166E or 0x1735 or 0x1736 or 0x1803 or 0x1809 or 0x1944 or 0x1945
				or 0x203C or 0x203D or 0x2047 or 0x2048 or 0x2049 or 0x2E2E or 0x2E3C or 0x3002 or 0xA4FF or 0xA60E or 0xA60F or 0xA6F3 or 0xA6F7
				or 0xFE56 or 0xFE57 or 0xFF01 or 0xFF1F or 0xFF61:
				return SentenceBreak.STerm;
			case ',' or '-' or ':' or 0x055D or 0x060C or 0x060D or 0x07F8 or 0x1802 or 0x1808 or 0x2013 or 0x2014 or 0x3001 or 0xFE10 or 0xFE11 or 0xFE13
				or 0xFE31 or 0xFE32 or 0xFE50 or 0xFE51 or 0xFE55 or 0xFE58 or 0xFE63 or 0xFF0C or 0xFF0D or 0xFF1A or 0xFF64:
				return SentenceBreak.SContinue;
			case '"' or '\'' or 0xFF02 or 0xFF07:
				return SentenceBreak.Close;
			case 0x200D or 0x200C:
				return SentenceBreak.Extend;
		}
		if (Rune.IsWhiteSpace(rune))
			return SentenceBreak.Sp;
		return Rune.GetUnicodeCategory(rune) switch
		{
			UnicodeCategory.LowercaseLetter => SentenceBreak.Lower,
			UnicodeCategory.UppercaseLetter or UnicodeCategory.TitlecaseLetter => SentenceBreak.Upper,
			UnicodeCategory.OtherLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.LetterNumber => SentenceBreak.OLetter,
			UnicodeCategory.DecimalDigitNumber => SentenceBreak.Numeric,
			UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation => SentenceBreak.Close,
			UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark => SentenceBreak.Extend,
			UnicodeCategory.Format => SentenceBreak.Format,
			_ => SentenceBreak.Other,
		};
	}
}
