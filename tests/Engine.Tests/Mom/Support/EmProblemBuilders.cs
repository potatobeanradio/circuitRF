using System.Numerics;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Engine.Tests.Mom.Support;

/// <summary>
/// The oracle geometries of §8. Tier 1/2 build an <see cref="EmMesh"/> <b>directly</b> rather than
/// going through <see cref="BoundaryMesher"/>: their exact closed forms are coaxial, so their
/// dielectric interface is a <i>cylinder</i>, which the horizontal-slab <see cref="EmProblem"/> of
/// R-mom-3 deliberately cannot express. Those tiers exist to validate the physics of §3 — the
/// potential/field kernel, the bound-charge row, the ε_r charge weighting and the image ground —
/// before any of it is asked to also be right about meshing. Tier 3 onward goes through the mesher.
/// </summary>
public static class EmProblemBuilders
{
    /// <summary>A regular n-gon inscribed in a circle, wound counter-clockwise.</summary>
    public static EmPoint[] Circle(double cx, double cy, double r, int n)
    {
        var p = new EmPoint[n];
        for (int i = 0; i < n; i++)
        {
            double th = 2.0 * Math.PI * i / n;
            p[i] = new EmPoint(cx + r * Math.Cos(th), cy + r * Math.Sin(th));
        }
        return p;
    }

