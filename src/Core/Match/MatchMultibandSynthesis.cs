namespace CircuitRF.Core.Matching;

/// <summary>
/// match.md §18.2 and §18.5: the resonated multiband network — <b>a lowpass-form prototype pushed
/// through §4.4's bandpass transformation</b>, so that its passband in <c>Omega</c> lands on two or
/// three frequency bands and its stopbands become the gaps between them.
/// </summary>
/// <remarks>
/// <h3>Two band counts, one code path, one prototype interface</h3>
/// <para><b>Dual and tri differ only in which polynomial Phi is</b>, and everything either side of
/// that is shared. For two bands Phi is <c>T_n(x(u))^2</c> on the single interval <c>[a^2, 1]</c>,
/// written down by arccosine (§18.2). For three it is <c>p(u)^2</c> with p the equiripple polynomial
/// on the UNION <c>[0, a^2] u [b^2, 1]</c>, produced by <see cref="MatchRemez"/>'s exchange (§18.5).
/// Both have <c>max Phi = 1</c> in band, so the worst in-band <c>|Gamma|^2</c> is
/// <c>(K + eps^2)/(1 + eps^2)</c> either way, the (K, eps^2) search below does not know which it is
/// looking at, and the arm count is 2n in both.</para>
///
/// <para><b>Butterworth exists for two bands and does not exist for three</b>, and that is
/// structural rather than unimplemented: the Butterworth member is <c>x(u)^{2n}</c>, maximally flat
/// at the band centre of ONE interval, and a union of intervals has no such point to be flat at. The
/// equiripple polynomial is the only member of this family on a union — which is what
/// <see cref="MatchRemez"/> computes — so tri-band offers Chebyshev and refuses the rest by name.</para>
///
/// <b>There is no new synthesis here, and that is the design.</b> The ladder is
/// <c>MatchSynthesis.Build(g, 2n, ...)</c> applied to the g-vector
/// <c>MatchFormPrototype.GvaluesAt(family, n, A, K, eps2)</c> returns, at
/// <c>omega0 = 2 pi sqrt(f1*f4)</c>, <c>w = (f4 - f1)/sqrt(f1*f4)</c> and
/// <c>a = (f3 - f2)/(f4 - f1)</c>. What comes out is an ordinary alternating bandpass ladder of 2n
/// two-element arms, so Norton transforms, the excess-element rule, <c>WithEndSplits</c>, the
/// linkage rule, Flatten and the stamp all handle it already and none of them needed a multiband
/// case. Everything below is the choice of ONE family member.
///
/// <h3>The design rule is §4.3's, with a different prototype</h3>
/// <para>The near end's absorbed element is fixed — <c>g1 = Q_ana * w</c>, with Q read at omega0 (the
/// gap centre, where every arm is transparent) and w the OUTER fractional bandwidth — which pins one
/// of the family's two parameters. The other is chosen to minimise the worst in-band
/// <c>|Gamma|^2 = (K + eps^2)/(1 + eps^2)</c>. That is the shape of <c>FanoG</c>: one end prescribed,
/// one free parameter, an optimum. The far end is then reconciled by §4.5 exactly as the single-band
/// bandpass path reconciles it.</para>
///
/// <h3>Scan K, solve eps^2 — not the other way round</h3>
/// <para><b>K is not monotone in the near element</b> (<c>RESOLVED.md</c> §MN-LP, measured: g1 rises
/// then falls with K), so K is SCANNED rather than bisected. For a fixed K, g1 was monotone in eps^2
/// in every case measured, so eps^2 is found by bracketing a sign change on a log grid and bisecting.
/// The optimum is insensitive to K — §18.4 measures the worst return loss moving by at most 0.1 dB
/// across a decade — so a coarse log scan plus a bounded refinement is ample and no root-finding
/// subtlety exists.</para>
/// </remarks>
public static class MatchMultibandSynthesis
{
    /// <summary>How many K values the scan looks at, log-spaced over <c>[KFloor, KCeiling]</c>.</summary>
    private const int KSamples = 64;

