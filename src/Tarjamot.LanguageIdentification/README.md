# Tarjamot.LanguageIdentification

Compact language identification in pure .NET: naive Bayes over character n-grams of 1 to 4
characters, scored like NTextCat and loaded from profiles in eM Client's binary profile format. It
does not depend on the Tarjamot translation engine.

```csharp
using var stream = File.OpenRead("profiles.dat");
var identifier = LanguageIdentifier.Load(stream);

var ranking = identifier.Identify(text);               // most likely first
if (ranking.Length > 0)
{
	var (index, score) = ranking[0];
	var language = identifier.Languages[index];        // "en", "de", "zh-Hant", ...
	var distance = identifier.CalculateDistance(index, text); // 0 (same n-gram mix) to 1
}
```

- All languages share one sorted table of n-grams packed into 64-bit keys, with each language's
  log-count stored in a byte. `WriteTables` writes these tables and `LoadTables` uses them in place
  from any `ReadOnlyMemory<byte>`, for example an embedded resource, without copying them;
  `Identify(text, destination)` writes the ranking to a span without allocating. eM Client's 45-language profile, built from NTextCat's, takes about
  0.5 MB in memory against about 8 MB with NTextCat's own data structures, and text is identified without allocating per n-gram.
- The scores are NTextCat's naive Bayes with add-one smoothing, up to the byte rounding.
- The Unicode scripts of the text limit the candidates first. A language that is the only one
  written in its script (Greek, Hebrew, Thai, Korean, ...) is decided by the script, and its
  n-grams are not loaded; pass `dropScriptDecidedLanguages: false` to `Load` to score them anyway.

The package does not include a profile. `Tarjamot.LanguageIdentification.Profiles` has a prebuilt
one for 55 languages (`WikipediaLanguageProfile.Load()`), `LanguageProfileBuilder` writes one from
training text, and `tools/LanguageProfiles` in the repository builds one from Wikipedia:

```
dotnet run --project tools/LanguageProfiles -c Release -- download corpus en de cs zh-Hans zh-Hant
dotnet run --project tools/LanguageProfiles -c Release -- build corpus profiles.dat
dotnet run --project tools/LanguageProfiles -c Release -- tables profiles.dat profiles.tables
```

`download` takes random Wikipedia articles until each language has 500,000 characters (at most
10,000 from one article). Chinese and Serbian Wikipedia store mixed scripts and convert them when
read, so `zh-Hans`/`zh-Hant` and `sr-Cyrl`/`sr-Latn` are the same articles in each script, and
the identifier itself tells Simplified from Traditional Chinese. Wikipedia's text is CC BY-SA.

`build` keeps the 2,500 most frequent n-grams of 1 to 3 characters per language by default. On
eM Client's UI translations in 29 languages (a 55-language profile against eM Client's previous
45-language one, which was built from NTextCat's profiles), that profile took 0.65 MB instead of 0.5 MB and was more accurate at every length: 84.9%
instead of 83.4% for 15-40 characters, 97.3% instead of 94.4% for paragraphs, and Simplified or
Traditional Chinese right for 91% of single words, against 82% for a separate detector of the
characters that differ. Longer n-grams or 4,000-8,000 n-grams per language cost up to four times
the memory for at most 2.5 points on single words and nothing on longer text.
