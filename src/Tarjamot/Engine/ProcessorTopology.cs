using System.Runtime.InteropServices;

namespace Tarjamot.Engine;

/// <summary>
/// The number of physical cores. Simultaneous multithreading (SMT) siblings share a core's vector
/// multiply units, which the int8 products saturate, so a second worker per core adds contention
/// rather than throughput.
/// </summary>
internal static partial class ProcessorTopology
{
	static readonly Lazy<int> physicalCores = new(CountPhysicalCores);

	/// <summary>Physical cores available to the process, at most <see cref="Environment.ProcessorCount"/>.</summary>
	public static int PhysicalCores => Math.Clamp(physicalCores.Value, 1, Environment.ProcessorCount);

	static int CountPhysicalCores()
	{
		try
		{
			int cores = OperatingSystem.IsWindows() ? CountWindowsCores()
				: OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() ? CountMacCores()
				: OperatingSystem.IsLinux() || OperatingSystem.IsAndroid() ? CountLinuxCores()
				: 0;
			return cores > 0 ? cores : Environment.ProcessorCount;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
		{
			return Environment.ProcessorCount;
		}
	}

	static unsafe int CountWindowsCores()
	{
		const int RelationProcessorCore = 0;
		uint length = 0;
		GetLogicalProcessorInformationEx(RelationProcessorCore, null, ref length);
		if (length == 0)
			return 0;
		var buffer = new byte[length];
		fixed (byte* pointer = buffer)
		{
			if (!GetLogicalProcessorInformationEx(RelationProcessorCore, pointer, ref length))
				return 0;
		}
		// Only the cores with a logical processor in the affinity mask are available. A process that
		// spans several processor groups has no single mask; all cores count then.
		ushort groupCount = 1;
		ushort group = 0;
		nuint affinity = 0;
		bool filter = GetProcessGroupAffinity(GetCurrentProcess(), ref groupCount, &group) && groupCount == 1 &&
			GetProcessAffinityMask(GetCurrentProcess(), out affinity, out _);
		// Each SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX record (Relationship, Size, ...) is one core.
		int cores = 0;
		for (int offset = 0; offset + 8 <= (int)length;)
		{
			int size = BitConverter.ToInt32(buffer, offset + 4);
			if (size <= 0)
				return 0;
			if (!filter || CoreIntersects(buffer, offset, group, affinity))
				cores++;
			offset += size;
		}
		return cores;
	}

	// The record's PROCESSOR_RELATIONSHIP: Flags, EfficiencyClass, 20 reserved bytes, GroupCount at
	// offset 30, then GROUP_AFFINITY entries (Mask, Group, 3 reserved words) from offset 32.
	static bool CoreIntersects(byte[] buffer, int offset, ushort group, nuint affinity)
	{
		int groupCount = BitConverter.ToUInt16(buffer, offset + 30);
		int entrySize = IntPtr.Size + 4 * sizeof(ushort);
		for (int g = 0; g < groupCount; g++)
		{
			int entry = offset + 32 + g * entrySize;
			nuint mask = IntPtr.Size == 8 ? (nuint)BitConverter.ToUInt64(buffer, entry) : BitConverter.ToUInt32(buffer, entry);
			if (BitConverter.ToUInt16(buffer, entry + IntPtr.Size) == group && (mask & affinity) != 0)
				return true;
		}
		return false;
	}

	static unsafe int CountMacCores()
	{
		int cores = 0;
		nint size = sizeof(int);
		return sysctlbyname("hw.physicalcpu", &cores, ref size, null, 0) == 0 ? cores : 0;
	}

	// Each core lists its hardware threads; SMT siblings share one list.
	static int CountLinuxCores() => Directory.EnumerateDirectories("/sys/devices/system/cpu", "cpu*")
		.Select(directory => Path.Combine(directory, "topology", "thread_siblings_list"))
		.Where(File.Exists)
		.Select(path => File.ReadAllText(path).Trim())
		.Distinct()
		.Count();

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static unsafe partial bool GetLogicalProcessorInformationEx(int relationshipType, byte* buffer, ref uint returnedLength);

	[LibraryImport("kernel32.dll")]
	private static partial nint GetCurrentProcess();

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static unsafe partial bool GetProcessGroupAffinity(nint process, ref ushort groupCount, ushort* groupArray);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetProcessAffinityMask(nint process, out nuint processAffinityMask, out nuint systemAffinityMask);

	[LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
	private static unsafe partial int sysctlbyname(string name, void* oldValue, ref nint oldLength, void* newValue, nint newLength);
}
