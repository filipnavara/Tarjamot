using NUnit.Framework;

namespace Tarjamot.Tests;

[TestFixture]
public class MarkupTranslatorTest
{
	[Test]
	public void ProjectionIsTheDefault()
	{
		Assert.That(TranslationOptions.Default.MarkupTranslator, Is.SameAs(ProjectedMarkupTranslator.Instance));
		Assert.That(new TranslationOptions { MarkupTranslator = BergamotMarkupTranslator.Instance }.MarkupTranslator, Is.SameAs(BergamotMarkupTranslator.Instance));
	}
}
