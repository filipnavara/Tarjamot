using System.Text;
using NUnit.Framework;

namespace Tarjamot.LanguageIdentification.Tests;

[TestFixture]
public class LanguageIdentifierTest
{
	// "Thank you very much" in Greek and "Thank you, we will answer soon" in Russian.
	private const string Greek = "\u0395\u03C5\u03C7\u03B1\u03C1\u03B9\u03C3\u03C4\u03BF\u03CD\u03BC\u03B5 \u03C0\u03BF\u03BB\u03CD";
	private const string Russian = "\u0421\u043F\u0430\u0441\u0438\u0431\u043E, \u043C\u044B \u0441\u043A\u043E\u0440\u043E \u043E\u0442\u0432\u0435\u0442\u0438\u043C.";

	// A few sentences per language ("The weather is nice today...", "Thank you for your message...").
	private static readonly (string Iso639_2T, string Iso639_3, string Text)[] training =
	[
		("eng", "en", "The weather is nice today and we are going to the park with our friends. Thank you for your message, I will reply to you as soon as possible. Please find the attached document and let me know what you think."),
		("deu", "de", "Das Wetter ist heute sch\u00F6n und wir gehen mit unseren Freunden in den Park. Vielen Dank f\u00FCr Ihre Nachricht, ich werde Ihnen so schnell wie m\u00F6glich antworten. Im Anhang finden Sie das Dokument, bitte sagen Sie mir, was Sie davon halten."),
		("ces", "cs", "Po\u010Das\u00ED je dnes hezk\u00E9 a jdeme s na\u0161imi p\u0159\u00E1teli do parku. D\u011Bkuji za va\u0161i zpr\u00E1vu, odpov\u00EDm v\u00E1m co nejd\u0159\u00EDve. V p\u0159\u00EDloze najdete dokument, dejte mi pros\u00EDm v\u011Bd\u011Bt, co si o tom mysl\u00EDte."),
		("ell", "el", "\u039F \u03BA\u03B1\u03B9\u03C1\u03CC\u03C2 \u03B5\u03AF\u03BD\u03B1\u03B9 \u03C9\u03C1\u03B1\u03AF\u03BF\u03C2 \u03C3\u03AE\u03BC\u03B5\u03C1\u03B1 \u03BA\u03B1\u03B9 \u03C0\u03B7\u03B3\u03B1\u03AF\u03BD\u03BF\u03C5\u03BC\u03B5 \u03C3\u03C4\u03BF \u03C0\u03AC\u03C1\u03BA\u03BF \u03BC\u03B5 \u03C4\u03BF\u03C5\u03C2 \u03C6\u03AF\u03BB\u03BF\u03C5\u03C2 \u03BC\u03B1\u03C2. \u0395\u03C5\u03C7\u03B1\u03C1\u03B9\u03C3\u03C4\u03CE \u03B3\u03B9\u03B1 \u03C4\u03BF \u03BC\u03AE\u03BD\u03C5\u03BC\u03AC \u03C3\u03B1\u03C2, \u03B8\u03B1 \u03C3\u03B1\u03C2 \u03B1\u03C0\u03B1\u03BD\u03C4\u03AE\u03C3\u03C9 \u03C4\u03BF \u03C3\u03C5\u03BD\u03C4\u03BF\u03BC\u03CC\u03C4\u03B5\u03C1\u03BF \u03B4\u03C5\u03BD\u03B1\u03C4\u03CC."),
		("rus", "ru", "\u0421\u0435\u0433\u043E\u0434\u043D\u044F \u0445\u043E\u0440\u043E\u0448\u0430\u044F \u043F\u043E\u0433\u043E\u0434\u0430, \u0438 \u043C\u044B \u0438\u0434\u0451\u043C \u0432 \u043F\u0430\u0440\u043A \u0441 \u043D\u0430\u0448\u0438\u043C\u0438 \u0434\u0440\u0443\u0437\u044C\u044F\u043C\u0438. \u0421\u043F\u0430\u0441\u0438\u0431\u043E \u0437\u0430 \u0432\u0430\u0448\u0435 \u0441\u043E\u043E\u0431\u0449\u0435\u043D\u0438\u0435, \u044F \u043E\u0442\u0432\u0435\u0447\u0443 \u0432\u0430\u043C \u043A\u0430\u043A \u043C\u043E\u0436\u043D\u043E \u0441\u043A\u043E\u0440\u0435\u0435."),
		("jpn", "ja", "\u4ECA\u65E5\u306F\u3044\u3044\u5929\u6C17\u306A\u306E\u3067\u3001\u53CB\u9054\u3068\u516C\u5712\u306B\u884C\u304D\u307E\u3059\u3002\u30E1\u30C3\u30BB\u30FC\u30B8\u3092\u3042\u308A\u304C\u3068\u3046\u3054\u3056\u3044\u307E\u3059\u3002\u3067\u304D\u308B\u3060\u3051\u65E9\u304F\u8FD4\u4FE1\u3057\u307E\u3059\u3002"),
		("zho", "zh", "\u4ECA\u5929\u5929\u6C14\u5F88\u597D\uFF0C\u6211\u4EEC\u548C\u670B\u53CB\u4E00\u8D77\u53BB\u516C\u56ED\u3002\u8C22\u8C22\u4F60\u7684\u6D88\u606F\uFF0C\u6211\u4F1A\u5C3D\u5FEB\u56DE\u590D\u4F60\u3002\u8BF7\u67E5\u770B\u9644\u4EF6\u4E2D\u7684\u6587\u4EF6\u3002"),
	];

