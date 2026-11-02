// ================================================================
//  MimResonatorCompositionTests.cs — brief-em-mim-10-lc-resonator-without-the-capacitor.md
//
//  THE RECIPE, END TO END: EM everything except the 0.2 µm capacitor gap, and put the closed-form
//  capacitance in the circuit beside the result. It is what a MMIC designer does anyway — a MIM
//  capacitor on a known process is ε₀εᵣA/d to a fraction of a per cent, and what earns the EM run is
//  the coil, the crossover and the ground return, none of which is anywhere near the film.
//
//  It is also the remedy the engine itself now names. A user who draws this whole resonator and
//  presses Simulate is REFUSED (MIM-9's LevelSeparationVerdict), and the refusal's second remedy is
//  the sentence this file measures: take the upper level out of the EM run and model the part it
//  carries as a circuit element beside the EM result.
//
//  TWO CLAIMS, TWO TESTS:
//
//   1. The coil alone is a clean passive two-port, and composed with the kit's own 1.0838 pF it is a
//      series resonance. Tagged Benchmark — it is a de-embedded full-wave sweep, which is what the
//      opt-in tier is for. The composition goes through a real `.cnl`, an SnP component and
//      SParameterEngine, NOT through arithmetic here: MIM-10's own gate says the composed response
//      is the gate and a single extracted L is not, because this coil does not HAVE one constant L
//      (5.91 / 3.56 / 3.13 nH at 1 / 2 / 3 GHz — a distributed structure with a Metal2 underpass).
//
//   2. The `.s2p` says which drawn label became which port number. Fast, and gated on the function
//      rather than on a sweep, exactly as NonPassiveCaveatTests gates the caveat sentence.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine;
using CircuitRF.Engine.Mom;
using NumFlat;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public sealed class MimResonatorCompositionTests(ITestOutputHelper output) : IDisposable
{
    private readonly List<string> _scratch = [];

    public void Dispose()
    {
        foreach (string d in _scratch) try { Directory.Delete(d, true); } catch { /* best effort */ }
    }

    private string Scratch()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-mim10-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _scratch.Add(dir);
        return dir;
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir.Length > 0 ? dir : AppContext.BaseDirectory;
    }

    private static string ExampleRoot() => Path.Combine(RepoRoot(), "examples", "PDK PCells");

    /// <summary>The kit's own number for the 60 × 60 µm plate — ε₀ · 6.8 · A / d, which
    /// <c>KIT_MIMCAP</c> computes and reports and MIM-8's electrostatic instrument confirms to
    /// 0.3 %. It is better than the EM will ever be, and it is free.</summary>
    private const double KitCapacitancePf = 1.0838;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  1 — the coil alone, then the coil and the capacitance together
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The setup is the USER'S, spelled out rather than loaded.</b> The example's own <c>.cem</c>
    /// pins no levels and meshes with circuitRF's defaults; the run MIM-10 was measured on is the
    /// one that produced the reported failure, so its mesh controls are written here where they can
    /// be read. The artwork is the example workspace's <c>SpiralInductor.clay</c>, which IS the
    /// user's layout with the <c>KIT_MIMCAP</c> instance removed and an edge port on the landing pad
    /// it was abutting — reproduced from the real file, not from a reconstruction of it.
    ///
    /// <para><b>'MIM Metal' is deliberately NOT an analysis level.</b> With no plate level in the
    /// run, <c>PatternedDielectric</c> turns the film to air and the structure is ordinary
    /// Metal1/Metal2 interconnect two levels 6 µm apart — cell/separation of order 3, nowhere near
    /// the bound a de-embedded close-level pair is measured over. That was the whole reason this
    /// route existed while the all-EM one was refused; <b>since MIM-14 moved that bound from 40 to
    /// 200 the all-EM run is no longer refused</b> (see
    /// <c>MIM14_TheSpiralAndTheCapacitorArePREFLIGHTED_…</c> below), and this route is now the
    /// cheap alternative to it rather than the only one.</para>
    /// </summary>
    private static EmSetup TheUsersSetup(string layoutRef, double startGHz, double stopGHz, int points)
        => new()
        {
            Name         = "SpiralInductor",
            LayoutRef    = layoutRef,
            AnalysisKind = EmAnalysisKind.Planar,
            Frequency    = new FrequencySpec(
                               startGHz.ToString(System.Globalization.CultureInfo.InvariantCulture),
                               stopGHz.ToString(System.Globalization.CultureInfo.InvariantCulture),
                               points, SweepKind.Linear, "GHz", "GHz"),
            AnalysisLevelNames = ["Metal1", "Metal2"],
            PlanarMesh   = new PlanarMeshSettings(
                               Auto: false, CellsPerWavelength: 20, EdgeMesh: false, EdgeCells: 1,
                               MinCellsAcrossConductor: 2,
                               CurrentModel: PlanarCurrentModel.TransmissionLine,
                               DetailFloorDivisor: 10),
        };

    [Trait("Category", "Benchmark")]
    [Fact]
    public void MIM10_TheCoilAloneIsPassive_AndComposesWithTheKitsCapacitanceToASeriesResonance()
    {
        string results = Scratch();

        var setup    = TheUsersSetup("SpiralInductor/layout/SpiralInductor.clay", 1, 4, 7);
        var resolved = EmSetupResolver.Resolve(
            Path.Combine(ExampleRoot(), "SpiralInductor", "em", "SpiralInductor.cem"),
            setup.LayoutRef, Path.Combine(ExampleRoot(), ".cws"), new TechnologyCache());
        Assert.NotNull(resolved.Source);
        Assert.NotNull(resolved.Source!.Technology);

        var run = EmRunService.Run(setup, resolved.Source, results);
        Assert.Equal(EmRunStatus.Ok, run.Status);
        Assert.NotNull(run.Data);
        Assert.NotNull(run.SnpPath);

        // ── The coil alone is PASSIVE, at every solved point ──────────────────────────────────
        //
        // The engine's own verdict, not a second computation of it: a point it lists here is one it
        // would have written a caveat about onto the file.
        Assert.Empty(run.PlanarSolve!.NonPassivePoints);

        // …and the three the brief tabulates, to the 1e-3 its gate states. σ_max is read back off
        // the published S rather than off any internal counter, because the published S is what a
        // circuit composes.
        var expected = new Dictionary<double, double> { [1e9] = 0.99775, [2e9] = 0.99764, [3e9] = 0.99748 };
        foreach (var (hz, want) in expected)
        {
            double got = SigmaMax(run.Data!, hz);
            output.WriteLine($"coil alone at {hz / 1e9:F0} GHz: sigma_max(S) = {got:F5} (expected {want:F5})");
            Assert.True(Math.Abs(got - want) < 1e-3,
                $"sigma_max at {hz / 1e9:F0} GHz is {got:F5}, not {want:F5} to 1e-3 — the recipe's own " +
                "premise is that the coil alone is an ordinary passive two-port.");
        }

        // ── The port map reached the FILE ─────────────────────────────────────────────────────
        string[] header = [.. File.ReadAllLines(run.SnpPath!).TakeWhile(l => l.StartsWith('!'))];
        Assert.Contains(header, l => l.Contains("circuitRF-EM port 1: '1'", StringComparison.Ordinal));
        Assert.Contains(header, l => l.Contains("circuitRF-EM port 2: '2'", StringComparison.Ordinal));

        // ── The other half: the two pieces meet, in a `.cnl`, through the real engine ─────────
        string cnl = Path.Combine(results, "resonator.cnl");
        File.WriteAllText(cnl, string.Join('\n',
        [
            "; MIM-10 — the coil from the EM, the capacitor from the kit's own closed form.",
            "Port:P1 in  0 Num=1 Z=50 Ohm",
            "Port:P2 out 0 Num=2 Z=50 Ohm",
                        // QUOTED, which is the canonical form and not a nicety: CnlReader resolves a
            // relative Touchstone path against the `.cnl`'s own folder only inside its quoted
            // branch, and `CnlWriter` always writes it this way for that reason.
            $"SnP:L1  in  mid NumPorts=2 File=\"{Path.GetFileName(run.SnpPath!)}\"",
            $"C:C1    mid out C={KitCapacitancePf.ToString(System.Globalization.CultureInfo.InvariantCulture)} pF",
            "analysis SP1 type=sparam start=1 stop=4 npts=121 Unit=GHz",
            "",
        ]));

        var (lib, tb) = CnlReader.ReadFile(cnl);
        double[] freqs = [.. Enumerable.Range(0, 121).Select(i => 1e9 + i * (3e9 / 120.0))];
        var composed = SParameterEngine.Run(
            new Elaborator(lib) { BaseDirectory = results }.Elaborate(tb), freqs);

        var s21 = composed.S(2, 1).ComplexValues;
        var s11 = composed.S(1, 1).ComplexValues;
        int peak = 0;
        for (int i = 1; i < s21.Length; i++) if (s21[i].Magnitude > s21[peak].Magnitude) peak = i;

        output.WriteLine(
            $"composed with {KitCapacitancePf} pF: peak |S21| = " +
            $"{20 * Math.Log10(s21[peak].Magnitude):F3} dB at {freqs[peak] / 1e9:F3} GHz, " +
            $"|S11| there = {20 * Math.Log10(s11[peak].Magnitude):F2} dB");

        // A SERIES RESONANCE, stated as what the composed response does rather than as an L. The
        // band is wide because the claim is "it resonates where the design intends", and the design
        // intends a coil whose reactance crosses 1/ωC between 2.5 and 3 GHz.
        Assert.InRange(freqs[peak] / 1e9, 2.5, 3.0);
        Assert.True(s21[peak].Magnitude > 0.944,   // −0.5 dB
            $"the composed peak is {20 * Math.Log10(s21[peak].Magnitude):F3} dB — not a through, so " +
            "this is not a series resonance.");
        Assert.True(s11[peak].Magnitude < 0.1,     // −20 dB
            $"|S11| at the peak is {20 * Math.Log10(s11[peak].Magnitude):F2} dB — a series resonance " +
            "matches, and the all-EM run this recipe replaces is the one that never got below −14 dB.");
    }

    /// <summary>The largest singular value of the published S at one frequency.</summary>
    private static double SigmaMax(DataSet data, double hz)
    {
        double[] f = data.S(1, 1).Axis("freq").Values;   // every S(i,j) shares one frequency axis
        int k = 0;
        for (int i = 1; i < f.Length; i++) if (Math.Abs(f[i] - hz) < Math.Abs(f[k] - hz)) k = i;
        Assert.True(Math.Abs(f[k] - hz) < 1e3, $"no solved point at {hz / 1e9:F3} GHz");

        var s = new Mat<Complex>(2, 2);
        for (int i = 1; i <= 2; i++)
            for (int j = 1; j <= 2; j++)
                s[i - 1, j - 1] = data.S(i, j).ComplexValues[k];
        return s.Svd().S[0];
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  1b — MIM-14's acceptance: the SAME resonator, all-EM, in ONE run
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>brief-em-mim-14 M5 — the structure that was refused is ACCEPTED, and the acceptance is
    /// asked of the real artwork through the real preflight.</b>
    ///
    /// <para>This is the run MIM-9 refused and MIM-10 was written to work around. MIM-12 measured
    /// the de-embedded two-port of a close level pair as sign-INVERTED past cell/separation 40, so
    /// <c>PlanarLevels.FullWaveCellOverSeparation</c> stood there and this structure — which meshes
    /// at 60 before the solve grows its feeds and 127 after — was refused before a matrix was
    /// filled. MIM-12a repaired the kernel behind that
    /// measurement and MIM-14 re-ran the ladder on it: the floor is 200 and this runs.</para>
    ///
    /// <para><b>THE SWEEP ITSELF IS NOT IN THIS TEST, AND THAT IS A DECISION RATHER THAN AN
    /// OMISSION.</b> It was run, on this file, and its rows are in <c>HISTORY.md</c> §MIM-14: the
    /// |S11| null at <b>2.77 GHz, −31.6 dB</b> with |S21| = −0.22 dB there, against MIM-10's
    /// composition route at 2.750 GHz / −0.083 dB / −29.6 dB — <b>0.9 % in frequency and 0.14 dB</b>,
    /// inside the brief's 2 % and 0.3 dB, and passive at every solved point. What it costs is the
    /// problem: the DUT is 2,456 unknowns on three levels with four calibration standards solved
    /// beside it at every frequency, which is ~5 min for thirteen points in a RELEASE build and
    /// over an hour for five in the Debug build a test run uses. That is larger than the whole
    /// existing <c>Category=Benchmark</c> tier, so it is measured with the CLI in Release — the
    /// series' own convention for wall-clock-heavy work — and reproduced by
    /// <c>dotnet run -c Release --project src/Cli -- em "examples/PDK PCells/SpiralResonator/em/SpiralResonator.cem"</c>.</para>
    ///
    /// <para>What IS here is the half that can be asked in a second and is the half that moved: the
    /// real <c>.cem</c>, the real artwork, the real <c>EmRunService.Preflight</c> — the same call
    /// <c>circuitrf check</c> makes — reporting that the plate level is in the run, that the film is
    /// in the medium, and that the level pair the old floor refused is now inside it. A preflight
    /// that PASSES a setup the run refuses is the drift R-aut4-2 exists to prevent, so this is not a
    /// weaker question than the sweep about the thing the sweep would have been asked.</para>
    ///
    /// <para>The artwork is <c>SpiralResonator.clay</c>, which is <c>SpiralInductor.clay</c> —
    /// MIM-10's fixture, unchanged — plus a series MIM capacitor drawn as flat artwork rather than
    /// as a kit instance, so nothing here needs a generator process running.</para>
    /// </summary>
    [Fact]
    public void MIM14_TheSpiralAndTheCapacitorArePREFLIGHTED_OnAllThreeLevels_AndNoLongerRefused()
    {
        var setup = TheUsersSetup("SpiralResonator/layout/SpiralResonator.clay", 2.4, 3.0, 13);
        setup.Name = "SpiralResonator";
        // The PLATE level joins the run. That is the whole difference from the recipe above, and it
        // is what puts the 0.2 µm film in the medium.
        setup.AnalysisLevelNames = ["Metal1", "MIM Metal", "Metal2"];

        var resolved = EmSetupResolver.Resolve(
            Path.Combine(ExampleRoot(), "SpiralResonator", "em", "SpiralResonator.cem"),
            setup.LayoutRef, Path.Combine(ExampleRoot(), ".cws"), new TechnologyCache());
        Assert.NotNull(resolved.Source);

        var pre = EmRunService.Preflight(setup, resolved.Source!);
        foreach (var f in pre.Findings) output.WriteLine(f.Text);
        output.WriteLine(pre.Refusal ?? "(not refused)");

        // THE ACCEPTANCE: not refused.
        Assert.True(pre.Ok, pre.Refusal);

        // …and the reason it is not refused is the one MIM-14 moved, said in the run's own words.
        // The ratio is the mesh's and 200 is the constant's; both are asserted, because "it runs"
        // without the ratio would not say whether the floor moved or the mesh did.
        //
        // 60 HERE AND 127 IN THE SOLVE, and the difference is not noise. R-fed-1 grows a uniform
        // feed lead on each port before the DUT is meshed, which widens the artwork, and with
        // DetailFloorDivisor set the detail floor is a fraction of that extent — so the solve's own
        // cells are coarser than the preflight's. Both are inside 200, which is the claim; a test
        // that asserted one number for both would be asserting something untrue.
        string separation = Assert.Single(
            pre.Findings, f => f.Text.Contains("cell/separation", StringComparison.Ordinal)).Text;
        Assert.Contains("are resolved by the mesh", separation, StringComparison.Ordinal);
        Assert.Contains("cell/separation = 60 ", separation, StringComparison.Ordinal);
        Assert.Contains("inside the 200", separation, StringComparison.Ordinal);
        Assert.Equal(200.0, PlanarLevels.FullWaveCellOverSeparation);

        // The film is in the medium and the plate is in the matrix — without those two this would be
        // the cheap route wearing the expensive one's name.
        Assert.Contains(pre.Findings,
            f => f.Text.Contains("CARRIES the patterned thin film", StringComparison.Ordinal));
        Assert.Contains(pre.Findings,
            f => f.Text.Contains("3 conductor level(s)", StringComparison.Ordinal));

        // The via CHAIN is the other half of "the plate is really in the run": the Metal1-to-Metal2
        // post crosses the plate level, so it is several vias and not one, and if it were not built
        // that way the capacitor's top terminal would not reach the landing pad at all.
        Assert.Contains(pre.Findings,
            f => f.Text.Contains("were built as a CHAIN", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    //  2 — the port map on the file
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Which drawn label became which port number, on the copy that outlives the session.</b> The
    /// run says it in its notes; MIM-9's own finding is that the notes do not survive onto the file,
    /// and MIM-10's result is read by a NETLIST rather than by the person who ran it.
    ///
    /// <para>ASCII is asserted rather than assumed, for the reason MIM-9 measured on the caveat
    /// line: a Touchstone file is written in an encoding that turns a µ or an Ω into "?".</para>
    /// </summary>
    [Fact]
    public void ThePortMapNamesEachLabel_ItsLevel_ItsAnchorAndItsZ0_InAscii()
    {
        var ports = new[]
        {
            new PlanarPort(1, new EmPoint(-110e-6, -125e-6), PlanarPortSide.MinY, 50, LayerIndex: 0),
            new PlanarPort(2, new EmPoint(-155e-6,  -46e-6), PlanarPortSide.MinX, 75, LayerIndex: 1),
        };
        var labels = new[]
        {
            new LabelShape { Text = "1",  X = -110_000, Y = -125_000, IsPort = true },
            new LabelShape { Text = "P2", X = -155_000, Y =  -46_000, IsPort = true },
        };

        var lines = EmSnpProvenance.PortMap(["Metal1", "Metal2"], ports, labels, 1000, LayoutUnit.Um);
        foreach (string l in lines) output.WriteLine(l);

        // First, which numbered port is which named one, in the Port[N] = name spelling other tools read.
        Assert.Equal(["Port[1] = 1", "Port[2] = P2"], lines.Take(2));
        Assert.Equal("circuitRF-EM port 1: '1' on 'Metal1' at (-110, -125 um), edge, de-embedded, 50 Ohm",
                     lines[2]);
        Assert.Equal("circuitRF-EM port 2: 'P2' on 'Metal2' at (-155, -46 um), edge, de-embedded, 75 Ohm",
                     lines[3]);

        // The sentence that says what to DO with the order — carried once, under the list, and only
        // where there is an order to get wrong.
        Assert.Equal(5, lines.Count);
        Assert.Contains("port N of this file", lines[4], StringComparison.Ordinal);

        Assert.All(lines, l => Assert.All(l, c => Assert.True(c < 128, $"non-ASCII '{c}' in: {l}")));

        // One port has no order to get wrong, so there is no sentence about one.
        Assert.Equal(2, EmSnpProvenance.PortMap(["Metal1"], [ports[0]], [labels[0]], 1000, LayoutUnit.Um).Count);
        Assert.Empty(EmSnpProvenance.PortMap(["Metal1"], [], [], 1000, LayoutUnit.Um));
    }
}
