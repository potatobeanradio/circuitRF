// ================================================================
//  TransientSeedSurfaceTests.cs — brief TRN-8: transient-assisted harmonic balance, on the surfaces.
//
//  Gate 5: the shipped VCO (Receiver Noise ▸ 7 VCO Phase Noise) without Fguess finds the frequency and amplitude of the run
//  with it. The keys through .cnl, .csch and the two cards, and a directive without them written as before (gate 6's
//  file half).
// ================================================================

using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Ui.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Transient;

public sealed class TransientSeedSurfaceTests(ITestOutputHelper output)
{
    [Fact]
    public void Gate5_ShippedVco_WithoutFguess_FindsTheOscillationOfTheRunWithIt()
    {
        const string cell = "7 VCO Phase Noise";
        string csch = Path.Combine(Examples.ReceiverNoiseExampleTests.Workspace, cell, "schematic", cell + ".csch");
        var (model, _, _) = SchematicPersistence.LoadFromFile(csch);
        var (lib, tb) = new CnlReader().Read(SchematicCircuit.CnlTextOf(model, cell));
        var shipped = tb.Analyses.OfType<OscillatorAnalysis>().Single();
        Assert.False(string.IsNullOrWhiteSpace(shipped.FguessExpr));

        OscResult Run(OscillatorAnalysis a)
        {
            using var nl = new Elaborator(lib).Elaborate(tb);
            var r = OscEngine.Run(nl, tb, OscEngine.Resolve(a, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit));
            foreach (var n in nl.Notes) output.WriteLine("  " + n);
            return r;
        }
        OscillatorAnalysis Copy(bool seeded) => new(shipped.Name)
        {
            Probe = shipped.Probe, Unit = shipped.Unit, MaxHarmonicExpr = shipped.MaxHarmonicExpr, FFTOverSampleExpr = shipped.FFTOverSampleExpr,
            TolExpr = shipped.TolExpr, FguessExpr = seeded ? "" : shipped.FguessExpr, AguessExpr = seeded ? "" : shipped.AguessExpr,
            InitialGuess = seeded ? Analysis.InitialGuessTransient : Analysis.InitialGuessDc,
        };
        var withGuess = Run(Copy(seeded: false));
        var seeded = Run(Copy(seeded: true));
        output.WriteLine($"Fguess {shipped.FguessExpr} {shipped.Unit}: {withGuess.FoscHz:R} Hz, {withGuess.Aosc:R} V; " +
                         $"seeded: {seeded.FoscHz:R} Hz, {seeded.Aosc:R} V, Seed.Periods {seeded.Data["Seed.Periods"].RealValues[0]}");
        Assert.True(Math.Abs(seeded.FoscHz / withGuess.FoscHz - 1) < 1e-6);
        Assert.True(Math.Abs(seeded.Aosc / withGuess.Aosc - 1) < 1e-4);
        Assert.True(seeded.Data["Seed.Periods"].RealValues[0] > 0);
    }

    private const string Hb = "R:R1 a 0 R=50\nanalysis HB1 type=hb Tone=1 ToneUnit=GHz";
    private const string Osc = "OscProbe:X1 a 0 Mode=shunt\nR:R1 a 0 R=50\nanalysis OSC1 type=osc";

