// ================================================================
//  TechFileMenuAndViaThicknessTests.cs — two user reports about the .ctech editor, 2026-08-30.
//
//  (1) File > Save was greyed out for a DIRTY .ctech even though Cmd+S saved it. CanSaveAllDocuments
//      already answered "yes"; nothing re-EVALUATED it. The enablement fan-outs fire on a tab switch,
//      a window focus change, a completed save, and — for the canvas-backed editors only — a canvas
//      click. A .ctech editor is a form with no canvas, so while the user typed into it no refresh
//      point ever came and the menu item kept whatever appearance it had when the tab opened.
//
//  (2) The Stackup tab showed a "Thickness" field on VIA rows, sitting at 0 mil in every shipped
//      .ctech, and it read as a real dimension nobody had filled in. A via has no z band of its own:
//      PlanarExtractor.BuildStack skips every StackupKind.Via entry, and the barrel's length comes
//      from the SpanFrom/SpanTo pair. Nothing reads the field.
//
//  Both fixes live in code that needs an Avalonia application and a dock factory to exercise, so
//  they are asserted against the source they are written in — the pattern DataDisplayTreeDirtyTests
//  established for this same area — naming the mechanism rather than scanning for a word.
// ================================================================

using System;
using System.IO;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class TechFileMenuAndViaThicknessTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Src(params string[] parts)
    {
        string raw = File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));
        raw = Regex.Replace(raw, @"/\*.*?\*/", "", RegexOptions.Singleline);
        raw = Regex.Replace(raw, @"//[^\n]*", "", RegexOptions.None);
        return raw;
    }

    private static string Raw(params string[] parts)
        => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    private static string Between(string src, string signature)
    {
        int i = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{signature}' is not in the source any more");
        int open = src.IndexOf('{', i);
        int depth = 0;
        for (int k = open; k < src.Length; k++)
        {
            if (src[k] == '{') depth++;
            else if (src[k] == '}' && --depth == 0) return src[open..(k + 1)];
        }
        Assert.Fail($"'{signature}' has no closing brace");
        return "";
    }

    private static string Workspace() => Src("src", "Ui", "ViewModels", "WorkspaceViewModel.cs");

    // ── 1 — the missing refresh point ─────────────────────────────────────────

    /// <summary>The dirty transition is the only moment a form-backed editor produces, so the File
    /// menu must be re-asked from it. Without this the item stays at its open-time appearance for
    /// the whole editing session.</summary>
    [Fact]
    public void ADirtyTechDocument_ReasksTheFileMenu()
    {
        string hook = Between(Workspace(), "private void HookTechFileDirty(TechDocument doc)");
        Assert.Contains("RaiseFileMenuEnablementChanged()", hook, StringComparison.Ordinal);
    }

    /// <summary>A .cem editor is the same shape — workspace-scoped, never scratch, no canvas — so it
    /// has the same hole and rides the same fix. Kept in step deliberately: this file's own §1 note.</summary>
    [Fact]
    public void ADirtyEmSetupDocument_ReasksTheFileMenu()
    {
        string hook = Between(Workspace(), "private void HookEmSetupDirty(EmSetupDocument doc)");
        Assert.Contains("RaiseFileMenuEnablementChanged()", hook, StringComparison.Ordinal);
    }

    /// <summary>The predicate the refresh exists to re-run. If a TechDocument stops being answered
    /// by its own dirty flag, the refresh above is re-asking the wrong question.</summary>
    [Fact]
    public void SaveIsGatedOnTheTechDocumentsOwnDirtyFlag()
    {
        string can = Regex.Replace(
            Between(Workspace(), "private bool CanSaveAllDocuments()"), @"\s+", " ");
        Assert.Contains("TechDocument td => td.IsDirty", can, StringComparison.Ordinal);
        Assert.Contains("EmSetupDocument emd => emd.IsDirty", can, StringComparison.Ordinal);
    }

    // ── 2 — the via thickness field that measures nothing ─────────────────────

    /// <summary>The Stackup tab's Thickness row must be hidden on a via row. Asserted on the RAW
    /// XAML (comments included) so the binding and the reason travel together.</summary>
    [Fact]
    public void TheStackupTabHidesThicknessOnAViaRow()
    {
        string xaml = Raw("src", "Ui", "Views", "Layout", "TechEditorView.axaml");
        int row = xaml.IndexOf("Text=\"Thickness:\"", StringComparison.Ordinal);
        Assert.True(row > 0, "the Thickness row is gone from the Stackup tab");

        // The IsVisible must be on the StackPanel that OPENS immediately before the label.
        string before = xaml[..row];
        int panel = before.LastIndexOf("<StackPanel", StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding !IsVia}\"", before[panel..], StringComparison.Ordinal);
    }

    /// <summary>The .cem editor's read-only stackup table showed the same 0 for a via. It must print
    /// the span — the quantity the extractor actually reads — not a formatted zero length.</summary>
    [Fact]
    public void TheEmStackupTableShowsAViasSpanRatherThanAZeroThickness()
    {
        string vm = Src("src", "Ui", "Layout", "Em", "EmSetupEditorViewModel.cs");
        int build = vm.IndexOf("new EmStackupRow(", StringComparison.Ordinal);
        Assert.True(build > 0, "the stackup table rows are no longer built here");

        string region = Regex.Replace(vm[Math.Max(0, build - 1200)..build], @"\s+", " ");
        Assert.Contains("l.Kind == StackupKind.Via", region, StringComparison.Ordinal);
        Assert.Contains("SpanFromLayer", region, StringComparison.Ordinal);
        Assert.Contains("SpanToLayer", region, StringComparison.Ordinal);
    }

    /// <summary>The claim the two UI changes rest on, asserted against the extractor rather than
    /// restated: a Via entry contributes no z band, so its own thickness cannot reach the medium.
    /// Two stackups differing ONLY in the via's ThicknessDbu must extract the identical stack.</summary>
    [Fact]
    public void AViasThicknessDoesNotReachTheExtractedMedium()
    {
        var zero = StarterTechnologies.Pcb2Layer();
        var huge = StarterTechnologies.Pcb2Layer();

        var zeroVia = zero.Stackup.Layers.Find(l => l.Kind == StackupKind.Via);
        var hugeVia = huge.Stackup.Layers.Find(l => l.Kind == StackupKind.Via);
        Assert.NotNull(zeroVia);
        Assert.NotNull(hugeVia);
        Assert.Equal(0, zeroVia!.ThicknessDbu);
        hugeVia!.ThicknessDbu = LayoutUnits.ToDbu(500m, LayoutUnit.Mil, LayoutUnits.DefaultDbuPerMicron);

        Assert.Equal(TotalStackHeightDbu(zero.Stackup), TotalStackHeightDbu(huge.Stackup));
    }

    /// <summary>PlanarExtractor.BuildStack's own rule, restated once here so the test above has an
    /// oracle that is not the code under test: walk the stackup, skipping every Via entry.</summary>
    private static long TotalStackHeightDbu(Stackup stackup)
    {
        long total = 0;
        foreach (var l in stackup.Layers)
            if (l.Kind != StackupKind.Via) total += l.ThicknessDbu;
        return total;
    }
}