    /// <summary>
    /// The top of the K scan.
    /// </summary>
    /// <remarks>
    /// K is the in-band return-loss FLOOR (<c>RESOLVED.md</c> §MN-LP): the worst <c>|Gamma|^2</c> is
    /// at least K, so a member at K = 0.9 is a -0.5 dB match and is never the optimum of anything.
    /// The measured optima sit at 1e-6..1e-3; the ceiling is here only so the scan cannot mistake the
    /// edge of its own range for a bracket.
    /// </remarks>
    private const double KCeiling = 0.9;

    /// <summary>How many eps^2 values the inner bracket search looks at, log-spaced.</summary>
    private const int EpsSamples = 64;

    private const double EpsLow = 1e-7;
    private const double EpsHigh = 1e6;

    /// <summary>
    /// The Remez polynomial for one tri-band interval set, memoised on the set itself.
    /// </summary>
    /// <remarks>
    /// <b>The exchange is milliseconds and the search calls it up to 130 times per synthesis</b> — 64
    /// K samples plus the golden-section refinement, each running a bracket-and-bisect in eps^2 — and
    /// none of those calls moves the intervals, which depend only on the effective band edges. So it
    /// is computed once per (degree, band set) and handed to <c>GvaluesAtPolynomial</c> thereafter.
    /// Keyed on the SCALED interval edges, which are exactly what the exchange reads.
    /// </remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (int Degree, double UR, double Lo0, double Hi0, double LoLast), MatchPrototypePolynomial?>
        RemezMemo = new();

    /// <summary>
    /// The <c>uR</c> grid the odd family's extra parameter is scanned on — log-spaced, and wide.
    /// </summary>
    /// <remarks>
    /// <b>Scanned rather than solved, for the reason K is</b> (match.md §18.9): nothing is known to be
    /// monotone in it, and the objective is flat enough that a coarse grid finds the optimum to well
    /// inside the 0.1 dB the family's own K-insensitivity already costs. The range spans the two
    /// limits that matter — a pole close to the band, where the extra element is large, and one far
    /// from it, where the odd member degenerates onto the even member below it (§16.3, measured in
    /// <c>MatchFormPrototypeTests</c>).
    /// </remarks>
    private static readonly double[] UrSamples =
        [0.02, 0.06, 0.15, 0.4, 1.0, 2.5, 6.0, 15.0, 40.0, 150.0];

    /// <summary>
    /// How many K values the odd family's <b>uR sweep</b> looks at, before the winning uR is searched
    /// again at full resolution.
    /// </summary>
    /// <remarks>
    /// <b>Two stages, because one member of the weighted family costs what fifty of the closed-form
    /// one do.</b> A weighted member is two Durand-Kerner runs at degree 2n + 1 plus the extraction;
    /// a closed-form member is an arccosine. At <see cref="KSamples"/> across ten pole positions the
    /// sweep was measured at 2.5 s for one order, which is background work a user waits on. A
    /// 12-point K scan is enough to RANK the pole positions — the objective moves by at most 0.1 dB
    /// across a decade of K (§18.4), so the ranking does not depend on K's resolution — and the
    /// winner is then searched at the full 64 points and refined. Measured at 0.35 s for the same
    /// order, with the same member coming out.
    /// </remarks>
    private const int KSamplesCoarse = 12;

    /// <summary>Synthesises the basis ladder of a multiband design (match.md §18).</summary>
    /// <remarks>
    /// Reached from <see cref="MatchSynthesis.Synthesize"/>, which memoises it, and only after that
    /// method's own termination validation and its bandpass-form check.
    /// </remarks>
    public static MatchSynthesisResult Synthesize(MatchDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);

        int bandCount = Math.Clamp(design.BandCount, 2, 3);
        string word = bandCount >= 3 ? "tri-band" : "dual-band";

