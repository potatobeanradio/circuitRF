// The gripper inverse — "the user let go HERE; what value is that?" (docs/design/smith-chart.md
// §4.3; brief-smith-3-gripper-inverse.md).
//
// CLOSED FORM IN EVERY CASE, and that is not luck. A gripper changes exactly ONE parameter of one
// element, and the reachable set of a single free parameter is a known locus — a line, a ray or a
// circle — so the answer is the PROJECTION of the drag point onto it. There is no search, no
// Newton step and no tolerance anywhere in this file.
//
// ONLY THE COMPONENT OF THE DRAG THE PARAMETER CAN REACH IS USED; the perpendicular component is
// discarded. That is not an approximation — it is what "drag along the arc" means, and it is why
// the gripper follows the curve rather than the cursor.
//
// WHICH PLANE THE PROJECTION HAPPENS IN IS THE ONE REAL DESIGN DECISION HERE, and it is: the plane
// in which the active parameter's own contribution is a STRAIGHT LINE.
//
//   - The single-parameter kinds (R, L, C) project in the PLACEMENT's own additive form — Z for a
//     series element, Y for a shunt one. There the element's contribution is Z_out = Z_in + Z_e (or
//     Y_out = Y_in + Y_e), the locus is a straight line through Z_in, and taking the real or the
//     imaginary part IS the orthogonal projection onto it. This is what §4.3's table spells out,
//     and why a shunt R reads Re(Y_d) − Re(Y_in) rather than Re(Z_d − Z_in): the reachable set of a
//     shunt conductance is a horizontal ray in Y and nothing at all like one in Z.
//
//   - The COMPOSITE kinds (SRLC, PRLC, Z1P) project in their OWN form instead — Z for SRLC and Z1P,
//     Y for PRLC — because that is the one plane where all of their parameters are simultaneously
//     linear. For an SRLC in SHUNT the required element admittance Y_e = Y_d − Y_in is still exact,
//     so inverting it to Z_e loses nothing; the projection then happens on the straight line where
//     the parameter lives. The consequence worth stating: for those cross-placement cases the
//     answer is the orthogonal projection in Z rather than in Y, which is exact for every REACHABLE
//     drag and merely a choice for an unreachable one. Both readings invert; this one is the one
//     whose arithmetic is a one-liner per parameter instead of a circle fit.
//
// PHYSICALITY IS ENFORCED AT THE PIN, AND REPORTED — never silently. L, C and R are non-negative, a
// TLIN's Z₀ is positive, an electrical length is non-negative. WHERE the pin lands depends on how
// the parameter enters, and getting this wrong is the defect that makes a drag feel broken:
//
//   - A parameter that enters LINEARLY (a series R, a series L, a shunt C, an SRLC's L) pins at
//     ZERO. Its reachable set runs from 0 outward, so zero is the boundary the drag overshot.
//   - A parameter that enters RECIPROCALLY (a shunt R, a series C, a shunt L, an SRLC's C) pins at
//     the CURRENT value. Its reachable set runs to the demand's boundary as the parameter goes to
//     INFINITY — a shunt R reaches zero conductance only at R = ∞ — so there is no representable
//     boundary value to sit on, and clamping such a case to zero would swing a near-open shunt R to
//     a dead short under a two-pixel move. The limit is named in the reason instead.
//
// NO CASE RETURNS NaN. A non-finite anything — the drag at Γ = 1 where Z_d is infinite, a degenerate
// Z_in, a non-positive frequency — returns the CURRENT value pinned, with a reason.
//
// NOTHING HERE WRITES TO THE DESIGN AND NOTHING HERE KNOWS WHAT A POINTER IS. Solve returns a
// number; brief 5 owns the gesture, the undo entry and the decision to commit it.

using System.Numerics;

namespace CircuitRF.Design.Smith;

/// <summary>
/// One gripper drag's answer: the value the active parameter should take.
/// </summary>
/// <param name="Value">The parameter's new value, in its own base SI unit — or DEGREES for
/// <see cref="SmithParameter.ElectricalLength"/>, which is the one named exception
/// <see cref="SmithElementValues.ElectricalLengthDeg"/> already makes. <b>Always finite.</b></param>
/// <param name="Pinned">True when the drag asked for something unphysical or unresolvable and
/// <paramref name="Value"/> is a boundary or the value the element already had.</param>
/// <param name="PinReason">A sentence naming the element, the parameter and the limit — what the
/// status strip says. Null exactly when <paramref name="Pinned"/> is false.</param>
public readonly record struct InverseResult(double Value, bool Pinned, string? PinReason);

