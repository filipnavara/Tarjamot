using System.Net;
using System.Security.Cryptography;
using NUnit.Framework;
using ZstdSharp;

namespace Tarjamot.Tests;

[TestFixture]
public class TranslationModelStoreTest
{
	static readonly LanguagePair CsEn = new("cs", "en");

	string root = "";

	[SetUp]
	public void SetUp()
	{
		root = Path.Combine(Path.GetTempPath(), "TranslationModelStoreTest", Guid.NewGuid().ToString("N"));
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(root))
			Directory.Delete(root, recursive: true);
	}

	[Test]
	public async Task InstallsVerifiedFiles()
	{
		var content = new Dictionary<string, byte[]> { ["model.csen.bin"] = [1, 2, 3, 4], ["vocab.csen.spm"] = [5, 6] };
		using var httpClient = new HttpClient(new FakeServer(content));
		var store = new TranslationModelStore(root, httpClient);
		var files = content.Select(entry => CreateFile(entry.Key, entry.Value)).ToArray();
		long reported = 0;

		var directory = await store.InstallAsync(CsEn, files, new SynchronousProgress(bytes => reported = bytes));

		Assert.That(File.ReadAllBytes(Path.Combine(directory, "model.csen.bin")), Is.EqualTo(content["model.csen.bin"]));
		Assert.That(File.ReadAllBytes(Path.Combine(directory, "vocab.csen.spm")), Is.EqualTo(content["vocab.csen.spm"]));
		Assert.That(reported, Is.EqualTo(files.Sum(file => file.Size)));
		Assert.That(store.IsInstalled(CsEn, files), Is.True);
		Assert.That(store.IsInstalled(CsEn, files[..1]), Is.False);
		Assert.That(store.GetInstalledPairs(), Is.EqualTo(new[] { CsEn }));
	}

	[Test]
	public async Task RejectsCorruptDownloadAndKeepsPreviousInstallation()
	{
		var content = new Dictionary<string, byte[]> { ["model.csen.bin"] = [1, 2, 3, 4] };
		using var httpClient = new HttpClient(new FakeServer(content));
		var store = new TranslationModelStore(root, httpClient);
		var good = new[] { CreateFile("model.csen.bin", content["model.csen.bin"]) };
		await store.InstallAsync(CsEn, good);

		var corrupt = new[] { good[0] with { DecompressedHash = new string('0', 64) } };
		await Assert.ThatAsync(() => store.InstallAsync(CsEn, corrupt), Throws.TypeOf<InvalidDataException>());

		Assert.That(store.IsInstalled(CsEn, good), Is.True);
		Assert.That(Directory.GetDirectories(root), Has.Length.EqualTo(1));
	}

	static TranslationModelFile CreateFile(string name, byte[] data) => new(
		name, name.StartsWith("model") ? "model" : "vocab", "3.0", "base", new Uri($"https://attachments.example/{name}.zst"),
		Compress(data).Length, Convert.ToHexStringLower(SHA256.HashData(data)), data.Length);

	static byte[] Compress(byte[] data)
	{
		using var compressor = new Compressor();
		return compressor.Wrap(data).ToArray();
	}

	sealed class FakeServer(Dictionary<string, byte[]> content) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			var name = Path.GetFileNameWithoutExtension(request.RequestUri!.AbsolutePath);
			return Task.FromResult(content.TryGetValue(name, out var data)
				? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Compress(data)) }
				: new HttpResponseMessage(HttpStatusCode.NotFound));
		}
	}

	sealed class SynchronousProgress(Action<long> report) : IProgress<long>
	{
		public void Report(long value) => report(value);
	}
}
