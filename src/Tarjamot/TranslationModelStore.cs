using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
#if NET11_0_OR_GREATER
using System.IO.Compression;
#else
using ZstdSharp;
#endif

namespace Tarjamot;

/// <summary>
/// Downloaded models on disk, one directory per language pair ("cs-en") with the decompressed
/// files and a manifest of what was installed. Downloads are verified against the SHA-256
/// hashes from the catalog and replace an existing installation only when complete.
/// </summary>
public sealed class TranslationModelStore
{
	const string ManifestName = "manifest.json";

	readonly string rootDirectory;
	readonly HttpClient httpClient;

	public TranslationModelStore(string rootDirectory, HttpClient httpClient)
	{
		this.rootDirectory = rootDirectory;
		this.httpClient = httpClient;
	}

	public string GetDirectory(LanguagePair pair) => Path.Combine(rootDirectory, pair.ToString());

	/// <summary>Language pairs with a complete installation.</summary>
	public IReadOnlyList<LanguagePair> GetInstalledPairs()
	{
		if (!Directory.Exists(rootDirectory))
			return [];
		return Directory.GetDirectories(rootDirectory)
			.Select(directory => ReadManifest(directory)?.Pair)
			.OfType<LanguagePair>()
			.ToArray();
	}

	/// <summary>True if exactly these files (by name and hash) are installed for the pair.</summary>
	public bool IsInstalled(LanguagePair pair, IReadOnlyList<TranslationModelFile> files)
	{
		var manifest = ReadManifest(GetDirectory(pair));
		return manifest is not null &&
			manifest.Files.Select(file => (file.Name, file.Hash)).ToHashSet()
				.SetEquals(files.Select(file => (file.Name, file.DecompressedHash)));
	}

	/// <summary>
	/// Downloads and installs the files of a language pair, replacing any previous installation.
	/// Progress reports the number of compressed bytes received.
	/// </summary>
	public async Task<string> InstallAsync(LanguagePair pair, IReadOnlyList<TranslationModelFile> files, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
	{
		Directory.CreateDirectory(rootDirectory);
		var target = GetDirectory(pair);
		var staging = Path.Combine(rootDirectory, $".{pair}.{Guid.NewGuid():N}");
		Directory.CreateDirectory(staging);
		try
		{
			long received = 0;
			foreach (var file in files)
			{
				await DownloadAsync(file, Path.Combine(staging, file.Name), bytes => progress?.Report(received += bytes), cancellationToken).ConfigureAwait(false);
			}
			var manifest = new Manifest(pair, files.Select(file => new ManifestFile(file.Name, file.Version, file.DecompressedHash)).ToArray());
			await File.WriteAllTextAsync(Path.Combine(staging, ManifestName), JsonSerializer.Serialize(manifest, StoreJsonContext.Default.Manifest), cancellationToken).ConfigureAwait(false);

			Delete(pair);
			Directory.Move(staging, target);
			return target;
		}
		finally
		{
			if (Directory.Exists(staging))
				Directory.Delete(staging, recursive: true);
		}
	}

	public void Delete(LanguagePair pair)
	{
		var directory = GetDirectory(pair);
		if (Directory.Exists(directory))
			Directory.Delete(directory, recursive: true);
	}

	async Task DownloadAsync(TranslationModelFile file, string path, Action<int> onReceived, CancellationToken cancellationToken)
	{
		using var response = await httpClient.GetAsync(file.Location, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		await using var compressed = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		await using var counting = new ProgressStream(compressed, onReceived);
#if NET11_0_OR_GREATER
		await using var decompressed = new ZstandardStream(counting, CompressionMode.Decompress);
#else
		await using var decompressed = new DecompressionStream(counting);
#endif
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		long length = 0;
		await using (var output = File.Create(path))
		{
			var buffer = new byte[81920];
			int read;
			while ((read = await decompressed.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
			{
				hash.AppendData(buffer, 0, read);
				await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
				length += read;
			}
		}

		var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
		if (length != file.DecompressedSize || !string.Equals(actual, file.DecompressedHash, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException($"The downloaded {file.Name} does not match the catalog (size {length}, SHA-256 {actual}).");
	}

	static Manifest? ReadManifest(string directory)
	{
		var path = Path.Combine(directory, ManifestName);
		if (!File.Exists(path))
			return null;
		try
		{
			return JsonSerializer.Deserialize(File.ReadAllText(path), StoreJsonContext.Default.Manifest);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	internal sealed record Manifest(LanguagePair Pair, ManifestFile[] Files);

	internal sealed record ManifestFile(string Name, string Version, string Hash);

	// Reports the bytes read from the network stream.
	sealed class ProgressStream(Stream inner, Action<int> onRead) : Stream
	{
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

		public override int Read(byte[] buffer, int offset, int count) => Report(inner.Read(buffer, offset, count));

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
			Report(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
			ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

		public override void Flush() { }
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		int Report(int read)
		{
			onRead(read);
			return read;
		}
	}
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(TranslationModelStore.Manifest))]
internal sealed partial class StoreJsonContext : JsonSerializerContext;
