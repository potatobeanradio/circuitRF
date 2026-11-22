using System.Numerics;
using CircuitRF.Core.Matching;

namespace CircuitRF.Core.Systems;

/// <summary>
/// The ladders with finite transmission zeros — elliptic and inverse Chebyshev — by zero shifting
/// on the prototype's own input function, EVALUATED AT POINTS.
/// </summary>
/// <remarks>
/// <para><b>The procedure is the textbook one-pass zero shifting.</b> At each finite zero
/// <c>jω_k</c>, in a fixed order along the ladder: a partial removal at infinity (a series L, or a
/// shunt C) that leaves the remainder zero at <c>jω_k</c>, then the full removal of that pole of
/// the reciprocal as a resonator (a series LC to ground, or a parallel LC in the through path).
/// What is left after the last finite zero is one or two elements and the load.</para>
///
/// <para><b>Nothing is expanded into coefficients and nothing is divided.</b> The input function
/// <c>f = (1 + S11)/(1 − S11)</c> (an impedance for a series-first ladder, an admittance for the
/// shunt-first dual) is evaluated with its derivative from the products in
/// <see cref="FilterRoots"/>, and every removal so far is applied to that value as a step. A
/// doubly-terminated lossless ladder delivers no power at a transmission zero, so <c>f(jω_k)</c> is
/// purely reactive there and the partial element is its imaginary part over <c>ω_k</c>; the
/// residue of the next pole is <c>1/f'(jω_k)</c>, read from the derivative carried alongside. The
/// previous version divided expanded polynomials in double with a 1e-3 remainder tolerance and
/// then searched every removal order; this one has no tolerance to accept a wrong step with,
/// because nothing is accepted until §verification says the ladder IS the prototype.</para>
///
/// <para><b>The order of the zeros is the only freedom</b>, and positivity depends on it. The
/// lowest zero (nearest the passband) goes in the centre section with the rest alternating outward
/// — the order that realises every elliptic in the reference table but a handful of
/// narrow-transition ones — then ascending, then descending, then, for up to six zeros, every
/// permutation in lexicographic order. The first all-positive ladder that verifies is the answer.
/// <c>testdata/filter-elliptic</c> holds an independent 120-digit reference of the same rule.</para>
/// </remarks>
public static partial class FilterLadder
{
    /// <summary>The largest difference in S21 between a ladder and its prototype that is accepted.</summary>
    internal const double VerifyTolerance = 1e-8;

    private enum StepKind { SubS, Invert, SubRes }

    private readonly record struct Step(StepKind Kind, Dd A, Dd W2);

    /// <summary>A normalised zero ladder, or why there is none — what the cache holds.</summary>
    private sealed record ZeroLadder(List<NormArm>? Arms, double Load, bool ShuntFirst, string? Refusal);

    /// <summary>
    /// Normalised ladders by specification. The synthesis is independent of the form, the
    /// frequencies and the impedance level, which are what a user changes most, so those edits
    /// only re-denormalise.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (FilterResponse, int, double, double, EllipticType, bool), ZeroLadder> ZeroCache = new();

    /// <summary>
    /// The ladder of a family with zeros on the axis, from the cache or by building its prototype
    /// and synthesising it. Null for the all-pole families.
    /// </summary>
    private static FilterLadderResult? SynthesizeWithZeros(
        FilterResponse response, int order, double rippleDb, double astopDb, EllipticType type,
        NetworkForm form, double fcHz, double f1Hz, double f2Hz,
        double sourceOhms, FilterFirstElement first)
    {
        if (response is not (FilterResponse.Elliptic or FilterResponse.InvChebyshev)) return null;
        if (!Cutoff(form, fcHz, f1Hz, f2Hz, out double wEdge, out double w0, out double bw, out string? why))
            return Refuse(why!);

        bool wantShunt = first == FilterFirstElement.Parallel;
        type = response == FilterResponse.Elliptic ? EllipticTypes.Resolve(type, order) : EllipticType.A;
        var key = (response, order, response == FilterResponse.Elliptic ? rippleDb : 0.0, astopDb,
                   type, wantShunt);
        if (!ZeroCache.TryGetValue(key, out var z))
        {
            try
            {
                var proto = FilterPrototype.Create(response, order, rippleDb, astopDb, type);
                z = NormalisedZeroLadder(proto, wantShunt);
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException)
            {
                return Refuse(e.Message);
            }
            if (ZeroCache.Count > 256) ZeroCache.Clear();
            ZeroCache[key] = z;
        }
        if (z.Arms is null) return Refuse(z.Refusal!);

        var elements = new List<FilterElement>();
        var placed = new List<FilterArm>();
        string node = "p1";
        var ids = new Ids();
        foreach (var arm in z.Arms)
        {
            int from = elements.Count;
            node = PlaceNorm(elements, arm, form, sourceOhms, wEdge, w0, bw, node, ref ids);
            placed.Add(new FilterArm(ArmKindOf(arm.Kind), [.. Enumerable.Range(from, elements.Count - from)]));
        }
        double rl = z.Load * sourceOhms;
        string note = z.ShuntFirst == wantShunt ? "" :
            $"No {(wantShunt ? "parallel" : "series")}-first ladder of this response has every " +
            $"element positive; this is the {(z.ShuntFirst ? "parallel" : "series")}-first one.";
        return new FilterLadderResult
        {
            Realization = new FilterRealization
            {
                First = z.ShuntFirst ? FilterFirstElement.Parallel : FilterFirstElement.Series,
                SourceOhms = sourceOhms,
                LoadOhms = rl,
                Elements = elements,
                Arms = placed,
                G = [],
                Note = note,
                Cnl = ToCnl(elements, sourceOhms, rl),
            },
        };
    }

