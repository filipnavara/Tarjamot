using NUnit.Framework;

namespace Tarjamot.Tests;

/// <summary>
/// Expected segmentations come from Firefox's Bergamot WASM build (Intl.Segmenter).
/// </summary>
[TestFixture]
public class SentenceSplitterTest
{
	// "\u6211\u5F88\u597D\u3002" is a sentence ending with an ideographic full stop.
	const string Good = "\u6211\u5F88\u597D\u3002";
	const string Times = "\u7EBD\u7EA6\u65F6\u62A5";

	[TestCase("It is good. (Times) is good.", new[] { "It is good.", "(Times) is good." })]
	[TestCase("It is good.(Times) is good.", new[] { "It is good.(", "Times) is good." })]
	[TestCase(Good + "\u300A" + Times + "\u300B" + Good, new[] { Good + "\u300A", Times + "\u300B" + Good })]
	[TestCase(Good + " \u300A" + Times + "\u300B" + Good, new[] { Good, "\u300A" + Times + "\u300B" + Good })]
	[TestCase(Good + Times + Good, new[] { Good, Times + Good })]
	[TestCase("From J. Schmidhuber (2010).", new[] { "From J.", "Schmidhuber (2010)." })]
	[TestCase("Version 1.5 is out. See e.g. the notes.", new[] { "Version 1.5 is out.", "See e.g. the notes." })]
	[TestCase("Ahoj.Jak se m\u00E1\u0161?", new[] { "Ahoj.Jak se m\u00E1\u0161?" })]
	[TestCase("What? Really! Yes.", new[] { "What?", "Really!", "Yes." })]
	[TestCase("He said \"Stop.\" Then he left.", new[] { "He said \"Stop.\"", "Then he left." })]
	[TestCase("First line\nSecond line", new[] { "First line", "Second line" })]
	[TestCase("  ", new string[0])]
	public void Split(string text, string[] expected)
	{
		Assert.That(SentenceSplitter.Split(text), Is.EqualTo(expected));
	}

	[Test]
	public void SplitRangesExcludeWhitespace()
	{
		var text = "  One.  Two.\n";
		var ranges = SentenceSplitter.SplitRanges(text).ToArray();
		Assert.That(ranges.Select(range => (range.Start.Value, range.End.Value)), Is.EqualTo(new[] { (2, 6), (8, 12) }));
	}

	[Test]
	public void SplitLines()
	{
		Assert.That(SentenceSplitter.SplitLines(" One. Two. \n\nThree").Select(range => " One. Two. \n\nThree"[range]), Is.EqualTo(new[] { "One. Two.", "Three" }));
	}
}
