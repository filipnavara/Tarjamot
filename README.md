# Tarjamot

Local machine translation with the Firefox Translations models (Project Bergamot, Marian
"student" transformers with int8 weights), in pure .NET. No native code. The packages are
`System.Numerics.Tensors` and `ZstdSharp.Port`, which decompresses the downloads.

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

HTML fragments keep their markup in place (see HTML mode):

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

The int8 kernel is portable `Vector128`/`Vector256` code. ARM's `SDOT` instruction is used where
available, which makes it about 3x faster, and x86's `PMADDUBSW` (AVX2, AVX-512BW), which makes
it about 4.6x faster.

More measurements and the history of the optimizations are in [docs/PERFORMANCE.md](docs/PERFORMANCE.md)
and [docs/X86-PERFORMANCE.md](docs/X86-PERFORMANCE.md).

## HTML mode

`LocalTranslator.TranslateMarkupAsync` translates HTML like Bergamot's HTML mode in Firefox.
The parts are in `Markup/`:

- **Parsing (`HtmlScanner`, `HtmlMarkup`):** a port of `xh_scanner` and `html.cpp`. The markup
  becomes plain text plus spans, each with its stack of open tags.
  - Block elements and `<br>` start a new sentence.
  - Inline elements add a space where the text would otherwise run into one word.
  - `<code>`, `<script>` and the like are kept as they are.
  - `&nbsp;` and U+00A0 become spaces. Firefox reads the DOM through innerHTML, which writes
    every U+00A0 as `&nbsp;`.
  - Malformed markup throws `FormatException`, so pass well-formed fragments (for example
    serialized from a DOM). Real-world email HTML is best divided into runs of inline content
    between block elements, each translated as one fragment.
- **Alignments:** the engine returns the soft alignment of every target token: head 0 of the
  last decoder layer's context attention, as Marian's `alignment: soft`. Hard alignment is the
  argmax with Bergamot's smoothing over continuation tokens. Pivots combine the alignments of both
  models through the English characters (`remapAlignments`).
- **Restoring:** each target token gets the tags of its aligned source token. Close tags go
  before the token's leading whitespace and open tags after it. Empty elements (`<img>`,
  comments) that match no token are written where they fit in the order.
- **Inline elements:** besides Bergamot's list, `<font>`, `<s>`, `<strike>`, `<tt>`, `<big>`,
  `<cite>`, `<dfn>`, `<label>`, `<bdi>`, `<bdo>` and `<data>` don't break sentences. They are
  common in email.

**Parity with Firefox's WASM build** (HTML mode, same models):

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

**On an HTML newsletter** (en→cs, Apple M5): sending its 19 runs of inline content as fragments
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

MIT, see [LICENSE](LICENSE). Some files are ports of third-party code and keep their original
licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
