using System.Runtime.InteropServices;
using CircuitRF.Core.Devices.External;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// <b>A worker that hosts a compiled model must be built for the MODEL'S architecture.</b>
///
/// <para>Reported on Windows arm64, 2026-09: placing a compiled Verilog-A model refused, naming a
/// helper the user had no way to obtain. The cause was not the architecture at all — the OSDI worker
/// had never been built on Windows on any architecture — but the architecture question is real and
/// arrives immediately behind it, because an arm64 Windows machine routinely runs a translated x64
/// Verilog-A compiler and that compiler emits x64 <c>.osdi</c> files. circuitRF's own architecture
/// never enters into it: the worker is a separate process.</para>
///
/// <para><b>The headers here are written by hand, byte by byte, from the published PE layout.</b>
/// Building a real DLL for each architecture would need a cross-compiler on the test machine, and
/// asserting against a file this repository produced would only prove our writer and our reader
/// agree. A literal 0x8664 at the documented offset is an independent statement of the contract.</para>
/// </summary>
public class OsdiWorkerArchitectureTests : IDisposable
{
    private readonly string _dir;
    private readonly string _savedTools;

    public OsdiWorkerArchitectureTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crf-osdi-arch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _savedTools = DeviceWorkerManifest.ToolsDirectory;
        DeviceWorkerManifest.ToolsDirectory = _dir;
    }

    public void Dispose()
    {
        DeviceWorkerManifest.ToolsDirectory = _savedTools;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ── a PE header, written from the specification ──────────────────────────

    private const ushort MachineX64   = 0x8664;
    private const ushort MachineArm64 = 0xAA64;
    private const ushort MachineX86   = 0x014C;

    /// <summary>
    /// The smallest byte sequence that answers "which machine": the MZ signature, the offset of the
    /// COFF header written at 0x3C, "PE\0\0", and the machine word immediately after it.
    /// </summary>
    private static byte[] PeHeader(ushort machine, int peOffset = 0x80)
    {
        var b = new byte[peOffset + 8];
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BitConverter.GetBytes(peOffset).CopyTo(b, 0x3C);
        b[peOffset] = (byte)'P'; b[peOffset + 1] = (byte)'E';        // "PE\0\0"
        BitConverter.GetBytes(machine).CopyTo(b, peOffset + 4);
        return b;
    }

    private string WriteFile(string name, byte[] bytes)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string WriteModel(string name, ushort machine) => WriteFile(name, PeHeader(machine));

    private static string Refusal(string modelFile)
    {
        var ex = Assert.Throws<ExternalDeviceException>(
            () => new VerilogAFileResolver().Resolve(VerilogAFileResolver.ProviderNameFor(modelFile)));
        return ex.Message;
    }

    // ── the reader ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(MachineX64,   nameof(Architecture.X64))]
    [InlineData(MachineArm64, nameof(Architecture.Arm64))]
    [InlineData(MachineX86,   nameof(Architecture.X86))]
    public void MachineOf_ReadsTheArchitectureOutOfAPeHeader(ushort machine, string expected)
        => Assert.Equal(expected, PeImports.MachineOf(PeHeader(machine))!.Value.ToString());

    /// <summary>
    /// A PE offset past the prefix that is read is refused rather than chased. This is what keeps
    /// identifying an architecture from reading a hundred-megabyte model library — and a value that
    /// large is a corrupt file, not a large one.
    /// </summary>
    [Fact]
    public void MachineOf_RefusesAHeaderOffsetBeyondThePrefixItReads()
    {
        var far = PeHeader(MachineX64, peOffset: PeImports.HeaderPrefixBytes + 64);
        Assert.Null(PeImports.MachineOf(far.AsSpan(0, PeImports.HeaderPrefixBytes)));
    }

    /// <summary>
    /// Null for anything that is not a PE, which is the answer on every non-Windows platform. That
    /// is what lets the selection rule below be written with no branch on the operating system: a
    /// Mach-O or an ELF simply yields no architecture to match on.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' })]        // ELF
    [InlineData(new byte[] { 0xCF, 0xFA, 0xED, 0xFE })]                      // Mach-O, 64-bit
    [InlineData(new byte[] { (byte)'n', (byte)'o', (byte)'p', (byte)'e' })]
    [InlineData(new byte[0])]
    public void MachineOf_IsNullForAnythingThatIsNotAPe(byte[] bytes)
        => Assert.Null(PeImports.MachineOf(bytes));

    // ── selection ────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The mismatch is named as a mismatch.</b> Two workers are beside the application and the
    /// model needs neither, so the message must say which architecture the model is and which ones
    /// are present — not that the helper is missing, which is what a user would then go and try to
    /// fix by rebuilding something they already have.
    /// </summary>
    [Fact]
    public void AModelNoShippedWorkerCanHost_IsRefusedByArchitecture_NotAsAnAbsence()
    {
        WriteFile("osdi-worker-x64.exe", PeHeader(MachineX64));
        WriteFile("osdi-worker.exe",     PeHeader(MachineX64));

        string message = Refusal(WriteModel("model.osdi", MachineArm64));

        Assert.Contains("arm64", message, StringComparison.Ordinal);
        Assert.Contains("x64", message, StringComparison.Ordinal);
        Assert.DoesNotContain("was not found beside", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the day a worker for that architecture ships beside the application, the sentence above
    /// stops being produced — with nothing to remember to delete, because it was never a written-down
    /// claim about which platforms are supported. Here the refusal changes from the architecture one
    /// to whatever starting the (bogus) worker reports, which is a different failure entirely.
    /// </summary>
    [Fact]
    public void ShippingTheMatchingWorker_RetiresTheArchitectureMessageWithNoCodeChange()
    {
        string model = WriteModel("model.osdi", MachineArm64);
        WriteFile("osdi-worker-x64.exe", PeHeader(MachineX64));

        Assert.Contains("present only for x64", Refusal(model), StringComparison.Ordinal);

        WriteFile("osdi-worker-arm64.exe", PeHeader(MachineArm64));

        // It is not a working worker - it is eight bytes of header - so this still fails. What it
        // must NOT say any more is that the architecture is unavailable.
        string after;
        try
        {
            new VerilogAFileResolver().Resolve(VerilogAFileResolver.ProviderNameFor(model));
            after = "";
        }
        catch (Exception ex) { after = ex.Message; }

        Assert.DoesNotContain("present only for", after, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing beside the application at all is the OTHER message, and it names the running build —
    /// because "which one am I running?" is the first thing asked of a helper that is not there, and
    /// on Windows arm64 the answer may well be the x64 build.
    /// </summary>
    [Fact]
    public void NoWorkerAtAll_NamesThePlatformAndArchitectureOfTheRunningBuild()
    {
        string message = Refusal(WriteModel("model.osdi", MachineX64));

        Assert.Contains("was not found beside", message, StringComparison.Ordinal);
        Assert.Contains(ExpectedArchToken(), message, StringComparison.Ordinal);
    }

    private static string ExpectedArchToken() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64   => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86   => "x86",
        _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// A worker whose own architecture cannot be read — every POSIX build, which is not a PE — is
    /// taken as before. Without this the whole rule would refuse macOS and Linux outright.
    /// </summary>
    [Fact]
    public void AWorkerThatDeclaresNoArchitecture_IsStillUsed()
    {
        WriteFile("osdi-worker", [0x7F, (byte)'E', (byte)'L', (byte)'F', 0, 0, 0, 0]);

        string message = Refusal(WriteModel("model.osdi", MachineArm64));

        Assert.DoesNotContain("present only for", message, StringComparison.Ordinal);
        Assert.DoesNotContain("was not found beside", message, StringComparison.Ordinal);
    }
}