	private static readonly byte[] profile = TestProfile.Write(training, maxNGramLength: 3);
	private static readonly LanguageIdentifier identifier = LanguageIdentifier.Load(new MemoryStream(profile));

	[Test]
	public void LoadsProfile()
	{
		Assert.That(identifier.Languages, Is.EqualTo(new[] { "en", "de", "cs", "el", "ru", "ja", "zh" }));
		Assert.That(identifier.MaxNGramLength, Is.EqualTo(3));
		Assert.That(identifier.MaximumSizeOfDistribution, Is.EqualTo(TestProfile.MaximumSizeOfDistribution));
		Assert.That(identifier.TableSize, Is.GreaterThan(0));
	}

	[Test]
	public void UsesTheIso639_2TCodeWhenIso639_3IsEmpty()
	{
		var loaded = LanguageIdentifier.Load(new MemoryStream(TestProfile.Write([("en", "", "hello world")], maxNGramLength: 3)));
		Assert.That(loaded.Languages, Is.EqualTo(new[] { "en" }));
	}

	[Test]
	public void RejectsLongerNGramsThanTheKeyHolds()
	{
		var tooLong = TestProfile.Write([("eng", "en", "hello world")], maxNGramLength: 5);
		Assert.That(() => LanguageIdentifier.Load(new MemoryStream(tooLong)), Throws.TypeOf<ArgumentOutOfRangeException>());
	}

	[Test]
	public void LeavesTheStreamOpen()
	{
		using var stream = new MemoryStream(profile);
		LanguageIdentifier.Load(stream);
		Assert.That(stream.CanRead, Is.True);
	}

	[TestCase("Thank you, we will reply as soon as we can.", "en")]
	[TestCase("Vielen Dank, wir werden so schnell wie m\u00F6glich antworten.", "de")]
	[TestCase("D\u011Bkujeme, odpov\u00EDme v\u00E1m co nejd\u0159\u00EDve.", "cs")]
	[TestCase("\u3042\u308A\u304C\u3068\u3046\u3054\u3056\u3044\u307E\u3059\u3002\u3059\u3050\u306B\u8FD4\u4FE1\u3057\u307E\u3059\u3002", "ja")]
	[TestCase("\u8C22\u8C22\uFF0C\u6211\u4EEC\u4F1A\u5C3D\u5FEB\u56DE\u590D\u3002", "zh")]
	public void IdentifiesLanguage(string text, string expected)
	{
		var ranking = identifier.Identify(text);
		Assert.That(ranking, Is.Not.Empty);
		Assert.That(identifier.Languages[ranking[0].Language], Is.EqualTo(expected));
	}

	[Test]
	public void RanksCandidatesByDescendingScore()
	{
		var scores = identifier.Identify("Thank you for the document.").Select(r => r.Score).ToArray();
		Assert.That(scores, Has.Length.EqualTo(3), "en, de and cs are written in Latin");
		Assert.That(scores, Is.Ordered.Descending);
	}

	[TestCase("Thank you for your message, I will reply soon.")]
	[TestCase("Vielen Dank, ich werde antworten.")]
	[TestCase("D\u011Bkuji za zpr\u00E1vu.")]
	public void ScoresMatchUnquantizedNaiveBayes(string text)
	{
		var expected = TestProfile.NaiveBayesScores(training, text);
		var ranking = identifier.Identify(text);
		var languages = ranking.Select(r => identifier.Languages[r.Language]).ToArray();
		Assert.That(languages[0], Is.EqualTo(expected.MaxBy(kv => kv.Value).Key));

		// Each n-gram weight is rounded to a 1/255 step of the largest log-count.
		var tolerance = identifier.GetNGrams(text).Count * Math.Log(TestProfile.MaxCount(training) + 1) / 255 / 2;
		foreach (var (language, score) in ranking)
			Assert.That(score, Is.EqualTo(expected[identifier.Languages[language]]).Within(tolerance + 1e-9));
	}

