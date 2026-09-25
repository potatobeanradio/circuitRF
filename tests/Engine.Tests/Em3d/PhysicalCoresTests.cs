using System.Diagnostics;
using CircuitRF.Engine.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Em3d;

/// <summary>
/// brief-em3d-21 gate 6 — the physical core count. Each OS's parser runs on a sample of what that OS
/// hands back, on every machine; then the count on THIS machine is checked against the OS's own
/// figure, read by the OS's own tool. Apple Silicon has no SMT, so on the F0 Mac physical equals
/// logical and only the parsers can tell the two apart.
/// </summary>
public sealed class PhysicalCoresTests(ITestOutputHelper output)
{
    /// <summary>An x86 machine with SMT: two cores, two threads each — four processors, two pairs.</summary>
    private const string X86Smt = """
        processor	: 0
        vendor_id	: GenuineIntel
        model name	: Intel(R) Core(TM) i3-7100U CPU @ 2.40GHz
        physical id	: 0
        siblings	: 4
        core id		: 0
        cpu cores	: 2

        processor	: 1
        vendor_id	: GenuineIntel
        physical id	: 0
        siblings	: 4
        core id		: 1
        cpu cores	: 2

        processor	: 2
        vendor_id	: GenuineIntel
        physical id	: 0
        siblings	: 4
        core id		: 0
        cpu cores	: 2

        processor	: 3
        vendor_id	: GenuineIntel
        physical id	: 0
        siblings	: 4
        core id		: 1
        cpu cores	: 2

        """;

    /// <summary>Two sockets with the same core ids: the pair, not the core id, is the core.</summary>
    private const string TwoSockets = """
        processor	: 0
        physical id	: 0
        core id		: 0

        processor	: 1
        physical id	: 1
        core id		: 0

        """;

    /// <summary>An arm64 kernel prints neither line.</summary>
    private const string Arm64 = """
        processor	: 0
        BogoMIPS	: 48.00
        Features	: fp asimd evtstrm aes pmull sha1 sha2 crc32 atomics
        CPU implementer	: 0x61

        processor	: 1
        BogoMIPS	: 48.00
        CPU implementer	: 0x61

        """;

    [Fact]
    public void Gate6_LinuxCpuinfo_CountsUniquePhysicalAndCoreIdPairs()
    {
        Assert.Equal(2, PhysicalCores.ParseProcCpuinfo(X86Smt));
        Assert.Equal(2, PhysicalCores.ParseProcCpuinfo(TwoSockets));
        Assert.Null(PhysicalCores.ParseProcCpuinfo(Arm64));          // not the logical count: nothing
        Assert.Equal(2, PhysicalCores.ParseProcCpuinfo(X86Smt.Replace("\n", "\r\n")));
    }

    [Fact]
    public void Gate6_LinuxSysfs_CountsDistinctThreadSiblingLists()
    {
        Assert.Equal(2, PhysicalCores.ParseThreadSiblings(["0,2\n", "1,3\n", "0,2\n", "1,3\n"]));   // SMT
        Assert.Equal(4, PhysicalCores.ParseThreadSiblings(["0\n", "1\n", "2\n", "3\n"]));           // arm64
        Assert.Null(PhysicalCores.ParseThreadSiblings([]));
    }

    /// <summary>
    /// A <c>GetLogicalProcessorInformationEx</c> buffer as the documented layout gives it: each record
    /// a 32-bit relationship, a 32-bit size, then that record's own body. Four cores of 48 bytes (a
    /// PROCESSOR_RELATIONSHIP with one GROUP_AFFINITY), and a 56-byte cache record that is skipped.
    /// </summary>
    [Fact]
    public void Gate6_WindowsBuffer_CountsProcessorCoreRecords()
    {
        var buffer = new List<byte>();
        void Record(int relationship, int size)
        {
            buffer.AddRange(BitConverter.GetBytes(relationship));
            buffer.AddRange(BitConverter.GetBytes(size));
            buffer.AddRange(new byte[size - 8]);
        }
        for (int i = 0; i < 4; i++) Record(0, 48);
        Record(2, 56);
        Assert.Equal(4, PhysicalCores.CountWindowsCores(buffer.ToArray()));

        Assert.Null(PhysicalCores.CountWindowsCores(buffer.Take(buffer.Count - 4).ToArray()));   // truncated
        Assert.Null(PhysicalCores.CountWindowsCores(new byte[8]));                               // size 0
    }

    [Fact]
    public void Gate6_TheFallback_IsHalfTheLogicalCount_AtLeastOne()
    {
        Assert.Equal(4, PhysicalCores.Fallback(8));
        Assert.Equal(1, PhysicalCores.Fallback(1));
    }

    /// <summary>On the machine running the tests, the count equals the OS's own figure.</summary>
    [Fact]
    public void Gate6_OnThisMachine_TheCountIsTheOsOwnFigure()
    {
        var r = PhysicalCores.Current;
        output.WriteLine($"{r.Count} physical core(s), {Environment.ProcessorCount} logical, from {r.How}");
        Assert.True(r.Measured, r.How);

        string? own =
            OperatingSystem.IsMacOS()   ? Tool("/usr/sbin/sysctl", "-n hw.physicalcpu") :
            OperatingSystem.IsLinux()   ? CountLscpu(Tool("lscpu", "-p=Core,Socket")) :
            OperatingSystem.IsWindows() ? Tool("powershell", "-NoProfile -Command \"(Get-CimInstance Win32_Processor | Measure-Object -Property NumberOfCores -Sum).Sum\"") :
            null;
        output.WriteLine($"the OS's own figure: {own ?? "(not available)"}");
        if (own is null) return;       // no tool to ask; the parsers above are still gated
        Assert.Equal(int.Parse(own.Trim()), r.Count);
    }

    private static string? CountLscpu(string? text)
        => text is null ? null
         : text.Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).Distinct().Count().ToString();

    private static string? Tool(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            });
            if (p is null) return null;
            string text = p.StandardOutput.ReadToEnd();
            return p.WaitForExit(10_000) && p.ExitCode == 0 ? text : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
