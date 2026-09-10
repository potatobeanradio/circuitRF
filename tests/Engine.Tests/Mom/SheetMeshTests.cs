// ANT-3 — the SHEET intent: metal that is a radiator rather than a line.
//
// WHY THERE IS A SECOND INTENT AT ALL, stated once so no future reader has to reconstruct it.
// `PlanarCurrentModel.TransmissionLine` asks WHICH WAY IS THE CURRENT GOING and declines when it
// cannot tell — which is right, and is not weakened here. A wide radiating sheet is the case it
// declines by construction, and it is also the case where the question does not apply: on a patch
// the current varies on the scale of a wavelength in BOTH directions, a half-cosine along the
// resonant dimension and near-uniform across the width, with no singularity in the interior to
// resolve.
//
// A patch fails the direction test twice over. It is a one-port, so the port vector is unavailable;
// and the area moment of a 41.3 x 49.4 mm rectangle reports the LONGER side, which on the measured
// board is perpendicular to both the feed and the resonant dimension.
//
// So Sheet is: bulk pitch = lambda_g/CellsPerWavelength in BOTH axes, floored locally by
// local-width/MinCellsAcrossConductor only where the metal is genuinely narrower than that. Same
// field, same sampling, same Lipschitz envelope, same one-way floor at the per-axis rule. The
// direction term drops out, and with it the decline.
//
// MESHER TESTS ONLY — ANT-2's rule, unchanged. SurfaceMesher.Mesh on a hand-built PlanarProblem is
// milliseconds; a de-embedded planar point is tens of seconds. No EM solve anywhere in this file.

