using Tarjamot.Projection;

namespace Tarjamot;

/// <summary>
/// Translates HTML by translating the text without its markup and projecting each inline element
/// onto the translation through the model's word alignments ("detag and project"). Block elements
/// and line breaks are kept exactly where they are and only the text between them is translated;
/// an inline element such as &lt;a&gt; or &lt;b&gt; is placed around the contiguous translated
/// words aligned with its words, widened to whole words. Elements without translatable text
/// (&lt;img&gt;, comments, &lt;code&gt;, &lt;script&gt;) are copied as written next to the word they
/// preceded. The markup is copied as written, so the output differs from Firefox's.
/// </summary>
public sealed class ProjectedMarkupTranslator : MarkupTranslator
{
	public static ProjectedMarkupTranslator Instance { get; } = new();

	ProjectedMarkupTranslator()
	{
	}

	internal override string[] Translate(IReadOnlyList<string> fragments, IReadOnlyList<TranslationModel> models, CancellationToken cancellationToken) =>
		MarkupProjection.Translate(fragments, models, cancellationToken);
}
