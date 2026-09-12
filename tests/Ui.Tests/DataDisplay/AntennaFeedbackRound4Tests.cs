// ================================================================
//  AntennaFeedbackRound4Tests.cs — the antenna-display feedback of
//  2026-09-11, round four: the POLAR plot was offering a long list
//  of far-field quantities it cannot draw.
//
//  It is the same defect round 3 fixed on the 3D surface, one plot
//  kind over. A pattern plot's compass is the trace's own swept
//  ANGLE, so a cube that is a function of frequency and port — the
//  directivity, the gains, the efficiencies, TRP, peak EIRP, most of
//  ANT-5's registry — has no cut in it and resolves to "<invalid>".
//
//  §1 is the RULE, over a real solve's own group. §2 is the picker
//  itself, end to end against a loaded file. §3 is the part that
//  makes the rule worth having: what the picker greys is exactly what
//  the PICTURE refuses, measured by resolving every cube on every one
//  of its axes rather than by restating the predicate.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.DataDisplay;

public sealed class AntennaFeedbackRound4Tests(ITestOutputHelper output)
{
    private static string Group => CircuitRF.Engine.Mom.PlanarFarField.Group;

    /// <summary>The far-field cubes of the fixture, bare name and cube, in a stable order and
    /// without the internal ones.</summary>
    private static IEnumerable<KeyValuePair<string, DataCube>> FarFieldCubes() =>
        PatternFixture.Data.CubesIn(Group)
            .Where(c => !c.Key.StartsWith("__", StringComparison.Ordinal))
            .OrderBy(c => c.Key, StringComparer.Ordinal);