using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class SheetMeshTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    /// <summary>
    /// <b>Auto: false everywhere here, and it is not decoration.</b>
    /// <c>PlanarMeshSettings.Default with { EdgeMesh = false }</c> is INERT — <c>Default</c> has
    /// <c>Auto = true</c> and <c>Resolved</c> collapses it back — so a fixture that varies a
    /// resolution through <c>Default</c> silently tests the same mesh twice. That trap is recorded
    /// in brief-em-transmission-line-mesh.md §0a and has already cost this area once.
    /// </summary>
    private static PlanarMeshSettings At(PlanarCurrentModel model, int cpw = 20, int across = 4,
                                         bool edge = true, int div = 200) =>
        new(Auto: false, CellsPerWavelength: cpw, EdgeMesh: edge, EdgeCells: 3,
            MinCellsAcrossConductor: across, CurrentModel: model, DetailFloorDivisor: div);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The fixtures
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The measured board, reduced to its essential: a 41.3 x 49.4 mm patch on 203.2 um FR-4, fed by
    /// a 349.3 um microstrip, with a cluster of ~310 um connector detail at the far end of the feed.
    /// The same fixture ANT-2's own tests use, so the two phases' numbers are comparable.
    /// </summary>
    private static PlanarProblem Patch(bool detail = true, double fHz = 1.74e9)
    {
        const double patchW = 41.3e-3, patchH = 49.4e-3, feedW = 349.3e-6, totalX = 55.61e-3;
        double patchX0 = totalX - patchW, yc = patchH / 2;

        var polys = new List<PlanarPolygon>
        {
            PlanarLineFixtures.Rect(patchX0, 0, totalX, patchH),
            PlanarLineFixtures.Rect(4e-3, yc - feedW / 2, patchX0, yc + feedW / 2),
        };
        if (detail)
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                double x = 2.2e-3 + 2e-3 * Math.Cos(a), y = yc + 2e-3 * Math.Sin(a);
                polys.Add(PlanarLineFixtures.Rect(x - 155e-6, y - 155e-6, x + 155e-6, y + 155e-6));
            }

        return new PlanarProblem([new PlanarConductorLayer("Top", polys, 5.8e7, 35e-6)],
                                 new GroundedSlab(203.2e-6, new EmMaterial(4.4, 0.02)), fHz);
    }

    /// <summary>The mesher's existing fixture set, plus the patch this brief is about.</summary>
    private static IEnumerable<(string Name, PlanarProblem Problem)> Fixtures()
    {
        yield return ("patch + connector detail", Patch());
        yield return ("patch, connector deleted", Patch(detail: false));
        yield return ("200 um x 10 mm FR-4 line", PlanarLineFixtures.Line(GroundedSlab.Fr4Starter, 200e-6, 10e-3, 10e9));
        yield return ("2.9 mm x 20 mm FR-4 line", PlanarLineFixtures.Line(GroundedSlab.Fr4Starter, 2.9e-3, 20e-3, 10e9));
        yield return ("72 um x 2 mm GaAs line", PlanarLineFixtures.Line(GroundedSlab.GaAsStarter, 72e-6, 2e-3, 20e9));
        yield return ("8-segment taper", PlanarLineFixtures.Taper(GroundedSlab.Fr4Starter, 2.9e-3, 0.5e-3, 20e-3, 10e9));
        yield return ("east-then-north bend", Bend());
    }

    private static PlanarProblem Bend()
    {
        const double w = 200e-6, arm = 5e-3;
        return PlanarLineFixtures.Problem(GroundedSlab.Fr4Starter, 10e9,
            new PlanarPolygon([
                new EmPoint(0, -0.5 * w), new EmPoint(arm + 0.5 * w, -0.5 * w),
                new EmPoint(arm + 0.5 * w, arm), new EmPoint(arm - 0.5 * w, arm),
                new EmPoint(arm - 0.5 * w, 0.5 * w), new EmPoint(0, 0.5 * w)]));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 1 — the one-way invariant. ASSERT IT, DO NOT ASSUME IT (§6).
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void SheetsBULKPITCH_IsNeverFinerThanThePerAxisRules_OnEveryFixture()
    {
        // THE STRUCTURAL GUARANTEE, and it is what makes the intent safe to reach for. Every pitch
        // the field asks for is floored at the one the per-axis rule would have used, and the local
        // width is measured from the metal rather than from a global 5th percentile, so the field is
        // pointwise >= the per-axis rule's own pitch everywhere.
        //
        // §6 ASKS FOR THIS ON THE CELL COUNT AND THAT FORM IS NOT ATTAINABLE — measured, not
        // assumed, and the reason is one this area already had a name for. A coarser bulk gives the
        // graded edge fan FURTHER to climb, its ratio is clamped at 3x per cell, and on artwork that
        // is mostly rim and hardly any bulk the extra fan cells outweigh the bulk saving. The
        // 8-segment taper here is exactly that shape: 702 cells under the per-axis rule, 810 under
        // Sheet, 824 under TransmissionLine — and 679 under ALL THREE with the edge mesh off, which
        // is what says the whole excess is the fan and none of it is the bulk. The transmission-line
        // mesh's own version of this gate had to be stated the same way for the same measurement
        // (728 -> 858 on the same taper), and the mesher reports the trade in its notes rather than
        // hiding it.
        //
        // So the invariant is asserted where it is structural — on the PITCH, unconditionally — and
        // on the cell count in the two forms that hold: exactly, with the fan removed, and within
        // the same 1.25x the sibling intent's gate allows with it on.
        foreach (var (name, p) in Fixtures())
            foreach (int cpw in new[] { 20, 10, 5 })
            {
                var off   = SurfaceMesher.Mesh(p, At(PlanarCurrentModel.None, cpw),
                                               PlanarEdgeReference.LocalConductorWidth);
                var sheet = SurfaceMesher.Mesh(p, At(PlanarCurrentModel.Sheet, cpw),
                                               PlanarEdgeReference.LocalConductorWidth);

                // The BULK is what the field governs; the edge fan's own cells are smaller by design
                // in both meshes. So the comparison is of the largest cell each mesh contains.
                Assert.True(sheet.MaxCellEdgeM >= off.MaxCellEdgeM * 0.999,
                    $"{name} at cells/λ {cpw}: the sheet produced a FINER bulk cell than the " +
                    $"per-axis rule — {off.MaxCellEdgeM * 1e6:G4} µm -> {sheet.MaxCellEdgeM * 1e6:G4} µm");

                Assert.True(sheet.CellCount <= off.CellCount * 1.25,
                    $"{name} at cells/λ {cpw}: sheet {sheet.CellCount} cells against the per-axis " +
                    $"rule's {off.CellCount} — past the edge fan's own known trade");
            }
    }

    [Fact]
    public void WithTheFanRemoved_SheetIsNeverAboveThePerAxisRule_Exactly()
    {
        // The other half of the gate above, and the one that isolates the bulk: with the edge mesh
        // off there is no fan to climb, so nothing is left but the size field — and there the
        // one-way invariant is exact on every fixture, at every cells/λ. A failure here is the
        // sheet field genuinely refining something, which is the thing that must not happen.
        foreach (var (name, p) in Fixtures())
            foreach (int cpw in new[] { 20, 10, 5 })
            {
                var off   = SurfaceMesher.Mesh(p, At(PlanarCurrentModel.None, cpw, edge: false),
                                               PlanarEdgeReference.LocalConductorWidth);
                var sheet = SurfaceMesher.Mesh(p, At(PlanarCurrentModel.Sheet, cpw, edge: false),
                                               PlanarEdgeReference.LocalConductorWidth);
                Assert.True(sheet.CellCount <= off.CellCount,
                    $"{name} at cells/λ {cpw}, edge mesh off: sheet {sheet.CellCount} cells against " +
                    $"the per-axis rule's {off.CellCount} — the sheet field refined something");
            }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 2 — a plain line meshed as a sheet must not get WORSE (§6's safety property)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void APlainUniformLineUnderSheet_KeepsItsTransverseResolutionAndOnlyCoarsensAlong()
    {
        // §6'S SAFETY PROPERTY, and it is what says the intent cannot be catastrophically
        // mis-chosen: "a line meshed as a sheet is coarse along its length and correctly fine across
        // it, which is exactly the per-axis rule." Both halves are asserted, and the ACROSS half is
        // the load-bearing one — a sheet that coarsened the width would stop resolving the 1/√d edge
        // current, which is an accuracy failure a cell count cannot show.
        //
        // "Exactly the per-axis rule" is true of the PITCH and not of the gridline count, and the
        // difference is worth stating rather than tolerating: with a field, `BuildGridLines` drops
        // its per-interval minimum-cell floor (a single number computed from a varying field is
        // wrong somewhere — the trap is recorded in that method), so the marcher places fewer,
        // larger cells along a uniform run at the same cap. Measured on the 200 µm x 10 mm line:
        // 23 x gridlines under the per-axis rule against 15 under Sheet, with the y grid identical
        // at 10 and the cell count 198 -> 126. Coarser along, unchanged across, cheaper — which is
        // the property, spelled exactly.
        foreach (var p in new[]
        {
            PlanarLineFixtures.Line(GroundedSlab.Fr4Starter, 200e-6, 10e-3, 10e9),
            PlanarLineFixtures.Line(GroundedSlab.Fr4Starter, 2.9e-3, 20e-3, 10e9),
            PlanarLineFixtures.Line(GroundedSlab.GaAsStarter, 72e-6, 2e-3, 20e9),
        })
        {
            var off   = SurfaceMesher.Mesh(p, At(PlanarCurrentModel.None));
            var sheet = SurfaceMesher.Mesh(p, At(PlanarCurrentModel.Sheet));

            // ACROSS: the transverse grid keeps the per-axis rule's line COUNT and its finest cell.
            //
            // Not its coordinates to the bit, and that is a real difference rather than a tolerance
            // hiding one: with a field the edge fan grades toward the LOCAL bulk cap rather than the
            // one global hy (ANT-2's per-attractor rate), so the interior gridlines of the fan land
            // a few nanometres apart — 94.1257 µm against 94.1226 µm on the 200 µm line, 1.5e-5
            // relative. Nor its line COUNT to the unit: on the 2.9 mm line the width term
            // (2.9 mm / 4 = 725 µm) and the λ cap (714.6 µm) are within 1.5% of each other, so the
            // field's own grading is free to drop one interior line, 10 -> 9. What actually says the
            // width is still resolved is the FINEST transverse cell, and that is asserted exactly.
            Assert.True(sheet.Mesh.GridY.Count <= off.Mesh.GridY.Count,
                $"the sheet refined ACROSS the line: {off.Mesh.GridY.Count} -> {sheet.Mesh.GridY.Count}");
            double finestOff   = FinestStepIn(off.Mesh.GridY,   double.MinValue, double.MaxValue);
            double finestSheet = FinestStepIn(sheet.Mesh.GridY, double.MinValue, double.MaxValue);
            Assert.True(finestSheet <= finestOff * 1.01,
                $"the sheet coarsened the finest transverse cell: {finestOff * 1e6:G4} µm -> " +
                $"{finestSheet * 1e6:G4} µm — the 1/√d edge current is no longer resolved");

            // ALONG: never finer, and never more expensive.
            Assert.True(sheet.Mesh.GridX.Count <= off.Mesh.GridX.Count,
                $"the sheet refined ALONG the line: {off.Mesh.GridX.Count} -> {sheet.Mesh.GridX.Count}");
            Assert.True(sheet.CellCount <= off.CellCount,
                $"a plain line got more expensive as a sheet: {off.CellCount} -> {sheet.CellCount}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 3 — translation invariance, the 3.7 mm hard gate
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void MovingTheArtwork3Point7mm_LeavesTheMeshUnchanged()
    {
        // L8b's own knife edge, and any new size field is exactly the class of change that
        // reintroduces it: a column-wise minimum is a STEP function, and a discontinuous size field
        // puts the marcher's landing point on a floating-point comparison. Moving the same rectangle
        // 3.7 mm changed the mesh by 33% the last time this was not held. The field is Lipschitz-
        // smoothed at the edge fan's own growth ratio for this reason, and the sheet branch reuses
        // that smoothing rather than adding a second one.
        const double d = 3.7e-3;

        // A patch AND a line, because the two exercise different halves of the field: the patch's
        // bulk is the λ cap and its feed is the width floor, and only the second is a step.
        foreach (var (baseline, shifted) in new[]
        {
            (PlanarLineFixtures.Line(GroundedSlab.Fr4Starter, 200e-6, 10e-3, 10e9),
             PlanarLineFixtures.Problem(GroundedSlab.Fr4Starter, 10e9,
                 PlanarLineFixtures.Rect(d, d - 100e-6, d + 10e-3, d + 100e-6))),
            (Patch(detail: false), Shift(Patch(detail: false), d)),
        })
        {
            var a = SurfaceMesher.Mesh(baseline, At(PlanarCurrentModel.Sheet));
            var b = SurfaceMesher.Mesh(shifted,  At(PlanarCurrentModel.Sheet));

            Assert.Equal(a.CellCount, b.CellCount);
            Assert.Equal(a.Mesh.GridX.Count, b.Mesh.GridX.Count);
            Assert.Equal(a.Mesh.GridY.Count, b.Mesh.GridY.Count);
            for (int i = 0; i < a.Mesh.GridX.Count; i++)
                Assert.Equal(a.Mesh.GridX[i], b.Mesh.GridX[i] - d, 12);
            for (int i = 0; i < a.Mesh.GridY.Count; i++)
                Assert.Equal(a.Mesh.GridY[i], b.Mesh.GridY[i] - d, 12);
        }
    }

    private static PlanarProblem Shift(PlanarProblem p, double d)
    {
        var layers = new List<PlanarConductorLayer>(p.Layers.Count);
        foreach (var l in p.Layers)
        {
            var moved = new List<PlanarPolygon>(l.Polygons.Count);
            foreach (var poly in l.Polygons)
            {
                var pts = new List<EmPoint>(poly.Outer.Count);
                foreach (var v in poly.Outer) pts.Add(new EmPoint(v.X + d, v.Y + d));
                moved.Add(new PlanarPolygon(pts));
            }
            layers.Add(new PlanarConductorLayer(l.Name, moved, l.SigmaSm, l.ThicknessM));
        }
        return new PlanarProblem(layers, p.Slab, p.MaxFrequencyHz);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 4 — it never declines, and it says what it did
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void OnEveryFixture_ItActs_AndNeverDeclines()
    {
        // The transmission-line mesh declines on a structure that states no usable direction, and
        // that decline is right. A sheet has nothing to be unsure of, so a decline here would be a
        // defect rather than a judgement — and the whole reason for the second intent is the patch
        // the first one refuses.
        foreach (var (name, p) in Fixtures())
        {
            var r = SurfaceMesher.Mesh(p, At(PlanarCurrentModel.Sheet));
            string notes = string.Join("\n", r.Notes);
            Assert.Contains("Sheet mesh ON", notes, StringComparison.Ordinal);
            Assert.DoesNotContain("NOT applied", notes, StringComparison.Ordinal);
            _out.WriteLine($"{name}: {r.CellCount} cells, {r.UnknownCount} unknowns");
        }
    }

    [Fact]
    public void TheNoteNamesTheBulkPitchPerAxis_AndWhereTheFloorBound()
    {
        // §6: "The report names the bulk pitch per axis and where the floor bound." It never
        // declines, so the note is the ONLY way a user sees that it acted at all — which is the same
        // standard every control in this file is held to, and the defect this whole thread started
        // from was a control that silently did nothing.
        var withFeed = SurfaceMesher.Mesh(Patch(detail: false), At(PlanarCurrentModel.Sheet));
        string note = Assert.Single(withFeed.Notes, n => n.StartsWith("Sheet mesh ON", StringComparison.Ordinal));

        Assert.Contains("x pitch", note, StringComparison.Ordinal);
        Assert.Contains("y pitch", note, StringComparison.Ordinal);
        Assert.Contains("worst aspect", note, StringComparison.Ordinal);
        // The feed is narrow enough for the width to decide the pitch there, and the note says on
        // how much of the metal that happened — which is §6's "where the floor bound".
        Assert.Contains("metal sample", note, StringComparison.Ordinal);
        _out.WriteLine(note);

        // …and on artwork where nothing is narrow enough, it says THAT rather than going quiet:
        // "the floor is on and it changed nothing here" is the answer to a question a user reading
        // the count will ask. A BARE patch is that artwork — 41.3 x 49.4 mm at 1.74 GHz puts
        // width/4 at ~10 mm against a λ_g/20 cap of 4.15 mm, so the wavelength decides every sample
        // and the width decides none. (Add the feed back and it is the other branch, above.)
        var bare = SurfaceMesher.Mesh(
            new PlanarProblem(
                [new PlanarConductorLayer("Top", [PlanarLineFixtures.Rect(0, 0, 41.3e-3, 49.4e-3)],
                                          5.8e7, 35e-6)],
                new GroundedSlab(203.2e-6, new EmMaterial(4.4, 0.02)), 1.74e9),
            At(PlanarCurrentModel.Sheet));
        string bareNote = Assert.Single(bare.Notes, n => n.StartsWith("Sheet mesh ON", StringComparison.Ordinal));
        Assert.Contains("bulk pitch is the whole answer", bareNote, StringComparison.Ordinal);
        _out.WriteLine(bareNote);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 5 — both intents survive Auto
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void BothIntentsSurviveAuto()
    {
        // Auto means "choose the RESOLUTION for me", and neither of these is one — they say what
        // the resolutions are FOR. The failure shape this prevents is the one BoundaryCells,
        // MeshFrequencyHz, MinCellsAcrossConductor and DetailFloorDivisor were each protected from
        // in turn: a user sets a control, leaves Auto on, and silently gets the other mesh.
        foreach (var model in new[] { PlanarCurrentModel.TransmissionLine, PlanarCurrentModel.Sheet })
        {
            var s = PlanarMeshSettings.Default with { CurrentModel = model };
            Assert.True(s.Auto);
            Assert.Equal(model, s.Resolved.CurrentModel);

            var p = Patch();
            Assert.Equal(SurfaceMesher.Mesh(p, s).CellCount,
                         SurfaceMesher.Mesh(p, s.Resolved).CellCount);
        }

        // …and None is still the default, so every number in HISTORY.md stays reproducible.
        Assert.Equal(PlanarCurrentModel.None, PlanarMeshSettings.Default.CurrentModel);
        Assert.Equal(PlanarCurrentModel.None, PlanarMeshSettings.Default.Resolved.CurrentModel);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 6 — the aspect cap and the bounded growth ratio still hold
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheAspectCapCannotBindOnASheet_BecauseItAsksForOneToOne()
    {
        // A high-aspect cell is not new (a 200 µm x 50 mm trace already ships 60:1) but an UNBOUNDED
        // one is, and the fill's τ binning was measured on moderate aspects. On a sheet the cap is
        // satisfied STRUCTURALLY rather than by clamping: both axes are asked for the same number at
        // every point, so the requested aspect is exactly 1:1.
        //
        // Asserted on the FIELD, not on the realised cells, and that is the only correct place — a
        // tensor grid's cell aspect is the product of two INDEPENDENT axis fields, so a cell where a
        // coarse column crosses an edge-fan row can exceed any bulk ratio and did before any of this
        // existed. This is the same fixture the transmission-line mesh's own cap test uses, where
        // the directed field asks for ~4,000:1 and is clamped to 64.
        var p = PlanarLineFixtures.Line(GroundedSlab.Fr4Starter, 200e-6, 10e-3, 10e9);
        double hWave = (p with { MaxFrequencyHz = 1e8 }).GuidedWavelengthM / 20;

        var tline = PlanarMeshPitchField.Build(
            p, hWave, PlanarMeshSettings.DefaultMinCellsAcrossConductor, 2.0,
            floorX: 50e-6, floorY: 50e-6, model: PlanarCurrentModel.TransmissionLine);
        var sheet = PlanarMeshPitchField.Build(
            p, hWave, PlanarMeshSettings.DefaultMinCellsAcrossConductor, 2.0,
            floorX: 50e-6, floorY: 50e-6, model: PlanarCurrentModel.Sheet);

        Assert.True(tline.Ok && sheet.Ok);
        Assert.True(tline.Capped, "the directed field must actually be clamped on this fixture, " +
                                  "or the comparison below proves nothing");
        Assert.False(sheet.Capped);
        Assert.Equal(1.0, sheet.WorstAspect, 12);
        Assert.True(sheet.WorstAspect <= PlanarMeshSettings.MaxCellAspect);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 7 — the transmission-line decline is NOT weakened (§7)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheDirectedIntentStillFollowsABend_AndSheetDoesNotPretendTo()
    {
        // §7: "Do not remove or weaken the transmission-line decline. It is still right for its own
        // intent." The two intents are separate answers to one question, and this asserts they stay
        // separate — on an L-bend the directed field keeps each arm's own transverse resolution,
        // which is the requirement a single averaged direction cannot meet.
        var bend = Bend();
        var tline = SurfaceMesher.Mesh(bend, At(PlanarCurrentModel.TransmissionLine));
        Assert.Contains(tline.Notes, n => n.Contains("Transmission-line mesh ON", StringComparison.Ordinal));

        double transverse = 200e-6 / PlanarMeshSettings.DefaultMinCellsAcrossConductor;
        Assert.True(FinestStepIn(tline.Mesh.GridY, -100e-6, 100e-6) <= transverse * 1.01);
        Assert.True(FinestStepIn(tline.Mesh.GridX, 5e-3 - 100e-6, 5e-3 + 100e-6) <= transverse * 1.01);

        // A bend meshed as a SHEET is a mis-choice, not a crash: it is still bounded above by the
        // per-axis rule (gate 1 covers it on this fixture) and it still resolves the width where
        // the metal is narrow — a 200 µm arm is narrow everywhere, so it comes out fine in both
        // axes, which is exactly the per-axis rule. What it does NOT do is claim a direction.
        var sheet = SurfaceMesher.Mesh(bend, At(PlanarCurrentModel.Sheet));
        Assert.DoesNotContain(sheet.Notes, n => n.Contains("along the current", StringComparison.Ordinal));
        Assert.True(FinestStepIn(sheet.Mesh.GridY, -100e-6, 100e-6) <= transverse * 1.01);
        Assert.True(FinestStepIn(sheet.Mesh.GridX, 5e-3 - 100e-6, 5e-3 + 100e-6) <= transverse * 1.01);
    }

    /// <summary>The finest grid step anywhere inside [lo, hi] of one axis.</summary>
    private static double FinestStepIn(IReadOnlyList<double> lines, double lo, double hi)
    {
        double best = double.PositiveInfinity;
        for (int i = 1; i < lines.Count; i++)
        {
            double a = lines[i - 1], b = lines[i];
            if (b < lo || a > hi) continue;
            best = Math.Min(best, b - a);
        }
        return best;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Gate 8 — the rim is NOT skipped (§7's named trap)
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void SheetDoesNotSkipTheRim()
    {
        // §7: "Do not let Sheet skip the rim. The temptation is real, because the rim is where the
        // cells go and a patch interior is smooth. The rim is also where the radiation comes from."
        // The two radiating edges set the effective length, hence the resonant frequency, hence
        // every number downstream; the two non-radiating edges carry the transverse 1/√d
        // singularity.
        //
        // Asserted as "the edge mesh still acts under Sheet" — the fan is still built, still
        // reported, and the cell nearest a patch rim is still far finer than the bulk pitch.
        var r = SurfaceMesher.Mesh(Patch(detail: false), At(PlanarCurrentModel.Sheet),
                                   PlanarEdgeReference.LocalConductorWidth);
        Assert.True(r.LongestEdgeFanCells > 0,
            "the edge fan was not built at all under the sheet intent");
        Assert.Contains(r.Notes, n => n.StartsWith("Edge mesh on:", StringComparison.Ordinal));

        // And turning the edge mesh OFF must actually change the mesh, which is what says the fan
        // above was real rather than reported.
        var noEdge = SurfaceMesher.Mesh(Patch(detail: false), At(PlanarCurrentModel.Sheet, edge: false),
                                        PlanarEdgeReference.LocalConductorWidth);
        Assert.True(noEdge.CellCount < r.CellCount,
            $"the edge mesh changed nothing under Sheet: {r.CellCount} -> {noEdge.CellCount}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // §5 — what it comes out at. THE MEASUREMENT, reported rather than tuned to.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ThePatchBoardsUnknownCount_IsReported()
    {
        // §5 is arithmetic, not a measurement: λ_g at 1.74 GHz on εᵣ 4.4 is ≈ 83 mm, so λ_g/20 is
        // ≈ 4.2 mm and a 41.3 x 49.4 mm patch is 10 x 12 cells in the bulk. With ANT-2's bounded fan
        // and a locally resolved 349 µm feed the brief expects order 500-900 unknowns.
        //
        // THE ASSERTION IS DELIBERATELY LOOSE. §5 says "if it does not land in that range, that is
        // the result and it goes in the write-up. Do not tune the default until the number is
        // understood" — so this pins the ORDER (it must be a mesh that solves, not one that is
        // refused) and prints the rest for RESOLVED.md. A tight bound here would be a number tuned
        // to itself.
        foreach (var (name, p) in new[]
        {
            ("with connector detail", Patch()),
            ("connector deleted",     Patch(detail: false)),
        })
        {
            var off   = SurfaceMesher.Mesh(p, At(PlanarCurrentModel.None), PlanarEdgeReference.LocalConductorWidth);
            var tline = SurfaceMesher.Mesh(p, At(PlanarCurrentModel.TransmissionLine), PlanarEdgeReference.LocalConductorWidth);
            var sheet = SurfaceMesher.Mesh(p, At(PlanarCurrentModel.Sheet), PlanarEdgeReference.LocalConductorWidth);
            var sheetNoEdge = SurfaceMesher.Mesh(p, At(PlanarCurrentModel.Sheet, edge: false), PlanarEdgeReference.LocalConductorWidth);

            _out.WriteLine($"{name}: per-axis {off.UnknownCount:N0} | tline {tline.UnknownCount:N0} | " +
                           $"sheet {sheet.UnknownCount:N0} | sheet, edge off {sheetNoEdge.UnknownCount:N0}");
            _out.WriteLine("    " + string.Join("\n    ", sheet.Notes));

            Assert.True(sheet.UnknownCount <= SurfaceMesher.UnknownCeiling,
                $"{name}: the sheet mesh is {sheet.UnknownCount:N0} unknowns, past the " +
                $"{SurfaceMesher.UnknownCeiling:N0} ceiling — this board is the one the intent exists for");
        }
    }
}
