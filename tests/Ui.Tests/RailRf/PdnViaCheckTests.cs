// ================================================================
//  PdnViaCheckTests.cs — brief-railrf-6-via-check.md §4
//
//  ── THE SECOND HALF IS THE POINT ─────────────────────────────────────────────────────────────
//
//  The symmetric gate is a parallel-resistance calculation and needs no simulator: n identical vias
//  in the same place split the current n ways. The ASYMMETRIC gate is the one that catches the
//  defect, because the defect reads perfectly naturally — `group.Sum(i) / group.Count` — and a mean
//  is BELOW the limit on exactly the field whose worst via is over it. So the negative here is not
//  an edit to production code: the test computes the mean itself and asserts that a check built on
//  it would have said nothing, which is the same statement and survives a refactor.
//
//  ── THE LIMIT'S OWN GATE IS ARITHMETIC AND NEEDS NO BOARD ────────────────────────────────────
//
//  R-rail6-2's two numbers are properties of PdnViaCurrentLimit, not of a layout, so they are
//  asserted directly: at 0.3 mm the limit at 20 µm of plating lands in the 0.8–1.0 A review quoted
//  separately, and at 10 µm it lands near half that AND inside the table's own 0.3 mm row. The
//  DISAGREEMENT between those two is the finding, which is why both are in one test.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class PdnViaCheckTests
{
    // ── the board ──────────────────────────────────────────────────────────────────────────────
    //
    // Three conductors, because a via that carries RAIL current joins two rail layers. A two-layer
    // fixture would have its barrels shorting the rail to its own reference, which is a different
    // thing entirely and would make every current below meaningless.

    private const int DbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);
    private static readonly LayerKey Mid = new(2, 0);
    private static readonly LayerKey Bot = new(3, 0);
    private static readonly LayerKey ViaLayer = new(10, 0);

    private const double CopperSigma = 5.8e7;

    private static long Mm(double v) => (long)Math.Round(v * 1e3 * DbuPerMicron);
    private static long Um(double v) => (long)Math.Round(v * DbuPerMicron);

    /// <summary>
    /// TOP and MID are the rail; BOT is the reference. The via entry spans TOP → MID, so every
    /// barrel below is on the rail's own current path.
    /// </summary>
    /// <param name="wallUm">The stackup's plated-wall thickness, or null to state none.</param>
    /// <param name="drillMm">What the holes are drilled at.</param>
    /// <param name="declareSpan">False leaves the via entry with no span declaration at all, which is
    /// R-rail6-5's unresolved span.</param>
    private static Technology Board(double? wallUm, bool declareSpan = true)
    {
        var tech = new Technology { Name = "test board" };
        tech.Stackup.Layers =
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "TOP",
                ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [Top],
            },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "C1", ThicknessDbu = Mm(0.8), Epsr = 4.3 },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "MID",
                ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [Mid],
            },
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "C2", ThicknessDbu = Mm(0.8), Epsr = 4.3 },
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "BOT",
                ThicknessDbu = Um(35), SigmaSm = CopperSigma, DrawingLayers = [Bot],
                IsGroundReference = true,
            },
            new StackupLayer
            {
                Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [ViaLayer],
                Fill = ViaFillKind.Plated,
                WallThicknessDbu = wallUm is { } w ? Um(w) : null,
                SpanFromLayer = declareSpan ? "TOP" : null,
                SpanToLayer = declareSpan ? "MID" : null,
            },
        ];
        return tech;
    }

    private static RectShape Rect(LayerKey layer, long x1, long y1, long x2, long y2) =>
        new() { Layer = layer, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    private static ViaShape Via(double xMm, double yMm, double drillMm) => new()
    {
        Layer = ViaLayer, LandingLayer = Top,
        X = Mm(xMm), Y = Mm(yMm), DrillSize = Mm(drillMm), PadSize = Mm(drillMm + 0.2),
    };

    /// <summary>
    /// A two-layer run: TOP carries the current in from the source, a via field at x = 6 mm hands it
    /// to MID, and MID carries it out to the load. BOT is the return.
    ///
    /// <para>The SOURCE is always a pin field at the via field's own y positions, so the flow
    /// reaching the band is as uniform as the band is. What varies is the LOAD: spread across the
    /// same positions (the flow stays uniform and the field shares equally) or on ONE pad in the
    /// corner immediately past the band, which is the shape of every real pin field on a board.</para>
    /// </summary>
    /// <param name="viaYsMm">Where the barrels sit across the run.</param>
    /// <param name="drillMm">The drill. It also sets the field linkage distance, which must exceed the
    /// via pitch or the barrels are separate transitions rather than one.</param>
    /// <param name="cornerLoad">False spreads the load, true puts it on one pad beside the band.</param>
    /// <param name="widthMm">How wide the run is.</param>
    /// <param name="cellSizeM">The mesh. The symmetric gate needs a fine one — the split converges to
    /// equal from above and reads 4.2 % out at 0.25 mm, 1.6 % at 0.1 mm and inside 1 % at 0.05 mm.</param>
    private static RailDcResult Solve(
        double[] viaYsMm, double drillMm, bool cornerLoad,
        double widthMm = 4.0, double cellSizeM = 0.05e-3,
        double? wallUm = 20.0, double? settingUm = null, double rise = 10.0,
        bool declareSpan = true, double loadA = 3.0, double cornerLoadXmm = 7.0)
    {
        var doc = new RailDocument { Name = "via check" };
        doc.Settings.ViaPlatingThicknessMicrometres = settingUm;
        doc.Settings.ViaTemperatureRiseCelsius = rise;

        var rail = new RailSpec { Name = "VDD", ReferenceLayer = Bot };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
            OpenCircuitVoltageV = 5.0,
        });
        rail.Loads.Add(new RailLoad
        {
            Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" }, DcCurrentA = loadA,
        });
        doc.Rails.Add(rail);

        var shapes = new List<LayoutShape>
        {
            Rect(Top, 0,     0, Mm(7),  Mm(widthMm)),
            Rect(Mid, Mm(5), 0, Mm(12), Mm(widthMm)),
            Rect(Bot, 0,     0, Mm(12), Mm(widthMm)),
        };
        foreach (double y in viaYsMm) shapes.Add(Via(6.0, y, drillMm));

        var pads = new List<PlacedPin>();
        foreach (double y in viaYsMm) pads.Add(new PlacedPin("BT1", "1", "VDD", Mm(1), Mm(y), PinSource.BoardNetlist));

        if (cornerLoad)
            pads.Add(new PlacedPin("U1", "VDD", "VDD", Mm(cornerLoadXmm), Mm(viaYsMm[0]), PinSource.BoardNetlist));
        else
            foreach (double y in viaYsMm) pads.Add(new PlacedPin("U1", "VDD", "VDD", Mm(11), Mm(y), PinSource.BoardNetlist));

        var run = RailDcRun.Run(new RailDcRequest
        {
            Document = doc,
            Technology = Board(wallUm, declareSpan),
            DbuPerMicron = DbuPerMicron,
            Shapes = shapes,
            Pads = pads,
            Model = PdnModelKind.Accurate,
            Mesh = new PdnMeshSettings { CellSizeMetres = cellSizeM, PortRefinementRatio = 1 },
        });

        Assert.Null(run.Refusal);
        return run.Rails[0];
    }

    /// <summary>The asymmetric run: wide, four 0.8 mm vias 3 mm apart, and the load in the corner.
    /// The coarser mesh is deliberate — the discretization noise the symmetric gate has to chase is
    /// two orders below the effect being measured here.</summary>
    private static RailDcResult Offset(double[] viaYsMm, double loadA) =>
        Solve(viaYsMm, drillMm: 0.8, cornerLoad: true,
              widthMm: 12.0, cellSizeM: 0.2e-3, loadA: loadA);

    // ── R-rail6-1: the symmetric case, against the closed form ─────────────────────────────────

    /// <summary>
    /// Four identical vias across a uniform flow split it four ways. This is a parallel-resistance
    /// calculation and needs no simulator — and it is the baseline the asymmetric case is read
    /// against: without it, "the worst via carries more than its share" has no share to be more than.
    /// </summary>
    [Fact]
    public void FourIdenticalViasAcrossAUniformFlowSplitItEqually()
    {
        var result = Solve([0.5, 1.5, 2.5, 3.5], drillMm: 0.3, cornerLoad: false);

        var t = Assert.Single(result.ViaCheck.Transitions);
        Assert.Equal(4, t.Count);

        foreach (var v in t.Vias)
            Assert.InRange(v.ShareOfTransition, 0.25 * 0.99, 0.25 * 1.01);

        Assert.InRange(t.PeakingFactor, 0.99, 1.01);
    }

    // ── R-rail6-1: the asymmetric case, which is the one that catches the defect ───────────────

    /// <summary>
    /// The same four vias with the load in a corner. The point is precisely that the split is NOT
    /// equal: the via nearest the load carries most, the worst is well above I/n, and the flag is on
    /// the worst.
    ///
    /// <para><b>And the negative, stated as arithmetic rather than as an edit.</b> The mean of this
    /// field is below the limit while its worst via is above it — so a check that read
    /// <c>group.Sum(i) / group.Count</c> would report nothing at all on the board that is over. That
    /// assertion IS "replace the per-via read with the average and the test goes red", expressed so
    /// that it keeps holding after the next refactor.</para>
    /// </summary>
    [Fact]
    public void TheWorstViaIsFlaggedAndTheMeanWouldHaveSaidNothing()
    {
        var result = Offset([1.5, 4.5, 7.5, 10.5], loadA: 10.0);

        var t = Assert.Single(result.ViaCheck.Transitions);
        Assert.Equal(4, t.Count);

        // The one nearest the load carries most — asserted by POSITION, not by index, because
        // "Vias[0] is the worst" is the thing under test rather than a premise of it.
        Assert.Equal(Mm(1.5), t.Worst!.Barrel.Y);

        // Ordered, worst first, and genuinely unequal.
        for (int i = 1; i < t.Vias.Count; i++)
            Assert.True(t.Vias[i - 1].CurrentA >= t.Vias[i].CurrentA);

        // The stated factor. This fixture measures 1.57× and the six-via one below measures 1.91×,
        // so 1.4 is well clear of both and well clear of the equal split the symmetric gate holds.
        Assert.True(t.PeakingFactor > 1.4,
            $"the worst via carries only {t.PeakingFactor:0.###}× an equal split — this fixture is not " +
            "asymmetric enough to be testing anything");

        var flag = Assert.Single(result.ViaCheck.Flags);
        Assert.Same(t, flag.Transition);
        Assert.Equal(t.Worst, flag.Transition.Worst);

        // ── the negative ───────────────────────────────────────────────────────────────────────
        double mean = t.Vias.Sum(v => v.CurrentA) / t.Count;
        Assert.True(mean <= t.Limit!.AmpsLimit,
            $"this fixture cannot demonstrate the defect: its MEAN via current ({mean:0.####} A) is " +
            $"already over the {t.Limit.AmpsLimit:0.####} A limit, so averaging would have flagged it too");
        Assert.True(t.Worst.CurrentA > t.Limit.AmpsLimit);

        // And the flag reads as an instruction rather than a complaint.
        string said = flag.Describe();
        Assert.Contains("do not share it equally", said);
        Assert.Contains("would clear it", said);
    }

    // ── R-rail6-1: the count that clears it, and the round trip that makes it an instruction ──

    /// <summary>
    /// A flagged field reports the count that clears it, and re-solving the same board with that many
    /// vias over the same footprint clears the flag. That round trip is what makes the number an
    /// instruction rather than an estimate.
    /// </summary>
    [Fact]
    public void TheCountThatClearsTheFlagActuallyClearsIt()
    {
        double[] six = [1.0, 3.0, 5.0, 7.0, 9.0, 11.0];
        var flagged = Offset(six, loadA: 14.0);

        var t = Assert.Single(flagged.ViaCheck.Transitions);
        Assert.Equal(6, t.Count);
        Assert.Single(flagged.ViaCheck.Flags);

        int count = Assert.IsType<int>(t.CountThatClears);
        Assert.True(count > 6, $"a flagged field must need MORE than the {t.Count} it has");

        // The same footprint, made denser — which is what the count's own stated assumption is about.
        var ys = new List<double>(count);
        for (int i = 0; i < count; i++) ys.Add(1.0 + 10.0 * i / (count - 1));

        var cleared = Offset(ys.ToArray(), loadA: 14.0);

        var c = Assert.Single(cleared.ViaCheck.Transitions);
        Assert.Equal(count, c.Count);
        Assert.True(cleared.ViaCheck.Flags.Count == 0,
            $"{count} vias were asked for and the field still carries {c.Worst!.CurrentA:0.####} A in " +
            $"its worst one against a {c.Limit!.AmpsLimit:0.####} A limit — its peaking rose from " +
            $"{t.PeakingFactor:0.###} to {c.PeakingFactor:0.###} as it was made denser, which is " +
            "exactly what the count's own extrapolation is supposed to allow for");
    }

    // ── R-rail6-2: the limit, and the factor of two the table hides ───────────────────────────

    /// <summary>
    /// §4.2's two figures, in one test, because the DISAGREEMENT between them is the finding: at
    /// 0.3 mm the computed limit at 20 µm of plating lands in the 0.8–1.0 A review quoted separately,
    /// and at 10 µm it lands near half that — and inside the table's own 0.3 mm row, which is what
    /// makes the table a 10 µm table and the separate figure a 20 µm one.
    /// </summary>
    [Fact]
    public void TwentyMicronsOfPlatingIsTwiceTheCurrentOfTen()
    {
        var thick = PdnViaCurrentLimit.Compute(
            0.3e-3, 20e-6, PdnPlatingBasis.StackupViaEntry, 1.6e-3, 10.0)!;
        var thin = PdnViaCurrentLimit.Compute(
            0.3e-3, 10e-6, PdnPlatingBasis.StackupViaEntry, 1.6e-3, 10.0)!;

        Assert.Equal(PdnViaLimitBasis.Computed, thick.Basis);
        Assert.InRange(thick.AmpsLimit, 0.8, 1.0);

        Assert.InRange(thin.AmpsLimit, 0.3, 0.5);
        Assert.InRange(thick.AmpsLimit / thin.AmpsLimit, 1.8, 2.1);

        // The 20 µm figure is OUTSIDE the table's own 0.3 mm row and is not clamped to it — that is
        // §4.2's whole argument, and the limit says so rather than hiding it.
        Assert.False(thick.InsideBand);
        Assert.True(thin.InsideBand);
        Assert.Contains("OUTSIDE", thick.Describe());
    }

    /// <summary>
    /// Every computed limit for a drill the table covers is reported WITH the band, and a drill
    /// outside the table's range says so rather than borrowing a neighbouring row.
    /// </summary>
    [Fact]
    public void EveryLimitCarriesItsSanityBandAndTheGapsAreNotInterpolated()
    {
        foreach (double drillMm in new[] { 0.2, 0.3, 0.4, 0.5, 0.6, 0.8, 1.0, 1.2 })
        {
            var lim = PdnViaCurrentLimit.Compute(
                drillMm * 1e-3, 10e-6, PdnPlatingBasis.StackupViaEntry, 1.6e-3, 10.0)!;

            Assert.NotNull(lim.BandLowAmps);
            Assert.NotNull(lim.BandHighAmps);
            Assert.NotNull(lim.InsideBand);
        }

        // 0.35 mm falls in a gap BETWEEN review's rows. Interpolating across it would invent a band
        // review never gave, so there is none — and the limit still computes.
        var gap = PdnViaCurrentLimit.Compute(
            0.35e-3, 10e-6, PdnPlatingBasis.StackupViaEntry, 1.6e-3, 10.0)!;
        Assert.Null(gap.BandLowAmps);
        Assert.Null(gap.InsideBand);
        Assert.True(gap.AmpsLimit > 0);
        Assert.Contains("outside the range", gap.Describe());
    }

    // ── R-rail6-3 / R-rail6-4: where the plating came from, on every flag ─────────────────────

    /// <summary>
    /// A stackup stating a wall thickness is used and the limit is <c>Computed</c> from it; one
    /// stating none takes the document's typed setting and is still <c>Computed</c>, marked as typed;
    /// and one where NOBODY states it falls back to the drill-size <c>Table</c> rather than computing
    /// an annulus from a number no one checked. Neither of the first two is a silent default and the
    /// third is not a silent computation.
    /// </summary>
    [Fact]
    public void ThePlatingIsTheStackupsOrTypedAndNeverSilentlyDefaulted()
    {
        double[] ys = [0.5, 1.5, 2.5, 3.5];

        var fromStackup = Solve(ys, 0.3, cornerLoad: false, wallUm: 20.0).ViaCheck.Transitions[0].Limit!;
        Assert.Equal(PdnViaLimitBasis.Computed, fromStackup.Basis);
        Assert.Equal(PdnPlatingBasis.StackupViaEntry, fromStackup.PlatingBasis);
        Assert.Contains("the stackup's via entry states", fromStackup.Describe());

        var fromSetting = Solve(ys, 0.3, cornerLoad: false, wallUm: null, settingUm: 20.0)
            .ViaCheck.Transitions[0].Limit!;
        Assert.Equal(PdnViaLimitBasis.Computed, fromSetting.Basis);
        Assert.Equal(PdnPlatingBasis.Setting, fromSetting.PlatingBasis);
        Assert.Contains("from this document's own setting", fromSetting.Describe());

        // The two stated 20 µm by different routes, so they must agree on the number as well as
        // disagree on the sentence — a provenance that changed the ANSWER would be a second rule.
        Assert.Equal(fromStackup.AmpsLimit, fromSetting.AmpsLimit, 12);

        var neither = Solve(ys, 0.3, cornerLoad: false, wallUm: null).ViaCheck.Transitions[0].Limit!;
        Assert.Equal(PdnViaLimitBasis.Table, neither.Basis);
        Assert.NotEqual(fromStackup.AmpsLimit, neither.AmpsLimit);
        Assert.Contains("drill-size guidance", neither.Describe());
    }

    // ── R-rail6-5: an unresolved span is a note and no flag ───────────────────────────────────

    /// <summary>
    /// A via entry with no span declaration is R-rail3-10's "reports rather than assumes", and it
    /// reaches the via check: no barrel, no transition, no flag — and a note saying so. A flag
    /// computed from a guessed 1.6 mm span would be a number with no basis at all.
    /// </summary>
    [Fact]
    public void AnUnresolvedSpanProducesANoteAndNoFlag()
    {
        // The load pad stands where TOP and MID overlap, so its pin field ties the two. At the usual
        // x = 7 mm it lands on MID alone, and with the barrels unstamped MID is joined to nothing:
        // 4 A drawn from a floating piece, which PdnAssembly now refuses (brief-railrf-31 §4).
        var result = Solve([0.5, 1.5, 2.5, 3.5], 0.3, cornerLoad: true, loadA: 4.0, declareSpan: false,
                           cornerLoadXmm: 6.5);

        Assert.Empty(result.ViaCheck.Transitions);
        Assert.Empty(result.ViaCheck.Flags);

        Assert.Equal(4, result.Netlist.Provenance.UnresolvedViaSpans);
        Assert.Contains(result.ViaCheck.Notes, n => n.Contains("could not be resolved to a layer span"));

        // And it is a NOTE, never a finding: a check that did not happen is not a check that failed.
        Assert.DoesNotContain(result.Findings, f => f.Contains("layer span"));
    }
}
