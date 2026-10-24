using NUnit.Framework;

namespace Tarjamot.Tests;

[TestFixture]
public class TranslationModelCatalogTest
{
	static readonly Uri AttachmentsBase = new("https://attachments.example/");

	const string Records = """
		{ "data": [
			{ "name": "model.csen.intgemm.alphas.bin", "version": "3.0", "fileType": "model", "sourceLanguage": "cs", "targetLanguage": "en", "architecture": "base-memory", "attachment": { "location": "a/csen-model-30.zst", "size": 100, "hash": "x" }, "decompressedHash": "h1", "decompressedSize": 1000, "filter_expression": "" },
			{ "name": "model.csen.intgemm.alphas.bin", "version": "3.1", "fileType": "model", "sourceLanguage": "cs", "targetLanguage": "en", "architecture": "base-memory", "attachment": { "location": "a/csen-model-31.zst", "size": 100, "hash": "x" }, "decompressedHash": "h2", "decompressedSize": 1000, "filter_expression": "" },
			{ "name": "model.csen.intgemm.alphas.bin", "version": "4.0", "fileType": "model", "sourceLanguage": "cs", "targetLanguage": "en", "architecture": "base", "attachment": { "location": "a/csen-model-40.zst", "size": 100, "hash": "x" }, "decompressedHash": "h3", "decompressedSize": 1000, "filter_expression": "" },
			{ "name": "vocab.csen.spm", "version": "3.0", "fileType": "vocab", "sourceLanguage": "cs", "targetLanguage": "en", "attachment": { "location": "a/csen-vocab-30.zst", "size": 10, "hash": "x" }, "decompressedHash": "h4", "decompressedSize": 20, "filter_expression": "" },
			{ "name": "vocab.csen.spm", "version": "3.1", "fileType": "vocab", "sourceLanguage": "cs", "targetLanguage": "en", "attachment": { "location": "a/csen-vocab-31.zst", "size": 10, "hash": "x" }, "decompressedHash": "h5", "decompressedSize": 20, "filter_expression": "" },
			{ "name": "lex.50.50.csen.s2t.bin", "version": "3.1", "fileType": "lex", "sourceLanguage": "cs", "targetLanguage": "en", "attachment": { "location": "a/csen-lex-31.zst", "size": 10, "hash": "x" }, "decompressedHash": "h6", "decompressedSize": 20, "filter_expression": "" },
			{ "name": "model.enja.intgemm.alphas.bin", "version": "3.0", "fileType": "model", "sourceLanguage": "en", "targetLanguage": "ja", "attachment": { "location": "a/enja-model.zst", "size": 100, "hash": "x" }, "decompressedHash": "h7", "decompressedSize": 1000, "filter_expression": "env.appinfo.OS != 'Android'" },
			{ "name": "model.enja.intgemm.alphas.bin", "version": "3.1", "fileType": "model", "sourceLanguage": "en", "targetLanguage": "ja", "attachment": { "location": "a/enja-model-android.zst", "size": 100, "hash": "x" }, "decompressedHash": "h8", "decompressedSize": 1000, "filter_expression": "env.appinfo.OS == 'Android'" },
			{ "name": "model.enko.intgemm.alphas.bin", "version": "3.0a1", "fileType": "model", "sourceLanguage": "en", "targetLanguage": "ko", "attachment": { "location": "a/enko-model.zst", "size": 100, "hash": "x" }, "decompressedHash": "h9", "decompressedSize": 1000, "filter_expression": "env.channel == 'default' || env.channel == 'nightly'" }
		] }
		""";

	[Test]
	public void SelectsHighestSupportedVersionAndMatchingFiles()
	{
		var catalog = TranslationModelCatalog.Parse(Records, AttachmentsBase, TranslationPlatform.Desktop);
		var files = catalog.GetFiles(new LanguagePair("cs", "en"), includeShortlist: true);
		Assert.That(files.Select(file => (file.Name, file.Version)), Is.EquivalentTo(new[]
		{
			("lex.50.50.csen.s2t.bin", "3.1"),
			("model.csen.intgemm.alphas.bin", "3.1"),
			("vocab.csen.spm", "3.1"),
		}));
		Assert.That(files.Single(file => file.FileType == "model").Location, Is.EqualTo(new Uri("https://attachments.example/a/csen-model-31.zst")));
	}

	[Test]
	public void ShortlistIsOptional()
	{
		var catalog = TranslationModelCatalog.Parse(Records, AttachmentsBase, TranslationPlatform.Desktop);
		Assert.That(catalog.GetFiles(new LanguagePair("cs", "en")).Select(file => file.FileType), Is.EquivalentTo(new[] { "model", "vocab" }));
	}

	[TestCase(TranslationPlatform.Desktop, "h7")]
	[TestCase(TranslationPlatform.Mobile, "h8")]
	public void FiltersByPlatform(TranslationPlatform platform, string expectedHash)
	{
		var catalog = TranslationModelCatalog.Parse(Records, AttachmentsBase, platform);
		Assert.That(catalog.GetFiles(new LanguagePair("en", "ja")).Single().DecompressedHash, Is.EqualTo(expectedHash));
	}

	[Test]
	public void IgnoresChannelLimitedRecords()
	{
		var catalog = TranslationModelCatalog.Parse(Records, AttachmentsBase, TranslationPlatform.Desktop);
		Assert.That(catalog.LanguagePairs, Is.EquivalentTo(new[] { new LanguagePair("cs", "en"), new LanguagePair("en", "ja") }));
	}

	[Test]
	public void FindsPivotRoute()
	{
		var catalog = TranslationModelCatalog.Parse(Records, AttachmentsBase, TranslationPlatform.Desktop);
		Assert.That(catalog.FindRoute("cs", "ja"), Is.EqualTo(new[] { new LanguagePair("cs", "en"), new LanguagePair("en", "ja") }));
		Assert.That(catalog.FindRoute("CS", "EN"), Is.EqualTo(new[] { new LanguagePair("cs", "en") }));
		Assert.That(catalog.FindRoute("ja", "cs"), Is.Null);
	}

	[TestCase("3.0", "3.1", -1)]
	[TestCase("3.0a1", "3.0", -1)]
	[TestCase("3.0a1", "3.0a2", -1)]
	[TestCase("3.10", "3.9", 1)]
	[TestCase("3.1", "3.1", 0)]
	public void CompareVersions(string a, string b, int expected)
	{
		Assert.That(Math.Sign(ModelVersion.Parse(a)!.Value.CompareTo(ModelVersion.Parse(b)!.Value)), Is.EqualTo(expected));
	}
}
