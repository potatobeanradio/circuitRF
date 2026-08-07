// ================================================================
//  HarmonicaInterchangeTests.cs  —  M4's gate, brief-harmonicarf-h7
//
//  R-h7-11  .gam in and out through the EXISTING reader and writer; an imported grid invalidates the
//           RBF factorization, because the node set moved.
//  R-h7-12  dragging ONE grid point costs ~one Γ sample, not the whole grid.
//  R-h7-13  Export testbench produces a RUNNABLE .cnl whose Pout/gain agree with the frame.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Engine.Loadpull;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaInterchangeTests(ITestOutputHelper output)
{
    // ══ R-h7-13 — the exported testbench, through the ordinary HB path ══════

    /// <summary>
    /// The FOMs both routes report, computed by <c>LoadpullEngine.ComputeFoms</c> on each side — so
    /// this compares two SOLVES rather than two formulas, exactly as H0–H3's Tier 3 does.
    /// </summary>
    private readonly record struct Foms(double PoutDbm, double GtDb, double GpDb, double De, double PdcW);

    private static (Foms Harmonica, Foms Testbench, bool Converged, string Cnl)
        RunBothRoutes(CircuitModel model, Action<TerminationSet>? terms = null)
    {
        var vm = new HarmonicaViewModel(model);
        terms?.Invoke(vm.Terminations);
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });
        double pavl = vm.OperatingPointDbm;

        // ── harmonicaRF's own answer, at the frame's operating point ──────────
        var ctx  = HarmonicaContext.Create(vm.Model);
        var pt   = ctx.Solve(vm.Terminations, pavl);
        var meas = PinSearch.Measure(ctx, pt, vm.Terminations);
        var h = new Foms(10 * Math.Log10(meas.PoutW) + 30,
                         meas.Foms.GtDb, meas.Foms.GpDb, meas.De, meas.PdcW);

        // ── the exported testbench, through HbEngine.Run — the SAME entry point
        //    Cli's `hb` verb calls after it has parsed its arguments ───────────
        string cnl = HarmonicaInterchange.ExportTestbench(vm.Model, vm.Terminations, pavl);
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var p  = HbEngine.Resolve((HarmonicBalanceAnalysis)tb.Analyses[0],
                                  nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        var run = new HbEngine(nl, tb).Run(p);

        var vCube = run.DataSet["V"];
        var iCube = run.DataSet["INl"];
        var names = vCube.Axes[0].Labels!;
        int bands = vCube.Axes[1].Values.Length;
        Complex V(int n, int k) => vCube.ComplexValues[n * bands + k];
        Complex I(int n, int k) => iCube.ComplexValues[n * bands + k];

        // The DUT's own terminals. With no package they ARE the termination planes; with one they
        // are their own nodes, and the power leaving the DEVICE is what harmonicaRF measures too.
        int gate  = Array.IndexOf(names, HarmonicaNetlist.GateTerminal);
        int drain = Array.IndexOf(names, HarmonicaNetlist.DrainTerminal);
        if (gate  < 0) gate  = Array.IndexOf(names, HarmonicaNetlist.SourcePlane);
        if (drain < 0) drain = Array.IndexOf(names, HarmonicaNetlist.LoadPlane);

        int K = vm.Model.Settings.HarmonicCount;
        var vm2 = new Complex[2, K + 1];
        var im2 = new Complex[2, K + 1];
        var iin = new Complex[K + 1];
        for (int k = 0; k <= K; k++)
        {
            vm2[0, k] = V(gate, k);  vm2[1, k] = V(drain, k);
            im2[0, k] = I(gate, k);  im2[1, k] = I(drain, k);
            iin[k]    = I(gate, k);
        }

        double pavlW = Math.Pow(10, (pavl - 30) / 10);
        var foms = LoadpullEngine.ComputeFoms(vm2, iin, im2, 1, 0, pavlW, K);
        double pdc = vm2[1, 0].Real * im2[1, 0].Real + vm2[0, 0].Real * im2[0, 0].Real;

        var r = new Foms(10 * Math.Log10(foms.PoutW) + 30, foms.GtDb, foms.GpDb,
                         foms.PoutW / pdc, pdc);

        return (h, r, run.Converged, cnl);
    }

    [Fact]
    public void TheExportedTestbench_ReproducesTheFrame_OnTheShippedDefaultDocument()
    {
        var (h, r, converged, cnl) = RunBothRoutes(HarmonicaViewModel.DefaultModel());

        Assert.True(converged, "the exported testbench did not converge");
        output.WriteLine($"harmonicaRF Pout {h.PoutDbm:F8} dBm  Gt {h.GtDb:F8} dB  DE {h.De * 100:F5}%  Pdc {h.PdcW:F6} W");
        output.WriteLine($"testbench   Pout {r.PoutDbm:F8} dBm  Gt {r.GtDb:F8} dB  DE {r.De * 100:F5}%  Pdc {r.PdcW:F6} W");
        output.WriteLine($"Δ Pout {Math.Abs(h.PoutDbm - r.PoutDbm):E3} dB   Δ Gt {Math.Abs(h.GtDb - r.GtDb):E3} dB");

        Assert.True(h.PoutDbm > 0, "the fixture must actually produce power, or agreement is vacuous");

        // H0–H3's Tier 3 landed at 6.7e-5 dB against a Tuner-based reference whose IDEAL bias tee
        // differed from the model's own values. This export states the model's own DcBlockFarads and
        // BiasChokeHenries, so the two circuits are literally the same one and the agreement is
        // several orders better. The bound is the Tier 3 class, and the measured number is reported.
        Assert.True(Math.Abs(h.PoutDbm - r.PoutDbm) < 6.7e-5,
                    $"Pout differs by {Math.Abs(h.PoutDbm - r.PoutDbm):E3} dB");
        Assert.True(Math.Abs(h.GtDb - r.GtDb) < 6.7e-5, "Gt differs");
        Assert.True(Math.Abs(h.GpDb - r.GpDb) < 6.7e-5, "Gp differs");
        Assert.True(Math.Abs(h.De - r.De) < 1e-6, "DE differs");

        Assert.Contains("analysis HB1 type=hb", cnl, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExportedTestbench_ReproducesTheFrame_WithAPackageAndHarmonicTerminations()
    {
        // The case the export exists for: a full lumped package (so the source lead makes Z_S depend
        // on gm) and marked bands on BOTH sides above the fundamental — which is exactly what a
        // Tuner-based export could not have carried through `Cli hb`, since nothing gives a Tuner a
        // band ruler on that path.
        var model = HarmonicaViewModel.DefaultModel() with
        {
            Embedding = new EmbeddingStack
            {
                Package = new LumpedPackage
                {
                    Rg = 1.5, Lg = 0.2e-9, Rd = 4.0, Ld = 0.3e-9,
                    Rs = 0.8, Ls = 0.05e-9, Cpd = 0.2e-12,
                },
            },
        };

        var (h, r, converged, cnl) = RunBothRoutes(model, t =>
        {
            t.Set(TerminationSide.Load,   2, new Complex(3, -40));
            t.Set(TerminationSide.Load,   3, new Complex(2,  18));
            t.Set(TerminationSide.Source, 2, new Complex(6,  25));
        });

        Assert.True(converged);
        output.WriteLine($"harmonicaRF Pout {h.PoutDbm:F8} dBm  Gt {h.GtDb:F8} dB  Pdc {h.PdcW:F6} W");
        output.WriteLine($"testbench   Pout {r.PoutDbm:F8} dBm  Gt {r.GtDb:F8} dB  Pdc {r.PdcW:F6} W");
        output.WriteLine($"Δ Pout {Math.Abs(h.PoutDbm - r.PoutDbm):E3} dB   Δ Gt {Math.Abs(h.GtDb - r.GtDb):E3} dB");

        Assert.True(Math.Abs(h.PoutDbm - r.PoutDbm) < 6.7e-5,
                    $"Pout differs by {Math.Abs(h.PoutDbm - r.PoutDbm):E3} dB");
        Assert.True(Math.Abs(h.GtDb - r.GtDb) < 6.7e-5, "Gt differs");

        // Every marked band travels, and the unmarked ones take D9's near-short explicitly rather
        // than a component's own 50 Ω default.
        Assert.Contains("Z[2]=complex(3,-40)", cnl, StringComparison.Ordinal);
        Assert.Contains("Z[3]=complex(2,18)",  cnl, StringComparison.Ordinal);
        Assert.Contains($"Z={TerminationSet.UnmarkedBandOhms.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)}",
                        cnl, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExportedTestbench_UsesP1ToneAndPnTone_BecauseATunerIsInertUnderAPlainHbRun()
    {
        var vm = new HarmonicaViewModel();
        string cnl = HarmonicaInterchange.ExportTestbench(vm.Model, vm.Terminations, 10.0);

        // The load side is a PnTone declaring NO tones — its drive phasor is zero at every spectral
        // line while its per-band Z[k] is live. A Tuner here would present Z[1] flat at every
        // harmonic AND emit no drive, because HbEngine calls SetRole / SetTone / SetSourceDrive on
        // nobody; those belong to the loadpull engine, and the CLI has no loadpull verb.
        Assert.Contains("P1Tone:PIN", cnl, StringComparison.Ordinal);
        Assert.Contains("PnTone:PLOAD", cnl, StringComparison.Ordinal);
        Assert.DoesNotContain("Tuner:", cnl, StringComparison.Ordinal);
        Assert.DoesNotContain("Freq[1]", cnl, StringComparison.Ordinal);   // no tones on the load

        // The DC block is STATED. §6.2 folds it into the termination admittance rather than the
        // netlist, so an export that left it out would be a different circuit.
        Assert.Contains("C:CBLKS", cnl, StringComparison.Ordinal);
        Assert.Contains("C:CBLKL", cnl, StringComparison.Ordinal);
    }

    // ══ §7.8 — Copy termination set ═════════════════════════════════════════

    [Fact]
    public void CopyTerminationSet_ProducesTextThatPastesIntoACnlAndParses()
    {
        var vm = new HarmonicaViewModel();
        vm.AddMarkerBand(TerminationSideKind.Load, 2);
        vm.SetMarkerImpedance(vm.Markers.Single(m => m is { Side: TerminationSideKind.Load, Band: 2 }),
                              new Complex(4, -33));

        string pasted = HarmonicaInterchange.CopyTerminationSet(vm.Terminations, vm.Model.Bias);
        output.WriteLine(pasted);

        // Pasted beside a DUT in an otherwise ordinary .cnl — which is what §7.8 asks for.
        string cnl = $"""
            RFfreq = 2e9

            {pasted}
            SDD:M1  n_srcterm 0  n_ldterm 0  I[1,0]=_v1/50  I[2,0]=_v2/25

            analysis HB1 type=hb Tone=RFfreq MaxHarm=3 Tol=1e-8
            """;

        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);

        var tuners = nl.Components
            .Where(c => c.Model is CircuitRF.Core.Devices.TunerModel)
            .Select(c => c.InstancePath)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Load", "Src"], tuners);

        // The pasted line is a Tuner pair on PURPOSE and is not the testbench export: a Tuner pasted
        // into a schematic is driven by the loadpull engine, which sets its role, tone and drive.
        Assert.Contains("Tuner:Src", pasted, StringComparison.Ordinal);
        Assert.Contains("Tuner:Load", pasted, StringComparison.Ordinal);
        Assert.Contains("Z[2]=complex(4,-33)", pasted, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", pasted.Split("Z[2]=")[1].Split('\n')[0].Trim().Split("  ")[0]);
    }

    // ══ R-h7-11 — .gam round trip, holes included ═══════════════════════════

    [Fact]
    public void AGamWrittenAndReRead_ReproducesTheGridIncludingHoles()
    {
        var points = new List<HarmonicaGridPoint>
        {
            new(new Complex( 0.00,  0.00), false),
            new(new Complex( 0.40,  0.10), false),
            new(new Complex(-0.35,  0.55), true),      // a hole
            new(new Complex( 0.12, -0.62), false),
            new(new Complex(-0.70, -0.20), true),      // another
        };

        string gam = HarmonicaInterchange.ExportGam(points, freqHz: 2e9);
        output.WriteLine(gam);

        var back = HarmonicaInterchange.ImportGam(gam, 2e9, out var notes);
        foreach (string n in notes) output.WriteLine("note: " + n);

        Assert.Equal(points.Count, back.Count);
        for (int i = 0; i < points.Count; i++)
        {
            Assert.Equal(points[i].Gamma.Real,      back[i].Real,      8);
            Assert.Equal(points[i].Gamma.Imaginary, back[i].Imaginary, 8);
        }

        // The HOLES travel: they are points the user placed and the run could not compress, not
        // points that are absent. Dropping them on export would shrink the grid every round trip.
        Assert.Contains("hole", gam, StringComparison.Ordinal);
        Assert.Equal(5, back.Count);
    }

    [Fact]
    public void AGamPointOutsideTheUnitCircle_IsDroppedWithANote_NotAcceptedSilently()
    {
        const string gam = """
            # gamma Z0=50 re_im
            0.2  0.1
            1.4  0.0
            -0.3 0.2
            """;

        var points = HarmonicaInterchange.ImportGam(gam, 2e9, out var notes);
        output.WriteLine(string.Join("\n", notes));

        Assert.Equal(2, points.Count);
        Assert.Single(notes);
        Assert.Contains("outside the unit circle", notes[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ImportingAGrid_ReplacesTheRingSetAndResetsTheLadder()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });

        var imported = new[]
        {
            new Complex(0.0, 0.0), new Complex(0.3, 0.1), new Complex(-0.2, 0.4),
            new Complex(0.15, -0.35), new Complex(-0.45, -0.1),
        };
        vm.SetGammaGrid(imported);

        Assert.NotNull(vm.CustomGrid);
        Assert.Equal(imported.Length, vm.CustomGrid!.Count);

        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 5, Spokes = 12,
                                                    GammaGrid = vm.CustomGrid,
                                                    RasterResolution = 32 });

        // The RING set is gone: the grid is the imported scatter, not 61 points.
        Assert.Equal(imported.Length, vm.Frame.SmithPower.GridPoints.Count);
    }
}
