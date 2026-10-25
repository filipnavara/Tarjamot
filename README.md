# Tarjamot

Local machine translation with the Firefox Translations models (Project Bergamot, Marian
"student" transformers with int8 weights), in pure .NET. No native code. The package builds for
.NET 10 and .NET 11 and depends on `System.Numerics.Tensors`; the .NET 10 build also uses
`ZstdSharp.Port` to decompress the downloads, the .NET 11 build the runtime's `ZstandardStream`.

Status: experimental. The API may change before 1.0.

## Usage

```csharp
using var httpClient = new HttpClient();
var store = new TranslationModelStore(modelsDirectory, httpClient);

// Download (needs network; Firefox's model catalog on Mozilla's Remote Settings).
var catalog = await TranslationModelCatalog.LoadAsync(httpClient, TranslationPlatform.Desktop);
foreach (var pair in catalog.FindRoute("cs", "de")!)      // cs-en + en-de
{
	var files = catalog.GetFiles(pair);
	if (!store.IsInstalled(pair, files))
		await store.InstallAsync(pair, files, progress);
}

// Translate (offline, installed models only).
using var translator = new LocalTranslator(store);
var german = await translator.TranslateAsync("cs", "de", czechText);
```

HTML fragments keep their markup in place (see HTML mode; Firefox's exact output needs the
`Tarjamot.BergamotMarkup` package):

```csharp
var html = await translator.TranslateMarkupAsync("en", "cs", ["Click <a href=\"x\">here</a> to unsubscribe."]);
```

`TranslationModel.Load(directory, targetLanguage)` loads a single model directly.

Command line, see `tools/Translate`:

```
dotnet run --project tools/Translate -c Release -- languages
dotnet run --project tools/Translate -c Release -- download cs de
echo "Dobrý den, jak se máte?" | dotnet run --project tools/Translate -c Release -- cs de
dotnet run --project tools/Translate -c Release -- cs de --input article.txt
```

### Publishing with NativeAOT

The library and `tools/Translate` are NativeAOT-compatible. A native binary starts in about
40 ms instead of about 200 ms and uses about 55 MB less memory, at the same warm speed (see
[docs/PERFORMANCE.md](docs/PERFORMANCE.md#nativeaot)):

```
dotnet publish tools/Translate -c Release -r osx-arm64 -p:PublishAot=true -o publish
```

The int8 kernel is chosen when the binary is compiled, not when it runs. osx-arm64 targets the
Apple M1 and includes `SDOT`. NativeAOT's default x64 target does not include AVX2, so the binary
contains only the portable kernel, which is about 4.6x slower. Add
`-p:IlcInstructionSet=x86-64-v3` for AVX2, or `x86-64-v4` for AVX-512. Such a binary does not
start on CPUs without those instructions. The same applies to applications that publish the
library with NativeAOT.

## What matches Firefox

The output is meant to be the same as Firefox 158 (WASM engine 4.0, `useLexicalShortlist` off).

- Arithmetic: int8 products with intgemm's `Int8Shift` semantics (including int16 saturation of
  product pairs), Marian's `exp_ps`, softmax, layer norm and SSRU, and musl's `expf`/`sinf`/`cosf`.
- Segmentation: Firefox's WASM build calls `Intl.Segmenter` (ICU, Unicode UAX #29 sentence
  rules, no abbreviation exceptions). It does not use ssplit. `SentenceSplitter` implements
  those rules. Sentences are never cut at `max-length-break`.
- Output assembly: whitespace between source sentences is copied, line breaks are kept, there
  are no spaces between sentences for Japanese and Chinese targets, and a space is inserted
  between sentences when the source had none (Chinese to English). See Bergamot's
  `AnnotatedText::handleEndingWhitespace`.
- Pivoting: two models through English, keeping the source segmentation (`translateViaPivoting`).
- Model selection: the same version and platform rules as `TranslationsParent.sys.mjs`.
- Separate source and target vocabularies (en-ja, en-zh, zh-Hant-en) and the "tiny", "base" and
  "base-memory" architectures.

