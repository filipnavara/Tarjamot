using System.Buffers.Binary;
using System.Text;

namespace Tarjamot.Engine;

/// <summary>
/// Minimal SentencePiece Unigram tokenizer: precompiled charsmap normalization, Viterbi
/// segmentation and byte fallback. Covers what the Firefox Translations vocabularies use.
/// </summary>
internal sealed class SentencePieceVocab
{
	const float UnkPenalty = 10f;

	enum PieceType { Normal = 1, Unknown = 2, Control = 3, UserDefined = 4, Unused = 5, Byte = 6 }

	readonly string[] pieces;
	readonly PieceType[] types;
	readonly Dictionary<string, (int Id, float Score)> lookup = new();
	readonly Dictionary<string, (int Id, float Score)>.AlternateLookup<ReadOnlySpan<char>> spanLookup;
	readonly int[] byteIds = new int[256];
	readonly int maxPieceLength;
	readonly float unkScore;
	readonly bool addDummyPrefix = true;
	readonly bool removeExtraWhitespaces = true;
	readonly bool escapeWhitespaces = true;
	readonly uint[] trieUnits = [];
	readonly byte[] normalizedBlob = [];

	public int EosId { get; } = -1;
	public int UnkId { get; } = -1;
	public int Count => pieces.Length;

	public SentencePieceVocab(byte[] model)
	{
		var pieceList = new List<string>();
		var typeList = new List<PieceType>();
		var scoreList = new List<float>();
		foreach (var (field, value, _) in Fields(model))
		{
			switch (field)
			{
				case 1:
					ReadPiece(model.AsSpan(value.Start, value.Length), out var piece, out var score, out var type);
					pieceList.Add(piece);
					scoreList.Add(score);
					typeList.Add(type);
					break;
				case 3:
					var normalizer = model.AsSpan(value.Start, value.Length).ToArray();
					foreach (var (nf, nv, nVarint) in Fields(normalizer))
					{
						switch (nf)
						{
							case 2:
								(trieUnits, normalizedBlob) = ParseCharsMap(normalizer.AsSpan(nv.Start, nv.Length));
								break;
							case 3: addDummyPrefix = nVarint != 0; break;
							case 4: removeExtraWhitespaces = nVarint != 0; break;
							case 5: escapeWhitespaces = nVarint != 0; break;
						}
					}
					break;
			}
		}

		pieces = pieceList.ToArray();
		types = typeList.ToArray();
		var minScore = float.MaxValue;
		Array.Fill(byteIds, -1);
		for (int i = 0; i < pieces.Length; i++)
		{
			switch (types[i])
			{
				case PieceType.Normal:
				case PieceType.UserDefined:
					lookup[pieces[i]] = (i, types[i] == PieceType.UserDefined ? 0f : scoreList[i]);
					maxPieceLength = Math.Max(maxPieceLength, pieces[i].Length);
					if (types[i] == PieceType.Normal)
						minScore = Math.Min(minScore, scoreList[i]);
					break;
				case PieceType.Byte:
					byteIds[Convert.ToByte(pieces[i][3..5], 16)] = i;
					break;
				case PieceType.Unknown:
					UnkId = i;
					break;
				case PieceType.Control when pieces[i] == "</s>":
					EosId = i;
					break;
			}
		}
		unkScore = minScore - UnkPenalty;
		spanLookup = lookup.GetAlternateLookup<ReadOnlySpan<char>>();
	}

	public List<int> Encode(string text) => Encode(Encoding.UTF8.GetBytes(text), null);

