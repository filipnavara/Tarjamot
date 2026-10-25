using NUnit.Framework;

namespace Tarjamot.LanguageIdentification.Tests;

[TestFixture]
public class WikipediaLanguageProfileTest
{
	private static readonly LanguageIdentifier identifier = WikipediaLanguageProfile.Load();

	[Test]
	public void LoadsTheEmbeddedProfile()
	{
		Assert.That(identifier.Languages, Has.Count.EqualTo(55));
		Assert.That(identifier.Languages, Is.Unique);
		Assert.That(identifier.Languages, Is.SupersetOf(new[] { "en", "de", "ja", "ko", "zh-Hans", "zh-Hant", "sr-Cyrl", "sr-Latn", "nb", "fil" }));
		Assert.That(identifier.MaxNGramLength, Is.EqualTo(3));
		Assert.That(identifier.MaximumSizeOfDistribution, Is.EqualTo(2500));
	}

	[Test]
	public void EmbeddedTablesMatchTheProfile()
	{
		using var stream = File.OpenRead(Path.Combine(TestContext.CurrentContext.TestDirectory, "wikipedia.dat"));
		Assert.That(Tables(identifier), Is.EqualTo(Tables(LanguageIdentifier.Load(stream))),
			"Write wikipedia.tables with tools/LanguageProfiles after changing wikipedia.dat or the tables");
	}

	[Test]
	public void LoadingAllocatesOnlyTheLanguageCodes()
	{
		WikipediaLanguageProfile.Load();
		long before = GC.GetAllocatedBytesForCurrentThread();
		WikipediaLanguageProfile.Load();
		Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.LessThan(8 * 1024));
	}

	// "Thank you for your message, I will reply as soon as possible." and similar.
	[TestCase("Thank you for your message, I will reply as soon as possible.", "en")]
	[TestCase("Vielen Dank f\u00FCr Ihre Nachricht, ich antworte so schnell wie m\u00F6glich.", "de")]
	[TestCase("D\u011Bkuji za zpr\u00E1vu, odpov\u00EDm co nejd\u0159\u00EDve.", "cs")]
	[TestCase("\u30E1\u30C3\u30BB\u30FC\u30B8\u3092\u3042\u308A\u304C\u3068\u3046\u3054\u3056\u3044\u307E\u3059\u3002\u3067\u304D\u308B\u3060\u3051\u65E9\u304F\u8FD4\u4FE1\u3057\u307E\u3059\u3002", "ja")]
	[TestCase("\u8C22\u8C22\u4F60\u7684\u6D88\u606F\uFF0C\u6211\u4EEC\u4F1A\u5C3D\u5FEB\u56DE\u590D\u4F60\u3002", "zh-Hans")]
	[TestCase("\u8B1D\u8B1D\u4F60\u7684\u6D88\u606F\uFF0C\u6211\u5011\u6703\u76E1\u5FEB\u56DE\u8986\u4F60\u3002", "zh-Hant")]
	[TestCase("\u0425\u0432\u0430\u043B\u0430 \u043D\u0430 \u043F\u043E\u0440\u0443\u0446\u0438, \u043E\u0434\u0433\u043E\u0432\u043E\u0440\u0438\u045B\u0443 \u0432\u0430\u043C \u0448\u0442\u043E \u043F\u0440\u0435.", "sr-Cyrl")]
	[TestCase("\uBA54\uC2DC\uC9C0 \uAC10\uC0AC\uD569\uB2C8\uB2E4. \uCD5C\uB300\uD55C \uBE68\uB9AC \uB2F5\uC7A5\uD558\uACA0\uC2B5\uB2C8\uB2E4.", "ko")]
	[TestCase("\u0C2E\u0C40 \u0C38\u0C02\u0C26\u0C47\u0C36\u0C3E\u0C28\u0C3F\u0C15\u0C3F \u0C27\u0C28\u0C4D\u0C2F\u0C35\u0C3E\u0C26\u0C3E\u0C32\u0C41, \u0C28\u0C47\u0C28\u0C41 \u0C35\u0C40\u0C32\u0C48\u0C28\u0C02\u0C24 \u0C24\u0C4D\u0C35\u0C30\u0C17\u0C3E \u0C38\u0C2E\u0C3E\u0C27\u0C3E\u0C28\u0C02 \u0C07\u0C38\u0C4D\u0C24\u0C3E\u0C28\u0C41.", "te")]
	public void IdentifiesLanguage(string text, string expected)
	{
		var ranking = identifier.Identify(text);
		Assert.That(identifier.Languages[ranking[0].Language], Is.EqualTo(expected));
	}

	private static byte[] Tables(LanguageIdentifier identifier)
	{
		using var stream = new MemoryStream();
		identifier.WriteTables(stream);
		return stream.ToArray();
	}
}
