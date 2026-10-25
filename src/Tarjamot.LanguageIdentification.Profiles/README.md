# Tarjamot.LanguageIdentification.Profiles

A prebuilt profile for [Tarjamot.LanguageIdentification](https://github.com/filipnavara/Tarjamot/tree/main/src/Tarjamot.LanguageIdentification),
embedded in the assembly, so an application can identify languages without building its own:

```csharp
var identifier = WikipediaLanguageProfile.Load();   // uses the embedded tables in place
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
dotnet run --project tools/LanguageProfiles -c Release -- tables wikipedia.dat wikipedia.tables
```

That is the plain text of random Wikipedia articles (at most 10,000 characters of each, articles
under 500 characters skipped), 500,000 characters per language, reduced to the counts of the
2,500 most frequent n-grams of 1 to 3 characters per language. The profile is about 1 MB.

The package embeds `wikipedia.tables` (0.6 MB), the lookup tables written from the profile, without
the n-grams of the languages decided by their script (`bn`, `el`, `he`, `hi`, `ko`, `ta`, `te`, `th`).
`Load` uses them in place in the assembly's image: it allocates about 2.5 KB (the language codes) and
takes microseconds, and the tables are shared memory mapped from the file rather than copied to the
heap. `wikipedia.dat` stays in the repository as the source of the tables.

## License and attribution

The code of this package is MIT, like Tarjamot.

The profile (`wikipedia.dat`) and the tables written from it (`wikipedia.tables`) are derived from the text of Wikipedia articles by Wikipedia
contributors (https://www.wikipedia.org), licensed under the
[Creative Commons Attribution-ShareAlike 4.0 International License](https://creativecommons.org/licenses/by-sa/4.0/).
The text was changed: it was reduced to the counts of the most frequent character n-grams of each
language. The profile and the tables are licensed under the same CC BY-SA 4.0 license. The binary profile format
is eM Client's (see THIRD-PARTY-NOTICES.md).
