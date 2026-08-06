// L9's PHASE GATE — the product path, on the MMIC starter, through a TWO-LEVEL structure with vias.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// WHY THE GATE IS WORDED THE WAY IT IS, AND WHY IT IS NOT §11's OWN SENTENCE
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// §11's L9 row reads "Multi-layer structure with backside vias; agreement with published reference
// structures." Both halves were re-examined in L9e and neither survives as written:
//
//   • "agreement with published reference structures" — §10.9's rule is that golden data becomes a
//     gate only when the owner has approved it, and a published multilayer S-parameter almost always
//     arrives WITHOUT a verifiable stackup (no tanδ, no metal thickness, often no dielectric
//     tolerance). A gate resting on one measures the transcription, not the kernel, and when it
//     disagrees there is no way to tell which. That is the same reasoning that made L7b's Tier C3 a
//     reported non-result rather than a loosened tolerance.
//
//   • "backside vias" — **a backside via is not representable through the product path, and this is
//     a real finding rather than a scoping convenience.** A backside via joins a signal level to the
//     GROUND PLANE, and the ground plane is the laterally infinite plane the Green's function handles
//     analytically: it is not a meshed level. L9c's via basis is a rooftop spanning two ADJACENT
//     MESHED levels, so a via to ground would need an attachment (half) basis terminating on the
//     boundary, which does not exist. PlanarExtractor.BuildVias already reports it: a via whose span
//     names Backside Metal is dropped with a note, because Backside Metal is a ground reference and
//     therefore never an analysis level. What IS representable on this starter is the Metal1↔Metal2
//     post — the airbridge/crossover the MMIC stackup was built for — and that is what this gate uses.
//
// So the gate is three external-data-free claims, exactly as L9e proposed them:
//
//   1. a two-level structure WITH VIAS runs end to end through the product path and is physically
//      sane — reciprocal, passive, and carrying the signature a series via inductance must produce;
//   2. the two-level answer DEGENERATES onto the one-level reduction it must, as the inter-level
//      gap grows — an exact-limit check with no external data, and the one place the general kernel
//      is measured against the shipped one through the whole product path;
//   3. the wiring itself — one drawn layout, a .cem, the registry, the extractor's via handling and
//      the ℓ/w refusal — which is the claim most likely to break and the cheapest to check.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// TIERING, AND THE COST THAT DECIDED IT
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// L9d measured a de-embedded two-level point at 71.9 s (N = 514, one via, four single-level
// standards) against L8d's 7.66 s at N = 552 on one level — 9.4× at essentially the same N, because
// what moved is the per-entry cost of the general kernel, not the unknown count. Gates 1 and 2 are
// therefore Category=Benchmark and gate 3 is not: it refuses or extracts, and never fills a matrix.
//
// The fixtures are sized against TWO constraints that both bind here and neither of which bound L8:
//
//   • G_A^zz is validated to ρ/λ ≤ 0.1 (L9c Tier 5, and PlanarKernelSet.WithinValidatedRange asks it
//     of the mesh). λ is the FREE-SPACE wavelength — §10.7's own FR-4 hero is 0.67 λ across at
//     10 GHz — so 300 µm at 30 GHz is 0.03 λ, comfortably inside. **Do not widen that constant to
//     make a bigger fixture fit**: L9c measured the 14× error that justifies it.
//   • the mesh's own narrowest RUN per axis sets the pitch (L8b's D8), so a via footprint that lands
//     a few µm from a metal edge produces a sliver run and multiplies N for no physics. Every edge
//     in these fixtures is at least 20 µm from every other, which is why the via squares sit in the
//     middle of their metal rather than flush with it.

