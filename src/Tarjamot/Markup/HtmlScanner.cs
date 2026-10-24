namespace Tarjamot.Markup;

/// <summary>
/// Port of Bergamot's markup scanner (xh_scanner.cpp, after Andrew Fedoniouk's "Fast and Compact
/// HTML/XML Scanner", BSD license). It works on UTF-8 bytes like the original, so offsets, entity
/// decoding and whitespace handling are identical. A NUL byte ends the input, as in C.
/// </summary>
internal sealed class HtmlScanner
{
	public enum TokenType
	{
		Error = -1,
		Eof = 0,
		TagStart,
		TagEnd,
		Attribute,
		Text,
		Data,
		CommentStart,
		CommentEnd,
		ProcessingInstructionStart,
		ProcessingInstructionEnd,
	}

	static readonly byte[] Lt = "<"u8.ToArray(), Gt = ">"u8.ToArray(), Amp = "&"u8.ToArray(), Quot = "\""u8.ToArray(), Apos = "'"u8.ToArray(), Space = " "u8.ToArray();

	readonly byte[] input;
	int position;
	Func<TokenType> scan;
	// Value: either a slice of the input or a decoded entity.
	byte[] valueSource;
	int valueStart, valueLength;
	int tagStart, tagLength;
	int attributeStart, attributeLength;
	bool gotTail;

	public HtmlScanner(byte[] input)
	{
		this.input = input;
		valueSource = input;
		scan = ScanBody;
	}

	public byte[] Input => input;

	/// <summary>Start of the current token in the input (Scanner::start()).</summary>
	public int Start { get; private set; }

	public TokenType Next() => scan();

	public ReadOnlySpan<byte> Value => valueSource.AsSpan(valueStart, valueLength);

	public ReadOnlySpan<byte> Attribute => input.AsSpan(attributeStart, attributeLength);

	public ReadOnlySpan<byte> Tag => input.AsSpan(tagStart, tagLength);

	byte Peek() => position < input.Length ? input[position] : (byte)0;

	byte Consume() => position < input.Length ? input[position++] : (byte)0;

	void SetValue(int start, int length)
	{
		valueSource = input;
		valueStart = start;
		valueLength = length;
	}

	TokenType ScanBody()
	{
		SetValue(position, 0);
		Start = position;
		switch (Peek())
		{
			case 0:
				return TokenType.Eof;
			case (byte)'<':
				return ScanTag();
			case (byte)'&':
				return ScanEntity(TokenType.Text);
		}
		while (true)
		{
			switch (Peek())
			{
				case 0:
				case (byte)'<':
				case (byte)'&':
					return TokenType.Text;
				default:
					Consume();
					valueLength++;
					break;
			}
		}
	}

	TokenType ScanAttribute()
	{
		SkipWhitespace();
		switch (Peek())
		{
			case (byte)'>':
				Consume();
				// Some elements are opaque: their attributes are parsed, but not their content.
				var tag = Tag;
				if (EqualsIgnoreCase(tag, "script"u8) || EqualsIgnoreCase(tag, "style"u8) || EqualsIgnoreCase(tag, "textarea"u8) ||
					EqualsIgnoreCase(tag, "iframe"u8) || EqualsIgnoreCase(tag, "noembed"u8) || EqualsIgnoreCase(tag, "noscript"u8) ||
					EqualsIgnoreCase(tag, "noframes"u8))
				{
					scan = ScanSpecial;
					return ScanSpecial();
				}
				scan = ScanBody;
				return ScanBody();
			case (byte)'/':
				Consume();
				if (Peek() == (byte)'>')
				{
					Consume();
					scan = ScanBody;
					return TokenType.TagEnd;
				}
				return TokenType.Error;
		}

		attributeStart = position;
		attributeLength = 0;
		valueSource = input;
		valueStart = 0;
		valueLength = 0;

		while (Peek() != (byte)'=')
		{
			switch (Peek())
			{
				case 0:
					return TokenType.Eof;
				case (byte)'>':
					return TokenType.Attribute; // attribute without value at the end of the tag
				case (byte)'<':
					return TokenType.Error;
				default:
					if (SkipWhitespace() > 0)
					{
						if (Peek() == (byte)'=')
							break;
						return TokenType.Attribute; // attribute without value, more to come
					}
					Consume();
					attributeLength++;
					break;
			}
		}

		// '=' and any following whitespace
		Consume();
		SkipWhitespace();

		switch (Peek())
		{
			case (byte)'"':
			case (byte)'\'':
				byte quote = Consume();
				SetValue(position, 0);
				while (true)
				{
					if (Peek() == 0)
						return TokenType.Error;
					if (Peek() == quote)
					{
						Consume();
						return TokenType.Attribute;
					}
					Consume();
					valueLength++;
				}
			default:
				SetValue(position, 0);
				while (true)
				{
					if (IsWhitespace(Peek()))
						return TokenType.Attribute;
					if (Peek() == (byte)'>')
						return TokenType.Attribute; // '>' is consumed next round
					Consume();
					valueLength++;
				}
		}
	}

