# HTML translation: Bergamot's algorithm and projection

Tarjamot translates HTML two ways. Both translate the text without its markup and use the
model's soft alignments (head 0 of the last decoder layer's context attention) to put the
markup back. They differ in how.

- **`BergamotMarkupTranslator`** (package `Tarjamot.BergamotMarkup`, MPL-2.0 and BSD-3-Clause) ports
  Bergamot's HTML mode. Every subword of the translation takes the stack of open tags of the
  source subword it is aligned with, and tags are written wherever the stack changes. Its output
  is Firefox's output, byte for byte.
- **`ProjectedMarkupTranslator`** (package `Tarjamot`, MIT, the default) is "detag and project"
  (Zenkel, Wuebker and DeNero, *Automatic Bilingual Markup Transfer*, Findings of EMNLP 2021;
  Hanneman and Dinu 2020). Block structure is copied as written. Each inline element is placed
  around the contiguous translated words aligned with its words.

## Result

On 134 translations of 46 fragments, projection placed the markup better than Bergamot in 47
of the 59 cases where the outputs differ. Bergamot was better in 2, and the other 75 outputs
were identical. Speed is the same. Projection does not match Firefox, so the Firefox parity
tests apply only to `BergamotMarkupTranslator`.

| Of the 59 differing translations | Count |
| --- | ---: |
| Projection better | 47 |
| Bergamot better | 2 |
| Both wrong | 6 |
| Equivalent | 4 |

Most of the difference is Japanese. Of the 38 Japanese translations with inline elements,
Bergamot left every inline element empty in 34, mostly at the start of the sentence
(`<a href="x"></a>ここをクリック…`), which drops links and formatting; projection left none
empty. It placed most of them on the right words, though a Japanese particle next to the word
is often included or the span is one token off.

## Method

`tools/MarkupComparison` translates `tools/MarkupComparison/corpus.json` with both translators
and writes every output, metrics and timings:

```bash
dotnet run --project tools/Translate -c Release -- download en ja    # and en-cs, en-de, cs-en, de-en
dotnet run --project tools/MarkupComparison -c Release -- <models directory> tools/MarkupComparison/corpus.json <output directory>
```

The corpus has 31 English fragments (links, nesting, images, `<br>`, lists, tables, `<code>`,
comments, entities, `<sub>`/`<sup>`, a tag inside a word, emphasis over several sentences,
email footers, Wikipedia sentences), 11 Czech (including the benchmark email) and 4 German
(a separable verb, a compound). They go en→cs, en→de, en→ja, cs→en, cs→de and cs→ja (both
through English), de→en and de→cs (through English). Apple M5, .NET 10, one thread, models
from Mozilla's catalog of 2026-10-24.

The automatic metrics count tags, not placement. One person (the author of the projection)
judged placement in all 59 differing outputs, listed below; no Firefox reference exists for
most of them. Treat the verdicts as a careful reading, not a benchmark.

## Automatic metrics

| Of 134 translations | Bergamot | Projection |
| --- | ---: | ---: |
| Well-formed output | 134 | 134 |
| Every element of the source present once | 126 | 128 |
| No new empty inline elements (`<b></b>`) | 91 | 133 |
| Every start tag as written in the source | 131 | 134 |
| Text equal to the plain-text translation of the same text | 122 | 131 |
| Identical to Firefox (6 references from `LocalTranslatorTest`) | 6 | 3 |

"Every element present once" counts copies as extra: both translators copy a tag onto the
two parts of a split translation (`<b>läuft</b> … <b>ab</b>`). Bergamot also drops elements
inside `<code>` (`<code></code>`). The 3 projected outputs that differ from Firefox are the
Japanese ones where Firefox writes empty tags.

## Timing

Median of 15 warm calls, interleaved, each with all fragments of a direction, one thread. Plain
text is the same text without markup through `TranslateAsync`.

| Direction | Models | Fragments | Bergamot | Projection | Plain text |
| --- | --- | ---: | ---: | ---: | ---: |
| en→cs | en-cs | 31 | 224 ms | 206 ms | 211 ms |
| en→de | en-de | 31 | 176 ms | 176 ms | 178 ms |
| en→ja | en-ja | 31 | 177 ms | 165 ms | 170 ms |
| cs→en | cs-en | 11 | 79 ms | 78 ms | 80 ms |
| cs→de | cs-en + en-de | 11 | 171 ms | 166 ms | 169 ms |
| cs→ja | cs-en + en-ja | 11 | 140 ms | 148 ms | 151 ms |
| de→en | de-en | 4 | 22 ms | 22 ms | 22 ms |
| de→cs | de-en + en-cs | 4 | 55 ms | 55 ms | 55 ms |

The translation dominates; the differences come from the text each one sends to the model.

## Where they differ

