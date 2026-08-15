// ================================================================
//  HarmonicaSetVswrDialogTests.cs — brief-harmonicarf-r6b §2.1
//
//  "VSWR: <val> ▸ Set…" — a single numeric field, OK/Cancel gated, reject-and-keep on bad input.
//  Following the SAME source-text-pinning convention HarmonicaSetZ0DialogTests already uses for a
//  dialog that is a live Avalonia Window (no headless-instantiation harness in this codebase for
//  harmonicaRF dialogs — every existing dialog test in this folder pins behaviour from source text).
// ================================================================

using System;
using System.IO;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaSetVswrDialogTests
{
    private static string DialogSource() =>
        ReadSource("src", "Ui", "Views", "Dialogs", "HarmonicaSetVswrDialog.axaml.cs");

    private static string ViewSource() =>
        ReadSource("src", "Ui", "Views", "Harmonica", "HarmonicaView.axaml.cs");

    private static string ReadSource(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine([dir!.FullName, .. parts]);
        Assert.True(File.Exists(path), $"source not found at {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void Commit_RejectsNonFiniteAndBelowOne_LeavingTheDialogOpen()
    {
        string src = DialogSource();
        Assert.Contains("!double.IsFinite(v)", src, StringComparison.Ordinal);
        Assert.Contains("if (v < 1.0)", src, StringComparison.Ordinal);
        // Rejection shows an error and returns — it never calls Close(true) on the bad-input path.
        Assert.Contains("ShowError(\"VSWR must be a number.\");\n            return false;", src, StringComparison.Ordinal);
        Assert.Contains("ShowError(\"VSWR must be at least 1.\");\n            return false;", src, StringComparison.Ordinal);
    }

    [Fact]
    public void Commit_NeverSilentlySubstitutes_TheAcceptedValueIsExactlyWhatWasTyped()
    {
        string src = DialogSource();
        // _result is set to the parsed value verbatim — never clamped/rounded here (SetMarkerVswr,
        // the caller, is what applies the MinVswr floor).
        Assert.Contains("_result = v;", src, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Clamp", src, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Max", src, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowMarkerSetVswrDialogAsync_CommitsThrough_SetMarkerVswr_TheSameCallTheDragUses()
    {
        string src = ViewSource();
        int m = src.IndexOf("private async System.Threading.Tasks.Task ShowMarkerSetVswrDialogAsync(",
            StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    }", m, StringComparison.Ordinal);
        string body = src[m..mEnd];

        Assert.Contains("HarmonicaSetVswrDialog.ShowAsync(owner, marker.Name, marker.VswrValue)", body, StringComparison.Ordinal);
        Assert.Contains("h.SetMarkerVswr(marker, v);", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkerMenu_HeaderAndSetSubmenu_UseTheSharedFormatter()
    {
        string src = ViewSource();
        int m = src.IndexOf("private void BuildMarkerMenu(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    private async System.Threading.Tasks.Task ShowMarkerSetVswrDialogAsync", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        // R8B §7.1 — the header IS "VSWR: <val>" (saturated-aware), via the SAME formatter §1.3's
        // live readout uses; the value row and "Set…" now appear only when Show VSWR is on, and
        // "Set…" is its own CHILD of that row (never a flattened sibling any more — a value row with
        // no children could not host it, and R7A §2.4's "no Click on a row with children" trap is why
        // the value row itself carries none).
        Assert.Contains("HarmonicaReadoutFormatting.FormatVswr(marker.VswrValue, saturated)", body, StringComparison.Ordinal);
        Assert.Contains("Toggle(\"Show VSWR\", marker.VswrEnabled,", body, StringComparison.Ordinal);
        Assert.DoesNotContain("MenuItemToggleType", body, StringComparison.Ordinal);
        Assert.Contains("Item(\"Set…\"", body, StringComparison.Ordinal);
        Assert.Contains("ShowMarkerSetVswrDialogAsync(h, marker)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatVswr_IsSharedByTheLiveReadoutAndTheMenuHeader()
    {
        // The one place both actually read: HarmonicaReadoutFormatting.FormatVswr. Direct, not
        // source-pinned — the same call the drag readout and the menu header both make.
        Assert.Equal("VSWR: 2.5",  HarmonicaReadoutFormatting.FormatVswr(2.5));
        Assert.Equal("VSWR: 3",    HarmonicaReadoutFormatting.FormatVswr(3.0));
        Assert.Equal("VSWR: 1.33", HarmonicaReadoutFormatting.FormatVswr(4.0 / 3.0));
    }
}
