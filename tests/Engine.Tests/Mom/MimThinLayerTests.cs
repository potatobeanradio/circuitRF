// brief-em-mim-3-thin-layer-gate.md — two meshed conductor levels 0.05-0.5 um apart is a regime
// nothing had measured, and LayerStack.CanRepresent accepts any positive thickness, so nothing
// refused the structure either.
//
// The three ladders are in src/Engine/Mom/HISTORY.md and the verdict is in RESOLVED.md. What is
// here gates the tier that proved fragile — the CROSS-LEVEL block of the multi-level fill, whose
// error against forced-high quadrature grows four decades between cell/separation 1 and 20
// while the SAME-level block does not move at all.
//
// WHY THIS SHAPE. Reciprocity holds to 1e-19 and passivity to 1e-5 the whole way up the ladder, so
// no self-consistency check can see this: it is L8c's converged-looking-but-wrong mode, one tier
// down in z. Only a comparison against a better quadrature can, and that comparison is expensive
// (19 s and 74 s in a Debug test run), so it carries Category=Benchmark and the ROUTINE gate is a
// fixed-input matrix-entry comparison against literals — P3/P4's own pattern.
//
// WHERE THE LITERALS CAME FROM. Printed by this file's own fixtures on the tree that landed MIM-3,
// against PlanarFillSettings.Default. They assert nothing about accuracy — that is HISTORY's job
// and T7's — but they hold the cross-level path still, which is the thing whose silent movement
// would invalidate the verdict.

