using System.Net;
using System.Text;
using System.Text.Json;

namespace Tarjamot.LanguageProfiles;

/// <summary>
/// A language of the corpus: its tag, the Wikipedia it comes from and the variant that Wikipedia converts the text
/// to, for wikis that store mixed scripts (Chinese, Serbian) and convert them when they are read.
/// </summary>
internal sealed record CorpusLanguage(string Tag, string Wiki, string? Variant)
{
	// Tags whose text comes from another wiki or from a variant of a mixed-script wiki.
	private static readonly Dictionary<string, (string Wiki, string? Variant)> knownLanguages = new(StringComparer.OrdinalIgnoreCase)
	{
		["zh-Hans"] = ("zh", "zh-hans"),
		["zh-Hant"] = ("zh", "zh-hant"),
		["sr-Cyrl"] = ("sr", "sr-ec"),
		["sr-Latn"] = ("sr", "sr-el"),
		["nb"] = ("no", null),
		["fil"] = ("tl", null),
	};

	public static IReadOnlyDictionary<string, (string Wiki, string? Variant)> KnownLanguages => knownLanguages;

	/// <summary>
	/// Parses "tag" (the wiki of that name, or a known mapping) or "tag=wiki" or "tag=wiki:variant".
	/// </summary>
	public static CorpusLanguage Parse(string argument)
	{
		var separator = argument.IndexOf('=');
		if (separator < 0)
		{
			if (knownLanguages.TryGetValue(argument, out var known))
				return new CorpusLanguage(argument, known.Wiki, known.Variant);
			return new CorpusLanguage(argument, argument.ToLowerInvariant(), null);
		}
		var tag = argument[..separator];
		var source = argument[(separator + 1)..].Split(':', 2);
		return new CorpusLanguage(tag, source[0], source.Length > 1 ? source[1] : null);
	}
}

/// <summary>
/// Downloads the plain text of random Wikipedia articles (through the TextExtracts API) into one text file per
/// language.
/// </summary>
internal sealed class WikipediaCorpus(HttpClient httpClient, string directory, TextWriter log)
{
	private const int RandomBatchSize = 20;
	private const int MaxAttempts = 6;

	/// <summary>Articles shorter than this, mostly stubs and disambiguation pages, are skipped.</summary>
	public int MinimumArticleLength { get; init; } = 500;

	/// <summary>Longer articles are cut at a paragraph break so that no single article dominates a language.</summary>
	public int MaximumArticleLength { get; init; } = 10_000;

	public static string GetPath(string directory, string tag) => Path.Combine(directory, tag + ".txt");

	/// <summary>
	/// Downloads text until each language's file holds at least <paramref name="characters"/> characters. Variants
	/// of one wiki are downloaded together from the same articles, so they differ only in how they are written.
	/// </summary>
	public async Task DownloadAsync(IReadOnlyList<CorpusLanguage> languages, long characters, int parallelism, CancellationToken cancellationToken)
	{
		Directory.CreateDirectory(directory);
		var wikis = languages.GroupBy(l => l.Wiki, StringComparer.OrdinalIgnoreCase).ToList();
		await Parallel.ForEachAsync(
			wikis,
			new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
			async (wiki, token) => await DownloadWikiAsync(wiki.Key, wiki.ToList(), characters, token).ConfigureAwait(false)).ConfigureAwait(false);
	}