	/// <summary>
	/// Encodes UTF-8 text. With <paramref name="ranges"/>, also returns each piece's byte range in
	/// <paramref name="utf8"/> as SentencePiece's EncodeAsProto does: through the normalizer's offset
	/// map, and for an unknown character split into byte pieces, the whole character on the last
	/// byte piece and empty ranges at its start on the others.
	/// </summary>
	public List<int> Encode(ReadOnlySpan<byte> utf8, List<(int Begin, int End)>? ranges)
	{
		var (normalizedBytes, normToOrig) = Normalize(utf8);
		var normalized = Encoding.UTF8.GetString(normalizedBytes);
		var ids = new List<int>();
		if (normalized.Length == 0)
			return ids;

		// Viterbi over UTF-16 positions; best[i] is the best segmentation of normalized[..i].
		int n = normalized.Length;
		var bestScore = new float[n + 1];
		var bestStart = new int[n + 1];
		var bestId = new int[n + 1];
		Array.Fill(bestScore, float.NegativeInfinity);
		bestScore[0] = 0;
		for (int start = 0; start < n; start++)
		{
			if (float.IsNegativeInfinity(bestScore[start]))
				continue;
			int charLength = char.IsHighSurrogate(normalized[start]) && start + 1 < n ? 2 : 1;
			bool hasSingleChar = false;
			for (int length = 1; length <= maxPieceLength && start + length <= n; length++)
			{
				if (!spanLookup.TryGetValue(normalized.AsSpan(start, length), out var entry))
					continue;
				if (length == charLength)
					hasSingleChar = true;
				Relax(start, length, entry.Id, entry.Score);
			}
			if (!hasSingleChar)
				Relax(start, charLength, UnkId, unkScore);
		}

		var segments = new List<(int Start, int Length, int Id)>();
		for (int end = n; end > 0; end = bestStart[end])
			segments.Add((bestStart[end], end - bestStart[end], bestId[end]));
		segments.Reverse();

		// Byte offset in the normalized UTF-8 of each UTF-16 position.
		var byteOffsets = new int[n + 1];
		for (int i = 0, offset = 0; i <= n; i++)
		{
			byteOffsets[i] = offset;
			if (i < n)
				offset += char.IsHighSurrogate(normalized[i]) || char.IsLowSurrogate(normalized[i]) ? 2 : Encoding.UTF8.GetByteCount(normalized.AsSpan(i, 1));
		}

		foreach (var (start, length, id) in segments)
		{
			int begin = normToOrig[byteOffsets[start]];
			int end = normToOrig[byteOffsets[start + length]];
			if (id != UnkId)
			{
				ids.Add(id);
				ranges?.Add((begin, end));
				continue;
			}
			var bytes = normalizedBytes.AsSpan(byteOffsets[start], byteOffsets[start + length] - byteOffsets[start]);
			for (int i = 0; i < bytes.Length; i++)
			{
				ids.Add(byteIds[bytes[i]] >= 0 ? byteIds[bytes[i]] : UnkId);
				ranges?.Add(i == bytes.Length - 1 ? (begin, end) : (begin, begin));
			}
		}
		return ids;

		void Relax(int start, int length, int id, float score)
		{
			float candidate = bestScore[start] + score;
			if (candidate > bestScore[start + length])
			{
				bestScore[start + length] = candidate;
				bestStart[start + length] = start;
				bestId[start + length] = id;
			}
		}
	}

	public string Decode(IEnumerable<int> ids)
	{
		var bytes = new List<byte>();
		foreach (var id in ids)
		{
			switch (types[id])
			{
				case PieceType.Normal:
				case PieceType.UserDefined:
					bytes.AddRange(Encoding.UTF8.GetBytes(pieces[id].Replace('\u2581', ' ')));
					break;
				case PieceType.Byte:
					bytes.Add(PieceByte(id));
					break;
				case PieceType.Unknown:
					bytes.AddRange(" \u2047 "u8);
					break;
			}
		}
		var text = Encoding.UTF8.GetString(bytes.ToArray());
		return addDummyPrefix && text.StartsWith(' ') ? text[1..] : text;
	}

