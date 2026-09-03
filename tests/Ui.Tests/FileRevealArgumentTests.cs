// ================================================================
//  FileRevealArgumentTests.cs
//  Reveal's per-platform argument forms are not interchangeable, and both ways of getting one wrong
//  are silent.
//
//  Reported on Windows (2026-09-03): Help ▸ Crash Reports… opened the WRONG directory. Not on macOS.
//  Explorer's command-line parser is not the standard one — it needs `/select,"<path>"`, the switch
//  bare and the path quoted — and ArgumentList cannot express that: .NET quotes an argument as a
//  whole when it contains a space, so Explorer receives a quoted SWITCH, does not recognise it,
//  treats the lot as a path, and falls back to its default folder. A space-free path works, which is
//  what made it look intermittent.
// ================================================================

using System.Diagnostics;
using System.Linq;
using CircuitRF.Ui;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class FileRevealArgumentTests
{
    private const string Spaced = @"C:\Users\First Last\AppData\Local\circuitRF\crash-reports\crash-1.log";
    private const string Plain  = @"C:\Users\rfuser\AppData\Local\circuitRF\crash-reports\crash-1.log";

    /// <summary>
    /// The command line Windows will actually receive. With ArgumentList empty, Arguments IS the
    /// command line — so asserting it is asserting what Explorer parses.
    /// </summary>
    [Theory]
    [InlineData(Spaced)]
    [InlineData(Plain)]
    public void WindowsFile_QuotesThePath_AndLeavesTheSwitchBare(string path)
    {
        var psi = FileReveal.BuildCommand(path, isFile: true, FileReveal.Platform.Windows);

        Assert.NotNull(psi);
        Assert.Equal("explorer.exe", psi!.FileName);
        Assert.Empty(psi.ArgumentList);
        Assert.Equal($"/select,\"{path}\"", psi.Arguments);

        // The regression itself: a quoted switch is what Explorer cannot read.
        Assert.StartsWith("/select,", psi.Arguments, System.StringComparison.Ordinal);
        Assert.DoesNotContain("\"/select", psi.Arguments);
    }

    /// <summary>
    /// The form that produced the report, kept here as the thing that must not come back: handing
    /// `/select,<path>` to ArgumentList makes .NET quote the whole argument once a space appears.
    /// Built with the runtime's own pasting, not a re-implementation of it.
    /// </summary>
    [Fact]
    public void TheOldArgumentListForm_QuotesTheSwitchToo_WhichIsTheBug()
    {
        string Built(string p)
        {
            var psi = new ProcessStartInfo("explorer.exe", new[] { $"/select,{p}" });
            var m = typeof(ProcessStartInfo).GetMethod("BuildArguments",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return (string)m!.Invoke(psi, null)!;
        }

        Assert.Equal($"/select,{Plain}", Built(Plain));            // no space: survived, hence intermittent
        Assert.Equal($"\"/select,{Spaced}\"", Built(Spaced));      // a space: the switch is inside the quotes
    }

    /// <summary>A Windows DIRECTORY takes the ordinary path — no switch, so normal quoting is both
    /// correct and safe, and the raw-string form never meets a trailing separator.</summary>
    [Fact]
    public void WindowsDirectory_UsesAnOrdinaryArgument()
    {
        var psi = FileReveal.BuildCommand(@"C:\Users\First Last\Documents", isFile: false,
                                          FileReveal.Platform.Windows);

        Assert.NotNull(psi);
        Assert.Equal("", psi!.Arguments);
        Assert.Equal(new[] { @"C:\Users\First Last\Documents" }, psi.ArgumentList);
    }

    /// <summary>
    /// The Unix branches keep ArgumentList, and must: there a double quote IS legal in a filename,
    /// and .NET parses a single argument string into argv itself — the 2026-08-25 security finding.
    /// A Windows path can never contain one (it is a reserved character), which is what makes the
    /// raw string safe there and only there.
    /// </summary>
    [Fact]
    public void UnixBranches_KeepArgumentList_SoAQuotedFilenameCannotInjectArguments()
    {
        const string nasty = "/tmp/a\" -a Calculator \".s2p";

        var mac = FileReveal.BuildCommand(nasty, isFile: true, FileReveal.Platform.MacOS);
        Assert.Equal("open", mac!.FileName);
        Assert.Equal("", mac.Arguments);
        Assert.Equal(new[] { "-R", nasty }, mac.ArgumentList);

        var lin = FileReveal.BuildCommand(nasty, isFile: true, FileReveal.Platform.Other);
        Assert.Equal("xdg-open", lin!.FileName);
        Assert.Equal("", lin.Arguments);
        Assert.Equal(new[] { "/tmp" }, lin.ArgumentList);          // the containing folder
    }

    [Fact]
    public void MacDirectory_OpensIt_WithoutTheSelectFlag()
    {
        var psi = FileReveal.BuildCommand("/tmp/reports", isFile: false, FileReveal.Platform.MacOS);
        Assert.Equal(new[] { "/tmp/reports" }, psi!.ArgumentList);
    }
}
