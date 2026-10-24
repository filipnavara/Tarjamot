// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy
// of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Tarjamot.BergamotMarkup;

namespace Tarjamot;

/// <summary>
/// Translates HTML as Firefox does, with a port of Bergamot's HTML mode: the text of each fragment
/// is translated in whole sentences, and each word of the translation takes the tags of the source
/// word it is aligned with. Block elements such as &lt;p&gt; or &lt;li&gt; separate sentences.
/// The fragments must be well-formed (every element closed, except void elements like &lt;br&gt;);
/// malformed markup throws <see cref="FormatException"/>.
/// </summary>
/// <example><code>
/// var options = new TranslationOptions { MarkupTranslator = BergamotMarkupTranslator.Instance };
/// using var translator = new LocalTranslator(store, options);
/// </code></example>
public sealed class BergamotMarkupTranslator : MarkupTranslator
{
	public static BergamotMarkupTranslator Instance { get; } = new();

	BergamotMarkupTranslator()
	{
	}

	internal override string[] Translate(IReadOnlyList<string> fragments, IReadOnlyList<TranslationModel> models, CancellationToken cancellationToken) =>
		MarkupTranslation.Translate(fragments, models, cancellationToken);
}
