// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy
// of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// Ported from bergamot-translator's src/translator/response_builder.cpp, text_processor.cpp and translation_model.cpp.

using System.Text;

namespace Tarjamot.BergamotMarkup;

/// <summary>
/// Translates HTML like Bergamot's HTML mode in Firefox's WASM build: the markup is parsed out
/// (<see cref="HtmlMarkup"/>), the plain text is segmented and translated with soft alignments,
/// and the markup is written back into the translation along the alignments. With two models
/// (through English), the second model translates the English sentences and the alignments are
/// combined through the English characters (Bergamot's remapAlignments).
/// </summary>
internal static class MarkupTranslation
{
	public static string[] Translate(IReadOnlyList<string> htmls, IReadOnlyList<TranslationModel> models, CancellationToken cancellationToken)
	{
		var markups = new HtmlMarkup[htmls.Count];
		var sources = new AnnotatedText[htmls.Count];
		var tokens = new List<List<int>>[htmls.Count];
		var first = models[0].Engine;
		for (int i = 0; i < htmls.Count; i++)
		{
			markups[i] = new HtmlMarkup(Encoding.UTF8.GetBytes(htmls[i]), out var plainText);
			(sources[i], tokens[i]) = Segment(plainText, first, models[0].TargetLanguage);
		}

		var responses = TranslateLeg(sources, tokens, models[0], cancellationToken);
		for (int leg = 1; leg < models.Count; leg++)
		{
			var engine = models[leg].Engine;
			var pivotSources = new AnnotatedText[htmls.Count];
			var pivotTokens = new List<List<int>>[htmls.Count];
			for (int i = 0; i < htmls.Count; i++)
				(pivotSources[i], pivotTokens[i]) = Retokenize(responses[i].Target, engine, models[leg].TargetLanguage);
			var pivotResponses = TranslateLeg(pivotSources, pivotTokens, models[leg], cancellationToken);
			for (int i = 0; i < htmls.Count; i++)
				responses[i] = new MarkupResponse(responses[i].Source, pivotResponses[i].Target, RemapAlignments(responses[i], pivotResponses[i]));
		}

		return responses.Select((response, i) => Encoding.UTF8.GetString(markups[i].Restore(response))).ToArray();
	}

	// Segments the plain text into sentences (Intl.Segmenter rules) and records the sentences with
	// tokens; sentences without tokens are left in the gaps (TextProcessor::process).
	static (AnnotatedText Source, List<List<int>> Tokens) Segment(byte[] plainText, Engine.MarianEngine engine, string targetLanguage)
	{
		var source = new AnnotatedText(plainText, targetLanguage);
		var tokens = new List<List<int>>();
		var text = Encoding.UTF8.GetString(plainText);
		var byteOffsets = Utf8Offsets(text);
		foreach (var range in SentenceSplitter.SplitRanges(text))
		{
			int begin = byteOffsets[range.Start.Value];
			int end = byteOffsets[range.End.Value];
			var ranges = new List<(int Begin, int End)>();
			var ids = engine.SourceVocab.Encode(plainText.AsSpan(begin, end - begin), ranges);
			if (ids.Count == 0)
				continue;
			var wordRanges = ranges.Select(r => (r.Begin + begin, r.End + begin)).ToList();
			wordRanges.Add((wordRanges[^1].Item2, wordRanges[^1].Item2)); // EOS
			source.RecordExistingSentence(wordRanges, begin);
			tokens.Add([.. ids, engine.SourceVocab.EosId]);
		}
		return (source, tokens);
	}

	// The sentences of a pivot translation, tokenized by the next model's vocabulary within the
	// same text (TextProcessor::processFromAnnotation). Every sentence is kept, even an empty one.
	static (AnnotatedText Source, List<List<int>> Tokens) Retokenize(AnnotatedText intermediate, Engine.MarianEngine engine, string targetLanguage)
	{
		var text = intermediate.Text;
		var source = new AnnotatedText(text, targetLanguage);
		var tokens = new List<List<int>>();
		for (int sentence = 0; sentence < intermediate.NumSentences; sentence++)
		{
			var (begin, end) = intermediate.SentenceRange(sentence);
			var ranges = new List<(int Begin, int End)>();
			var ids = engine.SourceVocab.Encode(text.AsSpan(begin, end - begin), ranges);
			var wordRanges = ranges.Select(r => (r.Begin + begin, r.End + begin)).ToList();
			int eos = wordRanges.Count > 0 ? wordRanges[^1].Item2 : end;
			wordRanges.Add((eos, eos));
			source.RecordExistingSentence(wordRanges, wordRanges[0].Item1);
			tokens.Add([.. ids, engine.SourceVocab.EosId]);
		}
		return (source, tokens);
	}

