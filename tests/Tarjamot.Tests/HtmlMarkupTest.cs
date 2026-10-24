using System.Text;
using Tarjamot.Markup;
using NUnit.Framework;

namespace Tarjamot.Tests;

/// <summary>The plain text that Bergamot's HTML mode translates, and the markup it rejects.</summary>
[TestFixture]
public class HtmlMarkupTest
{
	[TestCase("Hello <b>world</b>.", "Hello world.")]
	[TestCase("<p>One.</p><p>Two.</p>", "One.\n\nTwo.")]
	[TestCase("First line.<br>Second line.", "First line.\n\nSecond line.")]
	[TestCase("We <u>under</u>stand.", "We under stand.")]
	[TestCase("A very<wbr>long word.", "A verylong word.")]
	[TestCase("Tom &amp; Jerry &lt;3&nbsp;forever&hellip;", "Tom & Jerry <3 forever&hellip;")]
	[TestCase("Non-breaking\u00A0space.", "Non-breaking space.")]
	[TestCase("Run <code>git <b>push</b></code> now.", "Run \n\n now.")]
	[TestCase("<script>var x = '<b>';</script>Text.", "Text.")]
	[TestCase("<!-- comment -->Text<br />.", "Text\n\n.")]
	[TestCase("Read <font color=\"red\">this</font> first.", "Read this first.")]
	public void PlainText(string html, string expected)
	{
		_ = new HtmlMarkup(Encoding.UTF8.GetBytes(html), out var plainText);
		Assert.That(Encoding.UTF8.GetString(plainText), Is.EqualTo(expected));
	}

	[TestCase("<b>Bold</i>")]
	[TestCase("<b>Unclosed")]
	[TestCase("Closed</b>")]
	[TestCase("<code>Unclosed")]
	public void RejectsMalformedMarkup(string html) =>
		Assert.Throws<FormatException>(() => new HtmlMarkup(Encoding.UTF8.GetBytes(html), out _));
}