    /// <summary>Every far-field cube carrying an axis a compass can be, by name — derived from the
    /// fixture through the resolve's OWN finder rather than listed by hand, for the reason round 3
    /// gives: a hand list is a second opinion about which quantities are directional, and the point
    /// of the rule is that there is only one.</summary>
    private static string[] DirectionalCubes() =>
    [
        .. FarFieldCubes()
            .Where(c => PolarPatternAngle.TryFindAngleAxis(c.Value, out _))
            .Select(c => c.Key).Order()
    ];

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §1 — the rule
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The gate is the resolve's own angle test, and it is asked of ONE axis rather than two.</b>
    /// That is the whole difference from the surface: a cut is a single plane, so a cube carrying
    /// only θ is drawable here and is not drawable as a surface.
    /// </summary>
    [Fact]
    public void OnlyTheDirectionalQuantitiesMayBeDrawnAsAPolarPattern()
    {
        var drawable = new List<string>();
        int greyed = 0;

        foreach (var (name, cube) in FarFieldCubes())
        {
            string? reason = PolarPatternAngle.DisabledReasonOnPattern(cube);
            Assert.Equal(PolarPatternAngle.TryFindAngleAxis(cube, out _), reason is null);
            if (reason is null) drawable.Add(name); else greyed++;
        }

        output.WriteLine($"drawable as a polar cut: {string.Join(", ", drawable.Order())}");
        output.WriteLine($"greyed: {greyed} rows");

        // The named ones are why the rule is not "everything in the farfield group": a cut of U and
        // of the Ludwig-3 pair is exactly the picture this plot exists for, and a cut of TRP is not
        // a thing.
        Assert.Contains("U",      drawable);
        Assert.Contains("Etheta", drawable);
        Assert.True(greyed > drawable.Count,
                    "most of a far-field registry is per-frequency, and that is what is being greyed");

        // An item with no cube to test — a derived network metric, a WSProbe quantity — answers with
        // the same sentence from the same place rather than with a second one.
        Assert.Equal(PolarPatternAngle.NotOnAPatternRefusal,
                     PolarPatternAngle.DisabledReasonOnPattern(null));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §2 — the picker itself
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The real trace card, against a real loaded file.</b> Three plots: the dB-radial polar,
    /// which greys everything that is not a function of direction; the LINEAR polar, which is a
    /// locus in the complex plane and greys by KIND instead (its own pre-existing rule, untouched);
    /// and a rectangular plot, which takes the lot.
    ///
    /// <para>Every greyed row carries a REASON, which is this repository's standing rule: a greyed
    /// row with nothing to read cannot be told apart from a broken one, and a row that VANISHES
    /// cannot be told apart from a quantity the run never published — which is the misreading that
    /// produced the first of these reports.</para>
    /// </summary>
    [Fact]
    public async Task ThePickerGreysTheQuantitiesAPolarPatternPlotCannotDraw()
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf_ff4_{Guid.NewGuid():N}.npy");
        try
        {
            RfCore.Export.DataSetExporter.Export(PatternFixture.Data, path,
                                                 RfCore.Export.ExportFormat.Npy);
            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(path);
            await lib.SelectDataSourceAsync(path);

            foreach (var (type, radial) in new[]
                     {
                         (PlotType.Polar, PolarRadialMode.Db),
                         (PlotType.Polar, PolarRadialMode.Linear),
                         (PlotType.Rect,  PolarRadialMode.Linear),
                     })
            {
                var plot = new Plot(type, FreqUnit.GHz) { PolarRadial = radial };
                var row  = new TraceRowViewModel(new Trace(new SNP([PatternFixture.FHz], 2),
                                                           MatrixType.S, 0, 0, DependentVarFormat.Db),
                                                 new PlotInspectorViewModel(plot, () => { }, lib));

                string? group = row.AvailableGroups.FirstOrDefault(
                    g => g.Contains(Group, StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(group);
                row.SelectedGroup = group;

                var rows = row.AvailableSignals.ToList();
                Assert.NotEmpty(rows);

                var on  = rows.Where(i =>  i.IsEnabled).Select(i => i.Label).Order().ToArray();
                var off = rows.Where(i => !i.IsEnabled).Select(i => i.Label).ToList();
                output.WriteLine($"{type}/{radial}: enabled [{string.Join(", ", on)}]");
                output.WriteLine($"{type}/{radial}: greyed  {off.Count} rows");

                foreach (var i in rows.Where(i => !i.IsEnabled))
                    Assert.False(string.IsNullOrWhiteSpace(i.DisabledReason),
                                 $"{i.Label} greyed with no reason");

                if (type == PlotType.Rect)
                {
                    Assert.Empty(off);                      // a rect plot draws every one of them
                }
                else if (radial == PolarRadialMode.Db)
                {
                    Assert.NotEmpty(off);
                    Assert.Equal(DirectionalCubes(), on);
                    // The sentence a reader gets is the pattern one, not the complex-plane one.
                    foreach (var i in rows.Where(i => !i.IsEnabled))
                        Assert.Equal(PolarPatternAngle.NotOnAPatternRefusal, i.DisabledReason);
                }
                else
                {
                    // A LINEAR polar plot is a locus: the gate is the cube's KIND, and it is the one
                    // that was already there. Only the complex cubes survive it, and the directional
                    // real ones — U, the Ludwig-3 pair — are greyed HERE and enabled in dB mode.
                    Assert.All(rows.Where(i => i.IsEnabled),
                               i => Assert.Equal(DataKind.Complex,
                                                 PatternFixture.Data[$"{Group}.{i.Label}"].DataKind));
                    Assert.Contains("U", off);
                }
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>Switching the radial mode is what changes the list, so the inspector rebuilds every
    /// row's picker when it moves — a card left showing the other mode's rows is the same defect
    /// one step later.</summary>
    [Fact]
    public async Task MovingTheRadialModeRebuildsTheList()
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf_ff4m_{Guid.NewGuid():N}.npy");
        try
        {
            RfCore.Export.DataSetExporter.Export(PatternFixture.Data, path,
                                                 RfCore.Export.ExportFormat.Npy);
            var lib = new DataSourceLibraryViewModel();
            await lib.LoadFileAsync(path);
            await lib.SelectDataSourceAsync(path);

            var plot = new Plot(PlotType.Polar, FreqUnit.GHz) { PolarRadial = PolarRadialMode.Linear };
            var vm   = new PlotInspectorViewModel(plot, () => { }, lib);
            var row  = new TraceRowViewModel(new Trace(new SNP([PatternFixture.FHz], 2),
                                                       MatrixType.S, 0, 0, DependentVarFormat.Db), vm);
            vm.Traces.Add(row);

            string group = row.AvailableGroups.First(g => g.Contains(Group, StringComparison.OrdinalIgnoreCase));
            row.SelectedGroup = group;
            Assert.False(row.AvailableSignals.First(i => i.Label == "U").IsEnabled);

            vm.PolarRadialIsDb = true;
            row.SelectedGroup  = group;
            Assert.True(row.AvailableSignals.First(i => i.Label == "U").IsEnabled);
            Assert.False(row.AvailableSignals.First(i => i.Label == "GainDbi").IsEnabled);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §3 — the picker and the picture agree
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>What is greyed is what the PICTURE refuses, measured rather than restated.</b> For every
    /// far-field cube, every one of its axes is tried as the trace's X — which is what the card's
    /// axis-role editor lets a user do — and the trace is resolved on a real dB-radial polar plot.
    /// A cube that draws a CURVE under SOME choice must be offered; a cube that draws one under
    /// NONE must be greyed. Anything else is a row a user can pick and never get a curve from, or a
    /// row withheld from a curve that would have drawn.
    ///
    /// <para><b>A curve is two points, and that threshold is the one place picker and renderer
    /// deliberately part company.</b> This fixture's <c>BeamwidthDeg</c> is per-cut over a single
    /// cut plane, so its <c>cut</c> axis has length 1: the renderer puts one dot on the disc and
    /// the picker greys it, because a dot at one bearing is not a pattern. The case is asserted by
    /// NAME below rather than left as an unexplained gap in a count — it is the only cube in the
    /// registry that reaches it, and the reason the refusal is worded "not swept over an
    /// angle".</para>
    /// </summary>
    [Fact]
    public void EveryGreyedCubeIsOneNoChoiceOfAxisCanDraw()
    {
        var ds = PatternFixture.Data;
        var degenerate = new List<string>();

        foreach (var (name, cube) in FarFieldCubes())
        {
            string qualified = $"{Group}.{name}";
            var drew = new List<string>();

            for (int d = 0; d < cube.Rank; d++)
            {
                var t = new Trace(new SNP([PatternFixture.FHz], 2), MatrixType.S, 0, 0,
                                  DependentVarFormat.Db)
                {
                    CubeName  = qualified,
                    Slice     = TraceRowViewModel.BuildDefaultSlice([.. cube.Axes], d),
                    Transform = TraceRowViewModel.DefaultTransformFor(cube, PlotType.Polar, qualified,
                                                                     isPatternPlot: true),
                };
                TraceResolve.SetCubeDataFrom(t, ds, PlotType.Polar, FreqUnit.GHz);

                var plot = new Plot(PlotType.Polar, FreqUnit.GHz)
                {
                    PolarRadial = PolarRadialMode.Db, PolarDbFloor = -40, PolarDbRingStep = 10,
                };
                plot.Traces.Add(t);
                plot.Autoscale(force: true);
                t.BuildPath(plot.PlotType, plot.FreqUnits);

                if (!t.PatternAxisInvalid && !t.PatternValueInvalid && t.Points.Count > 1)
                    drew.Add(cube.Axes[d].Name);
                else if (!t.PatternAxisInvalid && !t.PatternValueInvalid && t.Points.Count == 1)
                    degenerate.Add($"{name}/{cube.Axes[d].Name}");
            }

            bool offered = PolarPatternAngle.DisabledReasonOnPattern(cube) is null;
            output.WriteLine($"{name,-24} offered={offered,-5} draws on: "
                           + (drew.Count == 0 ? "(nothing)" : string.Join(", ", drew)));
            Assert.Equal(drew.Count > 0, offered);
        }

        // The single-cut beamwidth, named. A degenerate case that grows silently is a rule nobody
        // is checking any more.
        output.WriteLine($"one point only: {string.Join(", ", degenerate)}");
        Assert.Equal(["BeamwidthDeg/cut"], degenerate);
    }
}
