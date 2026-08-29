using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>Phase L9c — M3's fit, and M4's via basis, junction continuity and problem type.</b>
///
/// <para>D5's rule is the one that shapes this file: <b>junction continuity is a GATE, not a
/// comment</b>, and it is the R-mom-11 pattern — kernel A enforces "the frequency-independent
/// quantities really are computed once" with a COUNTER asserted at exactly 4, and L8c does the same
/// with <c>CoreFillCount</c>. So the total charge on a via basis and the current continuity at each
/// foot are asserted as NUMBERS, and because D2(a)'s construction makes both exact statements they
/// are asserted as EQUALITIES rather than to a tolerance.</para>
/// </summary>
public sealed class ViaBasisTests
{
    private readonly ITestOutputHelper _out;
    public ViaBasisTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// The MMIC two-level shape L9c exists for: 100 µm GaAs on a backside ground with a 3 µm spacer,
    /// metal at z = 100 µm (the interior interface) and z = 103 µm (the top surface), and a via
    /// through the spacer. Both levels are plain rectangles, so the mesh is exact and every count
    /// below is checkable by hand.
    /// </summary>
    private static PlanarProblem TwoLevelWithVia(double fHz = 10e9, bool withVia = true,
                                                 bool viaOffMetal = false)
    {
        var stack = LayerStacks.MmicTwoLevel;
        double zLow = stack.InterfaceZ[1], zHigh = stack.TopZ;

        static PlanarPolygon Rect(double x0, double y0, double x1, double y1) =>
            new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);

        // For the negative case the LOWER level stops short of the via, so the footprint is inside
        // the meshed extent but lands on bare dielectric on one of the two levels.
        var lowerShape = viaOffMetal ? Rect(0, 0, 150e-6, 100e-6) : Rect(0, 0, 400e-6, 100e-6);
        var lower = new PlanarConductorLayer("M1", [lowerShape], 4.1e7, 2e-6, zLow);
        var upper = new PlanarConductorLayer("M2", [Rect(0, 0, 400e-6, 100e-6)], 4.1e7, 3e-6, zHigh);

        // The via footprint sits inside both rectangles (or, for the negative case, clear of them).
        var footprint = Rect(180e-6, 30e-6, 220e-6, 70e-6);
        var vias = withVia ? new[] { new PlanarVia(0, 1, [footprint], 4.1e7) } : [];