    private static ZeroLadder NormalisedZeroLadder(FilterPrototype proto, bool wantShunt)
    {
        var roots = proto.Roots!;
        if (roots.ZerosAtInfinity == 0)
            return new ZeroLadder(null, 0, wantShunt, proto.Response == FilterResponse.Elliptic
                ? "An even-order elliptic of type a keeps a finite floor at infinite frequency, and a " +
                  "ladder of inductors and capacitors is an open there. Types b and c move that zero " +
                  "to infinity and are ladders; Auto builds type c."
                : $"{proto.Response} of even order keeps a finite floor at infinite frequency, and a " +
                  "ladder of inductors and capacitors is an open there. An odd order is the ladder.");

        double[] zeros = [.. roots.Xz.Select(Math.Sqrt)];
        double worst = double.NaN;
        foreach (bool shuntFirst in (bool[])[wantShunt, !wantShunt])
        {
            foreach (var order in ZeroOrderings(zeros.Length))
            {
                double[] seq = [.. order.Select(i => zeros[i])];
                if (!TryZeroShift(roots, seq, shuntFirst, out var arms, out double load)) continue;
                double err = VerifyError(arms, load, roots);
                if (err <= VerifyTolerance) return new ZeroLadder(arms, load, shuntFirst, null);
                if (!(worst <= err)) worst = err;
            }
        }

        string what = $"{proto.Response} order {proto.Order}";
        return new ZeroLadder(null, 0, wantShunt, double.IsNaN(worst)
            ? $"{what}: no order of its {zeros.Length} transmission zeros gives a ladder with every " +
              "element positive. A deeper stopband, a wider transition or a lower order does."
            : $"{what}: the ladder could not be built to within {VerifyTolerance:G1} of the " +
              $"response in double precision (closest {worst:G3}). A lower order or a wider " +
              "transition does.");
    }

    private static FilterArmKind ArmKindOf(NormKind kind) => kind switch
    {
        NormKind.SeriesL or NormKind.SeriesC => FilterArmKind.Series,
        NormKind.ShuntC or NormKind.ShuntL => FilterArmKind.Shunt,
        NormKind.SeriesParallel => FilterArmKind.SeriesTank,
        _ => FilterArmKind.ShuntStub,
    };

    /// <summary>
    /// The tank orders tried, source end first, as indices into the ascending zeros: centre-out,
    /// ascending, descending, then every other permutation (lexicographic) for up to six zeros.
    /// </summary>
    internal static IEnumerable<int[]> ZeroOrderings(int m)
    {
        double centre = (m - 1) / 2.0;
        int[] pos = [.. Enumerable.Range(0, m).OrderBy(p => Math.Abs(p - centre)).ThenBy(p => p)];
        var centreOut = new int[m];
        for (int zi = 0; zi < m; zi++) centreOut[pos[zi]] = zi;

        var seen = new List<int[]>();
        foreach (var o in new[]
                 {
                     centreOut,
                     Enumerable.Range(0, m).ToArray(),
                     Enumerable.Range(0, m).Reverse().ToArray(),
                 })
        {
            if (seen.Any(x => x.SequenceEqual(o))) continue;
            seen.Add(o);
            yield return o;
        }
        if (m > 6) yield break;

        var perm = Enumerable.Range(0, m).ToArray();
        while (true)
        {
            if (!seen.Any(x => x.SequenceEqual(perm))) yield return [.. perm];
            int i = m - 2;
            while (i >= 0 && perm[i] >= perm[i + 1]) i--;
            if (i < 0) yield break;
            int j = m - 1;
            while (perm[j] <= perm[i]) j--;
            (perm[i], perm[j]) = (perm[j], perm[i]);
            Array.Reverse(perm, i + 1, m - i - 1);
        }
    }

