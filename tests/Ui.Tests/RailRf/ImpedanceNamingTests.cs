// ================================================================
//  ImpedanceNamingTests.cs — brief-railrf-21-two-numbers-one-name.md §4, gates 1-4
//
//  ── THE DEFECT WAS NOT ARITHMETIC ───────────────────────────────────────────────────────────
//
//  railRF prints two impedances of ONE board at ONE frequency from ONE port name and they differ
//  by four orders of magnitude. The board map is the plane pair's — copper, shape, stackup, driven
//  from one observation port, with nothing hanging on it. The plot's curve is the decoupled rail's
//  — the capacitors, their ESR, their mounting loops, the source's R and L. 465 Ω and 23 mΩ are
//  both right. A competent reader concluded the tool was broken, because no surface in the window
//  named which quantity it was showing (2026-09-20).
//
//  So these tests are about WORDS, and there are only two claims worth gating:
//
//    · every surface that says anything says WHICH |Z| it is, and they all say it from ONE
//      constant — two spellings of one distinction is how they come to disagree;
//    · where both numbers are in hand, both are printed, named, and the rail's one really is the
//      curve's value at the map's frequency rather than a second arithmetic.
//
//  And one regression guard, because the temptation R-rail21-1e names is real: NOTHING here may
//  change the map. Gate 4 is the half that would catch someone "fixing" the map by putting the
//  parts into it.
//
//  The fixtures are hand-built answers rather than solves: what is under test is the sentences,
//  and a mesh extraction would add seconds to say nothing about them.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Engine.Pdn;
using CircuitRF.Render;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class ImpedanceNamingTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;   // 1000 DBU/µm
    private static readonly LayerKey Top = new(1, 0);

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * Dbu);

    private const double MapHz = 50e6;
    private const string Port = "U1.VDD";

    // ══ gate 1 — R-rail21-1a: every surface names the quantity, from one constant ═════════════

    /// <summary>
    /// <b>The map caption, the hover readout, the strip and the tab's tooltip all carry the phrase,
    /// and all of them get it from <see cref="PdnImpedanceNames.PlanePair"/>.</b>
    /// </summary>
    /// <remarks>
    /// The assertion is deliberately against the CONSTANT and not against a literal: a test that
    /// spelled the phrase itself would pass while the four surfaces drifted apart, which is the
    /// defect rather than a detail of it. Fails at HEAD — none of the four said anything.
    /// </remarks>
    [Fact]
    public void EverySurfaceThatSaysAnythingSaysWhichImpedanceItIs()
    {
        var plane = Plane();
        var scene = RailMapScene.Build(result: null, RailMapKind.Impedance, Dbu, plane);

        Assert.NotNull(scene.Legend);
        Assert.Contains(PdnImpedanceNames.PlanePair, scene.Legend!.Caption, StringComparison.Ordinal);

        var overlay = new RailLayoutOverlay { DbuPerMicron = Dbu, Kind = RailMapKind.Impedance, Plane = plane };
        overlay.OnPointerMoved(Mm(5), Mm(5), Mm(1), leftButtonDown: false, Avalonia.Input.KeyModifiers.None);
        Assert.NotNull(overlay.Readout);
        Assert.Contains(PdnImpedanceNames.PlanePair, overlay.Readout!, StringComparison.Ordinal);

        var vm = new RailRfViewModel { Plane = plane };
        Assert.Contains(PdnImpedanceNames.PlanePair, vm.ImpedanceMapAt, StringComparison.Ordinal);
        Assert.Contains(PdnImpedanceNames.PlanePair, vm.ImpedanceTabTip, StringComparison.Ordinal);

        // …and the tooltip names the OTHER one too, because the tab is where the two are confused.
        Assert.Contains(PdnImpedanceNames.Rail, vm.ImpedanceTabTip, StringComparison.Ordinal);
    }

    // ══ gate 2 — R-rail21-1b: the note is on the panel, not only in Notes ═════════════════════

    /// <summary>
    /// <b>The sentence that says what is NOT on this map is bound text of the map panel</b>, and it
    /// is the same string the answer carries rather than a second copy of it.
    /// </summary>
    [Fact]
    public void TheNoteSayingWhatTheMapIsOfIsOnThePanelAndIsTheAnswersOwnSentence()
    {
        var plane = Plane();
        Assert.Contains(PdnImpedanceNames.PlanePairNote, plane.Notes);

        var vm = new RailRfViewModel { Plane = plane };
        Assert.Contains(PdnImpedanceNames.PlanePairNote, vm.ImpedanceMapNote, StringComparison.Ordinal);
        Assert.True(vm.HasImpedanceMapNote);
    }

    // ══ gate 3 — R-rail21-1c: both numbers, named, and the rail's one is the curve's ══════════

    /// <summary>
    /// <b>With a sweep run, the map's readout states the plane pair's ohms AND the rail's, each
    /// named — and the rail figure is the curve interpolated at the map's frequency.</b>
    /// </summary>
    /// <remarks>
    /// The oracle is the sweep's own grid: the fixture's curve is built so the map frequency falls
    /// between two points whose values bracket it, and the readout's number is checked against
    /// <c>PdnSweepResult.MagnitudeAt</c> — the one reading of that curve, which is what makes this
    /// about the plumbing rather than about a second interpolation agreeing with itself.
    /// </remarks>
    [Fact]
    public void WithASweepInHandTheReadoutStatesBothNumbersAndTheRailsIsTheCurves()
    {
        var plane = Plane();
        var sweep = Sweep();

        double? expected = sweep.MagnitudeAt(0, MapHz);
        Assert.NotNull(expected);

        // Between the grid points, and genuinely interpolated rather than landing on one.
        Assert.DoesNotContain(MapHz, sweep.FrequenciesHz);
        Assert.InRange(expected!.Value, 0.020, 0.030);

        var vm = new RailRfViewModel { Plane = plane };
        vm.SweepByModel[PdnModelKind.Fast] = sweep;
        vm.Current = new RailResultView(PdnModelKind.Fast, EmptyRun(), 0);

        Assert.Equal(expected!.Value, vm.RailOhmsAtMapFrequency!.Value, 12);

        var overlay = new RailLayoutOverlay
        {
            DbuPerMicron = Dbu, Kind = RailMapKind.Impedance, Plane = plane,
            RailOhmsAtMapFrequency = vm.RailOhmsAtMapFrequency,
        };
        overlay.OnPointerMoved(Mm(5), Mm(5), Mm(1), leftButtonDown: false, Avalonia.Input.KeyModifiers.None);

        // Two numbers, two names, one line.
        string readout = overlay.Readout!;
        Assert.Contains(PdnImpedanceNames.PlanePair, readout, StringComparison.Ordinal);
        Assert.Contains(PdnImpedanceNames.Rail, readout, StringComparison.Ordinal);
        Assert.Contains(RailMapScene.Ohms(expected.Value), readout, StringComparison.Ordinal);
    }

    // ══ gate 4 — R-rail21-1e: the map is still the plane pair's, bit for bit ══════════════════

    /// <summary>
    /// <b>Knowing the rail's own |Z| changes the SENTENCE and not one tile of the picture.</b>
    /// </summary>
    /// <remarks>
    /// This is the guard against the fix R-rail21-1e forbids: adding the parts to the map. The
    /// tiles are compared bit for bit between an overlay that has the rail's number and one that
    /// does not, and the readout is asserted to have CHANGED — without that half the comparison
    /// would pass on a change that reached nothing at all.
    /// </remarks>
    [Fact]
    public void KnowingTheRailsOwnImpedanceChangesNoTileOfTheMap()
    {
        var plane = Plane();

        var bare = new RailLayoutOverlay { DbuPerMicron = Dbu, Kind = RailMapKind.Impedance, Plane = plane };
        bare.OnPointerMoved(Mm(5), Mm(5), Mm(1), leftButtonDown: false, Avalonia.Input.KeyModifiers.None);
        string bareReadout = bare.Readout!;
        var bareTiles = bare.Scene.Tiles.ToList();

        var both = new RailLayoutOverlay
        {
            DbuPerMicron = Dbu, Kind = RailMapKind.Impedance, Plane = plane,
            RailOhmsAtMapFrequency = 0.0234,
        };
        both.OnPointerMoved(Mm(5), Mm(5), Mm(1), leftButtonDown: false, Avalonia.Input.KeyModifiers.None);

        Assert.NotEqual(bareReadout, both.Readout);
        Assert.Equal(bareTiles.Count, both.Scene.Tiles.Count);
        for (int i = 0; i < bareTiles.Count; i++)
        {
            Assert.Equal(bareTiles[i].Value, both.Scene.Tiles[i].Value);     // bit for bit
            Assert.Equal(bareTiles[i].CentreX, both.Scene.Tiles[i].CentreX);
            Assert.Equal(bareTiles[i].CentreY, both.Scene.Tiles[i].CentreY);
        }
    }

    // ══ fixtures ═════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A plane answer with a field worth colouring — four cells an order of magnitude apart, so the
    /// map is not flat and the caption takes its ordinary spelling rather than the flat one.
    /// </summary>
    private static PdnPlaneAnswer Plane()
    {
        var cells = new List<PdnCellRef>();
        var map = new List<PdnPlaneMapCell>();

        for (int i = 0; i < 4; i++)
        {
            var cell = new PdnCellRef(Top, i, 0, Mm(2 + 3 * i), Mm(5), false);
            cells.Add(cell);
            map.Add(new PdnPlaneMapCell(cell, 100.0 * Math.Pow(2.0, i)));
        }

        return new PdnPlaneAnswer(
            null, [], cells, map, MapHz, Port, 1, 1e-3, PdnModelKind.Accurate, 0,
            [new PdnPlanePort(0, Port, Mm(2), Mm(5), true)],
            [PdnImpedanceNames.PlanePairNote]);
    }

    /// <summary>
    /// A two-point |Z| curve at the driven port, with the map's frequency strictly between the
    /// points — so <c>MagnitudeAt</c> has something to interpolate.
    /// </summary>
    private static PdnSweepResult Sweep() =>
        new(null, null, [10e6, 100e6],
            [new PdnPortImpedance(
                0, Port, new RailPortAnchor { Refdes = "U1", Pin = "VDD" },
                [0.018, 0.031], null, NoMask, [], [])],
            [], [], []);

    private static readonly PdnMaskReport NoMask =
        new([], null, null, 0, 0, false, false);

    private static RailDcRunResult EmptyRun() => new(null, [], [], []);
}
