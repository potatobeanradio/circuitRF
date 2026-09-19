// ================================================================
//  PdnImpedanceTests.cs — brief-railrf-12-impedance.md §8
//
//  Z(f) at every observation port, the mask, the aggressor coincidences, the anti-resonance table
//  and the removal ranking. Pure model tests: no window, no app host, no Avalonia. Everything under
//  test is src/Design/RailRf and src/Engine/Pdn, both below the UI firewall.
//
//  ONE TEST PER CLAIM THE BRIEF MAKES. The fixtures are built so the ANSWER IS KNOWN BY
//  CONSTRUCTION — a two-branch board's anti-resonance is 1/(2*pi*sqrt(L*C)) and nothing about that
//  comes out of circuitRF — which is the only kind of gate worth having on arithmetic this easy to
//  make plausible and wrong.
//
//  THE ACCEPTANCE ANCHOR OF §7 IS THE LAST TEST IN THIS FILE and it is guarded: a published measured
//  PDN is external data that has to arrive as bytes somebody measured, and inventing a curve to
//  match would make the one gate that is not our own arithmetic into another piece of it. It SKIPS
//  with a stated reason until the reference lands, exactly as the proprietary loadpull fixtures do.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Engine.Pdn;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.RailRf;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.RailRf;

public class PdnImpedanceTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    // ── fixtures ─────────────────────────────────────────────────────────────
    //
    //  Q-9's own three parts, worded exactly as the note words them: "a 470 uF bulk WITH 5 nH OF
    //  MOUNTING self-resonates near 104 kHz, a 1 uF ceramic near 5.3 MHz, a 100 nF 0402 WITH 1 nH
    //  near 16 MHz". So the bulk's and the 0402's inductance is on the MOUNTING loop and their rows
    //  state none of their own, and the 1 uF row is §2.2's own library row (1 uF at 5.31 MHz), whose
    //  inductance is therefore the part's and derived from its f0.

    private const double BulkMountingH = 5e-9;
    private const double CeramicMountingH = 1e-9;

    private static PartLibraryRow BulkRow() => new()
    {
        PartNumber        = "PN-470U-BULK",
        CapacitanceFarads = 470e-6,
        EsrOhms           = 50e-3,
    };

    /// <summary>§2.2's own first row: a 1 µF part resonating at 5.31 MHz.</summary>
    private static PartLibraryRow Ceramic1uRow() => new()
    {
        PartNumber              = "PN-1U-0402",
        DielectricClass         = "X7R",
        CapacitanceFarads       = 1e-6,
        SelfResonantFrequencyHz = 5.31e6,
        EsrOhms                 = 8e-3,
    };

    private static PartLibraryRow Ceramic100nRow(string partNumber = "PN-100N-0402") => new()
    {
        PartNumber        = partNumber,
        DielectricClass   = "X7R",
        CapacitanceFarads = 100e-9,
        EsrOhms           = 5e-3,
    };

    private static PartLibrary LibraryOf(params PartLibraryRow[] rows)
    {
        var lib = new PartLibrary();
        lib.Rows.AddRange(rows);
        return lib;
    }

    private static RailPart Part(string refdes, string partNumber, double? mountingH) =>
        new() { Refdes = refdes, PartNumber = partNumber, MountingInductanceHenries = mountingH };

    private static RailLoad Load(string refdes, RailTarget? mask = null) =>
        new() { Anchor = new RailPortAnchor { Refdes = refdes, Pin = "VDD" }, Mask = mask };

    /// <summary>
    /// The source, well out of the way. <b>1 Ω in series with 1 µH</b>: above a few hundred kilohertz
    /// its inductance puts it tens of ohms above anything the decoupling does, so a peak this fixture
    /// measures is the parts' own and not the source damping them — which is what makes a closed-form
    /// two-branch oracle legitimate at all.
    /// </summary>
    private static RailSourceModel Source(double ohms = 1.0, double henries = 1e-6) =>
        new(0, "BT1", RailSourceBasis.Rl, ohms, henries, 3.6);

    private static RailSpec Rail(
        IEnumerable<RailPart> parts,
        IEnumerable<RailLoad>? loads = null,
        RailTarget? target = null,
        RailBand? band = null,
        IEnumerable<RailAggressor>? aggressors = null)
    {
        var rail = new RailSpec
        {
            Name            = "VDD_CORE",
            ImpedanceTarget = target,
            Band            = band ?? new RailBand(1e3, 2e8, 4001, true),
        };
        rail.Parts.AddRange(parts);
        rail.Loads.AddRange(loads ?? [Load("U1")]);
        rail.Aggressors.AddRange(aggressors ?? []);
        return rail;
    }

    private static PdnSweepResult Sweep(
        RailSpec rail, PartLibrary library,
        bool rankRemovals = false, IReadOnlyList<RailSourceModel>? sources = null)
    {
        var models = new RailPartResolver(library).ResolveAll(rail.Parts, railVoltageV: 3.6);
        var result = PdnSweep.Run(new PdnSweepRequest
        {
            Rail         = rail,
            Parts        = models,
            Sources      = sources ?? [Source()],
            RankRemovals = rankRemovals,
        });
        Assert.Null(result.Refusal);
        return result;
    }

    /// <summary>The peak of a port's own curve — the highest |Z| the anti-resonance table found.</summary>
    private static PdnAntiResonancePeak Tallest(PdnPortImpedance port) =>
        port.Peaks.OrderByDescending(p => p.PeakOhms).First();

    // ── R-rail12-4: Q-9's four numbers ───────────────────────────────────────

    /// <summary>
    /// <b>R-rail12-4.</b> Q-9's own arithmetic, from the parts the note names — three self-resonant
    /// frequencies out of the part models and one anti-resonance out of the sweep.
    ///
    /// <para><b>The fourth number is NOT 7.1 MHz and the difference is the finding.</b> Q-9 quotes
    /// the bulk-against-ceramic anti-resonance at 7.1 MHz, which is <c>1/(2π√(L_mount·C_ceramic))</c>
    /// — the first-order form that treats the ceramic as a pure capacitor. The circuit does not: at
    /// the anti-resonance the two branches' reactances cancel, so the ceramic's OWN inductance adds
    /// to the bulk's and the answer is <c>1/(2π√((L_bulk + L_ceramic)·C_ceramic))</c> = 6.50 MHz,
    /// 8.7 % below. Both are written out below, and the one the sweep is gated against is the
    /// circuit's — a test that reproduced 7.1 MHz would be a test of an approximation railRF is not
    /// entitled to make.</para>
    /// </summary>
    [Fact]
    public void R_rail12_4_Q9sOwnNumbers_ComeOutOfTheModelsAndTheSweep()
    {
        var library = LibraryOf(BulkRow(), Ceramic1uRow(), Ceramic100nRow());
        var models = new RailPartResolver(library).ResolveAll(
            [Part("C1", "PN-470U-BULK", BulkMountingH),
             Part("C2", "PN-1U-0402", null),
             Part("C3", "PN-100N-0402", CeramicMountingH)],
            railVoltageV: 3.6);

        // Rows 1-3: the mounted parts' own resonances. Not the rows' stated f0 — the model's, which
        // includes the mounting loop (RailPartModel.SelfResonanceHz's own note).
        Assert.Equal(103.8e3, models.Models[0].SelfResonanceHz!.Value, 0.005 * 103.8e3);
        Assert.Equal(5.31e6,  models.Models[1].SelfResonanceHz!.Value, 0.005 * 5.31e6);
        Assert.Equal(15.92e6, models.Models[2].SelfResonanceHz!.Value, 0.005 * 15.92e6);

        // Row 4: the bulk against the ceramic, as a two-part board so the oracle is closed-form.
        var rail = Rail([Part("C1", "PN-470U-BULK", BulkMountingH),
                         Part("C3", "PN-100N-0402", CeramicMountingH)]);
        var port = Sweep(rail, library).Ports[0];
        var peak = Tallest(port);

        double circuit = 1.0 / (2 * Math.PI * Math.Sqrt((BulkMountingH + CeramicMountingH) * 100e-9));
        double firstOrder = 1.0 / (2 * Math.PI * Math.Sqrt(BulkMountingH * 100e-9));

        _output.WriteLine(
            $"peak {peak.FrequencyHz / 1e6:0.###} MHz · circuit closed form {circuit / 1e6:0.###} MHz " +
            $"· Q-9's first-order form {firstOrder / 1e6:0.###} MHz · {peak.Contributors}");

        Assert.Equal(circuit, peak.FrequencyHz, 0.01 * circuit);

        // And it is attributed, not merely located: the bulk supplies the L and the 0402 the C.
        Assert.Equal("C1", peak.Inductive!.Label);
        Assert.Equal("C3", peak.Capacitive!.Label);
    }

    // ── R-rail12-5: the table names its two contributors ─────────────────────

    /// <summary>
    /// <b>R-rail12-5.</b> A two-bank board built so the answer is known by construction names THOSE
    /// TWO BANKS — not the port, not the source, and not a third bank that is far off resonance.
    ///
    /// <para>The negative half is the point of the test: a third bank placed AT the same frequency
    /// changes the naming, because the attribution is a measurement of what is ringing rather than a
    /// guess from proximity, value or part type.</para>
    /// </summary>
    [Fact]
    public void R_rail12_5_TheAntiResonanceNamesTheTwoBanksThatMadeIt()
    {
        var library = LibraryOf(BulkRow(), Ceramic100nRow(), Ceramic1uRow());

        // A bulk bank of three and a ceramic bank of five. The banks are one row of the part library
        // each, so the contributor label reads as the refdes range each spans.
        var parts = new List<RailPart>
        {
            Part("C1", "PN-470U-BULK", BulkMountingH),
            Part("C2", "PN-470U-BULK", BulkMountingH),
            Part("C3", "PN-470U-BULK", BulkMountingH),
            Part("C4", "PN-100N-0402", CeramicMountingH),
            Part("C5", "PN-100N-0402", CeramicMountingH),
            Part("C6", "PN-100N-0402", CeramicMountingH),
            Part("C7", "PN-100N-0402", CeramicMountingH),
            Part("C8", "PN-100N-0402", CeramicMountingH),
        };

        var peak = Tallest(Sweep(Rail(parts), library).Ports[0]);
        _output.WriteLine($"two banks: {peak.Describe()}");

        Assert.Equal("C1–C3", peak.Inductive!.Label);
        Assert.Equal("C4–C8", peak.Capacitive!.Label);

        // THE NEGATIVE, and the direction it moves in is itself the evidence. Add a third bank of
        // 1 µF parts: mounted on 1 nH they resonate at 3.65 MHz, so at the new peak they are
        // INDUCTIVE, and three of them in parallel are 0.63 nH against the bulk bank's 1.67 nH. The
        // attribution follows the susceptance, so the INDUCTIVE name moves off the bulk and onto
        // them — which is the answer a rule that paired "the big one against the small ones", or
        // paired by part type, could not have produced.
        var withThird = new List<RailPart>(parts)
        {
            Part("C9",  "PN-1U-0402", CeramicMountingH),
            Part("C10", "PN-1U-0402", CeramicMountingH),
            Part("C11", "PN-1U-0402", CeramicMountingH),
        };

        var moved = Tallest(Sweep(Rail(withThird), library).Ports[0]);
        _output.WriteLine($"three banks: {moved.Describe()}");

        Assert.NotEqual(peak.Inductive.Group, moved.Inductive!.Group);
        Assert.Equal("C9–C11", moved.Inductive.Label);
        Assert.Equal("C4–C8", moved.Capacitive!.Label);

        // And nothing that is not a part is ever named: the source is a branch of the same netlist
        // and is deliberately not a candidate, because it is not something a user can take off.
        Assert.DoesNotContain("BT1", moved.Contributors, StringComparison.Ordinal);
    }

    // ── R-rail12-6: the removal ranking ──────────────────────────────────────

    /// <summary>
    /// <b>R-rail12-6.</b> A part swamped by its neighbour ranks 0.0 dB; the part holding a decade on
    /// its own ranks large.
    ///
    /// <para><b>The brief's premise for the 0.0 dB row is wrong and the arithmetic is why.</b>
    /// R-rail12-6 words it as <i>"a part in parallel with a LOWER-ESL neighbour 2 mm away"</i>, and
    /// §2.6 calls such parts <i>"shadowed by lower-inductance neighbours"</i>. A mounting loop only
    /// decides a branch ABOVE its own resonance: below it the branch is <c>1/(jωC)</c> and two parts
    /// of the same capacitance contribute the same susceptance whatever they are mounted on. The
    /// worst margin here lives at the anti-resonance, which is below both ceramics' resonances — so
    /// the 4× worse-mounted twin measured below ranks <b>1.6 dB, not 0.0</b>. Shadowing by ESL is a
    /// statement about the band above resonance; what swamps a part where the margin actually is, is
    /// CAPACITANCE. Both rows are asserted, because the first is the finding.</para>
    /// </summary>
    [Fact]
    public void R_rail12_6_AShadowedPartRanksZeroAndTheOneHoldingADecadeRanksLarge()
    {
        var small = Ceramic100nRow("PN-1N-0402");
        small.CapacitanceFarads = 1e-9;
        var library = LibraryOf(BulkRow(), Ceramic100nRow(), small);

        // C2 is C3's twin mounted four times worse — the brief's own "2 mm away". C4 is two decades
        // smaller and mounted four times worse as well: swamped in the band the margin lives in.
        var rail = Rail(
            [Part("C1", "PN-470U-BULK", BulkMountingH),
             Part("C2", "PN-100N-0402", 4 * CeramicMountingH),
             Part("C3", "PN-100N-0402", CeramicMountingH),
             Part("C4", "PN-1N-0402", 4 * CeramicMountingH)],
            target: RailTarget.OfFlatImpedance(milliohms: 900),
            band: new RailBand(1e3, 2e7, 601, true));

        var result = Sweep(rail, library, rankRemovals: true);
        foreach (var row in result.Removal) _output.WriteLine(row.Describe());

        var byName = result.Removal.ToDictionary(r => r.Name);

        Assert.True(byName["C4 (PN-1N-0402)"].Redundant,
            $"the swamped part ranked {byName["C4 (PN-1N-0402)"].GrowthDb:0.0#} dB");
        Assert.True(byName["C4 (PN-1N-0402)"].GrowthDb < PdnRemovalRanking.DisplayResolutionDb);

        // THE FINDING: the worse-mounted TWIN is not shadowed at all where the margin is. Asserted
        // rather than noted, so a later change that quietly made it read 0.0 dB would fail here.
        Assert.False(byName["C2 (PN-100N-0402)"].Redundant);
        Assert.True(byName["C2 (PN-100N-0402)"].GrowthDb > 1.0,
            $"the worse-mounted twin ranked {byName["C2 (PN-100N-0402)"].GrowthDb:0.0#} dB");

        // The bulk is the only thing under 900 mΩ across the whole low decade, so taking it off has
        // to cost real decibels.
        Assert.True(byName["C1 (PN-470U-BULK)"].GrowthDb > 3.0,
            $"the bulk ranked {byName["C1 (PN-470U-BULK)"].GrowthDb:0.0} dB");

        // Ranked most-first, so §2.6 step 7 can read the 0.0 dB rows as a block at the bottom.
        Assert.Equal("C1 (PN-470U-BULK)", result.Removal[0].Name);
        Assert.Contains(result.Redundant, r => r.Name == "C4 (PN-1N-0402)");
    }

    /// <summary>
    /// <b>R-rail12-6's own reason.</b> The ranking by re-solve DIFFERS from a first-order sensitivity
    /// near an anti-resonance — which is the claim §2.4 makes and the reason for the design.
    ///
    /// <para>The arithmetic is exact and needs no solver. The port sees <c>Z = 1/ΣY</c>, so removing
    /// branch <c>p</c> gives <c>Z' = Z/(1 − Z·Y_p)</c> exactly, while a first-order step from
    /// <c>∂Z/∂Y_p = −Z²</c> gives <c>Z' ≈ Z(1 + Z·Y_p)</c>. The two agree only while
    /// <c>|Z·Y_p| ≪ 1</c> — and at an anti-resonance <c>ΣY</c> is the near-cancellation of two large
    /// susceptances, so <c>|Z·Y_p|</c> is of order one or more and the approximation is not merely
    /// imprecise, it has left its neighbourhood.</para>
    /// </summary>
    [Fact]
    public void R_rail12_6_TheReSolveDiffersFromAFirstOrderSensitivityAtTheAntiResonance()
    {
        var library = LibraryOf(BulkRow(), Ceramic100nRow());
        var rail = Rail([Part("C1", "PN-470U-BULK", BulkMountingH),
                         Part("C3", "PN-100N-0402", CeramicMountingH)]);

        var result = Sweep(rail, library);
        var port = result.Ports[0];
        var peak = Tallest(port);
        double f = peak.FrequencyHz;

        // The two branch admittances at the peak, from the models' own numbers.
        var yBulk = BranchAdmittance(f, 470e-6, BulkMountingH, 50e-3);
        var yCeramic = BranchAdmittance(f, 100e-9, CeramicMountingH, 5e-3);
        var ySource = Complex.One / Source().ImpedanceAt(f);

        var z = Complex.One / (yBulk + yCeramic + ySource);
        var exact = z / (Complex.One - z * yCeramic);
        var firstOrder = z * (Complex.One + z * yCeramic);

        double exactDb = 20 * Math.Log10(exact.Magnitude / z.Magnitude);
        double firstOrderDb = 20 * Math.Log10(firstOrder.Magnitude / z.Magnitude);

        _output.WriteLine(
            $"at {f / 1e6:0.###} MHz: |Z·Y_p| = {(z * yCeramic).Magnitude:0.###}; " +
            $"re-solve {exactDb:0.0} dB, first order {firstOrderDb:0.0} dB");

        // The sweep's own |Z| agrees with the closed form it was built from, which is what makes the
        // rest of this test a statement about the two ESTIMATORS rather than about the solver.
        Assert.Equal(z.Magnitude, port.MagnitudeOhms[peak.Index], 0.02 * z.Magnitude);

        Assert.True((z * yCeramic).Magnitude > 0.5,
            "the fixture is not actually near an anti-resonance");
        Assert.True(Math.Abs(exactDb - firstOrderDb) > 3.0,
            $"the two estimators differ by only {Math.Abs(exactDb - firstOrderDb):0.0} dB");
    }

    private static Complex BranchAdmittance(double f, double farads, double henries, double ohms)
    {
        double w = 2 * Math.PI * f;
        return Complex.One / new Complex(ohms, w * henries - 1.0 / (w * farads));
    }

    // ── R-rail12-1 / R-rail12-2: the result is an ordinary DataSet ───────────

    /// <summary>
    /// <b>R-rail12-1 and R-rail12-2.</b> The result is a <c>DataSet</c> carrying a Z cube over
    /// <c>[freq, port, port]</c>, and it resolves through the Data Display's OWN trace parser with no
    /// special support — where <c>Z[:, 2, 1]</c> is port 2 into port 1 and not index 2.
    /// </summary>
    [Fact]
    public void R_rail12_1_TheResultIsADataSetWhoseZCubeResolvesAsPortNumbers()
    {
        var library = LibraryOf(BulkRow(), Ceramic100nRow());
        var rail = Rail(
            [Part("C1", "PN-470U-BULK", BulkMountingH), Part("C3", "PN-100N-0402", CeramicMountingH)],
            loads: [Load("U1"), Load("U2")],
            band: new RailBand(1e3, 2e8, 201, true));

        var data = Sweep(rail, library).Data!;

        var z = data["Z"];
        Assert.Equal(["freq", "i", "j"], z.Axes.Select(a => a.Name).ToArray());
        Assert.Equal(DataKind.Complex, z.DataKind);
        Assert.Equal("Ohm", z.Unit);
        Assert.Equal(201, z.Axes[0].Length);

        // R-rail12-2: the port axes carry 1-BASED PORT NUMBERS, the same convention FromSnp gives
        // the S cube — which is what makes "i=2" mean port 2 rather than the third port.
        Assert.Equal([1.0, 2.0], z.Axes[1].Values);
        Assert.Equal([1.0, 2.0], z.Axes[2].Values);
        Assert.Equal("port", z.Axes[1].Unit);
        Assert.Equal(["U1.VDD", "U2.VDD"], z.Axes[1].Labels!);

        // And the resolution itself, through the parser the trace card and the `plot` verb both use.
        // Off by one here is the quietest possible wrong answer on a PDN, because a Z matrix is
        // reciprocal and Z12 and Z21 are the same curve.
        Assert.True(CubeTraceSpecParser.TryParse("Z[:, 2, 1]", data, out string cube, out var slice,
                                                 out _, out string error), error);
        Assert.Equal("Z", cube);
        Assert.Equal(
            [("freq", AxisRole.KeepAsX, 0), ("i", AxisRole.PinToIndex, 1), ("j", AxisRole.PinToIndex, 0)],
            slice!.Select(s => (s.AxisName, s.Role, s.Index)).ToArray());

        // The S cube it was converted from is there too, so the same run overlays in an ordinary
        // Data Display as S-parameters or as impedance without a second analysis.
        Assert.True(data.Contains("S"));
        Assert.True(data.Contains("Z0"));
    }

    // ── R-rail12-7: an indicative margin is marked as one ────────────────────

    /// <summary>
    /// <b>R-rail12-7.</b> A margin derived from a class-default ESR is flagged; one derived from a
    /// stated ESR is not — <b>on the row</b>, on the report, and in the metadata that crosses the
    /// <c>DataSet</c> boundary into every export.
    /// </summary>
    [Fact]
    public void R_rail12_7_AMarginFromAClassDefaultEsrIsFlaggedAndOneFromAStatedEsrIsNot()
    {
        var target = RailTarget.OfFlatImpedance(milliohms: 100);
        var band = new RailBand(1e3, 2e8, 401, true);
        var parts = new[] { Part("C1", "PN-470U-BULK", BulkMountingH),
                            Part("C3", "PN-100N-0402", CeramicMountingH) };

        // The stated case: every row carries its own ESR, so nothing here is indicative.
        var stated = Sweep(Rail(parts, target: target, band: band),
                           LibraryOf(BulkRow(), Ceramic100nRow()));

        Assert.False(stated.Indicative);
        Assert.All(stated.Ports, p => Assert.False(p.MaskReport.Indicative));
        Assert.All(stated.Ports, p => Assert.All(p.Peaks, k => Assert.False(k.Indicative)));

        // The class-default case: the 0402's row states a dielectric class and NO ESR, so Q-15's
        // dissipation-factor default is what sets every peak height it takes part in.
        var defaulted = Ceramic100nRow();
        defaulted.EsrOhms = null;
        var indicative = Sweep(Rail(parts, target: target, band: band),
                               LibraryOf(BulkRow(), defaulted));

        Assert.True(indicative.Indicative);
        Assert.All(indicative.Ports, p => Assert.True(p.MaskReport.Indicative));
        Assert.All(indicative.Ports, p => Assert.All(p.Peaks, k => Assert.True(k.Indicative)));
        Assert.Contains("indicative", indicative.Ports[0].MaskReport.Describe(), StringComparison.Ordinal);

        // R-rail11-4's mechanism: the marking rides the DataSet as metadata, which is the only route
        // by which an export's provenance can carry it.
        var basis = indicative.Data![RailPartModelSet.EsrBasisCube];
        Assert.Contains(basis.RealValues, v => v == (double)(int)EsrProvenance.ClassDefault);
        Assert.DoesNotContain(
            Sweep(Rail(parts, target: target, band: band), LibraryOf(BulkRow(), Ceramic100nRow()))
                .Data![RailPartModelSet.EsrBasisCube].RealValues,
            v => v == (double)(int)EsrProvenance.ClassDefault);
    }

    // ── §2.4: the coincidence check ──────────────────────────────────────────

    /// <summary>
    /// <b>R-rail12-4's second half.</b> The sentence the tool exists to produce: the anti-resonance
    /// named against the aggressor it lands on, with the harmonic number and the separation.
    ///
    /// <para>And the negative that makes the feature worth having (§2.2): the same peak with the
    /// aggressor moved off it produces NO row — <i>a 9 dB peak nothing excites is not a
    /// problem.</i></para>
    /// </summary>
    [Fact]
    public void TheCoincidenceCheckNamesTheAggressorAndSaysNothingWhenNothingExcitesThePeak()
    {
        var library = LibraryOf(BulkRow(), Ceramic100nRow());
        var parts = new[] { Part("C1", "PN-470U-BULK", BulkMountingH),
                            Part("C3", "PN-100N-0402", CeramicMountingH) };

        double resonance = 1.0 / (2 * Math.PI * Math.Sqrt((BulkMountingH + CeramicMountingH) * 100e-9));

        // A converter whose FIFTH harmonic lands on the anti-resonance — §2.2's own example of the
        // small peak that matters.
        var onIt = Sweep(
            Rail(parts, aggressors: [new RailAggressor("converter", resonance / 5.0, 6)]), library);

        var row = Assert.Single(onIt.Coincidences);
        _output.WriteLine(row.Describe());
        Assert.Equal("converter", row.AggressorName);
        Assert.Equal(5, row.Harmonic);
        Assert.True(row.SeparationFraction < PdnCoincidence.DefaultFraction);

        // The same board with the converter an octave away: the peak is still there and nothing
        // excites it, so the short list is empty.
        var offIt = Sweep(
            Rail(parts, aggressors: [new RailAggressor("converter", resonance / 10.0, 4)]), library);
        Assert.Empty(offIt.Coincidences);
        Assert.NotEmpty(offIt.Ports[0].Peaks);
    }

    // ── §2.4: the mask ───────────────────────────────────────────────────────

    /// <summary>
    /// The mask is per OBSERVATION PORT (§2.2), so a load row's own mask beats the rail's single
    /// number — and a margin computed outside the mask's own band is not computed at all.
    /// </summary>
    [Fact]
    public void AMaskOnTheLoadRowBeatsTheRailsFlatTargetAndIsNotJudgedOutsideItsOwnBand()
    {
        var library = LibraryOf(BulkRow(), Ceramic100nRow());

        // U1 takes the rail's flat 100 mΩ; U2 states its own mask, one decade wide and far looser.
        var rail = Rail(
            [Part("C1", "PN-470U-BULK", BulkMountingH), Part("C3", "PN-100N-0402", CeramicMountingH)],
            loads:
            [
                Load("U1"),
                Load("U2", RailTarget.OfMask([new RailMaskPoint(1e6, 10.0),
                                              new RailMaskPoint(1e7, 10.0)])),
            ],
            target: RailTarget.OfFlatImpedance(milliohms: 100),
            band: new RailBand(1e3, 2e8, 401, true));

        var result = Sweep(rail, library);

        Assert.Equal(false, result.Ports[0].MaskReport.Passes);   // 100 mΩ is not met at the peak
        Assert.Equal(true, result.Ports[1].MaskReport.Passes);    // 10 Ω over one decade is

        // The second port's mask covers one decade of a 5.3-decade sweep, so most of the run is
        // UNJUDGED rather than passing — which is the distinction a report that only counted
        // violations could not make.
        Assert.True(result.Ports[1].MaskReport.UnjudgedPoints > result.Ports[1].MaskReport.JudgedPoints);
        Assert.Contains("not judged", result.Ports[1].MaskReport.Describe(), StringComparison.Ordinal);
    }

    // ── R-rail12-3: the plot ─────────────────────────────────────────────────

    /// <summary>
    /// <b>R-rail12-3</b>, and brief 4's <c>R-rail4-5</c>: the |Z| curve, the target and the aggressor
    /// lines are on the plot, and <b>after an Accuracy run BOTH curves are there</b> — so the error
    /// between the two readings is measured on this design rather than promised in a document.
    ///
    /// <para>Every one of them is an ordinary <c>Trace</c> on an ordinary <c>PlotControl</c> in
    /// rectangular mode (§11.1). What is asserted is the TRACES, not pixels: the window owns which
    /// curves exist and the Data Display owns how they are drawn, and a pixel assertion here would be
    /// testing the second through the first.</para>
    /// </summary>
    [Fact]
    public void R_rail12_3_BothModelsCurvesAreOnThePlotWithTheTargetAndTheAggressorLines()
    {
        var vm = Window();

        vm.RunCommand.Execute(null);

        var afterFast = vm.ImpedancePlot.Traces.Select(t => t.CubeName).ToArray();
        _output.WriteLine("fast: " + string.Join(" · ", afterFast));

        Assert.Contains("Z", afterFast);
        Assert.Contains(afterFast, n => n!.StartsWith("target(", StringComparison.Ordinal));
        Assert.Contains(afterFast, n => n!.StartsWith("converter × ", StringComparison.Ordinal));
        Assert.False(vm.HasBothImpedanceCurves);

        // The aggressor's fundamental and its three harmonics, each its own line — a peak's row in
        // the coincidence table names a harmonic NUMBER, so the picture has to be able to show which.
        Assert.Equal(4, afterFast.Count(n => n!.StartsWith("converter × ", StringComparison.Ordinal)));

        vm.AccuracyCommand.Execute(null);

        Assert.True(vm.HasBothImpedanceCurves);
        Assert.Equal(2, vm.ImpedancePlot.Traces.Count(t => t.CubeName == "Z"));

        // And the two are told apart on the picture rather than in a legend: Fast dashed, Accurate
        // solid. Both carry points, which is the half that would silently fail if the cube binding
        // resolved against the wrong DataSet.
        var curves = vm.ImpedancePlot.Traces.Where(t => t.CubeName == "Z").ToArray();
        Assert.All(curves, c => Assert.NotEmpty(c.Points));
        Assert.Contains(curves, c => c.Properties.LineType == LineType.Dashed);
        Assert.Contains(curves, c => c.Properties.LineType == LineType.Solid);

        // ── THE TITLE NAMES THE PICTURE, AND THE PANEL IS THE CURVES' (owner, 2026-09-19) ──
        //
        // It used to spell out the model kinds and what the vertical lines were. Both are said
        // elsewhere on screen — the model kind is on the status strip on every frame — so the
        // claim they carried is asserted where it now lives rather than dropped.
        Assert.Equal("|Z| over frequency", vm.ImpedancePlot.CustomTitle);
        Assert.Contains("Accuracy", vm.StatusLine, StringComparison.Ordinal);

        // And the margin is sized for ONE label column, not one per trace. With thirteen traces of
        // the one quantity the left margin hit its 0.40 clamp and the curves were left in just over
        // half the panel; the Y label is plot-wide, so exactly one rotated column is ever drawn.
        Assert.True(vm.ImpedancePlot.Traces.Count > 5,
                    "fewer traces than the defect needed, so this is not exercising it.");
        Assert.True(vm.ImpedancePlot.Axes.Viewport.Width > 0.75,
                    $"the curves have {vm.ImpedancePlot.Axes.Viewport.Width:P0} of the panel's width — "
                  + "the Y-label margin is still being charged per trace.");

        // §2.4's four tables are beside the curve, not only in the result object — each is a card in
        // the docked results list and each is gated on its own bool, so an empty one is absent rather
        // than a heading over nothing.
        Assert.True(vm.HasMaskVerdict);
        Assert.True(vm.HasAntiResonances);
        Assert.True(vm.HasCoincidences);
        Assert.True(vm.HasRemovalRanking);

        // The coincidence row is the sentence the tool exists to produce, so it names BOTH halves.
        _output.WriteLine(vm.CoincidenceLines[0]);
        Assert.Contains("converter", vm.CoincidenceLines[0], StringComparison.Ordinal);
        Assert.Contains("against C(", vm.CoincidenceLines[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A multi-marker reads the CURVES, and the mask and the aggressor lines are not curves.</b>
    /// </summary>
    /// <remarks>
    /// Turning Multi on in a marker's info box added eleven rows reading <c>NaN</c> to the two real
    /// ones (owner, 2026-09-19). They are not wrong readings, they are not readings: a multi-marker
    /// reads every other trace at its own X SAMPLE, and the mask edge and the aggressor lines carry
    /// two points rather than the sweep's 401, so at the marker's index there is nothing to read.
    ///
    /// <para>Asserted through <c>BuildMarkerBoxLines</c> — the one function the renderer both
    /// MEASURES the box with and DRAWS it with, so a filter that worked in only one of the two
    /// would give a box the wrong size for its contents.</para>
    /// </remarks>
    [Fact]
    public void AMultiMarkerReadsTheImpedanceCurvesAndNotTheMaskOrTheAggressorLines()
    {
        var vm = Window();

        // A SECOND observation port, so the readout has a real row to produce as well as the rows it
        // must not: a test that only asserted the absence of NaN would pass on a filter that dropped
        // everything.
        vm.Document.Rails[0].Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U2", Pin = "VDD" } });
        vm.RunCommand.Execute(null);

        var traces = vm.ImpedancePlot.Traces.ToList();
        Assert.True(traces.Count > 5, "not enough traces to be exercising the defect.");

        // Every trace that is not a |Z| curve says so of itself — one flag, so the menu, the
        // double-click and the readout cannot come to disagree about what this plot holds.
        Assert.All(traces, t => Assert.Equal(t.CubeName != "Z", t.IsAnnotation));

        var curve = traces.First(t => t.CubeName == "Z");
        var marker = new Marker(curve, curve.Points[curve.Points.Count / 2].X, isMulti: true,
                                isDelta: false, index: 1, FreqUnit.MHz);

        var lines = curve.BuildMarkerBoxLines(marker, FreqUnit.MHz, showFilePrefix: false, traces);
        foreach (var (text, _) in lines) _output.WriteLine(text);

        Assert.DoesNotContain(lines, l => l.Text.Contains("NaN", StringComparison.Ordinal));

        // And the rows it DOES add are the other curves — the marker's own port being its first
        // line. A filter that dropped everything would pass the assertion above for free.
        int curves = traces.Count(t => t.CubeName == "Z");
        Assert.True(curves > 1, "one curve only — the multi readout has nothing to report.");
        Assert.Equal(curves, lines.Count(l => l.Text.StartsWith("Z(", StringComparison.Ordinal)));

        // The exclusion is load-bearing, not decorative: read directly, an annotation trace is
        // exactly the NaN row that was on screen.
        var mask = traces.First(t => t.CubeName!.StartsWith("target(", StringComparison.Ordinal));
        Assert.Contains("NaN", curve.GetMultiMarkerLine(marker, mask), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Delete removes the selected marker</b> (owner, 2026-09-19: a marker is selected and the
    /// Delete keystroke does not remove it).
    /// </summary>
    /// <remarks>
    /// This window answered no Delete at all — the binding was simply never written, although the
    /// results plot is a real <c>PlotControl</c> with real markers on it and every other plot
    /// surface in the application answers the key. The Match Designer had the identical report and
    /// the identical fix, which is why the command here is a copy of that one rather than
    /// <c>DataDisplayViewModel.DeleteSelected</c>: that also removes selected plot CONTAINERS, and
    /// binding the wrong one would look identical until somebody selected the plot.
    /// </remarks>
    [Fact]
    public void DeleteRemovesTheSelectedMarkerAndCannotTakeThePlotWithIt()
    {
        var vm = Window();
        vm.RunCommand.Execute(null);

        var trace = vm.ImpedancePlot.Traces.First(t => t.CubeName == "Z");
        var marker = new Marker(trace, trace.Points[trace.Points.Count / 2].X, isMulti: false,
                                isDelta: false, index: 1, FreqUnit.MHz);
        trace.Markers.Add(marker);
        vm.ImpedanceContainer.OnPlotChanged(vm, EventArgs.Empty);   // what publishes the info box

        var box = Assert.Single(vm.PlotHost.MarkerInfoBoxes);
        box.IsSelected = true;

        int plots = vm.PlotHost.Plots.Count;
        vm.DeleteSelectedMarkersCommand.Execute(null);

        Assert.Empty(trace.Markers);
        Assert.Empty(vm.PlotHost.MarkerInfoBoxes);

        // And it did NOT take the plot with it — this window's one plot is not deletable, there
        // being nothing to delete it from and its traces rebuilt from the document on every solve.
        Assert.Equal(plots, vm.PlotHost.Plots.Count);

        // The keystroke reaches it: bound at the window, so it works wherever focus sits.
        string xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(
            RepoRoot(), "src", "Ui", "Views", "RailRf", "RailRfWindow.axaml"));
        Assert.Contains("Gesture=\"Delete\" Command=\"{Binding DeleteSelectedMarkersCommand}\"",
                        xaml, StringComparison.Ordinal);
    }

    // ── Markers persist in the `.crail` (owner, 2026-09-19) ──────────────────

    /// <summary>
    /// A marker survives closing the document: it is written into the <c>.crail</c>, it comes back
    /// on the same curve at the same frequency, and its info box comes back where it was dragged to.
    /// </summary>
    /// <remarks>
    /// <b>A marker is a reading somebody took</b> — the thing they came to this window for — and the
    /// document said nothing about markers at all, so every one of them was gone the moment the
    /// window closed.
    ///
    /// <para><b>The trace it is restored onto is matched on <c>CurveKey</c></b>, the reading and the
    /// port, which is the same key <c>RebuildImpedancePlot</c> already carries a user's edits across
    /// a re-solve with. A marker pinned to a trace OBJECT would be pinned to something that does not
    /// survive a keystroke: every trace here is rebuilt from the sweep on each committed edit.</para>
    ///
    /// <para><b>The marker is placed here the way <c>PlotControl.TryAddMarkerNearPoint</c> places
    /// one on a cube-bound trace</b> — <c>PositionStatic</c> = (cube X, curve index), <c>Freq</c>
    /// left at 0 — because there is no headless Avalonia host in this project to raise a real
    /// double-click on. That is the same reason the double-click rule above is gated by a source
    /// scan.</para>
    /// </remarks>
    [Fact]
    public void AMarkerIsWrittenIntoTheCrail_AndComesBackOnItsOwnCurveAtItsOwnFrequency()
    {
        var vm = Window();
        vm.RunCommand.Execute(null);

        var curve = vm.ImpedancePlot.Traces.Single(t => t.CubeName == "Z");
        PlaceMarker(curve, index: 3, hz: 2.5e6, boxX: 140.5, boxY: 62.25);
        vm.CaptureMarkers();

        var saved = Assert.Single(vm.Document.Rails[0].Markers);
        Assert.Equal(PdnModelKind.Fast, saved.Model);
        Assert.Equal(0, saved.Port);
        Assert.Equal(2.5e6, saved.FrequencyHz, 0);
        Assert.Equal(3, saved.Index);
        Assert.Equal(140.5, saved.InfoBoxX!.Value, 3);
        Assert.Equal(62.25, saved.InfoBoxY!.Value, 3);

        // Through the file, and back into a window that has never seen it.
        string json = RailDocumentIo.Serialize(vm.Document);
        Assert.Contains("\"Markers\"", json);

        var reopened = Window(RailDocumentIo.Deserialize(json));
        reopened.RunCommand.Execute(null);

        var restored = Assert.Single(
            reopened.ImpedancePlot.Traces.Single(t => t.CubeName == "Z").Markers);
        Assert.Equal(2.5e6, restored.PositionStatic.X, 0);
        Assert.Equal(3, restored.Index);
        Assert.Equal(140.5, restored.InfoBoxPos.X, 3);
        Assert.Equal(62.25, restored.InfoBoxPos.Y, 3);

        // A document with no markers still says nothing about them, so an untouched `.crail` is the
        // bytes it was.
        Assert.DoesNotContain("\"Markers\"", RailDocumentIo.Serialize(Window().Document));
    }

    /// <summary>
    /// Moving a marker — or its info box — marks the document, which is what puts the bullet on the
    /// title and makes closing the window ask.
    /// </summary>
    /// <remarks>
    /// <b>The owner's own instruction.</b> It works because the capture runs off
    /// <c>DataDisplayViewModel.ContentChanged</c>, the same channel a <c>.cdd</c> document's own
    /// dirty check runs off — so the info-box drag and the marker editor's rename are seen as well
    /// as the add and the move. A hand-maintained list of marker events would be a list that misses
    /// one.
    /// </remarks>
    [Fact]
    public void MovingAMarkerMarksTheDocumentAsUnsaved()
    {
        var vm = Window();
        vm.RunCommand.Execute(null);

        var curve = vm.ImpedancePlot.Traces.Single(t => t.CubeName == "Z");
        var marker = PlaceMarker(curve, index: 1, hz: 1.0e6, boxX: 10, boxY: 10);
        vm.CaptureMarkers();

        // Pretend it has just been saved: what is on screen is what is on disk.
        vm.NoteSaved("/boards/evk/evk.crail");
        Assert.False(vm.IsDirty);

        // Drag the marker along the curve.
        marker.PositionStatic = new Vector2(4.0e6f, 0f);
        vm.CaptureMarkers();
        Assert.True(vm.IsDirty);
        Assert.Equal(4.0e6, vm.Document.Rails[0].Markers[0].FrequencyHz, 0);

        // And dragging only the BOX is an edit too — it is what the reader arranged.
        vm.NoteSaved("/boards/evk/evk.crail");
        marker.InfoBoxPos = new PlotPoint(200, 90);
        vm.CaptureMarkers();
        Assert.True(vm.IsDirty);
    }

    /// <summary>
    /// A marker on the ACCURATE curve is not deleted while only the fast one is drawn, and it is
    /// put back the moment that curve exists.
    /// </summary>
    /// <remarks>
    /// <b>This is the failure that would have lost work silently.</b> The accurate reading exists
    /// only after Accuracy has been pressed, so a capture that rebuilt the list from what is
    /// currently on the plot would quietly delete every marker belonging to the reading that is not
    /// on screen — and a restore driven by one flag for the whole plot would never put them back,
    /// because the first rebuild would have set it. Both halves are per CURVE for that reason.
    /// </remarks>
    [Fact]
    public void AMarkerOnTheAccurateCurveSurvivesWhileOnlyTheFastOneIsDrawn()
    {
        var vm = Window();
        vm.RunCommand.Execute(null);
        vm.AccuracyCommand.Execute(null);
        Assert.True(vm.HasBothImpedanceCurves);

        var accurate = vm.ImpedancePlot.Traces.First(
            t => t.CubeName == "Z" && t.Properties.LineType == LineType.Solid);
        PlaceMarker(accurate, index: 2, hz: 3.0e6, boxX: 20, boxY: 30);
        vm.CaptureMarkers();

        Assert.Equal(PdnModelKind.Accurate, Assert.Single(vm.Document.Rails[0].Markers).Model);

        // Reopen and run the FAST reading only. The accurate curve is not on the plot, so nothing
        // can speak for its markers — and the capture that runs on every redraw must not.
        var reopened = Window(RailDocumentIo.Deserialize(RailDocumentIo.Serialize(vm.Document)));
        reopened.RunCommand.Execute(null);
        Assert.False(reopened.HasBothImpedanceCurves);
        Assert.Empty(reopened.ImpedancePlot.Traces.Single(t => t.CubeName == "Z").Markers);
        Assert.Single(reopened.Document.Rails[0].Markers);

        // Accuracy brings its curve back, and the marker with it.
        reopened.AccuracyCommand.Execute(null);
        var back = Assert.Single(reopened.ImpedancePlot.Traces
            .First(t => t.CubeName == "Z" && t.Properties.LineType == LineType.Solid).Markers);
        Assert.Equal(3.0e6, back.PositionStatic.X, 0);
    }

    /// <summary>
    /// One marker, placed the way <c>PlotControl</c> places one on a cube-bound trace.
    /// </summary>
    private static Marker PlaceMarker(Trace curve, int index, double hz, double boxX, double boxY)
    {
        var marker = new Marker(curve, 0.0, isMulti: false, isDelta: false, index, FreqUnit.MHz)
        {
            MarkerKind     = MarkerKind.Polyline,
            PositionStatic = new Vector2((float)hz, 0f),
            InfoBoxPos     = new PlotPoint(boxX, boxY),
        };
        curve.Markers.Add(marker);
        return marker;
    }

    /// <summary>
    /// A window with a board, a confirmed reference, a part library and a stubbed DC solve — so the
    /// FREQUENCY answer under test is the real <see cref="PdnSweep"/> and nothing else is.
    /// </summary>
    private static RailRfViewModel Window()
    {
        var doc = new RailDocument { Name = "evk" };
        var rail = new RailSpec { Name = "+1V8", NetName = "+1V8", Band = new RailBand(1e3, 2e8, 401, true) };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
            OpenCircuitVoltageV = 3.7, SeriesResistanceOhms = 1.0, SeriesInductanceHenries = 1e-6,
        });
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" } });
        rail.Parts.Add(Part("C1", "PN-470U-BULK", BulkMountingH));
        rail.Parts.Add(Part("C3", "PN-100N-0402", CeramicMountingH));
        rail.Aggressors.Add(new RailAggressor("converter", 1.5e6, 4));
        rail.ImpedanceTarget = RailTarget.OfFlatImpedance(milliohms: 200);
        doc.Rails.Add(rail);
        return Window(doc);
    }

    /// <summary>The same window over a document that already exists — what reopening a saved
    /// <c>.crail</c> gives you, with the board and the library this fixture supplies.</summary>
    private static RailRfViewModel Window(RailDocument doc)
    {
        var vm = new RailRfViewModel(doc, null)
        {
            PostToUi     = a => a(),
            RunOffThread = (work, _) => System.Threading.Tasks.Task.FromResult(work()),
        };
        vm.SolveFunc = (request, _) =>
            new RailDcRunResult(null, [], [.. request.Document.Rails.Select(r => r.Name)], []);
        vm.PartLibrary = LibraryOf(BulkRow(), Ceramic100nRow());
        vm.Board = new RailBoardInputs { Shapes = [], Technology = TechWithGround() };
        vm.ConfirmReferenceCommand.Execute(null);
        Assert.True(vm.CanRun, vm.RunBlockedReason);
        return vm;
    }

    /// <summary>
    /// <b>Add Marker offers the curves only</b> (owner-reported, 2026-09-19 — the context menu
    /// offered many rows that are not traces a marker belongs on).
    /// </summary>
    /// <remarks>
    /// The submenu is one row per trace, so on this plot eleven of the thirteen were the mask edge
    /// and the aggressor lines — annotation, drawn as traces only because §11.1 forbids a bespoke
    /// chart, and burying the two rows that answer anything. Same flag as the NaN readout above,
    /// because it is the same fact about the same traces.
    ///
    /// <para><b>The double-click path is asserted with it.</b> Filtering the menu alone would leave
    /// a double-click near an aggressor line putting a marker on a trace the menu deliberately does
    /// not offer — one gesture disagreeing with another. A source scan is the gate because there is
    /// no headless Avalonia host in this project to open a real menu on; the comments are stripped
    /// first, so a rule that exists only in a comment does not pass.</para>
    /// </remarks>
    [Fact]
    public void AddMarkerOffersTheCurvesOnly_AndSoDoesTheDoubleClick()
    {
        var vm = Window();
        vm.RunCommand.Execute(null);

        // What the submenu would list, by the rule the control applies.
        var offered = vm.ImpedancePlot.Traces.Where(t => !t.IsAnnotation).ToList();
        _output.WriteLine(string.Join(" · ", vm.ImpedancePlot.Traces.Select(
            t => (t.IsAnnotation ? "-" : "+") + t.CubeName)));

        Assert.All(offered, t => Assert.Equal("Z", t.CubeName));
        Assert.True(vm.ImpedancePlot.Traces.Count - offered.Count >= 5,
                    "not enough annotation traces to be exercising the defect.");

        string code = StripComments(System.IO.File.ReadAllText(System.IO.Path.Combine(
            RepoRoot(), "src", "Ui", "DataDisplay", "Controls", "PlotControl.cs")));

        Assert.Contains("IsAnnotation", Body(code, "private void RefreshAddMarkerSubmenu()"),
                        StringComparison.Ordinal);
        Assert.Contains("IsAnnotation", Body(code, "private bool TryAddMarkerNearPoint(Point canvasPt)"),
                        StringComparison.Ordinal);
    }

    /// <summary>The body of one method, by brace matching from its signature.</summary>
    private static string Body(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' is no longer in the source — the scan is testing nothing.");

        int open = code.IndexOf('{', at);
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[open..i];
        }
        return "";
    }

    private static string StripComments(string src)
    {
        src = System.Text.RegularExpressions.Regex.Replace(
            src, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        return System.Text.RegularExpressions.Regex.Replace(src, @"//[^\n]*", "");
    }

    /// <summary>
    /// <b>The curve and its own mask are in the same decibel</b> (owner, 2026-09-19, found while
    /// making the unit settable).
    /// </summary>
    /// <remarks>
    /// The curve was authored as <c>dB(Z[…])</c>, and <c>dB</c> in this vocabulary is
    /// <c>10·log₁₀</c> — a POWER decibel — while the mask edge beside it was built with
    /// <c>20·log₁₀</c>. Measured on this fixture: a 200 mΩ target drew at −13.979 and a curve at
    /// 0.311 Ω drew at −5.076 rather than −10.152, so a design sitting exactly on its ceiling read
    /// as seven decibels clear of it. The mask VERDICT is computed in ohms by <c>PdnSweep</c> and
    /// was always right — it was the picture that disagreed with it.
    /// </remarks>
    [Fact]
    public void TheCurveAndItsMaskAreInTheSameDecibel()
    {
        var vm = Window();
        vm.RunCommand.Execute(null);

        var curve = vm.ImpedancePlot.Traces.First(t => t.CubeName == "Z");
        var mask  = vm.ImpedancePlot.Traces.First(
            t => t.CubeName!.StartsWith("target(", StringComparison.Ordinal));

        double limitOhms = vm.Sweep!.Ports[0].Mask!.Points[0].LimitOhms;
        Assert.Equal(20 * Math.Log10(limitOhms), mask.Points[0].Y, 3);

        // The curve's own value in ohms, read off the same trace — so the comparison is of the two
        // CONVENTIONS and not of two different numbers.
        double db = curve.Points[0].Y;
        curve.Transform = CubeTransform.Mag;
        curve.BuildPath(PlotType.Rect, FreqUnit.MHz);
        double ohms = curve.Points[0].Y;

        _output.WriteLine($"|Z|={ohms:0.#####} Ω drew at {db:0.###} dB; " +
                          $"20·log10={20 * Math.Log10(ohms):0.###}, 10·log10={10 * Math.Log10(ohms):0.###}");
        Assert.Equal(20 * Math.Log10(ohms), db, 3);
    }

    /// <summary>
    /// <b>The Y unit is settable, the target follows it, and a re-solve does not undo any of it.</b>
    /// </summary>
    /// <remarks>
    /// Owner, 2026-09-19: the trace is listed as dB and there is no way to see it in ohms; offer the
    /// Plot Inspector, do not let the impedance traces be deleted, do not show the target traces as
    /// cards, and make those targets adapt to any scale the impedance traces are given.
    ///
    /// <para>The last clause of that is the hard one and it is why the mask carries raw OHMS with a
    /// transform rather than a baked <c>20·log₁₀</c>. The re-solve half is the other: this plot is
    /// rebuilt from the sweep on every committed edit, so without carrying state across it the unit
    /// picked in the panel would last until the next keystroke.</para>
    /// </remarks>
    [Fact]
    public void TheYUnitIsSettableInTheInspector_TheTargetFollowsIt_AndAReSolveKeepsIt()
    {
        var vm = Window();
        vm.RunCommand.Execute(null);

        var inspector = vm.ImpedanceContainer.Inspector;

        // ── The cards are the CURVES, and none of them can be removed ──────────────────────────
        Assert.Equal(vm.ImpedancePlot.Traces.Count(t => t.CubeName == "Z"), inspector.Traces.Count);
        Assert.All(inspector.Traces, c => Assert.Equal("Z", c.Trace.CubeName));
        Assert.All(inspector.Traces, c => Assert.False(c.CanRemove));
        Assert.False(inspector.CanEditTraceSet);
        Assert.False(inspector.CanAddTrace);

        var curve = vm.ImpedancePlot.Traces.First(t => t.CubeName == "Z");
        double limitOhms = vm.Sweep!.Ports[0].Mask!.Points[0].LimitOhms;

        Mask(vm, out double maskDb);
        Assert.Equal(20 * Math.Log10(limitOhms), maskDb, 3);
        Assert.Equal("|Z| (dBΩ)", vm.ImpedancePlot.CustomYLabel);

        // ── Ohms, as the trace card sets it ───────────────────────────────────────────────────
        curve.Transform = CubeTransform.Mag;
        inspector.RebuildAndNotify();

        // The curve still HAS data. It is re-resolved from its source on every inspector edit, and
        // this window has no data-source library — before RailPlotDataSources that emptied it.
        Assert.NotEmpty(curve.Points);

        Mask(vm, out double maskOhms);
        Assert.Equal(limitOhms, maskOhms, 6);                       // the target adapted
        Assert.Equal("|Z| (Ω)", vm.ImpedancePlot.CustomYLabel);

        // ── And a marker and a chosen style, to be carried ─────────────────────────────────────
        curve.Markers.Add(new Marker(curve, curve.Points[0].X, isMulti: false, isDelta: false,
                                     index: 1, FreqUnit.MHz));
        curve.Properties.LineWidth = 4.0;                           // raises Properties.Custom
        Assert.True(curve.Properties.Custom);

        // ── A re-solve: what every committed edit does ────────────────────────────────────────
        vm.RunCommand.Execute(null);

        var rebuilt = vm.ImpedancePlot.Traces.First(t => t.CubeName == "Z");
        Assert.NotSame(curve, rebuilt);                             // it really is a new trace
        Assert.Equal(CubeTransform.Mag, rebuilt.Transform);
        Assert.Equal(4.0, rebuilt.Properties.LineWidth);
        Assert.Single(rebuilt.Markers);
        Assert.Equal("|Z| (Ω)", vm.ImpedancePlot.CustomYLabel);

        Mask(vm, out double maskAfter);
        Assert.Equal(limitOhms, maskAfter, 6);
    }

    /// <summary>
    /// <b>An aggressor line spans the window and never sets it.</b>
    /// </summary>
    /// <remarks>
    /// A vertical line is a two-point trace cut TO the Y window, so with it in the autoscale the
    /// window it was drawn from becomes the window it produces and every curve is squashed to
    /// nothing. That used to be held by ORDER — added after the autoscale, in capitals in the file —
    /// which works exactly until something else autoscales, and the Plot Inspector does on every
    /// edit. <c>Trace.ExcludeFromAutoscale</c> is what holds it now.
    /// </remarks>
    [Fact]
    public void AnAggressorLineSpansTheWindowAndNeverSetsIt()
    {
        var vm = Window();
        vm.RunCommand.Execute(null);

        var lines = vm.ImpedancePlot.Traces.Where(t => t.ExcludeFromAutoscale).ToList();
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.StartsWith("converter × ", l.CubeName!, StringComparison.Ordinal));

        var window = vm.ImpedancePlot.Axes.Window;
        double lo = Math.Min(window.Top, window.Bottom);
        double hi = Math.Max(window.Top, window.Bottom);
        Assert.All(lines, l =>
        {
            Assert.Equal(lo, Math.Min(l.Points[0].Y, l.Points[1].Y), 3);
            Assert.Equal(hi, Math.Max(l.Points[0].Y, l.Points[1].Y), 3);
        });

        // The half that order alone could not hold: autoscale again, with the lines already on the
        // plot, and the window must not have walked outwards.
        vm.ImpedancePlot.Autoscale(force: true);
        Assert.Equal(lo, Math.Min(vm.ImpedancePlot.Axes.Window.Top,
                                  vm.ImpedancePlot.Axes.Window.Bottom), 3);
        Assert.Equal(hi, Math.Max(vm.ImpedancePlot.Axes.Window.Top,
                                  vm.ImpedancePlot.Axes.Window.Bottom), 3);
    }

    /// <summary>The Y of the first mask point currently on the plot.</summary>
    private static void Mask(RailRfViewModel vm, out double y) =>
        y = vm.ImpedancePlot.Traces
              .First(t => t.CubeName!.StartsWith("target(", StringComparison.Ordinal))
              .Points[0].Y;

    /// <summary>
    /// <b>The panel restyles this plot and cannot re-aim it.</b>
    /// </summary>
    /// <remarks>
    /// Owner, 2026-09-19: the plot type, <c>vs X</c>, the "(load a file…)" combo and the quantity
    /// picker beside it are not used for railRF and just take up space — on the narrowest panel in
    /// the window, every one of them empty. They are one fact, not four: each re-aims a trace (or
    /// the whole plot) at something the next re-solve aims straight back, because this plot is a
    /// READ-OUT rebuilt from the sweep on every committed edit. <c>Plot.IsFixedReadout</c> is that
    /// fact; what it deliberately leaves alone is everything about how a trace LOOKS, which is why
    /// the panel opens at all.
    ///
    /// <para>Hidden rather than disabled throughout — a permanently grey control is one the user
    /// goes on trying, and these are comboboxes that would sit empty at the width of the panel.</para>
    /// </remarks>
    [Fact]
    public void ThePanelRestylesThisPlotAndCannotReAimIt()
    {
        var vm = Window();
        vm.RunCommand.Execute(null);

        var inspector = vm.ImpedanceContainer.Inspector;
        Assert.True(vm.ImpedancePlot.IsFixedReadout);

        // The plot is the kind it is, and the trace set is the window's.
        Assert.False(inspector.CanChangePlotType);
        Assert.False(inspector.CanEditTraceSet);
        Assert.False(inspector.CanAddTrace);

        var card = inspector.Traces.First();
        Assert.False(card.CanRemove);              // no trash
        Assert.False(card.CanPickTraceData);       // no re-aiming
        Assert.False(card.ShowIdentityRow);        // "(load a file…)" + the quantity picker
        Assert.False(card.ShowVersusRow);          // vs X
        Assert.False(card.SourceSelectorVisible);  // the source combo

        // And what the panel is OPEN for is untouched.
        Assert.True(card.ShowTransformCombo);
        Assert.True(card.IsTransformComboEnabled);

        // ── The Data Display's own plots keep every one of them ───────────────────────────────
        var display = new CircuitRF.Ui.DataDisplay.ViewModels.DataDisplayViewModel(
            new CircuitRF.Ui.DataDisplay.ViewModels.DataSourceLibraryViewModel(),
            addEmptyPlot: false, selectEmptyPlot: false);
        var ordinary = display.AddPlot(PlotType.Rect, FreqUnit.GHz, 0, 0, 100, 100).Inspector;
        Assert.True(ordinary.CanChangePlotType);
        Assert.True(ordinary.CanEditTraceSet);
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !System.IO.File.Exists(System.IO.Path.Combine(dir, "circuitRF.slnx")))
            dir = System.IO.Path.GetDirectoryName(dir) ?? "";
        return dir;
    }

    /// <summary>A stackup whose second conductor is marked as the ground reference — the only thing
    /// the window's own reference proposal reads.</summary>
    private static CircuitRF.Design.Layout.Technology TechWithGround()
    {
        var tech = new CircuitRF.Design.Layout.Technology { Name = "board" };
        tech.Layers.Add(new CircuitRF.Design.Layout.LayerDef
            { Key = new CircuitRF.Design.Layout.LayerKey(1, 0), Name = "L1" });
        tech.Layers.Add(new CircuitRF.Design.Layout.LayerDef
            { Key = new CircuitRF.Design.Layout.LayerKey(2, 0), Name = "L2" });
        tech.Stackup.Layers.Add(new CircuitRF.Design.Layout.StackupLayer
        {
            Kind = CircuitRF.Design.Layout.StackupKind.Conductor, Name = "L1",
            DrawingLayers = [new CircuitRF.Design.Layout.LayerKey(1, 0)],
        });
        tech.Stackup.Layers.Add(new CircuitRF.Design.Layout.StackupLayer
        {
            Kind = CircuitRF.Design.Layout.StackupKind.Conductor, Name = "L2", IsGroundReference = true,
            DrawingLayers = [new CircuitRF.Design.Layout.LayerKey(2, 0)],
        });
        return tech;
    }

    // ── §7's acceptance anchor ───────────────────────────────────────────────

    /// <summary>
    /// <b>§7's acceptance anchor, and the only gate in this series that is neither our arithmetic nor
    /// a closed form:</b> a published measured PDN from the open SI literature, reproduced within the
    /// tolerance that literature states.
    ///
    /// <para><b>It SKIPS until the reference lands, and that is deliberate.</b> The fixture has to be
    /// a digitised curve somebody measured, with the source cited and the tolerance taken from the
    /// source rather than chosen by us. Synthesising one here would turn the one external gate in
    /// this brief into another piece of our own arithmetic agreeing with itself — which is exactly
    /// the failure mode §7 and the PRD's "externally generated" rule exist to prevent, and the same
    /// reason Q-18's reader gates ship in two tiers.</para>
    ///
    /// <para>The fixture is <c>testdata/railrf/measured-pdn/</c>: the board's parts, sources and
    /// stackup as a <c>.crail</c>, the digitised |Z(f)| as a two-column file, and a <c>SOURCE.md</c>
    /// naming the publication and quoting its stated tolerance.</para>
    /// </summary>
    [FixtureFact("testdata/railrf/measured-pdn",
                 "the published measured-PDN reference of railrf.md §7 has not been committed yet. " +
                 "It needs a digitised |Z(f)| from the open SI literature, the board it was measured " +
                 "on as a .crail, and the tolerance that publication states — not one we chose.")]
    public void TheAcceptanceAnchor_ReproducesAPublishedMeasuredPdnWithinItsStatedTolerance()
    {
        string dir = FixturePaths.Require("testdata/railrf/measured-pdn");

        var document = RailDocumentIo.LoadFromFile(System.IO.Path.Combine(dir, "board.crail"));
        Assert.Null(document.Refusal());

        var (frequenciesHz, measuredOhms, toleranceDb) = ReadReference(
            System.IO.Path.Combine(dir, "measured.txt"));

        var rail = document.Rails[0];
        var library = PartLibraryIo.LoadFromFile(System.IO.Path.Combine(dir, "parts.json"));

        var result = PdnSweep.Run(new PdnSweepRequest
        {
            Rail          = rail,
            Parts         = new RailPartResolver(library).ResolveAll(rail.Parts, rail.NominalVoltageV),
            Sources       = [.. rail.Sources.Select((s, i) => RailSourceLife.Of(s, i))],
            FrequenciesHz = frequenciesHz,
            RankRemovals  = false,
        });

        Assert.Null(result.Refusal);

        var modelled = result.Ports[0].MagnitudeOhms;
        for (int i = 0; i < frequenciesHz.Length; i++)
        {
            double errorDb = Math.Abs(20 * Math.Log10(modelled[i] / measuredOhms[i]));
            Assert.True(errorDb <= toleranceDb,
                $"{frequenciesHz[i] / 1e6:0.###} MHz: modelled {modelled[i] * 1e3:0.###} mΩ against " +
                $"measured {measuredOhms[i] * 1e3:0.###} mΩ — {errorDb:0.##} dB, over the " +
                $"publication's stated {toleranceDb:0.##} dB.");
        }
    }

    /// <summary>
    /// The digitised curve, and the tolerance <b>the publication states</b> — read from the file's
    /// own header rather than chosen here, which is what §7 means by an external gate.
    /// </summary>
    private static (double[] Hz, double[] Ohms, double ToleranceDb) ReadReference(string path)
    {
        var hz = new List<double>();
        var ohms = new List<double>();
        double tolerance = double.NaN;

        foreach (string line in System.IO.File.ReadLines(path))
        {
            string text = line.Trim();
            if (text.Length == 0) continue;

            if (text.StartsWith('#'))
            {
                int at = text.IndexOf("tolerance_db=", StringComparison.OrdinalIgnoreCase);
                if (at >= 0)
                    tolerance = double.Parse(
                        text[(at + "tolerance_db=".Length)..].Split(' ')[0],
                        System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }

            var fields = text.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
            hz.Add(double.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture));
            ohms.Add(double.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.False(double.IsNaN(tolerance),
            $"{path} states no '# tolerance_db=' header. The tolerance is the publication's, and a " +
            "reference file that does not carry it cannot gate anything.");

        return ([.. hz], [.. ohms], tolerance);
    }
}
