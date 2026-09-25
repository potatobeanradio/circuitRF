// brief-em3d-21 R-em3d21-3 — how many PHYSICAL cores this machine has, per operating system.
//
// A Palace run starts one MPI rank per core. Open MPI's default slot count is the physical core
// count, so on an x86 machine with SMT (two logical processors per core) a run asking for
// Environment.ProcessorCount ranks is refused for lack of slots — the series 1 review found it. The
// default rank count is therefore this number, never the logical one.
//
// Each OS has its own source, and each source has a PARSER that takes the raw text or bytes and no
// process, file or handle, so the gate runs every one of them on a captured sample on any machine:
//   * macOS  — sysctl hw.physicalcpu, read through sysctlbyname (the value is an integer; there is
//              nothing to parse, and the gate compares it against the sysctl program's own figure);
//   * Linux  — the unique (physical id, core id) pairs of /proc/cpuinfo, and where those lines are
//              absent (arm64 prints neither) the unique thread_siblings_list sets of
//              /sys/devices/system/cpu/cpu*/topology;
//   * Windows — GetLogicalProcessorInformationEx(RelationProcessorCore): one record per core.
// When none of them answers, the count falls back to half the logical count (at least 1) and says so.

using System.Runtime.InteropServices;

namespace CircuitRF.Engine.Em3d;

/// <summary>The physical core count, and how it was found. <see cref="Measured"/> is false for the
/// fallback, and <see cref="How"/> is then a sentence a run's notes can carry.</summary>
public sealed record PhysicalCoreReading(int Count, bool Measured, string How);

public static class PhysicalCores
{
    private static readonly Lazy<PhysicalCoreReading> Reading = new(Read);

    /// <summary>This machine's reading, read once per process.</summary>
    public static PhysicalCoreReading Current => Reading.Value;

    /// <summary>This machine's physical core count.</summary>
    public static int Count => Current.Count;

    /// <summary>The fallback when no source answers: half the logical count, at least 1.</summary>
    public static int Fallback(int logical) => Math.Max(1, logical / 2);

    private static PhysicalCoreReading Read()
    {
        int logical = Environment.ProcessorCount;
        int? n = null;
        string how = "";
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                n = MacPhysicalCpu();
                how = "sysctl hw.physicalcpu";
            }
            else if (OperatingSystem.IsLinux())
            {
                if (File.Exists("/proc/cpuinfo") && ParseProcCpuinfo(File.ReadAllText("/proc/cpuinfo")) is { } c)
                    (n, how) = (c, "/proc/cpuinfo");
                else if (ReadSysfsSiblings() is { Count: > 0 } lists && ParseThreadSiblings(lists) is { } s)
                    (n, how) = (s, "/sys/devices/system/cpu/*/topology");
            }
            else if (OperatingSystem.IsWindows())
            {
                n = WindowsCoreRecords() is { } buffer ? CountWindowsCores(buffer) : null;
                how = "GetLogicalProcessorInformationEx";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException
                                     or EntryPointNotFoundException)
        {
            n = null;
        }

        if (n is { } count && count >= 1)
            return new PhysicalCoreReading(count, true, how);
        int fallback = Fallback(logical);
        return new PhysicalCoreReading(fallback, false,
            $"the physical core count could not be read on this machine, so half of its {logical} logical " +
            $"processor(s) — {fallback} — was assumed");
    }

    // ── Linux ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The unique <c>(physical id, core id)</c> pairs of a <c>/proc/cpuinfo</c>. Null when the text
    /// carries no such pair — arm64 kernels print neither line, and a count of processors there would
    /// be the logical count this exists to avoid.
    /// </summary>
    public static int? ParseProcCpuinfo(string text)
    {
        var pairs = new HashSet<(string, string)>();
        string? physical = null, core = null;
        void Flush()
        {
            if (physical is not null && core is not null) pairs.Add((physical, core));
            physical = core = null;
        }
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) { Flush(); continue; }
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string key = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
            if (key == "physical id") physical = value;
            else if (key == "core id") core = value;
        }
        Flush();
        return pairs.Count > 0 ? pairs.Count : null;
    }

    /// <summary>The number of distinct <c>thread_siblings_list</c> values — one per physical core,
    /// since every logical processor of a core lists the same siblings. Null for an empty input.</summary>
    public static int? ParseThreadSiblings(IEnumerable<string> siblingLists)
    {
        var distinct = siblingLists.Select(s => s.Trim()).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
        return distinct.Count > 0 ? distinct.Count : null;
    }

    private static List<string> ReadSysfsSiblings()
    {
        var lists = new List<string>();
        const string root = "/sys/devices/system/cpu";
        if (!Directory.Exists(root)) return lists;
        foreach (string dir in Directory.EnumerateDirectories(root, "cpu*"))
        {
            string name = Path.GetFileName(dir);
            if (name.Length <= 3 || !name[3..].All(char.IsAsciiDigit)) continue;
            string file = Path.Combine(dir, "topology", "thread_siblings_list");
            if (File.Exists(file)) lists.Add(File.ReadAllText(file));
        }
        return lists;
    }

    // ── Windows ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The number of <c>RelationProcessorCore</c> records in a
    /// <c>GetLogicalProcessorInformationEx</c> buffer. Every record opens with its relationship
    /// (a 32-bit value, 0 for a core) and its own size in bytes (32-bit), and the next record starts
    /// that many bytes on. Null when the buffer is malformed — a size that runs past the end, or zero.
    /// </summary>
    public static int? CountWindowsCores(ReadOnlySpan<byte> buffer)
    {
        const int RelationProcessorCore = 0;
        int count = 0, offset = 0;
        while (offset < buffer.Length)
        {
            if (buffer.Length - offset < 8) return null;
            int relationship = BitConverter.ToInt32(buffer.Slice(offset, 4));
            int size = BitConverter.ToInt32(buffer.Slice(offset + 4, 4));
            if (size < 8 || size > buffer.Length - offset) return null;
            if (relationship == RelationProcessorCore) count++;
            offset += size;
        }
        return count > 0 ? count : null;
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint returnedLength);

    private static byte[]? WindowsCoreRecords()
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref length);
        if (length == 0) return null;
        IntPtr p = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(0, p, ref length)) return null;
            var bytes = new byte[length];
            Marshal.Copy(p, bytes, 0, (int)length);
            return bytes;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    // ── macOS ───────────────────────────────────────────────────────────────────────────────────

    [DllImport("libc")]
    private static extern int sysctlbyname(string name, out int value, ref nint length, IntPtr newValue, nint newLength);

    private static int? MacPhysicalCpu()
    {
        nint length = sizeof(int);
        return sysctlbyname("hw.physicalcpu", out int value, ref length, IntPtr.Zero, 0) == 0 && value > 0 ? value : null;
    }
}