	private async Task DownloadWikiAsync(string wiki, List<CorpusLanguage> variants, long characters, CancellationToken cancellationToken)
	{
		var lengths = variants.ToDictionary(v => v, v => File.Exists(GetPath(directory, v.Tag)) ? (long)File.ReadAllText(GetPath(directory, v.Tag)).Length : 0);
		var writers = variants.ToDictionary(v => v, v => new StreamWriter(GetPath(directory, v.Tag), append: true, new UTF8Encoding(false)));
		try
		{
			var seen = new HashSet<long>();
			var skipped = 0;
			while (lengths.Values.Min() < characters)
			{
				foreach (var pageId in await GetRandomPageIdsAsync(wiki, cancellationToken).ConfigureAwait(false))
				{
					if (lengths.Values.Min() >= characters)
						break;
					if (!seen.Add(pageId))
						continue;

					// The variants of one article, all of them or none.
					var texts = new List<string>();
					foreach (var variant in variants)
					{
						var text = await GetExtractAsync(wiki, variant.Variant, pageId, cancellationToken).ConfigureAwait(false);
						if (text.Length < MinimumArticleLength)
							break;
						texts.Add(Truncate(text, MaximumArticleLength));
					}
					if (texts.Count < variants.Count)
					{
						if (++skipped % 500 == 0 && seen.Count == skipped)
							throw new InvalidOperationException($"{wiki}.wikipedia.org returned no article of at least {MinimumArticleLength} characters in {skipped} attempts.");
						continue;
					}

					for (var i = 0; i < variants.Count; i++)
					{
						await writers[variants[i]].WriteAsync((texts[i] + "\n\n").AsMemory(), cancellationToken).ConfigureAwait(false);
						await writers[variants[i]].FlushAsync(cancellationToken).ConfigureAwait(false);
						lengths[variants[i]] += texts[i].Length + 2;
					}
				}
				lock (log)
					log.WriteLine($"{string.Join(", ", variants.Select(v => v.Tag))}: {lengths.Values.Min():N0} of {characters:N0} characters");
			}
		}
		finally
		{
			foreach (var writer in writers.Values)
				await writer.DisposeAsync().ConfigureAwait(false);
		}
	}

	private async Task<List<long>> GetRandomPageIdsAsync(string wiki, CancellationToken cancellationToken)
	{
		using var document = await QueryAsync(wiki, $"list=random&rnnamespace=0&rnfilterredir=nonredirects&rnlimit={RandomBatchSize}", cancellationToken).ConfigureAwait(false);
		return document.RootElement.GetProperty("query").GetProperty("random").EnumerateArray().Select(p => p.GetProperty("id").GetInt64()).ToList();
	}

	private async Task<string> GetExtractAsync(string wiki, string? variant, long pageId, CancellationToken cancellationToken)
	{
		var query = $"prop=extracts&explaintext=1&exsectionformat=plain&pageids={pageId}" + (variant is null ? "" : "&variant=" + Uri.EscapeDataString(variant));
		using var document = await QueryAsync(wiki, query, cancellationToken).ConfigureAwait(false);
		var pages = document.RootElement.GetProperty("query").GetProperty("pages");
		foreach (var page in pages.EnumerateArray())
		{
			if (page.TryGetProperty("extract", out var extract))
				return extract.GetString()!.Trim();
		}
		return "";
	}

	private static string Truncate(string text, int maximumLength)
	{
		if (text.Length <= maximumLength)
			return text;
		var end = text.LastIndexOf('\n', maximumLength);
		return text[..(end > maximumLength / 2 ? end : maximumLength)].TrimEnd();
	}

	private async Task<JsonDocument> QueryAsync(string wiki, string query, CancellationToken cancellationToken)
	{
		var url = $"https://{wiki}.wikipedia.org/w/api.php?action=query&format=json&formatversion=2&maxlag=5&{query}";
		for (var attempt = 1; ; attempt++)
		{
			using var response = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
			if (response.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError && attempt < MaxAttempts)
			{
				await Task.Delay(GetRetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
				continue;
			}
			response.EnsureSuccessStatusCode();

			var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
			if (!document.RootElement.TryGetProperty("error", out var error))
				return document;
			using (document)
			{
				// maxlag: the database replicas are behind, and the API asks to come back later.
				if (error.GetProperty("code").GetString() == "maxlag" && attempt < MaxAttempts)
				{
					await Task.Delay(GetRetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
					continue;
				}
				throw new InvalidOperationException($"{wiki}.wikipedia.org: {error.GetProperty("info").GetString()}");
			}
		}
	}

	private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt) =>
		response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(60, 2 << attempt));
}
