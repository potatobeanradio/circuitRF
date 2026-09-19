// The constant-Q arc pair, in closed form (docs/design/smith-chart.md §4.4;
// brief-smith-9-q-and-sweep.md R-smith9-1, R-smith9-2).
//
// FRAMEWORK-FREE AND BESIDE THE REST OF THE CLOSED FORMS, so brief 10's headless render draws the
// same arcs the window does rather than a second set that agrees until it does not.

using System;
using System.Numerics;

namespace CircuitRF.Design.Smith;

/// <summary>One branch of the pair, as the circle it is.</summary>
/// <param name="Centre">(0, −1/Q) for the inductive branch, (0, +1/Q) for the capacitive one.</param>
/// <param name="Radius">√(1 + 1/Q²), the same for both.</param>
public readonly record struct SmithQCircle(Complex Centre, double Radius);

/// <summary>
/// A Q drag's answer. <see cref="Q"/> is <b>always finite and positive</b> — a drag that has no Q
/// pins at the value the document already held and says why (<c>R-smith9-2</c>).
/// </summary>
public readonly record struct SmithQResult(double Q, bool Pinned, string? PinReason);

/// <summary>
/// The constant-Q arcs: where they are, and what a drag on one means.
/// </summary>
/// <remarks>
/// <b>The derivation, once.</b> For normalized <c>z = r + jx</c> constant Q means <c>|x| = Q·r</c>.
/// Substituting <c>z = (1+Γ)/(1−Γ)</c> with <c>Γ = u + jv</c>:
///
/// <code>
/// r = (1 − u² − v²) / ((1−u)² + v²)        x = 2v / ((1−u)² + v²)
/// x = Q·r  ⇒  u² + v² + (2/Q)·v − 1 = 0  ⇒  u² + (v + 1/Q)² = 1 + 1/Q²
/// </code>
///
/// <para>So each branch is <b>a true circle</b>, and the common denominator cancels — which is why
/// nothing here divides by it and why <see cref="QAt"/> is two multiplies and a divide rather than a
/// search.</para>
///
/// <para><b>Do not sample the z plane and map across.</b> That is the mistake
/// <c>docs/design/vswr-locus-gamma-plane.md</c> records for the VSWR locus: equal steps in the z
/// parameter become very unequal steps in Γ, and the rendered arc reads as jaggy at exactly the
/// values people use. <see cref="Arc"/> emits <c>centre + radius·e^{jθ}</c> at uniform θ, so every
/// chord around the arc is the same length by construction.</para>
///
/// <para><b>Q → ∞ degenerates to the unit circle and Q → 0 to the real axis; both come out of the
/// formula and neither is a special case.</b> The document's own refusal keeps Q finite and
/// positive, so neither limit is ever reached with a circle to draw.</para>
/// </remarks>
public static class SmithQArcs
{
    /// <summary>
    /// How many points one arc is emitted as.
    /// </summary>
    /// <remarks>
    /// <b>A constant rather than an adaptive count, unlike the trajectories.</b> A trajectory is an
    /// arbitrary curve whose chord error has to be measured against the canvas; this is a circle of
    /// radius ≥ 1 in a plane whose interesting region is the unit disc, so a fixed 240 chords is
    /// under a quarter of a pixel at any zoom anyone drags at and costs two sines per point.
    /// </remarks>
    public const int DefaultSamples = 241;

    /// <summary>The shift-modified step (<c>R-smith9-2</c>), applied to the computed Q
    /// <b>before it is stored</b>.</summary>
    public const double QuarterStep = 0.25;

    // ── the circles ──────────────────────────────────────────────────────────

    /// <summary>
    /// The branch's circle. <paramref name="inductive"/> is the <c>x &gt; 0</c> half of the chart.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Q is not finite and positive — which
    /// <see cref="SmithDesign.Refusal"/> has already said in a sentence by the time anything asks to
    /// draw one.</exception>
    public static SmithQCircle Circle(double q, bool inductive)
    {
        Require(q);

        double c = 1.0 / q;
        return new SmithQCircle(new Complex(0.0, inductive ? -c : c), Math.Sqrt(1.0 + c * c));
    }