Measured against Firefox and its engine:

| Test | Identical |
|---|---|
| cs-en Wikipedia article, per sentence | 198 of 202 sentences |
| zh-Hans-en Wikipedia article | 152 of 153 sentences, 38 of 38 paragraphs |
| cs-de through English | 198 of 202 sentences |
| Whitespace, en-ja and cs-ja checks | 14 of 14 |

The remaining differences come from the summation order of Eigen's blocked matrix product in
encoder self-attention, and from Bergamot's padding when it batches the sentences of one request.

## Speed

Apple M5, warm, Czech Wikipedia article (3,136 words):

| Engine | Words/s |
|---|---|
| Firefox 158 | 231-287 |
| This library, 1 thread | 797-937 |
| This library, 1 thread, shortlist | 1,074-1,386 |
| This library, multi-threaded | 1,348-2,059 |

Chinese article (7,759 characters): Firefox 15 s, this library 4.7-6.2 s on one thread.

The int8 kernel is portable `Vector128` code. ARM's `SDOT` instruction is used where
available, which makes it about 3x faster, and x86's `PMADDUBSW` (AVX2, AVX-512BW), which makes
it about 4.6x faster. The runtime switches `DOTNET_EnableArm64Dp=0` and `DOTNET_EnableAVX2=0`
select the portable kernel.

More measurements and the history of the optimizations are in [docs/PERFORMANCE.md](docs/PERFORMANCE.md)
and [docs/X86-PERFORMANCE.md](docs/X86-PERFORMANCE.md).

## HTML mode

`LocalTranslator.TranslateMarkupAsync` translates HTML fragments. The text is translated
without its markup, and the markup is put back along the model's alignments: the soft alignment
of every target token, head 0 of the last decoder layer's context attention (Marian's
`alignment: soft`). Pivots combine the alignments of both models through the English text.
Fragments must be well-formed; malformed markup throws `FormatException`. Real-world email HTML
is best divided into runs of inline content between block elements, each translated as one
fragment. `TranslationOptions.MarkupTranslator` chooses how the markup is put back:

- **`ProjectedMarkupTranslator`** (the default, in this package, MIT):
  - Block elements and `<br>` are copied as written, and only the text between them is
    translated.
  - Each inline element (`<a>`, `<b>`, `<span>`, `<font>` and the like) goes around the
    contiguous translated words aligned with its words, widened to whole words. Punctuation
    stays outside unless the source had it inside.
  - An element without children is copied onto each part of a split translation
    (`<b>läuft</b> … <b>ab</b>`).
  - `<img>`, comments, `<code>` and `<script>` are copied as written before the translation of
    the word they preceded.
  - Tags keep their attributes as written; the text is decoded and re-encoded (`&hellip;`
    becomes "…").
- **`BergamotMarkupTranslator`** (package `Tarjamot.BergamotMarkup`, MPL-2.0): a port of Bergamot's
  HTML mode, which gives Firefox's output byte for byte.

```csharp
var options = new TranslationOptions { MarkupTranslator = BergamotMarkupTranslator.Instance };
using var translator = new LocalTranslator(store, options);
```

[docs/MARKUP-COMPARISON.md](docs/MARKUP-COMPARISON.md) compares the two on 134 translations.
Projection placed the markup better in 47 of the 59 that differ, most of them Japanese, where
Bergamot leaves tags empty. Bergamot was better in 2. Both take the same time.

**Bergamot's HTML mode.** The parts are in `src/Tarjamot.BergamotMarkup`:

- **Parsing (`HtmlScanner`, `HtmlMarkup`):** a port of `xh_scanner` and `html.cpp`. The markup
  becomes plain text plus spans, each with its stack of open tags.
  - Block elements and `<br>` start a new sentence.
  - Inline elements add a space where the text would otherwise run into one word.
  - `<code>`, `<script>` and the like are kept as they are.
  - `&nbsp;` and U+00A0 become spaces. Firefox reads the DOM through innerHTML, which writes
    every U+00A0 as `&nbsp;`.
