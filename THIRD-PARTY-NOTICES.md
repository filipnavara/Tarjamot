# Third-party notices

Tarjamot is MIT-licensed (see [LICENSE](LICENSE)). Parts of it are ports of, or follow, the
projects below. The ports of MPL-2.0 code are in a separate package, Tarjamot.BergamotMarkup:
they carry the MPL-2.0 header and remain under that license, and the one file with BSD-licensed
origins carries that notice too. The NuGet packages' license expressions are `MIT` for Tarjamot and
`MPL-2.0 AND BSD-3-Clause` for Tarjamot.BergamotMarkup.

## bergamot-translator (MPL-2.0)

https://github.com/browsermt/bergamot-translator

Ported, MPL-2.0 header in the file, all in the Tarjamot.BergamotMarkup package:

- `src/Tarjamot.BergamotMarkup/AnnotatedText.cs`: `src/translator/annotation.{h,cpp}`
- `src/Tarjamot.BergamotMarkup/HtmlMarkup.cs`: `src/translator/html.{h,cpp}`
- `src/Tarjamot.BergamotMarkup/MarkupTranslation.cs`: the HTML and alignment parts of
  `src/translator/response_builder.cpp`, `text_processor.cpp` and `translation_model.cpp`
  (`remapAlignments`)
- `src/Tarjamot.BergamotMarkup/HtmlScanner.cs`: `src/translator/xh_scanner.{h,cpp}`, Bergamot's
  modified copy of the BSD-licensed xh_scanner below (the modifications are MPL-2.0)

`src/Tarjamot.BergamotMarkup/BergamotMarkupTranslator.cs` is new code in the package, MPL-2.0
like it.

The text translation (`TextTranslation.cs`) reproduces the behavior of `AnnotatedText` and the
WASM build's batching without copying their code. `ProjectedMarkupTranslator` and
`src/Tarjamot/Projection/` are written for Tarjamot (MIT) after the detag-and-project approach
described by Zenkel, Wuebker and DeNero (*Automatic Bilingual Markup Transfer*, 2021). They
use a different algorithm and copy no code from Bergamot or from the ports above; the pivot
alignment composition follows the same idea as Bergamot's `remapAlignments` (a matrix product
through the English characters) with its own code.

## xh_scanner (BSD-3-Clause)

`src/Tarjamot.BergamotMarkup/HtmlScanner.cs` is a port of bergamot-translator's
`src/translator/xh_scanner.{h,cpp}`, which is Andrew Fedoniouk's "Fast and Compact HTML/XML
Scanner/Tokenizer" (https://www.codeproject.com/Articles/14076, published under CodeProject's
"The BSD License", the 3-clause BSD license) as modified by Bergamot. The modifications are
MPL-2.0 (above); the original is:

Copyright (c) Andrew Fedoniouk (terrainformatica.com). All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted
provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of
   conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of
   conditions and the following disclaimer in the documentation and/or other materials provided
   with the distribution.
3. Neither the name of the copyright holder nor the names of its contributors may be used to
   endorse or promote products derived from this software without specific prior written
   permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER
IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT
OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## NTextCat (MIT)

https://github.com/ivanakcheurov/ntextcat. Copyright (c) Ivan Akcheurov.

`src/Tarjamot.LanguageIdentification/LanguageIdentifier.cs` extracts NTextCat's character n-grams
(each word wrapped in underscores) and computes the scores of NTextCat's naive Bayes classifier
with add-one smoothing and the distance of its `DistributionDistanceCalculator`, with its own code
and data structures.

## eM Client

https://www.emclient.com. The binary language profile format that `LanguageIdentifier` reads and
`LanguageProfileBuilder` writes was designed by the eM Client team for eM Client's language
detection; NTextCat itself stores profiles as XML.

## Wikipedia (CC BY-SA 4.0)

`src/Tarjamot.LanguageIdentification.Profiles/wikipedia.dat`, embedded in the
Tarjamot.LanguageIdentification.Profiles package, is derived from the text of Wikipedia articles
by Wikipedia contributors (https://www.wikipedia.org), licensed under the Creative Commons
Attribution-ShareAlike 4.0 International License
(https://creativecommons.org/licenses/by-sa/4.0/). The text was reduced to the counts of the most
frequent character n-grams of each language, and the profile is licensed under CC BY-SA 4.0 too.
The package's license expression is `MIT AND CC-BY-SA-4.0`.

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

`src/Tarjamot/Engine/MuslMath.cs` ports musl's `expf` and `sincosf` (the `sinf` and `cosf` kernels and argument reduction),
including their tables.

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