        return new PlanarProblem([lower, upper], GroundedSlab.GaAsStarter, fHz,
                                 null, stack, vias);
    }

    // =========================================================================================
    // M4 / D5 — junction continuity, as NUMBERS.
    // =========================================================================================

    [Fact]
    public void M4_1_EveryBasis_ConservesChargeEXACTLY_ViasIncluded()
    {
        // R-via-3. ∫∇·f dS = +1 − 1 for a rooftop, and for a via basis too, because D2(a)'s
        // construction is the SAME construction one dimension over. Asserted as an equality: a basis
        // that conserves charge only to a tolerance puts a monopole on a cell, and the wrongness
        // looks like a bad mesh rather than like a bad basis.
        var report = SurfaceMesher.Mesh(TwoLevelWithVia());
        var mesh = report.Mesh;
        Assert.True(report.ViaUnknownCount > 0, "the fixture must actually produce via unknowns");

        int vertical = 0;
        foreach (var b in mesh.Bases)
        {
            var (ha, hb) = PlanarBasisFunctions.Halves(mesh, b);
            var ca = mesh.Cells[ha.CellIndex];
            var cb = mesh.Cells[hb.CellIndex];

            // ∫∇·f over a half is (Sign/Area)·Area = Sign. The EXACT statement is the one the fill
            // actually uses (L8c's D4 assembles the scalar block from the signs directly, never from
            // a (1/A)·A round trip), so that is what is asserted as an equality; the round trip is
            // asserted to machine precision beside it, and the 1-ulp residue there belongs to the
            // round trip rather than to the basis.
            Assert.Equal(+1.0, ha.Sign);
            Assert.Equal(-1.0, hb.Sign);
            Assert.Equal(0.0, ha.Sign + hb.Sign);
            Assert.Equal(0.0, ha.Sign / ca.Area * ca.Area + hb.Sign / cb.Area * cb.Area, 15);

            if (b.Direction != PlanarBasisDirection.Z) continue;
            vertical++;

            // The two feet are the SAME grid position on CONSECUTIVE levels — which is what makes
            // the pair a cell pair "in z" rather than an arbitrary association.
            Assert.Equal(ca.IX, cb.IX);
            Assert.Equal(ca.IY, cb.IY);
            Assert.Equal(ca.LayerIndex + 1, cb.LayerIndex);

            // …and the divergence pulse is signed by which level is the LOWER one.
            double xc = ca.CenterX, yc = ca.CenterY;
            Assert.Equal(+1.0 / ca.Area, PlanarBasisFunctions.Divergence(mesh, b, xc, yc, ca.LayerIndex));
            Assert.Equal(-1.0 / cb.Area, PlanarBasisFunctions.Divergence(mesh, b, xc, yc, cb.LayerIndex));
        }
        _out.WriteLine($"charge conservation: {mesh.Bases.Count} bases ({vertical} vertical), " +
                       $"Σ∫∇·f dS = 0 as an EQUALITY on every one.");
    }

    [Fact]
    public void M4_2_TheCurrentEnteringAViaFoot_EqualsTheCurrentLeavingIt_EXACTLY()
    {
        // The second half of D5. A via basis carries unit current across its shared FOOTPRINT, and
        // the vertical current density is uniform at 1/Area — so the integral over the footprint is
        // exactly 1 at both feet, and it is the same 1. This is the Z analogue of L8c's "∫f·û dℓ over
        // the shared edge is L·(1/L) = 1 A", and it is an equality for the same reason.
        var mesh = SurfaceMesher.Mesh(TwoLevelWithVia()).Mesh;
        int checkedCount = 0;

        foreach (var b in mesh.Bases)
        {
            if (b.Direction != PlanarBasisDirection.Z) continue;
            var ca = mesh.Cells[b.CellA];
            var cb = mesh.Cells[b.CellB];
            var (ha, hb) = PlanarBasisFunctions.Halves(mesh, b);

            // The density is EXACTLY 1/Area at both feet — the equality that matters, because the
            // two feet are the same grid cell on two levels and therefore the same area. The
            // integrated current is that times the area, equal at the two feet BIT-IDENTICALLY.
            double dLower = PlanarBasisFunctions.Weight(ca, ha, b.Direction, ca.CenterX, ca.CenterY);
            double dUpper = PlanarBasisFunctions.Weight(cb, hb, b.Direction, cb.CenterX, cb.CenterY);
            Assert.Equal(1.0 / ca.Area, dLower);
            Assert.Equal(1.0 / cb.Area, dUpper);
            Assert.Equal(dLower * ca.Area, dUpper * cb.Area);
            Assert.Equal(1.0, dLower * ca.Area, 15);

            // The uniform vertical density, read the other way, and zero off the footprint.
            Assert.Equal(1.0 / ca.Area, PlanarBasisFunctions.VerticalWeight(mesh, b, ca.CenterX, ca.CenterY));
            Assert.Equal(0.0, PlanarBasisFunctions.VerticalWeight(mesh, b, ca.XMin - 1e-9, ca.CenterY));

            // …and it carries NO in-plane current at all, which is what makes the vector block's
            // ẑẑ entry the only place it appears.
            Assert.Equal((0.0, 0.0), PlanarBasisFunctions.Evaluate(mesh, b, ca.CenterX, ca.CenterY));
            checkedCount++;
        }

        Assert.True(checkedCount > 0);
        _out.WriteLine($"junction continuity: {checkedCount} via bases, current in = current out = 1 A " +
                       $"EXACTLY (an equality, not a tolerance) — D2(a)'s construction, not a " +
                       $"constraint row.");
    }

    [Fact]
    public void M4_3_VerticalBases_SitAtTheEND_OfTheUnknownVector_AndAViaRenumbersNoHorizontalOne()
    {
        // R-via-5, and it is the ordering property that actually matters: ports, the current-density
        // map and de-embedding all index by this vector, so adding a via must not move a horizontal
        // unknown. Asserted by building the SAME problem with and without the via and comparing the
        // horizontal block element by element.
        var report = SurfaceMesher.Mesh(TwoLevelWithVia());
        var mesh = report.Mesh;
        int horizontal = mesh.Bases.Count - report.ViaUnknownCount;

        Assert.True(report.ViaUnknownCount > 0);
        for (int i = 0; i < mesh.Bases.Count; i++)
            Assert.Equal(i >= horizontal, mesh.Bases[i].Direction == PlanarBasisDirection.Z);

        // The vertical block is itself ordered (via as given, IY, IX) — integers throughout, no
        // dictionary and no floating-point tie, exactly as R-msh-2 requires of the cells.
        for (int i = horizontal + 1; i < mesh.Bases.Count; i++)
        {
            var p = mesh.Cells[mesh.Bases[i - 1].CellA];
            var q = mesh.Cells[mesh.Bases[i].CellA];
            Assert.True(p.IY < q.IY || (p.IY == q.IY && p.IX < q.IX),
                        $"vertical bases out of (IY, IX) order at {i}");
        }

        // NOTE what is NOT claimed: that adding a via leaves the horizontal unknowns untouched. It
        // does not, and it must not — a via footprint is Manhattan artwork and contributes GRIDLINES
        // (R-msh-1), so the shared grid genuinely refines around it. What R-via-5 buys is that the
        // horizontal block is a PREFIX of the unknown vector, so nothing downstream has to know
        // whether a mesh has vias in order to index it.
        _out.WriteLine($"R-via-5: {horizontal} horizontal unknowns form a PREFIX, then " +
                       $"{report.ViaUnknownCount} vertical ones in (via, IY, IX) order.");
    }

    [Fact]
    public void M4_4_AViaLandingOnBareDielectric_IsDroppedAndCounted_NotSolved()
    {
        // The third condition on a vertical basis, and the reason it is a condition: a via that lands
        // where one level has no metal has nothing to conserve charge against. Dropping it silently
        // would be as bad as solving it, so the mesher says so in a note.
        var report = SurfaceMesher.Mesh(TwoLevelWithVia(viaOffMetal: true));
        Assert.Equal(0, report.ViaUnknownCount);
        Assert.Contains(report.Notes, n => n.Contains("DROPPED"));
        _out.WriteLine(report.Notes.First(n => n.Contains("DROPPED")));
    }

    // =========================================================================================
    // M4 / D6 — the problem type.
    // =========================================================================================

    [Fact]
    public void M4_5_TheProblemType_GainsZAndVias_WithoutChangingAnyONESLAB_Answer()
    {
        // D6's decision, gated: the new members are OPTIONAL with a one-slab default, so the Ui-side
        // extractor goes on producing the old shape and L9d gets a design rather than a compile error.
        // The half of that which can go wrong silently is GuidedWavelengthM, whose rule changed from
        // "the slab's εᵣ" to "the maximum of εᵣµᵣ over every region" — on a one-slab problem those
        // are the same number, and this asserts it as an EQUALITY rather than trusting the argument.
        foreach (var slab in new[] { GroundedSlab.Fr4Starter, GroundedSlab.GaAsStarter })
        foreach (double f in new[] { 0.0, 2e9, 10e9 })
        {
            var p = new PlanarProblem([new PlanarConductorLayer("M", [], 5.8e7, 35e-6)], slab, f);
            double expected = f > 0
                ? EmConstants.C0 / (f * Math.Sqrt(Math.Max(1.0, slab.Material.EpsR)))
                : double.PositiveInfinity;
            Assert.Equal(expected, p.GuidedWavelengthM);
            Assert.Equal(slab.HeightM, p.LevelZ(0));                 // D2's default, unchanged
            Assert.True(p.CanSolve().Ok);
            Assert.Empty(p.ViaList);
        }

        // …and on the two-level stack it takes the FASTEST-slowing medium, which is the GaAs and not
        // the 2.7 spacer the top level actually sits on (R-msh-3's conservative direction).
        var two = TwoLevelWithVia();
        Assert.Equal(EmConstants.C0 / (10e9 * Math.Sqrt(12.9)), two.GuidedWavelengthM, 12);
        Assert.Equal(LayerStacks.MmicTwoLevel.InterfaceZ[1], two.LevelZ(0));
        Assert.Equal(LayerStacks.MmicTwoLevel.TopZ, two.LevelZ(1));
    }

    [Fact]
    public void M4_6_TheProblemTypesRefusals_AreEARNED()
    {
        // R-via-6: each refusal names a configuration that IS representable in the type and that no
        // part of the engine can answer — and the earned half is that the legitimate neighbour of
        // each one is accepted.
        var stack = LayerStacks.MmicTwoLevel;
        static PlanarConductorLayer L(string n, double z) => new(n, [], 4.1e7, 2e-6, z);

        var floating = new PlanarProblem([L("M1", 50e-6)], GroundedSlab.GaAsStarter, 10e9, null, stack);
        Assert.Contains("not an interface", floating.CanSolve().Reason);
        Assert.True(new PlanarProblem([L("M1", stack.InterfaceZ[1])], GroundedSlab.GaAsStarter,
                                      10e9, null, stack).CanSolve().Ok);

        var unordered = new PlanarProblem([L("M2", stack.TopZ), L("M1", stack.InterfaceZ[1])],
                                          GroundedSlab.GaAsStarter, 10e9, null, stack);
        Assert.Contains("BOTTOM-TO-TOP", unordered.CanSolve().Reason);

        var skipping = new PlanarProblem(
            [L("M0", stack.InterfaceZ[0]), L("M1", stack.InterfaceZ[1]), L("M2", stack.TopZ)],
            GroundedSlab.GaAsStarter, 10e9, null, stack,
            [new PlanarVia(0, 2, [], 4.1e7)]);
        Assert.Contains("skipping", skipping.CanSolve().Reason);

        _out.WriteLine("three earned refusals: a level off any interface, unordered levels, and a " +
                       "via that skips one — each with its legitimate neighbour accepted.");
    }

    // =========================================================================================
    // M3 / TIER 5 — DCIM AGAINST DIRECT INTEGRATION, PER HEIGHT PAIRING. THE REPORTED MEASUREMENT.
    // =========================================================================================

    [Fact]
    [Trait("Category", "Benchmark")]
    public void M3_Tier5_TheFitVsTheOracle_PerPairingPerComponent_IsTheReportedCurve()
    {
        // R-via-4's curve. Two measures, as L8a, L9a and L9b all report: the SCALED error |ΔG|·4πR —
        // and R rather than ρ, because normalising by ρ overstates the error whenever the vertical
        // separation dominates — and the STRICT relative error. The oracle is EvaluateInterior, whose
        // own Tier 3 rungs were passed before a single number here was believed.
        double lam = EmConstants.C0 / 10e9;
        double[] rhoOverLambda = [1e-3, 1e-2, 0.1, 0.5, 1.0];

        _out.WriteLine("stack                  pairing    component                 scaled(≤0.1λ)  scaled(≤1λ)   strict(≤0.1λ)");
        double worstAdmittedHorizontal = 0, worstAdmittedVertical = 0;

        foreach (var (name, stack) in LayerStacks.All())
        {
            if (stack.LayerCount < 1) continue;
            var g = new LayeredSpectralGreens(stack, 10e9);
            bool grounded = !stack.Bottom.IsOpen;
            double high = stack.TopZ;
            double low = stack.LayerCount >= 2 ? stack.InterfaceZ[stack.LayerCount - 1] : 0.5 * stack.TopZ;

            foreach (var (label, z, zp) in new[] { ("low-low ", low, low), ("low-high", high, low) })
            foreach (var k in new[] { GreensKernel.VectorPotential, GreensKernel.ScalarPotential,
                                      GreensKernel.VerticalVectorPotential,
                                      GreensKernel.MixedVectorPotential })
            {
                var m = Dcim.FitAtHeights(g, k, z, zp);
                double inRange = 0, all = 0, strict = 0;
                foreach (double rl in rhoOverLambda)
                {
                    double rho = rl * lam;
                    Complex exact = SommerfeldIntegral.EvaluateInterior(g, k, rho, z, zp).Value;
                    Complex fit = m.EvaluateAtHeights(rho);
                    double r = Math.Sqrt(rho * rho + (z - zp) * (z - zp));
                    double scaled = (exact - fit).Magnitude * 4 * Math.PI * r;
                    all = Math.Max(all, scaled);
                    if (rl > Dcim.ValidatedRhoOverLambdaAtHeights) continue;
                    inRange = Math.Max(inRange, scaled);
                    strict = Math.Max(strict, (exact - fit).Magnitude / Math.Max(exact.Magnitude, 1e-300));
                }
                _out.WriteLine($"{name[..Math.Min(22, name.Length)],-22} {label} {k,-24} " +
                               $"{inRange:E2}       {all:E2}      {strict:E2}");

                if (!grounded) continue;
                if (k == GreensKernel.VerticalVectorPotential)
                    worstAdmittedVertical = Math.Max(worstAdmittedVertical, inRange);
                else
                    worstAdmittedHorizontal = Math.Max(worstAdmittedHorizontal, inRange);
            }
        }

        _out.WriteLine(
            $"\nINSIDE Dcim.ValidatedRhoOverLambdaAtHeights = {Dcim.ValidatedRhoOverLambdaAtHeights}, on the " +
            $"GROUNDED stacks: G_A^xx / G_q / mixed worst {worstAdmittedHorizontal:E2}, " +
            $"G_A^zz worst {worstAdmittedVertical:E2}, both as a fraction of the free-space kernel. " +
            $"L9b's top-half-space envelope is 1.6e-2 and L8a's one-layer one 6e-3.\n" +
            $"THE CROSS-REGION PAIRING IS NOT WORSE THAN THE SAME-REGION ONE — which is the answer to " +
            $"the question §10.2's warning was about, and it is the OPPOSITE of what the branch point " +
            $"located in VerticalCurrentTests.M3_2 suggested. Locating a cut is not measuring its cost.");

        Assert.True(worstAdmittedHorizontal < 5e-2,
            $"G_A^xx/G_q/mixed inside the validated range: {worstAdmittedHorizontal:E2}");
        Assert.True(worstAdmittedVertical < 5e-2,
            $"G_A^zz inside the validated range: {worstAdmittedVertical:E2}");
    }

    [Fact]
    public void M3_TheSumRuleIsATHEOREM_HereToo_AndItWasMeasuredNotAssumed()
    {
        // The finding of M3, kept as its own rung because the first version of the fit asserted the
        // ABSENCE of this theorem and was wrong. M(k_zm) = 2j·k_zm·K vanishes at the source region's
        // own branch point — not because a reflection cancels (L8a's reason) but because the kernel is
        // simply FINITE there. Measured as O(k_zm): each decade of k_zm costs a decade of |M|.
        double worstRatio = 0;
        foreach (var (name, stack) in LayerStacks.All())
        {
            if (stack.LayerCount < 1) continue;
            var g = new LayeredSpectralGreens(stack, 10e9);
            double high = stack.TopZ;
            double low = stack.LayerCount >= 2 ? stack.InterfaceZ[stack.LayerCount - 1] : 0.5 * stack.TopZ;

            foreach (var (z, zp) in new[] { (low, low), (high, low) })
            foreach (var k in new[] { GreensKernel.VectorPotential, GreensKernel.ScalarPotential,
                                      GreensKernel.VerticalVectorPotential,
                                      GreensKernel.MixedVectorPotential })
            {
                Complex km = g.AsymptoticAtHeights(k, z, zp).ReferenceWavenumber;
                double prev = -1;
                foreach (double f in new[] { 1e-3, 1e-4, 1e-5 })
                {
                    Complex kzm = f * km;
                    Complex w = km * km - kzm * kzm;
                    double mag = (2.0 * Complex.ImaginaryOne * kzm *
                                  g.KernelAtHeights(k, Complex.Sqrt(w), z, zp)).Magnitude;
                    if (prev > 0)
                    {
                        double ratio = prev / Math.Max(mag, 1e-300);
                        worstRatio = Math.Max(worstRatio, Math.Abs(ratio - 10.0));
                        Assert.True(Math.Abs(ratio - 10.0) < 0.5,
                            $"{name} {k}: M(k_zm) is not O(k_zm) — a decade of k_zm changed |M| by {ratio:F2}×, " +
                            $"so ΣA_i = −(C_dir + C_img) is not a theorem here and the fit is pinning a guess.");
                    }
                    prev = mag;
                }
            }
        }
        _out.WriteLine($"M(k_zm) = 2j·k_zm·K vanishes linearly at k_zm = 0 on every stack, pairing and " +
                       $"component: worst departure from a decade-per-decade ratio {worstRatio:E2}. " +
                       $"The far-field sum rule is therefore a THEOREM for the interior fit too.");
    }

    // =========================================================================================
    // M5 — N AND THE COST FOR A REAL TWO-LEVEL STRUCTURE.
    // =========================================================================================

    [Fact]
    [Trait("Category", "Benchmark")]
    public void M5_NAndCost_ForATwoLevelStructureWithVias_AgainstR17()
    {
        // §8 item 5. L8b measured N = 552 for §10.7's own FR-4 hero on ONE level and up to 2,055 for
        // a library PCell; R17's ceiling is 5,000 and L8d measured 7.66 s per de-embedded point at
        // N = 552. This measures what a SECOND LEVEL and a via actually cost, on the same geometry.
        static PlanarPolygon Rect(double x0, double y0, double x1, double y1) =>
            new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);

        // §10.7's hero: a 2.9 mm × 20 mm line on 1.6 mm FR-4, plus a second level 0.2 mm above it.
        var twoLevelFr4 = new LayerStack(
            Termination.Pec,
            [new MediumLayer(1.6e-3, new EmMaterial(4.4, 0.02)),
             new MediumLayer(0.2e-3, new EmMaterial(4.4, 0.02))],
            Termination.Air);

        var hero = Rect(0, 0, 20e-3, 2.9e-3);
        var slab = GroundedSlab.Fr4Starter;

        var oneLevel = new PlanarProblem([new PlanarConductorLayer("M1", [hero], 5.8e7, 35e-6)],
                                         slab, 10e9);
        var r1 = SurfaceMesher.Mesh(oneLevel);

        var lower = new PlanarConductorLayer("M1", [hero], 5.8e7, 35e-6, twoLevelFr4.InterfaceZ[1]);
        var upper = new PlanarConductorLayer("M2", [hero], 5.8e7, 35e-6, twoLevelFr4.TopZ);
        var viaPad = Rect(9.6e-3, 1.15e-3, 10.4e-3, 1.75e-3);

        var twoNoVia = new PlanarProblem([lower, upper], slab, 10e9, null, twoLevelFr4);
        var twoWithVia = new PlanarProblem([lower, upper], slab, 10e9, null, twoLevelFr4,
                                           [new PlanarVia(0, 1, [viaPad], 5.8e7)]);
        var r2 = SurfaceMesher.Mesh(twoNoVia);
        var r3 = SurfaceMesher.Mesh(twoWithVia);

        _out.WriteLine($"§10.7's FR-4 hero (2.9 × 20 mm), 10 GHz, against R17's 5,000 ceiling:");
        _out.WriteLine($"  ONE level              N = {r1.UnknownCount,5}  cells {r1.CellCount,5}   " +
                       $"(L8b measured 552)");
        _out.WriteLine($"  TWO levels, no via     N = {r2.UnknownCount,5}  cells {r2.CellCount,5}   " +
                       $"= {(double)r2.UnknownCount / r1.UnknownCount:F2}× one level");
        _out.WriteLine($"  TWO levels + one via   N = {r3.UnknownCount,5}  cells {r3.CellCount,5}   " +
                       $"of which {r3.ViaUnknownCount} vertical; the via adds " +
                       $"{r3.UnknownCount - r2.UnknownCount} unknowns in total");
        _out.WriteLine($"  verdicts: {r1.Verdict} / {r2.Verdict} / {r3.Verdict}");
        _out.WriteLine(
            $"\nTHE VIA COSTS MORE THAN ITS OWN UNKNOWNS, and that is the shared-grid trade L8b's D8 " +
            $"bought the basis with. Its footprint is Manhattan artwork, so it contributes GRIDLINES " +
            $"(R-msh-1) and refines every level — {r3.ViaUnknownCount} vertical unknowns arrive with " +
            $"{r3.UnknownCount - r2.UnknownCount - r3.ViaUnknownCount} extra horizontal ones. Giving a " +
            $"via footprint the EDGE GRADING a conductor rim gets makes that far worse — measured at " +
            $"5.8× on a small fixture — and it is not given any, because the 1/√d edge current a rim " +
            $"has is not a feature of continuous metal.");
        _out.WriteLine(
            $"\nNOT MEASURED, because it is not built: the two-level FILL and therefore the seconds " +
            $"per frequency point. L8c's fill is O(N²) in its cores, so an N ratio of " +
            $"{(double)r3.UnknownCount / r1.UnknownCount:F2}× projects to " +
            $"{Math.Pow((double)r3.UnknownCount / r1.UnknownCount, 2):F1}× the fill — but L9a's own " +
            $"cost projection was wrong by 15–35× and D7 asks for a number that has been CHECKED. " +
            $"This one has not.");

        Assert.True(r3.ViaUnknownCount > 0);
        Assert.True(r3.UnknownCount > r2.UnknownCount);
    }

    // =========================================================================================
    // M5 — THE MULTI-LEVEL FILL.
    // =========================================================================================

    [Trait("Category", "Benchmark")]
    [Fact]
    public void M5_1_OnAOneLevelMeshWithNoVias_TheMultiLevelFill_ReproducesL8csOwnAnswer()
    {
        // The gate that matters most, and it is L9a's D5 precedent applied to the fill: the general
        // path is checked against the shipped one rather than replacing it. A one-level problem's
        // only height pairing is (h, h), which is the TOP HALF-SPACE pair L8a fits — so the two paths
        // reach the same matrix through completely different fits (Dcim.Fit vs Dcim.FitAtHeights,
        // referenced to k₀ and to k_m — which are the same number here, air on top) and completely
        // different extractions (FromDcim vs FromDcimAtHeights).
        static PlanarPolygon Rect(double x0, double y0, double x1, double y1) =>
            new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);

        var slab = GroundedSlab.Fr4Starter;
        double f = 10e9;
        var problem = new PlanarProblem(
            [new PlanarConductorLayer("M", [Rect(0, 0, 3e-3, 1e-3)], 5.8e7, 35e-6)], slab, f);
        var mesh = SurfaceMesher.Mesh(problem with { MaxFrequencyHz = f }).Mesh;
        var cores = PlanarFill.BuildCores(mesh);
        double omega = 2 * Math.PI * f;

        var shipped = PlanarFill.Fill(cores,
            PlanarKernelTerms.FromDcim(Dcim.Fit(new SpectralGreens(slab, f), GreensKernel.VectorPotential)),
            PlanarKernelTerms.FromDcim(Dcim.Fit(new SpectralGreens(slab, f), GreensKernel.ScalarPotential)),
            omega);

        var set = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, f))
                      .For(cores);
        var general = PlanarFill.FillMultiLevel(cores, set, PlanarLevels.From(problem), omega);

        double worst = 0, scale = 0;
        for (int i = 0; i < mesh.Bases.Count; i++)
        for (int j = 0; j < mesh.Bases.Count; j++)
        {
            worst = Math.Max(worst, (shipped[i, j] - general[i, j]).Magnitude);
            scale = Math.Max(scale, shipped[i, j].Magnitude);
        }
        _out.WriteLine($"multi-level fill vs L8c's on N = {mesh.Bases.Count}: worst |ΔZ|/max|Z| = " +
                       $"{worst / scale:E3}, through two independent fits and two independent " +
                       $"extractions. {set.FitCount} fits were asked for (one pairing, two components).");
        Assert.Equal(2, set.FitCount);
        Assert.True(worst / scale < 5e-3,
            $"the multi-level fill must reproduce L8c's on a one-level mesh: {worst / scale:E3}");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void M5_2_TheAssembledMatrix_IsSymmetric_AndTheMixedBlockIsWhatMakesThatNonTrivial()
    {
        // R-fil-2 survives the new blocks. It is structural for the scalar and ẑẑ blocks (computed on
        // m ≤ n and mirrored) — but the MIXED block is where it could have gone wrong silently:
        // G_A^uz = −G_A^zu with the heights swapped, and only the ∂/∂x being ODD in x − x′ supplies
        // the second sign that makes Z[m,n] = Z[n,m]. A formulation carrying only ẑx̂, or an even
        // integrand, gives a matrix with an entry on one side of the diagonal and not the other.
        var problem = TwoLevelWithVia();
        var report = SurfaceMesher.Mesh(problem);
        var mesh = report.Mesh;
        var cores = PlanarFill.BuildCores(mesh);
        double f = 10e9;
        var set = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, f)).For(cores);
        var z = PlanarFill.FillMultiLevel(cores, set, PlanarLevels.From(problem), 2 * Math.PI * f);

        int n = mesh.Bases.Count;
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            Assert.Equal(z[i, j], z[j, i]);

        // …and the mixed block is not zero, so the symmetry above cannot pass for the wrong reason.
        int horizontal = n - report.ViaUnknownCount;
        double biggestMixed = 0;
        for (int i = horizontal; i < n; i++)
        for (int j = 0; j < horizontal; j++)
            biggestMixed = Math.Max(biggestMixed, z[i, j].Magnitude);
        Assert.True(biggestMixed > 0, "the ẑx̂ block is identically zero — the symmetry test above is vacuous");

        _out.WriteLine($"N = {n} ({report.ViaUnknownCount} vertical), Z symmetric bit-identically, " +
                       $"largest mixed (ẑx̂) entry {biggestMixed:E3} Ω — non-zero, so the symmetry is " +
                       $"a real statement.");
    }

    // =========================================================================================
    // M3 — THE GROUND-ATTACHMENT BASIS THROUGH THE MESHER AND THE FILL
    // (brief-ground-vias-and-interior-electrostatics, R-gv-5 / R-gv-7 / D7)
    // =========================================================================================

    /// <summary>The same MMIC fixture, plus a BACKSIDE via from the lower level down to the ground
    /// plane — so one mesh carries an attachment basis, an ordinary interior via, and a mixed
    /// block at once. That combination is the MMIC starter's own shape and is exactly what a
    /// per-basis current-direction slip would break.</summary>
    private static PlanarProblem TwoLevelWithViaAndGroundVia(double fHz = 10e9)
    {
        var basis = TwoLevelWithVia(fHz);
        static PlanarPolygon Rect(double x0, double y0, double x1, double y1) =>
            new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);

        var backside = new PlanarVia(PlanarVia.GroundTerminal, 0,
                                     [Rect(60e-6, 30e-6, 100e-6, 70e-6)], 4.1e7);
        return basis with { Vias = [.. basis.ViaList, backside] };
    }

    [Fact]
    public void M6_1_RgV5_AGroundViaFootprint_ContributesGRIDLINES_AndIsNotEDGEGRADED()
    {
        // R-gv-5. Both of L9c's silent mesher failures apply to the new path and it does NOT inherit
        // their tests — they were written for the two-meshed-feet case.
        //
        //   • no gridlines ⇒ the via VANISHES with no error (L9c measured zero vertical unknowns for
        //     a 40 µm footprint that fell between bulk cell centres);
        //   • edge grading ⇒ 2,448 unknowns against 424, a 5.8× cost for one via, because a via
        //     footprint is an interior feature of continuous metal with no 1/√d rim singularity.
        var report = SurfaceMesher.Mesh(TwoLevelWithViaAndGroundVia());
        var mesh   = report.Mesh;

        var ground = mesh.Bases.Where(b => b.AttachesToGround).ToList();
        Assert.NotEmpty(ground);

        // GRIDLINES: the footprint's own four edges are gridlines, so the grid tiles it EXACTLY —
        // the basis count is a property of the geometry rather than of where the via happened to fall.
        foreach (double edge in new[] { 60e-6, 100e-6 })
            Assert.Contains(mesh.GridX, g => Math.Abs(g - edge) < 1e-15);
        foreach (double edge in new[] { 30e-6, 70e-6 })
            Assert.Contains(mesh.GridY, g => Math.Abs(g - edge) < 1e-15);

        // NO EDGE GRADING: adding the ground via must not multiply the unknown count. A graded
        // footprint would add EdgeCells fans on four edges and blow this up several-fold.
        var without = SurfaceMesher.Mesh(TwoLevelWithVia());
        double growth = report.UnknownCount / (double)without.UnknownCount;
        Assert.True(growth < 1.30,
            $"a ground-via footprint must contribute hard gridlines only, not an edge-graded fan: " +
            $"N went {without.UnknownCount} → {report.UnknownCount} ({growth:F2}×)");

        // Every attachment basis names ONE meshed foot, on the level the via lands on.
        foreach (var b in ground)
        {
            Assert.Equal(b.CellA, b.CellB);
            Assert.Equal(0, mesh.Cells[b.CellA].LayerIndex);
            Assert.Equal(0, b.LayerIndex);
        }

        _out.WriteLine($"ground via: {ground.Count} attachment basis/bases; N {without.UnknownCount} → " +
                       $"{report.UnknownCount} ({growth:F2}×) — gridlines only, no graded fan.");
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void M6_2_RgV7_ReciprocityIsSTRUCTURAL_WithAnAttachmentANDAnInteriorViaInOneMesh()
    {
        // R-gv-7. The failure this exists for is specific: if the attachment's current ran the
        // OPPOSITE way to an interior via's, every ẑẑ cross term between them would carry the wrong
        // sign — and a matrix that is still symmetric would still be wrong. So the mesh must hold
        // BOTH kinds, and the cross block between them must be asserted non-zero.
        var problem = TwoLevelWithViaAndGroundVia();
        var report  = SurfaceMesher.Mesh(problem);
        var mesh    = report.Mesh;
        var cores   = PlanarFill.BuildCores(mesh);
        double f    = 10e9;
        var set = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, f)).For(cores);
        var z   = PlanarFill.FillMultiLevel(cores, set, PlanarLevels.From(problem), 2 * Math.PI * f);

        int n = mesh.Bases.Count;
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            Assert.Equal(z[i, j], z[j, i]);

        // …and it cannot pass for the wrong reason: the attachment↔interior-via cross term and the
        // mixed (ẑx̂) block are both non-zero.
        var attach   = Enumerable.Range(0, n).Where(i => mesh.Bases[i].AttachesToGround).ToArray();
        var interior = Enumerable.Range(0, n).Where(i => mesh.Bases[i].Direction == PlanarBasisDirection.Z
                                                      && !mesh.Bases[i].AttachesToGround).ToArray();
        var horizontal = Enumerable.Range(0, n)
            .Where(i => mesh.Bases[i].Direction != PlanarBasisDirection.Z).ToArray();
        Assert.NotEmpty(attach);
        Assert.NotEmpty(interior);

        double crossZz = attach.SelectMany(i => interior, (i, j) => z[i, j].Magnitude).Max();
        double mixed   = attach.SelectMany(i => horizontal, (i, j) => z[i, j].Magnitude).Max();
        Assert.True(crossZz > 0, "the attachment↔interior-via ẑẑ cross block is identically zero — " +
                                 "the symmetry assertion above is vacuous for the case it exists for");
        Assert.True(mixed > 0, "the attachment's mixed (ẑx̂) block is identically zero");

        _out.WriteLine($"N = {n} ({attach.Length} attachment, {interior.Length} interior vertical); " +
                       $"Z symmetric bit-identically. Largest attachment↔via ẑẑ {crossZz:E3} Ω, " +
                       $"largest attachment↔horizontal ẑx̂ {mixed:E3} Ω — both non-zero.");
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void M6_3_D7_AProblemWithNOGroundVia_IsBITIDENTICAL_ToBeforeTheAttachmentBasisExisted()
    {
        // D7. The gate for everything this does not touch, pinned by RECONSTRUCTION at full
        // precision on a small mesh rather than by a tolerance — the R-viz-1 / M5_6 precedent.
        //
        // The attachment basis added an optional field to PlanarBasis, a branch to Halves, a branch
        // to SpanOf and a branch to the current-density reduction. None of them may move a structure
        // that has no ground via by one ulp, and "the tests still pass" is not that statement.
        var problem = TwoLevelWithVia();
        var mesh    = SurfaceMesher.Mesh(problem).Mesh;
        var cores   = PlanarFill.BuildCores(mesh);
        double f    = 10e9;
        var levels  = PlanarLevels.From(problem);

        var setA = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, f)).For(cores);
        var a    = PlanarFill.FillMultiLevel(cores, setA, levels, 2 * Math.PI * f);
        var setB = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, f)).For(cores);
        var b    = PlanarFill.FillMultiLevel(cores, setB, levels, 2 * Math.PI * f);

        // Not a single basis in this mesh attaches to ground, and every one of them takes exactly
        // the pre-existing branch.
        Assert.DoesNotContain(mesh.Bases, x => x.AttachesToGround);
        foreach (var x in mesh.Bases)
        {
            var (ha, hb) = PlanarBasisFunctions.Halves(mesh, x);
            Assert.Equal(+1.0, ha.Sign);
            Assert.Equal(-1.0, hb.Sign);
            Assert.Equal(0.0, PlanarBasisFunctions.NetCharge(mesh, x));   // D5's own equality, intact
        }

        for (int i = 0; i < a.RowCount; i++)
        for (int j = 0; j < a.ColCount; j++)
            Assert.Equal(a[i, j], b[i, j]);

        _out.WriteLine($"N = {a.RowCount}: no attachment basis, every Halves/NetCharge unchanged, and " +
                       $"the matrix reproduces itself bit for bit.");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void M5_3_TheKernelSet_FitsONCEPerHeightPAIRING_NotPerCellPair()
    {
        // D7's counter, and it is R-mom-11's pattern: kernel A asserts MatrixFillCount == 4 for a
        // 3-point AND a 1001-point sweep rather than commenting that the fill is reused. The failure
        // this catches is the expensive one — a refactor that starts asking the set per CELL PAIR
        // instead of per PAIRING turns 12 fits per frequency into O(N²) of them, and nothing else
        // would notice until a sweep took an hour.
        var problem = TwoLevelWithVia();
        var mesh = SurfaceMesher.Mesh(problem).Mesh;
        var cores = PlanarFill.BuildCores(mesh);
        double f = 10e9;
        var set = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, f)).For(cores);

        PlanarFill.FillMultiLevel(cores, set, PlanarLevels.From(problem), 2 * Math.PI * f);
        int after = set.FitCount;
        PlanarFill.FillMultiLevel(cores, set, PlanarLevels.From(problem), 2 * Math.PI * f);

        Assert.Equal(after, set.FitCount);

        // THE BOUND IS UPDATED, NOT LOOSENED, and the arithmetic is written out so the next change to
        // it has to be justified the same way. L9c's 9 fits were: 3 horizontal pairings × 2 components
        // (G_A^xx and G_q), ONE ẑẑ pairing at the via's midpoint, and 2 mixed ones. The via's
        // z-integral replaces the last two groups with a Gauss rule: the ẑẑ block asks for the
        // unordered node pairs of one span, n_z(n_z+1)/2, and the mixed block for one per (node,
        // level), n_z × levels. It is still ONE fit per PAIRING — the failure this counter exists to
        // catch is a refactor that starts asking per CELL PAIR, which would be O(N²) — and it is still
        // independent of N.
        int nz = PlanarFillSettings.Default.ViaZNodes;
        int expected = 6 + nz * (nz + 1) / 2 + nz * problem.Layers.Count;
        Assert.Equal(expected, after);
        _out.WriteLine($"D7's counter: {after} fits for a two-level structure with a via " +
                       $"({mesh.Cells.Count} cells, N = {mesh.Bases.Count}) at n_z = {nz} — and the " +
                       $"SECOND fill asked for none. L9c's midpoint rule asked for 9; the z-integral " +
                       $"adds {after - 9}, which M1 measured at 0.28% of a de-embedded point.");
    }

    [Fact]
    public void M5_7_AViaCrossingADielectricInterface_IsRefusedByName_AndItsNeighbourIsAccepted()
    {
        // The one thing the z-integral's closed form COSTS, refused rather than approximated. The
        // asymptote coefficients it integrates are the source REGION's own Fresnel coefficients, so a
        // via with two regions under it has two different sets of them and the straddling height pairs
        // have none — a single closed form over the whole span would put back a different function
        // from the one that was removed, and the answer would be a plausible wrong inductance rather
        // than an obvious failure.
        //
        // R-mom-17's shape: the legitimate NEIGHBOUR is accepted in the same test, so the refusal is
        // scoped to the crossing rather than to "vias in a multi-layer medium".
        static PlanarPolygon Rect(double x0, double y0, double x1, double y1) =>
            new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);

        // Three medium layers, with an interface at 101.5 µm that carries NO conductor level.
        var split = new LayerStack(
            Termination.Pec,
            [new MediumLayer(100e-6, new EmMaterial(12.9, 0.002)),
             new MediumLayer(1.5e-6, new EmMaterial(3.9, 0.002)),
             new MediumLayer(1.5e-6, new EmMaterial(2.7, 0.002))],
            Termination.Air);

        var crossing = new PlanarProblem(
            [new PlanarConductorLayer("M1", [Rect(0, 0, 400e-6, 100e-6)], 4.1e7, 2e-6, split.InterfaceZ[1]),
             new PlanarConductorLayer("M2", [Rect(0, 0, 400e-6, 100e-6)], 4.1e7, 3e-6, split.TopZ)],
            GroundedSlab.GaAsStarter, 10e9, null, split,
            [new PlanarVia(0, 1, [Rect(180e-6, 30e-6, 220e-6, 70e-6)], 4.1e7)]);

        var no = new PlanarKernel().CanSolve(crossing);
        Assert.False(no.Ok);
        Assert.Contains("crosses a dielectric interface", no.Reason);

        // …and the same artwork on a medium with no intervening interface solves, which is what makes
        // the refusal about the crossing.
        Assert.True(new PlanarKernel().CanSolve(TwoLevelWithVia()).Ok);

        _out.WriteLine(no.Reason!);
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void M5_6_RvIz1_AProblemWithNoVERTICALBasis_IsBITIDENTICALUnderTheZQuadraturesSETTING()
    {
        // R-viz-1. The z-integral must not move a single answer that has no via in it — L9a's D5 and
        // L9d's R-mlp-1 precedent, and pinned the same way: by RECONSTRUCTION at full precision rather
        // than by a tolerance.
        //
        // The strongest available statement is that ViaZNodes is *unreachable* without a vertical
        // basis: sweep it over a range that changes the via answer by orders of magnitude, on a
        // two-level problem with no via and on a one-level one, and require the last bit to be equal.
        // That covers every calibration standard (always single-level), every L8 path and the whole
        // horizontal vector and scalar blocks in one assertion.
        // The fixture is deliberately TINY — R-viz-1 is a bit-identity, so the mesh only has to be
        // big enough to exercise every block (scalar, both horizontal vector directions, both height
        // pairings), not big enough to be a physical answer. §Gate command's "do not add a routine
        // test that fills a matrix" is about the BUDGET, and this one costs under a second.
        static PlanarPolygon Rect(double x0, double y0, double x1, double y1) =>
            new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);

        double f = 5e9;
        var stack = LayerStacks.MmicTwoLevel;
        var twoLevel = new PlanarProblem(
            [new PlanarConductorLayer("M1", [Rect(0, 0, 60e-6, 30e-6)], 4.1e7, 2e-6, stack.InterfaceZ[1]),
             new PlanarConductorLayer("M2", [Rect(0, 0, 60e-6, 30e-6)], 4.1e7, 3e-6, stack.TopZ)],
            GroundedSlab.GaAsStarter, f, null, stack);
        var oneLevel = new PlanarProblem(
            [new PlanarConductorLayer("M", [Rect(0, 0, 60e-6, 30e-6)], 4.1e7, 2e-6)],
            GroundedSlab.GaAsStarter, f);

        // Edge grading off: it multiplies N for a physical reason that has nothing to do with the
        // claim being made here, and the claim is about the last bit of every entry rather than about
        // how many entries there are.
        var coarse = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 20, EdgeMesh: false);

        foreach (var problem in new[] { twoLevel, oneLevel })
        {
            var mesh = SurfaceMesher.Mesh(problem, coarse).Mesh;
            var levels = PlanarLevels.From(problem);
            NumFlat.Mat<Complex>? reference = null;

            foreach (int nz in new[] { 1, 4, 9 })
            {
                var st = PlanarFillSettings.Default with { ViaZNodes = nz, ViaZStaticNodes = nz + 3 };
                var cores = PlanarFill.BuildCores(mesh, st);
                var set = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, f))
                              .For(cores);
                var z = PlanarFill.FillMultiLevel(cores, set, levels, 2 * Math.PI * f);

                if (reference is null) { reference = z; continue; }
                for (int i = 0; i < z.RowCount; i++)
                for (int j = 0; j < z.ColCount; j++)
                    Assert.Equal(reference.Value[i, j], z[i, j]);
            }
            _out.WriteLine($"{problem.Layers.Count} level(s), no via, N = {mesh.Bases.Count}: " +
                           $"bit-identical across ViaZNodes ∈ {{1, 4, 9}}.");
        }
    }

    [Fact]
    public void M5_4_AnELECTRICALLYLongVia_IsRefusedBecauseItsCurrentCannotBeUniform()
    {
        // R-mom-17 / R-via-6, earned — and re-worded when the z-integral landed. The refusal used to
        // be about the MIDPOINT RULE and its O((kℓ)²); the midpoint rule is gone, and what k·ℓ ≤ 0.05
        // still bounds is L9c's BASIS: one z-rooftop per gap, so a uniform current along the via.
        var levels = PlanarLevels.From(TwoLevelWithVia());
        double k10 = 2 * Math.PI * 10e9 / EmConstants.C0 * Math.Sqrt(12.9);
        Assert.True(levels.CanRepresentVias(k10).Ok);

        var longVia = new PlanarLevels([0.0, 5e-3]);
        var no = longVia.CanRepresentVias(k10);
        Assert.False(no.Ok);
        Assert.Contains("UNIFORM", no.Reason);
        Assert.Contains("BASIS, not on the quadrature", no.Reason);
        _out.WriteLine($"3 µm spacer at 10 GHz: k·ℓ = {k10 * levels.LengthOf(0):G3}, accepted. " +
                       $"5 mm via: k·ℓ = {k10 * 5e-3:G3}, refused.");
    }

    // =========================================================================================
    // M1 (brief-gazz-accuracy-ceiling) — DO THE EXISTING KNOBS ALREADY FIX G_A^zz?
    //
    // §0.2 item 5: DcimSettings' defaults were chosen on the ONE-LAYER (L8a) fit and every one of
    // them is a trade BETWEEN components, settled by making all four share one setting.
    // BranchSamples is 0 because it "buys FR-4's G_A another 30× and costs FR-4's G_q 4×, and G_q is
    // the kernel that carries the charge, so it wins the trade." BranchPointOrders is 1 for the same
    // shape of reason. **The failing component is G_A^zz alone and G_q at heights is fine, so that
    // trade is not the one this case faces** — and neither knob has ever been measured at interior
    // heights, where L9c separately established the sum rule is a theorem for a different reason.
    // =========================================================================================

    /// <summary>The grounded stacks and both interior pairings — the cases L9c's Tier 5 curve is
    /// reported on and the only ones <c>WithinValidatedRangeAtHeights</c> admits at all.</summary>
    private static IEnumerable<(string Name, LayerStack Stack, string Pairing, double Z, double Zp)>
        GroundedInteriorCases()
    {
        foreach (var (name, stack) in LayerStacks.All())
        {
            if (stack.LayerCount < 1 || stack.Bottom.IsOpen) continue;
            double high = stack.TopZ;
            double low  = stack.LayerCount >= 2 ? stack.InterfaceZ[stack.LayerCount - 1] : 0.5 * stack.TopZ;
            yield return (name, stack, "low-low ", low, low);
            yield return (name, stack, "low-high", high, low);
        }
    }

    [Fact]
    [Trait("Category", "Benchmark")]   // ~40 configs × 8 cases of Dcim.FitAtHeights, ~2 min
    public void M1_1_RzZ2_TheKNOBSWEEP_ForGAzzAlone_AtINTERIORHeights()
    {
        // The oracle does not depend on the settings, so it is evaluated ONCE per (case, ρ) and
        // every configuration is then a fit plus five model evaluations. EvaluateInterior is
        // 40-50 ms a point and this is what keeps the sweep affordable rather than prohibitive.
        const double f = 10e9;
        double lam = EmConstants.C0 / f;
        double[] rhoOverLambda = [1e-3, 1e-2, 0.1, 0.5, 1.0];

        var cases  = GroundedInteriorCases().ToArray();
        var greens = cases.Select(c => new LayeredSpectralGreens(c.Stack, f)).ToArray();
        var oracle = new Complex[cases.Length][];
        for (int i = 0; i < cases.Length; i++)
        {
            oracle[i] = new Complex[rhoOverLambda.Length];
            for (int r = 0; r < rhoOverLambda.Length; r++)
                oracle[i][r] = SommerfeldIntegral.EvaluateInterior(
                    greens[i], GreensKernel.VerticalVectorPotential,
                    rhoOverLambda[r] * lam, cases[i].Z, cases[i].Zp).Value;
        }

        // Worst SCALED error (|ΔG|·4πR, the measure a fill experiences — R rather than ρ, because
        // normalising by ρ overstates it whenever the vertical separation dominates), over ρ/λ ≤ 0.1
        // and separately ≤ 1, across every grounded case.
        (double In, double All, double WorstAll, string WorstCase) Score(DcimSettings st)
        {
            double inR = 0, all = 0, worst = 0;
            string who = "";
            for (int i = 0; i < cases.Length; i++)
            {
                DcimModel m;
                try { m = Dcim.FitAtHeights(greens[i], GreensKernel.VerticalVectorPotential,
                                            cases[i].Z, cases[i].Zp, st); }
                catch (Exception e) { return (double.NaN, double.NaN, double.NaN, e.GetType().Name); }

                double caseAll = 0;
                for (int r = 0; r < rhoOverLambda.Length; r++)
                {
                    double rho = rhoOverLambda[r] * lam;
                    double rr  = Math.Sqrt(rho * rho + Math.Pow(cases[i].Z - cases[i].Zp, 2));
                    double sc  = (oracle[i][r] - m.EvaluateAtHeights(rho)).Magnitude * 4 * Math.PI * rr;
                    caseAll = Math.Max(caseAll, sc);
                    if (rhoOverLambda[r] <= Dcim.ValidatedRhoOverLambdaAtHeights) inR = Math.Max(inR, sc);
                }
                all = Math.Max(all, caseAll);
                if (caseAll > worst) { worst = caseAll; who = $"{cases[i].Name[..Math.Min(12, cases[i].Name.Length)]} {cases[i].Pairing}"; }
            }
            return (inR, all, worst, who);
        }

        var d = DcimSettings.Default;
        var baseline = Score(d);
        _out.WriteLine($"BASELINE (shipped defaults): worst G_A^zz scaled error ≤0.1λ {baseline.In:E2}, " +
                       $"≤1λ {baseline.All:E2} (on {baseline.WorstCase}).");
        _out.WriteLine($"The target is the ≤ 1.9e-2 the other three components already meet at ρ/λ = 1.\n");

        // ── Sweep A: the three knobs the brief names FIRST — and they are INERT ────────────
        // Dcim.FitAtHeights re-references the whole decomposition to the source region's own
        // k_m and never reads BranchSamples, BranchExtent or BranchPointOrders. It cannot: L9c
        // established that the far-field sum rule is a THEOREM for an interior source for a
        // different reason (the kernel is simply FINITE at its own branch point, so the numerator
        // vanishes there by inspection), so there is no branch-point Taylor sampling to configure.
        // Asserted as bit-identity rather than argued, because "the trade that chose those
        // defaults is not the trade this case faces" turns out to understate it: those knobs are
        // not in this path at all.
        _out.WriteLine("BranchPointOrders / BranchSamples / BranchExtent — the interior fit does " +
                       "not read them:");
        foreach (var (label, st) in new (string, DcimSettings)[]
                 {
                     ("orders=2 branchSamples=64  branchExtent=1.0", d with { BranchPointOrders = 2, BranchSamples = 64,  BranchExtent = 1.0 }),
                     ("orders=3 branchSamples=128 branchExtent=2.0", d with { BranchPointOrders = 3, BranchSamples = 128, BranchExtent = 2.0 }),
                     ("orders=1 branchSamples=32  branchExtent=0.5", d with { BranchPointOrders = 1, BranchSamples = 32,  BranchExtent = 0.5 }),
                 })
        {
            var sc = Score(st);
            _out.WriteLine($"  {label}   ≤0.1λ {sc.In:E2}   ≤1λ {sc.All:E2}");
            Assert.Equal(baseline.In,  sc.In);       // bit-identical, not "close"
            Assert.Equal(baseline.All, sc.All);
        }

        // ── Sweep B: the knobs that DO reach it — the fit's own orders and paths ───────────
        _out.WriteLine("\nThe orders and the sampling paths, worst over every grounded case:");
        _out.WriteLine("  farOrder  maxOrder  farExtent  farSamples  pathExtent  samples  fitTol" +
                       "     ≤0.1λ      ≤1λ");
        var best = (Score: baseline, Label: "defaults", Settings: d);

        void Try(string label, DcimSettings st)
        {
            var sc = Score(st);
            _out.WriteLine($"  {label}   {sc.In:E2}   {sc.All:E2}");
            if (!double.IsNaN(sc.All) && sc.All < best.Score.All) best = (sc, label.Trim(), st);
        }

        // farOrder alone — the knob that moved it at all in the first pass.
        foreach (int fo in new[] { 6, 8, 10, 12, 14, 16, 20 })
            Try($"{fo,8}  {d.MaxOrder,8}  {d.FarPathExtent,9:F1}  {d.FarSamples,10}  " +
                $"{d.PathExtent,10:F0}  {d.Samples,7}  {d.FitTolerance,6:E0}",
                d with { FarOrder = fo });

        // maxOrder alone — the NEAR path's image count.
        foreach (int mo in new[] { 10, 14, 18, 22, 28 })
            Try($"{d.FarOrder,8}  {mo,8}  {d.FarPathExtent,9:F1}  {d.FarSamples,10}  " +
                $"{d.PathExtent,10:F0}  {d.Samples,7}  {d.FitTolerance,6:E0}",
                d with { MaxOrder = mo });

        // the fit tolerance — what Prony accepts as converged.
        foreach (double ft in new[] { 1e-6, 1e-8, 1e-10, 1e-12 })
            Try($"{d.FarOrder,8}  {d.MaxOrder,8}  {d.FarPathExtent,9:F1}  {d.FarSamples,10}  " +
                $"{d.PathExtent,10:F0}  {d.Samples,7}  {ft,6:E0}",
                d with { FitTolerance = ft });

        // the far path's own geometry, at the two most promising orders.
        foreach (int fo in new[] { 10, 14 })
        foreach (double fe in new[] { 2.0, 3.0, 4.0, 6.0, 8.0 })
        foreach (int fs in new[] { 192, 384 })
            Try($"{fo,8}  {d.MaxOrder,8}  {fe,9:F1}  {fs,10}  " +
                $"{d.PathExtent,10:F0}  {d.Samples,7}  {d.FitTolerance,6:E0}",
                d with { FarOrder = fo, FarPathExtent = fe, FarSamples = fs });

        // and the near path, on top of the best far configuration found so far.
        var seed = best.Settings;
        foreach (double pe in new[] { 150.0, 300.0, 600.0, 1200.0 })
        foreach (int ns in new[] { 512, 1024 })
            Try($"{seed.FarOrder,8}  {seed.MaxOrder,8}  {seed.FarPathExtent,9:F1}  " +
                $"{seed.FarSamples,10}  {pe,10:F0}  {ns,7}  {seed.FitTolerance,6:E0}",
                seed with { PathExtent = pe, Samples = ns });

        _out.WriteLine($"\nBEST: {best.Label}");
        _out.WriteLine($"  ≤0.1λ {best.Score.In:E2} (was {baseline.In:E2}), " +
                       $"≤1λ {best.Score.All:E2} (was {baseline.All:E2}) — " +
                       $"{baseline.All / Math.Max(best.Score.All, 1e-300):F1}× better at ρ/λ = 1.");
        _out.WriteLine(best.Score.All <= 1.9e-2
            ? "  → INSIDE the ≤ 1.9e-2 envelope the other three components meet. A per-component " +
              "DcimSettings is the whole fix and M2 is not needed."
            : $"  → STILL OUTSIDE 1.9e-2. The knobs do not close it; M2 (direct integration for the " +
              $"ẑẑ block alone) is the next measurement.");

        // The baseline must not have silently drifted — this is the curve L9c reported.
        Assert.True(baseline.In < 5e-2, $"baseline inside the validated range: {baseline.In:E2}");
    }
}