        bool ordered = design.F1 > 0 && design.F2 > design.F1 && design.F3 > design.F2
                       && design.F4 > design.F3
                       && (bandCount < 3 || (design.F5 > design.F4 && design.F6 > design.F5));
        if (!ordered)
            return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                MatchRefusalKind.InvalidTermination,
                bandCount >= 3
                    ? "A tri-band spec must satisfy 0 < f1 < f2 < f3 < f4 < f5 < f6 — three "
                      + "increasing bands with a gap between each pair."
                    : "A dual-band spec must satisfy 0 < f1 < f2 < f3 < f4 — two increasing bands "
                      + "with a gap between them.",
                null,
                ("f1", design.F1), ("f2", design.F2), ("f3", design.F3), ("f4", design.F4),
                ("f5", design.F5), ("f6", design.F6)));

        // ── Which parity of ladder the terminations need (match.md §18.5) ────
        //
        // A like pair — both parallel or both series, both reactive — needs an ODD arm count, whose
        // two ends have the same orientation. That is the weighted family; a mixed pair, or a pair
        // with a resistive end, takes the even one, which has a closed form. ORDER means match points
        // per band in both, so the picker offers the same list either way and only the element count
        // moves: 4n against 4n + 2.
        bool odd = MatchOrders.NeedsOddCount(design.Term1, design.Term2);

        var valid = MatchOrders.ValidOrders(
            design.Term1, design.Term2, NetworkForm.Bandpass, bandCount);
        if (!valid.Contains(design.Order))
            return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                MatchRefusalKind.InvalidOrder,
                $"Order {design.Order} is outside {valid[0]}..{valid[^1]}: a {word} order is match "
                + "points PER BAND and the element count is 4n.",
                null, ("order", design.Order), ("minValid", valid[0]), ("maxValid", valid[^1])));

        // ── Which families this band count has ────────────────────────────────
        //
        // Bessel and the double-match Chebyshev are absent for BOTH counts and for the reasons §18.2
        // gives. Butterworth is absent for THREE and the reason is different and structural: the
        // maximally-flat member is flat at one interval's centre, and a union of intervals has no
        // such point. See the class remark.
        bool chebyshevOnly = (bandCount >= 3 || odd) && design.Response != ResponseShape.ChebyshevFano;
        if (design.Response is ResponseShape.Bessel or ResponseShape.ChebyshevTwoEnded
            || chebyshevOnly)
            return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                MatchRefusalKind.ResponseInfeasible,
                odd && design.Response == ResponseShape.Butterworth
                    ? "Butterworth has no member with an odd element count: two ends of the same "
                      + "topology need the weighted family, which a Remez exchange produces and "
                      + "which is equiripple by construction. Chebyshev is offered."
                    : bandCount >= 3
                    ? $"{design.Response} is not offered in tri-band form: over a UNION of prototype "
                      + "intervals the equiripple polynomial is the only member of this family — "
                      + "maximal flatness needs one interval to be flat in the middle of, and the "
                      + "double-match Chebyshev is a 2-D solve in (K, eps^2). Chebyshev is offered."
                    : $"{design.Response} is not offered in dual-band form: the "
                      + "double-match Chebyshev is a 2-D solve in (K, eps^2) for both end elements, "
                      + "and Bessel has no prototype in this family. Chebyshev and Butterworth are.",
                null, ("order", design.Order)));

        var bands = design.Effective;
        double om0 = design.Omega0, w = design.W, a = design.A;
        int n = design.Order;

        // ── The one refusal only three bands can make (match.md §18.3) ────────
        //
        // Mirroring widens each outer band to cover its partner's image, and a wide enough pair
        // reaches the middle band. There is then no gap to save budget in on that side, the union in
        // u is no longer two disjoint intervals, and the Remez exchange has nothing to solve — so
        // this is a refusal naming the remedy rather than a polynomial that happens to fail.
        if (bands.Overlaps)
            return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                MatchRefusalKind.InvalidTermination,
                "Bands 1 and 3 overlap band 2 after mirroring: widened to "
                + $"{Ghz(bands.F1)}\u2013{Ghz(bands.F2)} and {Ghz(bands.F5)}\u2013{Ghz(bands.F6)} GHz "
                + $"against a middle band of {Ghz(bands.F3)}\u2013{Ghz(bands.F4)} GHz. Move the outer "
                + "bands apart, narrow them, or use dual-band.",
                null,
                ("f2Effective", bands.F2), ("f3", bands.F3),
                ("f4", bands.F4), ("f5Effective", bands.F5)));

        double q1 = design.Term1.QAt(om0), q2 = design.Term2.QAt(om0);
        bool anaIsTerm1 = MatchSynthesis.AnalysisIsTerm1(design);
        Termination ana = anaIsTerm1 ? design.Term1 : design.Term2;
        Termination far = anaIsTerm1 ? design.Term2 : design.Term1;
        double qAnaActual = anaIsTerm1 ? q1 : q2;
        double qFarActual = anaIsTerm1 ? q2 : q1;
        double qAna = design.QAdjust > 0 ? design.QAdjust : qAnaActual;

        // The same "a parasitic cannot be subtracted" refusal the single-band path makes, for the
        // same reason: an end arm below the termination's own reactance is not a network.
        if (design.QAdjust > 0 && qAnaActual > 0 && design.QAdjust < qAnaActual * (1.0 - 1e-9))
            return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                MatchRefusalKind.AnalysisEndNotAbsorbable,
                $"The Q-adjust of {design.QAdjust:0.####} is BELOW termination "
                + $"{(anaIsTerm1 ? 1 : 2)}'s own Q of {qAnaActual:0.####} at the gap centre, so the "
                + "ladder's end arm would be smaller than the reactance that termination already "
                + "supplies. Q-adjust inflates an analysis end's Q; it cannot reduce "
                + $"one. Clear it, or set it to at least {qAnaActual:0.####}.",
                anaIsTerm1 ? 1 : 2,
                ("qAdjust", design.QAdjust), ("qActual", qAnaActual),
                ("ratio", design.QAdjust / qAnaActual)));

        var family = design.Response == ResponseShape.Butterworth
            ? ResponseShape.Butterworth
            : ResponseShape.ChebyshevFano;

        // ── ONE member factory, whatever the band count and parity ───────────
        //
        // Two bands with a mixed pair take the closed form: Phi = T_n(x(u))^2 on [a^2, 1], roots by
        // arccosine (§18.2, §16.9). Three take the equiripple polynomial on the union, which has no
        // closed form and is the Remez exchange's answer (§18.5). A like pair takes the WEIGHTED
        // exchange's, on whichever interval set the band count gives, with uR as one more parameter.
        // Every one of them is memoised, because the search below asks for ~130 members per uR and
        // none of them moves the intervals. Nothing downstream knows which it got.
        var intervals = bands.Intervals;

        MatchPrototypePolynomial? Prototype(double uR) => RemezMemo.GetOrAdd(
            (n, uR, intervals[0].Lo, intervals[0].Hi, intervals[^1].Lo),
            key => key.UR > 0.0
                ? MatchRemez.MinimaxWeightedScaled(key.Degree, key.UR, intervals)
                : MatchRemez.MinimaxScaled(key.Degree, intervals));

        Func<double, double, double[]?>? MemberAt(double uR)
        {
            if (odd)
            {
                var w2 = Prototype(uR);
                return w2 is null ? null : (k, eps2) => MatchFormPrototype.GvaluesAtWeighted(w2, uR, k, eps2);
            }
            if (bandCount < 3) return (k, eps2) => MatchFormPrototype.GvaluesAt(family, n, a, k, eps2);
            var p2 = Prototype(0.0);
            return p2 is null ? null : (k, eps2) => MatchFormPrototype.GvaluesAtPolynomial(p2, k, eps2);
        }

        var member = MemberAt(odd ? UrSamples[0] : 0.0);
        if (member is null)
            return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                MatchRefusalKind.NoRealRoot,
                $"The Remez exchange did not converge on the {word} prototype at order {n} for the "
                + $"effective bands {string.Join(", ", design.Bands.Select(b => $"{Ghz(b.Lo)}\u2013{Ghz(b.Hi)}"))} GHz.",
                null, ("order", n)));

        var notes = new List<string>();
        if (bands.Widened && bands.Note is not null) notes.Add(bands.Note);

        double[]? chosen;
        bool ripple = qAna <= 0.0;
        if (ripple)
        {
            // ── match.md §18.2/§3.4: neither end is reactive, so there is no g1 to prescribe ──
            //
            // K goes to its floor and eps^2 comes from the requested ripple instead, exactly as the
            // real-to-real bandpass case takes its prototype from RippleDb. The ripple is an
            // INSERTION-loss figure, so |Gamma|^2 = 1 - 10^(-ripple/10) and eps^2 is what puts the
            // family's own worst |Gamma|^2 = (K + eps^2)/(1 + eps^2) there.
            double lar = design.RippleDb > 0 ? design.RippleDb : 0.1;
            double gamma2 = 1.0 - Math.Pow(10.0, -lar / 10.0);
            double eps2 = gamma2 / (1.0 - gamma2);
            chosen = member(MatchFormPrototype.KFloor, eps2);
            if (chosen is null)
                return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                    MatchRefusalKind.NoRealRoot,
                    $"The {word} {family} family produced no realizable member at order {n} for a "
                    + $"{lar:0.###} dB equal-ripple response.",
                    null, ("order", n), ("a", a), ("ripple", lar)));
            if (design.Response != ResponseShape.ChebyshevFano)
                notes.Add(
                    $"{design.Response} needs a reactance to prescribe; with a purely resistive end "
                    + $"the equal-ripple prototype at {lar:0.###} dB ran instead.");
        }
        else
        {
            // What "reaches the far end" means, in g. Build reads Q_far off the last element and the
            // terminating ratio; with an ODD arm count both ends carry the analysis end's own
            // orientation, so the inversion is decided once here rather than per member.
            bool farIsSeries = ana.Topology == TerminationTopology.Series;
            bool Reaches(double[] g)
            {
                double q = g[^2] * g[^1] / w;
                if (farIsSeries) q = 1.0 / q;
                return qFarActual <= 0.0 || q >= qFarActual * (1.0 - 1e-9);
            }

            var found = odd
                ? SearchOdd(MemberAt, qAna * w, Reaches)
                : Search(member, qAna * w);
            if (found.G is null)
                return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                    MatchRefusalKind.NoRealRoot,
                    $"No {word} member at order {n} puts g1 = {qAna * w:0.#####} at the analysis "
                    + $"end: over the whole (K, eps^2) scan the family reaches g1 between "
                    + $"{found.MinG1:0.#####} and {found.MaxG1:0.#####}. Another order, the other "
                    + "response family, or the other analysis end will.",
                    anaIsTerm1 ? 1 : 2,
                    ("order", n), ("g1Required", qAna * w),
                    ("g1Min", found.MinG1), ("g1Max", found.MaxG1)));
            chosen = found.G;
        }

        if (chosen.Any(v => !double.IsFinite(v) || v <= 0.0))
            return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                MatchRefusalKind.NoRealRoot,
                $"The {word} {family} prototype at order {n} produced no positive g-vector for an "
                + $"analysis-end Q of {qAna:0.####}.",
                anaIsTerm1 ? 1 : 2, ("order", n), ("q", qAna)));

        // g[0] = 1 in front, so Build and Fingerprint see the shape every prototype in this library
        // has: a leading 1, the elements, then the terminating ratio. The ARM COUNT is read off the
        // vector — 2n for the even family and 2n + 1 for the weighted one — rather than assumed,
        // which is the only line the odd counts needed here.
        double[] g5 = [1.0, .. chosen];
        int armCount = chosen.Length - 1;
        var b = MatchSynthesis.Build(g5, armCount, ana, far, design, anaIsTerm1);

        if (qFarActual > 0.0 && b.QFar < qFarActual * (1.0 - 1e-9))
            return MatchSynthesisResult.Refuse(MatchRefusal.Create(
                MatchRefusalKind.FarEndNotAbsorbable,
                $"Termination {(anaIsTerm1 ? 2 : 1)} is not absorbable at order {n}: the {word} "
                + $"synthesis reaches Q_far = {b.QFar:0.####} against the termination's own "
                + $"{qFarActual:0.####} (ratio {b.QFar / qFarActual:0.###}). Try another order, "
                + "another response, the other analysis end, or a Q-adjusted solution.",
                anaIsTerm1 ? 2 : 1,
                ("qFar", b.QFar), ("qActual", qFarActual), ("ratio", b.QFar / qFarActual)));

        bool needsExcess =
            qFarActual > 0.0 && b.QFar / qFarActual > MatchSynthesis.ExcessRatioThreshold;

        return new MatchSynthesisResult
        {
            G = g5,
            Omega0 = om0,
            W = w,
            AnalysisIsTerm1 = anaIsTerm1,
            QAnalysis = qAna,
            QAnalysisActual = qAnaActual,
            QFarSynthesised = b.QFar,
            QFarActual = qFarActual,
            RAnalysis = ana.R,
            RFarSynthesised = b.RFar,
            RFarTarget = far.R,
            Network = b.Network,
            UsedRipplePrototype = ripple,
            NeedsExcessElement = needsExcess,
            BasisFingerprint = MatchSynthesis.Fingerprint(g5, b.Network),
            Notes = notes,
        };
    }

    /// <summary>What one (K, eps^2) search produced, and what it saw on the way.</summary>
    /// <param name="G">The chosen member, or null when no K brackets the target.</param>
    /// <param name="MinG1">The smallest g1 any scanned member reached — for the refusal.</param>
    /// <param name="MaxG1">The largest.</param>
    /// <param name="Worst">The winner's worst in-band <c>|Gamma|^2</c>, or +infinity when none.</param>
    internal readonly record struct MultibandSearch(
        double[]? G, double MinG1, double MaxG1, double Worst = double.PositiveInfinity);

    /// <summary>
    /// match.md §18.2's one-parameter optimum: over K, solve eps^2 for <c>g1 = target</c>, keep the K
    /// whose member has the smallest worst in-band <c>|Gamma|^2</c>.
    /// </summary>
    /// <remarks>
    /// <b>The family arrives as a function of (K, eps^2) and nothing else.</b> That is what makes one
    /// search serve both band counts: the caller has already decided whether Phi is the closed-form
    /// <c>T_n(x(u))^2</c> or the Remez polynomial squared, and the search cannot tell.
    ///
    /// <para><b>Internal so a test can score the family directly.</b> The golden values of §18.4 are stated
    /// at a specific (K, eps^2), and the optimum this finds may sit at a slightly different K — the
    /// worst return loss moves by at most 0.1 dB per decade — so the test asserts the MEMBER through
    /// <c>GvaluesAt</c> and this search's answer to a tolerance, rather than pretending they are the
    /// same claim.
    /// </remarks>
    internal static MultibandSearch Search(
        Func<double, double, double[]?> member, double target, int kSamples = KSamples,
        Func<double[], bool>? reachesFarEnd = null)
    {
        double minG1 = double.PositiveInfinity, maxG1 = 0.0;
        void Observe(double g1)
        {
            if (!double.IsFinite(g1) || g1 <= 0) return;
            minG1 = Math.Min(minG1, g1);
            maxG1 = Math.Max(maxG1, g1);
        }

        double logKLo = Math.Log(MatchFormPrototype.KFloor), logKHi = Math.Log(KCeiling);
        double step = (logKHi - logKLo) / (kSamples - 1.0);

        // ── Feasibility outranks the objective, when a caller supplies one ───
        //
        // The even family has one free parameter and no choice: it takes the best member and the
        // caller then reports whether the far end fits. The WEIGHTED family has two, and match.md
        // §18.5 §5 says what the second one is for — so a member that reaches the far end beats one
        // that does not, however good the latter's return loss, because the latter is refused. With
        // no predicate this is exactly the old ranking, which is what keeps MN-MB1's goldens fixed.
        double bestLogK = double.NaN, bestWorst = double.PositiveInfinity;
        double[]? best = null;
        bool bestReaches = reachesFarEnd is null;

        void Consider(double[] g, double logK, double worst)
        {
            bool reaches = reachesFarEnd is null || reachesFarEnd(g);
            if (bestReaches && !reaches) return;
            if (reaches == bestReaches && worst >= bestWorst) return;
            best = g;
            bestWorst = worst;
            bestLogK = logK;
            bestReaches = reaches;
        }

        for (int i = 0; i < kSamples; i++)
        {
            double logK = logKLo + step * i;
            var (g, eps2) = SolveEps(member, Math.Exp(logK), target, Observe);
            if (g is null) continue;
            Consider(g, logK, MatchFormPrototype.WorstInBand(Math.Exp(logK), eps2));
        }

        if (best is null || double.IsNaN(bestLogK))
            return new MultibandSearch(null, double.IsInfinity(minG1) ? 0.0 : minG1, maxG1);

        // Bounded golden-section on log K, one grid step either side of the scan's best. The
        // objective is flat here by construction (§18.4: 0.1 dB per decade), so this is polish, not a
        // search — which is why it is bounded rather than allowed to wander out of the bracket.
        const double Phi = 0.6180339887498949;
        double lo = Math.Max(logKLo, bestLogK - step), hi = Math.Min(logKHi, bestLogK + step);
        double x1 = hi - Phi * (hi - lo), x2 = lo + Phi * (hi - lo);
        var (g1v, f1) = Evaluate(member, x1, target, Observe);
        var (g2v, f2) = Evaluate(member, x2, target, Observe);
        for (int it = 0; it < 40 && hi - lo > 1e-9; it++)
        {
            if (f1 <= f2)
            {
                hi = x2; x2 = x1; g2v = g1v; f2 = f1;
                x1 = hi - Phi * (hi - lo);
                (g1v, f1) = Evaluate(member, x1, target, Observe);
            }
            else
            {
                lo = x1; x1 = x2; g1v = g2v; f1 = f2;
                x2 = lo + Phi * (hi - lo);
                (g2v, f2) = Evaluate(member, x2, target, Observe);
            }
        }

        if (g1v is not null) Consider(g1v, x1, f1);
        if (g2v is not null) Consider(g2v, x2, f2);

        return new MultibandSearch(best, double.IsInfinity(minG1) ? 0.0 : minG1, maxG1, bestWorst);
    }

    /// <summary>
    /// The odd family's search: <see cref="Search"/> once per <c>uR</c>, keeping the best member.
    /// </summary>
    /// <remarks>
    /// <b>Three parameters, one prescribed, two searched</b> (match.md §18.5 §5): <c>g1 = Q.w</c> pins
    /// eps^2 for any (K, uR), so the free pair is scanned — uR on <see cref="UrSamples"/>, and K by
    /// the same 64-point log scan and bounded refinement the even family uses. The whole grid is
    /// ~12 Remez exchanges and a few thousand root-finds, all of them milliseconds.
    ///
    /// <para><b>The reported g1 range spans every uR</b>, so the refusal names what the FAMILY can
    /// reach rather than what one arbitrary pole position could.</para>
    /// </remarks>
    private static MultibandSearch SearchOdd(
        Func<double, Func<double, double, double[]?>?> memberAt, double target,
        Func<double[], bool> reachesFarEnd)
    {
        double bestUr = double.NaN, bestWorst = double.PositiveInfinity;
        double minG1 = double.PositiveInfinity, maxG1 = 0.0;
        bool bestReaches = false;

        foreach (double uR in UrSamples)
        {
            var coarse = memberAt(uR);
            if (coarse is null) continue;

            var found = Search(coarse, target, KSamplesCoarse, reachesFarEnd);
            if (found.MinG1 > 0) minG1 = Math.Min(minG1, found.MinG1);
            maxG1 = Math.Max(maxG1, found.MaxG1);
            if (found.G is null) continue;

            // A pole position that reaches the far end beats one that does not, for the reason
            // Search's own note gives; among those that do, the best return loss wins.
            bool reaches = reachesFarEnd(found.G);
            if (bestReaches && !reaches) continue;
            if (reaches == bestReaches && found.Worst >= bestWorst) continue;
            bestUr = uR;
            bestWorst = found.Worst;
            bestReaches = reaches;
        }

        double lo = double.IsInfinity(minG1) ? 0.0 : minG1;
        if (double.IsNaN(bestUr)) return new MultibandSearch(null, lo, maxG1);

        var winner = memberAt(bestUr);
        if (winner is null) return new MultibandSearch(null, lo, maxG1);

        var full = Search(winner, target, KSamples, reachesFarEnd);
        return new MultibandSearch(full.G, lo, Math.Max(maxG1, full.MaxG1), full.Worst);
    }

    /// <summary>One golden-section probe: the member at <c>exp(logK)</c> and its worst |Gamma|^2.</summary>
    private static (double[]? G, double Worst) Evaluate(
        Func<double, double, double[]?> member, double logK, double target, Action<double> observe)
    {
        double k = Math.Exp(logK);
        var (g, eps2) = SolveEps(member, k, target, observe);
        return g is null
            ? (null, double.PositiveInfinity)
            : (g, MatchFormPrototype.WorstInBand(k, eps2));
    }

    /// <summary>
    /// For a fixed K, the eps^2 that puts the family's first element on <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// <b>Bracket then bisect, both in log eps^2.</b> g1 was monotone in eps^2 for fixed K in every
    /// case measured (match.md §18.9), but a log grid plus a sign change does not depend on that
    /// being true everywhere — it depends only on the bracket it actually found. Where a K row shows
    /// two brackets the FIRST is taken, which is the one with the smaller eps^2 and therefore the
    /// better worst-case <c>|Gamma|^2</c>.
    /// </remarks>
    private static (double[]? G, double Eps2) SolveEps(
        Func<double, double, double[]?> member, double k, double target, Action<double> observe)
    {
        if (!(k > 0.0) || k >= 1.0 || !(target > 0.0)) return (null, 0.0);

        double lo = Math.Log(EpsLow), hi = Math.Log(EpsHigh);
        double prevX = double.NaN, prevF = 0.0;
        double bracketLo = double.NaN, bracketHi = double.NaN;

        for (int i = 0; i < EpsSamples; i++)
        {
            double x = lo + (hi - lo) * i / (EpsSamples - 1.0);
            var g = member(k, Math.Exp(x));
            if (g is null) { prevX = double.NaN; continue; }

            observe(g[0]);
            double f = g[0] - target;
            if (f == 0.0) return (g, Math.Exp(x));
            if (!double.IsNaN(prevX) && Math.Sign(f) != Math.Sign(prevF))
            {
                bracketLo = prevX;
                bracketHi = x;
                break;
            }
            prevX = x;
            prevF = f;
        }

        if (double.IsNaN(bracketLo)) return (null, 0.0);

        double[]? bestG = null;
        double bestEps2 = 0.0;
        for (int it = 0; it < 200 && bracketHi - bracketLo > 1e-12; it++)
        {
            double mid = 0.5 * (bracketLo + bracketHi);
            var g = member(k, Math.Exp(mid));
            if (g is null) break;
            bestG = g;
            bestEps2 = Math.Exp(mid);
            if (Math.Sign(g[0] - target) == Math.Sign(prevF)) bracketLo = mid; else bracketHi = mid;
        }

        return bestG is null ? (null, 0.0) : (bestG, bestEps2);
    }

    private static string Topology(Termination t) =>
        t.Topology == TerminationTopology.Parallel ? "parallel" : "series";

    /// <summary>Three significant figures in GHz — the register match.md §18.7's sentences are in.</summary>
    private static string Ghz(double hz) =>
        (hz / 1e9).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
