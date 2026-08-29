using System.Text.Json.Serialization;

namespace CircuitRF.Core.Matching;

/// <summary>Which single reactive element a termination carries, if any (match.md §5.3).</summary>
/// <remarks>
/// <c>None</c> is a first-class value rather than <c>Value == 0</c>. The reference implementation
/// overloaded a zero capacitance to mean "purely resistive" and could then not tell it apart from a
/// legitimately tiny one, which forced it to silently rewrite a series termination to parallel. An
/// inductive "no reactance" has no zero to overload at all (it would be L = infinity), so the
/// convention could not have survived <see cref="ReactanceKind.L"/> even in principle.
/// </remarks>
public enum ReactanceKind
{
    /// <summary>Purely resistive: Q = 0, nothing to absorb.</summary>
    None,

    /// <summary>A capacitance, in farads.</summary>
    C,

    /// <summary>An inductance, in henries.</summary>
    L,
}

/// <summary>How the termination's reactance sits against its resistance.</summary>
public enum TerminationTopology
{
    /// <summary>R and the reactance in series — the end arm is a SERIES arm.</summary>
    Series,

    /// <summary>R and the reactance in parallel — the end arm is a SHUNT arm.</summary>
    Parallel,
}

/// <summary>
/// The network form (match.md §16). <see cref="Bandpass"/> is §4's synthesis; the other two are
/// ladders of SINGLE elements matched between F1 and F2, with the impedance ratio pinned by the
/// ladder's own transparency at DC (lowpass) or at infinity (highpass).
/// </summary>
/// <remarks>
/// <b>Additive, and default <see cref="Bandpass"/>.</b> A payload written before rev 2 carries no
/// <c>Form</c> at all and decodes to the form it was synthesised in, so <c>Version</c> stays 1 and an
/// old design rebuilds to the identical ladder.
/// </remarks>
public enum NetworkForm
{
    /// <summary>match.md §4: two-element arms resonant at band centre. The default.</summary>
    Bandpass,

    /// <summary>Series L in the through path, shunt C to ground. Transparent at DC.</summary>
    Lowpass,

    /// <summary>Series C in the through path, shunt L to ground. Transparent at infinity.</summary>
    Highpass,
}

/// <summary>The prototype family the synthesis draws from (match.md §4.3, §6).</summary>
public enum ResponseShape
{
    /// <summary>Levy's singly-prescribed closed form with the Fano root — the default.</summary>
    ChebyshevFano,

    /// <summary>
    /// Levy's doubly-prescribed closed form. Both end Q's are inputs, so the far end absorbs exactly
    /// and no excess element is ever needed; what it gives up is Fano optimality.
    /// </summary>
    ChebyshevTwoEnded,

    /// <summary>Maximally-flat magnitude, via the numerical route.</summary>
    Butterworth,

    /// <summary>Maximally-flat group delay, via the numerical route. Usually refused by the far end.</summary>
    Bessel,
}

/// <summary>Which end pins g1 (match.md §4.2).</summary>
public enum AnalysisEndChoice
{
    /// <summary>The higher-Q end — the binding constraint, and the default.</summary>
    Highest,

    /// <summary>Force termination 1.</summary>
    Term1,

    /// <summary>Force termination 2.</summary>
    Term2,
}

/// <summary>Which three-element equivalent a Norton transform produces (match.md §4.7).</summary>
public enum TransformForm
{
    /// <summary>shunt - series - shunt.</summary>
    Pi,

    /// <summary>series - shunt - series.</summary>
    T,
}

