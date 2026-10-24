using System.Buffers.Binary;

namespace Tarjamot.Engine;

/// <summary>
/// Binary lexical shortlist: restricts the output vocabulary to words likely given the source.
/// </summary>
internal sealed class Shortlist
{
	const ulong Magic = 0xF11A48D5013417F5;

	readonly int firstNum;
	readonly ulong[] wordToOffset;
	readonly uint[] shortLists;

	public Shortlist(ReadOnlySpan<byte> data)
	{
		var span = data;
		if (BinaryPrimitives.ReadUInt64LittleEndian(span) != Magic)
			throw new InvalidDataException("Invalid binary shortlist.");
		firstNum = (int)BinaryPrimitives.ReadUInt64LittleEndian(span[16..]);
		int offsetCount = (int)BinaryPrimitives.ReadUInt64LittleEndian(span[32..]);
		int listCount = (int)BinaryPrimitives.ReadUInt64LittleEndian(span[40..]);
		wordToOffset = new ulong[offsetCount];
		for (int i = 0; i < offsetCount; i++)
			wordToOffset[i] = BinaryPrimitives.ReadUInt64LittleEndian(span[(48 + i * 8)..]);
		int listStart = 48 + offsetCount * 8;
		shortLists = new uint[listCount];
		for (int i = 0; i < listCount; i++)
			shortLists[i] = BinaryPrimitives.ReadUInt32LittleEndian(span[(listStart + i * 4)..]);
	}

	public int[] Generate(IReadOnlyList<int> sourceIds, int vocabSize, bool sharedVocab)
	{
		var selected = new bool[vocabSize];
		for (int i = 0; i < firstNum && i < vocabSize; i++)
			selected[i] = true;
		foreach (var word in sourceIds)
		{
			if (sharedVocab)
				selected[word] = true;
			for (ulong j = wordToOffset[word]; j < wordToOffset[word + 1]; j++)
				selected[shortLists[j]] = true;
		}
		// Marian pads the selection to a multiple of eight (an intgemm requirement) with the
		// lowest unselected ids after firstNum; these extra candidates can win the argmax.
		int count = selected.Count(s => s);
		for (int i = firstNum; i < vocabSize && count % 8 != 0; i++)
		{
			if (!selected[i])
			{
				selected[i] = true;
				count++;
			}
		}

		var result = new List<int>();
		for (int i = 0; i < vocabSize; i++)
		{
			if (selected[i])
				result.Add(i);
		}
		return result.ToArray();
	}
}
