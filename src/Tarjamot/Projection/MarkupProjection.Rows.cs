using System.Runtime.InteropServices;
using Tarjamot.Engine;

namespace Tarjamot.Projection;

// Short cells of a table row, such as the days of a calendar ("Mon", "Tue", ... "Sun") or the
// headers of a list, are ambiguous on their own: "Sun" alone is the star. The short sentences of
// a row with them in two cells or more (a layout table's row of one cell is not a list) are
// therefore translated together, as one sentence joined with " · ", which the models
// keep as a list, and the translation is divided among them by the alignment. When the alignment
// does not divide it cleanly (a cell without words, words aligned with a separator, cells out of
// order), the sentences are translated one by one.
internal static partial class MarkupProjection
{
	// A sentence of at most this many tokens takes part in its row.
	const int MaxRowSentenceTokens = 8;
	const int MaxRowSentences = 12;
	const string RowSeparator = "·";

	sealed class RowGroup
	{
		public required List<Sentence> Members { get; init; }
		// Where each member's tokens start in the joined sentence.
		public required int[] Offsets { get; init; }
		public required Sentence Joined { get; init; }
	}

	static void TranslateRows(List<Sentence> sentences, IReadOnlyList<TranslationModel> models, CancellationToken cancellationToken)
	{
		var groups = FindRowGroups(sentences, models[0].Engine.SourceVocab);
		if (groups.Count == 0)
		{
			TranslateSentences(sentences, models, cancellationToken);
			return;
		}

		var grouped = groups.SelectMany(group => group.Members).ToHashSet();
		TranslateSentences([.. sentences.Where(sentence => !grouped.Contains(sentence)), .. groups.Select(group => group.Joined)], models, cancellationToken);
		var separately = new List<Sentence>();
		foreach (var group in groups)
		{
			if (!Divide(group))
				separately.AddRange(group.Members);
		}
		TranslateSentences(separately, models, cancellationToken);
	}

	// The short sentences that make up a whole run each, by row, in groups of at least two.
	static List<RowGroup> FindRowGroups(List<Sentence> sentences, SentencePieceVocab vocab)
	{
		var rows = new Dictionary<HtmlElement, List<Sentence>>();
		var order = new List<List<Sentence>>();
		foreach (var sentence in sentences)
		{
			var run = sentence.Run;
			if (run.Row is null || run.Sentences.Count != 1 || sentence.Ids.Count - 1 > MaxRowSentenceTokens)
				continue;
			if (!rows.TryGetValue(run.Row, out var members))
			{
				rows.Add(run.Row, members = []);
				order.Add(members);
			}
			members.Add(sentence);
		}

		var groups = new List<RowGroup>();
		var separator = order.Count > 0 ? vocab.Encode(RowSeparator) : [];
		foreach (var members in order)
		{
			if (members.Select(member => member.Run.Cell).Distinct().Count() < 2)
				continue;
			// Groups of nearly equal size, so that none is left with a single sentence.
			int count = (members.Count + MaxRowSentences - 1) / MaxRowSentences;
			int size = (members.Count + count - 1) / count;
			for (int start = 0; start < members.Count; start += size)
			{
				var chunk = members.GetRange(start, Math.Min(size, members.Count - start));
				if (chunk.Count >= 2)
					groups.Add(Join(chunk, separator, vocab.EosId));
			}
		}
		return groups;
	}

	static RowGroup Join(List<Sentence> members, List<int> separator, int eos)
	{
		var ids = new List<int>();
		var offsets = new int[members.Count];
		for (int i = 0; i < members.Count; i++)
		{
			if (i > 0)
				ids.AddRange(separator);
			offsets[i] = ids.Count;
			ids.AddRange(members[i].Ids.Take(members[i].Ids.Count - 1));
		}
		ids.Add(eos);
		var joined = new Sentence { Run = members[0].Run, Range = default, SourceTokens = [], Ids = ids };
		return new RowGroup { Members = members, Offsets = offsets, Joined = joined };
	}