/// <summary>
/// One end of the match: a resistance with at most one reactive element (match.md §4.1, §5).
/// </summary>
/// <param name="R">Port resistance, ohms.</param>
/// <param name="Kind">Which reactance, or <see cref="ReactanceKind.None"/>.</param>
/// <param name="Topology">Series or parallel against <paramref name="R"/>.</param>
/// <param name="Value">Farads or henries, per <paramref name="Kind"/>. Ignored when None.</param>
/// <param name="Probed">True when §10's probe supplied this, rather than the user.</param>
/// <param name="ProbedAtUtc">When the probe ran; provenance for the Designer.</param>
public sealed record Termination(
    double R,
    ReactanceKind Kind,
    TerminationTopology Topology,
    double Value,
    bool Probed = false,
    DateTime? ProbedAtUtc = null)
{
    /// <summary>A purely resistive end.</summary>
    public static Termination Resistive(double r, TerminationTopology topology = TerminationTopology.Parallel)
        => new(r, ReactanceKind.None, topology, 0.0);

    /// <summary>
    /// The equivalent capacitance at band centre — <b>the whole of match.md §5</b>, and the only place
    /// (with <see cref="MatchQ.SplitExcess"/> and <c>NortonTransform</c>'s absorbed-type rule) that is
    /// allowed to look at <see cref="Kind"/>. Everything downstream sees C_eq and Q and therefore
    /// supports inductive terminations without knowing that it does.
    /// </summary>
    public double CeqAt(double omega0) => Kind switch
    {
        ReactanceKind.C => Value,
        ReactanceKind.L => Value > 0 ? 1.0 / (omega0 * omega0 * Value) : double.PositiveInfinity,
        _ => 0.0,
    };

    /// <summary>
    /// The termination's Q at band centre. Parallel: omega0*R*C_eq. Series: its reciprocal — and that
    /// inversion is why match.md §4.5's excess rule reads the same for all four combinations.
    /// </summary>
    public double QAt(double omega0)
    {
        double ceq = CeqAt(omega0);
        if (Kind == ReactanceKind.None || ceq == 0.0) return 0.0;
        return Topology == TerminationTopology.Parallel
            ? omega0 * R * ceq
            : 1.0 / (omega0 * R * ceq);
    }

    /// <summary>True when this end has a reactance for the synthesis to absorb.</summary>
    [JsonIgnore]
    public bool HasReactance => Kind != ReactanceKind.None && Value > 0.0;

    /// <summary>Which ladder element type this end supplies, or null when it supplies none.</summary>
    [JsonIgnore]
    public ElementType? AbsorbedType => Kind switch
    {
        ReactanceKind.C => ElementType.C,
        ReactanceKind.L => ElementType.L,
        _ => null,
    };
}

/// <summary>
/// One applied Norton transform, keyed <b>by element name</b> (match.md §7.3).
/// </summary>
/// <remarks>
/// <b>NMin/NMax are deliberately absent.</b> A pair's range is the positivity threshold computed from
/// the element values as they stand when that transform is applied, which depends on every earlier
/// transform. A stored bound goes stale against the elements it bounds, and a stale bound silently
/// permits a negative element — strictly worse than no bound. The range is recomputed during the
/// sequential rebuild, where the state it depends on exists.
/// </remarks>
public sealed record TransformRecord(
    string ElementA,
    string ElementB,
    TransformForm Form,
    double N,
    bool Locked);

/// <summary>
/// The serializable Match design — <b>the single source of truth</b>. Everything else (g-values, the
/// element list, the response, the transform ranges) is derived at load and never stored, so a design
/// cannot disagree with its own inputs (match.md §7.1).
/// </summary>
/// <remarks>
/// <b>A computed property carries <c>[JsonIgnore]</c>, and since 2026-08-20 that matters to a person
/// and not only to a serializer.</b> <c>MatchEmbedding.Encode</c> writes this type's JSON straight
/// into the <c>.csch</c> so the stored design is readable, and a reader looking at it has to be able
/// to tell the INPUTS from the arithmetic. <c>Omega0</c>, <c>W</c>, <c>HasReactance</c> and
/// <c>AbsorbedType</c> were being written as if they were settable, which invites exactly the edit
/// that does nothing (they have no setter, so a reader ignores them). Anything derived from a field
/// on this record belongs behind the attribute.
/// </remarks>
public sealed class MatchDesign
{
    /// <summary>Payload version, for future readers.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Lower band edge, Hz.</summary>
    public double F1 { get; set; } = 3.3e9;

    /// <summary>Upper band edge, Hz.</summary>
    public double F2 { get; set; } = 5.0e9;