	// Translates the sentences of all texts together and builds each target text with the gaps of
	// its source (ResponseBuilder::buildTranslatedText).
	static MarkupResponse[] TranslateLeg(AnnotatedText[] sources, List<List<int>>[] tokens, TranslationModel model, CancellationToken cancellationToken)
	{
		var engine = model.Engine;
		var all = tokens.SelectMany(list => list).ToList();
		var alignments = all.Select(_ => new List<float[]>()).ToArray();
		var outputs = engine.TranslateTokens(all, alignments, cancellationToken);

		var responses = new MarkupResponse[sources.Length];
		int index = 0;
		for (int i = 0; i < sources.Length; i++)
		{
			var source = sources[i];
			var target = new AnnotatedText(model.TargetLanguage);
			var sentenceAlignments = new List<List<float[]>>();
			for (int sentence = 0; sentence < source.NumSentences; sentence++, index++)
			{
				// EOS was emitted unless the translation reached the maximum length.
				var ids = outputs[index];
				if (alignments[index].Count > ids.Count)
					ids = [.. ids, engine.TargetVocab.EosId];
				var ranges = new List<(int Begin, int End)>();
				var decoded = engine.TargetVocab.DecodeWithRanges(ids, ranges);
				target.AppendSentence(source.Gap(sentence), decoded, ranges);
				if (sentence + 1 == source.NumSentences)
					target.HandleEndingWhitespace(source.Gap(sentence + 1), betweenSentences: false);
				sentenceAlignments.Add(alignments[index]);
			}
			responses[i] = new MarkupResponse(source, target, sentenceAlignments);
		}
		return responses;
	}

	// p(source | target) = sum over pivot tokens of p(source | pivot) * p(pivot | target), with the
	// pivot tokens of the second model mapped onto those of the first through their characters.
	static List<List<float[]>> RemapAlignments(MarkupResponse first, MarkupResponse second)
	{
		var result = new List<List<float[]>>();
		for (int sentence = 0; sentence < first.Source.NumSentences; sentence++)
		{
			var sourceGivenPivots = first.Alignments[sentence];
			var pivotGivenTargets = second.Alignments[sentence];
			var sourceSidePivots = Enumerable.Range(0, first.Target.NumWords(sentence)).Select(w => first.Target.WordRange(sentence, w)).ToList();
			var targetSidePivots = Enumerable.Range(0, second.Source.NumWords(sentence)).Select(w => second.Source.WordRange(sentence, w)).ToList();
			var remapped = TransferThroughCharacters(sourceSidePivots, targetSidePivots, pivotGivenTargets);

			int sourceTokens = first.Source.NumWords(sentence);
			int targetTokens = second.Target.NumWords(sentence);
			var output = new List<float[]>();
			for (int t = 0; t < targetTokens; t++)
			{
				var row = new float[sourceTokens];
				for (int q = 0; q < sourceSidePivots.Count; q++)
				{
					for (int s = 0; s < sourceTokens; s++)
						row[s] += sourceGivenPivots[q][s] * remapped[t][q];
				}
				output.Add(row);
			}
			result.Add(output);
		}
		return result;
	}

	// Rewrites p(q' | t) over the second model's pivot tokens q' as p(q | t) over the first
	// model's: probability is spread over the characters of q' and collected over those of q.
	static float[][] TransferThroughCharacters(List<(int Begin, int End)> sourceSidePivots, List<(int Begin, int End)> targetSidePivots, List<float[]> pivotGivenTargets)
	{
		var remapped = pivotGivenTargets.Select(_ => new float[sourceSidePivots.Count]).ToArray();
		int sq = 0, qt = 0;
		while (sq < sourceSidePivots.Count && qt < targetSidePivots.Count)
		{
			var sourceSidePivot = sourceSidePivots[sq];
			var targetSidePivot = targetSidePivots[qt];
			if (sourceSidePivot == targetSidePivot)
			{
				for (int t = 0; t < pivotGivenTargets.Count; t++)
					remapped[t][sq] += pivotGivenTargets[t][qt];
				sq++;
				qt++;
				continue;
			}

			int left = Math.Max(targetSidePivot.Begin, sourceSidePivot.Begin);
			int right = Math.Min(targetSidePivot.End, sourceSidePivot.End);
			float charCount = right - left;
			float probabilitySpread = targetSidePivot.End - targetSidePivot.Begin;
			for (int t = 0; t < pivotGivenTargets.Count; t++)
				remapped[t][sq] += charCount * pivotGivenTargets[t][qt] / probabilitySpread;

			if (sourceSidePivot.End == targetSidePivot.End)
			{
				sq++;
				qt++;
			}
			else if (sourceSidePivot.End > targetSidePivot.End)
			{
				qt++;
			}
			else
			{
				sq++;
			}
		}

		// The second model's EOS when the first model did not emit one: shared out evenly.
		for (; qt < targetSidePivots.Count; qt++)
		{
			for (int t = 0; t < pivotGivenTargets.Count; t++)
			{
				float gift = pivotGivenTargets[t][qt] / sourceSidePivots.Count;
				for (int s = 0; s < sourceSidePivots.Count; s++)
					remapped[t][s] += gift;
			}
		}
		return remapped;
	}

	// UTF-8 byte offset of every UTF-16 position of the text, and of its end.
	static int[] Utf8Offsets(string text)
	{
		var offsets = new int[text.Length + 1];
		for (int i = 0, offset = 0; i <= text.Length; i++)
		{
			offsets[i] = offset;
			if (i < text.Length)
				offset += char.IsSurrogate(text[i]) ? 2 : Encoding.UTF8.GetByteCount(text.AsSpan(i, 1));
		}
		return offsets;
	}
}
