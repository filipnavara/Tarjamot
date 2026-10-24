namespace Tarjamot;

/// <summary>
/// How <see cref="LocalTranslator.TranslateMarkupAsync"/> and <see cref="TranslationModel.TranslateMarkup"/>
/// carry the markup of HTML fragments into their translations, set with
/// <see cref="TranslationOptions.MarkupTranslator"/>. The default,
/// <see cref="ProjectedMarkupTranslator"/>, is part of this package. The Tarjamot.Markup package
/// adds BergamotMarkupTranslator, which reproduces Firefox's output exactly.
/// </summary>
public abstract class MarkupTranslator
{
	// Implemented by this assembly and by Tarjamot.Markup only: they use the engine's internals.
	private protected MarkupTranslator()
	{
	}

	/// <summary>Translates the fragments with the models of a route (two when pivoting through English).</summary>
	internal abstract string[] Translate(IReadOnlyList<string> fragments, IReadOnlyList<TranslationModel> models, CancellationToken cancellationToken);
}
