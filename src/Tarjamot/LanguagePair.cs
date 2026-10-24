namespace Tarjamot;

/// <summary>
/// Translation direction with BCP 47 language tags as used by the Firefox models, e.g. "cs",
/// "en", "zh-Hans". Tags compare case-insensitively.
/// </summary>
public readonly record struct LanguagePair(string Source, string Target)
{
	/// <summary>The language all models translate from or to; other pairs pivot through it.</summary>
	public const string PivotLanguage = "en";

	public bool Equals(LanguagePair other) =>
		string.Equals(Source, other.Source, StringComparison.OrdinalIgnoreCase) &&
		string.Equals(Target, other.Target, StringComparison.OrdinalIgnoreCase);

	public override int GetHashCode() => HashCode.Combine(
		StringComparer.OrdinalIgnoreCase.GetHashCode(Source),
		StringComparer.OrdinalIgnoreCase.GetHashCode(Target));

	public override string ToString() => $"{Source}-{Target}";

	/// <summary>
	/// Finds the models that translate from one language to another: one direct model, or two
	/// models through <see cref="PivotLanguage"/> as Firefox does. Returns null when the
	/// available models do not cover the direction.
	/// </summary>
	public static LanguagePair[]? FindRoute(IEnumerable<LanguagePair> available, string source, string target)
	{
		var pairs = available.ToHashSet();
		var direct = new LanguagePair(source, target);
		if (pairs.Contains(direct))
			return [direct];
		var first = new LanguagePair(source, PivotLanguage);
		var second = new LanguagePair(PivotLanguage, target);
		return pairs.Contains(first) && pairs.Contains(second) ? [first, second] : null;
	}
}