- **Alignments:** hard alignment is the argmax with Bergamot's smoothing over continuation
  tokens. Pivots use Bergamot's `remapAlignments`.
- **Restoring:** each target token gets the tags of its aligned source token. Close tags go
  before the token's leading whitespace and open tags after it. Empty elements (`<img>`,
  comments) that match no token are written where they fit in the order.
- **Inline elements:** besides Bergamot's list, `<font>`, `<s>`, `<strike>`, `<tt>`, `<big>`,
  `<cite>`, `<dfn>`, `<label>`, `<bdi>`, `<bdo>` and `<data>` don't break sentences. They are
  common in email.

**Parity with Firefox's WASM build** (`BergamotMarkupTranslator`, same models):

| Test set | Identical output |
| --- | --- |
| 25 curated en cases (links, nesting, `<img>`, `<br>`, entities, `<code>`, comments), en→cs and en→ja | 50 of 50 |
| 4 curated cs cases, cs→en and cs→de (pivot) | 8 of 8 |
| Wikipedia paragraphs: cs→en/de/ja, en→cs/ja, zh→en | 194 of 217 |

Of the 23 differing paragraphs, 20 differ in the translated words and 3 only in the markup. In
an experiment, emulating Bergamot's batching (bucketing by length, batches padded to their
longest sentence, padded softmax) raised the count to 202 of 217, with 1 markup-only
difference. So the rest comes from numerics (batch padding and the order of the encoder's
summation), not from the markup code. The emulation costs throughput, so it was not kept.

**On an HTML newsletter** (en→cs, Apple M5, `BergamotMarkupTranslator`): sending its 19 runs of inline content as fragments
took about as long as sending its 40 text nodes as plain text (0.6–1.4 s warm), with identical
tags in the output. Sentences split by markup, such as "This email was sent to
`<span>`test@…`</span>` by Samsung", are translated as a whole.

## Models

- **Size:** a model is 12-45 MB compressed, 20-60 MB on disk. A pivot needs two models, but the
  English side is shared between pairs.
- **Source:** Mozilla's Remote Settings
  (`firefox.settings.services.mozilla.com`, collection `translations-models-v2`). Downloads come
  from Mozilla's CDN and are checked against the SHA-256 hashes in the records.
- **Signatures:** Firefox also verifies the collection's content signature. This library relies on
  HTTPS and the hashes.
- **License:** Mozilla's firefox-translations-models repository is MPL-2.0. Check the terms for
  the models and their training data before redistributing them.
- **Updates:** `IsInstalled` compares the installed hashes with the catalog, so an update is a
  re-download.
- **Mobile:** some pairs have smaller models for Android (`TranslationPlatform.Mobile`).
- **Memory:** loaded models retain about 50 MB for cs-en and 103 MB for zh-Hans-en in managed
  memory before translation buffers. `LocalTranslator.UnloadModels` frees the models; idle
  models are also released after a while (see `TranslationOptions`).

**Pairs without English.**

- **How it works:** Mozilla publishes only X→English and English→X models, and Firefox pivots
  every other pair through English. `LocalTranslator` does the same, so any two of the 59
  languages work.
- **Costs:** pivoting takes about twice the time, needs two models, and loses quality. Errors
  add up, and English loses distinctions such as formal and informal "you" and grammatical
  gender. For example, cs→ja through English turned "please send your comments by Friday" into
  "please comment by Friday".

## Not implemented

- Content signature verification of the Remote Settings collection.
- Bergamot's batch padding, which in rare cases changes a word when Firefox translates many
  sentences in one request.
- The int8 "intgemm8" model variant without precomputed alphas (`int8shiftAll`). The current
  catalog contains only `*.intgemm.alphas.bin` models.

## License

`Tarjamot` is MIT, see [LICENSE](LICENSE). Parts follow or reimplement MIT, zlib and
Apache-2.0 code. `Tarjamot.BergamotMarkup` ports Bergamot's HTML mode and is MPL-2.0, with a
BSD-3-Clause HTML scanner. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
