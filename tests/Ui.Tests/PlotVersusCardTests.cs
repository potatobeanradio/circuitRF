// ================================================================
//  PlotVersusCardTests.cs  —  the "vs X" row on the trace card.
//
//  The point of holding the X side in its own field (rather than
//  folding it into the free-form expression) is that a versus trace
//  keeps its Y-side identity: the group/item combos and the axis-role
//  editor go on working. These tests hold that shut, along with the
//  one-choice picker flow and the typed alias::Cube form.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class PlotVersusCardTests
{
    private static readonly double[] PinVals  = { -10, -5, 0, 5, 10 };
    private static readonly double[] GainVals = { 15.0, 14.9, 14.5, 13.0, 10.0 };
    private static readonly double[] PoutVals = {  5.0,  9.9, 14.5, 18.0, 20.0 };

    private static DataSet MakeDs()
    {
        var pin = new Axis("Pin", PinVals, "dBm");
        var ds  = new DataSet();
        ds.Add("Gain", new DataCube(new[] { pin }, (double[])GainVals.Clone()));
        ds.Add("Pout", new DataCube(new[] { pin }, (double[])PoutVals.Clone()));
        return ds;
    }

    private static DataSet MakeFamilyDs()
    {
        var pin  = new Axis("Pin",    PinVals, "dBm");
        var freq = new Axis("RFfreq", new[] { 2.0e9, 2.4e9 }, "Hz");
        var gain = new double[PinVals.Length * 2];
        var pout = new double[PinVals.Length * 2];
        for (int i = 0; i < PinVals.Length; i++)
            for (int k = 0; k < 2; k++)
            {
                gain[i * 2 + k] = GainVals[i] - k * 0.5;
                pout[i * 2 + k] = PoutVals[i] - k * 1.0;
            }
        var ds = new DataSet();
        ds.Add("Gain", new DataCube(new[] { pin, freq }, gain));
        ds.Add("Pout", new DataCube(new[] { pin, freq }, pout));
        return ds;
    }

    private static async Task<(string path, DataSourceLibraryViewModel lib)> ExportAndLoad(DataSet ds, string tag = "a")
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf_vs_{tag}_{Guid.NewGuid():N}.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);
        await lib.SelectDataSourceAsync(path);
        return (path, lib);
    }

    private static TraceRowViewModel BuildCard(DataSourceLibraryViewModel lib, string sourcePath,
                                               string cubeName, PlotType plotType = PlotType.Rect)
    {
        var trace = new Trace(new SNP(new[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            SourcePath = sourcePath,
            CubeName   = cubeName,
        };
        // Seed the slice the way the picker does, so the card starts from the state the app starts
        // from (a cube-bound trace with a null Slice is not a state the picker can produce).
        var entry = lib.Entries.First(e =>
            string.Equals(e.FilePath, sourcePath, StringComparison.OrdinalIgnoreCase));
        trace.Slice = TraceRowViewModel.BuildDefaultSlice(entry.Data![cubeName]);
        var plot = new Plot(plotType, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
        inspector.RebuildAndNotify();
        return inspector.Traces[0];
    }

    // ── 1. The picker flow: one choice ───────────────────────────────────────

    [Fact]
    public async Task TickingVsX_PicksAnXQuantity_AndWritesTheSpec()
    {
        var (path, lib) = await ExportAndLoad(MakeDs());
        try
        {
            var card  = BuildCard(lib, path, "Gain");
            var trace = card.Trace;

            Assert.True(card.ShowVersusRow);
            Assert.False(card.VersusEnabled);

            card.VersusEnabled = true;

            // Defaults to a quantity that is NOT the Y quantity — Gain vs Gain is never meant.
            Assert.True(trace.IsVersus);
            Assert.Equal("Pout", trace.XSpec);
            Assert.Equal("Gain vs Pout", trace.CubeShorthand);
            Assert.Equal("Gain vs Pout", card.SpecShorthand);
            Assert.Null(trace.ExpressionError);
            Assert.Equal(PoutVals, trace.CubeXValues!.ToArray());

            // Unticking restores the swept axis.
            card.VersusEnabled = false;
            Assert.False(trace.IsVersus);
            Assert.Equal("Pin", trace.CubeXAxisName);
            Assert.Equal(PinVals, trace.CubeXValues!.ToArray());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task VersusTrace_KeepsItsYIdentity_SoTheAxisEditorStaysAlive()
    {
        var (path, lib) = await ExportAndLoad(MakeFamilyDs(), "fam");
        try
        {
            var card  = BuildCard(lib, path, "Gain");
            var trace = card.Trace;

            // Make it a family over RFfreq, then plot it against Pout.
            var freqRow = card.AxisRoles.First(r => r.AxisName == "RFfreq");
            freqRow.IsFamily = true;
            card.VersusEnabled = true;

            Assert.Equal("Gain[:, ~] vs Pout", trace.CubeShorthand);

            // The Y-side controls are all still populated — the reason XSpec is its own field.
            Assert.Equal("Gain", trace.CubeName);
            Assert.NotNull(trace.Slice);
            Assert.Equal(2, card.AxisRoles.Count);
            Assert.True(card.AxisRoles.First(r => r.AxisName == "RFfreq").IsFamily);
            Assert.True(card.AxisRoles.First(r => r.AxisName == "Pin").IsX);

            // And the X side followed the family without being told twice.
            Assert.Equal(2, trace.FamilyCurves.Count);
            Assert.All(trace.FamilyCurves, fc => Assert.NotNull(fc.RawX));
            Assert.Contains("RFfreq = family", card.XRoleSummary);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task XPickerOffersQuantities_AndSwitchingItRebindsTheTrace()
    {
        var (path, lib) = await ExportAndLoad(MakeDs());
        try
        {
            var card = BuildCard(lib, path, "Gain");
            card.VersusEnabled = true;

            Assert.Contains("Signals", card.XGroups);
            Assert.Contains(card.XSignals, s => s.CubeName == "Pout");
            Assert.Contains(card.XSignals, s => s.CubeName == "Gain");

            card.SelectedXSignal = card.XSignals.First(s => s.CubeName == "Gain");
            Assert.Equal("Gain", card.Trace.XSpec);
            Assert.Equal("Gain vs Gain", card.Trace.CubeShorthand);
        }
        finally { File.Delete(path); }
    }

    // ── 2. Typed specs ───────────────────────────────────────────────────────

    [Fact]
    public async Task TypedVersusSpec_BindsBothSides_AndSyncsTheCard()
    {
        var (path, lib) = await ExportAndLoad(MakeDs());
        try
        {
            var card = BuildCard(lib, path, "Gain");
            card.CommitSpec("Gain vs Pout");

            Assert.Equal("Gain", card.Trace.CubeName);      // Y identity survives the split
            Assert.Equal("Pout", card.Trace.XSpec);
            Assert.True(card.VersusEnabled);                 // the tick follows the text
            Assert.Equal("Pout", card.SelectedXSignal?.CubeName);
            Assert.Null(card.Trace.ExpressionError);
            Assert.Equal(PoutVals, card.Trace.CubeXValues!.ToArray());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TypedVersusSpec_WithTwoSeparators_IsReportedNotGuessed()
    {
        var (path, lib) = await ExportAndLoad(MakeDs());
        try
        {
            var card = BuildCard(lib, path, "Gain");
            card.CommitSpec("Gain vs Pout vs Pin");

            Assert.True(card.HasSpecError);
            Assert.Contains("Only one", card.SpecError);
            Assert.Empty(card.Trace.Points);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RemovingVsFromTheText_ClearsTheXBinding()
    {
        var (path, lib) = await ExportAndLoad(MakeDs());
        try
        {
            var card = BuildCard(lib, path, "Gain");
            card.CommitSpec("Gain vs Pout");
            Assert.True(card.Trace.IsVersus);

            card.CommitSpec("Gain");
            Assert.False(card.Trace.IsVersus);
            Assert.False(card.VersusEnabled);
            Assert.Equal("Pin", card.Trace.CubeXAxisName);
        }
        finally { File.Delete(path); }
    }

    // ── 3. Cross-source X ────────────────────────────────────────────────────

    [Fact]
    public async Task CrossSourceX_TypedByAlias_ResolvesAgainstTheOtherFile()
    {
        var simDs = MakeDs();

        // A second file whose Pout differs, standing in for a measured run.
        var measured = new double[] { 4.0, 9.0, 14.0, 17.5, 19.5 };
        var measDs = new DataSet();
        measDs.Add("Pout", new DataCube(new[] { new Axis("Pin", PinVals, "dBm") }, measured));

        var (simPath, lib) = await ExportAndLoad(simDs, "sim");
        string measPath = Path.Combine(Path.GetTempPath(), $"crf_vs_meas_{Guid.NewGuid():N}.npy");
        try
        {
            DataSetExporter.Export(measDs, measPath, ExportFormat.Npy);
            await lib.LoadFileAsync(measPath);

            var card = BuildCard(lib, simPath, "Gain");
            string alias = Path.GetFileNameWithoutExtension(measPath);
            card.CommitSpec($"Gain vs {alias}::Pout");

            Assert.Null(card.Trace.ExpressionError);
            Assert.Equal("Pout", card.Trace.XSpec);
            Assert.Equal(measPath, card.Trace.XSourcePath);
            Assert.Equal(measured, card.Trace.CubeXValues!.ToArray());

            // The alias round-trips into the displayed spec, so the trace says where X came from.
            Assert.Contains("::Pout", card.SpecShorthand);
        }
        finally { File.Delete(simPath); if (File.Exists(measPath)) File.Delete(measPath); }
    }

    [Fact]
    public async Task CrossSourceX_UnknownAlias_IsReported()
    {
        var (path, lib) = await ExportAndLoad(MakeDs());
        try
        {
            var card = BuildCard(lib, path, "Gain");
            card.CommitSpec("Gain vs nosuchfile::Pout");

            Assert.True(card.HasSpecError);
            Assert.Contains("nosuchfile", card.SpecError);
        }
        finally { File.Delete(path); }
    }

    // ── 4. The X side's own transform, and its axis rows ─────────────────────

    private static DataSet MakeComplexXDs()
    {
        // Gp_dB[Pin, RFfreq] (real) beside HB1.V[Pin, RFfreq, node, harmonic] (complex) — the shape
        // that made the owner reach for the transform combo and move the Y side instead.
        var pin  = new Axis("Pin",      PinVals, "dBm");
        var freq = new Axis("RFfreq",   new[] { 2.0e9, 2.4e9 }, "Hz");
        var node = new Axis("node",     new[] { 0.0, 1.0 }, "V", new[] { "Vin", "Vout" });
        var harm = new Axis("harmonic", new[] { 0.0, 1.0, 2.0 }, "");

        var gp = new double[PinVals.Length * 2];
        for (int i = 0; i < gp.Length; i++) gp[i] = 10.0 + i * 0.1;

        var v = new System.Numerics.Complex[PinVals.Length * 2 * 2 * 3];
        for (int i = 0; i < v.Length; i++) v[i] = new System.Numerics.Complex(0.5 + i * 0.01, 0.25);

        // A second REAL measurement beside Gp_dB — the sibling a sensible default X should reach for
        // (the owner's own run has fifteen of them).
        var pout = new double[PinVals.Length * 2];
        for (int i = 0; i < pout.Length; i++) pout[i] = 20.0 + i * 0.5;

        var ds = new DataSet();
        ds.Add("Gp_dB",    new DataCube(new[] { pin, freq }, gp));
        ds.Add("Pout_dBm", new DataCube(new[] { pin, freq }, pout));
        ds.AddToGroup("HB1", "V", new DataCube(new[] { pin, freq, node, harm }, v));
        return ds;
    }

    [Fact]
    public async Task ComplexX_GetsItsOwnTransform_AndTheYTransformIsUntouched()
    {
        var (path, lib) = await ExportAndLoad(MakeComplexXDs(), "cplx");
        try
        {
            var card  = BuildCard(lib, path, "Gp_dB");
            var trace = card.Trace;

            card.VersusEnabled = true;
            card.SelectedXGroup  = "HB1";
            card.SelectedXSignal = card.XSignals.First(s => s.CubeName == "HB1.V");

            // A complex X lands on mag() by itself — the only choice that yields a real axis — and
            // None/Conj are not offered as usable options.
            Assert.Equal(CubeTransform.Mag, card.SelectedXTransformItem!.Transform);
            Assert.StartsWith("mag(HB1.V[", trace.XSpec);
            Assert.False(card.XTransformItems.First(i => i.Transform == CubeTransform.None).Enabled);
            Assert.DoesNotContain(card.XTransformItems, i => i.Transform == CubeTransform.Conj);

            // The Y side's own transform is NOT what moved.
            Assert.Equal(CubeTransform.None, trace.Transform);

            // Changing the X transform rewrites the X half only.
            card.SelectedXTransformItem = card.XTransformItems.First(i => i.Transform == CubeTransform.dB20);
            Assert.StartsWith("dB20(HB1.V[", trace.XSpec);
            Assert.Equal(CubeTransform.None, trace.Transform);
            Assert.Equal("Gp_dB", trace.CubeName);
            Assert.Null(trace.ExpressionError);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SharedAxesAreSTATED_AndOnlyForeignAxesGetRows()
    {
        var (path, lib) = await ExportAndLoad(MakeComplexXDs(), "rows");
        try
        {
            var card  = BuildCard(lib, path, "Gp_dB");
            card.AxisRoles.First(r => r.AxisName == "RFfreq").IsFamily = true;

            card.VersusEnabled   = true;
            card.SelectedXGroup  = "HB1";
            card.SelectedXSignal = card.XSignals.First(s => s.CubeName == "HB1.V");

            // Rows ONLY for the X quantity's own axes — Pin and RFfreq belong to the trace and are
            // edited by the rows above; duplicating them here put two controls on one state.
            Assert.Equal(new[] { "node", "harmonic" }, card.XAxisPins.Select(r => r.AxisName));

            // What the user needs to know about the shared axes is SAID, including the family.
            Assert.Contains("Pin = X",           card.XRoleSummary);
            Assert.Contains("RFfreq = family",   card.XRoleSummary);

            // Setting a foreign axis writes it into the X spec (as a quoted label when it has one).
            card.XAxisPins.First(r => r.AxisName == "node").PinIndex = 1;
            Assert.Contains("\"Vout\"", card.Trace.XSpec);
            Assert.Equal(2, card.Trace.FamilyCurves.Count);
            Assert.Null(card.Trace.ExpressionError);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task AFailedVersusBinding_MarksTheTraceInvalidOnThePlot()
    {
        // The owner's report: "mag(Gp_dB[~, :]) vs HB1.V[~, :, \"Vout\", 2]" rendered nothing and the
        // Y label still looked perfectly valid — the complex-X refusal reached the card's spec box
        // and nowhere else.
        var (path, lib) = await ExportAndLoad(MakeComplexXDs(), "inv");
        try
        {
            var card = BuildCard(lib, path, "Gp_dB");
            card.CommitSpec("mag(Gp_dB[~, :]) vs HB1.V[~, :, \"Vout\", 2]");

            Assert.NotNull(card.Trace.ExpressionError);
            Assert.Contains("must be real", card.Trace.ExpressionError);
            Assert.Empty(card.Trace.Points);

            var labels = TraceLabeler.ComputeMinimalLabels(new[] { card.Trace });
            Assert.Contains("<invalid>", labels[0]);
            Assert.Contains("<invalid>", card.Trace.RectYLabel(labels[0], dimensionMismatch: false));

            // …and the named remedy clears it.
            card.CommitSpec("mag(Gp_dB[~, :]) vs mag(HB1.V[~, :, \"Vout\", 2])");
            Assert.Null(card.Trace.ExpressionError);
            Assert.DoesNotContain("<invalid>",
                TraceLabeler.ComputeMinimalLabels(new[] { card.Trace })[0]);
        }
        finally { File.Delete(path); }
    }

    // ── 5. Round-3 card fixes ────────────────────────────────────────────────

    [Fact]
    public async Task ChangingTheXGroup_DoesNotBlankTheSourceCombo()
    {
        // Owner: picking "Measurements" in the vs-X group combo blanked the SOURCE combo until an
        // item was picked. Cause: the source list was cleared and rebuilt from new objects on the
        // refresh that every edit triggers, and clearing a bound ItemsSource drops the selection.
        var simDs = MakeComplexXDs();
        var (simPath, lib) = await ExportAndLoad(simDs, "grp");
        string otherPath = Path.Combine(Path.GetTempPath(), $"crf_vs_other_{Guid.NewGuid():N}.npy");
        try
        {
            DataSetExporter.Export(MakeDs(), otherPath, ExportFormat.Npy);
            await lib.LoadFileAsync(otherPath);            // 2 sources → the source combo is shown

            var card = BuildCard(lib, simPath, "Gp_dB");
            card.VersusEnabled = true;

            Assert.True(card.XSourceSelectorVisible);
            var sourceBefore = card.SelectedXSourceItem;
            Assert.NotNull(sourceBefore);

            var itemsBefore = card.XSourceEntries.ToList();
            card.SelectedXGroup = "HB1";                   // the reported gesture

            Assert.NotNull(card.SelectedXSourceItem);      // ← was null
            Assert.Same(sourceBefore, card.SelectedXSourceItem);
            Assert.Equal(itemsBefore, card.XSourceEntries);  // list untouched, not rebuilt
        }
        finally { File.Delete(simPath); if (File.Exists(otherPath)) File.Delete(otherPath); }
    }

    [Fact]
    public async Task SwitchingXFromComplexToReal_DropsTheTransform()
    {
        var (path, lib) = await ExportAndLoad(MakeComplexXDs(), "xform");
        try
        {
            var card = BuildCard(lib, path, "Gp_dB");
            card.VersusEnabled   = true;
            card.SelectedXGroup  = "HB1";
            card.SelectedXSignal = card.XSignals.First(s => s.CubeName == "HB1.V");
            Assert.Equal(CubeTransform.Mag, card.SelectedXTransformItem!.Transform);

            // Back to a REAL quantity: mag() is meaningless there and must not be carried over.
            card.SelectedXGroup  = "Signals";
            card.SelectedXSignal = card.XSignals.First(s => s.CubeName == "Gp_dB");

            Assert.Equal(CubeTransform.None, card.SelectedXTransformItem!.Transform);
            Assert.Equal("Gp_dB", card.Trace.XSpec);        // no mag() wrapper
            Assert.True(card.XTransformItems.First(i => i.Transform == CubeTransform.None).Enabled);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TheSummaryTracksTheYSidesRoles_IncludingAPinnedValue()
    {
        // The shared axes have no controls here on purpose; the summary is what carries them, so it
        // has to stay true as the Y side changes — and name the VALUE of a pinned one, since that is
        // the frequency the X data is read at.
        var (path, lib) = await ExportAndLoad(MakeComplexXDs(), "sum");
        try
        {
            var card = BuildCard(lib, path, "Gp_dB");
            card.VersusEnabled   = true;
            card.SelectedXGroup  = "HB1";
            card.SelectedXSignal = card.XSignals.First(s => s.CubeName == "HB1.V");

            Assert.Contains("Pin = X", card.XRoleSummary);
            Assert.Contains("RFfreq = fixed at", card.XRoleSummary);

            // Make RFfreq the family on the Y side — the summary follows, and so does the X spec.
            card.AxisRoles.First(r => r.AxisName == "RFfreq").IsFamily = true;
            Assert.Contains("RFfreq = family", card.XRoleSummary);
            Assert.Contains("[:, ~,", card.Trace.XSpec);
            Assert.Equal(2, card.Trace.FamilyCurves.Count);

            // Pin it again at the SECOND frequency: the summary names that value, not an index.
            var freqRow = card.AxisRoles.First(r => r.AxisName == "RFfreq");
            freqRow.IsFamily = false;
            freqRow.PinIndex = 1;
            Assert.Contains($"RFfreq = fixed at {freqRow.PinOptions[1]}", card.XRoleSummary);
            Assert.Contains("[:, 1,", card.Trace.XSpec);
            Assert.Null(card.Trace.ExpressionError);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task AForeignAxisValue_IsTheXSidesOwnToSet()
    {
        // The X quantity's own axes ARE the X side's: picking HB1.V as X still needs a node and a
        // harmonic, and neither exists on the Y side to inherit from.
        var (path, lib) = await ExportAndLoad(MakeComplexXDs(), "pin");
        try
        {
            var card = BuildCard(lib, path, "Gp_dB");
            card.VersusEnabled   = true;
            card.SelectedXGroup  = "HB1";
            card.SelectedXSignal = card.XSignals.First(s => s.CubeName == "HB1.V");

            var harmRow = card.XAxisPins.First(r => r.AxisName == "harmonic");
            Assert.Equal(3, harmRow.PinOptions.Count);

            harmRow.PinIndex = 2;                                   // the 2nd harmonic
            Assert.Contains(", 2]", card.Trace.XSpec);
            Assert.Null(card.Trace.ExpressionError);

            // And the Y side is untouched by it.
            Assert.Equal("Gp_dB", card.Trace.CubeName);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task UntickingAndReTicking_LeavesThePickerPopulated()
    {
        // Owner: "when I check the vs X checkbox, the data source combobox blanks (no options)".
        // The visible collections were cleared on untick while the content-diffing CACHE behind them
        // was left populated, so the next rebuild concluded "nothing changed" and refilled nothing.
        var (path, lib) = await ExportAndLoad(MakeComplexXDs(), "recheck");
        try
        {
            var card = BuildCard(lib, path, "Gp_dB");

            card.VersusEnabled = true;
            var groupsFirst  = card.XGroups.ToList();
            var signalsFirst = card.XSignals.Count;
            Assert.NotEmpty(groupsFirst);
            Assert.True(signalsFirst > 0);

            card.VersusEnabled = false;
            card.VersusEnabled = true;                       // ← the reported gesture

            Assert.Equal(groupsFirst, card.XGroups);
            Assert.Equal(signalsFirst, card.XSignals.Count);
            Assert.NotNull(card.SelectedXSignal);
            Assert.NotNull(card.Trace.XSpec);
            Assert.NotEmpty(card.XTransformItems);
            Assert.Null(card.Trace.ExpressionError);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TheDefaultXQuantity_IsASiblingOfTheYQuantity_NotARawComplexCube()
    {
        // On the owner's own run the first cube in the file is a complex HB voltage, so "first cube
        // that isn't Y" opened the feature on an X that could not be plotted without a transform.
        // A measurement beside the measurement being plotted is what PA work actually wants.
        var (path, lib) = await ExportAndLoad(MakeComplexXDs(), "dflt");
        try
        {
            var card = BuildCard(lib, path, "Gp_dB");
            card.VersusEnabled = true;

            Assert.Equal("Signals", card.SelectedXGroup);          // Gp_dB's own group, not HB1
            Assert.Equal("Pout_dBm", card.SelectedXSignal!.CubeName);
            Assert.Equal(CubeTransform.None, card.SelectedXTransformItem!.Transform);
            Assert.DoesNotContain("HB1", card.Trace.XSpec);
            Assert.Null(card.Trace.ExpressionError);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task AFreshlyBuiltCard_OverAVersusTrace_ComesUpTicked()
    {
        // Owner: "if I copy and paste a plot with a plot vs x trace, the pasted plot's trace has its
        // vs X disabled." The paste round-trip is fine — the TRACE keeps its X binding and goes on
        // plotting against it. What was wrong is the CARD: its vs state is synced by
        // RefreshDescription, which the constructor never calls (the same hole
        // TraceCardConstructionInitTests documents for the network-metric row), so a card built over
        // an already-versus trace came up clear and empty.
        var (path, lib) = await ExportAndLoad(MakeDs(), "fresh");
        try
        {
            var trace = new Trace(new SNP(new[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
            {
                SourcePath = path,
                CubeName   = "Gain",
                Slice      = TraceRowViewModel.BuildDefaultSlice(lib.Entries.First().Data!["Gain"]),
                XSpec      = "Pout",
            };
            trace.Expression = trace.CubeShorthand;

            var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
            plot.Traces.Add(trace);
            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);

            // Construct the card directly — no prior RefreshDescription, exactly as paste does.
            var card = new TraceRowViewModel(trace, inspector);

            Assert.True(card.VersusEnabled);
            Assert.Equal("Pout", card.SelectedXSignal?.CubeName);
            Assert.NotEmpty(card.XGroups);
            Assert.NotEmpty(card.XSignals);
            Assert.Contains("Pin = X", card.XRoleSummary);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CopyPastingAPlot_KeepsTheVersusBinding()
    {
        var (path, lib) = await ExportAndLoad(MakeDs(), "paste");
        try
        {
            var ddvm = new DataDisplayViewModel(lib, addEmptyPlot: false);
            var container = ddvm.AddPlot(PlotType.Rect);
            var trace = new Trace(new SNP(new[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db)
            {
                SourcePath = path,
                CubeName   = "Gain",
                Slice      = TraceRowViewModel.BuildDefaultSlice(lib.Entries.First().Data!["Gain"]),
                XSpec      = "Pout",
            };
            trace.Expression = trace.CubeShorthand;
            container.PlotVM.Plot.Traces.Add(trace);

            // The clipboard round-trip the Copy/Paste menu items perform.
            string json = PlotExporter.BuildContainersJson(new[] { container });
            var cfg = System.Text.Json.JsonSerializer.Deserialize<DataDisplayConfig>(
                json, DataDisplayViewModel.JsonOpts)!;
            var pastedContainers = await ddvm.PasteFromConfigAsync(cfg);

            var pasted = pastedContainers[0].PlotVM.Plot.Traces[0];
            Assert.True(pasted.IsVersus);
            Assert.Equal("Pout", pasted.XSpec);
            Assert.Equal("Gain vs Pout", pasted.Expression);
            Assert.Null(pasted.ExpressionError);
            Assert.Equal(PoutVals, pasted.CubeXValues!.ToArray());   // X really is Pout
        }
        finally { File.Delete(path); }
    }

    // ── 6. Plot-type gate ────────────────────────────────────────────────────

    [Fact]
    public async Task VsRowIsHiddenOnSmith()
    {
        var (path, lib) = await ExportAndLoad(MakeDs());
        try
        {
            var card = BuildCard(lib, path, "Gain", PlotType.Smith);
            Assert.False(card.ShowVersusRow);
        }
        finally { File.Delete(path); }
    }
}
