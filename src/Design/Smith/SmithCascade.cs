// The Smith Chart tool's numeric half — the cascade evaluator and every element immittance
// (docs/design/smith-chart.md §3.2, §3.3, §3.5, §3.6, §4.1, §4.2; brief-smith-2-cascade.md).
//
// CLOSED FORM, AND THAT IS THE DESIGN. There is no matrix, no factorisation, no solve and no
// iteration anywhere in this file or its partial. A 12-element network over a 201-point sweep is
// under 2,500 element evaluations, so nothing here caches, warm-starts or schedules a frame — if a
// future element ever makes that untrue, THAT ELEMENT is the thing to reconsider, because an
// interactive Smith chart which has to schedule frames is a different and much worse tool.
//
// THE ENGINE IS THIS FILE'S ORACLE. Every element is a component ComponentTypeRegistry already
// declares, so tests/Ui.Tests/Smith/SmithCascadeTests.cs writes the equivalent `.cnl`, runs the
// ordinary S-parameter analysis and compares S11-as-an-impedance against Evaluate to 1e-9 relative.
// What that catches is CONVENTION — a sign, a port order, a reference impedance, a tan where a cot
// belongs — which is the class of error that produces a plausible picture.
//
// THE WALK IS PROJECTIVE, on purpose. The node state is a (numerator, denominator) PAIR rather than
// an impedance, so a quarter-wave open stub (Y → ∞, Z → 0) and the shunt element after it, or a
// quarter-wave line fed from a short (Z → ∞), are ordinary arithmetic instead of a division by
// zero. The textbook immittance forms in `Immittance` still say `tan θ` — they are what §3.3 states
// and what a reader checks against — but nothing in the walk or the trajectories divides by one.

using System.Numerics;
using CircuitRF.Core.Devices;
using NumFlat;
using RfCore;

namespace CircuitRF.Design.Smith;

/// <summary>
/// What a caller wants done with a frequency the generator table does not span.
///
/// <para><b>An explicit argument rather than a bool</b> (R-smith2-5): the swept band is the one
/// caller allowed to clamp — a band is a VIEWING choice where a design frequency is a design INPUT
/// — and the difference has to be visible at the call site rather than buried in a flag nobody
/// reads.</para>
/// </summary>
public enum SmithOutOfBand
{
    /// <summary>Refuse, with the table's span in the sentence. The design frequency's rule.</summary>
    Refuse,

    /// <summary>Clamp to the nearest end of the table. The swept band's rule, and only its.</summary>
    Clamp,
}

/// <summary>
/// One node of the walk: the impedance looking <b>back toward the generator</b>, and which element
/// produced it.
///
/// <para><see cref="ElementIndex"/> is an index into <see cref="SmithDesign.Elements"/>, or −1 for
/// node 0, the generator. It is carried here rather than recomputed because a DISABLED element
/// occupies no node, so the node array and the element list do not line up — and brief 5's grippers
/// and brief 6's selection would otherwise each write that skip out again.</para>
/// </summary>
public readonly record struct SmithNode(Complex Z, int ElementIndex);

/// <summary>
/// One element's immittance, in <b>the form §3.3's own table gives it</b> — an impedance for R, L,
/// SRLC and Z1P, an admittance for C, PRLC and the two stubs.
///
/// <para>The other form is the reciprocal and is derived here, once. Two hand-written forms of the
/// same element is two places for a sign to be wrong.</para>
/// </summary>
public readonly record struct SmithImmittance(Complex Value, bool IsAdmittance)
{
    /// <summary>The impedance form.</summary>
    public Complex Z => IsAdmittance ? Other(Value) : Value;

    /// <summary>The admittance form.</summary>
    public Complex Y => IsAdmittance ? Value : Other(Value);

    /// <summary>
    /// The reciprocal of the form this element states itself in — <b>an INFINITY at zero, never a
    /// NaN</b>.
    /// </summary>
    /// <remarks>
    /// <c>Complex.One / Complex.Zero</c> in .NET is <c>(NaN, NaN)</c>: the division forms <c>0/0</c>
    /// internally whichever branch it takes. A zero here is an ordinary element — a series C of zero
    /// farads is an open, a shunt R of zero ohms is a short — so the answer is the limit and saying
    /// NaN would report the wrong failure.
    ///
    /// <para><b>The WALK does not come through here.</b> <see cref="SmithCascade"/> folds the
    /// reciprocal into its projective pair instead (see its <c>Add</c>), where the same two cases
    /// come out as exact finite pairs rather than as an infinity that has to be carried. This is for
    /// a caller holding one element's immittance on its own, and it is the honest answer for one.</para>
    /// </remarks>
    private static Complex Other(Complex v)
        => v == Complex.Zero
               ? new Complex(double.PositiveInfinity, double.PositiveInfinity)
               : Complex.One / v;
}