	// Divides the translation of a row among its sentences: each takes the target tokens with
	// words that are aligned with its source tokens, which must be consecutive and in order, and
	// the punctuation aligned with it next to them.
	static bool Divide(RowGroup group)
	{
		var joined = group.Joined;
		int sourceCount = joined.Ids.Count - 1;
		int targetCount = joined.TargetTokens.Count;
		var owners = new int[targetCount];
		var hasWords = new bool[targetCount];
		for (int t = 0; t < targetCount; t++)
		{
			owners[t] = Owner(ArgMax(joined.Alignment[t], sourceCount));
			var (begin, end) = joined.TargetTokens[t];
			hasWords[t] = joined.Target.AsSpan(begin, end - begin).ContainsAnyExcept(Punctuation);
		}

		var first = new int[group.Members.Count];
		var last = new int[group.Members.Count];
		Array.Fill(first, -1);
		int previous = 0;
		for (int t = 0; t < targetCount; t++)
		{
			if (!hasWords[t])
				continue;
			int owner = owners[t];
			if (owner < previous)
				return false;
			previous = owner;
			if (first[owner] < 0)
				first[owner] = t;
			last[owner] = t;
		}
		if (first.Contains(-1))
			return false;

		for (int i = 0; i < group.Members.Count; i++)
		{
			while (first[i] > 0 && !hasWords[first[i] - 1] && owners[first[i] - 1] == i)
				first[i]--;
			while (last[i] + 1 < targetCount && !hasWords[last[i] + 1] && owners[last[i] + 1] == i)
				last[i]++;
			Assign(group.Members[i], group.Offsets[i], first[i], last[i]);
		}
		return true;

		// The member whose source tokens include the joined sentence's token, or -1 for a separator.
		int Owner(int source)
		{
			for (int i = group.Members.Count - 1; i >= 0; i--)
			{
				if (source >= group.Offsets[i])
					return source < group.Offsets[i] + group.Members[i].Ids.Count - 1 ? i : -1;
			}
			return -1;
		}

		void Assign(Sentence member, int offset, int firstToken, int lastToken)
		{
			int start = Trim(joined.Target, joined.TargetTokens[firstToken]).Begin;
			int end = Trim(joined.Target, joined.TargetTokens[lastToken]).End;
			member.Target = joined.Target[start..end];
			int count = member.Ids.Count - 1;
			var tokens = new List<(int Begin, int End)>(lastToken - firstToken + 1);
			var alignment = new float[lastToken - firstToken + 1][];
			for (int t = firstToken; t <= lastToken; t++)
			{
				var (begin, tokenEnd) = joined.TargetTokens[t];
				begin = Math.Clamp(begin - start, 0, member.Target.Length);
				tokens.Add((begin, Math.Clamp(tokenEnd - start, begin, member.Target.Length)));
				var row = new float[count + 1];
				Array.Copy(joined.Alignment[t], offset, row, 0, count);
				row[count] = joined.Alignment[t][sourceCount];
				alignment[t - firstToken] = row;
			}
			member.TargetTokens = tokens;
			member.Alignment = alignment;
		}
	}

	// Characters of target tokens without words.
	static readonly System.Buffers.SearchValues<char> Punctuation = System.Buffers.SearchValues.Create(
		"  ·.,;:!?…–—-|/()[]\"'«»“”„‘’、。，：；・");

	// Token ids compared by value, to translate each distinct sentence once.
	sealed class IdsComparer : IEqualityComparer<List<int>>
	{
		public static IdsComparer Instance { get; } = new();

		public bool Equals(List<int>? x, List<int>? y) =>
			ReferenceEquals(x, y) || (x is not null && y is not null && CollectionsMarshal.AsSpan(x).SequenceEqual(CollectionsMarshal.AsSpan(y)));

		public int GetHashCode(List<int> ids)
		{
			var hash = new HashCode();
			foreach (int id in ids)
				hash.Add(id);
			return hash.ToHashCode();
		}
	}
}