/// <summary>
/// §4.3's inverse: the value of one element's active parameter that puts the walk's next node at
/// the drag point. See this file's header for the projection rule and the pinning rule.
/// </summary>
public static class SmithInverse
{
    /// <summary>
    /// Below this, a line's <c>tan θ</c> is taken as zero and the line transforms nothing.
    ///
    /// <para><b>It is not just a zero-length line</b>: a HALF-WAVE line has θ = π, whose tangent is
    /// 1.22e-16 rather than 0, and it transforms nothing either. A test for exact zero would send
    /// that case into a quadratic whose leading coefficient is a rounding error.</para>
    /// </summary>
    private const double TanIsZero = 1e-12;

    /// <summary>Below this magnitude a Γ is taken as the centre of the chart it is drawn on.</summary>
    private const double GammaIsCentre = 1e-12;

    // ═════════════════════════════════════════════════════════════════════════
    //  The entry point
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The value <paramref name="p"/> must take for element <paramref name="elementIndex"/> to
    /// carry the walk from <paramref name="zIn"/> to the drag point <paramref name="gammaDrag"/>.
    /// </summary>
    /// <param name="d">The document. Read only — <b>nothing here writes to it</b>.</param>
    /// <param name="elementIndex">Index into <see cref="SmithDesign.Elements"/>. This is the element
    /// the gripper belongs to; node k of the walk drags element k−1 (§4.3).</param>
    /// <param name="p">The parameter being dragged — the element's
    /// <see cref="SmithElement.ActiveParameter"/>, supplied rather than read, because choosing it is
    /// brief 6's business and this is told.</param>
    /// <param name="zIn">The impedance at the element's INPUT, looking back toward the generator —
    /// node k−1 of <see cref="SmithCascade.Evaluate"/>.</param>
    /// <param name="gammaDrag">Where the user let go, in Γ against <paramref name="z0Chart"/>.</param>
    /// <param name="fHz">The design frequency, HERTZ.</param>
    /// <param name="z0Chart">The chart's own real reference impedance (§3.4), OHMS.</param>
    /// <exception cref="InvalidOperationException">The element has no draggable parameter (S1P and
    /// S2P, whose value is a file), or <paramref name="p"/> is not one of the parameters its kind
    /// exposes. <b>This is the assert the brief asks for</b>: a gripper on a file element is a
    /// programming error, not a refusal a user could act on, and it is spelled as a throw rather
    /// than a <c>Debug.Assert</c> so that it is still visible in a Release build.</exception>
    public static InverseResult Solve(
        SmithDesign d, int elementIndex, SmithParameter p,
        Complex zIn, Complex gammaDrag, double fHz, double z0Chart)
    {
        ArgumentNullException.ThrowIfNull(d);

        if (elementIndex < 0 || elementIndex >= d.Elements.Count)
            throw new ArgumentOutOfRangeException(
                nameof(elementIndex), elementIndex,
                $"The design has {d.Elements.Count} element(s), so there is no element to drag here.");

        var e = d.Elements[elementIndex];

        if (p == SmithParameter.None || SmithComponentMap.UsesFile(e.Kind))
            throw new InvalidOperationException(
                $"'{e.Name}' is a {e.Kind}, whose value is a FILE — it has no draggable parameter "
              + "and no gripper, so asking for its inverse is a programming error rather than "
              + "something the user did.");

        if (!SmithComponentMap.Parameters(e.Kind).Contains(p))
            throw new InvalidOperationException(
                $"'{e.Name}' is a {e.Kind} and does not expose {p} — "
              + $"SmithComponentMap.Parameters says its parameters are "
              + $"{string.Join(", ", SmithComponentMap.Parameters(e.Kind))}.");

        double current = Current(e, p);

        // ── The things no formula below can proceed past ─────────────────────

        if (!(fHz > 0) || !double.IsFinite(fHz))
            return Hold(e, p, current, $"{SmithDesign.Fmt(fHz)} Hz is not a frequency to invert at");

        if (!(z0Chart > 0) || !double.IsFinite(z0Chart))
            return Hold(e, p, current,
                $"the chart's reference impedance is {SmithDesign.Fmt(z0Chart)} Ω, which is not one");

        if (!IsFinite(gammaDrag))
            return Hold(e, p, current, "the drag point is not a finite Γ");

        if (!IsFinite(zIn))
            return Hold(e, p, current,
                "the impedance arriving at this element is not finite, so there is nothing to "
              + "transform");

        // Γ_d = 1 is the open circuit, where Z_d is infinite and no finite component reaches it.
        Complex zd = z0Chart * (Complex.One + gammaDrag) / (Complex.One - gammaDrag);
        if (!IsFinite(zd))
            return Hold(e, p, current,
                "the drag landed on the open circuit, where the impedance is infinite");

        double w = 2.0 * Math.PI * fHz;

        return SmithComponentMap.IsLine(e.Kind)
            ? SolveLine(e, p, zIn, zd, fHz, current)
            : SolveLumped(e, p, zIn, zd, w, current);
    }

