# Third-party notices

Tarjamot is MIT-licensed (see [LICENSE](LICENSE)). Parts of it are ports of, or follow, the
projects below. Files ported from MPL-2.0 code carry the MPL-2.0 header and remain under that
license.

## bergamot-translator (MPL-2.0)

https://github.com/browsermt/bergamot-translator

Ported, MPL-2.0 header in the file:

- `src/Tarjamot/Markup/AnnotatedText.cs`: `src/translator/annotation.{h,cpp}`
- `src/Tarjamot/Markup/HtmlMarkup.cs`: `src/translator/html.{h,cpp}`
- `src/Tarjamot/Markup/MarkupTranslation.cs`: the HTML and alignment parts of
  `src/translator/response_builder.cpp`, `text_processor.cpp` and `translation_model.cpp`
  (`remapAlignments`)

The text translation (`TextTranslation.cs`) reproduces the behavior of `AnnotatedText` and the
WASM build's batching without copying their code.

## xh_scanner (BSD)

`src/Tarjamot/Markup/HtmlScanner.cs` is a port of bergamot-translator's
`src/translator/xh_scanner.{h,cpp}`, after Andrew Fedoniouk's "Fast and Compact HTML/XML
Scanner/Tokenizer" (https://www.codeproject.com/Articles/14076), BSD license.

## Marian (MIT)

https://github.com/marian-nmt/marian-dev. Copyright (c) 2016 Marcin Junczys-Dowmunt, the
University of Edinburgh, Adam Mickiewicz University.

The transformer decoder, SSRU, layer normalization, softmax and lexical shortlist follow
Marian's implementation and its order of operations (`src/Tarjamot/Engine/MarianMath.cs`,
`MarianTranslator.cs`, `MarianModel.cs`, `Shortlist.cs`).

## sse_mathfun (zlib)

`exp_ps` in `src/Tarjamot/Engine/MarianMath.cs` follows `sse_mathfun.h` as used by Marian,
Copyright (C) 2007 Julien Pommier, zlib license (after the Cephes library).

## intgemm (MIT)

https://github.com/kpu/intgemm. Copyright (c) 2017-2020 the University of Edinburgh, Mateusz
Chudyk and Kenneth Heafield.

The int8 products follow intgemm's `Int8Shift` arithmetic, including its int16 saturation, and
the model's quantization format (`src/Tarjamot/Engine/Int8Gemm*.cs`, `QuantizedMatrix.cs`).

## musl (MIT)

https://musl.libc.org. Copyright (c) 2005-2020 Rich Felker, et al.

`src/Tarjamot/Engine/MuslMath.cs` ports musl's `expf`, `sinf` and `cosf`, including their tables.

## SentencePiece (Apache-2.0)

https://github.com/google/sentencepiece. Copyright 2016 Google Inc.

`src/Tarjamot/Engine/SentencePieceVocab.cs` reimplements SentencePiece's model loading,
normalization, unigram encoding and decoding for the models' vocabularies.

## Firefox (MPL-2.0)

https://searchfox.org/mozilla-central/source/toolkit/components/translations. Model selection
(`TranslationModelCatalog.cs`) and the engine's configuration follow Firefox's translations
component.

## Models

The models are not included. They are downloaded from Mozilla's Remote Settings; see
https://github.com/mozilla/firefox-translations-models (MPL-2.0) for their terms.