/// <summary>
/// The cascade evaluator: §3.2's recurrence, §3.3's immittances, §3.6's generator interpolation, and
/// (in the partial beside this file) §3.5's per-element trajectories.
///
/// <para><b>Nothing here draws and nothing here validates the whole document.</b> A design is
/// evaluated as it stands — a chart has to keep drawing while someone is still typing a name — and
/// the refusals raised here are exactly the ones the arithmetic itself cannot proceed past: a
/// generator frequency outside the table, a file that does not resolve, a file whose port count is
/// not the element's, a line quoting its length at a non-positive reference frequency.</para>
/// </summary>
public static partial class SmithCascade
{
    // ── The walk ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Every node of §3.2's walk at one frequency: node 0 is the generator, node k is the impedance
    /// looking back toward the generator from the output of the k-th <b>enabled</b> element, and the
    /// last is the load.
    ///
    /// <para><b>There is no load element and nothing terminates the cascade</b> — the load is where
    /// you read. The array's length is the number of ENABLED elements plus one; a disabled element
    /// contributes nothing and occupies no node.</para>
    /// </summary>
    /// <param name="design">The document. Not validated as a whole — see the type's own remarks.</param>
    /// <param name="fHz">The frequency, HERTZ.</param>
    /// <param name="documentDirectory">What an element's relative <c>FileRef</c> resolves against —
    /// the folder the <c>.csmith</c> lives in. Null means the process's working directory, which is
    /// what a test or a CLI run with an absolute reference wants.</param>
    /// <param name="outOfBand">What to do with an <paramref name="fHz"/> the generator table does
    /// not span. <see cref="SmithOutOfBand.Refuse"/> unless the caller is a swept band.</param>
    /// <exception cref="InvalidDataException">A refusal naming the element, the file or the span.</exception>
    public static SmithNode[] Evaluate(
        SmithDesign      design,
        double           fHz,
        string?          documentDirectory = null,
        SmithOutOfBand   outOfBand         = SmithOutOfBand.Refuse)
    {
        ArgumentNullException.ThrowIfNull(design);

        var state = new Zp(GeneratorImpedance(design.Generator, fHz, outOfBand), Complex.One);

        var nodes = new List<SmithNode>(design.Elements.Count + 1) { new(state.Value, -1) };

        for (int i = 0; i < design.Elements.Count; i++)
        {
            var e = design.Elements[i];
            if (!e.Enabled) continue;                       // no contribution, and no node

            state = Step(state, e, fHz, documentDirectory).Normalized();
            nodes.Add(new SmithNode(state.Value, i));
        }

        return [.. nodes];
    }

