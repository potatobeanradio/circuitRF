// The per-element trajectories (docs/design/smith-chart.md §3.5, §4.2; brief-smith-2-cascade.md
// R-smith2-6 … R-smith2-9).
//
// THE RULE IS: SCALE THE IMMITTANCE, NOT THE COMPONENT VALUES. For an L, a C or an R that is
// exactly the classical construction and draws exactly the classical curves — a series reactance
// walks a constant-RESISTANCE circle, a shunt susceptance a constant-CONDUCTANCE circle. What it
// buys is no special case for the multi-parameter elements: an SRLC's Z_in + t(R + jX) is a
// straight segment in Z and therefore a circular arc in Γ, one curve, one gripper, no
// discontinuity.
//
// The alternative — scaling the component VALUES — is the reading "from 0 to its value" most
// naturally suggests and it does not survive contact with a capacitor: as C → 0 the reactance
// −1/ωC runs to −∞, so the trajectory leaves the chart at t → 0 and comes back. It is not
// implemented here and must not be offered as an option.
//
// NOTHING IN THIS FILE KNOWS WHAT A CANVAS IS. The sampler takes a world-to-canvas FUNCTION and
// returns points; there is no Plot, no Trace, no Skia and no PlotControl anywhere below the
// firewall.

using System.Numerics;

namespace CircuitRF.Design.Smith;

/// <summary>
/// One enabled element's curve in the Γ plane, from the impedance at its input to the impedance at
/// its output.
/// </summary>
public sealed class SmithTrajectory
{
    /// <summary>Index into <see cref="SmithDesign.Elements"/> — the enabled element this curve
    /// belongs to.</summary>
    public required int ElementIndex { get; init; }

    public required SmithElementKind  Kind      { get; init; }
    public required SmithPlacement    Placement { get; init; }

    /// <summary>The polyline, in Γ against the chart's own Z₀. At least two points.</summary>
    public required IReadOnlyList<Complex> Gamma { get; init; }

    /// <summary>
    /// True for the two file kinds, whose curve is a two-point <b>dashed chord</b> from Γ_in to
    /// Γ_out rather than a walk: an S1P or an S2P has no parameter to sweep, so there is no path
    /// between its ends to tell the truth about.
    /// </summary>
    public required bool IsChord { get; init; }

    /// <summary>
    /// The polyline's arc-length midpoint, in Γ — where brief 5 puts the arrowhead.
    ///
    /// <para><b>Arc length in Γ rather than in t</b>, so the arrow sits where the curve LOOKS
    /// halfway; and in Γ rather than in canvas, so it does not slide along the curve while somebody
    /// zooms.</para>
    /// </summary>
    public required Complex Midpoint { get; init; }

    /// <summary>
    /// The unit direction at <see cref="Midpoint"/>, pointing from input to output —
    /// <see cref="Complex.Zero"/> for a curve of zero length, which has no direction to report and
    /// wants no arrowhead. Two adjacent arcs sharing a gripper are otherwise ambiguous about which
    /// way the walk goes; the arithmetic belongs here and the glyph belongs in brief 5.
    /// </summary>
    public required Complex Tangent { get; init; }
}