using System.Numerics;
using System.Security.Cryptography;
using CircuitRF.Engine.Mom;
using NumFlat;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class MimThinLayerTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private const double Zlow = 103e-6;   // the shipped MIM technology's lower plate
    private const double Ztop = 106e-6;   // its upper interconnect level

    /// <summary>GaAs 103 um | capacitor dielectric d | air to 106 um, on a ground plane. Only d
    /// moves, so a ladder over it holds the artwork, the mesh and the unknown count fixed.</summary>
    private static LayerStack MimStack(double d) => new(
        Termination.Pec,
        [
            new MediumLayer(Zlow, new EmMaterial(12.90, 0.0006)),
            new MediumLayer(d,    new EmMaterial( 6.80, 0.0010)),
            new MediumLayer(Ztop - Zlow - d, new EmMaterial(1.0, 1e-6)),
        ],
        Termination.Air);

    private static PlanarPolygon Square(double w) =>
        new([new EmPoint(0, 0), new EmPoint(w, 0), new EmPoint(w, w), new EmPoint(0, w)]);

    /// <summary>Two coincident square plates straddling the dielectric, always four cells to a
    /// side, so <paramref name="cellOverD"/> is the only thing that changes between rungs.</summary>
    private static PlanarProblem Plates(double d, double cellOverD)
    {
        double w = 4 * cellOverD * d;
        var stack = MimStack(d);
        return new PlanarProblem(
            [
                new PlanarConductorLayer("bottom plate", [Square(w)], 4.1e7, 0.25e-6, Zlow),
                new PlanarConductorLayer("top plate",    [Square(w)], 4.1e7, 0.25e-6, Zlow + d),
            ],
            GroundedSlab.GaAsStarter, 10e9, null, stack, null);
    }

    private static readonly PlanarMeshSettings Uniform =
        new(Auto: false, CellsPerWavelength: 20, EdgeMesh: false, EdgeCells: 3);

    /// <summary>
    /// A modest step above the shipped rule — enough to separate the two regimes by three decades.
    /// HISTORY's own ladder uses a heavier one and shows that stepping the reference AGAIN moves it
    /// by 1e-14, i.e. that the reference is converged rather than merely different.
    /// </summary>
    private static readonly PlanarFillSettings Reference = PlanarFillSettings.Default with
    {
        SelfPanels = 8, TouchPanels = 6,
        NearNodes = 16, MidNodes = 12, FarNodes = 8,
        NearRatio = 8.0, FarRatio = 32.0,
        RemainderNodesNear = 12, RemainderNodesMid = 8, RemainderNodesFar = 6,
        UseRadialTable = false,
    };

    private static Mat<Complex> Fill(PlanarMesh mesh, PlanarProblem p, PlanarFillSettings st)
    {
        var cores  = PlanarFill.BuildCores(mesh, st);
        var greens = new LayeredSpectralGreens(p.EffectiveStack, p.MaxFrequencyHz);
        var set    = new PlanarKernelSet(greens, st.Order).For(cores);
        return PlanarFill.FillMultiLevel(cores, set, PlanarLevels.From(p), 2 * Math.PI * p.MaxFrequencyHz);
    }

    /// <summary>Worst entry-wise difference over the chosen block, scaled by that block's OWN
    /// largest entry — a small block must not be graded on a large block's dynamic range.</summary>
    private static double Block(Mat<Complex> a, Mat<Complex> b, int[] layer, bool cross)
    {
        double worst = 0, scale = 0;
        for (int i = 0; i < a.RowCount; i++)
            for (int j = 0; j < a.ColCount; j++)
            {
                if (cross != (layer[i] != layer[j])) continue;
                scale = Math.Max(scale, b[i, j].Magnitude);
                worst = Math.Max(worst, (a[i, j] - b[i, j]).Magnitude);
            }
        return scale > 0 ? worst / scale : double.NaN;
    }

    private static string DigestCross(Mat<Complex> z, int[] lay)
    {
        var buf = new byte[16];
        using var sha = SHA256.Create();
        for (int i = 0; i < z.RowCount; i++)
            for (int j = 0; j < z.ColCount; j++)
            {
                if (lay[i] == lay[j]) continue;
                BitConverter.TryWriteBytes(buf.AsSpan(0, 8), z[i, j].Real);
                BitConverter.TryWriteBytes(buf.AsSpan(8, 8), z[i, j].Imaginary);
                sha.TransformBlock(buf, 0, 16, null, 0);
            }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // T1 — the ROUTINE gate: the cross-level block, on a fixed input, against literals
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    // cell/separation, digest of the cross-level block, and one representative entry (row 0, last
    // column) so a failure message says HOW it moved rather than only that a hash changed.
    [InlineData(1.0,  "917BBCA5D70341ADB227CADC723A5B6311103FBA48C2418398921DAA1B890D76",
                0.27535759464851667, -318.6400677788218)]
    [InlineData(20.0, "12CB6DECE52B3234793855736C205D671717EB9AD48789A8892CA94285549081",
                0.007459157175535663, -13.46907400956937)]
    public void T1_TheCrossLevelBlockIsHeldStill_OnAFixedInput(
        double cellOverD, string digest, double lastReal, double lastImag)
    {
        var p    = Plates(0.2e-6, cellOverD);
        var mesh = SurfaceMesher.Mesh(p, Uniform).Mesh;
        var lay  = mesh.Bases.Select(b => b.LayerIndex).ToArray();
        var z    = Fill(mesh, p, PlanarFillSettings.Default);

        Assert.Equal(48, mesh.Bases.Count);
        Assert.Equal(32, mesh.Cells.Count);

        var probe = z[0, z.ColCount - 1];
        _out.WriteLine($"cell/separation {cellOverD:G3}: [0,{z.ColCount - 1}] = {probe.Real:R} {probe.Imaginary:R}");
        Assert.Equal(lastReal, probe.Real,      12);
        Assert.Equal(lastImag, probe.Imaginary, 9);
        Assert.Equal(digest, DigestCross(z, lay));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // T2-T5 — the note the verdict ships as
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T2_TheNoteFiresOnAnUnresolvedGapAndStaysQuietOnAnOrdinaryOne()
    {
        // The shipped MIM technology's own geometry: a 10 um plate pair 0.2 um apart meshes at
        // 2.5 um, i.e. cell/separation = 12.5. The same artwork at ordinary interconnect spacing is
        // 0.833 and must not be flagged — a note that fires on every multi-level run is a note
        // nobody reads, which is exactly what CheckFeedClearance was before 2026-08-12.
        var thin = Note(0.2e-6, 10e-6);
        var wide = Note(3.0e-6, 10e-6);
        _out.WriteLine("THIN: " + thin);
        _out.WriteLine("WIDE: " + wide);

        Assert.Contains("CELL/SEPARATION = 12.5", thin);
        Assert.Contains("PAST", thin);
        Assert.Contains("cell/separation = 0.833", wide);
        Assert.DoesNotContain("PAST", wide);
    }

    [Fact]
    public void T3_TheNoteNamesTheBindingQuantity_AndDoesNotRecommendTheInertKnobs()
    {
        // §3.5's recorded trap, and the reason EmCeilingRefusalTests exists: naming a remedy
        // without asking whether it BINDS. The cell size is
        // min(λ_g/CellsPerWavelength, width/MinCellsAcrossConductor) and only the first term
        // responds to the frequency knobs, so the note must name the pitch and must never simply
        // tell the user to lower Cells per wavelength.
        var thin = Note(0.2e-6, 10e-6);
        _out.WriteLine(thin);

        Assert.Contains("CELL PITCH", thin);
        Assert.Contains("only the first term responds to the frequency knobs", thin);
        Assert.Contains("neither frequency knob acts", thin);

        // And it must scope the damage: single-level results are untouched by this.
        Assert.Contains("single level is unaffected", thin);
    }

    [Fact]
    public void T3b_TheNoteDECIDESTheKnobQuestionArithmetically_RatherThanHedging()
    {
        // The two frequency knobs reach the cell size only through the λ_g/CellsPerWavelength cap,
        // so "do they act here" has an arithmetic answer: what would CellsPerWavelength have to be
        // for that cap to equal the pitch the mesh already has? On the shipped MIM geometry — a
        // 10 µm plate pair at 10 GHz over GaAs — λ_g is ~8.35 mm and the pitch is 2.5 µm, so the
        // answer is in the thousands and the note says so with the number in it.
        var thin = Note(0.2e-6, 10e-6);
        _out.WriteLine(thin);

        Assert.Contains("it would take Cells per wavelength ≥", thin);
        Assert.Contains("neither frequency knob acts here", thin);
        Assert.Contains("the metal's own width", thin);

        // The number itself: λ_g / 2.5 µm at 10 GHz in εᵣ 12.9. Quoted so the test fails if the
        // arithmetic drifts, not only if the sentence does.
        double lambdaG = 299792458.0 / (10e9 * Math.Sqrt(12.9));
        Assert.Contains($"≥ {lambdaG / 2.5e-6:N0} ", thin);
    }

    [Fact]
    public void T4_TheNoteIsAskedPerADJACENTLEVELPAIR_OverTheCellsOnThoseLevelsOnly()
    {
        // R-zz-1's discipline, carried over: the cross-level block is only ever evaluated between
        // cells on the two levels concerned, so a per-mesh question would grade a plate pair on
        // some unrelated conductor's cell. The fixture adds a wide line on the upper level, whose
        // own cells are large; the gate is that exactly one note comes out, about the one adjacent
        // pair that exists.
        double d = 3.0e-6;
        var p = new PlanarProblem(
            [
                new PlanarConductorLayer("plate", [Square(10e-6)], 4.1e7, 0.25e-6, Zlow),
                new PlanarConductorLayer("plate + a wide line",
                    [Square(10e-6),
                     new PlanarPolygon([new EmPoint(200e-6, 0), new EmPoint(400e-6, 0),
                                        new EmPoint(400e-6, 200e-6), new EmPoint(200e-6, 200e-6)])],
                    4.1e7, 0.25e-6, Zlow + d),
            ],
            GroundedSlab.GaAsStarter, 10e9, null, MimStack(d), null);

        var mesh  = SurfaceMesher.Mesh(p, Uniform).Mesh;
        var notes = PlanarSolve.LevelSeparationNotes(p, mesh, 10e9);
        _out.WriteLine(string.Join("\n", notes));

        Assert.Single(notes);
        Assert.Contains("levels 0 and 1", notes[0]);
    }

    [Fact]
    public void T5_ASingleLevelProblemGetsNoNoteAtAll()
    {
        // There is no cross-level block, so there is nothing to say — and saying it anyway is how a
        // note stops being read.
        var p = new PlanarProblem(
            [new PlanarConductorLayer("M", [Square(70e-6)], 4.1e7, 2e-6)],
            GroundedSlab.GaAsStarter, 10e9);
        var mesh = SurfaceMesher.Mesh(p, Uniform).Mesh;
        Assert.Empty(PlanarSolve.LevelSeparationNotes(p, mesh, 10e9));
    }

    [Fact]
    public void T6_TheSweepItselfCarriesTheNote()
    {
        // The note is worth nothing if the driver drops it. PlanarSolve.Run assembles it for any
        // multi-level problem — this is the wiring gate, and it is deliberately the cheap one
        // (VerticalRangeVerdict, not a solve).
        var p = Plates(0.2e-6, 12.5);
        var mesh = SurfaceMesher.Mesh(p, Uniform).Mesh;
        var (verdict, notes) = PlanarSolve.VerticalRangeVerdict(p, mesh, 10e9);

        Assert.True(verdict.Ok, verdict.Reason);
        Assert.Contains(notes, n => n.Contains("CELL/SEPARATION"));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // T7 — the ACCURACY statement the verdict rests on. Category=Benchmark: 19 s and 74 s in Debug
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T7_TheCrossLevelBlockDegradesByDecades_AndOnlyItDoes()
    {
        // Two claims, and the second is the load-bearing one: the cross-level block moves by
        // decades with cell/separation, and the SAME-level block does not follow it — which is what
        // says this is the cross-level quadrature rather than the mesh simply being coarse.
        //
        // IF THIS GOES RED BECAUSE THE ERROR SHRANK, that is a fix and not a regression:
        // PlanarLevels.ValidatedCellOverSeparation, the note in PlanarSolve.LevelSeparationNotes
        // and MIM-3's verdict in RESOLVED.md all rest on these numbers and must be re-measured
        // rather than this bound relaxed.
        var fine   = Measure(0.2e-6, 1.0);
        var coarse = Measure(0.2e-6, 20.0);

        Assert.True(fine.Cross < 1e-5,
            $"cross-level at cell/separation = 1: {fine.Cross:E2} (HISTORY's ladder: 2.2e-7).");
        Assert.True(coarse.Cross > 1e-2,
            $"cross-level at cell/separation = 20: {coarse.Cross:E2} (HISTORY's ladder: 1.5e-1).");
        Assert.True(coarse.Cross > 1e3 * fine.Cross,
            $"{fine.Cross:E2} at 1 vs {coarse.Cross:E2} at 20.");
        Assert.True(coarse.Same < 0.1 * coarse.Cross,
            $"the SAME-level block must not follow it: same {coarse.Same:E2}, cross {coarse.Cross:E2}.");
    }

    private (double Same, double Cross, int N) Measure(double d, double cellOverD)
    {
        var p    = Plates(d, cellOverD);
        var mesh = SurfaceMesher.Mesh(p, Uniform).Mesh;
        var lay  = mesh.Bases.Select(b => b.LayerIndex).ToArray();
        var lo   = Fill(mesh, p, PlanarFillSettings.Default);
        var hi   = Fill(mesh, p, Reference);
        var r    = (Block(lo, hi, lay, false), Block(lo, hi, lay, true), mesh.Bases.Count);
        _out.WriteLine($"d = {d * 1e6:G3} um, cell/separation = {cellOverD:G3}, N = {r.Item3}: " +
                       $"same-level {r.Item1:E2}, cross-level {r.Item2:E2}");
        return r;
    }

    private static string Note(double d, double plate)
    {
        var p = new PlanarProblem(
            [
                new PlanarConductorLayer("bottom plate", [Square(plate)], 4.1e7, 0.25e-6, Zlow),
                new PlanarConductorLayer("top plate",    [Square(plate)], 4.1e7, 0.25e-6, Zlow + d),
            ],
            GroundedSlab.GaAsStarter, 10e9, null, MimStack(d), null);
        var mesh = SurfaceMesher.Mesh(p, Uniform).Mesh;
        return Assert.Single(PlanarSolve.LevelSeparationNotes(p, mesh, 10e9));
    }
}
