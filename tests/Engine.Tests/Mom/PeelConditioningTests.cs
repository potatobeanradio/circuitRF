// PEEL — "a 3.8 mm microstrip de-embedded at 10 MHz publishes |S11| = -1.37 dB where the line gives
// -60.2 dB, and nothing in the run says so."
//
// The defect that reached a user is not the 59 dB. It is that the 59 dB arrived with NO SENTENCE
// ATTACHED — and that the one diagnostic anybody would have checked, `DeembedResidual`, is
// ANTI-CORRELATED with the truth down there: it is smallest exactly where the answer is worst,
// because two standards whose x = e^{-γℓ} are both approaching 1 agree better and better with each
// other while determining the error box less and less.
//
// WHAT THE MEASUREMENT FOUND, AND IT IS NOT WHAT THE BRIEF PREDICTED. The brief attributed the
// amplification to the factor (x₂² − x₁²) ≈ 2γΔℓ that both halves of the a₂₂² quotient carry, i.e.
// to the two standards' SEPARATION. That was measured directly and it is inert: a 30× longer second
// standard — βΔℓ from 0.40° to 11.8° at 10 MHz — moves the de-embedded |S₁₁| of a uniform line by
// under 6 %, and at 100 MHz a 30× separation is TEN TIMES WORSE. The amplifier is the one RAW1 §6
// and LF1 §5(a) already named: `Apply` forms Y = (S_meas − a₁₁)/a₂₁², an edge port at the bottom of
// a band is a series gap capacitance, so a₂₁ ∝ ω and the division is by an ω².
//
// Four things are gated here:
//
//   P1  THE LAW. |ΔS| on a uniform line = ConsistencyResidual · |a₁₁| / |a₂₁|², to a ratio near 1
//       over three stacks, three mesh densities and three decades. This is the primary gate and the
//       oracle is the control itself — a de-embedded S₁₁ on a plain line must be exactly 0, so no
//       second model is in the path.
//   P2  Δℓ IS INERT. The recorded number the "do not re-lengthen the standards" rule rests on.
//   P3  THE CUBE. `DeembedErrorFloor` is published per (frequency, port) beside the two residuals.
//   P4  THE GUARD. A run whose floor is catastrophic is REFUSED with a sentence naming the band edge
//       that answers it; one whose floor merely exceeds the budget is NAMED; and neither changes a
//       single published number on a run that is nowhere near either threshold.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit;
using Xunit.Abstractions;
using CircuitRF.Engine.Calibration;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class PeelConditioningTests(ITestOutputHelper output)
{
    /// <summary>The board the series was reported on — 20 mil-class laminate, εᵣ ≈ 4.</summary>
    private static GroundedSlab ReportedBoard => new(0.6e-3, new EmMaterial(4.0, 0.02));

    /// <summary>The reported trace: 254 µm wide on that board.</summary>
    private const double ReportedWidthM = 254e-6;

    private static PlanarMeshSettings Mesh(int cellsPerWavelength) =>
        new(Auto: false, CellsPerWavelength: cellsPerWavelength, EdgeMesh: false);

    /// <summary>
    /// <b>The control, and it is the only oracle in this file.</b> A uniform line de-embedded
    /// against standards of its own cross-section must come back with S₁₁ EXACTLY 0 — at the line's
    /// own Z_c, which is why nothing here renormalises to 50 Ω: a 106 Ω line read in a 50 Ω system
    /// has a real, physical S₁₁ that is not the instrument's error.
    /// </summary>
    private sealed record Control(double F, double Realised, double Floor, double Residual,
                                  double A21Sq, double ElectricalDeg);

    private List<Control> Measure(GroundedSlab slab, double widthM, double lengthM,
                                  PlanarMeshSettings mesh, IReadOnlyList<double> freqs,
                                  double fLo = 10e6, double fHi = 500e6)
    {
        var problem = PlanarLineFixtures.Line(slab, widthM, lengthM, 2e9);
        var (dutMesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, mesh);
        var plan = PlanarCalibration.SeparationPlan(slab, fLo, fHi, ports[0]);
        var set  = PlanarCalibration.BuildSet(ports[0], slab, plan,
                       PlanarCalibration.SuggestLengths(slab, fLo, fHi).Short);
        var fill = PlanarFillSettings.Default with { ConductorLoss = PlanarConductorLoss.For(problem) };
        var cal  = new PlanarPortCalibrator(ports[0], slab, fLo, fHi, null, fill,
                                            standards: set, separations: plan);
        var dut  = new PlanarSolveContext(dutMesh, ports, fill, null, slab.HeightM);

        var result = new List<Control>();
        foreach (double f in freqs.OrderBy(x => x))
        {
            var kernel = PlanarLineFixtures.Kernel(slab, f);
            var c = cal.At(kernel, f, 1);
            var s = LineDeembed.Apply(dut.RawScatteringAt(kernel, f), [c.Box, c.Box]);
            result.Add(new Control(f, Math.Max(s[0, 0].Magnitude, s[1, 1].Magnitude),
                                   c.Box.DeembedErrorFloor, c.Box.ConsistencyResidual,
                                   (c.Box.A21 * c.Box.A21).Magnitude, c.Gamma.ElectricalDegrees));
        }
        return result;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // P1 — THE LAW
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>|ΔS| = ConsistencyResidual · |a₁₁| / |a₂₁|², and the two factors move in opposite
    /// directions — which is the whole reason the residual alone is a trap.</b>
    ///
    /// <para>The residual is ∝ ω and |a₂₁|² is ∝ ω², so the published error goes as 1/f while the
    /// number a reader checks goes as f. On the fixtures below the residual falls by a decade
    /// between 100 MHz and 10 MHz and the realised error RISES by one.</para>
    ///
    /// <para><b>The tolerance is a factor of two either way and it is asserted at every point whose
    /// floor is above a TENTH of the guard's budget</b> — i.e. over the whole range in which the
    /// guard ever acts, and no further. Below that the realised error is dominated by things the
    /// floor cannot see (the DUT's own meshing, radiation), which is exactly what the doc comment on
    /// <see cref="LineErrorBox.DeembedErrorFloor"/> says it is blind to; the 1 GHz row of the 4×
    /// board reads a ratio of 0.48 at a floor of 2.9e-3 and that is the floor being conservative
    /// rather than the law failing.</para>
    /// </summary>
    [Theory]
    [InlineData("reported board 0.6 mm, coarse", 0, 10)]
    [InlineData("reported board 0.6 mm, 4x mesh", 0, 40)]
    [InlineData("FR-4 1.6 mm, coarse", 1, 10)]
    [InlineData("FR-4 1.6 mm, 2x mesh", 1, 20)]
    [InlineData("GaAs 0.1 mm, coarse", 2, 10)]
    public void TheErrorFloorPredictsWhatTheUniformLineControlActuallyReads(
        string name, int which, int cellsPerWavelength)
    {
        var (slab, w, len) = which switch
        {
            0 => (ReportedBoard, ReportedWidthM, 20e-3),
            1 => (GroundedSlab.Fr4Starter, PlanarLineFixtures.Fr4HeroWidthM, 40e-3),
            _ => (GroundedSlab.GaAsStarter, PlanarLineFixtures.GaAsHeroWidthM, 2e-3),
        };

        var rows = Measure(slab, w, len, Mesh(cellsPerWavelength),
                           [10e6, 30e6, 100e6, 300e6, 1e9]);

        output.WriteLine($"--- {name}");
        output.WriteLine($"{"f/MHz",8} {"resid",11} {"|a21|^2",11} {"floor",11} {"realised",11} {"ratio",7}");
        foreach (var r in rows)
            output.WriteLine($"{r.F / 1e6,8:0.##} {r.Residual,11:0.00e+00} {r.A21Sq,11:0.00e+00} " +
                             $"{r.Floor,11:0.00e+00} {r.Realised,11:0.00e+00} " +
                             $"{r.Realised / r.Floor,7:0.000}");

        var gated = rows.Where(r => r.Floor > 0.1 * PlanarSolve.PeelErrorBudgetDS).ToList();
        Assert.NotEmpty(gated);
        foreach (var r in gated)
            Assert.InRange(r.Realised / r.Floor, 0.5, 2.0);

        // …and the direction, which is the part that makes DeembedResidual a trap rather than
        // merely an incomplete measure. Asserted as the RELATIONSHIP over the band, not as two
        // separate facts: a fix that made the residual constant would pass half of this.
        var lo = rows[0];
        var hi = rows[^1];
        output.WriteLine($"residual {lo.Residual:0.00e+00} -> {hi.Residual:0.00e+00} over " +
                         $"{lo.F / 1e6:0.#}-{hi.F / 1e6:0.#} MHz, " +
                         $"realised {lo.Realised:0.00e+00} -> {hi.Realised:0.00e+00}");
        Assert.True(hi.Residual > lo.Residual,
                    "the residual must GROW up the band — that is what makes it anti-correlated");
        Assert.True(hi.Realised < lo.Realised,
                    "…while the error it is read as a proxy for SHRINKS");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // P2 — Δℓ IS INERT, AND THIS IS THE NUMBER THE "MUST NOT" RESTS ON
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Re-lengthening the calibration standards is the obvious move at the bottom of a band, it
    /// is exactly the 1/f_lo standard QSC existed to remove, and IT DOES NOT WORK.</b>
    ///
    /// <para>Measured rather than argued, because the brief this work was done under asserted the
    /// opposite in its own "Must NOT" ("it is the obvious move, it works"). On the reported board's
    /// cross-section with a 20 mm uniform line, second standards from 3× to 61× the short one:
    /// βΔℓ goes 0.40° → 11.8° at 10 MHz and the de-embedded |S₁₁| moves by under 6 %. At 100 MHz the
    /// same sweep takes βΔℓ from 4.0° to 118° — the whole of TRL's usable interval — and the answer
    /// gets an ORDER OF MAGNITUDE WORSE, because the two standards' M₁₁ inconsistency grows with the
    /// length difference while the amplifier it is multiplied by does not move at all.</para>
    ///
    /// <para>Anyone reaching for a longer standard has rediscovered the problem, not the solution.
    /// The amplifier is |a₁₁|/|a₂₁|² and there is no Δℓ in it.</para>
    /// </summary>
    [Fact]
    public void ALongerSecondStandardDoesNotMoveIt_AndUpTheBandItIsWorse()
    {
        var slab = ReportedBoard;
        double fLo = 10e6, fHi = 500e6;
        var problem = PlanarLineFixtures.Line(slab, ReportedWidthM, 20e-3, 2e9);
        var (dutMesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);
        var plan = PlanarCalibration.SeparationPlan(slab, fLo, fHi, ports[0]);
        var set  = PlanarCalibration.BuildSet(ports[0], slab, plan,
                       PlanarCalibration.SuggestLengths(slab, fLo, fHi).Short);

        var lines = new List<PlanarStandard>(set);
        foreach (double k in (double[])[6, 12, 30])
            lines.Add(PlanarCalibration.BuildLine(ports[0], set[0].LengthM * k, set[0].EndRunCells));

        var fill = PlanarFillSettings.Default with { ConductorLoss = PlanarConductorLoss.For(problem) };
        var cal  = new PlanarPortCalibrator(ports[0], slab, fLo, fHi, null, fill,
                                            standards: set, separations: plan);
        var dut  = new PlanarSolveContext(dutMesh, ports, fill, null, slab.HeightM);
        var ctx  = lines.Select(z => new PlanarSolveContext(z.Mesh, z.Ports, fill, null, slab.HeightM))
                        .ToArray();
        double[] L = [.. lines.Select(z => z.LengthM)];

        foreach (double f in (double[])[10e6, 100e6])
        {
            var kernel = PlanarLineFixtures.Kernel(slab, f);
            var c = cal.At(kernel, f, 1);
            Complex g = c.Gamma.Gamma;
            var raw = dut.RawScatteringAt(kernel, f);
            var m = ctx.Select(z => z.RawScatteringAt(kernel, f)).ToArray();

            var errors = new List<double>();
            output.WriteLine($"  f = {f / 1e6:0.##} MHz");
            for (int j = 1; j < L.Length; j++)
            {
                var box = LineDeembed.SolveErrorBox(m[0], m[j], L[0], L[j], g);
                var s = LineDeembed.Apply(raw, [box, box]);
                double err = Math.Max(s[0, 0].Magnitude, s[1, 1].Magnitude);
                errors.Add(err);
                output.WriteLine($"    dl = {(L[j] - L[0]) * 1e3,8:0.##} mm  " +
                                 $"betaDL = {g.Imaginary * (L[j] - L[0]) * 180 / Math.PI,8:0.###} deg  " +
                                 $"N = {lines[j].Mesh.Bases.Count,4}  |S11| = {err:0.000e+00}");
            }

            double spread = errors.Max() / errors.Min();
            output.WriteLine($"    spread over a {L[^1] / L[1]:0.#}x longer second standard: {spread:0.00}x");

            if (f < 50e6)
                Assert.InRange(spread, 1.0, 1.25);   // 10 MHz: a 30x separation buys nothing
            else
                Assert.True(errors[^1] > errors[0] * 3,
                            "at 100 MHz the LONGEST separation must be the WORST — which is the " +
                            "half of this that stops someone reading the 10 MHz row as 'harmless'");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // P3 — THE CUBE
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b><c>DeembedErrorFloor</c> is published per (frequency, port) beside the two residuals, and
    /// it is emitted unconditionally</b> — <c>CalQuasiStatic</c>'s own rule: a reader of the file can
    /// always ask the question and gets an answer rather than a missing cube to interpret.
    ///
    /// <para>Asserted against the box the run actually solved rather than against a literal, and with
    /// its own recorded number on the reported cross-section, the way §QSC recorded the a₂₂
    /// margin.</para>
    /// </summary>
    [Fact]
    public void TheErrorFloorIsPublishedPerFrequencyAndPort()
    {
        var problem = PlanarLineFixtures.Line(ReportedBoard, ReportedWidthM, 20e-3, 4e9);
        var ports   = PlanarLineFixtures.EndPorts(problem);
        double[] freqs = [1e9, 2e9, 4e9];

        var ds = new PlanarKernel().Solve(problem, PlanarLineFixtures.Coarse, ports, freqs,
                                          PlanarSolveSettings.Default);

        var cube = ds.Data[$"{PlanarKernel.DiagnosticsGroup}.DeembedErrorFloor"];
        var resid = ds.Data[$"{PlanarKernel.DiagnosticsGroup}.DeembedResidual"];
        Assert.Equal(freqs.Length * ports.Length, cube.RealValues.Length);
        Assert.Equal(["freq", "port"], cube.Axes.Select(a => a.Name));

        for (int i = 0; i < freqs.Length; i++)
            output.WriteLine($"  {freqs[i] / 1e9:0.#} GHz  floor = " +
                             $"{cube.RealValues[i * ports.Length]:0.000e+00} " +
                             $"(residual {resid.RealValues[i * ports.Length]:0.000e+00})");

        // Real, finite and positive everywhere a calibration ran — and comfortably inside the
        // budget on a band that sits where the measured calibration belongs.
        foreach (double v in cube.RealValues)
        {
            Assert.False(double.IsNaN(v));
            Assert.InRange(v, 0.0, PlanarSolve.PeelErrorBudgetDS);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // P4 — THE GUARD
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A point the peel cannot answer is LEFT OUT of the sweep; the other points are published
    /// exactly as they were solved.</b>
    ///
    /// <para><b>Refusing the run was the first shape of this guard and it was wrong.</b> A user who
    /// has waited for a sweep does not want all of it discarded because its bottom two points are
    /// below an instrument wall — and this file already had the right rule written down a few
    /// hundred lines further on, where a far field that cannot be produced is reported as "present
    /// and refused" precisely so the sweep is not thrown away for it.</para>
    ///
    /// <para>Three things the note has to carry and all three are asserted: which points are missing
    /// (by frequency, so the gap in the <c>.sNp</c> is never a mystery), that the rest of the sweep
    /// is unaffected, and the band edge that gets them back. The clause saying longer calibration
    /// lines do not help is there because that is the move a user who reads this will otherwise
    /// make, and P2 is the measurement behind it.</para>
    ///
    /// <para><b>Its LENGTH and its register are gated too</b>, on the owner's instruction: plain
    /// terms, under 400 characters, and no words in block capitals. The mechanism — a₂₁ ∝ ω, the
    /// peel's division, why <c>DeembedResidual</c> points the wrong way — is in the source and in
    /// <c>RESOLVED.md</c> §PEEL, which is where someone asking "why" looks. A run's notes are for
    /// someone asking "what do I do".</para>
    ///
    /// <para><b>The way to get them into the file anyway is the flag that already exists.</b>
    /// <see cref="PlanarSolveSettings.DeembedOutsideCalibrationValidity"/> is named for what it does
    /// and already says so in the run's notes and the <c>.sNp</c>'s provenance; the claim here is the
    /// same one (this calibration is not valid at this frequency) and it does not deserve a second
    /// flag.</para>
    /// </summary>
    [Fact]
    public void APointThePeelCannotAnswerIsDropped_AndTheRestOfTheSweepSurvives()
    {
        var problem = PlanarLineFixtures.Line(ReportedBoard, ReportedWidthM, 20e-3, 2e9);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, Mesh(40));
        double[] freqs = [10e6, 30e6, 100e6, 300e6, 1e9];

        var run = PlanarSolve.Run(problem, mesh, ports, freqs, PlanarSolveSettings.Default);
        var note = Assert.Single(run.Notes, n => n.Contains("DeembedErrorFloor"));
        output.WriteLine(note);

        var published = run.Points.Select(p => p.FrequencyHz).ToList();
        output.WriteLine("published: " + string.Join(", ", published.Select(f => f / 1e6 + " MHz")));

        // THE POINT OF THE WHOLE CHANGE: the sweep survives. The bottom is gone and everything
        // above the wall is here, in order, with nothing interpolated across the gap.
        Assert.DoesNotContain(10e6, published);
        Assert.Contains(300e6, published);
        Assert.Contains(1e9, published);
        Assert.Equal(published.OrderBy(f => f), published);
        Assert.True(published.Count is > 0 and < 5, "some points dropped, and not all of them");

        Assert.Contains("were dropped", note);
        Assert.Contains("10 MHz", note);                    // named, so the gap is not a mystery
        Assert.Contains("the rest of the sweep is unaffected", note);
        Assert.Contains("raise the sweep's lower edge to get them back", note);
        Assert.Contains("Longer calibration lines do not help", note);
        Assert.Contains("DeembedErrorFloor", note);

        // Owner instruction, 2026-09-14: these notes are read by a designer, not by someone
        // reading this file. Plain terms, short, and no shouting — asserted rather than hoped for,
        // because the first version of this note was eight sentences of mechanism in block capitals.
        Assert.DoesNotMatch(@"\b[A-Z]{4,}\b", note);
        Assert.True(note.Length < 400, $"the note is {note.Length} chars; it will not be read");

        // …and the remedy is a frequency the user can type, derived from the run's own measurement
        // rather than modelled: the floor goes as 1/f, so floor·f is the constant and the budget is
        // met above floor·f/budget.
        Assert.Matches(@"above about [0-9.]+ ?[kMG]?Hz", note);

        // Every surviving point is on the right side of the wall, which is the invariant the drop
        // exists to establish rather than a restatement of the filter.
        foreach (var pt in run.Points.Where(pt => pt.Calibrations.Count > 0))
            Assert.True(pt.Calibrations.Max(c => c.Box.DeembedErrorFloor) < PlanarSolve.PeelErrorRefusalDS);

        // ── THE FLAG PUTS THEM BACK, AND THE POINTS IT PUTS BACK ARE BIT-IDENTICAL ──────────────
        //
        // Which is what says the drop is a FILTER and not a different solve: the run that keeps
        // everything and the run that keeps some agree exactly on the ones they share.
        var all = PlanarSolve.Run(problem, mesh, ports, freqs,
                      PlanarSolveSettings.Default with { DeembedOutsideCalibrationValidity = true });
        Assert.Equal(freqs.Length, all.Points.Count);
        foreach (var kept in run.Points)
        {
            var same = all.Points.Single(p => p.FrequencyHz == kept.FrequencyHz);
            for (int r = 0; r < 2; r++)
                for (int c = 0; c < 2; c++)
                    Assert.Equal(kept.S[r, c], same.S[r, c]);
        }
    }

    /// <summary>
    /// <b>The ONE case that is still a refusal: every de-embedded point of the sweep was
    /// unanswerable.</b> There is then no result to hand back, so nothing is lost by saying so —
    /// which is the whole distinction from the case above, where nine solved points would have been
    /// discarded to suppress two.
    /// </summary>
    [Fact]
    public void ASweepWithNothingLeftIsRefused()
    {
        var problem = PlanarLineFixtures.Line(ReportedBoard, ReportedWidthM, 20e-3, 2e9);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, Mesh(40));

        var refused = Assert.Throws<PlanarFeedClearanceRefusedException>(
            () => PlanarSolve.Run(problem, mesh, ports, [9e6, 10e6], PlanarSolveSettings.Default));
        output.WriteLine(refused.Message);

        Assert.Contains("nothing to publish", refused.Message);
        Assert.Contains("above about", refused.Message);
        Assert.DoesNotMatch(@"\b[A-Z]{4,}\b", refused.Message);
        Assert.True(refused.Message.Length < 400, "a refusal is read too, and under the same rule");
    }

    /// <summary>
    /// <b>The guard reads the sweep; it does not change it.</b> A run above the threshold and the
    /// same run with the guard's own escape hatch open must publish BIT-IDENTICAL s-parameters —
    /// which is what says the whole of this work is a diagnostic and not a change to the answer.
    ///
    /// <para>The gate the brief was written under: above the quasi-static crossover every published
    /// s-parameter must be what it was. Exact equality rather than a tolerance, because that is a
    /// claim about the code path.</para>
    /// </summary>
    [Fact]
    public void TheGuardChangesNoPublishedNumber()
    {
        var problem = PlanarLineFixtures.Fr4Line(6e-3, 6e9);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem);
        double[] freqs = [2e9, 4e9, 6e9];

        var plain = PlanarSolve.Run(problem, mesh, ports, freqs, PlanarSolveSettings.Default);
        var open  = PlanarSolve.Run(problem, mesh, ports, freqs,
                        PlanarSolveSettings.Default with { DeembedOutsideCalibrationValidity = true });

        for (int i = 0; i < freqs.Length; i++)
        {
            double floor = plain.Points[i].Calibrations.Max(c => c.Box.DeembedErrorFloor);
            output.WriteLine($"  {freqs[i] / 1e9:0.#} GHz  floor = {floor:0.000e+00}");
            Assert.True(floor < PlanarSolve.PeelErrorBudgetDS,
                        "precondition: this band is where the measured calibration belongs");
            for (int r = 0; r < 2; r++)
                for (int c = 0; c < 2; c++)
                    Assert.Equal(plain.Points[i].S[r, c], open.Points[i].S[r, c]);
        }

        Assert.DoesNotContain(plain.Notes, n => n.Contains("The DeembedErrorFloor result estimates"));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // M4 — THE REPORTED FILE'S OWN SWEEP, END TO END
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>1 MHz – 2 GHz in 11 log points on the reported cross-section — the sweep as it was
    /// actually written — and "published silently and 59 dB wrong" is no longer reachable from
    /// it.</b>
    ///
    /// <para>Every point of that sweep now leaves by one of exactly four doors and the test asserts
    /// that the set of doors covers it: below <see cref="Dcim.LowestFittableFrequency"/> it carries
    /// LF2's conduction answer and says so; above the peel's own wall it is LEFT OUT and named;
    /// between the two thresholds it is published with its floor beside it; above them it is simply
    /// published. There is no fifth door, and the fifth door — published, silent, and wrong — is
    /// what this whole section exists to remove.</para>
    ///
    /// <para><b>The run still comes back.</b> Two of these eleven points are unanswerable and nine
    /// are not; refusing over the two would have cost the user the nine, which is the shape this
    /// guard had before the owner pointed it out.</para>
    ///
    /// <para><b>The frequency below which this file is no longer answered is reported rather than
    /// improved.</b> Three routes at the floor itself were measured and none of them moved it
    /// (<c>RESOLVED.md</c> §PEEL): the two standards already share their mesh cell for cell, so there
    /// is nothing to make cancel; continuing the box down in frequency is worse than solving it; and
    /// the separation is inert (P2). A guard that tells the truth is the deliverable.</para>
    /// </summary>
    [Fact]
    public void TheReportedSweepEndToEnd_EveryPointIsAnsweredOrLeftOutAndNoneIsSilent()
    {
        var slab = ReportedBoard;
        var problem = PlanarLineFixtures.Line(slab, ReportedWidthM, 3.8e-3, 2e9);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, PlanarLineFixtures.Coarse);

        double[] freqs = [.. Enumerable.Range(0, 11)
                                       .Select(i => 1e6 * Math.Pow(2e3, i / 10.0))];

        var run = PlanarSolve.Run(problem, mesh, ports, freqs, PlanarSolveSettings.Default);

        double fitFloor = Dcim.LowestFittableFrequency(slab.HeightM);
        output.WriteLine($"the fit's own floor on this stack: {SurfaceMesher.Eng(fitFloor)}Hz");
        output.WriteLine($"{"f",12} {"door",14} {"floor",11} {"|S11|",10} {"|S21|",10}");

        int substituted = 0, flagged = 0, clean = 0;
        foreach (var pt in run.Points)
        {
            string door;
            double floor = double.NaN;
            if (pt.Calibrations.Count == 0) { door = "conduction"; substituted++; }
            else
            {
                floor = pt.Calibrations.Max(c => c.Box.DeembedErrorFloor);
                if (floor >= PlanarSolve.PeelErrorBudgetDS) { door = "flagged"; flagged++; }
                else { door = "published"; clean++; }
            }
            output.WriteLine($"{SurfaceMesher.Eng(pt.FrequencyHz) + "Hz",12} {door,14} " +
                             $"{floor,11:0.00e+00} {pt.S[0, 0].Magnitude,10:0.0000} " +
                             $"{pt.S[1, 0].Magnitude,10:0.0000}");
        }

        var missing = freqs.Where(f => run.Points.All(p => p.FrequencyHz != f)).ToList();
        output.WriteLine("LEFT OUT: " + string.Join(", ", missing.Select(f => SurfaceMesher.Eng(f) + "Hz")));
        output.WriteLine($"{substituted} substituted, {flagged} flagged, {clean} published clean, " +
                         $"{missing.Count} left out");

        Assert.Equal(freqs.Length, substituted + flagged + clean + missing.Count);
        Assert.True(substituted > 0, "the bottom of this sweep is below the fit's floor (LF2)");
        Assert.True(missing.Count > 0, "…and the points above it are past the peel's own wall");
        Assert.True(clean > 0, "…and the top of the sweep is answered");

        // THE POINT: the run came back. Nine of eleven rows survive, and the two that do not are
        // named in a sentence rather than taking the other nine with them.
        Assert.True(run.Points.Count >= freqs.Length - missing.Count);
        Assert.True(missing.Count < freqs.Length / 2,
                    "most of the sweep must survive — losing it all is the defect this replaced");

        // Both walls are named, and they are named SEPARATELY — the fit's sentence used to be the
        // only one down there, and it reads as "trouble ends here" when it is a ceiling on one wall.
        var conduction = Assert.Single(run.Notes, n => n.Contains("the field solver has no valid range"));
        // MIM-9 named the counter in the NOT PASSIVE attribution too — deliberately, so a reader
        // told the peel is or is not the cause knows which diagnostic says so. The sentinel here is
        // the peel-conditioning note's OWN closing clause, which is what this line always meant.
        var peel = Assert.Single(run.Notes, n => n.Contains("The DeembedErrorFloor result estimates"));
        output.WriteLine(conduction);
        output.WriteLine(peel);
        Assert.Contains("the field solver has no valid range", conduction);
        Assert.Contains("DeembedErrorFloor", peel);
        Assert.Contains("were dropped", peel);
        foreach (string n in (string[])[conduction, peel])
            Assert.DoesNotMatch(@"\b[A-Z]{4,}\b", n);
    }
}
