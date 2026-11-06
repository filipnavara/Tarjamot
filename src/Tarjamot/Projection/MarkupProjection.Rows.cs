using System.Runtime.InteropServices;
using System.Text;

namespace Tarjamot.Projection;

// Short cells of a table row, such as the days of a calendar ("Mon", "Tue", ... "Sun") or the
// headers of a list, are ambiguous on their own: "Sun" alone is the star. The short runs of a row
// with them in two cells or more (a layout table's row of one cell is not a list) are therefore
// translated together as one sentence, "Mon · Tue · ... · Sun", and the translation is split at
// the separators. The models keep such a list as a list. Only runs whose markup is around their
// words take part (a <span> or <font> around the label, an icon before it): the markup goes back
// around each cell's translation as it was, with no projection. When the translation does not
// split into one part with words per cell, the runs are translated one by one.
internal static partial class MarkupProjection
{
	// A run of one sentence of at most this many tokens takes part in its row.
	const int MaxRowSentenceTokens = 8;
	const int MaxRowRuns = 12;
	const string RowSeparator = " · ";

	// The separator and what the models make of it: the katakana middle dot in Japanese, and the like.
	static readonly char[] RowSeparators = ['·', '•', '‧', '・', '･', '|', '｜'];

	static void TranslateRuns(List<Run> runs, IReadOnlyList<TranslationModel> models, CancellationToken cancellationToken)
	{
		var engine = models[0].Engine;
		var sentences = Segment(runs, engine);
		var groups = FindRowGroups(runs);
		var grouped = groups.SelectMany(group => group.Members).ToHashSet();
		var joined = Segment([.. groups.Select(group => group.Joined)], engine);
		TranslateSentences([.. sentences.Where(sentence => !grouped.Contains(sentence.Run)), .. joined], models, cancellationToken);

		var separately = new List<Sentence>();
		foreach (var group in groups)
		{
			if (!SplitRow(group))
				separately.AddRange(group.Members.Select(member => member.Sentences[0]));
		}
		TranslateSentences(separately, models, cancellationToken);

		bool omitSpaces = TextTranslation.OmitsSpacesBetweenSentences(models[^1].TargetLanguage);
		foreach (var run in runs)
		{
			Assemble(run, omitSpaces);
			if (run.IsRowCell)
				PlaceAround(run);
			else
				Project(run);
		}
	}

	sealed record RowGroup(List<Run> Members, Run Joined);

	// The runs of a row that can take part, in groups of at least two.
	static List<RowGroup> FindRowGroups(List<Run> runs)
	{
		var rows = new Dictionary<HtmlElement, List<Run>>();
		var order = new List<List<Run>>();
		foreach (var run in runs)
		{
			if (run.Row is null || !IsRowCellCandidate(run))
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
				{
					var text = string.Join(RowSeparator, chunk.Select(member => member.Text[member.Sentences[0].Range]));
					groups.Add(new RowGroup(chunk, new Run { Html = chunk[0].Html, Text = text, Items = [], IsJoined = true }));
				}
			}
		}
		return groups;
	}

	// A run of one short sentence without a separator, whose markup is all around its words: no
	// element starts or ends among them.
	static bool IsRowCellCandidate(Run run)
	{
		if (run.Sentences is not [var sentence] || sentence.Ids.Count - 1 > MaxRowSentenceTokens)
			return false;
		var (start, end) = (sentence.Range.Start.Value, sentence.Range.End.Value);
		if (run.Text.AsSpan(start, end - start).IndexOfAny(RowSeparators) >= 0)
			return false;
		foreach (var item in run.Items)
		{
			if (item.SourceStart > start && item.SourceStart < end || item.SourceEnd > start && item.SourceEnd < end)
				return false;
		}
		return true;
	}

	// Gives each member its part of the joined translation, when it splits into one part with
	// words per member.
	static bool SplitRow(RowGroup group)
	{
		var parts = group.Joined.Sentences[0].Target.Split(RowSeparators);
		if (parts.Length != group.Members.Count || !parts.All(part => part.Any(char.IsLetterOrDigit)))
			return false;
		for (int i = 0; i < parts.Length; i++)
		{
			var member = group.Members[i];
			member.Sentences[0].Target = parts[i].Trim();
			member.IsRowCell = true;
		}
		return true;
	}

	// Puts the markup of a row cell around its translation: what was before or after the words
	// stays before or after them, and an element around them goes around the translation.
	static void PlaceAround(Run run)
	{
		var sentence = run.Sentences[0];
		var (start, end) = (sentence.Range.Start.Value, sentence.Range.End.Value);
		int targetStart = sentence.TargetOffset, targetEnd = targetStart + sentence.Target.Length;
		foreach (var item in run.Items)
		{
			item.TargetStart = Map(item.SourceStart);
			item.TargetEnd = Map(item.SourceEnd);
		}

		// The whitespace before and after the sentence keeps its length unless Assemble dropped it.
		int Map(int position) => position <= start
			? Math.Min(position, targetStart)
			: Math.Min(targetEnd + (position - end), run.Target.Length);
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
