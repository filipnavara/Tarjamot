using NUnit.Framework;

namespace Tarjamot.LanguageIdentification.Tests;

[TestFixture]
public class LanguageProfileBuilderTest
{
	private const string English = "The weather is nice today and we are going to the park with our friends.";
	// "The weather is nice today and we are going to the park with our friends." in German and Czech.
	private const string German = "Das Wetter ist heute sch\u00F6n und wir gehen mit unseren Freunden in den Park.";
	private const string Czech = "Po\u010Das\u00ED je dnes hezk\u00E9 a jdeme s na\u0161imi p\u0159\u00E1teli do parku.";

	[Test]
	public void WritesTheProfileLanguageIdentifierReads()
	{
		var builder = new LanguageProfileBuilder();
		builder.Add("en", English);
		builder.Add("de", German);
		builder.Add("cs", Czech);
		var identifier = Load(builder);

		var reference = LanguageIdentifier.Load(new MemoryStream(TestProfile.Write([("en", "en", English), ("de", "de", German), ("cs", "cs", Czech)], maxNGramLength: 3)));
		Assert.That(identifier.Languages, Is.EqualTo(new[] { "en", "de", "cs" }));
		Assert.That(identifier.TableSize, Is.EqualTo(reference.TableSize));
		foreach (var text in new[] { "we are going", "wir gehen", "jdeme do parku" })
			Assert.That(identifier.Identify(text), Is.EqualTo(reference.Identify(text)));
	}

	[Test]
	public void AddingTextAgainAccumulatesCounts()
	{
		var once = new LanguageProfileBuilder();
		once.Add("en", English + "\n" + English);
		var twice = new LanguageProfileBuilder();
		twice.Add("en", English);
		twice.Add("en", English);
		Assert.That(Write(twice), Is.EqualTo(Write(once)));
		Assert.That(twice.Languages, Is.EqualTo(new[] { "en" }));
	}

	[Test]
	public void ReaderGivesTheSameCountsAsText()
	{
		var fromText = new LanguageProfileBuilder();
		fromText.Add("de", German + "\n" + English);
		var fromReader = new LanguageProfileBuilder();
		fromReader.Add("de", new StringReader(German + "\n" + English));
		Assert.That(Write(fromReader), Is.EqualTo(Write(fromText)));
	}

	[Test]
	public void KeepsTheMostFrequentNGrams()
	{
		var builder = new LanguageProfileBuilder(maxNGramLength: 3, maximumSizeOfDistribution: 10);
		builder.Add("en", English);
		builder.Add("de", German);
		var identifier = Load(builder);
		Assert.That(identifier.MaximumSizeOfDistribution, Is.EqualTo(10));

		var full = new LanguageProfileBuilder();
		full.Add("en", English);
		full.Add("de", German);
		Assert.That(identifier.TableSize, Is.LessThan(Load(full).TableSize));
	}

	[Test]
	public void OutputIsDeterministic()
	{
		var first = new LanguageProfileBuilder();
		var second = new LanguageProfileBuilder();
		foreach (var builder in new[] { first, second })
		{
			builder.Add("cs", Czech);
			builder.Add("en", English);
		}
		Assert.That(Write(first), Is.EqualTo(Write(second)));
	}

	[TestCase(0)]
	[TestCase(5)]
	public void RejectsUnsupportedNGramLengths(int maxNGramLength)
	{
		Assert.That(() => new LanguageProfileBuilder(maxNGramLength), Throws.TypeOf<ArgumentOutOfRangeException>());
	}

	private static byte[] Write(LanguageProfileBuilder builder)
	{
		using var stream = new MemoryStream();
		builder.Write(stream);
		return stream.ToArray();
	}

	private static LanguageIdentifier Load(LanguageProfileBuilder builder) => LanguageIdentifier.Load(new MemoryStream(Write(builder)));
}