public static partial class SmithCascade
{
    /// <summary>
    /// One polyline per ENABLED element, in the Γ plane, at one frequency.
    ///
    /// <para><b>Sampled in t, never in the derived quantity</b> (R-smith2-7). Sampling a stub's
    /// susceptance uniformly would put no points where the curve moves fastest; sampling θ uniformly
    /// is correct everywhere, INCLUDING THROUGH THE POLE — a stub longer than a quarter wave sweeps
    /// its susceptance to ±∞ and that is not a discontinuity on the chart, it is the closed
    /// constant-conductance circle, emitted as ONE polyline.</para>
    ///
    /// <para><b>A negative-real-part impedance is drawn, not clamped</b> (R-smith2-8). An active
    /// S2P, or a Z1P with negative R, puts a node outside the unit circle; clamping to the disc
    /// would be a lie about a stability result, and the chart's own autoscale already enforces a
    /// unit-circle MINIMUM and grows past it when the data asks.</para>
    /// </summary>
    /// <param name="toCanvas">The world-to-canvas map the chord-error test is measured in. Supplied
    /// by the caller and deliberately not defaulted: a tolerance means nothing without knowing what
    /// unit it is in, and the whole point of the adaptive sampler is that a curve is as smooth as
    /// the ZOOM deserves and no smoother.</param>
    /// <param name="tolerance">Chord error, in <paramref name="toCanvas"/>'s units, below which a
    /// segment is not subdivided.</param>
    /// <param name="budget">The most Γ evaluations any ONE element's curve may cost.</param>
    /// <exception cref="InvalidDataException">A refusal naming the element, the file or the span —
    /// the same ones <see cref="Evaluate"/> raises.</exception>
    public static IReadOnlyList<SmithTrajectory> Trajectories(
        SmithDesign                         design,
        double                              fHz,
        Func<Complex, (double X, double Y)> toCanvas,
        double                              tolerance         = 0.25,
        int                                 budget            = 256,
        string?                             documentDirectory = null,
        SmithOutOfBand                      outOfBand         = SmithOutOfBand.Refuse)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(toCanvas);

        double z0 = design.Chart.Z0Ohm;

        var state = new Zp(GeneratorImpedance(design.Generator, fHz, outOfBand), Complex.One);
        var result = new List<SmithTrajectory>();

        for (int i = 0; i < design.Elements.Count; i++)
        {
            var e = design.Elements[i];
            if (!e.Enabled) continue;                       // emits nothing, and occupies no node

            var zIn  = state;
            var zOut = Step(state, e, fHz, documentDirectory).Normalized();

            Complex[] gamma = SmithComponentMap.UsesFile(e.Kind)
                ? [zIn.Gamma(z0), zOut.Gamma(z0)]           // no parameter — a two-point chord
                : Sample(Walk(e, zIn, fHz, documentDirectory), z0, toCanvas, tolerance, budget,
                         Seed(e, fHz));

            var (mid, tangent) = MidpointAndTangent(gamma);

            result.Add(new SmithTrajectory
            {
                ElementIndex = i,
                Kind         = e.Kind,
                Placement    = e.Placement,
                Gamma        = gamma,
                IsChord      = SmithComponentMap.UsesFile(e.Kind),
                Midpoint     = mid,
                Tangent      = tangent,
            });

            state = zOut;
        }