    [Fact]
    public void Keys_RoundTripThroughCnlCschAndTheCards_AndADefaultDirectiveWritesNone()
    {
        var (_, tb) = new CnlReader().Read(
            Hb + " InitialGuess=fallback SeedTol=1e-4 SeedPeriods=50 SeedFastBias=no\n" +
            Osc + " InitialGuess=transient SeedPeriods=80 KickV=0.2\n");
        var hb = tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single();
        var osc = tb.Analyses.OfType<OscillatorAnalysis>().Single();
        Assert.Equal(("fallback", "1e-4", "50", "no"), (hb.InitialGuess, hb.SeedTolExpr, hb.SeedPeriodsExpr, hb.SeedFastBias));
        Assert.Equal(("transient", "", "80", "0.2"), (osc.InitialGuess, osc.FguessExpr, osc.SeedPeriodsExpr, osc.KickVExpr));

        // .cnl: written and read back
        string text = CnlWriter.Write(tb);
        Assert.Contains("InitialGuess=fallback SeedTol=\"1e-4\" SeedPeriods=\"50\" SeedFastBias=no", text);
        Assert.DoesNotContain("Fguess", text);
        var (_, back) = new CnlReader().Read(text);
        var hb2 = back.Analyses.OfType<HarmonicBalanceAnalysis>().Single();
        var osc2 = back.Analyses.OfType<OscillatorAnalysis>().Single();
        Assert.Equal((hb.InitialGuess, hb.SeedTolExpr, hb.SeedPeriodsExpr, hb.SeedFastBias), (hb2.InitialGuess, hb2.SeedTolExpr, hb2.SeedPeriodsExpr, hb2.SeedFastBias));
        Assert.Equal((osc.InitialGuess, osc.FguessExpr, osc.SeedPeriodsExpr, osc.KickVExpr), (osc2.InitialGuess, osc2.FguessExpr, osc2.SeedPeriodsExpr, osc2.KickVExpr));

        // .csch
        var hb3 = (HarmonicBalanceAnalysis)AnalysisSerialization.FromDto(AnalysisSerialization.ToDto(hb))!;
        var osc3 = (OscillatorAnalysis)AnalysisSerialization.FromDto(AnalysisSerialization.ToDto(osc))!;
        Assert.Equal((hb.InitialGuess, hb.SeedTolExpr, hb.SeedPeriodsExpr, hb.SeedFastBias), (hb3.InitialGuess, hb3.SeedTolExpr, hb3.SeedPeriodsExpr, hb3.SeedFastBias));
        Assert.Equal((osc.InitialGuess, osc.FguessExpr, osc.SeedPeriodsExpr, osc.KickVExpr), (osc3.InitialGuess, osc3.FguessExpr, osc3.SeedPeriodsExpr, osc3.KickVExpr));

        // the cards: opened and built again, unchanged; an oscillator with no Fguess is valid only from a transient seed
        var model = new SchematicEditModel();
        var hb4 = HbBodyViewModel.FromAnalysis(hb, model).BuildAnalysis("HB1", true);
        Assert.Equal((hb.InitialGuess, hb.SeedTolExpr, hb.SeedPeriodsExpr, hb.SeedFastBias), (hb4.InitialGuess, hb4.SeedTolExpr, hb4.SeedPeriodsExpr, hb4.SeedFastBias));
        var oscVm = OscBodyViewModel.From(osc, model);
        Assert.True(oscVm.IsValid && oscVm.SeedKeysEnabled);
        var osc4 = oscVm.BuildAnalysis("OSC1", true);
        Assert.Equal((osc.InitialGuess, osc.FguessExpr, osc.SeedPeriodsExpr, osc.KickVExpr), (osc4.InitialGuess, osc4.FguessExpr, osc4.SeedPeriodsExpr, osc4.KickVExpr));
        oscVm.InitialGuess = Analysis.InitialGuessDc;
        Assert.False(oscVm.IsValid);

        // a directive without the keys writes what it always did, in both formats
        var (_, plain) = new CnlReader().Read(Hb + "\n" + Osc + " Fguess=1\n");
        string plainText = CnlWriter.Write(plain);
        Assert.DoesNotContain("InitialGuess", plainText);
        Assert.DoesNotContain("Seed", plainText);
        Assert.DoesNotContain("KickV", plainText);
        foreach (var a in plain.Analyses)
        {
            var dto = AnalysisSerialization.ToDto(a);
            Assert.Null(dto.SeedInitialGuess);
            Assert.Null(dto.SeedTol);
            Assert.Null(dto.SeedPeriods);
            Assert.Null(dto.SeedFastBias);
            Assert.Null(dto.OscKickV);
        }
    }

    // A malformed key is refused by name, as the run refuses it; an oscillator with no Fguess and no seed likewise.
    [Fact]
    public void MalformedKeys_AreRefusedByName()
    {
        var (lib, tb) = new CnlReader().Read(Hb + " InitialGuess=warm\n" + Osc + "\n");
        using var nl = new Elaborator(lib).Elaborate(tb);
        var e1 = Assert.Throws<InvalidOperationException>(() =>
            HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single(), nl.ResolvedGlobals));
        Assert.Contains("InitialGuess must be dc, transient or fallback, not 'warm'", e1.Message);
        var e2 = Assert.Throws<InvalidOperationException>(() =>
            OscEngine.Resolve(tb.Analyses.OfType<OscillatorAnalysis>().Single(), nl.ResolvedGlobals));
        Assert.Contains("needs Fguess", e2.Message);
    }
}