    /// <summary>
    /// The one step of the recurrence, in projective form.
    ///
    /// <para>The three <c>TLIN</c>-backed kinds and the two file kinds get their own arithmetic;
    /// everything else is §3.2's series sum or shunt parallel over <see cref="Immittance"/>.</para>
    /// </summary>
    private static Zp Step(Zp zIn, SmithElement e, double fHz, string? dir)
    {
        switch (e.Kind)
        {
            // ── The ideal line, in the through path ──────────────────────────
            // Z' = Z0·(Z + jZ0·tanθ)/(Z0 + jZ·tanθ), written over cos/sin so θ = 90° is ordinary
            // arithmetic rather than a pole times a zero.
            case SmithElementKind.Tline:
            {
                double z0 = e.Values.Z0Ohm;
                double th = ThetaRadians(e, fHz);
                double c = Math.Cos(th), s = Math.Sin(th);
                return new Zp(
                    z0 * (zIn.N * c + Complex.ImaginaryOne * z0 * zIn.D * s),
                    z0 * zIn.D * c + Complex.ImaginaryOne * zIn.N * s);
            }

            // ── The same line as a stub, far end open ────────────────────────
            // Y_stub = j·sinθ/(Z0·cosθ). Z' = N·Z0·cosθ / (D·Z0·cosθ + j·N·sinθ).
            case SmithElementKind.StubOpen:
            {
                double z0 = e.Values.Z0Ohm;
                double th = ThetaRadians(e, fHz);
                double c = Math.Cos(th), s = Math.Sin(th);
                return new Zp(
                    zIn.N * z0 * c,
                    zIn.D * z0 * c + Complex.ImaginaryOne * zIn.N * s);
            }

            // ── …far end grounded ────────────────────────────────────────────
            // Y_stub = cosθ/(j·Z0·sinθ). Z' = N·j·Z0·sinθ / (D·j·Z0·sinθ + N·cosθ).
            case SmithElementKind.StubShorted:
            {
                double z0 = e.Values.Z0Ohm;
                double th = ThetaRadians(e, fHz);
                double c = Math.Cos(th), s = Math.Sin(th);
                var jz0s = Complex.ImaginaryOne * z0 * s;
                return new Zp(zIn.N * jz0s, zIn.D * jz0s + zIn.N * c);
            }

            // ── The two-port file, port 1 facing the generator ───────────────
            // Z' = Z22 − Z12·Z21/(Z11 + Z), with Z = N/D.
            case SmithElementKind.S2P:
            {
                var z = ZMatrix(e, fHz, dir, ports: 2);
                Complex z11 = z[0, 0], z12 = z[0, 1], z21 = z[1, 0], z22 = z[1, 1];
                Complex den = z11 * zIn.D + zIn.N;
                return new Zp(z22 * den - z12 * z21 * zIn.D, den);
            }

            // ── Everything else is a two-terminal immittance ─────────────────
            default:
            {
                var imm = Immittance(e, fHz, dir);
                return Add(zIn, imm, e.Placement == SmithPlacement.Series);
            }
        }
    }

    /// <summary>
    /// §3.2's series sum or shunt parallel, <b>without ever forming the reciprocal of the form the
    /// element states itself in</b>.
    /// </summary>
    /// <remarks>
    /// <b>This is the projective walk applied to the ELEMENT as well as to the node, and it is what
    /// keeps a zero-valued part from taking the whole chart out.</b> §3.3 gives each kind its
    /// immittance in one form only (an impedance for R, L and Z1P, an admittance for C), so half the
    /// placements need the other one — and <c>1/0</c> in <see cref="Complex"/> is not an infinity, it
    /// is <c>(NaN, NaN)</c>. A series C of zero farads is an OPEN and a shunt R of zero ohms is a
    /// SHORT; both are ordinary, both are typable, and read through <see cref="SmithImmittance.Z"/> or
    /// <see cref="SmithImmittance.Y"/> both used to produce a NaN that reached every node downstream,
    /// the readout, the grippers and the picture — with no refusal, because
    /// <c>SmithElement.Refusal</c> requires R, L and C to be non-NEGATIVE and zero is not negative
    /// (brief-smith-1-document.md §94).
    ///
    /// <para>Written as a pair, the two singular cases are the ordinary arithmetic the rest of this
    /// file already is: <c>Z' = (N·Y_e + D)/(D·Y_e)</c> at <c>Y_e = 0</c> is <c>(D, 0)</c>, which is
    /// Z = ∞ and Γ = +1 exactly; <c>Z' = N·Z_e/(D·Z_e + N)</c> at <c>Z_e = 0</c> is <c>(0, N)</c>,
    /// which is Z = 0 and Γ = −1 exactly.</para>
    /// </remarks>
    private static Zp Add(Zp zIn, SmithImmittance imm, bool series)
    {
        var m = imm.Value;

        // The element states itself in the placement's OWN form: one multiply, no reciprocal.
        if (series != imm.IsAdmittance)
            return series
                ? new Zp(zIn.N + m * zIn.D, zIn.D)                          // Z' = Z + Z_e
                : new Zp(zIn.N, zIn.D + m * zIn.N);                         // Y' = Y + Y_e

        // …and in the other one, where the reciprocal is folded into the pair instead of taken.
        return series
            ? new Zp(zIn.N * m + zIn.D, zIn.D * m)                          // Z' = Z + 1/Y_e
            : new Zp(zIn.N * m, zIn.D * m + zIn.N);                         // Y' = Y + 1/Z_e
    }

    // ── The element immittances (R-smith2-2) ─────────────────────────────────

