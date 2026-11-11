// PSV-3 gate 4 — the airbridge post that a capacitor film between the interconnect metals used to
// take out of the planar kernel.
//
// Until MIM-7 the capacitor dielectric was in the medium of every run on the MIM technology, so the
// Metal1-Metal2 post crossed the film's top interface and PlanarKernel.CanSolve refused the WHOLE run.
// MIM-7 tied the film to its mask, which removed the interface from runs with no capacitor; it did not
// make the crossing solvable. This fixture puts the film back in the medium unconditionally — which
// is what the separate MIM technology stated — so the post crosses it, and holds that the run is
// accepted, solves reciprocal and passive, and gives the post an inductance beside the plain one.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public class PsvMimPostTests(ITestOutputHelper output)
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Metal1 = new(1, 0);
    private static readonly LayerKey Metal2 = new(2, 0);
    private static readonly LayerKey Post   = new(3, 0);

    private static long Um(double v) => (long)Math.Round(v * Dbu);
    private static RectShape Rect(LayerKey layer, double x0, double y0, double x1, double y1) =>
        new() { Layer = layer, X1 = Um(x0), Y1 = Um(y0), X2 = Um(x1), Y2 = Um(y1) };
    private static LabelShape Port(LayerKey layer, double x, double y, string name) =>
        new() { Layer = layer, X = Um(x), Y = Um(y), Text = name, Height = Um(4), IsPort = true };

    /// <summary>MimCapacitorTests' airbridge: two Metal1 feeds bridged by Metal2 on two posts.</summary>
    private static List<LayoutShape> Airbridge() =>
    [
        Rect(Metal1, 0,   0, 120, 100),
        Rect(Metal1, 180, 0, 300, 100),
        Rect(Metal2, 20,  0, 280, 100),
        Rect(Post,   40, 30,  80,  70),
        Rect(Post,  220, 30, 260,  70),
        Port(Metal1, 0,   50, "P1"),
        Port(Metal1, 300, 50, "P2"),
    ];

    /// <summary>The shipped MMIC technology with the capacitor film in the medium of EVERY run.</summary>
    private static Technology FilmAlwaysPresent()
    {
        var tech = StarterTechnologies.MmicGaAs();
        tech.Stackup.Layers.Single(l => l.Name == "MIM Dielectric").PresentWithLayer = null;
        return tech;
    }

    [Fact]
    public void Gate4_ThePostThroughTheCapacitorFilm_Solves_AndItsInductanceSitsBesideThePlainOne()
    {
        const double f = 30e9;
        var shapes = Airbridge();
        var film  = PlanarExtractor.Extract(shapes, FilmAlwaysPresent(),            Dbu, f);
        var plain = PlanarExtractor.Extract(shapes, StarterTechnologies.MmicGaAs(), Dbu, f);
        Assert.True(film.Ok,  film.Refusal);
        Assert.True(plain.Ok, plain.Refusal);

        var a = film.Problem!;
        var post = Assert.Single(a.ViaList);
        double lo = a.LevelZ(post.LowerLayerIndex), hi = a.LevelZ(post.UpperLayerIndex);
        var stack = a.EffectiveStack;

        // The premise: the post's span crosses at least one interface of the medium.
        Assert.NotEqual(stack.RegionOf(lo + 1e-3 * (hi - lo)), stack.RegionOf(hi - 1e-3 * (hi - lo)));

        var verdict = new PlanarKernel().CanSolve(a);
        Assert.True(verdict.Ok, verdict.Reason);

        // The product path end to end: mesh, fill, solve — reciprocal and passive.
        var ports = EmPortExtraction.Extract(shapes, a, Dbu);
        Assert.True(ports.Ok, ports.Refusal);
        var solved = new PlanarKernel().Solve(
            a, new PlanarMeshSettings(Auto: false, CellsPerWavelength: 6, EdgeMesh: false),
            ports.Ports, [f], PlanarSolveSettings.Default with { Deembed = false });
        var s = solved.Solve.Points[0].RawS;
        Assert.Equal(s[0, 1].Real,      s[1, 0].Real,      9);
        Assert.Equal(s[0, 1].Imaginary, s[1, 0].Imaginary, 9);
        foreach (var x in new[] { s[0, 0], s[0, 1], s[1, 0], s[1, 1] })
            Assert.True(x.Magnitude <= 1 + 1e-9);

        // The post's own series inductance, one 40 x 40 µm post, in each run's medium and span.
        var b = plain.Problem!;
        double withFilm = PostInductance(lo, hi, stack, 40e-6, f);
        double without  = PostInductance(b.LevelZ(0), b.LevelZ(1), b.EffectiveStack, 40e-6, f);
        // The same span with no film under it, for the record. This is the jωV part of one vertical
        // basis, and in this formulation G_A^zz carries a dielectric-dependent term (the TE content of
        // ∇φ, and an R_e image at the via's foot), so it is NOT a pure magnetostatic number and is
        // expected to move with the medium; the S-parameters are the physical comparison
        // (PsvViaThroughInterfaceTests, testdata/em-psv/via-two-dielectrics).
        double sameSpan = PostInductance(lo, hi, b.EffectiveStack, 40e-6, f);
        output.WriteLine(
            $"post with the film in the medium: z {lo * 1e6:G6} → {hi * 1e6:G6} µm across {stack}, " +
            $"L = {withFilm * 1e12:F3} pH");
        output.WriteLine(
            $"post on the plain starter:        z {b.LevelZ(0) * 1e6:G6} → {b.LevelZ(1) * 1e6:G6} µm, " +
            $"L = {without * 1e12:F3} pH; the film run's span with no film under it, " +
            $"L = {sameSpan * 1e12:F3} pH ({withFilm / sameSpan - 1:P3})");
        output.WriteLine($"|S21| raw at {f / 1e9:G3} GHz with the film: {s[1, 0].Magnitude:F4}");
        Assert.True(withFilm > 0 && without > 0 && sameSpan > 0);
    }

    /// <summary>One post over a w×w footprint from <paramref name="lo"/> to <paramref name="hi"/> in
    /// <paramref name="stack"/>, with the charge term cancelled algebraically (ViaPhysicsTests' construction).</summary>
    private static double PostInductance(double lo, double hi, LayerStack stack, double w, double fHz)
    {
        var mesh = new PlanarMesh([new PlanarCell(0, 0, 0, 0, 0, w, w), new PlanarCell(1, 1, 0, 0, 0, w, w)],
                                  [new PlanarBasis(0, 0, 1, PlanarBasisDirection.Z)],
                                  ["L0", "L1"], [0, w], [0, w]);
        var levels = new PlanarLevels([lo, hi]);
        var cores = PlanarFill.BuildCores(mesh);
        var set = new PlanarKernelSet(new LayeredSpectralGreens(stack, fHz)).For(cores);
        double w1 = 2 * Math.PI * fHz, w2 = 10.0 * w1;
        Complex z1 = PlanarFill.FillMultiLevel(cores, set, levels, w1)[0, 0];
        Complex z2 = PlanarFill.FillMultiLevel(cores, set, levels, w2)[0, 0];
        return ((w2 * z2 - w1 * z1) / (Complex.ImaginaryOne * (w2 * w2 - w1 * w1))).Real;
    }
}