using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using RfCore.Data;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public class L9PhaseGateTests(ITestOutputHelper output) : IDisposable
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "crf-l9-gate-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    // ── The MMIC starter, and the three drawing layers this gate uses ─────────────────────────
    //
    // Metal1 (1,0) sits on the GaAs slab; Metal2 (2,0) sits 3 µm above it across an explicit air
    // layer; Via (3,0) is bound to the "Metal1-Metal2 Post" stackup entry, whose SpanFrom/SpanTo
    // name those two conductors. Backside Via (8,0) spans Metal1 → Backside Metal and is the one
    // this kernel cannot host — see the header.

    private static readonly LayerKey Metal1      = new(1, 0);
    private static readonly LayerKey Metal2      = new(2, 0);
    private static readonly LayerKey Post        = new(3, 0);
    private static readonly LayerKey BacksideVia = new(8, 0);

    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static RectShape Rect(LayerKey layer, double x0, double y0, double x1, double y1) =>
        new() { Layer = layer, X1 = Um(x0), Y1 = Um(y0), X2 = Um(x1), Y2 = Um(y1) };

    /// <summary>A via whose EQUAL-AREA SQUARE has the given side — the extractor replaces a round
    /// barrel by side = 0.886 × drill (L9d/D5), so a fixture that wants to control its own gridlines
    /// has to state the square and invert.</summary>
    private static ViaShape Via(LayerKey layer, double cx, double cy, double squareSideUm)
    {
        double drill = squareSideUm / (Math.Sqrt(Math.PI) / 2.0);
        return new ViaShape
        {
            Layer = layer, X = Um(cx), Y = Um(cy),
            DrillSize = Um(drill), PadSize = Um(1.3 * drill),
        };
    }

    private static LabelShape PortLabel(LayerKey layer, double xUm, double yUm, string name) =>
        new()
        {
            Layer = layer, X = Um(xUm), Y = Um(yUm), Text = name,
            Height = Um(20), IsPort = true,
        };

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // THE FIXTURES
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The airbridge — the one two-level structure with vias IN SERIES that this kernel can host.</b>
    ///
    /// <para>Metal1 runs in from each end and stops; Metal2 bridges the gap between them; a post at
    /// each end of the bridge carries the current up and back down. Both ports are on <b>Metal1</b>,
    /// which is not a stylistic choice — L9d's D3 refuses a port on any level that is not the slab's
    /// top, because C_pul comes from an electrostatic image series over a grounded slab and a wrong
    /// C_pul renormalises every published s-parameter rather than merely blurring it.</para>
    ///
    /// <para>Every x edge is ≥ 20 µm from every other (0, 20, 40, 80, 120, 180, 220, 260, 280, 300),
    /// and every y edge likewise (0, 30, 70, 100). That is what keeps N near L9d's own measured
    /// fixture instead of exploding on a sliver run.</para>
    /// </summary>
    private static LayoutView Airbridge(double lengthUm = 300, double widthUm = 100,
                                        double squareUm = 40, bool withVias = true)
    {
        double m1End = 0.4 * lengthUm;                    // Metal1 runs in to 120 µm
        double m1Start2 = 0.6 * lengthUm;                 // …and back from 180 µm
        double m2Lo = 0.0667 * lengthUm, m2Hi = 0.9333 * lengthUm;   // the bridge, 20…280 µm
        double via1 = 0.2 * lengthUm, via2 = 0.8 * lengthUm;         // 60 and 240 µm
        double cy = 0.5 * widthUm;

        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Rect(Metal1, 0,        0, m1End,    widthUm));
        view.Shapes.Add(Rect(Metal1, m1Start2, 0, lengthUm, widthUm));
        view.Shapes.Add(Rect(Metal2, m2Lo,     0, m2Hi,     widthUm));
        if (withVias)
        {
            view.Shapes.Add(Via(Post, via1, cy, squareUm));
            view.Shapes.Add(Via(Post, via2, cy, squareUm));
        }
        view.Shapes.Add(PortLabel(Metal1, 0,        cy, "P1"));
        view.Shapes.Add(PortLabel(Metal1, lengthUm, cy, "P2"));
        return view;
    }

    /// <summary>
    /// <b>Gate 2's fixture: a plain Metal1 line, optionally shadowed by a floating Metal2 strip.</b>
    /// No via — the upper strip is coupled and nothing else, which is what makes the degeneracy claim
    /// clean: as the gap grows its influence must vanish and the two-level answer must converge onto
    /// the ONE-level run of the same line, which takes L8's shipped one-slab path.
    /// </summary>
    private static LayoutView ShadowedLine(double lengthUm, double widthUm, bool withUpperStrip)
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.Add(Rect(Metal1, 0, 0, lengthUm, widthUm));
        // The upper strip is INSET from both ends, and that is not cosmetic: a port label standing on
        // metal on more than one level is refused by name (L9d/D3's own ambiguity refusal), because
        // picking the lower one would drive a different conductor with the same footprint.
        if (withUpperStrip)
            view.Shapes.Add(Rect(Metal2, 0.2 * lengthUm, 0, 0.8 * lengthUm, widthUm));
        view.Shapes.Add(PortLabel(Metal1, 0,        0.5 * widthUm, "P1"));
        view.Shapes.Add(PortLabel(Metal1, lengthUm, 0.5 * widthUm, "P2"));
        return view;
    }

    /// <summary>The MMIC starter with its air gap widened. Everything else — Metal1, Metal2, GaAs,
    /// the ground reference, both via entries — is the shipped technology, so the only thing that
    /// varies between gate 2's runs is the inter-level distance.</summary>
    private static Technology MmicWithAirGap(double gapUm)
    {
        var tech = StarterTechnologies.MmicGaAs();
        var air = tech.Stackup.Layers.First(l => l.Kind == StackupKind.Dielectric && l.Name == "Air");
        air.ThicknessDbu = Um(gapUm);
        return tech;
    }

    // ── Product-path plumbing, copied from L8PhaseGateTests so the two gates drive one path ───

    private static EmSetup Setup(string name, double[] freqsHz, PlanarMeshSettings? mesh = null) =>
        new()
        {
            Name         = name,
            LayoutRef    = "layout.clay",
            AnalysisKind = EmAnalysisKind.Planar,
            Frequency    = new Core.Design.FrequencySpec(
                Inv(freqsHz[0] / 1e9), Inv(freqsHz[^1] / 1e9),
                freqsHz.Length, Core.Design.SweepKind.Linear, "GHz", "GHz"),
            PlanarMesh   = mesh ?? PlanarMeshSettings.Default,
        };

    private static string Inv(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private EmRunResult Run(EmSetup setup, Technology tech, LayoutView view, bool expectOk = true)
    {
        Directory.CreateDirectory(_dir);
        var source = new EmLayoutSource("layout.clay", view, tech, Dbu);
        var r = EmRunService.Run(setup, source, Path.Combine(_dir, "results"));
        if (expectOk && r.Status != EmRunStatus.Ok)
            Assert.Fail($"{setup.Name}: {r.Error}\n  " + string.Join("\n  ", r.Warnings));
        return r;
    }

    private static System.Numerics.Complex S(DataSet d, int fi, int i, int j)
    {
        string g = d.Groups.First(x => d.CubesIn(x).ContainsKey("S"));
        var cube = d.CubesIn(g)["S"];
        int n = cube.Axes[1].Values.Length;
        return cube.ComplexValues[(fi * n + i) * n + j];
    }

    private static double[] Freq(DataSet d)
    {
        string g = d.Groups.First(x => d.CubesIn(x).ContainsKey("S"));
        return d.CubesIn(g)["S"].Axes[0].Values;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 3 — the wiring, the via extraction, and the two refusals.   ROUTINE (nothing is filled).
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The extractor's via path had no test at all before this gate</b> — L9d built
    /// <c>PlanarExtractor.BuildVias</c> and every test of it is engine-side, on hand-built
    /// <c>PlanarVia</c>s. This is the one place the drawn <c>ViaShape</c> → equal-area square →
    /// <c>PlanarVia</c> → vertical basis chain is exercised end to end, and it is also where the N
    /// this gate's Benchmark half is scheduled against is reported.
    /// </summary>
    [Fact]
    public void Gate3Wiring_ADrawnAirbridge_ExtractsAsTwoLevelsWithVias_AndMeshesWithVerticalUnknowns()
    {
        var tech = StarterTechnologies.MmicGaAs();
        var view = Airbridge();

        var r = PlanarExtractor.Extract(view.Shapes, tech, Dbu, 30e9);
        Assert.True(r.Ok, r.Refusal);

        var p = r.Problem!;
        Assert.Equal(2, p.Layers.Count);
        Assert.True(p.RequiresGeneralKernel);
        Assert.NotNull(p.MediumStack);
        Assert.Equal(2, p.ViaList.Count);
        Assert.True(p.LevelIsOnSlabTop(0));
        Assert.True(p.CanSolve().Ok, p.CanSolve().Reason);
        Assert.True(new PlanarKernel().CanSolve(p).Ok, new PlanarKernel().CanSolve(p).Reason);

        // The equal-area square, stated as an area rather than as four coordinates so the assertion
        // is about the quantity D5 says is preserved (the conducting cross-section) rather than about
        // the arithmetic that produced it.
        // (Within a DBU: the drill is drawn as an integer number of nanometres, so the square's own
        // side carries that rounding. The claim is the AREA, not the arithmetic.)
        double area = p.ViaList[0].Polygons[0].Area();
        Assert.InRange(area / (40e-6 * 40e-6), 0.999, 1.001);

        var mesh = SurfaceMesher.Mesh(p);
        Assert.True(mesh.CanSolve, mesh.Refusal);

        // At least one vertical basis per via, and generally MORE than one: a via footprint is a
        // region of the shared tensor grid, not a single cell, so it carries one z-rooftop per cell
        // it covers. Asserting exactly one per via would be asserting the mesh's own pitch.
        Assert.True(mesh.ViaUnknownCount >= p.ViaList.Count,
            $"{p.ViaList.Count} via(s) produced {mesh.ViaUnknownCount} vertical unknown(s).");

        output.WriteLine($"GATE 3 (wiring) — airbridge on {tech.Name}, shipping mesh at 30 GHz:");
        output.WriteLine($"  N = {mesh.UnknownCount} ({mesh.ViaUnknownCount} vertical), " +
                         $"{mesh.Mesh.Cells.Count} cells over {p.Layers.Count} levels");
    }

    /// <summary>
    /// <b>A backside via is dropped, and reported.</b> The header explains why this is structural
    /// rather than unimplemented; what matters for a gate is that it is never silent — a via that
    /// vanishes from a full-wave solve with no note is exactly the failure mode L9c's own mesher
    /// finding records ("a via footprint must contribute gridlines, or the via silently vanishes").
    /// </summary>
    [Fact]
    public void Gate3Wiring_ABacksideVia_IsDroppedWithANote_BecauseGroundIsNotAMeshedLevel()
    {
        var tech = StarterTechnologies.MmicGaAs();
        var view = Airbridge(withVias: false);
        view.Shapes.Add(Via(BacksideVia, 150, 50, 40));

        var r = PlanarExtractor.Extract(view.Shapes, tech, Dbu, 30e9);
        Assert.True(r.Ok, r.Refusal);
        Assert.Empty(r.Problem!.ViaList);
        Assert.Contains(r.Notes, n => n.Contains("not among this EM setup's analysis levels",
                                                 StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>The ℓ/w refusal is RETIRED, and the ELECTRICAL one still fires.</b>
    ///
    /// <para>L9e's own finding was that the midpoint rule froze <c>1/R</c> over the via's length,
    /// making its inductance high by ≈ 0.673·(ℓ/w) with no frequency in the condition; it shipped a
    /// geometric bound and this gate asserted it. The z-integral is now resolved
    /// (<c>ViaZIntegral</c>) and <c>ViaPhysicsTests.T3_1</c> measures that curve flat to 0.13% over
    /// ℓ/w ∈ [0.01, 5], so the bound is gone. <b>The test is UPDATED to the claim that replaces it</b>
    /// rather than deleted: the geometry it used to refuse must now be ACCEPTED, and the remaining
    /// electrical bound — which is about L9c's basis carrying a uniform current, not about any
    /// quadrature — must still refuse a via that is a real fraction of a wavelength.</para>
    ///
    /// <para>Both halves are asked of <c>CanSolve</c> rather than of a run, because an accepted case
    /// would otherwise SOLVE (~150 s at this mesh) and the claim here is about the verdict.</para>
    /// </summary>
    [Fact]
    public void Gate3Wiring_TheAspectRatioRefusalIsGone_AndTheElectricalOneStillFires()
    {
        var tech = StarterTechnologies.MmicGaAs();
        var kernel = new PlanarKernel();

        // The shipped post: 3 µm of air over a 40 µm square, ℓ/w = 0.075.
        var shipped = PlanarExtractor.Extract(Airbridge().Shapes, tech, Dbu, 30e9);
        Assert.True(shipped.Ok, shipped.Refusal);
        Assert.True(kernel.CanSolve(shipped.Problem!).Ok);

        // The same structure with a 4 µm square: ℓ/w = 0.75, which L9e refused and which is now
        // simply a narrow via. Its answer is the one the z-integral exists to make right.
        var narrow = PlanarExtractor.Extract(Airbridge(squareUm: 4).Shapes, tech, Dbu, 30e9);
        Assert.True(narrow.Ok, narrow.Refusal);
        var verdict = kernel.CanSolve(narrow.Problem!);
        Assert.True(verdict.Ok, "the ℓ/w bound is retired; this must no longer refuse: " + verdict.Reason);

        // …and the electrical bound still fires, on the quantity it is actually about. A 60 µm-thick
        // air gap at 30 GHz on this stackup is k·ℓ well past 0.05.
        var tall = PlanarExtractor.Extract(Airbridge().Shapes, MmicWithAirGap(60), Dbu, 30e9);
        Assert.True(tall.Ok, tall.Refusal);
        var no = kernel.CanSolve(tall.Problem!);
        Assert.False(no.Ok);
        Assert.Contains("UNIFORM", no.Reason!, StringComparison.Ordinal);

        output.WriteLine("GATE 3 — ℓ/w = 0.75 now ACCEPTED; the electrical refusal reads:\n  " + no.Reason);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 1 — the via CARRIES CURRENT, and the whole product path says so.   Category=Benchmark
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The assertion is a COMPARISON, not an absolute, and that is deliberate.</b>
    ///
    /// <para>The obvious gate — "a series via inductance shows as |S₁₁| rising with frequency" — is
    /// real physics and is reported below, but it cannot be the gate here: L9d measured the
    /// two-level de-embedding residual at worst |S₁₁| ≈ 1.0e-1 on a matched section, which is the
    /// same order as the effect. Gating on it would be gating on the residual.</para>
    ///
    /// <para>What IS unambiguous is that Metal1 has a GAP in it. With the posts, current crosses that
    /// gap through Metal2 and the structure transmits; without them, all that is left is a floating
    /// bridge coupled capacitively across the gap, and it does not. So the gate is <b>|S₂₁| with the
    /// vias against |S₂₁| without them</b> — one comparison, the same artwork, the same ports, the
    /// same calibration geometry, so the de-embedding residual is common to both and largely cancels.
    /// It is also the ONE assertion that would have caught the bug this gate actually found: before
    /// it, <c>PlanarExtractor</c> silently dropped every drawn via and the two runs would have been
    /// identical.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]   // two de-embedded points at N = 1,023 (~150 s each) plus two cheaper ones
    public void Gate1_TheViasCarryTheCurrentAcrossTheGap_AndTheStructureIsReciprocalAndPassive()
    {
        var tech = StarterTechnologies.MmicGaAs();
        double[] band = [10e9, 30e9];

        var with    = Run(Setup("l9-gate1-bridged", band), tech, Airbridge());
        var without = Run(Setup("l9-gate1-open",    band), tech, Airbridge(withVias: false));

        Assert.Equal(EmAnalysisKind.Planar, with.Kind);
        Assert.Equal(PlanarKernel.KernelName, with.KernelName);
        Assert.True(File.Exists(with.SnpPath!));
        Assert.Contains(PlanarKernel.DiagnosticsGroup, with.Data!.Groups);

        var freqs = Freq(with.Data!);
        output.WriteLine($"GATE 1 — airbridge on {tech.Name}, shipping mesh, " +
                         $"N = {with.PlanarMesh!.UnknownCount} ({with.PlanarMesh!.ViaUnknownCount} vertical) " +
                         $"against N = {without.PlanarMesh!.UnknownCount} with the posts removed");
        output.WriteLine("    f (GHz)   |S21| bridged   |S21| open   ratio   |S11| bridged");

        for (int i = 0; i < freqs.Length; i++)
        {
            double s21With    = S(with.Data!,    i, 1, 0).Magnitude;
            double s21Without = S(without.Data!, i, 1, 0).Magnitude;
            double s11With    = S(with.Data!,    i, 0, 0).Magnitude;

            output.WriteLine($"    {freqs[i] / 1e9,7:F2}   {s21With,13:F4}   {s21Without,10:F4}   " +
                             $"{s21With / s21Without,5:F1}   {s11With,13:F4}");

            Assert.True(s21With > 2.0 * s21Without,
                $"at {freqs[i] / 1e9:F2} GHz the bridged structure transmits |S21| = {s21With:F4} and " +
                $"the un-bridged one {s21Without:F4}. The posts are the only conducting path across " +
                "Metal1's gap, so they must dominate — if these are equal, the vertical basis is " +
                "carrying no current (or the vias never reached the mesh at all).");

            // R-prt-10/11: reciprocity and passivity carry over to kernel B. LOSSLESSNESS DOES NOT,
            // and none is asserted anywhere — an open planar structure radiates and launches surface
            // waves, so Σ|S|² < 1 is physics rather than a defect (L8a's own standing warning).
            Assert.Equal(S(with.Data!, i, 0, 1).Real,      S(with.Data!, i, 1, 0).Real,      9);
            Assert.Equal(S(with.Data!, i, 0, 1).Imaginary, S(with.Data!, i, 1, 0).Imaginary, 9);

            double power = s11With * s11With + s21With * s21With;
            Assert.True(power <= 1.0 + 1e-6,
                $"the bridged structure delivers Σ|S|² = {power:F4} into port 1 — above unity is not " +
                "radiation, it is a defect.");
        }

        // The series-inductance signature, REPORTED rather than gated — see the summary above.
        double lo = S(with.Data!, 0, 0, 0).Magnitude, hi = S(with.Data!, freqs.Length - 1, 0, 0).Magnitude;
        output.WriteLine(hi > lo
            ? $"  → |S11| rises {lo:F4} → {hi:F4} across the band: the series inductance of the two " +
              "posts and the bridge, which is what a via in the signal path must look like."
            : $"  → |S11| did NOT rise ({lo:F4} → {hi:F4}); at this length the line's own contribution " +
              "dominates. Reported, not gated — L9d measured the two-level de-embedding residual at " +
              "the same order as this effect.");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 2 — the two-level answer DEGENERATES onto the one-level one.      Category=Benchmark
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The exact-limit check, and the only place the general kernel is measured against the
    /// shipped one through the whole product path.</b>
    ///
    /// <para>One drawn Metal1 line with two ports, shadowed by a floating Metal2 strip that carries
    /// no port and no via — so the strip's only effect is coupling. Pull it away and that effect must
    /// vanish, leaving the answer the SHIPPED one-slab path gives for the same line: a two-level
    /// layout takes <c>PlanarProblem.RequiresGeneralKernel</c>'s general path, a one-level layout
    /// does not, so the two answers come from different kernels on the same artwork.</para>
    ///
    /// <para><b>The gate is the ASYMPTOTIC ordering, and the reason it is not simply "closer is
    /// stronger" is a measurement that contradicted this test's own first premise.</b> Written as
    /// "a strip 3 µm away must perturb the line more than one 50 µm away", it FAILED: 8.9e-4 against
    /// 7.1e-3, the wrong way round by 8×. That is not a kernel defect — it is what a <b>floating</b>
    /// conductor does. Its perturbation vanishes at BOTH ends: as the gap closes it is capacitively
    /// tied to the line and simply rides at the line's own potential (the pair behaves as one thicker
    /// conductor), and as the gap opens it decouples. In between there is a maximum. A driven or
    /// terminated second line would fall monotonically; a floating one does not, and the gate is
    /// stated in the regime where the limit is a theorem rather than a guess.</para>
    ///
    /// <para>So the two gated points are both well past that maximum, and the near one is kept and
    /// REPORTED so the non-monotonicity stays visible instead of being quietly designed around. How
    /// close the far case gets is bounded by the de-embedding residual L9d measured at ~1e-1 on
    /// two-level structures, not by the kernels' agreement — so a tight absolute tolerance would be a
    /// tolerance on that residual under another name.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]   // four de-embedded runs, no vias so the mesh is coarser than gate 1's
    public void Gate2_AFloatingSecondLevel_DegeneratesOntoTheShippedOneLevelAnswer_AsTheGapGrows()
    {
        const double lengthUm = 300, widthUm = 100;
        double[] band = [20e9];

        var one = Run(Setup("l9-gate2-one", band), MmicWithAirGap(3),
                      ShadowedLine(lengthUm, widthUm, withUpperStrip: false));

        double[] gaps = [3, 50, 400];
        var d = new double[gaps.Length];
        var n = new int[gaps.Length];

        for (int i = 0; i < gaps.Length; i++)
        {
            var r = Run(Setup($"l9-gate2-gap{gaps[i]:F0}", band), MmicWithAirGap(gaps[i]),
                        ShadowedLine(lengthUm, widthUm, withUpperStrip: true));
            d[i] = WorstAbsDiff(r.Data!, one.Data!);
            n[i] = r.PlanarMesh!.UnknownCount;
        }

        output.WriteLine($"GATE 2 — a {lengthUm:F0} × {widthUm:F0} µm Metal1 line at 20 GHz, shadowed " +
                         "by a floating Metal2 strip:");
        output.WriteLine($"    one level  (shipped one-slab path) : N = {one.PlanarMesh!.UnknownCount}");
        for (int i = 0; i < gaps.Length; i++)
            output.WriteLine($"    gap {gaps[i],4:F0} µm (general kernel)       : N = {n[i]}, " +
                             $"worst |ΔS| vs one level {d[i]:E3}");

        // The gated claim: past the maximum, opening the gap decouples the strip.
        Assert.True(d[2] < d[1],
            $"a strip {gaps[2]:F0} µm away perturbed the line MORE ({d[2]:E3}) than one {gaps[1]:F0} µm " +
            $"away ({d[1]:E3}). Past the floating-conductor maximum this must fall — if it does not, " +
            "the second level is not decoupling as it should.");

        // Bounded, loosely, so a gross failure is caught without inventing a tolerance the
        // de-embedding residual would own anyway (L9d measured ~1e-1 on two-level structures).
        Assert.True(d[2] < 0.1,
            $"at {gaps[2]:F0} µm the floating strip still moves the answer by {d[2]:E3} — that is not a " +
            "residual, it is a different structure.");

        output.WriteLine(d[0] < d[1]
            ? "  → the 3 µm point is BELOW the 50 µm one, which is the floating-conductor " +
              "non-monotonicity: a strip tight against the line rides at the line's own potential. " +
              "Reported, not gated — see this test's own summary."
            : "  → monotone across all three gaps at this geometry.");
    }

    /// <summary>Worst |ΔS| over every entry of two 2-port sweeps on the same frequency grid.</summary>
    private static double WorstAbsDiff(DataSet a, DataSet b)
    {
        var fa = Freq(a);
        double worst = 0;
        for (int i = 0; i < fa.Length; i++)
            for (int r = 0; r < 2; r++)
                for (int c = 0; c < 2; c++)
                    worst = Math.Max(worst, (S(a, i, r, c) - S(b, i, r, c)).Magnitude);
        return worst;
    }
}
