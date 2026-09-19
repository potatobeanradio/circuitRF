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

        Assert.Contains("Fast model", vm.ImpedancePlot.CustomTitle, StringComparison.Ordinal);
        Assert.Contains("Accuracy", vm.ImpedancePlot.CustomTitle, StringComparison.Ordinal);

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
