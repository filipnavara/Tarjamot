namespace Tarjamot;

/// <summary>
/// Model record version such as "3.0", "3.1" or "3.0a1". A pre-release ("a1", "b2") sorts
/// before the release with the same number, as in Firefox's version comparator.
/// </summary>
internal readonly record struct ModelVersion(int Major, int Minor, string PreRelease) : IComparable<ModelVersion>
{
	public static ModelVersion? Parse(string text)
	{
		var parts = text.Split('.', 2);
		if (parts.Length != 2 || !int.TryParse(parts[0], out int major))
			return null;
		int digits = 0;
		while (digits < parts[1].Length && char.IsAsciiDigit(parts[1][digits]))
			digits++;
		if (digits == 0)
			return null;
		return new ModelVersion(major, int.Parse(parts[1].AsSpan(0, digits)), parts[1][digits..]);
	}

	public int CompareTo(ModelVersion other)
	{
		int result = Major.CompareTo(other.Major);
		if (result == 0)
			result = Minor.CompareTo(other.Minor);
		if (result != 0)
			return result;
		if (PreRelease.Length == 0 || other.PreRelease.Length == 0)
			return other.PreRelease.Length.CompareTo(PreRelease.Length);
		return string.CompareOrdinal(PreRelease, other.PreRelease);
	}
}