    /// <summary>An axis-aligned rectangle, wound counter-clockwise.</summary>
    public static EmPoint[] Rect(double x0, double y0, double x1, double y1) =>
    [
        new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1),
    ];

    // ── Tier 1: conductors only, no dielectric ────────────────────────────────────────────────

    /// <summary>Coaxial line: inner radius a at V = 1, outer shell radius b at V = 0.
    /// C = 2πε₀εᵣ / ln(b/a).</summary>
    public static EmMesh Coax(double a, double b, int n, Complex epsFill)
    {
        var inner = Circle(0, 0, a, n);
        var outer = Circle(0, 0, b, n);
        return BoundaryMesher.ConductorsOnly(
            [inner, outer], ["inner", "outer"],
            BoundaryMesher.UniformTemplate([inner, outer]),
            ground: null,
            epsOutside: (_, _) => epsFill);
    }

    /// <summary>Round wire of radius a with its centre h above a ground plane at y = 0.
    /// C = 2πε₀ / acosh(h/a). This is what tests R-mom-7's image.</summary>
    public static EmMesh WireOverGround(double a, double h, int n)
    {
        var wire = Circle(0, h, a, n);
        return BoundaryMesher.ConductorsOnly(
            [wire], ["wire"],
            BoundaryMesher.UniformTemplate([wire]),
            new EmGroundPlane(0, double.PositiveInfinity));
    }

    /// <summary>Two parallel round wires of radius a, centres d apart.
    /// C_odd = ½(C₁₁ − C₁₂) = πε₀ / acosh(d/2a).</summary>
    public static EmMesh TwoWires(double a, double d, int n)
    {
        var w1 = Circle(-0.5 * d, 0, a, n);
        var w2 = Circle(+0.5 * d, 0, a, n);
        return BoundaryMesher.ConductorsOnly(
            [w1, w2], ["w1", "w2"],
            BoundaryMesher.UniformTemplate([w1, w2]),
            ground: null);
    }

    // ── Tier 2: bound charge on a cylindrical interface ───────────────────────────────────────

    /// <summary>
    /// Two-layer coax: ε₁ for a &lt; r &lt; r_m, ε₂ for r_m &lt; r &lt; b.
    /// C = 2πε₀ / [ ln(r_m/a)/ε₁ + ln(b/r_m)/ε₂ ] — exact, and the only cheap closed form that
    /// genuinely exercises a dielectric interface.
    /// </summary>
    public static EmMesh TwoLayerCoax(double a, double rm, double b, Complex eps1, Complex eps2, int n)
    {
        var inner = Circle(0, 0, a, n);
        var outer = Circle(0, 0, b, n);
        var mesh = BoundaryMesher.ConductorsOnly(
            [inner, outer], ["inner", "outer"],
            BoundaryMesher.UniformTemplate([inner, outer]),
            ground: null,
            // The inner conductor faces ε₁; the outer shell's charge sits on its inner face, in ε₂.
            epsOutside: (mid, _) => (mid.X * mid.X + mid.Y * mid.Y) < rm * rm ? eps1 : eps2);

        var segs = new List<EmSegment>(mesh.Segments);
        segs.AddRange(CylindricalInterface(rm, n, eps1, eps2));
        return mesh with { Segments = segs };
    }

    /// <summary>
    /// A cylindrical dielectric interface at radius r. The reference normal is the outward radial,
    /// so region 1 (behind it) is the inside — K = (ε_inside − ε_outside)/(ε_inside + ε_outside).
    /// </summary>
    public static List<EmSegment> CylindricalInterface(double r, int n, Complex epsInside, Complex epsOutside)
    {
        var poly = Circle(0, 0, r, n);
        var k = (epsInside - epsOutside) / (epsInside + epsOutside);
        var segs = new List<EmSegment>(n);
        for (int i = 0; i < n; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % n];
            var d = b - a;
            var outward = (d * (1.0 / d.Norm)).RightNormal;   // CCW ⇒ right normal is radially out
            segs.Add(new EmSegment(a, b, outward, EmSegmentKind.DielectricInterface,
                                   -1, 0, Complex.One, k));
        }
        return segs;
    }

    // ── Tier 3+: the real thing, through the mesher ───────────────────────────────────────────

    public const double CopperSigma = 5.8e7;      // S/m
    public const double GoldSigma   = 4.1e7;      // S/m

    /// <summary>
    /// A single microstrip: strip of width w and thickness t on a substrate of height h, over a
    /// ground plane at y = 0, air above.
    /// </summary>
    public static EmProblem Microstrip(
        double w, double h, double t, double epsR,
        double tanD          = 0,
        double sigmaSm       = CopperSigma,
        double lengthMeters  = 0.020,
        double groundSigmaSm = CopperSigma,
        Complex? z0          = null)
    {
        var strip = Rect(-0.5 * w, h, 0.5 * w, h + t);
        var sub   = new EmMaterial(epsR, tanD);

        var z = z0 ?? new Complex(50, 0);
        return new EmProblem(
            Conductors: [new EmConductor("strip", strip, sigmaSm)],
            Regions:
            [
                new EmDielectricRegion(double.NegativeInfinity, h, sub),
                new EmDielectricRegion(h, double.PositiveInfinity, EmMaterial.Air),
            ],
            Ground: new EmGroundPlane(0, groundSigmaSm),
            Ports:
            [
                new EmPort(1, "strip", null, z),
                new EmPort(2, "strip", null, z),
            ],
            LengthMeters: lengthMeters);
    }

    /// <summary>
    /// An edge-coupled microstrip PAIR: two strips of width <paramref name="w"/> (or
    /// <paramref name="w2"/> for the second, when asymmetry is wanted) separated by gap
    /// <paramref name="s"/>, on a substrate of height h over a ground plane at y = 0.
    ///
    /// <para><b>Ports follow D3: 2k−1 is conductor k's NEAR end, 2k its FAR end.</b> So ports 1 and 2
    /// are the two ends of conductor A, 3 and 4 the two ends of conductor B. A transposed port map
    /// produces a coupler whose through and coupled ports are swapped — smooth, plausible, wrong, and
    /// invisible in a magnitude plot of a symmetric structure — so it is stated once here and once in
    /// the extractor, and pinned by a test whose WRONG pairing fails.</para>
    /// </summary>
    public static EmProblem CoupledMicrostrip(
        double w, double s, double h, double t, double epsR,
        double  tanD          = 0,
        double  sigmaSm       = CopperSigma,
        double  lengthMeters  = 0.020,
        double  groundSigmaSm = CopperSigma,
        double? w2            = null,
        Complex? z0           = null)
    {
        double wb = w2 ?? w;
        // Centred on the gap, so the pair is mirror-symmetric about x = 0 when w2 == w.
        double aRight = -0.5 * s, aLeft = aRight - w;
        double bLeft  = +0.5 * s, bRight = bLeft + wb;

        var z = z0 ?? new Complex(50, 0);
        return new EmProblem(
            Conductors:
            [
                new EmConductor("a", Rect(aLeft, h, aRight, h + t), sigmaSm),
                new EmConductor("b", Rect(bLeft, h, bRight, h + t), sigmaSm),
            ],
            Regions:
            [
                new EmDielectricRegion(double.NegativeInfinity, h, new EmMaterial(epsR, tanD)),
                new EmDielectricRegion(h, double.PositiveInfinity, EmMaterial.Air),
            ],
            Ground: new EmGroundPlane(0, groundSigmaSm),
            Ports:
            [
                new EmPort(1, "a", null, z), new EmPort(2, "a", null, z),
                new EmPort(3, "b", null, z), new EmPort(4, "b", null, z),
            ],
            LengthMeters: lengthMeters);
    }

    /// <summary>
    /// <b>N parallel microstrips</b> — the general case L7b-b ships. Strips of the given widths are
    /// laid left to right separated by <paramref name="gap"/>, centred on x = 0, on a substrate of
    /// height h over a ground plane at y = 0.
    ///
    /// <para><b>Ports follow D3 unchanged: 2k−1 is conductor k's NEAR end, 2k its FAR end.</b> The
    /// per-conductor loop is the same one <c>CoupledMicrostrip</c> writes out by hand for two, which
    /// is the point — the port map does not become a different rule at N &gt; 2.</para>
    ///
    /// <para>Widths may differ, which is what makes this the ASYMMETRIC oracle too: N conductors
    /// pushed far apart must reproduce N independent single lines <i>of their own widths</i>.</para>
    /// </summary>
    public static EmProblem MulticonductorMicrostrip(
        double[] widths, double gap, double h, double t, double epsR,
        double  tanD          = 0,
        double  sigmaSm       = CopperSigma,
        double  lengthMeters  = 0.020,
        double  groundSigmaSm = CopperSigma,
        Complex? z0           = null)
    {
        ArgumentNullException.ThrowIfNull(widths);
        if (widths.Length < 1) throw new ArgumentException("At least one strip.", nameof(widths));

        double total = gap * (widths.Length - 1);
        foreach (double w in widths) total += w;

        var conductors = new List<EmConductor>(widths.Length);
        var ports      = new List<EmPort>(2 * widths.Length);
        var z = z0 ?? new Complex(50, 0);

        double x = -0.5 * total;
        for (int k = 0; k < widths.Length; k++)
        {
            string name = ((char)('a' + k)).ToString();
            conductors.Add(new EmConductor(name, Rect(x, h, x + widths[k], h + t), sigmaSm));
            ports.Add(new EmPort(2 * k + 1, name, null, z));   // D3: near end
            ports.Add(new EmPort(2 * k + 2, name, null, z));   // D3: far end
            x += widths[k] + gap;
        }

        return new EmProblem(
            Conductors: conductors,
            Regions:
            [
                new EmDielectricRegion(double.NegativeInfinity, h, new EmMaterial(epsR, tanD)),
                new EmDielectricRegion(h, double.PositiveInfinity, EmMaterial.Air),
            ],
            Ground: new EmGroundPlane(0, groundSigmaSm),
            Ports: ports,
            LengthMeters: lengthMeters);
    }

    /// <summary>N parallel strips on the FR-4 starter stackup.</summary>
    public static EmProblem Fr4Multiconductor(
        double[] widths, double gap, double tanD = 0, double lengthMeters = 0.020,
        double sigmaSm = CopperSigma, double groundSigmaSm = CopperSigma)
        => MulticonductorMicrostrip(widths, gap, 1.6e-3, 35e-6, 4.4, tanD,
                                    sigmaSm, lengthMeters, groundSigmaSm);

    /// <summary>An edge-coupled pair on the FR-4 starter stackup.</summary>
    public static EmProblem Fr4CoupledMicrostrip(
        double w, double s, double tanD = 0, double lengthMeters = 0.020, double? w2 = null)
        => CoupledMicrostrip(w, s, 1.6e-3, 35e-6, 4.4, tanD,
                             lengthMeters: lengthMeters, w2: w2);

    /// <summary>The two starter stackups of §2.4.</summary>
    public static EmProblem Fr4Microstrip(double w, double tanD = 0, double lengthMeters = 0.020)
        => Microstrip(w, 1.6e-3, 35e-6, 4.4, tanD, lengthMeters: lengthMeters);

    public static EmProblem GaAsMicrostrip(double w, double tanD = 0, double lengthMeters = 0.002)
        => Microstrip(w, 100e-6, 3e-6, 12.9, tanD, GoldSigma, lengthMeters);
}
