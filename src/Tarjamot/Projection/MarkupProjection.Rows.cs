using System.Runtime.InteropServices;
using System.Text;

namespace Tarjamot.Projection;

// Short cells of a table row, such as the days of a calendar ("Mon", "Tue", ... "Sun") or the
// headers of a list, are ambiguous on their own: "Sun" alone is the star. The short runs of a row
// with them in two cells or more (a layout table's row of one cell is not a list) are therefore
// joined into one run, "Mon · Tue · ... · Sun", in which each cell is an element around its run,
// with the run's own elements inside. The models keep such a list as a list, and the cells are
// projected onto the translation like any other element. When the projection does not divide
// the translation cleanly (a cell without words, cells out of order, words outside the cells),
// the runs are translated one by one.
internal static partial class MarkupProjection
{
	// A run of one sentence of at most this many tokens takes part in its row.
	const int MaxRowSentenceTokens = 8;
	const int MaxRowRuns = 12;
	const string RowSeparator = " · ";

	sealed class RowGroup
	{
		public required List<Run> Members { get; init; }
		public required Run Joined { get; init; }
		// The index of each member's cell element in the joined run.
		public required int[] Cells { get; init; }
		// Each member's whitespace and markup without text before and after its words, copied as written.
		public required (string Before, string After)[] Edges { get; init; }
	}

	static void TranslateRuns(List<Run> runs, IReadOnlyList<TranslationModel> models, CancellationToken cancellationToken)
	{
		var engine = models[0].Engine;
		var sentences = Segment(runs, engine);
		bool omitSpaces = TextTranslation.OmitsSpacesBetweenSentences(models[^1].TargetLanguage);
		var groups = FindRowGroups(runs, omitSpaces);
		var grouped = groups.SelectMany(group => group.Members).ToHashSet();
		var joined = groups.Select(group => group.Joined).ToList();
		TranslateSentences([.. sentences.Where(sentence => !grouped.Contains(sentence.Run)), .. Segment(joined, engine)], models, cancellationToken);

		var separately = new List<Run>();
		foreach (var group in groups)
		{
			Assemble(group.Joined, omitSpaces);
			Project(group.Joined);
			if (!DivideRow(group))
				separately.AddRange(group.Members);
		}
		TranslateSentences([.. separately.SelectMany(run => run.Sentences)], models, cancellationToken);
		foreach (var run in runs)
		{
			if (run.Output is not null)
				continue;
			Assemble(run, omitSpaces);
			Project(run);
		}
	}

	// The runs of one short sentence, by row, in groups of at least two.
	static List<RowGroup> FindRowGroups(List<Run> runs, bool omitSpaces)
	{
		var rows = new Dictionary<HtmlElement, List<Run>>();
		var order = new List<List<Run>>();
		foreach (var run in runs)
		{
			if (run.Row is null || run.Sentences is not [var sentence] || sentence.Ids.Count - 1 > MaxRowSentenceTokens)
				continue;
			if (!rows.TryGetValue(run.Row, out var members))
			{
				rows.Add(run.Row, members = []);
				order.Add(members);
			}
			members.Add(run);
		}

		var groups = new List<RowGroup>();
		foreach (var members in order)
		{
			if (members.Select(member => member.Cell).Distinct().Count() < 2)
				continue;
			// Groups of nearly equal size, so that none is left with a single run.
			int count = (members.Count + MaxRowRuns - 1) / MaxRowRuns;
			int size = (members.Count + count - 1) / count;
			for (int start = 0; start < members.Count; start += size)
			{
				var chunk = members.GetRange(start, Math.Min(size, members.Count - start));
				if (chunk.Count >= 2)
					groups.Add(JoinRow(chunk, omitSpaces));
			}
		}
		return groups;
	}