	/// <summary>
	/// Decodes like SentencePiece's Decode into a SentencePieceText: the UTF-8 text and each piece's
	/// byte range in it. Control pieces (EOS) are empty. The space of the first piece is dropped
	/// only while the text is still empty. Runs of byte pieces are decoded as UTF-8: the last byte
	/// of each character holds its surface, the others are empty; an invalid byte becomes U+FFFD.
	/// </summary>
	public byte[] DecodeWithRanges(IReadOnlyList<int> ids, List<(int Begin, int End)> ranges)
	{
		var text = new List<byte>();
		int byteStart = 0;
		for (int i = 0; i < ids.Count; i++)
		{
			if (types[ids[i]] == PieceType.Byte)
				continue;
			DecodeBytePieces(byteStart, i);
			byteStart = i + 1;
			SetSurface(DecodePiece(ids[i], text.Count == 0));
		}
		DecodeBytePieces(byteStart, ids.Count);
		return text.ToArray();

		void SetSurface(ReadOnlySpan<byte> surface)
		{
			ranges.Add((text.Count, text.Count + surface.Length));
			text.AddRange(surface);
		}

		void DecodeBytePieces(int begin, int end)
		{
			if (begin >= end)
				return;
			var bytes = new byte[end - begin];
			for (int i = begin; i < end; i++)
				bytes[i - begin] = PieceByte(ids[i]);
			Span<byte> utf8 = stackalloc byte[4];
			for (int offset = 0; offset < bytes.Length;)
			{
				if (Rune.DecodeFromUtf8(bytes.AsSpan(offset), out var rune, out int length) != System.Buffers.OperationStatus.Done)
				{
					SetSurface("\uFFFD"u8);
					offset++;
					continue;
				}
				for (int j = 0; j < length - 1; j++)
					SetSurface([]);
				SetSurface(utf8[..rune.EncodeToUtf8(utf8)]);
				offset += length;
			}
		}
	}

	byte[] DecodePiece(int id, bool atStart)
	{
		switch (types[id])
		{
			case PieceType.Control:
				return [];
			case PieceType.Unknown:
				return " \u2047 "u8.ToArray();
		}
		var piece = pieces[id];
		if (atStart && (addDummyPrefix || removeExtraWhitespaces) && piece.StartsWith('\u2581'))
			piece = piece[1..];
		return Encoding.UTF8.GetBytes(piece.Replace('\u2581', ' '));
	}

	byte PieceByte(int id) => Convert.ToByte(pieces[id][3..5], 16);

	/// <summary>
	/// SentencePiece's Normalizer::Normalize: the normalized UTF-8 bytes and, for every normalized
	/// byte plus the end, the offset in <paramref name="input"/> it comes from.
	/// </summary>
	(byte[] Normalized, int[] NormToOrig) Normalize(ReadOnlySpan<byte> input)
	{
		var output = new List<byte>(input.Length + 3);
		var normToOrig = new List<int>(input.Length + 4);
		var space = "\u2581"u8;
		int consumed = 0;

		if (removeExtraWhitespaces)
		{
			while (!input.IsEmpty)
			{
				var prefix = NormalizePrefix(input, out int length);
				if (!prefix.SequenceEqual(" "u8))
					break;
				input = input[length..];
				consumed += length;
			}
		}
		if (input.IsEmpty)
			return ([], [consumed]);
		if (addDummyPrefix)
			AddBytes(space);

		bool isPrevSpace = removeExtraWhitespaces;
		while (!input.IsEmpty)
		{
			var prefix = NormalizePrefix(input, out int length);
			if (isPrevSpace)
				prefix = prefix.TrimStart((byte)' ');
			if (!prefix.IsEmpty)
			{
				foreach (var b in prefix)
				{
					if (escapeWhitespaces && b == ' ')
						AddBytes(space);
					else
						AddBytes([b]);
				}
				isPrevSpace = prefix[^1] == ' ';
			}
			consumed += length;
			input = input[length..];
			if (!removeExtraWhitespaces)
				isPrevSpace = false;
		}

		if (removeExtraWhitespaces)
		{
			while (output.Count >= space.Length && output[^1] == space[2] && output[^2] == space[1] && output[^3] == space[0])
			{
				int length = output.Count - space.Length;
				consumed = normToOrig[length];
				output.RemoveRange(length, space.Length);
				normToOrig.RemoveRange(length, space.Length);
			}
		}
		normToOrig.Add(consumed);
		return (output.ToArray(), normToOrig.ToArray());

		void AddBytes(ReadOnlySpan<byte> bytes)
		{
			foreach (var b in bytes)
			{
				output.Add(b);
				normToOrig.Add(consumed);
			}
		}
	}

