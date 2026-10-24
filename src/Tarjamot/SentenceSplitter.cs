using System.Globalization;
using System.Text;

namespace Tarjamot;

/// <summary>
/// Sentence boundaries as Firefox finds them. The WASM build of Bergamot does not use ssplit; it
/// calls Intl.Segmenter with sentence granularity, i.e. ICU's implementation of the Unicode
/// sentence boundary rules (UAX #29, SB1-SB11) without abbreviation suppressions. .NET has no
/// sentence break iterator, so the rules are implemented here with Sentence_Break property values
/// derived from general categories and the property's explicit code point lists: STerm and
/// SContinue are the complete lists of SentenceBreakProperty.txt (Unicode 18.0.0), sorted for
/// binary search. Like ICU without dictionary breaking, text without terminators (Thai, Lao)
/// stays one sentence.
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
			case '"' or '\'' or 0xFF02 or 0xFF07:
				return SentenceBreak.Close;
			case 0x200D or 0x200C:
				return SentenceBreak.Extend;
		}
		if (Array.BinarySearch(STermCodePoints, (uint)c) >= 0)
			return SentenceBreak.STerm;
		if (Array.BinarySearch(SContinueCodePoints, (uint)c) >= 0)
			return SentenceBreak.SContinue;
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

	// The STerm code points of SentenceBreakProperty.txt (Unicode 18.0.0), sorted for the binary
	// search above. Copied as a whole, so a UCD update only replaces the two lists.
	internal static readonly uint[] STermCodePoints =
	[
		0x21, 0x3F, 0x589, 0x61D, 0x61E, 0x61F, 0x6D4, 0x700, 0x701, 0x702,
		0x7F9, 0x837, 0x839, 0x83D, 0x83E, 0x964, 0x965, 0x104A, 0x104B, 0x1362,
		0x1367, 0x1368, 0x166E, 0x1735, 0x1736, 0x17D4, 0x17D5, 0x1803, 0x1809, 0x1944,
		0x1945, 0x1AA8, 0x1AA9, 0x1AAA, 0x1AAB, 0x1B4E, 0x1B4F, 0x1B5A, 0x1B5B, 0x1B5E,
		0x1B5F, 0x1B7D, 0x1B7E, 0x1B7F, 0x1C3B, 0x1C3C, 0x1C7E, 0x1C7F, 0x203C, 0x203D,
		0x2047, 0x2048, 0x2049, 0x2CF9, 0x2CFA, 0x2CFB, 0x2E2E, 0x2E3C, 0x2E53, 0x2E54,
		0x2E60, 0x2E61, 0x3002, 0xA4FF, 0xA60E, 0xA60F, 0xA6F3, 0xA6F7, 0xA876, 0xA877,
		0xA8CE, 0xA8CF, 0xA92F, 0xA9C8, 0xA9C9, 0xAA5D, 0xAA5E, 0xAA5F, 0xAAF0, 0xAAF1,
		0xABEB, 0xFE12, 0xFE15, 0xFE16, 0xFE56, 0xFE57, 0xFF01, 0xFF1F, 0xFF61, 0x10A56,
		0x10A57, 0x10F55, 0x10F56, 0x10F57, 0x10F58, 0x10F59, 0x10F86, 0x10F87, 0x10F88, 0x10F89,
		0x11047, 0x11048, 0x110BE, 0x110BF, 0x110C0, 0x110C1, 0x11141, 0x11142, 0x11143, 0x111C5,
		0x111C6, 0x111CD, 0x111DE, 0x111DF, 0x11238, 0x11239, 0x1123B, 0x1123C, 0x112A9, 0x113D4,
		0x113D5, 0x1144B, 0x1144C, 0x115C2, 0x115C3, 0x115C9, 0x115CA, 0x115CB, 0x115CC, 0x115CD,
		0x115CE, 0x115CF, 0x115D0, 0x115D1, 0x115D2, 0x115D3, 0x115D4, 0x115D5, 0x115D6, 0x115D7,
		0x11641, 0x11642, 0x1173C, 0x1173D, 0x1173E, 0x11944, 0x11946, 0x11A42, 0x11A43, 0x11A9B,
		0x11A9C, 0x11C41, 0x11C42, 0x11EF7, 0x11EF8, 0x11F43, 0x11F44, 0x16A6E, 0x16A6F, 0x16AF5,
		0x16B37, 0x16B38, 0x16B44, 0x16D6E, 0x16D6F, 0x16E98, 0x1BC9F, 0x1DA88,
	];

	// The SContinue code points of the same file, sorted.
	internal static readonly uint[] SContinueCodePoints =
	[
		0x2C, 0x2D, 0x3A, 0x3B, 0x37E, 0x55D, 0x60C, 0x60D, 0x7F8, 0x1802,
		0x1808, 0x2013, 0x2014, 0x3001, 0xFE10, 0xFE11, 0xFE13, 0xFE14, 0xFE31, 0xFE32,
		0xFE50, 0xFE51, 0xFE54, 0xFE55, 0xFE58, 0xFE63, 0xFF0C, 0xFF0D, 0xFF1A, 0xFF1B,
		0xFF64,
	];
}