    /// <summary>
    /// One pass of zero shifting in the order <paramref name="seq"/>. False when a removal is not
    /// positive or a residue is not real.
    /// </summary>
    private static bool TryZeroShift(FilterRoots roots, double[] seq, bool shuntFirst,
                                     out List<NormArm> arms, out double load)
    {
        arms = [];
        load = 0;
        var steps = new List<Step>(seq.Length * 4 + 4);
        bool y = shuntFirst;   // the running function is an admittance

        foreach (double w in seq)
        {
            var s = new DdComplex(0.0, w);
            var (v, d) = Remainder(roots, steps, s, derivative: true);
            if (!v.IsFinite || !d.IsFinite) return false;
            Dd part = v.Im / w;
            if (part.Hi < -1e-9) return false;
            if (part.Hi > 1e-9)
            {
                steps.Add(new Step(StepKind.SubS, part, 0.0));
                double pv = part.Hi + part.Lo;
                arms.Add(y ? new NormArm(NormKind.ShuntC, 0, pv) : new NormArm(NormKind.SeriesL, pv, 0));
                d -= new DdComplex(part, 0.0);
            }
            // The reciprocal has a pole at jω with residue 1/d; d is real and positive for a
            // realisable step.
            if (!(d.Re.Hi > 0) || Math.Abs(d.Im.Hi) > 1e-6 * d.Magnitude) return false;
            Dd w2 = (Dd)w * w;
            Dd a = 2.0 / (w2 * d.Re);
            Dd half = d.Re * 0.5;
            steps.Add(new Step(StepKind.Invert, 0.0, 0.0));
            steps.Add(new Step(StepKind.SubRes, a, w2));
            steps.Add(new Step(StepKind.Invert, 0.0, 0.0));
            double av = a.Hi + a.Lo, hv = half.Hi + half.Lo;
            arms.Add(y
                ? new NormArm(NormKind.SeriesParallel, av, hv)
                : new NormArm(NormKind.ShuntSeries, hv, av));
        }

        const double w0 = 1.0;
        switch (roots.ZerosAtInfinity)
        {
            case 1:
            {
                var v = Remainder(roots, steps, new DdComplex(0.0, w0), derivative: false).V.ToComplex();
                double x = v.Imaginary / w0, r = v.Real;
                if (!(x > 0) || !(r > 0) || !double.IsFinite(x + r)) return false;
                arms.Add(y ? new NormArm(NormKind.ShuntC, 0, x) : new NormArm(NormKind.SeriesL, x, 0));
                load = y ? 1.0 / r : r;
                return true;
            }
            case 2:
            {
                // f = s·X1 + 1/(s·X2 + T): the value at DC is 1/T, one more point fixes X1 and X2.
                var at0 = Remainder(roots, steps, DdComplex.Zero, derivative: false).V.Re;
                if (!(at0.Hi > 0) || !double.IsFinite(at0.Hi)) return false;
                var v = Remainder(roots, steps, new DdComplex(0.0, w0), derivative: false).V;
                Dd t = 1.0 / at0;                  // the far termination, in f's dual units
                Dd q = t / v.Re - t * t;
                if (!(v.Re.Hi > 0) || !(q.Hi > 0)) return false;
                Dd x2 = Dd.Sqrt(q) / w0;
                Dd x1 = (v.Im + x2 * w0 / (t * t + x2 * x2 * (w0 * w0))) / w0;
                double d1 = x1.Hi + x1.Lo, d2 = x2.Hi + x2.Lo, tv = t.Hi + t.Lo;
                if (!(d1 > 0) || !(d2 > 0) || !double.IsFinite(d1 + d2)) return false;
                if (y)
                {
                    arms.Add(new NormArm(NormKind.ShuntC, 0, d1));
                    arms.Add(new NormArm(NormKind.SeriesL, d2, 0));
                    load = tv;
                }
                else
                {
                    arms.Add(new NormArm(NormKind.SeriesL, d1, 0));
                    arms.Add(new NormArm(NormKind.ShuntC, 0, d2));
                    load = 1.0 / tv;
                }
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>
    /// The input function with every removal so far applied, and (optionally) its derivative, in
    /// double-double: the removals subtract nearly equal values by construction.
    /// </summary>
    private static (DdComplex V, DdComplex D) Remainder(FilterRoots roots, List<Step> steps,
                                                        DdComplex s, bool derivative)
    {
        var (rho, drho) = roots.Reflection(s, derivative);
        var one = DdComplex.One;
        var om = one - rho;
        var v = (one + rho) / om;
        var d = derivative ? drho * 2.0 / (om * om) : DdComplex.Zero;
        var s2 = s * s;
        foreach (var st in steps)
        {
            switch (st.Kind)
            {
                case StepKind.SubS:
                    v -= s * st.A;
                    d -= new DdComplex(st.A, 0.0);
                    break;
                case StepKind.Invert:
                    d = -d / (v * v);
                    v = one / v;
                    break;
                default:
                    var x = s2 / new DdComplex(st.W2, 0.0);
                    var den = one + x;
                    v -= st.A * s / den;
                    if (derivative) d -= st.A * (one - x) / (den * den);
                    break;
            }
        }
        return (v, d);
    }


    /// <summary>
    /// The largest |S21| difference between the normalised ladder (1 Ω source, <paramref name="load"/>
    /// at the far end) and the prototype, over the passband, the transition, every zero and well
    /// into the stopband.
    /// </summary>
    private static double VerifyError(List<NormArm> arms, double load, FilterRoots roots)
    {
        if (arms.Any(a => a.L < 0 || a.C < 0 || !double.IsFinite(a.L + a.C))) return double.PositiveInfinity;
        double top = 100.0 * Math.Max(double.IsFinite(roots.StopEdge) ? roots.StopEdge : 1.0,
                                      roots.Xz.Length > 0 ? Math.Sqrt(roots.Xz[^1]) : 1.0);
        // A log sweep over the whole response, plus the passband exactly where its ripple is
        // decided: every reflection zero (|S21| = 1) and the midpoint between neighbours (the
        // ripple's floor), and either side of each transmission zero.
        var omegas = new List<double>(200);
        const int N = 61;
        for (int i = 0; i < N; i++) omegas.Add(0.01 * Math.Pow(top / 0.01, i / (N - 1.0)));
        var pass = roots.Xr.Select(Math.Sqrt).Append(0.0).Append(1.0).OrderBy(w => w).ToList();
        for (int i = 0; i < pass.Count; i++)
        {
            omegas.Add(pass[i]);
            if (i + 1 < pass.Count) omegas.Add(0.5 * (pass[i] + pass[i + 1]));
        }
        // Either side of each zero rather than on it: at the resonance itself a tank is an
        // infinite impedance, which the chain can only spell as NaN.
        foreach (double x in roots.Xz)
        {
            omegas.Add(Math.Sqrt(x) * (1 - 1e-6));
            omegas.Add(Math.Sqrt(x) * (1 + 1e-6));
        }
        omegas.RemoveAll(w => !(w > 0));

        double worst = 0;
        foreach (double w in omegas)
        {
            var s = new DdComplex(0.0, w);
            var got = LadderS21(arms, load, s);
            var want = roots.S21(s);
            double e = Math.Min((got - want).Magnitude, (got + want).Magnitude);
            if (!(e <= worst)) worst = e;
            if (double.IsNaN(e)) return double.PositiveInfinity;
        }
        return worst;
    }

    /// <summary>
    /// S21 of the normalised ladder by its ABCD chain, 1 Ω source, in double-double: deep in the
    /// stopband S21 is a cancellation of large chain entries, and a double chain would put its own
    /// rounding into the comparison.
    /// </summary>
    private static DdComplex LadderS21(List<NormArm> arms, double load, DdComplex s)
    {
        DdComplex a = DdComplex.One, b = DdComplex.Zero, c = DdComplex.Zero, d = DdComplex.One;
        var one = DdComplex.One;
        foreach (var arm in arms)
        {
            DdComplex z = DdComplex.Zero, yy = DdComplex.Zero;
            bool series;
            switch (arm.Kind)
            {
                case NormKind.SeriesL: z = s * arm.L; series = true; break;
                case NormKind.SeriesC: z = one / (s * arm.C); series = true; break;
                case NormKind.ShuntC: yy = s * arm.C; series = false; break;
                case NormKind.ShuntL: yy = one / (s * arm.L); series = false; break;
                case NormKind.SeriesParallel: z = s * arm.L / (one + s * s * ((Dd)arm.L * arm.C)); series = true; break;
                default: yy = s * arm.C / (one + s * s * ((Dd)arm.L * arm.C)); series = false; break;
            }
            if (series) { b += a * z; d += c * z; }
            else { a += b * yy; c += d * yy; }
        }
        var rl = new DdComplex(load, 0.0);
        return new DdComplex(Dd.Sqrt(1.0 / (Dd)load) * 2.0, 0.0) / (a + b / rl + c + d / rl);
    }
}
