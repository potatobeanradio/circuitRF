namespace CircuitRF.Engine.Mom;

/// <summary>
/// The two closed-form 2D integrals kernel A is built from — the potential and the field of a
/// straight boundary segment carrying a uniform charge density σ (C/m², per unit length in z),
/// radiating into <b>free space</b>.
///
/// <para>Because bound (polarisation) charge is carried explicitly as an unknown
/// (<see cref="ChargeSolver"/>), every charge in this formulation radiates into vacuum and the
/// Green's function is the plain 2D logarithmic potential. There is no Sommerfeld integral, no
/// DCIM and no special function anywhere in this kernel, and an arbitrary number of dielectrics
/// costs nothing but more segments.</para>
///
/// <para><b>Frame convention.</b> For a segment a→b of length L, tangent û = (b−a)/L and normal
/// n̂ = (−û_y, û_x) — the <i>left</i> normal. Local coordinates of an observation point p are
/// x = (p−a)·û and y = (p−a)·n̂. Both returned quantities are frame-independent: <see cref="Field"/>
/// returns a vector in world coordinates and is invariant under reversing a↔b (verified by test),
/// so callers never have to reason about segment winding.</para>
/// </summary>
public static class Kernel2D
{
    private const double TwoPiEps0 = 2.0 * Math.PI * EmConstants.Eps0;

    /// <summary>
    /// F(u) = u·ln(u² + y²) − 2u + 2y·atan(u/y) — the antiderivative of 2·ln(hypot(u, y)).
    /// The u = 0 and y = 0 limits are taken analytically rather than left to produce NaN.
    /// </summary>
    private static double F(double u, double y)
    {
        double s = u * u + y * y;
        double t = s <= 0.0 ? 0.0 : u * Math.Log(s);      // u·ln(u²+y²) → 0 as u → 0
        double at = y == 0.0 ? 0.0 : 2.0 * y * Math.Atan(u / y);
        return t - 2.0 * u + at;
    }

    /// <summary>
    /// Potential coefficient P: the potential at <paramref name="p"/> per unit σ on the segment
    /// a→b. P = −Φ/(2πε₀) with Φ = ½·[F(L−x) − F(−x)] = ∫₀ᴸ ln r ds.
    ///
    /// <para>Collocation at the segment's own midpoint is <b>not</b> a special case — the general
    /// expression reduces analytically to <see cref="SelfPotential"/> there — but the dedicated
    /// entry point exists so the reduction can be pinned by a test.</para>
    /// </summary>
    public static double Potential(EmPoint a, EmPoint b, EmPoint p)
    {
        var d = b - a;
        double len = d.Norm;
        if (len <= 0.0) return 0.0;
        var u = d * (1.0 / len);
        var n = u.LeftNormal;

        var rel = p - a;
        double x = rel.Dot(u);
        double y = rel.Dot(n);

        double phi = 0.5 * (F(len - x, y) - F(-x, y));
        return -phi / TwoPiEps0;
    }

    /// <summary>
    /// P for collocation at the segment's own midpoint: L·(1 − ln(L/2)) / (2πε₀).
    /// The atan term vanishes in the y → 0 limit.
    /// </summary>
    public static double SelfPotential(double length)
        => length <= 0.0 ? 0.0 : length * (1.0 - Math.Log(0.5 * length)) / TwoPiEps0;

    /// <summary>
    /// Field coefficient: E at <paramref name="p"/> per unit σ on the segment a→b, in <b>world</b>
    /// coordinates. E = σ·∇Φ/(2πε₀) with, in the segment frame,
    /// <c>∂Φ/∂y = atan((L−x)/y) + atan(x/y)</c> (the angle the segment subtends at p) and
    /// <c>∂Φ/∂x = ln(r₁/r₂)</c>.
    ///
    /// <para><b>R-mom-5.</b> y == 0 is guarded explicitly: off the segment the subtended angle is
    /// 0; <i>on</i> it, it is π — which is the σ/(2ε₀) self-field that the dielectric-interface
    /// equation has already accounted for analytically, and which must therefore be
    /// <b>excluded</b> here. Getting this wrong double-counts the self-field and the solver
    /// converges smoothly to the wrong answer; Tier 0 checks this function against a finite
    /// difference of <see cref="Potential"/> rather than trusting it.</para>
    /// </summary>
    public static EmPoint Field(EmPoint a, EmPoint b, EmPoint p)
    {
        var d = b - a;
        double len = d.Norm;
        if (len <= 0.0) return default;
        var u = d * (1.0 / len);
        var n = u.LeftNormal;

        var rel = p - a;
        double x = rel.Dot(u);
        double y = rel.Dot(n);

        double r1 = Math.Sqrt(x * x + y * y);
        double r2 = Math.Sqrt((x - len) * (x - len) + y * y);

        // Observation exactly at an endpoint — ln(0) — cannot arise from midpoint collocation on a
        // non-degenerate mesh; return 0 rather than propagating an infinity into the fill.
        double dPhiDx = (r1 <= 0.0 || r2 <= 0.0) ? 0.0 : Math.Log(r1 / r2);
        double dPhiDy = y == 0.0 ? 0.0 : Math.Atan((len - x) / y) + Math.Atan(x / y);

        double et = dPhiDx / TwoPiEps0;   // along û
        double en = dPhiDy / TwoPiEps0;   // along n̂
        return new EmPoint(et * u.X + en * n.X, et * u.Y + en * n.Y);
    }

    // ── Ground plane: an exact image (R-mom-7) ────────────────────────────────────────────────
    //
    // Every source segment contributes its mirror about y = Yg with NEGATED charge. Because *all*
    // charge — free and bound — is explicit and radiating into free space, the image makes φ = 0
    // on the plane EXACTLY, dielectrics included. This is not an approximation: there is no image
    // series, no correction term, and no assumption that the plane is far from the dielectric.

    /// <summary>Reflect a point about the horizontal plane y = <paramref name="yGround"/>.</summary>
    public static EmPoint Mirror(EmPoint p, double yGround) => new(p.X, 2.0 * yGround - p.Y);

    /// <summary>P including the exact ground image, when <paramref name="ground"/> is present.</summary>
    public static double PotentialWithImage(EmPoint a, EmPoint b, EmPoint p, EmGroundPlane? ground)
    {
        double v = Potential(a, b, p);
        if (ground is null) return v;
        return v - Potential(Mirror(a, ground.Y), Mirror(b, ground.Y), p);
    }

    /// <summary>E including the exact ground image, when <paramref name="ground"/> is present.</summary>
    public static EmPoint FieldWithImage(EmPoint a, EmPoint b, EmPoint p, EmGroundPlane? ground)
    {
        var e = Field(a, b, p);
        if (ground is null) return e;
        var img = Field(Mirror(a, ground.Y), Mirror(b, ground.Y), p);
        return e - img;
    }
}
