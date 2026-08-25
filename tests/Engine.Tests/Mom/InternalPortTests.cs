// The INTERNAL SHUNT PORT — the third port type this kernel builds, and the one whose current
// leaves the plane instead of running along it.
//
// The two in-plane kinds (an edge port, an internal delta gap) are one object cut in two places: a
// gap across the shared EDGE of two cells, driving the rooftop that spans it. An internal port is that
// object one dimension over — a gap at the foot of a via, driving the GROUND-ATTACHMENT bases that
// span it (PlanarBasisFunctions' header). Same incidence matrix, same Y = BᵀZ⁻¹B, same
// not-de-embedded argument. What is genuinely new is the return path, and every test here is about
// that: which bases the port drives, that the polarity is fixed rather than asked for, and the two
// measurements that tell a SHUNT port from a SERIES one.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class InternalPortTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private const double ViaSideM = 1.2e-3;

    /// <summary>
    /// An FR-4 line with a backside via at its centre — the shape a grounded component attaches to. The
    /// via's footprint is square and small enough to stay well inside <c>ValidatedRhoOverLambdaAtHeights</c>
    /// at the frequencies used here.
    /// </summary>
    private static PlanarProblem LineWithGroundVia(double lengthM, double fHz, double? atXM = null)
    {
        var problem = PlanarLineFixtures.Fr4Line(lengthM, fHz);
        var (x0, y0, x1, y1) = problem.Bounds();
        double xc = atXM ?? 0.5 * (x0 + x1), yc = 0.5 * (y0 + y1);

        var via = new PlanarVia(PlanarVia.GroundTerminal, 0,
                                [PlanarLineFixtures.Rect(xc - 0.5 * ViaSideM, yc - 0.5 * ViaSideM,
                                                         xc + 0.5 * ViaSideM, yc + 0.5 * ViaSideM)],
                                5.8e7);
        return problem with { Vias = [via] };
    }

    private static PlanarPort InternalPort(double xM, double yM, int number = 3, double z0 = 50.0)
        => new(number, new EmPoint(xM, yM), PlanarPortSide.MinX, z0, Kind: PlanarPortKind.Internal);

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // What it resolves to
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AnInternalPortDrivesTheWHOLEViaFootprint_NotOnlyTheCellUnderTheLabel()
    {
        // A via's footprint is one conductor at one potential, exactly as a wide feed's transverse
        // row is. Driving one cell of it would leave the remaining cells shorting the trace straight
        // to the plane BESIDE the port — a complete, plausible answer for a structure with a short
        // across it.
        var problem = LineWithGroundVia(8e-3, 2e9);
        var report  = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse);
        var (x0, y0, x1, y1) = problem.Bounds();

        var p = PlanarPorts.Resolve(report.Mesh, InternalPort(0.5 * (x0 + x1), 0.5 * (y0 + y1)));
        _out.WriteLine(p.Describe());

        int attachments = report.Mesh.Bases.Count(b => b.AttachesToGround);
        Assert.True(attachments > 1, $"the fixture must mesh the via into more than one cell (got {attachments})");
        Assert.Equal(attachments, p.BasisCount);

        Assert.Equal(PlanarPortKind.Internal, p.Kind);
        Assert.Equal(PlanarBasisDirection.Z, p.Direction);
        Assert.False(p.IsDeembeddable);

        // Every basis in the row is an attachment — a horizontal rooftop at the same (x, y) is a
        // different port and is never substituted.
        foreach (int b in p.BasisIndices) Assert.True(report.Mesh.Bases[b].AttachesToGround);

        // The area is the MESHED footprint, and the footprint's own edges are hard gridlines, so it
        // is the drawn area exactly rather than to a tolerance.
        Assert.Equal(ViaSideM * ViaSideM, p.FootprintAreaM2, 12);
    }

    [Fact]
    public void ThePolarityIsFIXED_AndThePortsOwnSideIsNotReadAtAll()
    {
        // An internal delta gap has metal on both lips, so which one is + has to be stated. A via
        // port's second terminal is the ground plane, and the reference terminal of a port is never
        // the thing at ground: + is the metal, − is the plane, and positive current enters the
        // metal from below — the same "into the structure" convention every other port here uses.
        // Every vertical basis carries current +z, so that fixed convention is an incidence sign of
        // +1, and the port's own Side is not read at all. (Which sign expresses it is the step that
        // is easy to write down backwards; the three-way-node gate below is what settles it.)
        var problem = LineWithGroundVia(8e-3, 2e9);
        var mesh    = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse).Mesh;
        var (x0, y0, x1, y1) = problem.Bounds();
        double xc = 0.5 * (x0 + x1), yc = 0.5 * (y0 + y1);

        var a = PlanarPorts.Resolve(mesh, InternalPort(xc, yc));
        var b = PlanarPorts.Resolve(mesh, new PlanarPort(3, new EmPoint(xc, yc), PlanarPortSide.MaxY,
                                                         50.0, Kind: PlanarPortKind.Internal));

        Assert.Equal(+1.0, a.IncidenceSign);
        Assert.Equal(+1.0, b.IncidenceSign);
        Assert.Equal(a.BasisIndices, b.BasisIndices);
    }

    [Fact]
    public void TheReportSaysWhereTheAnswerIs_AndThatNothingIsDeembedded()
    {
        var problem = LineWithGroundVia(8e-3, 2e9);
        var mesh    = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse).Mesh;
        var (x0, y0, x1, y1) = problem.Bounds();

        string text = PlanarPorts.Resolve(mesh, InternalPort(0.5 * (x0 + x1), 0.5 * (y0 + y1))).Describe();
        _out.WriteLine(text);

        Assert.Contains("internal port", text);
        Assert.Contains("NOT de-embedded", text);
        Assert.Contains("UP the via into the conductor", text);
    }

    [Fact]
    public void TheINCIDENCERowIsTheAttachmentRow_AndTheSameSignReadsItBack()
    {
        // The default gate's stand-in for the four solves below, which are all in the Benchmark tier
        // because a via-bearing fill costs seconds. This asks the one thing that can be asked without
        // solving: that B's column is +1 on exactly this port's attachment bases and zero on every
        // other unknown, and that reading a current vector back through the same port applies the
        // same sign. That pairing is what makes Y = BᵀZ⁻¹B an admittance matrix rather than a
        // sign-scrambled relative of one (PlanarExcitation's header), and it is the cheap half of
        // what the solves gate.
        var problem = LineWithGroundVia(8e-3, 2e9);
        var mesh    = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse).Mesh;
        var (x0, y0, x1, y1) = problem.Bounds();

        var p   = PlanarPorts.Resolve(mesh, InternalPort(0.5 * (x0 + x1), 0.5 * (y0 + y1)));
        var rhs = PlanarExcitation.RightHandSide(mesh.Bases.Count, p);

        for (int m = 0; m < mesh.Bases.Count; m++)
            Assert.Equal(p.BasisIndices.Contains(m) ? Complex.One : Complex.Zero, rhs[m]);

        // Read it back: a unit current in every driven basis is BasisCount amps through the port,
        // with the port's own sign — not the negative of it, and not a sum over the whole mesh.
        Assert.Equal(new Complex(p.BasisCount, 0), PlanarExcitation.PortCurrent(rhs, p));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The refusals — the two ways this fails are different and a user can act on only one at a time
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ThePathToGroundIsBUILTWhenTheArtworkHasNone_AtTheSIZEITWASGIVEN()
    {
        // The ordinary case for this port type: it is placed on the METAL, and the path down to the
        // plane is the solver's problem. The size is the caller's — the technology's own default via
        // — and NOT a mesh cell: this via's inductance is part of the port's answer, so sizing it
        // from the mesh would make that answer a function of Cells per wavelength, and refining the
        // mesh would move it for a reason that has nothing to do with convergence.
        var problem = PlanarLineFixtures.Fr4Line(8e-3, 2e9);
        var (x0, y0, x1, y1) = problem.Bounds();
        double xc = 0.5 * (x0 + x1), yc = 0.5 * (y0 + y1);

        Assert.Empty(problem.ViaList);

        PlanarPort[] ports = [new(1, new EmPoint(xc, yc), PlanarPortSide.MinX, 50.0,
                                  Kind: PlanarPortKind.Internal, GroundPathWidthM: 0.3048e-3)];

        var (grown, built, notes) = PlanarGroundPath.Extend(problem, ports);
        _out.WriteLine(string.Join("\n", notes));

        var via = Assert.Single(grown.ViaList);
        Assert.True(via.ToGround);
        Assert.Equal(0.3048e-3, Assert.Single(built).WidthM, 15);

        // A SQUARE of that side, centred on the label — checkable by inspection rather than against
        // another run of the code under test.
        var (vx0, vy0, vx1, vy1) = Bounds(via.Polygons[0]);
        Assert.Equal(xc - 0.1524e-3, vx0, 12);
        Assert.Equal(xc + 0.1524e-3, vx1, 12);
        Assert.Equal(yc - 0.1524e-3, vy0, 12);
        Assert.Equal(yc + 0.1524e-3, vy1, 12);

        // It meshes, it attaches, and the port resolves onto it — the point of building it at all.
        var report = SurfaceMesher.Mesh(grown, PlanarLineFixtures.Coarse);
        var p = PlanarPorts.Resolve(report.Mesh, ports[0]);
        _out.WriteLine(p.Describe());
        Assert.True(p.BasisCount >= 1);
        Assert.Contains(notes, n => n.Contains("was built for it"));
    }

    [Fact]
    public void ADRAWNViaWINS_AndAProblemWithNothingToAddIsHandedOnBYREFERENCE()
    {
        // The built path only ever fills in where the artwork has none. Assert.Same rather than a
        // vertex count, for the reason PlanarFeedExtension's own gate uses it: "the problem reaches
        // the mesher by reference" is the property that keeps every recorded number reproducible.
        var problem = LineWithGroundVia(8e-3, 2e9);
        var (x0, y0, x1, y1) = problem.Bounds();

        PlanarPort[] ports = [new(1, new EmPoint(0.5 * (x0 + x1), 0.5 * (y0 + y1)),
                                  PlanarPortSide.MinX, 50.0,
                                  Kind: PlanarPortKind.Internal, GroundPathWidthM: 0.3048e-3)];

        var (grown, built, notes) = PlanarGroundPath.Extend(problem, ports);
        Assert.Same(problem, grown);
        Assert.Empty(built);
        Assert.Empty(notes);
    }

    private static (double X0, double Y0, double X1, double Y1) Bounds(PlanarPolygon poly)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var v in poly.Outer)
        {
            x0 = Math.Min(x0, v.X); x1 = Math.Max(x1, v.X);
            y0 = Math.Min(y0, v.Y); y1 = Math.Max(y1, v.Y);
        }
        return (x0, y0, x1, y1);
    }

    [Fact]
    public void AnInternalPortOnMetalWithNoViaAndNoSIZE_IsRefusedNamingTheMISSINGVIA()
    {
        // Nothing is built without a size, so a caller that supplies none gets the refusal rather
        // than a via of some plausible-looking default. This is the headless path.

        var problem = PlanarLineFixtures.Fr4Line(8e-3, 2e9);
        var mesh    = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse).Mesh;
        var (x0, y0, x1, y1) = problem.Bounds();

        Assert.False(PlanarPorts.TryResolve(mesh, InternalPort(0.5 * (x0 + x1), 0.5 * (y0 + y1)),
                                            out _, out string? why));
        _out.WriteLine(why);
        Assert.Contains("connects to the ground plane", why!);
        Assert.Contains("Draw a via", why!);
    }

    [Fact]
    public void AnInternalPortBESIDEItsVia_IsRefusedRatherThanSnappedToTheNearestOne()
    {
        // The via IS there, so the remedy is different: move the label onto it. Snapping silently
        // would drive a path the user did not point at, and on a board with several ground vias
        // "the nearest one" is a coin flip.
        var problem = LineWithGroundVia(8e-3, 2e9);
        var mesh    = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse).Mesh;
        var (x0, y0, x1, y1) = problem.Bounds();

        Assert.False(PlanarPorts.TryResolve(mesh, InternalPort(0.5 * (x0 + x1) + 2e-3, 0.5 * (y0 + y1)),
                                            out _, out string? why));
        _out.WriteLine(why);
        Assert.Contains("no via to the ground plane under it", why!);
        Assert.Contains("beside its via", why!);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The solve — all four are Category=Benchmark, measured at 5.5-11.5 s each
    //
    // Not because any of them is a timing measurement (they assert physics, not wall clock) but
    // because a via-bearing fill costs seconds per frequency whatever the mesh: the DCIM fit count
    // for a problem carrying vertical current is a fixed per-frequency cost, so shrinking the board
    // does not shrink it. The default gate keeps the resolution, the refusals and the incidence-row
    // wiring above, which is where a change to this file would break first.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void DeembeddingIsATrueNoOpForAnInternalPort()
    {
        // The same claim PlanarSolve.IdentityBox makes for the delta gap, asked of the kind whose
        // cut is not even in the plane. Tolerant rather than exact because the ON path still passes
        // through PlanarDeembed.Apply's LU — of the identity matrix.
        var problem = LineWithGroundVia(8e-3, 2e9);
        var (x0, y0, x1, y1) = problem.Bounds();

        var report   = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse);
        var resolved = PlanarPorts.ResolveAll(
            report.Mesh, [InternalPort(0.5 * (x0 + x1), 0.5 * (y0 + y1), number: 1)]);

        var on  = PlanarSolve.Run(problem, report.Mesh, resolved, [2e9]);
        var off = PlanarSolve.Run(problem, report.Mesh, resolved, [2e9],
                                  PlanarSolveSettings.Default with { Deembed = false });

        var a = on.Points[0].S[0, 0];
        var b = off.Points[0].S[0, 0];
        _out.WriteLine($"de-embedding on: {a}   off: {b}   |Δ| = {(a - b).Magnitude:E3}");
        Assert.True((a - b).Magnitude < 1e-12, $"de-embedding moved an internal port by {(a - b).Magnitude:E3}");
        Assert.Contains(on.Notes, n => n.Contains("internal ports"));
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void AnInternalPortAtTheCENTREOfASymmetricLine_IsSYMMETRIC_WhereADeltaGapIsANTIsymmetric()
    {
        // THE measurement that says this is an internal port rather than a series one, and it is the
        // exact counterpart of InternalDeltaGapPortTests' antisymmetry gate.
        //
        // A delta gap drives current INTO the line on one side of its cut and OUT of it on the
        // other, so a centred gap gives S₁₃ = −S₂₃. An internal port injects current against the ground
        // plane: both halves of the line see the same thing, so S₁₃ = +S₂₃. The difference is a hard
        // π and a magnitude plot would never show it.
        var problem = LineWithGroundVia(8e-3, 2e9);
        var (x0, y0, x1, y1) = problem.Bounds();
        double xc = 0.5 * (x0 + x1), yc = 0.5 * (y0 + y1);

        PlanarPort[] ports =
        [
            new(1, new EmPoint(x0, yc), PlanarPortSide.MinX, 50.0),
            new(2, new EmPoint(x1, yc), PlanarPortSide.MaxX, 50.0),
            InternalPort(xc, yc),
        ];

        var report   = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse);
        var resolved = PlanarPorts.ResolveAll(report.Mesh, ports);
        foreach (var p in resolved) _out.WriteLine(p.Describe());

        var s = PlanarSolve.Run(problem, report.Mesh, resolved, [2e9]).Points[0].S;
        _out.WriteLine($"S13 = {s[0, 2]}   S23 = {s[1, 2]}");
        _out.WriteLine($"S11 = {s[0, 0]}   S22 = {s[1, 1]}   S33 = {s[2, 2]}");

        double scale = Math.Max(s[0, 2].Magnitude, 1e-12);
        Assert.True((s[0, 2] - s[1, 2]).Magnitude / scale < 1e-6,
                    $"a centred internal port is not symmetric: S13 = {s[0, 2]}, S23 = {s[1, 2]}");
        Assert.True((s[0, 0] - s[1, 1]).Magnitude / Math.Max(s[0, 0].Magnitude, 1e-12) < 1e-6,
                    $"a symmetric structure reported S11 != S22: {s[0, 0]} vs {s[1, 1]}");
        Assert.True((s[0, 2] - s[2, 0]).Magnitude / scale < 1e-6, "S13 != S31");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void ASmallStructureAtLowFrequencyIsATHREEWAYNODE_WhichIsWhatFIXESTheSIGN()
    {
        // The sign of a one-port is unobservable through any termination — every reduction carries
        // S_i3·S_3j, and both flip together. What DOES observe it is the port's own coupling to the
        // others, so the oracle has to be a structure whose answer is known independently.
        //
        // A short line with a via to ground at its centre, at a frequency where every dimension is a
        // small fraction of a wavelength and the via's ωL is a few percent of 50 Ω, is three equal
        // lines meeting at one node above ground: S_ii = −1/3, S_ij = +2/3. The POSITIVE 2/3 is the
        // whole point — with the opposite polarity (+ at the plane instead of at the metal) every
        // term through this port comes back turned by π, and nothing else in the matrix changes.
        //
        // This gate EARNED its place: the first implementation took the other sign, from a written
        // derivation of which lip of the gap is "+", and produced exactly that matrix — |S₁₃| right
        // to two figures, S₁₃ = −0.66, everything else untouched.
        var problem = LineWithGroundVia(4e-3, 1e9);
        var (x0, y0, x1, y1) = problem.Bounds();
        double xc = 0.5 * (x0 + x1), yc = 0.5 * (y0 + y1);

        PlanarPort[] ports =
        [
            new(1, new EmPoint(x0, yc), PlanarPortSide.MinX, 50.0),
            new(2, new EmPoint(x1, yc), PlanarPortSide.MaxX, 50.0),
            InternalPort(xc, yc),
        ];

        var report   = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse);
        var resolved = PlanarPorts.ResolveAll(report.Mesh, ports);
        var s = PlanarSolve.Run(problem, report.Mesh, resolved, [1e9]).Points[0].S;

        for (int i = 0; i < 3; i++)
            _out.WriteLine($"S{i + 1}1 = {s[i, 0]}   S{i + 1}2 = {s[i, 1]}   S{i + 1}3 = {s[i, 2]}");

        Assert.True(s[0, 2].Real > 0.4,
            $"the internal port's coupling to the line came back as {s[0, 2]}; a three-way node's is " +
            "≈ +2/3, and a NEGATIVE real part is the polarity reversed");
        Assert.True((s[0, 2] - new Complex(2.0 / 3.0, 0)).Magnitude < 0.20,
                    $"S13 = {s[0, 2]}, expected ≈ +2/3 for a small three-way node");
        Assert.True((s[2, 2] - new Complex(-1.0 / 3.0, 0)).Magnitude < 0.20,
                    $"S33 = {s[2, 2]}, expected ≈ −1/3 for a small three-way node");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void SHORTINGTheInternalPortReproducesThePlainTwoPortSolveOfTheSameBoard()
    {
        // The end-to-end oracle for everything between the incidence row and the published matrix,
        // and it needs no external data: a port is a gap with a source in it, so terminating that
        // gap in a SHORT is the structure with no gap at all — the same board, the same via, solved
        // as an ordinary two-port. Reducing the 3-port with Γ₃ = −1 must reproduce it.
        //
        // This is insensitive to the port's polarity (the reduction carries S_i3·S_3j), which is
        // exactly why the three-way-node gate above exists as well: between them they pin the
        // magnitude AND the sign.
        var problem = LineWithGroundVia(8e-3, 2e9);
        var (x0, y0, x1, y1) = problem.Bounds();
        double xc = 0.5 * (x0 + x1), yc = 0.5 * (y0 + y1);

        PlanarPort[] two   = [new(1, new EmPoint(x0, yc), PlanarPortSide.MinX, 50.0),
                              new(2, new EmPoint(x1, yc), PlanarPortSide.MaxX, 50.0)];
        PlanarPort[] three = [two[0], two[1], InternalPort(xc, yc)];

        var report = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse);
        var sTwo   = PlanarSolve.Run(problem, report.Mesh,
                                     PlanarPorts.ResolveAll(report.Mesh, two), [2e9]).Points[0].S;
        var sThree = PlanarSolve.Run(problem, report.Mesh,
                                     PlanarPorts.ResolveAll(report.Mesh, three), [2e9]).Points[0].S;

        var gamma = new Complex(-1, 0);                        // an ideal short across the gap
        var denom = Complex.One - sThree[2, 2] * gamma;
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                var reduced = sThree[i, j] + sThree[i, 2] * gamma * sThree[2, j] / denom;
                _out.WriteLine($"S{i + 1}{j + 1}: shorted 3-port {reduced}   plain 2-port {sTwo[i, j]}   " +
                               $"|Δ| = {(reduced - sTwo[i, j]).Magnitude:E3}");
                Assert.True((reduced - sTwo[i, j]).Magnitude < 1e-9,
                    $"shorting the internal port did not reproduce the plain board at S{i + 1}{j + 1}: " +
                    $"{reduced} vs {sTwo[i, j]}");
            }
    }
}
