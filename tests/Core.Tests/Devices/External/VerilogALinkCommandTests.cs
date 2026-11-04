using System.IO;
using System.Linq;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// Brief vendorlib-18 §5.1: the compiler's link line for each of the six platforms, as zig's. The inputs are the link
/// lines the compiler was measured writing (the GNU-flavour line on a macOS host runs <c>clang</c>; Linux x86-64 adds
/// <c>-m elf_x86_64</c>; every Windows target runs <c>link.exe</c> with its own import library).
/// </summary>
public sealed class VerilogALinkCommandTests
{
    private static readonly string Work = Path.Combine(Path.GetTempPath(), "work");

    private static string[] Objects(string stem) => [$"/o/{stem}.o1", $"/o/{stem}.o2", $"/o/{stem}.o"];

    private static string[] Elf(string stem, bool x64)
        => [.. (string[])["--no-add-needed", "--hash-style=gnu"], .. x64 ? (string[])["-m", "elf_x86_64"] : [], .. Objects(stem), "-o", "/out/m.osdi", "-shared"];

    private static string[] Pe(string stem)
        => ["/NOLOGO", .. Objects(stem), "/OUT:/out/m.osdi", "/DLL", "msvcrt.lib", "/out/__import.lib"];

    private static string[] MachO(string stem)
        => ["-Wl,-undefined,dynamic_lookup", .. Objects(stem), "-o", "/out/m.osdi", "-dynamiclib"];

    [Theory]
    [InlineData("linux-x64", "x86_64-linux-gnu.2.28")]
    [InlineData("linux-arm64", "aarch64-linux-gnu.2.28")]
    [InlineData("win-x64", "x86_64-windows-gnu")]
    [InlineData("win-arm64", "aarch64-windows-gnu")]
    [InlineData("osx-x64", "x86_64-macos")]
    [InlineData("osx-arm64", "aarch64-macos")]
    public void ZigLinksEveryPlatform_FromTheCompilersOwnLine(string rid, string target)
    {
        string[] line = rid[..rid.IndexOf('-')] switch
        {
            "linux" => Elf("m", rid.EndsWith("x64")),
            "win" => Pe("m"),
            _ => MachO("m"),
        };
        var plan = VerilogALinkDriver.LinkCommand(rid, line, Work, onMac: false);

        Assert.Equal(VerilogALinkDriver.Zig, plan.Program);
        Assert.Equal(["cc", "-target", target, "-shared", "-o", Path.Combine(Work, "link.osdi")], plan.Arguments.Take(6));
        Assert.Equal("/out/m.osdi", plan.Destination);
        // The GNU ld and MSVC options are gone, and the runtime library zig supplies itself.
        foreach (string dropped in new[] { "--no-add-needed", "--hash-style=gnu", "-m", "elf_x86_64", "/NOLOGO", "/DLL", "msvcrt.lib", "-dynamiclib" })
            Assert.DoesNotContain(dropped, plan.Arguments);
        // .oN objects are linked as renamed copies; the .o is linked as it is.
        Assert.Equal([("/o/m.o1", Path.Combine(Work, "m.o1.o")), ("/o/m.o2", Path.Combine(Work, "m.o2.o"))], plan.Copies);
        Assert.Contains(Path.Combine(Work, "m.o1.o"), plan.Arguments);
        Assert.Contains("/o/m.o", plan.Arguments);
        Assert.DoesNotContain("/o/m.o1", plan.Arguments);

        if (rid.StartsWith("linux")) Assert.Equal(["-lc", "-lm"], plan.Arguments.TakeLast(2));
        if (rid.StartsWith("win")) Assert.Contains("/out/__import.lib", plan.Arguments);   // the compiler's own import library
        if (rid.StartsWith("osx")) Assert.Contains("-Wl,-undefined,dynamic_lookup", plan.Arguments);
        // _fltused for x86-64 Windows only.
        Assert.Equal(rid == "win-x64", plan.Writes.Any(w => w.Text.Contains("int _fltused = 0;")));
        Assert.Equal(rid == "win-x64", plan.Arguments.Any(a => a.EndsWith("fltused.c")));
    }

    [Theory]
    [InlineData("osx-x64", "x86_64")]
    [InlineData("osx-arm64", "arm64")]
    public void OnAMac_AMacBuildIsTheSystemLinkerWithTheArchitectureNamed(string rid, string arch)
    {
        var plan = VerilogALinkDriver.LinkCommand(rid, MachO("m"), Work, onMac: true);

        Assert.Equal("cc", plan.Program);
        Assert.Equal(["-arch", arch], plan.Arguments.Take(2));
        Assert.Contains("/o/m.o1", plan.Arguments);   // the system linker takes the compiler's objects as they are
        Assert.Empty(plan.Copies);
        Assert.Equal("/out/m.osdi", plan.Destination);
    }
}