    /// <summary>
    /// One element's immittance at <paramref name="fHz"/>, in §3.3's own form — see
    /// <see cref="SmithImmittance"/> for why only one of the two is written.
    ///
    /// <para><b>The two-port kinds have none.</b> <see cref="SmithElementKind.Tline"/> and
    /// <see cref="SmithElementKind.S2P"/> TRANSFORM an impedance rather than adding to one, so
    /// asking either for an immittance is a caller error and not a refusal the user can act on.
    /// The two STUBS do have one, and it is what makes them shunt elements.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="e"/> is a two-port kind.</exception>
    /// <exception cref="InvalidDataException">A file element's file does not resolve or is the
    /// wrong port count; a line's reference frequency is not positive.</exception>
    public static SmithImmittance Immittance(SmithElement e, double fHz, string? documentDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(e);

        double w = 2.0 * Math.PI * fHz;
        var v = e.Values;

        switch (e.Kind)
        {
            case SmithElementKind.R:
                return new SmithImmittance(new Complex(v.ROhm, 0.0), IsAdmittance: false);

            case SmithElementKind.L:
                return new SmithImmittance(new Complex(0.0, w * v.LHenry), IsAdmittance: false);

            case SmithElementKind.C:
                return new SmithImmittance(new Complex(0.0, w * v.CFarad), IsAdmittance: true);

            // ── The RLC family: eight kinds, two formulas (owner, 2026-09-20) ────────────
            //
            // SRLC/SRL/SRC/SLC are Z = R + jωL + 1/(jωC) and PRLC/PRL/PRC/PLC are its dual,
            // Y = 1/R + 1/(jωL) + jωC — in each case OVER THE ELEMENTS THE KIND CARRIES, which
            // SmithComponentMap.RlcElementsOf answers from the same enum the engine model reads.
            // Six more arms here would be six more places for a sign to be typed.
            case SmithElementKind.Srlc:
            case SmithElementKind.Srl:
            case SmithElementKind.Src:
            case SmithElementKind.Slc:
            {
                var  rlc  = SmithComponentMap.RlcElementsOf(e.Kind)!.Value;
                bool hasC = rlc.HasFlag(RlcElements.C);

                // C = 0 is the OPEN, whose impedance has no finite value — so it is stated as the
                // admittance it does have, which is zero, and Add() carries it. Writing the
                // impedance instead gives Complex(R, −∞), and the first multiply after that is a
                // NaN in every node downstream. Zero is a legal C (a document refuses a NEGATIVE
                // one), so this is a value the user can type rather than a hypothetical.
                //
                // A member with NO capacitor never reaches it: an SRL is R + jωL at every ω,
                // including zero, and asking whether its absent C is zero is the question that
                // makes 1/(ω·∞) a NaN in the engine model beside this one.
                if (hasC && !(v.CFarad > 0))
                    return new SmithImmittance(Complex.Zero, IsAdmittance: true);

                double r = rlc.HasFlag(RlcElements.R) ? v.ROhm : 0.0;
                double x = (rlc.HasFlag(RlcElements.L) ? w * v.LHenry : 0.0)
                         - (hasC ? 1.0 / (w * v.CFarad) : 0.0);
                return new SmithImmittance(new Complex(r, x), IsAdmittance: false);
            }

            case SmithElementKind.Prlc:
            case SmithElementKind.Prl:
            case SmithElementKind.Prc:
            case SmithElementKind.Plc:
            {
                var  rlc  = SmithComponentMap.RlcElementsOf(e.Kind)!.Value;
                bool hasR = rlc.HasFlag(RlcElements.R);
                bool hasL = rlc.HasFlag(RlcElements.L);

                // The dual of the series case, twice over: R = 0 and L = 0 are both the SHORT,
                // whose admittance has no finite value and whose impedance is zero. Only a member
                // that HAS the element can be shorted by it — a PLC has no R to be zero.
                if ((hasR && !(v.ROhm > 0)) || (hasL && !(v.LHenry > 0)))
                    return new SmithImmittance(Complex.Zero, IsAdmittance: false);

                double g = hasR ? 1.0 / v.ROhm : 0.0;
                double b = (rlc.HasFlag(RlcElements.C) ? w * v.CFarad : 0.0)
                         - (hasL ? 1.0 / (w * v.LHenry) : 0.0);
                return new SmithImmittance(new Complex(g, b), IsAdmittance: true);
            }

            case SmithElementKind.Z1P:
                // A complex constant over frequency — that is the point of it.
                return new SmithImmittance(v.ImpedanceOhm, IsAdmittance: false);

            case SmithElementKind.S1P:
                // Z11 against the FILE's own reference, through RfCore's own conversion rather than
                // a second spelling of Z = Z0(1+S)/(1−S) here.
                return new SmithImmittance(ZMatrix(e, fHz, documentDirectory, ports: 1)[0, 0],
                                           IsAdmittance: false);

            case SmithElementKind.StubOpen:
                // Y = j·tanθ/Z0
                return new SmithImmittance(
                    Complex.ImaginaryOne * Math.Tan(ThetaRadians(e, fHz)) / v.Z0Ohm,
                    IsAdmittance: true);

            case SmithElementKind.StubShorted:
                // Y = 1/(j·Z0·tanθ)
                return new SmithImmittance(
                    Complex.One / (Complex.ImaginaryOne * v.Z0Ohm * Math.Tan(ThetaRadians(e, fHz))),
                    IsAdmittance: true);

            default:
                throw new InvalidOperationException(
                    $"'{e.Name}' is a {e.Kind}, which transforms an impedance rather than adding to "
                  + "one — it has no immittance, and the cascade steps it with its own formula.");
        }
    }