	// One run of the members' text joined with the separator: each member is a cell element, and
	// its elements are copied into it.
	static RowGroup JoinRow(List<Run> members, bool omitSpaces)
	{
		var text = new StringBuilder();
		var items = new List<Item>();
		var cells = new int[members.Count];
		var edges = new (string Before, string After)[members.Count];
		for (int i = 0; i < members.Count; i++)
		{
			var member = members[i];
			if (i > 0)
				text.Append(RowSeparator);
			// The cell covers the member's words. The whitespace around them stays in place, as
			// between sentences, with the markup without text in it (an icon before the label).
			int leading = member.Text.Length - member.Text.AsSpan().TrimStart().Length;
			int trailing = member.Text.AsSpan().TrimEnd().Length;
			bool IsEdge(Item item) => item.Parent < 0 && item.IsAnchor && (item.SourceStart <= leading || item.SourceStart >= trailing);
			edges[i] = (Edge(member, 0, leading, item => IsEdge(item) && item.SourceStart <= leading),
				Edge(member, trailing, member.Text.Length, item => IsEdge(item) && item.SourceStart > leading));

			int offset = text.Length - leading;
			int start = text.Length, end = text.Length + trailing - leading;
			cells[i] = items.Count;
			items.Add(new Item { Open = "", Close = "", IsCell = true, Parent = -1, SourceStart = start, SourceEnd = end });
			text.Append(member.Text.AsSpan(leading, trailing - leading));
			var indexes = new int[member.Items.Count];
			for (int k = 0; k < member.Items.Count; k++)
			{
				var item = member.Items[k];
				if (IsEdge(item))
					continue;
				indexes[k] = items.Count;
				items.Add(new Item
				{
					Open = item.Open,
					Close = item.Close,
					MarkupStart = item.MarkupStart,
					MarkupEnd = item.MarkupEnd,
					Parent = item.Parent < 0 ? cells[i] : indexes[item.Parent],
					SourceStart = Math.Clamp(item.SourceStart + offset, start, end),
					SourceEnd = Math.Clamp(item.SourceEnd + offset, start, end),
				});
			}
		}
		var joined = new Run { Html = members[0].Html, Text = text.ToString(), Items = items, IsJoined = true };
		return new RowGroup { Members = members, Joined = joined, Cells = cells, Edges = edges };

		// The whitespace of a member between two positions with the markup in it, as written.
		string Edge(Run member, int from, int to, Func<Item, bool> include)
		{
			var edge = new StringBuilder();
			int position = from;
			foreach (var item in member.Items.Where(include))
			{
				AppendWhitespace(edge, member.Text.AsSpan(position, Math.Max(item.SourceStart - position, 0)));
				position = Math.Max(position, item.SourceStart);
				edge.Append(item.Open);
			}
			AppendWhitespace(edge, member.Text.AsSpan(position, Math.Max(to - position, 0)));
			return edge.ToString();
		}

		void AppendWhitespace(StringBuilder edge, ReadOnlySpan<char> whitespace)
		{
			if (!omitSpaces || whitespace.Contains('\n'))
				edge.Append(whitespace);
		}
	}

	// Renders each member from its cell when every cell took words, in order, and only text
	// without words is left between them.
	static bool DivideRow(RowGroup group)
	{
		var run = group.Joined;
		int previousEnd = 0;
		foreach (int cell in group.Cells)
		{
			var item = run.Items[cell];
			if (item.IsAnchor || item.TargetEnd <= item.TargetStart || item.TargetStart < previousEnd
				|| HasWords(run.Target.AsSpan(previousEnd, item.TargetStart - previousEnd)))
				return false;
			previousEnd = item.TargetEnd;
		}
		if (HasWords(run.Target.AsSpan(previousEnd)))
			return false;

		for (int i = 0; i < group.Members.Count; i++)
		{
			var item = run.Items[group.Cells[i]];
			var (before, after) = group.Edges[i];
			group.Members[i].Output = string.Concat(before, Render(run, group.Cells[i], item.TargetStart, item.TargetEnd), after);
		}
		return true;

		static bool HasWords(ReadOnlySpan<char> text)
		{
			foreach (char c in text)
			{
				if (char.IsLetterOrDigit(c))
					return true;
			}
			return false;
		}
	}

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