	[TestCase(Greek, "el")]
	[TestCase(Russian, "ru")]
	public void UniqueScriptDecidesLanguage(string text, string expected)
	{
		var ranking = identifier.Identify(text);
		Assert.That(ranking, Has.Length.EqualTo(1));
		Assert.That(identifier.Languages[ranking[0].Language], Is.EqualTo(expected));
		Assert.That(ranking[0].Score, Is.EqualTo(double.PositiveInfinity));
	}

	[Test]
	public void ScriptDecidedLanguagesCanBeScored()
	{
		var scoring = LanguageIdentifier.Load(new MemoryStream(profile), dropScriptDecidedLanguages: false);
		var ranking = scoring.Identify(Greek);
		Assert.That(scoring.Languages[ranking[0].Language], Is.EqualTo("el"));
		Assert.That(double.IsFinite(ranking[0].Score), Is.True);
		Assert.That(scoring.TableSize, Is.GreaterThan(identifier.TableSize));
	}

	[Test]
	public void KanaLimitsCandidatesToJapanese()
	{
		var ranking = identifier.Identify("\u3042\u308A\u304C\u3068\u3046\u53CB\u9054");
		Assert.That(ranking.Select(r => identifier.Languages[r.Language]), Is.EqualTo(new[] { "ja" }));
	}

	[Test]
	public void HanOnlyTextScoresJapaneseAndChinese()
	{
		var ranking = identifier.Identify("\u8C22\u8C22\u6211\u4EEC\u7684\u670B\u53CB");
		Assert.That(ranking.Select(r => identifier.Languages[r.Language]), Is.EquivalentTo(new[] { "ja", "zh" }));
		Assert.That(identifier.Languages[ranking[0].Language], Is.EqualTo("zh"));
	}

	[Test]
	public void DominantScriptWinsInMixedText()
	{
		var ranking = identifier.Identify(Greek + " " + Greek + " thank you");
		Assert.That(identifier.Languages[ranking[0].Language], Is.EqualTo("el"));
	}

	[TestCase("")]
	[TestCase("  ")]
	[TestCase("12345, 67.89 - !?")]
	public void TextWithoutLettersIdentifiesNothing(string text)
	{
		Assert.That(identifier.Identify(text), Is.Empty);
	}

	[Test]
	public void DistanceIsSmallerForMatchingLanguage()
	{
		const string text = "Thank you for your message, please find the attached document.";
		var toEnglish = identifier.CalculateDistance(IndexOf("en"), text);
		var toGerman = identifier.CalculateDistance(IndexOf("de"), text);
		Assert.That(toEnglish, Is.InRange(0.0, 1.0));
		Assert.That(toGerman, Is.InRange(0.0, 1.0));
		Assert.That(toEnglish, Is.LessThan(toGerman));
	}

	[Test]
	public void DistanceToIdenticalTextIsAboutZero()
	{
		Assert.That(identifier.CalculateDistance(IndexOf("en"), training[0].Text), Is.EqualTo(0).Within(0.05));
	}

	[Test]
	public void DistanceOfScriptDecidedLanguageIsZero()
	{
		Assert.That(identifier.CalculateDistance(IndexOf("el"), Greek), Is.Zero);
	}

	[Test]
	public void ExtractsNGramsOfUnderscoreWrappedWords()
	{
		Assert.That(identifier.GetNGrams("ab"), Is.EquivalentTo(new[] { "a", "b", "_a", "ab", "b_", "_ab", "ab_" }));
	}

	[Test]
	public void NonLettersSeparateWords()
	{
		Assert.That(identifier.GetNGrams("a1b, c"), Is.EquivalentTo(new[]
		{
			"a", "_a", "a_", "_a_",
			"b", "_b", "b_", "_b_",
			"c", "_c", "c_", "_c_",
		}));
	}

	[Test]
	public void NGramsAreCaseSensitive()
	{
		Assert.That(identifier.GetNGrams("Ab"), Does.Contain("_Ab").And.Not.Contain("_ab"));
	}

	[TestCase("The weather is nice today.")]
	[TestCase("D\u011Bkuji, \u00FApln\u011B v\u0161e!")]
	[TestCase("\u4ECA\u65E5\u306F\u3044\u3044\u5929\u6C17")]
	public void ExtractsTheSameNGramsAsTheTrainingCounter(string text)
	{
		Assert.That(identifier.GetNGrams(text), Is.EquivalentTo(TestProfile.CountNGrams(text, 3).SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value))));
	}

	private static int IndexOf(string language) => identifier.Languages.ToList().IndexOf(language);
}