    /// <summary>
    /// True for the two kinds the cascade steps as a two-port rather than as an immittance —
    /// <see cref="SmithElementKind.Tline"/> and <see cref="SmithElementKind.S2P"/>. Both are series
    /// only, which <see cref="SmithComponentMap.AllowedPlacement"/> is what says.
    /// </summary>
    public static bool IsTwoPort(SmithElementKind kind)
        => kind is SmithElementKind.Tline or SmithElementKind.S2P;

    // ── A line's length scales with frequency (R-smith2-3) ───────────────────

    /// <summary>
    /// θ(f) = (π/180)·E·f/F_ref, RADIANS — <c>TLIN</c>'s own semantics, and not negotiable.
    ///
    /// <para>The whole reason the load point is plotted at several frequencies is to watch the
    /// network come apart at the band edges, and a line whose electrical length did not change with
    /// frequency would be the one element in the cascade that never did. F_ref is the ELEMENT's own
    /// <see cref="SmithElementValues.ReferenceFrequencyHz"/> and does not follow the design
    /// frequency.</para>
    /// </summary>
    /// <exception cref="InvalidDataException">F_ref is not a positive frequency.</exception>
    public static double ThetaRadians(SmithElement e, double fHz)
    {
        ArgumentNullException.ThrowIfNull(e);

        double fRef = e.Values.ReferenceFrequencyHz;
        if (!(fRef > 0) || !double.IsFinite(fRef))
            throw new InvalidDataException(
                $"'{e.Name}' quotes its electrical length at {SmithDesign.FmtHz(fRef)} — the length "
              + "scales as f/F_ref, so F_ref has to be a positive frequency.");

        return Math.PI / 180.0 * e.Values.ElectricalLengthDeg * fHz / fRef;
    }

    // ── The generator (R-smith2-5) ───────────────────────────────────────────

    /// <summary>
    /// Z_gen at <paramref name="fHz"/>, <b>linearly interpolated in R and X</b> between the two
    /// bracketing rows.
    ///
    /// <para>A frequency outside the table's span is a REFUSAL whose sentence names the span, never
    /// an extrapolation — except when the table has exactly one row, where one impedance is flat and
    /// every frequency is legal. <paramref name="outOfBand"/> is how the swept band, and only the
    /// swept band, asks to be clamped instead.</para>
    /// </summary>
    /// <exception cref="InvalidDataException">The table is empty, or does not span
    /// <paramref name="fHz"/> and the caller asked to be refused.</exception>
    public static Complex GeneratorImpedance(
        SmithGenerator generator, double fHz, SmithOutOfBand outOfBand = SmithOutOfBand.Refuse)
    {
        ArgumentNullException.ThrowIfNull(generator);

        var rows = generator.Rows;
        if (rows.Count == 0)
            throw new InvalidDataException(
                "The generator table is empty — this tool starts from an impedance, so it needs at "
              + "least one row of one.");

        // One row is one impedance, flat. Every frequency is legal against it, and there is nothing
        // to interpolate between.
        if (rows.Count == 1) return rows[0].Impedance;

        double startHz = rows[0].FrequencyHz;
        double stopHz  = rows[^1].FrequencyHz;

        double f = fHz;
        if (f < startHz || f > stopHz)
        {
            if (outOfBand == SmithOutOfBand.Refuse)
                throw new InvalidDataException(
                    $"{SmithDesign.FmtHz(fHz)} is outside the generator table's span "
                  + $"{SmithDesign.FmtHz(startHz)} to {SmithDesign.FmtHz(stopHz)} — the generator "
                  + "impedance is interpolated between rows, never extrapolated past them.");

            f = Math.Clamp(f, startHz, stopHz);
        }

        // Rows are sorted and their frequencies unique (SmithGenerator.Refusal), so the bracketing
        // pair is the first row at or above f and the one before it.
        int hi = 1;
        while (hi < rows.Count - 1 && rows[hi].FrequencyHz < f) hi++;

        var lo = rows[hi - 1];
        var up = rows[hi];

        double span = up.FrequencyHz - lo.FrequencyHz;
        double t    = span > 0 ? (f - lo.FrequencyHz) / span : 0.0;

        return new Complex(
            lo.ResistanceOhm + t * (up.ResistanceOhm - lo.ResistanceOhm),
            lo.ReactanceOhm  + t * (up.ReactanceOhm  - lo.ReactanceOhm));
    }