**Text.** Bergamot changes the text it translates; projection translates the text as written.

- Bergamot inserts a space at inline tags inside words, so `We <u>under</u>stand` is
  translated as "We under stand" ("Stojíme za vašimi obavami", "we stand behind your
  concerns") and `H<sub>2</sub>O` comes out as "H 2 O". Projection translates "understand"
  and keeps "H2O".
- Bergamot decodes only five entities and escapes the rest again: `&hellip;` and `&euro;`
  come out as `&amp;hellip;` and `&amp;euro;`. Projection decodes all of HTML's entities.
- Bergamot ends a sentence at `<code>`, so "Run `<code>`…`</code>` to upload your changes"
  became "Utíkej … Chcete-li nahrát změny" ("Run away … If you want to upload the changes").
  Projection keeps it as one sentence and puts the `<code>` element between the words.

**Markup.**

- Bergamot copies a tag's start tag from its parsed attributes (`<a href=x>` becomes
  `<a href="x">`); projection copies it as written.
- Bergamot drops the content of `<code>` in Japanese (`走る<code></code>`).
- Projection keeps punctuation outside a tag unless the source had it inside, and widens a tag
  to whole words in languages with spaces.
- Projection copies an element without children onto every part of a split translation that
  carries at least half the alignment mass of the best part. Bergamot copies it onto every
  aligned word. In the two cases Bergamot won, the second part had less than half: "odhlaste
  se zde" got the link only on "odhlaste", and "the King of Bohemia" got `<em>` only on "the
  King".

**Shared failures.** Both are limited by the alignments. In Japanese they are often one
token off, which puts a particle inside the tag (`サブスクリプション<b>は自動的に更新されます</b>`) or
the tag on the wrong word (`お客様の懸念<u>は</u>理解しています`).

## The differing translations

| Case | Direction | Verdict | Note |
| --- | --- | --- | --- |
| in-word | en→cs, en→de | Projection | Bergamot splits "understand" and mistranslates it, empty `<u>` |
| image-inline | en→cs, en→de | Projection | `<img>` before "icon" rather than before "on the icon" |
| code-inline | en→cs, en→de | Projection | One sentence, correct "Run"; Bergamot "Run away" |
| comment-inline | en→cs, en→ja | Equivalent | Comment one word apart |
| entities | en→cs, en→de, en→ja | Projection | Bergamot `&amp;hellip;`, `&amp;euro;5` |
| footer | en→cs | Bergamot | Bergamot links "odhlaste" and "zde", projection only "odhlaste" |
| sub-sup | en→cs, en→de | Projection | Bergamot "H 2 O", empty `<sup>`; projection's `<sup>` covers "mc2" in Czech |
| quote | en→cs, en→de | Projection | Comma outside `<q>` |
| wiki-eiffel | en→cs | Projection | Bergamot leaves the Paris link empty |
| firefox-paragraphs | en→cs, en→de | Equivalent | Only `href=x` as written |
| line-breaks | en→de | Projection | Bergamot empty `<i>` after "auswählen" |
| list | en→de | Projection | Bergamot empty link before "Benutzeranleitung" |
| link-middle | en→ja | Both wrong | Projection links "するには", Bergamot an empty link |
| link-start, link-end, bold-phrase, nested, two-links | en→ja | Projection | Bergamot empty tags at the start |
| in-word, table, sub-sup, emphasis-verb, quote | en→ja | Both wrong | Projection one token off; Bergamot empty tags |
| line-breaks, list, span-style, font, footer, heading-paragraph | en→ja | Projection | Bergamot empty tags |
| code-inline | en→ja | Projection | Bergamot `<code></code>` without its content |
| entities | en→ja | Projection | As above |
| adjacent, link-image-text, wiki-eiffel, price, abbr, strike, wiki-prague | en→ja | Projection | Bergamot empty tags |
| firefox-paragraphs | en→ja | Projection | Link on "にはリンク" (with a particle) rather than empty |
| cs-email | cs→en | Bergamot | `<em>` on "the King" and "Bohemia" (Czech "český král") |
| cs-email | cs→de | Projection | Bergamot empty `<i>`, `<span>` moved to the end |
| cs-link, cs-paragraphs, cs-meeting, cs-prague, cs-website, cs-invoice, cs-underline, cs-nested, cs-discount, cs-email | cs→ja | Projection | Bergamot empty tags |

## Choosing

Use `BergamotMarkupTranslator` when the output must match Firefox, for example to compare with
it or to keep translations of pages Firefox also translates consistent. It needs the MPL-2.0
package. Use `ProjectedMarkupTranslator` otherwise: it is MIT, keeps the text and the markup
as written, and places tags better on this corpus, most of all in Japanese. A larger,
independently judged corpus would be needed before claiming it is better in general.
