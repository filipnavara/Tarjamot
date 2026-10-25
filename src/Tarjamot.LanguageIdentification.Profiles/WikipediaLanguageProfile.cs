namespace Tarjamot.LanguageIdentification;

/// <summary>
/// The prebuilt language profile: 55 languages built from Wikipedia articles with tools/LanguageProfiles (500,000
/// characters of random articles per language, the 2,500 most frequent n-grams of 1 to 3 characters). The profile
/// is derived from Wikipedia's text and licensed under CC BY-SA 4.0; see the package README.
/// </summary>
public static class WikipediaLanguageProfile
{
	private const string ResourceName = "Tarjamot.LanguageIdentification.Profiles.wikipedia.dat";

	/// <summary>
	/// Opens the embedded profile for <see cref="LanguageIdentifier.Load"/>.
	/// </summary>
	public static Stream OpenRead() =>
		typeof(WikipediaLanguageProfile).Assembly.GetManifestResourceStream(ResourceName)
			?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");

	/// <summary>
	/// Loads a <see cref="LanguageIdentifier"/> from the embedded profile. Loading takes a few milliseconds, so
	/// keep the identifier rather than loading it for every text.
	/// </summary>
	public static LanguageIdentifier Load(bool dropScriptDecidedLanguages = true)
	{
		using var stream = OpenRead();
		return LanguageIdentifier.Load(stream, dropScriptDecidedLanguages);
	}
}