    /// <summary>
    /// The branch's arc <b>inside the unit disc</b>, at uniform θ, from Γ = +1 to Γ = −1.
    /// </summary>
    /// <remarks>
    /// <b>The in-disc range is closed form, and it has to be emitted rather than clipped.</b> On the
    /// inductive circle <c>|Γ|² = 1 − (2/Q)·v</c>, so the arc is inside the disc exactly where
    /// <c>v &gt; 0</c> — which is <c>θ ∈ [atan(1/Q), π − atan(1/Q)]</c>, with both ends landing on
    /// Γ = ±1 exactly. The design note expected the renderer's disc clip to do this; a Smith
    /// <c>Plot</c> clips its traces to the PLOT BOX and not to the disc, deliberately (a node outside
    /// the unit circle is drawn, because clamping would be a lie about a stability result), so the
    /// other half of each circle would otherwise have been painted across the chart.
    ///
    /// <para><b>The capacitive branch is the inductive one CONJUGATED</b>, not computed again: the
    /// two are mirror images about the real axis and deriving them separately is a second chance for
    /// them to stop being exactly that.</para>
    ///
    /// <para><b>The two endpoints are the one place |x|/r is not Q.</b> At Γ = ±1 the load is the
    /// open and the short, where r and x both vanish or diverge together; the ratio's LIMIT is Q and
    /// its value is 0/0. They are on the arc because the branches pass through them, and every
    /// interior point carries the invariant.</para>
    /// </remarks>
    public static Complex[] Arc(double q, bool inductive, int samples = DefaultSamples)
    {
        Require(q);
        if (samples < 2)
            throw new ArgumentOutOfRangeException(nameof(samples), samples,
                "Two points is the fewest that is an arc rather than a dot.");

        double c  = 1.0 / q;
        double r  = Math.Sqrt(1.0 + c * c);
        double t0 = Math.Atan(c);                    // sin t0 = 1/√(1+Q²)  ⇒  v = 0, u = +1
        double t1 = Math.PI - t0;                    //                        v = 0, u = −1

        var points = new Complex[samples];
        for (int i = 0; i < samples; i++)
        {
            double t = t0 + (t1 - t0) * i / (samples - 1);
            points[i] = new Complex(r * Math.Cos(t), r * Math.Sin(t) - c);
        }

        // The ends ARE Γ = ±1 — the cosine above lands on ±1 to within a rounding step, and the
        // claim "both branches pass exactly through Γ = ±1" is worth being exactly true.
        points[0]  =  Complex.One;
        points[^1] = -Complex.One;

        if (!inductive)
            for (int i = 0; i < samples; i++) points[i] = Complex.Conjugate(points[i]);

        return points;
    }

    // ── the drag (R-smith9-2) ────────────────────────────────────────────────

    /// <summary>
    /// The Q of a point, <c>|x|/r</c>, <b>without ever forming z</b> — or null where there is no
    /// finite positive one.
    /// </summary>
    /// <remarks>
    /// <c>|x|/r = |2v| / (1 − u² − v²)</c>: the <c>(1−u)² + v²</c> both halves carry cancels, so the
    /// denominator's sign IS the sign of r and a point on or outside the unit circle — a load with
    /// R ≤ 0 — falls out as "no Q" rather than as a negative one.
    /// </remarks>
    public static double? QAt(Complex gamma)
    {
        double u = gamma.Real, v = gamma.Imaginary;
        if (!double.IsFinite(u) || !double.IsFinite(v)) return null;

        double denominator = 1.0 - u * u - v * v;    // r · ((1−u)² + v²)
        if (!(denominator > 0)) return null;

        double q = Math.Abs(2.0 * v) / denominator;
        return double.IsFinite(q) && q > 0 ? q : null;
    }

    /// <summary>
    /// Where a drag to <paramref name="gammaDrag"/> puts Q — closed form, no search.
    /// </summary>
    /// <param name="currentQ">What the document holds now, which is what a pinned drag keeps.</param>
    /// <param name="gammaDrag">Where the pointer is, in Γ.</param>
    /// <param name="quarterStep">Shift is held. <b>The rounding is applied to the computed Q before
    /// it is stored</b>, so a shift-drag lands on an exact quarter and a later un-shifted drag starts
    /// from that exact quarter rather than from a rounded display of something else.</param>
    /// <remarks>
    /// <b>Dragging either branch moves both</b>, because they are one setting — which is why this
    /// takes no branch argument: |x| is what Q is made of, and the two branches are the two signs of
    /// the same number.
    ///
    /// <para><b>A pin returns the value the document already had</b> and a sentence, never an
    /// infinity and never a NaN. Two drags have no Q: one on or outside the unit circle, where R ≤ 0
    /// and the tool is outside the passive region altogether; and one on the real axis, where the
    /// load is purely resistive and Q is zero — a Q of zero is the real axis itself rather than a
    /// pair of arcs, and it is what <see cref="SmithDesign.Refusal"/> refuses to store.</para>
    /// </remarks>
    public static SmithQResult Solve(double currentQ, Complex gammaDrag, bool quarterStep)
    {
        double held = double.IsFinite(currentQ) && currentQ > 0 ? currentQ : 1.0;

        if (!double.IsFinite(gammaDrag.Real) || !double.IsFinite(gammaDrag.Imaginary))
            return Hold(held, "the drag point is not a finite Γ");

        if (QAt(gammaDrag) is not { } q)
            return Hold(held,
                1.0 - gammaDrag.Real * gammaDrag.Real
                    - gammaDrag.Imaginary * gammaDrag.Imaginary > 0
                    ? "a purely resistive load has no Q to speak of — |x|/r is zero there, and a Q "
                    + "of zero is the real axis itself rather than a pair of arcs"
                    : "a Γ on or outside the unit circle is a load with R ≤ 0, which is outside the "
                    + "passive region and has no finite Q");

        if (quarterStep) q = Math.Max(QuarterStep, Math.Round(q / QuarterStep) * QuarterStep);

        return new SmithQResult(q, false, null);
    }

    /// <summary>The pin's sentence — <see cref="SmithInverse"/>'s own shape, so a Q drag that
    /// stopped and a gripper drag that stopped read as the same kind of event.</summary>
    private static SmithQResult Hold(double q, string because)
        => new(q, true, $"The constant-Q arcs keep Q = {SmithDesign.Fmt(q)}: {because}.");

    private static void Require(double q)
    {
        if (!(double.IsFinite(q) && q > 0))
            throw new ArgumentOutOfRangeException(nameof(q), q,
                "Q is |x|/r and has to be a finite positive number — Q → 0 is the real axis and "
              + "Q → ∞ is the unit circle, and neither is a pair of arcs.");
    }
}