    /// <summary>
    /// One parameter's value as the element carries it now — what a pin holds at and what a branch
    /// unwraps toward.
    ///
    /// <para>Public because brief 5 needs the before-value on press for its undo entry and brief 6
    /// needs it for the sliders, and a second copy of this switch is a second place for
    /// <see cref="SmithParameter.ElectricalLength"/> to be read out of the wrong field.</para>
    /// </summary>
    public static double Current(SmithElement element, SmithParameter p)
    {
        ArgumentNullException.ThrowIfNull(element);
        var v = element.Values;

        return p switch
        {
            SmithParameter.R                => v.ROhm,
            SmithParameter.L                => v.LHenry,
            SmithParameter.C                => v.CFarad,
            SmithParameter.Z0               => v.Z0Ohm,
            SmithParameter.ElectricalLength => v.ElectricalLengthDeg,
            SmithParameter.ImpedanceReal    => v.ImpedanceOhm.Real,
            SmithParameter.ImpedanceImag    => v.ImpedanceOhm.Imaginary,
            _ => throw new ArgumentOutOfRangeException(nameof(p), p, "Not a settable parameter."),
        };
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  The lumped kinds — R, L, C, SRLC, PRLC, Z1P
    // ═════════════════════════════════════════════════════════════════════════

    private static InverseResult SolveLumped(
        SmithElement e, SmithParameter p, Complex zIn, Complex zd, double w, double current)
    {
        bool shunt = e.Placement == SmithPlacement.Shunt;

        // The required element immittance, EXACTLY, in the placement's own additive form: a series
        // element adds to Z, a shunt one adds to Y. Nothing is projected yet.
        Complex additive = shunt
            ? Complex.One / zd - Complex.One / zIn
            : zd - zIn;

        if (!IsFinite(additive))
            return Hold(e, p, current,
                "the drag and the impedance arriving here do not differ by a finite immittance");

        // Which plane the projection happens in — see this file's header.
        bool wantAdmittance = e.Kind switch
        {
            SmithElementKind.Prlc                        => true,
            SmithElementKind.Srlc or SmithElementKind.Z1P => false,
            _                                            => shunt,
        };

        Complex a = wantAdmittance == shunt ? additive : Complex.One / additive;
        if (!IsFinite(a))
            return Hold(e, p, current,
                "the drag asks this element for an immittance with no finite "
              + (wantAdmittance ? "admittance" : "impedance"));

        var v = e.Values;

        return (e.Kind, p) switch
        {
            // ── R ────────────────────────────────────────────────────────────
            // Series: Z_e = R, so R is the real part. Shunt: Y_e = 1/R, so the real part is the
            // CONDUCTANCE and R is its reciprocal — a demand for negative conductance is beyond
            // what R = ∞ gives, which is why this pins at the current value and not at zero.
            (SmithElementKind.R, SmithParameter.R) => shunt
                ? Reciprocal(e, p, a.Real, current)
                : Linear(e, p, a.Real, current),

            // ── L ────────────────────────────────────────────────────────────
            // Series: Z_e = jωL. Shunt: Y_e = −j/(ωL), so the reachable susceptance is strictly
            // negative and reaches zero only as L → ∞.
            (SmithElementKind.L, SmithParameter.L) => shunt
                ? Reciprocal(e, p, -w * a.Imaginary, current)
                : Linear(e, p, a.Imaginary / w, current),

            // ── C ────────────────────────────────────────────────────────────
            // Series: Z_e = −j/(ωC), the dual of the shunt L. Shunt: Y_e = jωC.
            (SmithElementKind.C, SmithParameter.C) => shunt
                ? Linear(e, p, a.Imaginary / w, current)
                : Reciprocal(e, p, -w * a.Imaginary, current),

            // ── SRLC, in Z: Z_e = R + j(ωL − 1/(ωC)) ─────────────────────────
            (SmithElementKind.Srlc, SmithParameter.R) => Linear(e, p, a.Real, current),
            (SmithElementKind.Srlc, SmithParameter.L) =>
                Linear(e, p, (a.Imaginary + 1.0 / (w * v.CFarad)) / w, current),
            // X → ωL as C → ∞, so a demand at or past ωL has no finite capacitance behind it.
            (SmithElementKind.Srlc, SmithParameter.C) =>
                Reciprocal(e, p, w * (w * v.LHenry - a.Imaginary), current),

            // ── PRLC, in Y: Y_e = 1/R + j(ωC − 1/(ωL)) — the duals of the three above ─
            (SmithElementKind.Prlc, SmithParameter.R) => Reciprocal(e, p, a.Real, current),
            (SmithElementKind.Prlc, SmithParameter.C) =>
                Linear(e, p, (a.Imaginary + 1.0 / (w * v.LHenry)) / w, current),
            (SmithElementKind.Prlc, SmithParameter.L) =>
                Reciprocal(e, p, w * (w * v.CFarad - a.Imaginary), current),

            // ── Z1P, in Z, and NEITHER part is bounded ───────────────────────
            // A negative real part is an active one-port, which §4.2 draws rather than hides: it
            // leaves the unit circle, and clamping it would be a lie about a stability result.
            (SmithElementKind.Z1P, SmithParameter.ImpedanceReal) => Unbounded(e, p, a.Real, current),
            (SmithElementKind.Z1P, SmithParameter.ImpedanceImag) => Unbounded(e, p, a.Imaginary, current),

            _ => throw new InvalidOperationException(
                $"'{e.Name}' is a {e.Kind} and {p} has no inverse written for it."),
        };
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  The three line kinds — TLIN, and the same line as an open or a shorted stub
    // ═════════════════════════════════════════════════════════════════════════

    private static InverseResult SolveLine(
        SmithElement e, SmithParameter p, Complex zIn, Complex zd, double fHz, double current)
    {
        var    v    = e.Values;
        double fRef = v.ReferenceFrequencyHz;

        // SmithCascade.ThetaRadians REFUSES this, and rightly — but a refusal thrown out of a drag
        // frame is not something a pointer handler can do anything with, so it is a pin here.
        if (!(fRef > 0) || !double.IsFinite(fRef))
            return Hold(e, p, current,
                $"its length is quoted at {SmithDesign.Fmt(fRef)} Hz, and the length scales as "
              + "f/F_ref, so F_ref has to be a positive frequency");

        double theta = Math.PI / 180.0 * v.ElectricalLengthDeg * fHz / fRef;
        double z0L   = v.Z0Ohm;

        return (e.Kind, p) switch
        {
            (SmithElementKind.Tline, SmithParameter.ElectricalLength)
                => TlineLength(e, p, zIn, zd, z0L, fHz, fRef, current),

            (SmithElementKind.Tline, SmithParameter.Z0)
                => TlineZ0(e, p, zIn, zd, theta, current),

            (_, SmithParameter.ElectricalLength)
                => StubLength(e, p, zIn, zd, z0L, fHz, fRef, current),

            (_, SmithParameter.Z0)
                => StubZ0(e, p, zIn, zd, theta, current),

            _ => throw new InvalidOperationException(
                $"'{e.Name}' is a {e.Kind} and {p} has no inverse written for it."),
        };
    }

    /// <summary>
    /// A line's electrical length, from the ANGLE the drag asks the rotation to turn through.
    ///
    /// <para>Against the LINE's own Z₀ the transformation is a pure rotation, Γ' = Γ·e^(−2jθ), so
    /// the reachable set is the circle |Γ| = |Γ_in| and the projection onto it is simply reading the
    /// drag's angle — the radial component of the drag is the part no length can reach.</para>
    /// </summary>
    private static InverseResult TlineLength(
        SmithElement e, SmithParameter p,
        Complex zIn, Complex zd, double z0L, double fHz, double fRef, double current)
    {
        Complex gIn = (zIn - z0L) / (zIn + z0L);
        Complex gD  = (zd  - z0L) / (zd  + z0L);

        if (!IsFinite(gIn) || !IsFinite(gD))
            return Hold(e, p, current, "the rotation has no finite Γ to turn");

        // A rotation about a point that IS the centre is inert, and a drag TO the centre has no
        // angle to read. Both are real documents — a line fed at exactly its own Z₀ is the matched
        // case — and neither is an error.
        if (gIn.Magnitude < GammaIsCentre)
            return Hold(e, p, current,
                $"what arrives here is already the line's own {SmithDesign.Fmt(z0L)} Ω, so no "
              + "length changes anything");

        if (gD.Magnitude < GammaIsCentre)
            return Hold(e, p, current,
                $"the drag landed on the line's own {SmithDesign.Fmt(z0L)} Ω, where the rotation "
              + "has no angle to read");

        // Γ_d = Γ_in·e^(−2jθ)  ⇒  θ = (arg Γ_in − arg Γ_d)/2, modulo π.
        double theta = 0.5 * (gIn.Phase - gD.Phase);

        return Length(e, p, theta, fHz, fRef, current);
    }

    /// <summary>
    /// A stub's electrical length, from the SUSCEPTANCE the drag asks it for.
    ///
    /// <para>A stub is a shunt element, so B_req = Im(Y_d − Y_in) and §4.3's two one-liners follow
    /// from Y = j·tanθ/Z₀ (open) and Y = −j·cot θ/Z₀ (shorted).</para>
    /// </summary>
    private static InverseResult StubLength(
        SmithElement e, SmithParameter p,
        Complex zIn, Complex zd, double z0L, double fHz, double fRef, double current)
    {
        if (!RequiredSusceptance(zIn, zd, out double b))
            return Hold(e, p, current, "the drag asks this stub for no finite susceptance");

        // atan of ±∞ is ±π/2, which is the QUARTER WAVE — the shorted stub's own zero-susceptance
        // point — so neither branch needs a special case here. Unwrapping is what keeps it honest.
        double tan = e.Kind == SmithElementKind.StubOpen
            ? z0L * b
            : -1.0 / (z0L * b);

        return Length(e, p, Math.Atan(tan), fHz, fRef, current);
    }

    /// <summary>
    /// θ → E, <b>unwrapped into the branch the element's current length is in</b> (R-smith3-3).
    ///
    /// <para><b>This is the single most likely defect in this brief and the reason it is one
    /// function.</b> Both length inverses above read an angle modulo π — an <c>atan</c>, or the
    /// difference of two <c>arg</c>s — so a naive reading JUMPS A HALF-TURN the moment the drag
    /// crosses a quarter wave, and the gripper leaps to the far side of the chart under a hand that
    /// moved two pixels. Adding k·180° with k chosen to minimise the change from the current length
    /// is the whole fix, and it is the same continuity problem the trajectory's pole crossing has,
    /// seen from the inverse side.</para>
    ///
    /// <para>The period is 180°·F_ref/f and NOT 180°, because E is quoted at the element's own
    /// reference frequency while the rotation happens at f (§3.3).</para>
    /// </summary>
    private static InverseResult Length(
        SmithElement e, SmithParameter p, double thetaRad, double fHz, double fRef, double current)
    {
        double scale     = 180.0 / Math.PI * fRef / fHz;   // radians of θ → degrees of E
        double principal = thetaRad * scale;
        double period    = 180.0 * fRef / fHz;

        if (!double.IsFinite(principal) || !(period > 0) || !double.IsFinite(period))
            return Hold(e, p, current, "the drag does not resolve to a finite length");

        double k = Math.Round((current - principal) / period);
        if (!double.IsFinite(k))
            return Hold(e, p, current, "the drag does not resolve to a finite length");

        return Linear(e, p, principal + k * period, current);
    }

    /// <summary>
    /// A TLIN's Z₀ — <b>the one inverse that is not a one-liner</b> (R-smith3-2).
    ///
    /// <para>Requiring Z_out = Z_d in the line equation with Z₀ as the unknown gives the complex
    /// quadratic <c>j·tanθ·Z₀² + (Z_k − Z_d)·Z₀ − j·tanθ·Z_k·Z_d = 0</c>. Both roots are computed
    /// directly and the one with positive real part NEAREST the current value wins; its imaginary
    /// part is the component of the drag no real line can reach, and is discarded exactly as every
    /// other case discards its perpendicular.</para>
    /// </summary>
    private static InverseResult TlineZ0(
        SmithElement e, SmithParameter p, Complex zIn, Complex zd, double theta, double current)
    {
        double t = Math.Tan(theta);

        // At tan θ = 0 the quadratic degenerates and the drag is INERT, which is correct — a line
        // that is a whole number of half-waves long transforms nothing, whatever its Z₀. Returning
        // the current value pinned is the honest answer; dividing by the zero is not.
        if (!double.IsFinite(t) || Math.Abs(t) < TanIsZero)
            return Hold(e, p, current,
                $"a line {SmithDesign.Fmt(e.Values.ElectricalLengthDeg)}° long transforms nothing "
              + "at this frequency, so no characteristic impedance changes where the walk lands");

        Complex a    = Complex.ImaginaryOne * t;
        Complex b    = zIn - zd;
        Complex c    = -Complex.ImaginaryOne * t * zIn * zd;
        Complex root = Complex.Sqrt(b * b - 4.0 * a * c);

        Complex r1 = (-b + root) / (2.0 * a);
        Complex r2 = (-b - root) / (2.0 * a);

        Complex? best = null;
        foreach (var r in new[] { r1, r2 })
        {
            if (!IsFinite(r) || !(r.Real > 0)) continue;
            if (best is null || (r - current).Magnitude < (best.Value - current).Magnitude) best = r;
        }

        if (best is null)
            return Hold(e, p, current,
                "neither root of the line equation is a positive characteristic impedance, so no "
              + "real line reaches the drag point");

        return Positive(e, p, best.Value.Real, current);
    }

    /// <summary>
    /// A stub's Z₀, from the susceptance it is asked for — one line each, because a stub ADDS an
    /// admittance rather than transforming an impedance, and there is no quadratic in it.
    /// </summary>
    private static InverseResult StubZ0(
        SmithElement e, SmithParameter p, Complex zIn, Complex zd, double theta, double current)
    {
        if (!RequiredSusceptance(zIn, zd, out double bReq))
            return Hold(e, p, current, "the drag asks this stub for no finite susceptance");

        double t = Math.Tan(theta);
        if (!double.IsFinite(t) || Math.Abs(t) < TanIsZero)
            return Hold(e, p, current,
                $"a stub {SmithDesign.Fmt(e.Values.ElectricalLengthDeg)}° long "
              + (e.Kind == SmithElementKind.StubOpen
                    ? "presents no susceptance at this frequency"
                    : "is a short across the line at this frequency")
              + ", so no characteristic impedance changes where the walk lands");

        // Open:    B = tanθ/Z₀      ⇒  Z₀ = tanθ/B
        // Shorted: B = −1/(Z₀·tanθ) ⇒  Z₀ = −1/(B·tanθ)
        double z0 = e.Kind == SmithElementKind.StubOpen
            ? t / bReq
            : -1.0 / (bReq * t);

        return Positive(e, p, z0, current);
    }

    /// <summary>The susceptance a SHUNT element is being asked for: Im(Y_d − Y_in).</summary>
    private static bool RequiredSusceptance(Complex zIn, Complex zd, out double b)
    {
        Complex y = Complex.One / zd - Complex.One / zIn;
        b = y.Imaginary;
        return double.IsFinite(b);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  The four endings — and which one a parameter takes is the pinning rule
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>A parameter that enters LINEARLY. Its reachable set runs from zero outward, so a
    /// negative demand pins AT ZERO — the boundary the drag overshot.</summary>
    private static InverseResult Linear(SmithElement e, SmithParameter p, double value, double current)
    {
        if (!double.IsFinite(value)) return Hold(e, p, current, "the drag does not resolve to a finite value");

        return value >= 0
            ? new InverseResult(value, false, null)
            : new InverseResult(0.0, true,
                $"'{e.Name}' would need {Label(p)} = {SmithDesign.Fmt(value)} {Unit(p)}, and "
              + $"{Noun(p)} is non-negative — pinned at 0 {Unit(p)}.");
    }

    /// <summary>
    /// A parameter that enters RECIPROCALLY: value = 1/<paramref name="scalar"/>.
    ///
    /// <para>The demand runs out at <paramref name="scalar"/> = 0, which the parameter reaches only
    /// at INFINITY, so there is no representable boundary to pin at and the current value is held
    /// instead. Pinning such a case at zero would turn a near-open shunt R into a dead short under a
    /// two-pixel move, which is the defect this distinction exists to prevent.</para>
    /// </summary>
    private static InverseResult Reciprocal(SmithElement e, SmithParameter p, double scalar, double current)
    {
        if (!double.IsFinite(scalar))
            return Hold(e, p, current, "the drag does not resolve to a finite value");

        if (!(scalar > 0))
            return new InverseResult(current, true,
                $"'{e.Name}' cannot reach there with any {Label(p)} — the drag asks for more than "
              + $"an infinite {Noun(p)} would give, so {Label(p)} is held at "
              + $"{SmithDesign.Fmt(current)} {Unit(p)}.");

        double value = 1.0 / scalar;

        return double.IsFinite(value)
            ? new InverseResult(value, false, null)
            : Hold(e, p, current, "the drag does not resolve to a finite value");
    }

    /// <summary>A parameter that must be strictly POSITIVE — a line's Z₀, whose boundary at zero is
    /// not a line anyone can build, so the current value is what a refused drag holds.</summary>
    private static InverseResult Positive(SmithElement e, SmithParameter p, double value, double current)
        => double.IsFinite(value) && value > 0
            ? new InverseResult(value, false, null)
            : new InverseResult(current, true,
                $"'{e.Name}' would need {Label(p)} = {SmithDesign.Fmt(value)} {Unit(p)}, and a "
              + $"line's characteristic impedance is positive — held at {SmithDesign.Fmt(current)} "
              + $"{Unit(p)}.");

    /// <summary>A parameter with NO physical bound — either part of a Z1P, which §4.2 draws outside
    /// the unit circle rather than clamping.</summary>
    private static InverseResult Unbounded(SmithElement e, SmithParameter p, double value, double current)
        => double.IsFinite(value)
            ? new InverseResult(value, false, null)
            : Hold(e, p, current, "the drag does not resolve to a finite value");

    /// <summary>Hold the current value, and say why. The one ending that is never a computed
    /// number.</summary>
    private static InverseResult Hold(SmithElement e, SmithParameter p, double current, string because)
        => new(current, true,
            $"'{e.Name}' keeps {Label(p)} = {SmithDesign.Fmt(current)} {Unit(p)}: {because}.");

    // ── What the reasons call things ─────────────────────────────────────────

    private static string Label(SmithParameter p) => p switch
    {
        SmithParameter.R                => "R",
        SmithParameter.L                => "L",
        SmithParameter.C                => "C",
        SmithParameter.Z0               => "Z0",
        SmithParameter.ElectricalLength => "E",
        SmithParameter.ImpedanceReal    => "Re(Z)",
        SmithParameter.ImpedanceImag    => "Im(Z)",
        _                               => p.ToString(),
    };

    private static string Unit(SmithParameter p) => p switch
    {
        SmithParameter.R or SmithParameter.Z0
            or SmithParameter.ImpedanceReal or SmithParameter.ImpedanceImag => "Ω",
        SmithParameter.L                                                    => "H",
        SmithParameter.C                                                    => "F",
        SmithParameter.ElectricalLength                                     => "°",
        _                                                                   => "",
    };

    private static string Noun(SmithParameter p) => p switch
    {
        SmithParameter.R                => "a resistance",
        SmithParameter.L                => "an inductance",
        SmithParameter.C                => "a capacitance",
        SmithParameter.Z0               => "a characteristic impedance",
        SmithParameter.ElectricalLength => "an electrical length",
        _                               => "this value",
    };

    private static bool IsFinite(Complex z)
        => double.IsFinite(z.Real) && double.IsFinite(z.Imaginary);
}
