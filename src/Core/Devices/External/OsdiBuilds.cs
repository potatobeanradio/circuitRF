using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace CircuitRF.Core.Devices.External;

/// <summary>
/// Where one platform's build of a compiled model lives (brief vendorlib-15 R-vl15-1a, D1, D2).
///
/// <para><b>A reference to a compiled model names no platform.</b> A netlist says
/// <c>File="models/TSTFET100.osdi"</c> however many platforms its publisher built for, and each build sits
/// beside it in a folder named for its runtime identifier: <c>models/osx-arm64/TSTFET100.osdi</c>. The
/// platform folder is looked in FIRST, because a plain <c>TSTFET100.osdi</c> beside it is almost always one
/// platform's build copied up, and loading another platform's library fails with a loader message that never
/// mentions the platform.</para>
///
/// <para><b>Applies to every <c>.osdi</c> reference</b>, inside a model library or not, so a workspace can carry
/// several platforms' builds too. Source (<c>.va</c>) is compiled on the machine that runs it and has no
/// platform folders.</para>
/// </summary>
public static class OsdiBuilds
{
    public const string Extension = ".osdi";

    /// <summary>The runtime identifiers circuitRF is packaged for — the packaging scripts' own list, and the
    /// only folder names this looks in.</summary>
    public static readonly IReadOnlyList<string> Rids =
        ["win-x64", "win-arm64", "win-x86", "osx-arm64", "osx-x64", "linux-x64", "linux-arm64"];

    /// <summary>This process's runtime identifier, composed from the operating system and the process's own
    /// architecture rather than read from the runtime, which can report a distribution-specific spelling.</summary>
    public static string CurrentRid { get; } = RidOf(
        OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux",
        RuntimeInformation.ProcessArchitecture);

    private static string RidOf(string os, Architecture arch) => os + "-" + arch switch
    {
        Architecture.X64   => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86   => "x86",
        _                  => arch.ToString().ToLowerInvariant(),
    };

    public static bool IsKnownRid(string rid) => Rids.Contains(rid, StringComparer.Ordinal);

    /// <summary>Does <paramref name="path"/> name a compiled model?</summary>
    public static bool IsCompiled(string path)
        => Path.GetExtension(path).Equals(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary><c>&lt;dir&gt;/&lt;rid&gt;/&lt;name&gt;.osdi</c> for <paramref name="path"/>.</summary>
    public static string PlatformPath(string path, string rid)
        => Path.Combine(Path.GetDirectoryName(path) ?? "", rid, Path.GetFileName(path));

    /// <summary>
    /// The platform-neutral reference for <paramref name="path"/>: <c>&lt;dir&gt;/X.osdi</c> when it is a build in
    /// <c>&lt;dir&gt;/&lt;rid&gt;/X.osdi</c>, else <paramref name="path"/> unchanged. A walk that resolved a reference to
    /// this machine's build stores THIS, or the stored reference names one platform and fails on every other.
    /// </summary>
    public static string NeutralPath(string path)
    {
        if (!IsCompiled(path)) return path;
        string? dir = Path.GetDirectoryName(path);
        if (dir is null || !IsKnownRid(Path.GetFileName(dir)) || Path.GetDirectoryName(dir) is not { } parent) return path;
        return Path.Combine(parent, Path.GetFileName(path));
    }

    /// <summary>
    /// The file a compiled-model reference loads on this machine: this platform's build when it exists, else the
    /// path as written. Anything that is not a <c>.osdi</c> comes back unchanged.
    /// </summary>
    public static string Resolve(string path)
    {
        if (!IsCompiled(path)) return path;
        try
        {
            string platform = PlatformPath(path, CurrentRid);
            if (File.Exists(platform)) return platform;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
        return path;
    }

    /// <summary>The platform folders beside <paramref name="path"/> that hold a build of it, in <see cref="Rids"/>
    /// order.</summary>
    public static IReadOnlyList<string> BuildsBeside(string path)
    {
        var found = new List<string>();
        foreach (string rid in Rids)
        {
            try { if (File.Exists(PlatformPath(path, rid))) found.Add(rid); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
        }
        return found;
    }

    /// <summary>
    /// Why a compiled-model reference has no file on this machine: the current platform, and the platform
    /// folders that do hold a build. Null when the reference is not a <c>.osdi</c>.
    /// </summary>
    public static string? MissingSentence(string path)
    {
        if (!IsCompiled(path)) return null;
        var builds = BuildsBeside(path);
        string name = Path.GetFileName(path);
        return builds.Count == 0
            ? $"There is no build of '{name}' for {CurrentRid}: neither '{PlatformPath(path, CurrentRid)}' nor '{path}' exists, and no platform folder beside it holds one."
            : $"There is no build of '{name}' for {CurrentRid}. The folder holds builds for: {string.Join(", ", builds)}.";
    }

    // ── Which platform a binary was built for ────────────────────────────────

    /// <summary>
    /// The runtime identifiers the shared library at <paramref name="path"/> can load on, read from its own
    /// header: a Windows PE, a macOS Mach-O (a universal one answers for each slice it holds) or a Linux ELF.
    /// Empty when the file is unreadable or not one of those, which is a clear answer and not a refusal: the
    /// caller then has nothing to compare.
    /// </summary>
    public static IReadOnlyList<string> PlatformsOf(string path)
    {
        byte[] head;
        try
        {
            using var fs = File.OpenRead(path);
            head = new byte[PeImports.HeaderPrefixBytes];
            int n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            Array.Resize(ref head, n);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
        return PlatformsOf(head);
    }

    public static IReadOnlyList<string> PlatformsOf(ReadOnlySpan<byte> head)
    {
        if (PeImports.MachineOf(head) is { } pe) return [RidOf("win", pe)];
        if (head.Length < 20) return [];

        uint magicLe = BinaryPrimitives.ReadUInt32LittleEndian(head);
        uint magicBe = BinaryPrimitives.ReadUInt32BigEndian(head);

        // Mach-O, one architecture. cputype follows the magic.
        if (magicLe is 0xFEEDFACF or 0xFEEDFACE)
            return MachO(BinaryPrimitives.ReadUInt32LittleEndian(head[4..])) is { } one ? [one] : [];
        // Universal: a big-endian count, then 20-byte (or 32-byte for the 64-bit form) entries led by cputype.
        if (magicBe is 0xCAFEBABE or 0xCAFEBABF)
        {
            uint count = BinaryPrimitives.ReadUInt32BigEndian(head[4..]);
            int size = magicBe == 0xCAFEBABE ? 20 : 32;
            var rids = new List<string>();
            for (int i = 0; i < count && i < 8 && 8 + (i + 1) * size <= head.Length; i++)
                if (MachO(BinaryPrimitives.ReadUInt32BigEndian(head[(8 + i * size)..])) is { } r && !rids.Contains(r)) rids.Add(r);
            return rids;
        }
        // ELF: e_machine at 18, in the byte order EI_DATA states.
        if (head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F')
        {
            ushort machine = head[5] == 2 ? BinaryPrimitives.ReadUInt16BigEndian(head[18..]) : BinaryPrimitives.ReadUInt16LittleEndian(head[18..]);
            return machine switch
            {
                62  => ["linux-x64"],
                183 => ["linux-arm64"],
                _   => [],
            };
        }
        return [];

        static string? MachO(uint cpu) => cpu switch
        {
            0x01000007 => "osx-x64",
            0x0100000C => "osx-arm64",
            _          => null,
        };
    }
}
