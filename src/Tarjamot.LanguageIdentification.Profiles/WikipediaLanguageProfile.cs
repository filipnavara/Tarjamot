using System.Buffers;

namespace Tarjamot.LanguageIdentification;

/// <summary>
/// The prebuilt language profile: 55 languages built from Wikipedia articles with tools/LanguageProfiles (500,000
/// characters of random articles per language, the 2,500 most frequent n-grams of 1 to 3 characters). The profile
/// is derived from Wikipedia's text and licensed under CC BY-SA 4.0; see the package README.
/// </summary>
public static class WikipediaLanguageProfile
{
	private const string ResourceName = "Tarjamot.LanguageIdentification.Profiles.wikipedia.tables";

	/// <summary>
	/// Loads a <see cref="LanguageIdentifier"/> from the embedded tables. The tables are used in place in the
	/// assembly's image, so loading allocates only the language codes and takes microseconds.
	/// </summary>
	public static LanguageIdentifier Load()
	{
		using var stream = typeof(WikipediaLanguageProfile).Assembly.GetManifestResourceStream(ResourceName)
			?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");
		if (stream is UnmanagedMemoryStream unmanaged)
			return LanguageIdentifier.LoadTables(new ImageMemoryManager(unmanaged).Memory);

		var tables = new byte[stream.Length];
		stream.ReadExactly(tables);
		return LanguageIdentifier.LoadTables(tables);
	}

	/// <summary>
	/// Memory of an embedded resource, which stays mapped and unchanged as long as the assembly is loaded (the
	/// assembly is not collectible).
	/// </summary>
	private sealed unsafe class ImageMemoryManager(UnmanagedMemoryStream stream) : MemoryManager<byte>
	{
		private readonly byte* pointer = stream.PositionPointer;
		private readonly int length = checked((int)stream.Length);

		public override Span<byte> GetSpan() => new(pointer, length);

		public override MemoryHandle Pin(int elementIndex = 0) => new(pointer + elementIndex);

		public override void Unpin()
		{
		}

		protected override void Dispose(bool disposing)
		{
		}
	}
}