	TokenType ScanTag()
	{
		Start = position;
		if (Consume() != (byte)'<')
			return TokenType.Error;

		bool isTail = Peek() == (byte)'/';
		if (isTail)
			Consume();

		tagStart = position;
		tagLength = 0;
		while (Peek() != 0)
		{
			if (SkipWhitespace() > 0)
				break;
			if (Peek() == (byte)'/' || Peek() == (byte)'>')
				break;
			Consume();
			tagLength++;

			// Tested at every character, thus eager: "<?xml" matches on "?".
			if (Tag.SequenceEqual("!--"u8))
			{
				scan = ScanComment;
				return TokenType.CommentStart;
			}
			if (Tag.SequenceEqual("?"u8))
			{
				scan = ScanProcessingInstruction;
				return TokenType.ProcessingInstructionStart;
			}
		}

		if (Peek() == 0)
			return TokenType.Eof;
		if (isTail)
			return Consume() == (byte)'>' ? TokenType.TagEnd : TokenType.Error;

		scan = ScanAttribute;
		return TokenType.TagStart;
	}

	TokenType ScanEntity(TokenType parentTokenType)
	{
		// The entity includes the starting '&' and the ending ';'.
		Start = position;
		int entityStart = position;
		int entityLength = 0;
		bool hasEnd = false;

		if (Consume() != (byte)'&')
			return TokenType.Error;
		entityLength++;

		while (Peek() != 0)
		{
			if (Peek() == (byte)';')
			{
				Consume();
				entityLength++;
				hasEnd = true;
				break;
			}
			if (!char.IsAsciiLetter((char)Peek()))
				break;
			Consume();
			entityLength++;
		}

		// Decoded if known, otherwise the whole entity is text as it is.
		if (hasEnd && ResolveEntity(input.AsSpan(entityStart, entityLength)) is { } decoded)
		{
			valueSource = decoded;
			valueStart = 0;
			valueLength = decoded.Length;
			return parentTokenType;
		}
		SetValue(entityStart, entityLength);
		return parentTokenType;
	}

	static byte[]? ResolveEntity(ReadOnlySpan<byte> entity) =>
		entity.SequenceEqual("&lt;"u8) ? Lt :
		entity.SequenceEqual("&gt;"u8) ? Gt :
		entity.SequenceEqual("&amp;"u8) ? Amp :
		entity.SequenceEqual("&quot;"u8) ? Quot :
		entity.SequenceEqual("&apos;"u8) ? Apos :
		entity.SequenceEqual("&nbsp;"u8) ? Space : // Bergamot turns non-breaking spaces into spaces
		null;

	int SkipWhitespace()
	{
		int skipped = 0;
		while (IsWhitespace(Peek()))
		{
			Consume();
			skipped++;
		}
		return skipped;
	}

	static bool IsWhitespace(byte c) => c is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\f';

	TokenType ScanComment()
	{
		if (gotTail)
		{
			Start = position - 3; // "-->"
			scan = ScanBody;
			gotTail = false;
			return TokenType.CommentEnd;
		}

		Start = position;
		SetValue(position, 0);
		while (true)
		{
			if (Consume() == 0)
				return TokenType.Eof;
			valueLength++;
			if (EndsWith("-->"u8))
			{
				gotTail = true;
				valueLength -= 3;
				break;
			}
		}
		return TokenType.Data;
	}

	TokenType ScanProcessingInstruction()
	{
		if (gotTail)
		{
			Start = position - 2; // "?>"
			scan = ScanBody;
			gotTail = false;
			return TokenType.ProcessingInstructionEnd;
		}

		Start = position;
		SetValue(position, 0);
		while (true)
		{
			if (Consume() == 0)
				return TokenType.Eof;
			valueLength++;
			if (EndsWith("?>"u8))
			{
				gotTail = true;
				valueLength -= 2;
				break;
			}
		}
		return TokenType.Data;
	}

	// The content of <script>, <style> and the like, up to the matching close tag.
	TokenType ScanSpecial()
	{
		if (gotTail)
		{
			Start = position - (tagLength + 3); // "</tag>"
			scan = ScanBody;
			gotTail = false;
			return TokenType.TagEnd;
		}

		Start = position;
		SetValue(position, 0);
		while (true)
		{
			if (Consume() == 0)
				return TokenType.Eof;
			valueLength++;

			if (input[valueStart + valueLength - 1] == (byte)'>' && valueLength >= tagLength + 3)
			{
				int tagOpen = valueStart + valueLength - tagLength - 3;
				if (input[tagOpen] != (byte)'<' || input[tagOpen + 1] != (byte)'/')
					continue;
				// Case-insensitive, because <I>...</i> is fine.
				if (!EqualsIgnoreCase(input.AsSpan(valueStart + valueLength - tagLength - 1, tagLength), Tag))
					continue;
				gotTail = true;
				valueLength -= tagLength + 3;
				break;
			}
		}
		return TokenType.Data;
	}

	bool EndsWith(ReadOnlySpan<byte> suffix) => valueLength >= suffix.Length && input.AsSpan(valueStart + valueLength - suffix.Length, suffix.Length).SequenceEqual(suffix);

	internal static bool EqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
	{
		if (left.Length != right.Length)
			return false;
		for (int i = 0; i < left.Length; i++)
		{
			if (ToLower(left[i]) != ToLower(right[i]))
				return false;
		}
		return true;
	}

	// std::tolower in the "C" locale: ASCII only.
	internal static byte ToLower(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c;
}
