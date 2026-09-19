// ================================================================
//  RailPartModelTests.cs — brief-railrf-11-part-models.md §6
//
//  The arithmetic that turns a library row or a vendor file into an element the mesh can carry over
//  frequency, and the MARKING that keeps a defaulted number from reading like a measured one.
//  Pure model tests: no window, no app host, no Avalonia. Everything under test lives in
//  src/Design/RailRf, below the UI firewall.
//
//  ONE TEST PER CLAIM THE BRIEF MAKES, not one per dielectric class and not one per measured rung.
//  The brief says so itself: "Do not table-drive every dielectric class — two rows straddle the
//  behaviour and the rest is the same arithmetic."
//
//  The Touchstone fixture is SYNTHETIC and built in memory: a series R-L-C in the shunt-through
//  fixture, whose S-matrix has a closed form (S21 = 2Z/(2Z + Z0)). That makes the file path testable
//  with no disk and — more usefully — with a known answer, so a test that passes proves railRF read
//  the part the vendor measured rather than proving two copies of our own code agree.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.RailRf;
using NumFlat;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public class RailPartModelTests
{
    // ── fixtures ─────────────────────────────────────────────────────────────

    /// <summary>§2.2's own first row: a 1 µF part resonating at 5.31 MHz.</summary>
    private static PartLibraryRow Ceramic1u() => new()
    {
        PartNumber              = "PN-1U-0402",
        DielectricClass         = "X7R",
        VoltageRatingV          = 16,
        CapacitanceFarads       = 1e-6,
        SelfResonantFrequencyHz = 5.31e6,
    };

    /// <summary>§2.2's second: a 33 nF part at 39.1 MHz.</summary>
    private static PartLibraryRow Ceramic33n() => new()
    {
        PartNumber              = "PN-33N-0402",
        DielectricClass         = "X7R",
        CapacitanceFarads       = 33e-9,
        SelfResonantFrequencyHz = 39.1e6,
    };

    /// <summary>§9's own part: "a 10 µF 0402 X5R can be under 2 µF at its rated voltage."</summary>
    private static PartLibraryRow Ceramic10u(bool withCurve)
    {
        var row = new PartLibraryRow
        {
            PartNumber              = "PN-10U-0402",
            DielectricClass         = "X5R",
            VoltageRatingV          = 6.3,
            CapacitanceFarads       = 10e-6,
            SelfResonantFrequencyHz = 2.0e6,
        };
        if (withCurve)
        {
            row.BiasCurve.Add(new PartBiasPoint(0.0, 10e-6));
            row.BiasCurve.Add(new PartBiasPoint(3.3, 3.0e-6));
            row.BiasCurve.Add(new PartBiasPoint(6.3, 1.8e-6));
        }
        return row;
    }

    private static PartLibrary LibraryOf(params PartLibraryRow[] rows)
    {
        var lib = new PartLibrary();
        lib.Rows.AddRange(rows);
        return lib;
    }

    /// <summary>
    /// A series R-L-C measured in the shunt-through fixture, as an in-memory Touchstone sweep.
    ///
    /// <para>Shunt Z across a through line of reference Z0: <c>S21 = 2Z/(2Z + Z0)</c> and
    /// <c>S11 = −Z0/(2Z + Z0)</c>. Exactly what <see cref="PassiveMetrics.ShuntThrough"/> inverts,
    /// so the impedance railRF recovers is the one this built.</para>
    /// </summary>
    private static SNP ShuntThroughSnp(double esrOhms, double henries, double farads,
                                       double lowHz = 1e4, double highHz = 2e8, int points = 801)
    {
        var freqs = new double[points];
        double ratio = Math.Log(highHz / lowHz) / (points - 1);
        for (int i = 0; i < points; i++) freqs[i] = lowHz * Math.Exp(i * ratio);

        var z0 = new Complex(50, 0);
        var mats = new Mat<Complex>[points];
        for (int i = 0; i < points; i++)
        {
            double w = 2 * Math.PI * freqs[i];
            var z = new Complex(esrOhms, w * henries - 1.0 / (w * farads));

            var s21 = 2 * z / (2 * z + z0);
            var s11 = -z0 / (2 * z + z0);

            var m = new Mat<Complex>(2, 2);
            m[0, 0] = s11; m[0, 1] = s21;
            m[1, 0] = s21; m[1, 1] = s11;
            mats[i] = m;
        }

        return new SNP(freqs, mats, z0: z0);
    }

    // ── R-rail11-1: the model carries the derived inductance ─────────────────

    [Fact]
    public void R_rail11_1_TheModelCarriesTheInductanceDerivedFromCAndF0()
    {
        // The note's own two rows. Brief 2 tests the READER's derivation; this tests that the MODEL
        // carries it — L = 1/((2·π·f0)²·C), exactly, never a fit and never asked for twice.
        var resolver = new RailPartResolver(LibraryOf(Ceramic1u(), Ceramic33n()));

        var oneMicro = resolver.Resolve("PN-1U-0402", railVoltageV: null);
        var thirtyThreeNano = resolver.Resolve("PN-33N-0402", railVoltageV: null);

        Assert.Equal(898e-12, oneMicro.InductanceHenries!.Value, 1e-12);
        Assert.Equal(502e-12, thirtyThreeNano.InductanceHenries!.Value, 1e-12);

        Assert.Equal(RailInductanceBasis.DerivedFromResonance, oneMicro.InductanceBasis);
        Assert.Equal(RailInductanceBasis.DerivedFromResonance, thirtyThreeNano.InductanceBasis);
    }

    // ── R-rail11-2: the file overrides the row, and the ESR is Measured ──────

    [Fact]
    public void R_rail11_2_AnAttachedTouchstoneSuppliesCLAndARealEsrAndTheRowsOwnCIsNotUsed()
    {
        // The row states a capacitance that is WRONG BY A DECADE on purpose: Q-11 closed this as the
        // file overriding the row, so a model that quietly averaged the two — or preferred the row —
        // would still produce a plausible number here and only this fixture can tell.
        var row = Ceramic1u();
        row.CapacitanceFarads = 100e-9;
        row.ModelRef = "PN-1U-0402.s2p";

        var snp = ShuntThroughSnp(esrOhms: 5e-3, henries: 898e-12, farads: 1e-6);

        var resolver = new RailPartResolver(LibraryOf(row))
        {
            FileReader        = _ => snp,
            MeasureFileHealth = false,
        };

        var model = resolver.Resolve("PN-1U-0402", railVoltageV: 3.3);

        Assert.Equal(PartModelSource.AttachedFile, model.Source);
        Assert.Equal(EsrProvenance.Measured, model.EsrBasis);
        Assert.False(model.IsIndicative);

        // C, L and a REAL ESR, all the part's own.
        Assert.Equal(1e-6,    model.CapacitanceFarads,          1e-9);
        Assert.Equal(898e-12, model.InductanceHenries!.Value,   9e-12);
        Assert.Equal(5e-3,    model.EsrOhms,                    1e-6);
        Assert.Equal(RailCapacitanceBasis.Measured, model.Capacitance.Basis);

        // The row's 100 nF is nowhere in the answer.
        Assert.True(model.CapacitanceFarads > 9e-7);

        // And the self-resonance came out of the file's own reactance crossing.
        Assert.Equal(5.31e6, model.Measured!.SelfResonanceHz!.Value, 5e4);
    }

    // ── R-rail11-3: the class default IS the basis; no class means no ESR ────

    [Fact]
    public void R_rail11_3_AClassDefaultEsrIsDfOverTwoPiFC_AndAPartWithNoClassGetsNone()
    {
        var classed   = Ceramic1u();                     // X7R
        var unclassed = Ceramic33n();
        unclassed.DielectricClass = null;                 // brief 2's parse leaves it null and it stays null

        var set = new RailPartResolver(LibraryOf(classed, unclassed))
            .ResolveAll(new[] { classed.PartNumber, unclassed.PartNumber }, railVoltageV: null);

        var withClass = set.Models[0];
        Assert.Equal(EsrProvenance.ClassDefault, withClass.EsrBasis);
        Assert.True(withClass.IsIndicative);

        double df = RailEsrDefaults.DissipationFactors["X7R"];
        Assert.Equal(df, withClass.DissipationFactor!.Value, 1e-12);

        // ESR = DF/(2·π·f·C), at the frequency in question and nowhere else.
        foreach (double f in new[] { 1e5, 1e6, 5.31e6 })
            Assert.Equal(df / (2 * Math.PI * f * 1e-6), withClass.EsrOhmsAt(f), 1e-12);

        // A part with no dielectric class gets NO default — it is counted and reported, not given
        // X7R's number.
        var withoutClass = set.Models[1];
        Assert.Null(withoutClass.EsrBasis);
        Assert.True(double.IsNaN(withoutClass.EsrOhms));
        Assert.Contains(withoutClass.Name, set.WithoutEsrBasis);
        Assert.Contains(set.Warnings, w => w.Contains("no dielectric class"));
    }

    // ── R-rail11-4: the flag survives the DataSet boundary ───────────────────

    [Fact]
    public void R_rail11_4_TheIndicativeFlagSurvivesOntoTheResultAtTheDataSetBoundary()
    {
        // §9's five places are the part row, the plot, the anti-resonance table, the mask-margin
        // readout and the provenance of every export. Only the first reads a RailPartModel; the
        // other four read the result, so the flag is asserted where it would be dropped.
        var indicative = Ceramic1u();                    // X7R, no file → class default
        var measuredRow = Ceramic33n();
        measuredRow.ModelRef = "PN-33N-0402.s2p";

        var resolver = new RailPartResolver(LibraryOf(indicative, measuredRow))
        {
            FileReader        = _ => ShuntThroughSnp(5e-3, 502e-12, 33e-9),
            MeasureFileHealth = false,
        };

        var set = resolver.ResolveAll(
            new[] { indicative.PartNumber, measuredRow.PartNumber }, railVoltageV: 3.3);

        Assert.True(set.AnyIndicative);
        Assert.NotNull(set.IndicativeLine);
        Assert.Contains(RailEsrDefaults.Marking, set.IndicativeLine);

        var data = new DataSet();
        set.Annotate(data);

        var cube = data[RailPartModelSet.EsrBasisCube];
        var axis = cube.Axis("part");

        Assert.Equal(2, axis.Length);
        Assert.Equal(set.Models[0].Name, axis.Labels![0]);

        // 2 is ClassDefault — the indicative code — and it is readable from the DataSet alone.
        Assert.Equal((double)(int)EsrProvenance.ClassDefault, cube.RealValues[0]);
        Assert.Equal((double)(int)EsrProvenance.Measured,     cube.RealValues[1]);
    }

    // ── R-rail11-5: the same per-class figure serves the board dielectric ────

    [Fact]
    public void R_rail11_5_AStackupWithNoTanDeltaTakesTheClassFigureAndIsFlagged()
    {
        var unstated = new StackupLayer { Kind = StackupKind.Dielectric, Name = "FR-4 core", TanD = 0 };
        var stated   = new StackupLayer { Kind = StackupKind.Dielectric, Name = "FR-4 core", TanD = 0.015 };

        var defaulted = RailEsrDefaults.BoardLossTangent(unstated);
        Assert.True(defaulted.IsClassDefault);
        Assert.Equal(RailEsrDefaults.GeneralPurposeLaminateTanDelta, defaulted.TanDelta, 1e-12);
        Assert.Equal("FR-4", defaulted.ClassUsed);
        Assert.Contains(RailEsrDefaults.Marking, defaulted.Describe());

        var own = RailEsrDefaults.BoardLossTangent(stated);
        Assert.False(own.IsClassDefault);
        Assert.Equal(0.015, own.TanDelta, 1e-12);
        Assert.DoesNotContain(RailEsrDefaults.Marking, own.Describe());
    }

    // ── R-rail11-6: both headline counts, over a mixed library ───────────────

    [Fact]
    public void R_rail11_6_TheFileModelledCountAndTheNoCurveCountAreBothCorrect()
    {
        var fromFile = Ceramic1u();
        fromFile.ModelRef = "PN-1U-0402.s2p";

        var withCurve    = Ceramic10u(withCurve: true);
        var withoutCurve = Ceramic33n();

        var resolver = new RailPartResolver(
            LibraryOf(fromFile, withCurve, withoutCurve))
        {
            FileReader        = _ => ShuntThroughSnp(5e-3, 898e-12, 1e-6),
            MeasureFileHealth = false,
        };

        var set = resolver.ResolveAll(
            new[] { fromFile.PartNumber, withCurve.PartNumber, withoutCurve.PartNumber, "PN-NOT-HERE" },
            railVoltageV: 3.3);

        Assert.Equal(1, set.ModelledFromFile);

        // A file-modelled part is not derated and is not counted as missing a curve — the file
        // states the part's capacitance at whatever bias it was measured at, and Q-12's curve is not
        // applied on top of it.
        Assert.Equal(new[] { withoutCurve.PartNumber }, set.WithoutBiasCurve);

        Assert.Single(set.Unresolved);
        Assert.Equal("PN-NOT-HERE", set.Unresolved[0].PartNumber);
        Assert.False(set.Unresolved[0].IsResolved);
    }

    // ── R-rail11-7: marked, derated, and which was used — all three ──────────

    [Fact]
    public void R_rail11_7_DeratingReturnsAllThreeNumbers_AndAPartWithNoCurveWarnsAndIsCounted()
    {
        var withCurve    = Ceramic10u(withCurve: true);
        var withoutCurve = Ceramic10u(withCurve: false);
        withoutCurve.PartNumber = "PN-10U-0402-NOCURVE";

        var set = new RailPartResolver(LibraryOf(withCurve, withoutCurve))
            .ResolveAll(new[] { withCurve.PartNumber, withoutCurve.PartNumber }, railVoltageV: 3.3);

        // All three, asserted separately — a function returning one number is a function whose
        // caller cannot show the other two.
        var derated = set.Models[0].Capacitance;
        Assert.Equal(10e-6, derated.MarkedFarads,          1e-12);
        Assert.Equal(3.0e-6, derated.DeratedFarads!.Value, 1e-12);
        Assert.Equal(3.0e-6, derated.UsedFarads,           1e-12);
        Assert.Equal(RailCapacitanceBasis.Derated, derated.Basis);

        // A part with no curve uses MARKED, warns, and increments the count.
        var marked = set.Models[1].Capacitance;
        Assert.Equal(10e-6, marked.MarkedFarads, 1e-12);
        Assert.Null(marked.DeratedFarads);
        Assert.Equal(10e-6, marked.UsedFarads, 1e-12);
        Assert.Equal(RailCapacitanceBasis.Marked, marked.Basis);
        Assert.NotNull(marked.Warning);
        Assert.Equal(new[] { withoutCurve.PartNumber }, set.WithoutBiasCurve);
    }

    // ── R-rail11-8: the mounting loop is typed in P1, and it comes from the document ──

    [Fact]
    public void R_rail11_8_ATypedMountingInductanceRoundTripsAndSetsTheMountedResonance()
    {
        // Q-9's own arithmetic is the oracle: "a 470 µF bulk with 5 nH of mounting self-resonates
        // near 104 kHz." Nothing else in railRF would catch a mounting inductance that was carried
        // but not USED.
        // The row states C and no f0, which is ordinary for a bulk part and is what makes this
        // fixture Q-9's own: the MOUNTING LOOP is then the whole of the inductance.
        var bulk = new PartLibraryRow
        {
            PartNumber        = "PN-470U-BULK",
            DielectricClass   = "ELECTROLYTIC",
            CapacitanceFarads = 470e-6,
        };

        var doc = new RailDocument();
        var rail = new RailSpec { Name = "+3V3" };
        rail.Sources.Add(new RailSource
        {
            Anchor              = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
            OpenCircuitVoltageV = 3.3,
            SeriesResistanceOhms = 1.0,
        });
        rail.Parts.Add(new RailPart
        {
            Refdes                    = "C1",
            PartNumber                = bulk.PartNumber,
            MountingInductanceHenries = 5e-9,
        });
        doc.Rails.Add(rail);

        var reread = RailDocumentIo.Deserialize(RailDocumentIo.Serialize(doc));
        var part = reread.Rails[0].Parts[0];
        Assert.Equal(5e-9, part.MountingInductanceHenries!.Value, 1e-15);
        Assert.Equal(3.3, reread.Rails[0].NominalVoltageV!.Value, 1e-12);

        var model = new RailPartResolver(LibraryOf(bulk))
            .Resolve(part, reread.Rails[0].NominalVoltageV);

        Assert.Equal(RailMountingBasis.Typed, model.MountingBasis);

        // The mounting loop IS this part's total inductance here, and the mounted resonance is the
        // one Q-9 quotes: "a 470 µF bulk with 5 nH of mounting self-resonates near 104 kHz."
        Assert.Equal(5e-9, model.TotalInductanceHenries!.Value, 1e-15);
        Assert.Equal(104e3, model.SelfResonanceHz!.Value, 1e3);
    }

    // ── R-rail11-9: the life sweep, and what an R-L source does not claim ────

    [Fact]
    public void R_rail11_9_TheLifeSweepIsOneModelPerR_AndAConverterAsRlSaysItIsOptimistic()
    {
        var battery = RailSourceLife.Of(
            new RailSource
            {
                Anchor               = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
                OpenCircuitVoltageV  = 3.0,
                SeriesResistanceOhms = 1.0,
                SeriesInductanceHenries = 20e-9,
            },
            index: 0);

        var life = RailSourceLife.Sweep(battery);
        Assert.Equal(RailSourceLife.DefaultOhms.Count, life.Count);
        Assert.Equal(RailSourceLife.DefaultOhms, life.Select(m => m.SeriesResistanceOhms!.Value));

        // Every point keeps everything else, and the R-L is what it says it is at one frequency.
        var mid = life[2];
        Assert.Equal(10.0, mid.ImpedanceAt(1e6).Real, 1e-12);
        Assert.Equal(2 * Math.PI * 1e6 * 20e-9, mid.ImpedanceAt(1e6).Imaginary, 1e-12);

        // A converter modelled as R-L states that the answer near its loop crossover is optimistic —
        // a sentence on the result, not a log line.
        string? line = mid.OptimisticLine(isConverter: true);
        Assert.NotNull(line);
        Assert.Contains("LOOP CROSSOVER", line);

        // A battery is not a converter, and a measured curve is not swept.
        Assert.Null(mid.OptimisticLine(isConverter: false));

        var measured = battery with
        {
            Basis    = RailSourceBasis.Measured,
            Measured = new RailMeasuredPart("vrm.s1p", [1e4, 1e8], [new Complex(0.1, 0), new Complex(0.1, 1)], null),
        };
        Assert.Single(RailSourceLife.Sweep(measured));
        Assert.Null(measured.OptimisticLine(isConverter: true));
    }
}