    /// <summary>
    /// How many bands the network is matched over (match.md §18). 1 is the single band of §4; 2 is
    /// dual-band — f1-f2 and f3-f4, mirrored about the gap centre (§18.3), with the gap between them
    /// deliberately unmatched; 3 is tri-band (§18.5), a kept middle band with a mirrored outer pair
    /// and TWO deliberately unmatched gaps.
    /// </summary>
    /// <remarks>
    /// <b>Additive</b>: a payload written before rev 3 carries no band count at all and decodes to 1,
    /// so <c>Version</c> stays 1 and every existing design rebuilds to the identical ladder.
    /// </remarks>
    public int BandCount { get; set; } = 1;

    /// <summary>Lower edge of the second band, Hz (<see cref="BandCount"/> &gt;= 2). 0 when unused.</summary>
    public double F3 { get; set; }

    /// <summary>Upper edge of the second band, Hz (<see cref="BandCount"/> &gt;= 2). 0 when unused.</summary>
    public double F4 { get; set; }

    /// <summary>Lower edge of the third band, Hz (<see cref="BandCount"/> = 3). 0 when unused.</summary>
    public double F5 { get; set; }

    /// <inheritdoc cref="F5"/>
    public double F6 { get; set; }

    /// <summary>Network order, 2..6.</summary>
    public int Order { get; set; } = 4;

    /// <summary>Which prototype family.</summary>
    public ResponseShape Response { get; set; } = ResponseShape.ChebyshevFano;

    /// <summary>Which network form — match.md §16. Additive; absent in a pre-rev-2 payload.</summary>
    public NetworkForm Form { get; set; } = NetworkForm.Bandpass;

    /// <summary>Equal-ripple level, dB — the real-to-real prototype only (match.md §4.3).</summary>
    public double RippleDb { get; set; } = 0.1;

    /// <summary>The port-1 end.</summary>
    public Termination Term1 { get; set; } = new(200.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12);

    /// <summary>The port-2 end.</summary>
    public Termination Term2 { get; set; } = new(1.25, ReactanceKind.C, TerminationTopology.Series, 10e-12);

    /// <summary>Which end pins g1.</summary>
    public AnalysisEndChoice AnalysisEnd { get; set; } = AnalysisEndChoice.Highest;

    /// <summary>Deliberately inflated analysis-end Q, or 0 for none (match.md §4.6).</summary>
    public double QAdjust { get; set; }

    /// <summary>Widens every transform range past its positivity threshold.</summary>
    public bool AllowNegativeComponents { get; set; }

    /// <summary>Moving one N re-solves the unlocked others so the product stays on target.</summary>
    public bool LinkTransforms { get; set; } = true;

    /// <summary>The applied transforms, in application order.</summary>
    public List<TransformRecord> Transforms { get; set; } = [];

    /// <summary>Fingerprints of solutions the user has applied, for the Designer's badges.</summary>
    public List<string> AppliedSolutions { get; set; } = [];

    /// <summary>The basis ladder's structure hash when the design was last edited (match.md §7.3).</summary>
    public string? BasisFingerprint { get; set; }

    /// <summary>How far outside the band the Designer plots, as a fraction of the band.</summary>
    public double PlotBandFraction { get; set; } = 0.10;

    /// <summary>How many points the Designer plots — and the grid match.md §10.1's probe runs on.</summary>
    public int PlotPoints { get; set; } = 401;

    /// <summary>
    /// Whether the port-1 probe aims at the conjugate of what it measures (match.md §10.3).
    /// </summary>
    /// <remarks>
    /// <b>On the design rather than on the view-model</b> for the same reason every other switch here
    /// is: it changes what the next probe produces, so it has to still be set when the schematic is
    /// reopened. It does NOT change the stored termination — a probed termination is a snapshot, and
    /// flipping this re-probes nothing.
    /// </remarks>
    public bool Term1Conjugate { get; set; }

    /// <inheritdoc cref="Term1Conjugate"/>
    public bool Term2Conjugate { get; set; }

    /// <summary>
    /// The bands the synthesis actually designs to — the requested ones for a single band, and
    /// match.md §18.3's symmetrised pair for a dual-band design.
    /// </summary>
    /// <remarks>
    /// <b>Recomputed on every access, never stored</b> (§18.8), for the reason the whole record is
    /// inputs-only: an effective band written into the file could disagree with the requested one it
    /// was derived from, and there would be no way to tell which of the two the ladder came from.
    ///
    /// <para>A single-band design reports <c>(F1, F2, F2, F2)</c>, which is what makes
    /// <see cref="Omega0"/> and <see cref="W"/> below read the same in both cases: the outer pair is
    /// then (F1, F2) and the gap (F2, F3) is empty.</para>
    /// </remarks>
    [JsonIgnore]
    public EffectiveBands Effective => BandCount switch
    {
        >= 3 => MatchBands.Symmetrise3(F1, F2, F3, F4, F5, F6),
        2 => MatchBands.Symmetrise(F1, F2, F3, F4),
        _ => new EffectiveBands(F1, F2, F2, F2, false, 0, null, F2, F2, 1),
    };