        return result;
    }

    /// <summary>
    /// §3.5's parameter, as a function of t ∈ [0,1], for one element walked from
    /// <paramref name="zIn"/>.
    ///
    /// <para>The immittance is evaluated ONCE, outside the returned closure — a Touchstone-backed
    /// S1P would otherwise resolve and re-read its file at every sample of every frame.</para>
    /// </summary>
    private static Func<double, Zp> Walk(SmithElement e, Zp zIn, double fHz, string? dir)
    {
        switch (e.Kind)
        {
            // θ(t) = t·θ_total, at the LINE's own Z₀. At t = 0 this is Z_in, so the curve starts
            // where the walk arrives.
            case SmithElementKind.Tline:
            {
                double z0 = e.Values.Z0Ohm, total = ThetaRadians(e, fHz);
                return t =>
                {
                    double c = Math.Cos(t * total), s = Math.Sin(t * total);
                    return new Zp(
                        z0 * (zIn.N * c + Complex.ImaginaryOne * z0 * zIn.D * s),
                        z0 * zIn.D * c + Complex.ImaginaryOne * zIn.N * s);
                };
            }

            case SmithElementKind.StubOpen:
            {
                double z0 = e.Values.Z0Ohm, total = ThetaRadians(e, fHz);
                return t =>
                {
                    double c = Math.Cos(t * total), s = Math.Sin(t * total);
                    return new Zp(zIn.N * z0 * c,
                                  zIn.D * z0 * c + Complex.ImaginaryOne * zIn.N * s);
                };
            }

            // NOTE, and it is the one place §3.5's rule and its own sentence disagree: a shorted
            // stub of ZERO length is a dead short, so this curve starts at Γ = −1 rather than at
            // Γ_in. That is what "θ(t) = t·θ_total" means for this kind, and it is the honest
            // picture of growing the stub out of nothing. The arc is the right
            // constant-conductance circle; it reaches Γ_in only when the stub passes a quarter
            // wave, because below that its susceptance never crosses zero. Recorded in
            // src/Design/RESOLVED.md rather than quietly re-parameterised.
            case SmithElementKind.StubShorted:
            {
                double z0 = e.Values.Z0Ohm, total = ThetaRadians(e, fHz);
                return t =>
                {
                    double c = Math.Cos(t * total), s = Math.Sin(t * total);
                    var jz0s = Complex.ImaginaryOne * z0 * s;
                    return new Zp(zIn.N * jz0s, zIn.D * jz0s + zIn.N * c);
                };
            }

            default:
            {
                var     imm    = Immittance(e, fHz, dir);
                bool    series = e.Placement == SmithPlacement.Series;
                Complex m      = imm.Value;

                // The element states itself in the placement's OWN form — one multiply, and t
                // scales the immittance exactly as this file's header says it must.
                if (series != imm.IsAdmittance)
                    return series
                        ? t => new Zp(zIn.N + t * m * zIn.D, zIn.D)          // Z(t) = Z_in + t·Z_e
                        : t => new Zp(zIn.N, zIn.D + t * m * zIn.N);         // Y(t) = Y_in + t·Y_e

                // …and in the other one, where the reciprocal is folded into the pair rather than
                // taken — Step's own rule (see SmithCascade.Add), for its own reason. The t = 0
                // guard is the one thing that is NOT shared with Step: at a singular m both halves
                // of the pair vanish together there, which is 0/0 and not a limit, while the answer
                // is simply Z_in — the element has not been grown yet. For every non-singular m the
                // guard returns what the formula would have anyway.
                return series
                    ? t => t <= 0 ? zIn : new Zp(zIn.N * m + t * zIn.D, zIn.D * m)
                    : t => t <= 0 ? zIn : new Zp(zIn.N * m, zIn.D * m + t * zIn.N);
            }
        }
    }

    /// <summary>
    /// How many UNIFORM samples a curve is seeded with before the adaptive pass begins.
    /// </summary>
    /// <remarks>
    /// <b>Two is right for every kind whose parameter is a straight line, and catastrophically wrong
    /// for the three that are PERIODIC.</b> A lumped element's Γ(t) is the Möbius image of a
    /// straight segment in Z or Y — an arc, walked once, whose ends and midpoint can never coincide
    /// — so a two-point seed always finds a chord error to bisect on. The three line kinds are
    /// periodic in θ: a <c>TLIN</c> rotates Γ by 2θ and a stub's susceptance has period π, so at
    /// E = 360°, 720°, 1080° the samples at t = 0, ½ and 1 are the SAME POINT. Adaptive subdivision
    /// then measures a chord error of zero, stops on its first test, and emits the whole curve as
    /// two coincident points — a line that goes right round the chart drawn as a dot, silently
    /// (measured: E = 360° gave 2 points where E = 180° gave 65).
    ///
    /// <para>It is not an exotic document. A 90° line quoted at 1 GHz is θ = 720° at 8 GHz, which is
    /// one edit of the design frequency away from any quarter-wave stub.</para>
    ///
    /// <para>A segment of θ ≤ 45° cannot close a period of π, so the seed resolves every one of them
    /// and the adaptive pass then does what it always did. The cap is the budget's, because a seed
    /// that spent it would leave nothing to bisect with.</para>
    /// </remarks>
    private static int Seed(SmithElement e, double fHz)
    {
        if (!SmithComponentMap.IsLine(e.Kind)) return 2;

        double theta = Math.Abs(ThetaRadians(e, fHz));
        if (!double.IsFinite(theta)) return 2;

        return (int)Math.Clamp(Math.Ceiling(theta / (Math.PI / 4.0)) + 1.0, 2.0, 65.0);
    }

    /// <summary>
    /// Adaptive subdivision on chord error in <paramref name="toCanvas"/>'s space, with a fixed
    /// evaluation budget: always split the worst segment first, and stop when the worst is under
    /// tolerance or the budget is gone.
    /// </summary>
    /// <param name="seed">How many uniform samples to lay down before bisecting — see
    /// <see cref="Seed"/>, which is the only thing that knows why it is ever more than two.</param>
    private static Complex[] Sample(
        Func<double, Zp>                    at,
        double                              z0,
        Func<Complex, (double X, double Y)> toCanvas,
        double                              tolerance,
        int                                 budget,
        int                                 seed = 2)
    {
        budget = Math.Max(budget, 3);

        Complex G(double t) => at(t).Gamma(z0);

        int n = Math.Clamp(seed, 2, budget - 1);

        var samples = new SortedList<double, Complex>(budget);
        for (int i = 0; i < n; i++) samples[(double)i / (n - 1)] = G((double)i / (n - 1));
        int used = n;

        // Priority is −error, so the queue's head is the WORST segment.
        var queue = new PriorityQueue<(double T0, double T1, double Tm, Complex A, Complex B, Complex M), double>();

        void Offer(double t0, double t1, Complex a, Complex b)
        {
            // A t-interval this narrow cannot be resolved further in double, and a curve that still
            // fails the chord test there is one the budget is better spent elsewhere on.
            if (used >= budget || t1 - t0 < 1e-9) return;

            double tm = 0.5 * (t0 + t1);
            Complex m = G(tm);
            used++;

            queue.Enqueue((t0, t1, tm, a, b, m), -ChordError(toCanvas(m), toCanvas(a), toCanvas(b)));
        }

        for (int i = 1; i < n; i++)
        {
            double t0 = (double)(i - 1) / (n - 1), t1 = (double)i / (n - 1);
            Offer(t0, t1, samples[t0], samples[t1]);
        }

        while (queue.TryPeek(out _, out double negErr) && -negErr > tolerance && used < budget)
        {
            var seg = queue.Dequeue();
            samples[seg.Tm] = seg.M;

            Offer(seg.T0, seg.Tm, seg.A, seg.M);
            Offer(seg.Tm, seg.T1, seg.M, seg.B);
        }

        return [.. samples.Values];
    }

    /// <summary>Perpendicular distance from <paramref name="p"/> to the segment a–b. A degenerate
    /// segment — which is what a closed curve's own endpoints are — falls back to the distance from
    /// its ends, which is exactly what makes a full circle subdivide instead of collapsing.</summary>
    private static double ChordError((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;

        if (len2 <= 0) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));

        double t  = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0.0, 1.0);
        double qx = a.X + t * dx - p.X;
        double qy = a.Y + t * dy - p.Y;
        return Math.Sqrt(qx * qx + qy * qy);
    }

    /// <summary>R-smith2-9: the arc-length midpoint and the unit direction there, input → output.</summary>
    private static (Complex Midpoint, Complex Tangent) MidpointAndTangent(IReadOnlyList<Complex> gamma)
    {
        double total = 0.0;
        for (int i = 1; i < gamma.Count; i++) total += (gamma[i] - gamma[i - 1]).Magnitude;

        if (!(total > 0) || !double.IsFinite(total)) return (gamma[0], Complex.Zero);

        double half = 0.5 * total, walked = 0.0;
        for (int i = 1; i < gamma.Count; i++)
        {
            double seg = (gamma[i] - gamma[i - 1]).Magnitude;
            if (seg <= 0) continue;

            if (walked + seg >= half)
            {
                double t = (half - walked) / seg;
                var    d = gamma[i] - gamma[i - 1];
                return (gamma[i - 1] + t * d, d / d.Magnitude);
            }

            walked += seg;
        }

        var last = gamma[^1] - gamma[^2];
        return (gamma[^1], last.Magnitude > 0 ? last / last.Magnitude : Complex.Zero);
    }
}