	ReadOnlySpan<byte> NormalizePrefix(ReadOnlySpan<byte> input, out int consumed)
	{
		int matchLength = 0;
		int matchValue = 0;
		if (trieUnits.Length > 0)
			LongestPrefixMatch(input, out matchLength, out matchValue);
		if (matchLength > 0)
		{
			var replacement = normalizedBlob.AsSpan(matchValue);
			consumed = matchLength;
			return replacement[..replacement.IndexOf((byte)0)];
		}

		if (Rune.DecodeFromUtf8(input, out _, out int runeLength) != System.Buffers.OperationStatus.Done)
		{
			consumed = 1;
			return "\uFFFD"u8;
		}
		consumed = runeLength;
		return input[..runeLength];
	}

	// Darts-clone double array common prefix search, keeping the longest match.
	void LongestPrefixMatch(ReadOnlySpan<byte> key, out int matchLength, out int matchValue)
	{
		matchLength = 0;
		matchValue = 0;
		uint nodePos = 0;
		uint unit = trieUnits[nodePos];
		nodePos ^= Offset(unit);
		for (int i = 0; i < key.Length; i++)
		{
			nodePos ^= key[i];
			if (nodePos >= trieUnits.Length)
				return;
			unit = trieUnits[nodePos];
			if (Label(unit) != key[i])
				return;
			nodePos ^= Offset(unit);
			if (HasLeaf(unit))
			{
				matchLength = i + 1;
				matchValue = (int)(trieUnits[nodePos] & 0x7FFFFFFF);
			}
		}

		static bool HasLeaf(uint u) => ((u >> 8) & 1) == 1;
		static uint Label(uint u) => u & (0x80000000 | 0xFF);
		static uint Offset(uint u) => (u >> 10) << (int)((u & (1 << 9)) >> 6);
	}

	static (uint[] Units, byte[] Normalized) ParseCharsMap(ReadOnlySpan<byte> blob)
	{
		int trieSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob);
		var units = new uint[trieSize / 4];
		for (int i = 0; i < units.Length; i++)
			units[i] = BinaryPrimitives.ReadUInt32LittleEndian(blob[(4 + i * 4)..]);
		return (units, blob[(4 + trieSize)..].ToArray());
	}

	static void ReadPiece(ReadOnlySpan<byte> data, out string piece, out float score, out PieceType type)
	{
		piece = "";
		score = 0;
		type = PieceType.Normal;
		var buffer = data.ToArray();
		foreach (var (field, value, varint) in Fields(buffer))
		{
			switch (field)
			{
				case 1: piece = Encoding.UTF8.GetString(buffer, value.Start, value.Length); break;
				case 2: score = BitConverter.ToSingle(buffer, value.Start); break;
				case 3: type = (PieceType)varint; break;
			}
		}
	}

	// Tiny protobuf reader: yields field number, payload range (length-delimited / fixed) and varint value.
	static IEnumerable<(int Field, (int Start, int Length) Value, ulong Varint)> Fields(byte[] data)
	{
		int i = 0;
		while (i < data.Length)
		{
			ulong key = ReadVarint(data, ref i);
			int field = (int)(key >> 3);
			switch (key & 7)
			{
				case 0:
					yield return (field, (0, 0), ReadVarint(data, ref i));
					break;
				case 1:
					yield return (field, (i, 8), 0);
					i += 8;
					break;
				case 2:
					int length = (int)ReadVarint(data, ref i);
					yield return (field, (i, length), 0);
					i += length;
					break;
				case 5:
					yield return (field, (i, 4), 0);
					i += 4;
					break;
				default:
					throw new InvalidDataException($"Unsupported protobuf wire type {key & 7}.");
			}
		}

		static ulong ReadVarint(byte[] data, ref int i)
		{
			ulong result = 0;
			for (int shift = 0; ; shift += 7)
			{
				byte b = data[i++];
				result |= (ulong)(b & 0x7F) << shift;
				if (b < 0x80)
					return result;
			}
		}
	}
}