    /// <summary>The in-band spans, in frequency order — one, two or three of them.</summary>
    [JsonIgnore]
    public IReadOnlyList<(double Lo, double Hi)> Bands
    {
        get
        {
            var e = Effective;
            return BandCount switch
            {
                >= 3 => [(e.F1, e.F2), (e.F3, e.F4), (e.F5, e.F6)],
                2 => [(e.F1, e.F2), (e.F3, e.F4)],
                _ => [(e.F1, e.F2)],
            };
        }
    }

    /// <summary>
    /// The spans BETWEEN the bands — none for a single band, one for dual, two for tri.
    /// </summary>
    /// <remarks>
    /// <b>The deliberately unmatched frequencies</b> (match.md §18.1). They are reported rather than
    /// hidden: a finite ladder buys its in-band return loss by leaving these alone, and the gap
    /// maximum rising with order is the reclaim happening.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyList<(double Lo, double Hi)> Gaps
    {
        get
        {
            var e = Effective;
            return BandCount switch
            {
                >= 3 => [(e.F2, e.F3), (e.F4, e.F5)],
                2 => [(e.F2, e.F3)],
                _ => [],
            };
        }
    }

    /// <summary>
    /// Band centre, rad/s: <c>omega0 = 2 pi sqrt(f_lowest * f_highest)</c> of the EFFECTIVE outer
    /// pair — which is <c>sqrt(F1*F2)</c> for a single band and the gap centre for a dual one
    /// (match.md §18.9: the terminations' Q is read there, and every arm is transparent there).
    /// </summary>
    [JsonIgnore]
    public double Omega0 => Effective.Omega0;

    /// <summary>Fractional bandwidth w = (omega_high - omega_low)/omega0, of the effective OUTER pair.</summary>
    [JsonIgnore]
    public double W => Effective.W;

    /// <summary>
    /// The prototype's inner band edge <c>a = (f3 - f2)/(f4 - f1)</c> — the half-width of the gap in
    /// the mapped variable (match.md §18.2). Zero for a single band, where the prototype has no gap.
    /// </summary>
    /// <remarks>
    /// <b>Dual-band only.</b> A tri-band prototype's passband is a UNION of intervals in u and no
    /// single number describes it (match.md §18.5); <see cref="EffectiveBands.Intervals"/> is what
    /// that path reads, and it reduces to <c>[A^2, 1]</c> here.
    /// </remarks>
    [JsonIgnore]
    public double A
    {
        get
        {
            if (BandCount != 2) return 0.0;
            var e = Effective;
            double span = e.F4 - e.F1;
            return span > 0 ? (e.F3 - e.F2) / span : 0.0;
        }
    }

    /// <summary>A deep copy — the rebuild and the solution search both mutate working copies.</summary>
    public MatchDesign Clone() => new()
    {
        Version = Version,
        F1 = F1,
        F2 = F2,
        BandCount = BandCount,
        F3 = F3,
        F4 = F4,
        F5 = F5,
        F6 = F6,
        Order = Order,
        Response = Response,
        Form = Form,
        RippleDb = RippleDb,
        Term1 = Term1,
        Term2 = Term2,
        AnalysisEnd = AnalysisEnd,
        QAdjust = QAdjust,
        AllowNegativeComponents = AllowNegativeComponents,
        LinkTransforms = LinkTransforms,
        Transforms = [.. Transforms],
        AppliedSolutions = [.. AppliedSolutions],
        BasisFingerprint = BasisFingerprint,
        PlotBandFraction = PlotBandFraction,
        PlotPoints = PlotPoints,
        Term1Conjugate = Term1Conjugate,
        Term2Conjugate = Term2Conjugate,
    };
}
