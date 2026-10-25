# Tarjamot.LanguageIdentification.Profiles

A prebuilt profile for [Tarjamot.LanguageIdentification](https://github.com/filipnavara/Tarjamot/tree/main/src/Tarjamot.LanguageIdentification),
embedded in the assembly, so an application can identify languages without building its own:

```csharp
var identifier = WikipediaLanguageProfile.Load();   // keep it, loading takes a few milliseconds
var ranking = identifier.Identify(text);
var language = ranking.Length > 0 ? identifier.Languages[ranking[0].Language] : null;
```

## Languages

55 languages: `af`, `ar`, `bg`, `bn`, `bs`, `ca`, `cs`, `cy`, `da`, `de`, `el`, `en`, `es`, `et`, `fa`, `fi`, `fil`, `fr`, `ga`, `he`, `hi`, `hr`, `hu`, `id`, `is`, `it`, `ja`, `kk`, `ko`, `lt`, `lv`, `mg`, `ms`, `mt`, `nb`, `nl`, `pl`, `pt`, `ro`, `ru`, `sk`, `sl`, `sr-Cyrl`, `sr-Latn`, `sv`, `sw`, `ta`, `te`, `th`, `tr`, `uk`, `ur`, `vi`, `zh-Hans`, `zh-Hant`.

`zh-Hans`/`zh-Hant` and `sr-Cyrl`/`sr-Latn` are the same articles of Chinese and Serbian Wikipedia
in each script, `nb` comes from the Norwegian (Bokmål) Wikipedia and `fil` from the Tagalog one.

## How it was built

With `tools/LanguageProfiles` from this repository, in October 2026:

```
dotnet run --project tools/LanguageProfiles -c Release -- download corpus af ar bg bn bs ca cs cy da de el en es et fa fi fil fr ga he hi hr hu id is it ja kk ko lt lv mg ms mt nb nl pl pt ro ru sk sl sr-Cyrl sr-Latn sv sw ta te th tr uk ur vi zh-Hans zh-Hant
dotnet run --project tools/LanguageProfiles -c Release -- build corpus wikipedia.dat
```

That is the plain text of random Wikipedia articles (at most 10,000 characters of each, articles
under 500 characters skipped), 500,000 characters per language, reduced to the counts of the
2,500 most frequent n-grams of 1 to 3 characters per language. The profile is about 1 MB and
takes about 0.65 MB in memory.

## License and attribution

The code of this package is MIT, like Tarjamot.

The profile (`wikipedia.dat`) is derived from the text of Wikipedia articles by Wikipedia
contributors (https://www.wikipedia.org), licensed under the
[Creative Commons Attribution-ShareAlike 4.0 International License](https://creativecommons.org/licenses/by-sa/4.0/).
The text was changed: it was reduced to the counts of the most frequent character n-grams of each
language. The profile is licensed under the same CC BY-SA 4.0 license. The binary profile format
is eM Client's (see THIRD-PARTY-NOTICES.md).
