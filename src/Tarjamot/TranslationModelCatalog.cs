using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tarjamot;

/// <summary>Platform the models are selected for; some language pairs ship different models for Android.</summary>
public enum TranslationPlatform
{
	Desktop,
	Mobile,
}

/// <summary>One downloadable model file (a Remote Settings record with a zstd-compressed attachment).</summary>
public sealed record TranslationModelFile(
	string Name,
	string FileType,
	string Version,
	string Architecture,
	Uri Location,
	long Size,
	string DecompressedHash,
	long DecompressedSize);

/// <summary>
/// The Firefox Translations models published in Mozilla's Remote Settings collection
/// "translations-models-v2", selected the way Firefox selects them (TranslationsParent.sys.mjs):
/// records for other platforms or channels are dropped, the highest supported version of each
/// file wins, and every file of a language pair must have the version of its model file.
/// </summary>
public sealed class TranslationModelCatalog
{
	/// <summary>Mozilla's production Remote Settings server.</summary>
	public static readonly Uri DefaultServer = new("https://firefox.settings.services.mozilla.com/v1/");

	/// <summary>Major model versions this engine supports (Firefox's LANGUAGE_MODEL_MAJOR_VERSION_MIN/MAX).</summary>
	const int MinMajorVersion = 3;
	const int MaxMajorVersion = 3;

	const string CollectionPath = "buckets/main/collections/translations-models-v2/records";

	readonly Dictionary<LanguagePair, TranslationModelFile[]> files;

	TranslationModelCatalog(Dictionary<LanguagePair, TranslationModelFile[]> files)
	{
		this.files = files;
	}

	/// <summary>Language pairs with a direct model, e.g. cs-en and en-cs.</summary>
	public IReadOnlyCollection<LanguagePair> LanguagePairs => files.Keys;

	/// <summary>Downloads the record list from the Remote Settings server.</summary>
	public static async Task<TranslationModelCatalog> LoadAsync(HttpClient httpClient, TranslationPlatform platform, Uri? server = null, CancellationToken cancellationToken = default)
	{
		server ??= DefaultServer;
		var serverInfo = await httpClient.GetFromJsonAsync(server, CatalogJsonContext.Default.ServerInfo, cancellationToken).ConfigureAwait(false);
		var attachmentsBase = serverInfo?.Capabilities?.Attachments?.BaseUrl ?? throw new InvalidDataException("The Remote Settings server does not serve attachments.");
		var json = await httpClient.GetStringAsync(new Uri(server, CollectionPath), cancellationToken).ConfigureAwait(false);
		return Parse(json, new Uri(attachmentsBase), platform);
	}

	/// <summary>Creates the catalog from a Remote Settings records response.</summary>
	public static TranslationModelCatalog Parse(string recordsJson, Uri attachmentsBase, TranslationPlatform platform)
	{
		var records = JsonSerializer.Deserialize(recordsJson, CatalogJsonContext.Default.RecordsResponse)?.Data ?? [];

		// Highest supported version of each file per language pair.
		var best = new Dictionary<(LanguagePair Pair, string Name), Record>();
		foreach (var record in records)
		{
			if (!IsForPlatform(record.FilterExpression, platform) || ModelVersion.Parse(record.Version) is not { } version)
				continue;
			if (version.Major < MinMajorVersion || version.Major > MaxMajorVersion)
				continue;
			var key = (new LanguagePair(record.SourceLanguage, record.TargetLanguage), record.Name);
			if (!best.TryGetValue(key, out var existing) || version.CompareTo(ModelVersion.Parse(existing.Version)!.Value) > 0)
				best[key] = record;
		}

		var files = new Dictionary<LanguagePair, TranslationModelFile[]>();
		foreach (var group in best.GroupBy(entry => entry.Key.Pair, entry => entry.Value))
		{
			var model = group.FirstOrDefault(record => record.FileType == "model");
			if (model is null)
				continue;
			files[group.Key] = group
				.Where(record => record.Version == model.Version)
				.Select(record => new TranslationModelFile(
					record.Name,
					record.FileType,
					record.Version,
					record.Architecture ?? "",
					new Uri(attachmentsBase, record.Attachment.Location),
					record.Attachment.Size,
					record.DecompressedHash,
					record.DecompressedSize))
				.OrderBy(file => file.Name, StringComparer.Ordinal)
				.ToArray();
		}
		return new TranslationModelCatalog(files);
	}

	/// <summary>
	/// Files to download for a language pair. The lexical shortlist ("lex") is only needed with
	/// <see cref="TranslationOptions.UseShortlist"/>.
	/// </summary>
	public IReadOnlyList<TranslationModelFile> GetFiles(LanguagePair pair, bool includeShortlist = false) =>
		files.TryGetValue(pair, out var pairFiles)
			? pairFiles.Where(file => includeShortlist || file.FileType != "lex").ToArray()
			: throw new KeyNotFoundException($"There is no model for {pair}.");

	/// <summary>The models that translate between two languages, directly or through English.</summary>
	public LanguagePair[]? FindRoute(string source, string target) => LanguagePair.FindRoute(files.Keys, source, target);

	// Remote Settings filter expressions used by this collection. Unknown expressions (e.g. records
	// limited to Nightly) are treated as not matching.
	static bool IsForPlatform(string? expression, TranslationPlatform platform) => expression switch
	{
		null or "" => true,
		"env.appinfo.OS == 'Android'" => platform == TranslationPlatform.Mobile,
		"env.appinfo.OS != 'Android'" => platform == TranslationPlatform.Desktop,
		_ => false,
	};

	internal sealed record RecordsResponse(Record[] Data);

	internal sealed record Record(
		string Name,
		string Version,
		string FileType,
		string? Architecture,
		string SourceLanguage,
		string TargetLanguage,
		Attachment Attachment,
		string DecompressedHash,
		long DecompressedSize,
		[property: JsonPropertyName("filter_expression")] string? FilterExpression);

	internal sealed record Attachment(string Location, long Size, string Hash);

	internal sealed record ServerInfo(Capabilities? Capabilities);

	internal sealed record Capabilities(AttachmentsCapability? Attachments);

	internal sealed record AttachmentsCapability([property: JsonPropertyName("base_url")] string? BaseUrl);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TranslationModelCatalog.RecordsResponse))]
[JsonSerializable(typeof(TranslationModelCatalog.ServerInfo))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext;