    // ── Γ ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The ordinary voltage reflection coefficient against the chart's real Z₀ (§3.4).
    /// </summary>
    /// <remarks>
    /// <b>An INFINITE impedance is the open circuit, Γ = +1, and not a NaN.</b> The walk is
    /// projective and never divides (<see cref="Zp"/>), but a node it hands back as a plain
    /// <see cref="Complex"/> has already been divided once — and a cascade that ends on an open
    /// (a series C of zero farads, an SRLC with no capacitance) legitimately ends there. Γ of it is
    /// the limit, which is exactly what the chart draws and what the readout's VSWR of ∞ is about.
    /// A NaN stays a NaN: it is not an infinity and saying so would be a lie about which of the two
    /// happened.
    /// </remarks>
    public static Complex Gamma(Complex z, double z0Chart)
        => double.IsInfinity(z.Real) || double.IsInfinity(z.Imaginary)
               ? Complex.One
               : (z - z0Chart) / (z + z0Chart);

    // ── Touchstone-backed elements (R-smith2-4) ──────────────────────────────

    /// <summary>
    /// The element's Z-matrix at <paramref name="fHz"/>, from a file <b>fitted once</b>.
    ///
    /// <para>The fit lives in <c>TouchstoneCache</c>, keyed by resolved absolute path, and this
    /// brief adds no second cache: the wrapper returned per call is the thin per-consumer one, and
    /// the splines behind it are built exactly once per file per process. The interpolation settings
    /// are <c>SnpModel</c>'s own defaults — cubic spline, magnitude/phase, interpolated in S — so the
    /// element and the engine oracle read the same numbers out of the same file.</para>
    ///
    /// <para><paramref name="fHz"/> is CLAMPED into the file's stored range before evaluation.
    /// That is what <c>OutOfRangePolicy.WarnClamp</c> — the engine's own default, and therefore the
    /// oracle's — does with it anyway; doing it here as well keeps a drag or a sweep past the end of
    /// a file from emitting one warning per sample.</para>
    /// </summary>
    private static Mat<Complex> ZMatrix(SmithElement e, double fHz, string? dir, int ports)
    {
        string full = ResolveFile(e, dir, ports, out var snp);

        var interp = TouchstoneCache.GetInterpolator(
            full,
            InterpolationMethod.CubicSpline,
            InterpolationFormat.MagPhase,
            MatrixType.S,
            OutOfRangePolicy.WarnClamp);

        double f = Math.Clamp(fHz, interp.MinFrequency, interp.MaxFrequency);

        return RFNetwork.SToZ(interp.Evaluate(f), snp.Z0);
    }

