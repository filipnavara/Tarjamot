# Tarjamot.BergamotMarkup

Firefox's HTML translation for [Tarjamot](https://github.com/filipnavara/Tarjamot): a port of
Bergamot's HTML mode, which puts the markup of HTML fragments back into their translations
exactly as Firefox does.

```csharp
var options = new TranslationOptions { MarkupTranslator = BergamotMarkupTranslator.Instance };
using var translator = new LocalTranslator(store, options);
var html = await translator.TranslateMarkupAsync("en", "cs", ["Click <a href=\"x\">here</a> to unsubscribe."]);
```

Without this package, Tarjamot translates HTML with its own `ProjectedMarkupTranslator`, which
does not match Firefox. See the comparison in
[docs/MARKUP-COMPARISON.md](https://github.com/filipnavara/Tarjamot/blob/main/docs/MARKUP-COMPARISON.md).

The package depends on exactly the same version of Tarjamot.

## License

MPL-2.0: the files are ports of [bergamot-translator](https://github.com/browsermt/bergamot-translator).
The HTML scanner is Andrew Fedoniouk's, under the BSD-3-Clause license. See THIRD-PARTY-NOTICES.md
in the package. Tarjamot itself is MIT.
