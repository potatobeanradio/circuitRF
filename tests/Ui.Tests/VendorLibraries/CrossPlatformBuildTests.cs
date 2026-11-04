using System;
using System.IO;
using CircuitRF.Core.Devices.External;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using Xunit;

namespace CircuitRF.Ui.Tests.VendorLibraries;

/// <summary>
/// Brief vendorlib-18 §5.2 and §5.3, against a stub compiler that does what the real one does at the two points that
/// matter: it runs its linker by name from PATH (so the launcher, the real CLI executable, runs the real driver), or it
/// crashes naming a log that holds the processor message. Serialised with the tests that point the compiler lookup
/// elsewhere.
/// </summary>
[Collection(ExternalDeviceRegistryCollection.Name)]
public sealed class CrossPlatformBuildTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-vl18-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly Func<string?>? _compiler = VerilogACompilerDiscovery.PreferredCommand;
    private readonly Func<string?>? _zig = ZigDiscovery.PreferredCommand;

    public CrossPlatformBuildTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        VerilogACompilerDiscovery.PreferredCommand = _compiler;
        ZigDiscovery.PreferredCommand = _zig;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Stub(string body)
    {
        string stub = Path.Combine(_dir, "stub-compiler");
        File.WriteAllText(stub, "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo stub 1.0; exit 0; fi\n" + body);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        VerilogACompilerDiscovery.PreferredCommand = () => stub;
        return stub;
    }

    private string Source()
    {
        string va = Path.Combine(_dir, "m.va");
        File.WriteAllText(va, "module m(a); endmodule\n");
        return va;
    }

    // §5.2: no zig — every row that links with zig says to install it, in the driver's own words first.
    [Fact]
    public void AMissingZig_IsTheInstallSentence_OnEveryRowZigLinks()
    {
        if (OperatingSystem.IsWindows()) return;   // the stub is a shell script
        LinkDriverHost.Ensure();
        ZigDiscovery.PreferredCommand = () => Path.Combine(_dir, "no-such-zig");
        // The compiler's link: `link.exe` for a Windows target, the host's GNU-flavour linker otherwise.
        Stub("out=''; t=''\nwhile [ $# -gt 0 ]; do case \"$1\" in -o) shift; out=\"$1\";; --target) shift; t=\"$1\";; esac; shift; done\n" +
             "case \"$t\" in *windows*) link.exe /NOLOGO m.o1 \"/OUT:$out\" /DLL msvcrt.lib 2>&1;; " +
             "*) ld --no-add-needed m.o1 -o \"$out\" -shared 2>&1;; esac\nexit $?\n");

        foreach (var (rid, triple) in new[] { ("linux-x64", "x86_64-unknown-linux"), ("linux-arm64", "aarch64-unknown-linux"),
                                              ("win-x64", "x86_64-pc-windows"), ("win-arm64", "aarch64-pc-windows") })
        {
            var ex = Assert.Throws<ExternalDeviceException>(() =>
                VerilogASourceCompiler.Build(Source(), Path.Combine(_dir, "out", rid, "m.osdi"), triple, rid: rid));
            Assert.Equal(VerilogALinkDriver.ZigMissingLine(rid), VerilogALinkDriver.DriverLine(ex.Message));
            Assert.Equal(ModelLibraryExport.InstallZig, ModelLibraryExport.FixFor(rid, ex.Message));
        }
    }

    // §5.3: the processor limit is read from the compiler's own crash — here only in the log it names, as the real one does.
    [Fact]
    public void TheProcessorCrash_IsTheProcessorSentence()
    {
        if (OperatingSystem.IsWindows()) return;
        LinkDriverHost.Ensure();
        string log = Path.Combine(_dir, "crash.log");
        File.WriteAllText(log, "panic\ncalled `Result::unwrap()` on an `Err` value: \"No available targets are compatible with triple \\\"x86_64-unknown-linux-gnu\\\"\"\n");
        Stub($"echo 'The compiler has crashed!' >&2\necho 'A log file has been generated at \"{log}\".' >&2\nexit 101\n");

        var ex = Assert.Throws<ExternalDeviceException>(() =>
            VerilogASourceCompiler.Build(Source(), Path.Combine(_dir, "out", "m.osdi"), "x86_64-unknown-linux", rid: "linux-x64"));

        Assert.Contains(VerilogASourceCompiler.ProcessorLimitMarker, ex.Message);
        Assert.StartsWith("The Verilog-A compiler builds only for the processor it runs on (", ModelLibraryExport.FixFor("linux-x64", ex.Message));
        Assert.EndsWith(" here). A compiler build that targets every processor builds this platform too.", ModelLibraryExport.FixFor("linux-x64", ex.Message));
    }
}