    /// <summary>
    /// What a file element's file IS — the resolved path, its port count and the span it covers.
    /// </summary>
    /// <remarks>
    /// <b>For the window's own reporting only</b> (<c>R-smith6-4</c>: selecting an S1P or an S2P shows
    /// its file reference, its port count and its frequency span, and nothing to drag). It exists here
    /// rather than in the window so that the PATH is resolved by the one rule the evaluator resolves
    /// it by — a panel that answered "no such file" while the chart drew a trajectory, or the other
    /// way round, would be two answers about one reference.
    /// </remarks>
    /// <exception cref="InvalidDataException">The same refusals <see cref="Evaluate"/> raises: no
    /// reference, no file at it, unreadable as Touchstone, or the wrong port count.</exception>
    public static (string FullPath, int Ports, double MinHz, double MaxHz) FileSummary(
        SmithElement e, string? documentDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (!SmithComponentMap.UsesFile(e.Kind))
            throw new InvalidOperationException(
                $"'{e.Name}' is a {e.Kind}, which is not a file element — only S1P and S2P read a file.");

        int ports   = SmithComponentMap.Component(e.Kind).NumPorts;
        string full = ResolveFile(e, documentDirectory, ports, out var snp);

        // An empty file reports a zero span rather than throwing out of a Min() — the caller is a
        // panel describing what it found, and "0 … 0" is a true description of an empty table.
        return snp.IsEmpty
            ? (full, snp.Ports, 0.0, 0.0)
            : (full, snp.Ports, snp.Frequencies.Min(), snp.Frequencies.Max());
    }

    /// <summary>
    /// The element's file, resolved and checked.
    ///
    /// <para><b>A reference that does not resolve is a refusal naming the element AND the file</b>,
    /// never a silently-skipped element — which would draw a perfectly smooth network that is
    /// missing a part. A file whose port count disagrees with the element's kind is the same kind of
    /// refusal, and for the same reason: an <c>.s2p</c> dropped on an S1P is far more likely to be
    /// the wrong file than a request for the corner of its matrix.</para>
    /// </summary>
    private static string ResolveFile(SmithElement e, string? dir, int ports, out SNP snp)
    {
        if (string.IsNullOrWhiteSpace(e.FileRef))
            throw new InvalidDataException(
                $"'{e.Name}' is a {e.Kind} and names no file — a file element IS its file.");

        string raw  = e.FileRef;
        string full = Path.IsPathRooted(raw)
            ? Path.GetFullPath(raw)
            : Path.GetFullPath(Path.Combine(dir ?? Directory.GetCurrentDirectory(), raw));

        if (!File.Exists(full))
            throw new InvalidDataException(
                $"'{e.Name}' names '{raw}', and there is no file at '{full}'. A {e.Kind} IS its "
              + "file, so there is nothing for it to contribute until that reference resolves.");

        try { snp = TouchstoneCache.Get(full); }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"'{e.Name}' names '{raw}', which could not be read as Touchstone: {ex.Message}");
        }

        if (snp.Ports != ports)
            throw new InvalidDataException(
                $"'{e.Name}' is a {e.Kind} and needs a {ports}-port file; '{raw}' is a "
              + $"{snp.Ports}-port.");

        return full;
    }

    // ── The projective node state ────────────────────────────────────────────

    /// <summary>
    /// A node impedance as a NUMERATOR and a DENOMINATOR, so that Z = 0 and Z = ∞ are both ordinary
    /// values and neither the walk nor a trajectory ever divides by zero. See this file's header.
    /// </summary>
    private readonly record struct Zp(Complex N, Complex D)
    {
        /// <summary>
        /// The impedance this pair stands for — <b>an INFINITY where the denominator has vanished,
        /// never a NaN</b>.
        /// </summary>
        /// <remarks>
        /// A vanished denominator is the open circuit, which is an ordinary node of an ordinary
        /// cascade (a series C of zero farads is one). <see cref="Complex"/>'s own division answers
        /// <c>(NaN, NaN)</c> for it — <c>0/0</c> appears inside its algorithm — so the one place the
        /// projective walk has to come back to an ordinary number is the one place that has to say
        /// so explicitly. <see cref="Gamma(Complex, double)"/> takes it back to Γ = +1.
        /// </remarks>
        internal Complex Value
            => D == Complex.Zero && N != Complex.Zero
                   ? new Complex(double.PositiveInfinity, double.PositiveInfinity)
                   : N / D;

        /// <summary>Γ = (N − Z₀·D)/(N + Z₀·D) — the same expression as
        /// <see cref="Gamma(Complex, double)"/>, one division earlier.</summary>
        internal Complex Gamma(double z0) => (N - z0 * D) / (N + z0 * D);

        /// <summary>Scale the pair back to order 1. Nothing about Z changes; it is what keeps a
        /// twelve-element cascade of large impedances from running out of exponent.</summary>
        internal Zp Normalized()
        {
            double m = Math.Max(N.Magnitude, D.Magnitude);
            return m > 0 && double.IsFinite(m) ? new Zp(N / m, D / m) : this;
        }
    }
}
