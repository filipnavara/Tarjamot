using Tarjamot.Projection;
using NUnit.Framework;

namespace Tarjamot.Tests;

/// <summary>The tree of an HTML fragment for <see cref="ProjectedMarkupTranslator"/>, and the markup it rejects.</summary>
[TestFixture]
public class HtmlFragmentParserTest
{
	[TestCase("Hello <b>world</b>.", "T(Hello )E(b:T(world))T(.)")]
	[TestCase("<p class=\"x\">One</p><P>Two</P>", "E(p:T(One))E(p:T(Two))")]
	[TestCase("A<br>B<br/>C<img src=x />", "T(A)E(br:)T(B)E(br:)T(C)E(img:)")]
	[TestCase("Tom &amp; Jerry &lt;3&nbsp;&hellip;&#x41;", "T(Tom & Jerry <3\u00A0\u2026A)")]
	[TestCase("<script>if (a < b) x = '</b>';</script>Text", "E(script:O)T(Text)")]
	[TestCase("<!-- c --><!DOCTYPE html><?xml?>", "OOO")]
	[TestCase("1 < 2 and <a title='x > y'>z</a>", "T(1 < 2 and )E(a:T(z))")]
	[TestCase("<b/>x", "E(b:)T(x)")]
	public void ParsesTree(string html, string expected) =>
		Assert.That(Describe(HtmlFragmentParser.Parse(html)), Is.EqualTo(expected));

	[Test]
	public void KeepsSourceRanges()
	{
		const string html = "x <A HREF='y'>link</A>";
		var element = (HtmlElement)HtmlFragmentParser.Parse(html)[1];
		Assert.That(html[element.Start..element.StartTagEnd], Is.EqualTo("<A HREF='y'>"));
		Assert.That(html[element.EndTagStart..element.End], Is.EqualTo("</A>"));
	}

	[TestCase("<b>Bold</i>")]
	[TestCase("<b>Unclosed")]
	[TestCase("Closed</b>")]
	[TestCase("<code>Unclosed")]
	[TestCase("<a href=\"x>y")]
	[TestCase("<!-- open")]
	public void RejectsMalformedMarkup(string html) =>
		Assert.Throws<FormatException>(() => HtmlFragmentParser.Parse(html));

	static string Describe(List<HtmlNode> nodes) => string.Concat(nodes.Select(node => node switch
	{
		HtmlText text => $"T({text.Text})",
		HtmlElement element => $"E({element.Name}:{Describe(element.Children)})",
		_ => "O",
	}));
}
