// ================================================================
//  HarmonicaR5StripRebuildTests.cs — §2 of brief-harmonicarf-r5-the-unmeasured-stage-and-drag-starvation
//
//  ReadoutStripView.SetItems used to `.Clear()` and rebuild ALL FIVE of its Source/Load/MXP/MXE/
//  OperatingPoint columns unconditionally, every published frame — ~70-110 real Avalonia controls on
//  the UI thread, every frame, on a document that publishes constantly. §2's fix applies the SAME
//  build-once/update-in-place shape SetInputs' Settings column already uses (R3C §1), keyed on a
//  per-column SHAPE SIGNATURE (the marker set, or whether an MXP/MXE optimum is present) rather than
//  on the row VALUES, which the per-frame update step writes in place regardless.
//
//  Ui.Tests may not instantiate a live ReadoutStripView (no headless Avalonia Application), so the
//  structural claims here are pinned by source scan, the same convention every other strip test in
//  this project already uses (see HarmonicaR3cStripTests' own header comment).
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaR5StripRebuildTests
{
    private static string Source() =>
        ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml.cs");

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

    // ══ §2.2 — a column rebuilds ONLY when its own shape signature changes ═══════════════════════

    [Fact]
    public void UpdateReadoutColumn_ClearsAndRebuilds_OnlyWhenTheSignatureChanged()
    {
        string src = Source();

        int m = src.IndexOf("private void UpdateReadoutColumn(", StringComparison.Ordinal);
        Assert.True(m >= 0, "Expected to find UpdateReadoutColumn.");
        int mEnd = src.IndexOf("\n    /// <summary>What determines a row's STRUCTURE", m, StringComparison.Ordinal);
        Assert.True(mEnd > m);
        string body = src[m..mEnd];

        // The clear-and-rebuild branch is GATED on the signature (and a defensive count check), never
        // unconditional — the pre-r5 version called host.Children.Clear() on every single call.
        Assert.Contains("if (!_columnSignatures.TryGetValue(column, out var prev) || prev != signature ||",
                        body, StringComparison.Ordinal);
        Assert.Contains("host.Children.Clear();", body, StringComparison.Ordinal);

        // Values are written EVERY call, rebuild or not — the loop below the gate, unconditional.
        Assert.Contains("UpdateColumnRow(row, items[i]", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnSignatures_AreKeyedPerColumn_SoOneColumnsShapeChangeCannotRebuildAnother()
    {
        // §2.2's own "per-column, not whole-strip" rule, as a TYPE rather than a comment: one
        // dictionary keyed by ReadoutColumn, not a single shared string every column would collide on.
        string src = Source();
        Assert.Contains("private readonly Dictionary<ReadoutColumn, string> _columnSignatures = new();",
                        src, StringComparison.Ordinal);
    }

    [Fact]
    public void SetItems_CallsUpdateReadoutColumn_OnceEachForTheNonGeneralColumns()
    {
        string src = Source();
        int m = src.IndexOf("public void SetItems(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    /// <summary>One General-column row", m, StringComparison.Ordinal);
        Assert.True(mEnd > m);
        string body = src[m..mEnd];

        // R-hui-1 — Source and Load merged into ONE TerminationsColumn call.
        foreach (string column in new[] { "OperatingPointColumn", "TerminationsColumn", "MxpColumn", "MxeColumn" })
            Assert.Contains($"UpdateReadoutColumn({column},", body, StringComparison.Ordinal);

        // The General column is explicitly UNCHANGED — still Items.Children.Clear() every call (it
        // carries no editors and is typically 0-1 rows, so it is not where the ~70-110-control cost
        // lived; see SetItems' own doc comment for why it was left alone).
        Assert.Contains("Items.Children.Clear();", body, StringComparison.Ordinal);
    }

    // ══ §2.3 — the mid-edit guard extends to these columns too ═══════════════════════════════════

    [Fact]
    public void UpdateColumnRow_SkipsTheValueSlot_WhileStateIsEditing_SourceScan()
    {
        string src = Source();

        int m = src.IndexOf(
            "private static void UpdateColumnRow(Grid row, HarmonicaReadout item,", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    /// <summary>\n    /// R-h9r2-25", m, StringComparison.Ordinal);
        Assert.True(mEnd > m);
        string body = src[m..mEnd];

        Assert.Contains("SettingsRowMayBeOverwritten(state.IsEditing)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildColumnRowShell_RefusesASecondEditor_WhileOneIsAlreadyOpenOnTheSameRow()
    {
        // R3C's own bug this closes "for free" (§2.3): an open Source/Load inline editor used to be
        // destroyed and reopened as a stale row every published frame, because SetItems rebuilt the
        // whole column underneath it. Build-once removes the destruction; THIS guard is what stops a
        // second double-tap from opening a SECOND editor over the live one in the meantime.
        string src = Source();

        int m = src.IndexOf("private Grid BuildColumnRowShell(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    /// <summary>Writes one row's CURRENT label", m, StringComparison.Ordinal);
        Assert.True(mEnd > m);
        string body = src[m..mEnd];

        Assert.Contains("if (state.OnCommitEdit is not { } commit || !SettingsRowMayBeOverwritten(state.IsEditing)) return;",
                        body, StringComparison.Ordinal);
    }

    // ══ §2.2 — the shape signature is load-bearing: it changes with the marker set ════════════════

    private static string InvokeRowShapeKey(HarmonicaReadout item)
    {
        var type = typeof(CircuitRF.Ui.Views.Harmonica.ReadoutStripView);
        var method = type.GetMethod("RowShapeKey", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [item])!;
    }

    [Fact]
    public void RowShapeKey_DependsOnLabelHeaderComplexAndEditable_NeverOnTheCurrentValue()
    {
        var zRow = new HarmonicaReadout("ZS1", "25 Ω", "tip", ReadoutColumn.Source,
                                        IsComplex: true, Editable: true);
        var zRowNewValue = zRow with { Value = "30 Ω" };   // a re-solved figure, same row identity
        Assert.Equal(InvokeRowShapeKey(zRow), InvokeRowShapeKey(zRowNewValue));

        // A DIFFERENT marker (ZS2 instead of ZS1) is a genuinely different row — the signature must
        // say so, which is what makes "the marker set is load-bearing" true rather than aspirational.
        var zRowOtherMarker = zRow with { Label = "ZS2" };
        Assert.NotEqual(InvokeRowShapeKey(zRow), InvokeRowShapeKey(zRowOtherMarker));

        // A header row (empty Value AND Tooltip) has a different shape from a populated one, even at
        // the same Label — MXP/MXE's own "no optimum" row is Value-populated and NOT header-shaped,
        // while its title row above it IS.
        var header = new HarmonicaReadout("Source", "", "", ReadoutColumn.Source);
        var populated = new HarmonicaReadout("Source", "25 Ω", "tip", ReadoutColumn.Source);
        Assert.NotEqual(InvokeRowShapeKey(header), InvokeRowShapeKey(populated));
    }

    [Fact]
    public void AddingAMarkerBand_ChangesOnlyThatSidesColumnsReadoutLabels()
    {
        // Not a view-level test (Ui.Tests cannot instantiate one) — this pins the DATA-level invariant
        // that makes "per column, not whole-strip" correct in the first place: HarmonicaSolver.
        // BuildReadouts only ever emits Source-column rows for Source markers, so a Source-only
        // structural change cannot touch what UpdateReadoutColumn computes as the Load/OperatingPoint
        // columns' own shape signature. The default document's own marker set (S1, S2, L1, L2, L3 —
        // see HarmonicaViewModel's own constructor note) already claims every band up to K=3 except
        // S3, which is what this adds.
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });

        string[] SourceLabels() => vm.Frame.Readouts.Where(r => r.Column == ReadoutColumn.Source)
                                                     .Select(r => r.Label).ToArray();
        string[] LoadLabels() => vm.Frame.Readouts.Where(r => r.Column == ReadoutColumn.Load)
                                                   .Select(r => r.Label).ToArray();
        string[] OperatingPointLabels() => vm.Frame.Readouts.Where(r => r.Column == ReadoutColumn.OperatingPoint)
                                                             .Select(r => r.Label).ToArray();

        var sourceBefore = SourceLabels();
        var loadBefore    = LoadLabels();
        var opBefore      = OperatingPointLabels();

        vm.AddMarkerBand(TerminationSideKind.Source, 3);
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });

        Assert.Equal(loadBefore, LoadLabels());
        Assert.Equal(opBefore, OperatingPointLabels());
        Assert.NotEqual(sourceBefore, SourceLabels());
        Assert.Contains(SourceLabels(), l => l.Contains("S3", StringComparison.Ordinal));
    }
}
