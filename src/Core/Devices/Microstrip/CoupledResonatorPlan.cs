using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>How each resonator of an <see cref="CoupledResonatorPlan"/> filter is laid out.</summary>
public enum ResonatorShape
{
    /// <summary>A straight strip in two halves, each coupled to a neighbour: the parallel (edge-) coupled filter.</summary>
    Straight,
    /// <summary>The same strip folded 90° at its middle, alternately right and left, so the filter runs in steps.</summary>
    ZigZag,
    /// <summary>The strip folded into a U, the arms <see cref="CoupledResonatorSpec.Sa"/> apart, alternately inverted.</summary>
    Hairpin,
    /// <summary>A square loop opened by <see cref="CoupledResonatorSpec.Gap"/>, the loops side by side
    /// (<see cref="LoopArrangement"/>).</summary>
    OpenLoop,
    /// <summary>One closed ring, L round, fed across a gap on either side.</summary>
    Ring,
}

/// <summary>A resonator's width along its length: one width, or a stepped-impedance resonator whose centre section is
/// <see cref="CoupledResonatorSpec.Wc"/> wide over <see cref="CoupledResonatorSpec.Uc"/> of its length.</summary>
public enum ResonatorProfile { Uniform, Sir }

/// <summary>How the ports reach the end resonators: a coupled strip beside each, or a line tapped onto each.</summary>
public enum ResonatorFeed { Coupled, Tapped }

/// <summary>What the chain is: a bandpass filter between its feeds, resonators beside a through line (bandstop), or
/// resonators cut into the through line (spurline).</summary>
public enum ResonatorForm { Bandpass, Bandstop, Spurline }

/// <summary>How open loops sit: in a row, or four in a square with resonators 1 and 4 cross-coupled.</summary>
public enum LoopArrangement { Inline, Cross }

/// <summary>What the circuit couples. <see cref="Pairs"/>: each strip to the one neighbour it is placed against, as an
/// MCLIN pair of one width. <see cref="Full"/>: every coupled region as one N-line section
/// (<see cref="PlanCoupledStrips"/>): the pairs with a width per strip, and for hairpins every arm side by side over the
/// same stretch, a resonator's own two arms included.</summary>
public enum ResonatorCoupling { Pairs, Full }

/// <summary>
/// The physical geometry of a coupled-resonator filter, as MCRC stores it: what a filter synthesis produces and the
/// plan draws (docs/design/rf-passives.md §3.15). Lengths in metres. <see cref="W"/> and <see cref="L"/> hold one value
/// per resonator, index 0 for resonator 1. <see cref="S"/> holds the gaps by number: S_k is resonator k's gap to the
/// resonator or feed before it (bandpass; S_(N+1) the output feed's), or to the through line (bandstop), or its slot
/// (spurline). A tapped feed has no S1 or S_(N+1). A gap stated as no S_k of its own is the common S.
/// </summary>
public sealed record CoupledResonatorSpec(
    int N, IReadOnlyList<double> W, IReadOnlyList<double> L, IReadOnlyDictionary<int, double> S,
    ResonatorShape Shape, ResonatorProfile Profile, ResonatorFeed Feed, ResonatorForm Form, LoopArrangement Arrangement,
    double Sa, double Gap, double Tap, double Wc, double Uc, double Sx, ResonatorCoupling Coupling = ResonatorCoupling.Pairs)
{
    /// <summary>The most resonators a chain takes.</summary>
    public const int MaxN = 20;

    /// <summary>The factory's fallbacks, the registry's defaults: a three-resonator 2.4 GHz edge-coupled bandpass filter
    /// on 20 mil RO4350B (10 % wide, 0.5 dB ripple).</summary>
    public const double DefaultW = 1.058e-3, DefaultL = 37.17e-3, DefaultSa = 2.2e-3, DefaultGap = 1.0e-3,
        DefaultTap = 6.0e-3, DefaultWc = 0.4e-3, DefaultUc = 0.3, DefaultSx = 0.5e-3;

    /// <summary>The common gap's fallback: the default design's inner gap.</summary>
    public const double DefaultS = 0.574e-3;

    /// <summary>The gap numbers this filter needs.</summary>
    public IEnumerable<int> GapNumbers => (Form, Shape, Feed) switch
    {
        (ResonatorForm.Bandstop or ResonatorForm.Spurline, _, _) => Enumerable.Range(1, N),
        (_, _, ResonatorFeed.Tapped) => Enumerable.Range(2, N - 1),
        _ => Enumerable.Range(1, N + 1),
    };

    /// <summary>
    /// The spec from named values: <paramref name="real"/> a length or count in SI, <paramref name="text"/> a name, each
    /// null when the part does not state it. W_k, L_k and S_k (W1, L2, S5, ...) fall back to W, L and S, so a part whose
    /// N, Shape or Feed asks for a gap it was not given (four cross-coupled loops take S5) still builds, on S. The factory
    /// and the PCell both read a part through this.
    /// </summary>
    public static CoupledResonatorSpec Read(Func<string, double?> real, Func<string, string?> text)
    {
        double count = real("N") ?? 3.0;
        if (!(count >= 1.0 && count <= MaxN && count == Math.Round(count)))
            throw Refusal("n-invalid", "N = {n} must be a whole number of resonators from 1 to {max}.",
                ("n", count), ("max", MaxN));
        int n = (int)count;
        double w = real("W") ?? DefaultW, l = real("L") ?? DefaultL;
        var widths = Enumerable.Range(1, n).Select(k => real($"W{k}") ?? w).ToArray();
        var lengths = Enumerable.Range(1, n).Select(k => real($"L{k}") ?? l).ToArray();
        double s = real("S") ?? DefaultS;
        var gaps = Enumerable.Range(1, n + 1).ToDictionary(k => k, k => real($"S{k}") ?? s);
        var spec = new CoupledResonatorSpec(n, widths, lengths, gaps,
            Named(text("Shape"), ResonatorShape.Straight), Named(text("Profile"), ResonatorProfile.Uniform),
            Named(text("Feed"), ResonatorFeed.Coupled), Named(text("Form"), ResonatorForm.Bandpass),
            Named(text("Arrangement"), LoopArrangement.Inline),
            real("Sa") ?? DefaultSa, real("Gap") ?? DefaultGap, real("Tap") ?? DefaultTap,
            real("Wc") ?? DefaultWc, real("Uc") ?? DefaultUc, real("Sx") ?? DefaultSx,
            Named(text("Coupling"), ResonatorCoupling.Pairs));
        spec.Check();
        return spec;
    }

    private static T Named<T>(string? name, T fallback) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        if (Enum.TryParse(name.Trim(), ignoreCase: true, out T v) && Enum.IsDefined(v)) return v;
        throw Refusal("name-unknown", "{what} = '{name}' is not one of {names}.", ("what", typeof(T).Name.Replace("Resonator", "")
            .Replace("Loop", "")), ("name", name), ("names", string.Join(", ", Enum.GetNames<T>())));
    }

    /// <summary>The combinations the plan cannot build, refused before anything is planned.</summary>
    private void Check()
    {
        void Positive(string name, double v)
        {
            if (!(v > 0)) throw Refusal("spec-not-positive", "{name} = {value} must be positive.", ("name", name), ("value", v));
        }
        for (int k = 1; k <= N; k++) { Positive($"W{k}", W[k - 1]); Positive($"L{k}", L[k - 1]); }
        foreach (int k in GapNumbers) Positive($"S{k}", S[k]);

        if (Form == ResonatorForm.Bandstop && Shape is not (ResonatorShape.ZigZag or ResonatorShape.Hairpin))
            throw Refusal("form-shape", "a bandstop's resonators are ZigZag (folded once, the first half beside the through "
              + "line) or Hairpin (one arm beside it); a {shape} resonator coupled along its whole half wave does not notch.",
                ("shape", Shape));
        if (Form == ResonatorForm.Spurline && Shape != ResonatorShape.Straight)
            throw Refusal("form-shape", "a spurline's resonator is a Straight strip cut into the through line, not {shape}.",
                ("shape", Shape));
        if (Form != ResonatorForm.Bandpass && Feed == ResonatorFeed.Tapped)
            throw Refusal("form-feed", "a {form} is the through line itself and has no tapped feed.", ("form", Form));
        if (Profile == ResonatorProfile.Sir && (Form != ResonatorForm.Bandpass
                                                 || Shape is ResonatorShape.OpenLoop or ResonatorShape.Ring))
            throw Refusal("profile-shape", "a stepped-impedance profile is for Straight, ZigZag and Hairpin bandpass "
              + "resonators, not a {form} {shape}.", ("form", Form), ("shape", Shape));
        if (Shape == ResonatorShape.Ring && (N != 1 || Feed == ResonatorFeed.Tapped))
            throw Refusal("ring", "a Ring is one resonator (N = 1, here {n}) fed across a gap on either side (a Coupled "
              + "feed, here {feed}).", ("n", N), ("feed", Feed));
        if (Shape == ResonatorShape.OpenLoop && Arrangement == LoopArrangement.Cross && N != 4)
            throw Refusal("cross-n", "the cross-coupled open loops are four resonators in a square; N = {n}.", ("n", N));

        if (Shape == ResonatorShape.Hairpin) Positive("Sa", Sa);
        if (Shape == ResonatorShape.OpenLoop) Positive("Gap", Gap);
        if (Shape == ResonatorShape.OpenLoop && Arrangement == LoopArrangement.Cross) Positive("Sx", Sx);
        if (Feed == ResonatorFeed.Tapped) Positive("Tap", Tap);
        if (Profile == ResonatorProfile.Sir)
        {
            Positive("Wc", Wc);
            if (!(Uc > 0 && Uc < 1))
                throw Refusal("sir-ratio", "Uc = {uc} is the centre section's share of each resonator's length and must lie "
                  + "between 0 and 1.", ("uc", Uc));
        }
    }

    internal static ParameterRefusalException Refusal(string code, string message, params (string, object?)[] args)
        => new(Diagnostic.Create("device.mcrc." + code, DiagnosticSeverity.Error,
            "Coupled-resonator filter: " + message, args));
}

/// <summary>
/// MCRC, one part for every coupled-resonator microstrip filter: N resonators and the gaps between them, as physical
/// geometry (<see cref="CoupledResonatorSpec"/>), planned from the shared sections and nothing else. Pure: a spec and a
/// substrate in, a <see cref="PassivePlan"/> out, which <see cref="MicrostripPlanModel"/> stamps and the PCell draws.
///
/// <para><b>The sections.</b> Every coupled length is an MCLIN pair (<see cref="PlanCoupledLine"/>), every other run an
/// MLIN (<see cref="PlanLine"/>), every fold a mitred 90° corner (<see cref="PlanBend"/> at MMITER's Douville-James
/// chamfer), a ring two 180° MCURVEs (<see cref="PlanCurve"/>), a tap an MTEE (<see cref="PlanTee"/>), a ring's feed and
/// an open loop's opening an MGAP (<see cref="PlanGap"/>), and every free end an open end (<see cref="PlanOpen"/>). A
/// stepped-impedance resonator's width steps where its centre section starts and ends, with no step model, as a ladder's
/// lines step.</para>
///
/// <para><b>What is coupled.</b> Each strip couples to its neighbour on one side only, the resonator or feed it is
/// placed against: the coupling the textbook design methods (Cohn's J-inverters, Cristal and Frankel's hairpins, Hong
/// and Lancaster's coupling coefficients) design with. Left out: a hairpin's own two arms; strips further than the
/// next one (a resonator two over, the diagonal of the cross-coupled square); the part of a longer neighbour that
/// runs past the shorter one, which is a plain line; and the corners, which couple to nothing. All of it is in an EM
/// run of the same artwork. <see cref="Builder.Couple"/> is the one place pairs are made. With
/// <see cref="ResonatorCoupling.Full"/> each pair is an N-line section (<see cref="PlanCoupledStrips"/>) of its own two
/// widths, and a hairpin's arms side by side are one section each stretch (<c>Builder.Group</c>), its own arms
/// included.</para>
///
/// <para><b>Lengths.</b> L is each resonator's centre-line length from open end to open end, its corners counted along
/// the centre line (2·arm each, <see cref="PlanBend.Arm"/>); a loop's L is its square's perimeter less Gap, a ring's its
/// circumference. A synthesis that wants an electrical length takes the corners' own phase from
/// <see cref="PlanBend.LineLength"/>. The open ends' extension is the model's, not subtracted from L.</para>
/// </summary>
public static class CoupledResonatorPlan
{
    /// <summary>The part's pins: input and output.</summary>
    public static readonly IReadOnlyList<string> Pins = ["1", "2"];

    public static PassivePlan Build(CoupledResonatorSpec spec, MicrostripSubstrate s)
    {
        bool full = spec.Coupling == ResonatorCoupling.Full;
        var b = new Builder(s) { Full = full, Pool = full && spec.Shape == ResonatorShape.Hairpin ? [] : null };
        switch (spec.Form, spec.Shape)
        {
            case (ResonatorForm.Bandstop, _): Bandstop(b, spec); break;
            case (ResonatorForm.Spurline, _): Spurline(b, spec); break;
            case (_, ResonatorShape.OpenLoop): Loops(b, spec); break;
            case (_, ResonatorShape.Ring): Ring(b, spec); break;
            default: Chain(b, spec); break;
        }
        return b.Plan();
    }

    private static double G(double v) => PassivePlan.OnGrid(v);

    // ── Straight, ZigZag and Hairpin bandpass: a chain of folded or unfolded strips ──────────────────────────────────

    /// <summary>
    /// Each resonator is walked from one open end to the other: its first end section (beside the resonator or feed
    /// before it), its centre (straight, one 90° corner, or a U of two), its second end section (beside the next). The
    /// next resonator's first end section is placed against this one's second, W + S off on the side away from the
    /// fold, so every pair is aligned by construction and each fold turns away from the strip it last coupled to. A
    /// stepped-impedance resonator's end sections are W wide and its centre Wc over Uc·L; a uniform one's centre is just
    /// its fold. A shorter neighbour sets a pair's coupled length; the longer end runs on past it as a plain line.
    /// </summary>
    private static void Chain(Builder b, CoupledResonatorSpec p)
    {
        int n = p.N;
        bool tapped = p.Feed == ResonatorFeed.Tapped, sir = p.Profile == ResonatorProfile.Sir;
        var w = p.W.Select(G).ToArray();
        var l = p.L.Select(G).ToArray();
        double CentreWidth(int k) => sir ? G(p.Wc) : w[k];
        // The fold each shape puts in a resonator's centre, along its centre line, and the first resonator's turn and
        // heading; later resonators alternate.
        double Fold(int k) => p.Shape switch
        {
            ResonatorShape.ZigZag => 2.0 * b.Arm(CentreWidth(k)),
            ResonatorShape.Hairpin => w[k] + G(p.Sa) + 2.0 * b.Arm(CentreWidth(k)),
            _ => 0.0,
        };
        int turn1 = p.Shape switch { ResonatorShape.ZigZag => -1, ResonatorShape.Hairpin => 1, _ => 0 };
        int q1 = p.Shape == ResonatorShape.Hairpin ? 3 : 0;

        var centre = new double[n];
        var end = new double[n + 2];
        for (int k = 0; k < n; k++)
        {
            b.Context = $"resonator {k + 1}";
            centre[k] = sir ? G(p.Uc * l[k]) : Fold(k);
            if (centre[k] < Fold(k) - 1e-12)
                throw CoupledResonatorSpec.Refusal("sir-centre", "resonator {k}'s centre section, Uc·L = {c} m, is shorter "
                  + "than its fold, {fold} m.", ("k", k + 1), ("c", centre[k]), ("fold", Fold(k)));
            end[k + 1] = G((l[k] - centre[k]) / 2.0);
            b.NotShort(end[k + 1], "end section");
        }
        end[0] = end[1];
        end[n + 1] = end[n];
        double Coupled(int j) => Math.Min(end[j - 1], end[j]);

        int Side(int turn) => turn == 0 ? -1 : -Math.Sign(turn);
        int feedSide = turn1 == 0 ? 1 : -Math.Sign(turn1);
        Stretch? previous = null;
        int previousSide = 0;
        int turn = turn1;
        for (int k = 1; k <= n; k++)
        {
            b.Context = $"resonator {k}";
            double wk = w[k - 1], over = end[k] - Coupled(k);
            Pen pen;
            if (previous is null) pen = new Pen(0.0, 0.0, q1, wk, b.Node());
            else
            {
                var beside = Builder.Aside(previous.At(wk), previousSide, (previous.W + wk) / 2.0 + G(p.S[k]));
                pen = Builder.Ahead(beside, -over) with { Node = b.Node() };
            }
            b.End(pen);

            Stretch? first = null;
            if (k == 1 && tapped) pen = b.Tapped(pen, end[1], l[0] / 2.0 - G(p.Tap), feedSide, 1);
            else
            {
                pen = b.Strip(pen, over);
                (pen, first) = b.Run(pen, Coupled(k));
            }

            // The centre: a step to Wc and back for a stepped-impedance resonator, and the shape's fold.
            double wc = CentreWidth(k - 1);
            var mid = pen with { W = wc };
            switch (p.Shape)
            {
                case ResonatorShape.ZigZag:
                    double half = (centre[k - 1] - Fold(k - 1)) / 2.0;
                    mid = b.Line(b.Corner(b.Line(mid, half), turn), half);
                    break;
                case ResonatorShape.Hairpin:
                    double arm = (centre[k - 1] - Fold(k - 1)) / 2.0, across = wk + G(p.Sa) - 2.0 * b.Arm(wc);
                    b.NotShort(across, "U-turn across Sa");
                    mid = b.Strip(b.Corner(b.Line(b.Corner(b.Strip(mid, arm), turn), across), turn), arm);
                    break;
                default:
                    mid = b.Line(mid, centre[k - 1]);
                    break;
            }
            pen = mid with { W = wk };

            Stretch? second = null;
            int side = Side(turn);
            if (k == n && tapped) pen = b.Tapped(pen, end[n], l[n - 1] / 2.0 + G(p.Tap) - (l[n - 1] - end[n]), side, 2);
            else
            {
                (pen, second) = b.Run(pen, Coupled(k + 1));
                pen = b.Strip(pen, end[k] - Coupled(k + 1));
            }
            b.End(pen);

            if (k == 1 && !tapped)
            {
                b.Context = "input feed";
                var feed = Builder.Aside(first!.At(wk), feedSide, wk + G(p.S[1])) with { Node = 1 };
                b.Pin1 = feed;
                var (feedEnd, feedRun) = b.Run(feed, Coupled(1));
                b.End(feedEnd);
                b.Couple(feedRun, first, G(p.S[1]));
            }
            if (previous is not null) b.Couple(previous, first!, G(p.S[k]));
            if (k == n && !tapped)
            {
                b.Context = "output feed";
                var feed = Builder.Aside(second!.At(wk), side, wk + G(p.S[n + 1])) with { Node = b.Node() };
                b.End(feed);
                var (_, feedRun) = b.Run(feed, Coupled(n + 1), endNode: 2);
                b.Couple(second, feedRun, G(p.S[n + 1]));
            }
            previous = second;
            previousSide = side;
            turn = -turn;
        }
    }

    // ── Open loops ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One straight piece of a loop's centre line: where it starts, its heading (quarter turns from +x), its
    /// length, and its distance from the loop's start along the centre line.</summary>
    private sealed record Leg(double X, double Y, int Q, double Length, double S0);

    /// <summary>
    /// Square loops, each opened by an MGAP of Gap in the middle of one side and walked counter-clockwise from that gap:
    /// half a side, a corner, three sides with a corner after each, and half a side back to the gap. A loop's midpoint
    /// along its length is the middle of the side opposite its opening. Two loops couple where a straight side of one
    /// runs beside a straight side of the other W + S away, over the length the two overlap. Inline: the loops in a
    /// row, opened alternately at the top and the bottom, so each couples to the next through its right side with
    /// electric and magnetic coupling adding (mixed coupling); opened on the same side, the two cancel, exactly for TEM
    /// lines, since the facing sides then carry opposite voltages and equal currents at every point. Cross (Hong and
    /// Lancaster's four-pole): 1 top left, 2 below it, 3 beside 2, 4 beside 1; 1 and 4 open on the sides facing each
    /// other (electric coupling, across Sx), 2 and 3 on the sides away from each other (magnetic, across S3), and 1-2 and
    /// 3-4 couple through adjacent sides (mixed, across S2 and S4). The feeds are on the first loop's left side and the
    /// last loop's right side; a tap is Tap along the loop from its midpoint.
    /// </summary>
    private static void Loops(Builder b, CoupledResonatorSpec p)
    {
        int n = p.N;
        bool cross = p.Arrangement == LoopArrangement.Cross, tapped = p.Feed == ResonatorFeed.Tapped;
        var w = p.W.Select(G).ToArray();
        double gap = G(p.Gap);
        var side = p.L.Select(l => G((l + gap) / 4.0)).ToArray();
        var cx = new double[n];
        var cy = new double[n];
        var open = new int[n];   // the opened side: 0 top, 1 left, 2 bottom, 3 right (counter-clockwise from the top)
        var pairs = new List<(int A, int B, double Gap)>();
        if (!cross)
        {
            for (int k = 0; k < n; k++) open[k] = k % 2 == 0 ? 0 : 2;
            for (int k = 1; k < n; k++)
            {
                double sk = G(p.S[k + 1]);
                cx[k] = cx[k - 1] + side[k - 1] / 2.0 + (w[k - 1] + w[k]) / 2.0 + sk + side[k] / 2.0;
                pairs.Add((k - 1, k, sk));
            }
        }
        else
        {
            // Each pair of loops is centred on an axis; one centre of each is put on the grid and its partner placed from
            // it exactly, so the facing sides are W + S apart to the last unit.
            double s2 = G(p.S[2]), s3 = G(p.S[3]), s4 = G(p.S[4]), sx = G(p.Sx);
            double c12 = (w[0] + w[1]) / 2.0 + s2, c23 = (w[1] + w[2]) / 2.0 + s3;
            double c34 = (w[2] + w[3]) / 2.0 + s4, c14 = (w[0] + w[3]) / 2.0 + sx;
            cy[0] = G(side[0] / 2.0 + c12 / 2.0);
            cy[1] = cy[0] - side[0] / 2.0 - c12 - side[1] / 2.0;
            cy[3] = G(side[3] / 2.0 + c34 / 2.0);
            cy[2] = cy[3] - side[3] / 2.0 - c34 - side[2] / 2.0;
            cx[0] = -G(side[0] / 2.0 + c14 / 2.0);
            cx[3] = cx[0] + side[0] / 2.0 + c14 + side[3] / 2.0;
            cx[1] = -G(side[1] / 2.0 + c23 / 2.0);
            cx[2] = cx[1] + side[1] / 2.0 + c23 + side[2] / 2.0;
            open[0] = 3; open[1] = 1; open[2] = 3; open[3] = 1;
            pairs.AddRange([(0, 1, s2), (1, 2, s3), (2, 3, s4), (0, 3, sx)]);
        }

        // Each loop's legs, in walk order: the opened side's later half, three whole sides, the opened side's first half.
        var legs = new Leg[n][];
        for (int k = 0; k < n; k++)
        {
            b.Context = $"loop {k + 1}";
            double a = side[k], arm = b.Arm(w[k]), halfLeg = a / 2.0 - gap / 2.0 - arm, full = a - 2.0 * arm;
            b.NotShort(halfLeg, "half side beside Gap");
            b.NotShort(full, "side");
            (double X, double Y) Corner(int c) => c switch   // 0 top right, 1 top left, 2 bottom left, 3 bottom right
            {
                0 => (cx[k] + a / 2.0, cy[k] + a / 2.0), 1 => (cx[k] - a / 2.0, cy[k] + a / 2.0),
                2 => (cx[k] - a / 2.0, cy[k] - a / 2.0), _ => (cx[k] + a / 2.0, cy[k] - a / 2.0),
            };
            var list = new List<Leg>();
            int o = open[k];
            var (ox, oy) = Corner(o);
            var (ex, ey) = Corner((o + 1) & 3);
            double mx = (ox + ex) / 2.0, my = (oy + ey) / 2.0;
            var heading = Builder.Dir((o + 2) & 3);
            double s0 = 0.0;
            list.Add(new Leg(mx + heading.X * gap / 2.0, my + heading.Y * gap / 2.0, (o + 2) & 3, halfLeg, s0));
            s0 += halfLeg + 2.0 * arm;
            for (int j = 1; j <= 3; j++)
            {
                var (sx0, sy0) = Corner((o + j) & 3);
                var d = Builder.Dir((o + j + 2) & 3);
                list.Add(new Leg(sx0 + d.X * arm, sy0 + d.Y * arm, (o + j + 2) & 3, full, s0));
                s0 += full + 2.0 * arm;
            }
            list.Add(new Leg(ox + heading.X * arm, oy + heading.Y * arm, (o + 2) & 3, halfLeg, s0));
            legs[k] = [.. list];
        }

        // Where each pair of loops faces: (loop, leg) → the stretches of it that are coupled, each with its pair's id.
        var cuts = new Dictionary<(int Loop, int Leg), List<(double From, double To, int Id)>>();
        var runs = new Dictionary<int, List<Stretch>>();
        var pairGap = new List<double>();
        void Cut(int loop, int leg, double from, double to, int id)
        {
            if (!cuts.TryGetValue((loop, leg), out var list)) cuts[(loop, leg)] = list = [];
            list.Add((from, to, id));
        }
        // How far along leg g lies the point (x, y).
        static double Along(Leg g, double x, double y) { var d = Builder.Dir(g.Q); return (x - g.X) * d.X + (y - g.Y) * d.Y; }
        foreach (var (pa, pb, g) in pairs)
        {
            int found = 0;
            for (int i = 0; i < legs[pa].Length; i++)
                for (int j = 0; j < legs[pb].Length; j++)
                {
                    Leg u = legs[pa][i], v = legs[pb][j];
                    if (((u.Q - v.Q) & 1) != 0) continue;
                    var d = Builder.Dir(u.Q);
                    double lateral = Math.Abs(-(v.X - u.X) * d.Y + (v.Y - u.Y) * d.X);
                    if (Math.Abs(lateral - ((w[pa] + w[pb]) / 2.0 + g)) > 2e-9) continue;
                    double v0 = Along(u, v.X, v.Y), v1 = v0 + (u.Q == v.Q ? v.Length : -v.Length);
                    double from = Math.Max(0.0, Math.Min(v0, v1)), to = Math.Min(u.Length, Math.Max(v0, v1));
                    if (to - from <= 1e-9) continue;
                    int id = pairGap.Count;
                    pairGap.Add(g);
                    Cut(pa, i, from, to, id);
                    var (px, py) = (u.X + d.X * from, u.Y + d.Y * from);
                    var (qx, qy) = (u.X + d.X * to, u.Y + d.Y * to);
                    double f2 = Along(v, px, py), t2 = Along(v, qx, qy);
                    Cut(pb, j, Math.Min(f2, t2), Math.Max(f2, t2), id);
                    found++;
                }
            if (found == 0)
                throw CoupledResonatorSpec.Refusal("no-overlap", "loops {a} and {b} have no sides facing each other {g} m "
                  + "apart: the loops are too different in size for where they sit.", ("a", pa + 1), ("b", pb + 1), ("g", g));
        }

        // The feeds: a strip along the first loop's left side and the last loop's right side, or a tap on each loop.
        int last = cross ? 3 : n - 1;
        int LegOn(int loop, int sideIndex) => (sideIndex - open[loop] + 4) % 4;   // 0: the opened side
        Leg? feedIn = null, feedOut = null;
        int idIn = -1, idOut = -1;
        if (!tapped)
        {
            foreach (var (loop, sideIndex, gk, isIn) in new[] { (0, 1, G(p.S[1]), true), (last, 3, G(p.S[n + 1]), false) })
            {
                int j = LegOn(loop, sideIndex);
                if (j == 0) throw new InvalidOperationException("A loop is fed on its opened side.");
                var leg = legs[loop][j];
                int id = pairGap.Count;
                pairGap.Add(gk);
                Cut(loop, j, 0.0, leg.Length, id);
                var beside = Builder.Aside(new Pen(leg.X, leg.Y, leg.Q, w[loop], 0), -1, w[loop] + gk);
                var strip = new Leg(beside.X, beside.Y, leg.Q, leg.Length, 0.0);
                if (isIn) (feedIn, idIn) = (strip, id); else (feedOut, idOut) = (strip, id);
            }
        }

        // Walk each loop, cutting each leg into plain lines and coupled runs, with the taps on the first and last loop
        // (both on one loop when there is only one).
        for (int k = 0; k < n; k++)
        {
            b.Context = $"loop {k + 1}";
            double middle = (legs[k][^1].S0 + legs[k][^1].Length) / 2.0;
            var taps = new List<(double S, int Pin)>();
            if (tapped && k == 0) taps.Add((middle - G(p.Tap), 1));
            if (tapped && k == last) taps.Add((middle + G(p.Tap), 2));
            int start = b.Node();
            var pen = new Pen(legs[k][0].X, legs[k][0].Y, legs[k][0].Q, w[k], start);
            for (int j = 0; j < legs[k].Length; j++)
            {
                var leg = legs[k][j];
                if (j > 0) pen = b.Corner(pen, 1);
                double at = 0.0;
                foreach (var (from, to, id) in (cuts.GetValueOrDefault((k, j)) ?? []).OrderBy(c => c.From))
                {
                    pen = Free(pen, from - at, leg.S0 + at);
                    (pen, var r) = b.Run(pen, to - from);
                    if (!runs.TryGetValue(id, out var list)) runs[id] = list = [];
                    list.Add(r);
                    at = to;
                }
                pen = Free(pen, leg.Length - at, leg.S0 + at);
            }
            b.Gap(pen, gap, start, ends: true);
            if (taps.Count > 0)
                throw CoupledResonatorSpec.Refusal("tap-off-line", "Tap = {tap} m puts loop {k}'s tap on a corner or a "
                  + "coupled side; it must land on a side that couples to nothing, half a width clear of its ends.",
                    ("tap", p.Tap), ("k", k + 1));

            // A free stretch of length len starting s0 along the loop, with any tap that falls in it: the stretch up to
            // just past the tee, then the rest.
            Pen Free(Pen at, double len, double s0)
            {
                double h = at.W / 2.0;
                int i = taps.FindIndex(t => t.S - s0 >= h - 1e-12 && s0 + len - t.S >= h - 1e-12);
                if (i < 0) return b.Line(at, len);
                var (ts, pin) = taps[i];
                taps.RemoveAt(i);
                double upTo = G(ts - s0) + h;
                return Free(b.Tapped(at, upTo, G(ts - s0), -1, pin), len - upTo, s0 + upTo);
            }
        }

        if (feedIn is { } fi)
        {
            b.Context = "input feed";
            var feed = new Pen(fi.X, fi.Y, fi.Q, w[0], 1);
            b.Pin1 = feed;
            var (e, r) = b.Run(feed, fi.Length);
            b.End(e);
            runs[idIn].Add(r);
        }
        if (feedOut is { } fo)
        {
            b.Context = "output feed";
            var feed = new Pen(fo.X, fo.Y, fo.Q, w[last], b.Node());
            b.End(feed);
            runs[idOut].Add(b.Run(feed, fo.Length, endNode: 2).Run);
        }
        foreach (var (id, list) in runs)
            b.Couple(list[0], list[1], pairGap[id]);
    }

    // ── Ring ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One ring of centre-line circumference L, two 180° MCURVEs, fed at its leftmost and rightmost points
    /// across an MGAP of S1 and S2 from the end of a feed line one width long, the pin at its outer end.</summary>
    private static void Ring(Builder b, CoupledResonatorSpec p)
    {
        b.Context = "ring";
        double w = G(p.W[0]), r = G(p.L[0] / (2.0 * Math.PI)), s1 = G(p.S[1]), s2 = G(p.S[2]);
        int left = b.Node(), right = b.Node();
        var feed = new Pen(-r - w / 2.0 - s1 - w, 0.0, 0, w, 1);
        b.Pin1 = feed;
        b.Gap(b.Line(feed, w), s1, left, ends: false);
        b.Sections.Add(new PlanCurve(w, 180.0, r, [left, right], -r, 0.0, 270.0));
        b.Sections.Add(new PlanCurve(w, 180.0, r, [right, left], r, 0.0, 90.0));
        int outer = b.Node();
        b.Gap(new Pen(r + w / 2.0, 0.0, 0, w, right), s2, outer, ends: false);
        b.Line(new Pen(r + w / 2.0 + s2, 0.0, 0, w, outer), w, end: 2);
    }

    // ── Bandstop ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A through line from pin 1 to pin 2, and beside it, alternately above and below, each resonator's first stretch
    /// coupled to it across S_k, back to back along the line: half the resonator, folded away from the line by one corner
    /// (ZigZag), or one arm, the U turning away (Hairpin). Each through-line stretch is as wide as the resonator beside it.
    /// </summary>
    private static void Bandstop(Builder b, CoupledResonatorSpec p)
    {
        double x = 0.0;
        int along = 1;
        for (int k = 1; k <= p.N; k++)
        {
            b.Context = $"resonator {k}";
            double wk = G(p.W[k - 1]), lk = G(p.L[k - 1]), sk = G(p.S[k]), arm = b.Arm(wk);
            int up = k % 2 == 1 ? 1 : -1;
            double across = wk + G(p.Sa) - 2.0 * arm;
            double coupled = p.Shape == ResonatorShape.Hairpin ? G((lk - (wk + G(p.Sa) + 2.0 * arm)) / 2.0) : G(lk / 2.0 - arm);
            b.NotShort(coupled, "coupled stretch");
            var pen = new Pen(x, up * (wk + sk), 0, wk, b.Node());
            b.End(pen);
            (pen, var resonator) = b.Run(pen, coupled);
            pen = b.Corner(pen, up);
            if (p.Shape == ResonatorShape.Hairpin)
            {
                b.NotShort(across, "U-turn across Sa");
                pen = b.Strip(b.Corner(b.Line(pen, across), up), coupled);
            }
            else pen = b.Line(pen, lk - coupled - 2.0 * arm);
            b.End(pen);

            var through = new Pen(x, 0.0, 0, wk, along);
            if (k == 1) b.Pin1 = through;
            var (next, line) = b.Run(through, coupled, endNode: k == p.N ? 2 : null);
            along = next.Node;
            b.Couple(line, resonator, sk);
            x += coupled;
        }
    }

    // ── Spurline ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The through line, 2·W + S wide, with each resonator cut into it by an L-shaped slot S wide (Bates): for each, S of
    /// the full-width line, then the line split lengthwise into the through strip and the spur, an MCLIN pair L long
    /// joined at its near end, the spur's far end open across the slot's short leg; the through strip runs on S past
    /// it, then the full-width line again. The through strip keeps its outer edge, so the line's edge on that side is
    /// straight.
    /// </summary>
    private static void Spurline(Builder b, CoupledResonatorSpec p)
    {
        double x = 0.0;
        int node = 1;
        double w = 0.0, s = 0.0;
        for (int k = 1; k <= p.N; k++)
        {
            b.Context = $"spur {k}";
            w = G(p.W[k - 1]);
            s = G(p.S[k]);
            double l = G(p.L[k - 1]);
            var wide = new Pen(x, -(w + s) / 2.0, 0, 2.0 * w + s, node);
            if (k == 1) b.Pin1 = wide;
            int join = b.Line(wide, s).Node;
            var (thru, a) = b.Run(new Pen(x + s, 0.0, 0, w, join), l);
            var (spur, bRun) = b.Run(new Pen(x + s, -(w + s), 0, w, join), l);
            b.End(spur);
            b.Couple(a, bRun, s);
            node = b.Line(thru, s).Node;
            x += l + 2.0 * s;
        }
        b.Line(new Pen(x, -(w + s) / 2.0, 0, 2.0 * w + s, node), s, end: 2);
    }

    // ── The builder ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Where a strip is being walked: its point on the centre line, its heading (quarter turns from +x), its
    /// width and the plan node it stands on.</summary>
    private readonly record struct Pen(double X, double Y, int Q, double W, int Node);

    /// <summary>A straight stretch of a strip that couples to a neighbour, held until both are walked.</summary>
    private sealed record Stretch(int Start, int End, double X, double Y, int Q, double W, double L)
    {
        public Pen At(double w) => new(X, Y, Q, w, Start);
    }

    private sealed class Builder(MicrostripSubstrate s)
    {
        public readonly List<PlanSection> Sections = [];
        private readonly List<int> _ends = [];
        private int _next = 3;
        public string Context = "";
        public Pen Pin1;
        /// <summary>Coupling = Full: pairs as N-line sections, unequal widths allowed.</summary>
        public bool Full;
        /// <summary>Coupling = Full on hairpins: every straight stretch along the arms, grouped side by side at the end
        /// (<see cref="Group"/>) rather than paired as it is walked.</summary>
        public List<Stretch>? Pool;

        public int Node() => _next++;

        public static (double X, double Y) Dir(int q) => (q & 3) switch { 0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1) };

        public static Pen Ahead(Pen p, double l) { var (dx, dy) = Dir(p.Q); return p with { X = p.X + dx * l, Y = p.Y + dy * l }; }

        /// <summary>The pen moved <paramref name="d"/> to its left (<paramref name="side"/> +1) or right (−1).</summary>
        public static Pen Aside(Pen p, int side, double d)
        {
            var (dx, dy) = Dir(p.Q);
            return p with { X = p.X - side * dy * d, Y = p.Y + side * dx * d };
        }

        private PlanBend Bend(double w, int turn)
            => new([0, 0], 0.0, 0.0, 0.0, w, 90.0 * turn, PlanBend.MiterFor(PlanCorner.Miter, w, s));

        /// <summary>A mitred corner's pin-to-corner distance at width <paramref name="w"/>.</summary>
        public double Arm(double w) => Bend(w, 1).Arm;

        public void NotShort(double length, string what)
        {
            if (length < -1e-12)
                throw CoupledResonatorSpec.Refusal("too-short", "{where}'s {what} comes out {l} m long: L is too short for "
                  + "its folds, or Sa or Gap too wide.", ("where", Context), ("what", what), ("l", length));
        }

        public Pen Line(Pen p, double l, int? end = null)
        {
            NotShort(l, "line");
            if (l <= 1e-12 && end is null) return p;
            int n = end ?? Node();
            Sections.Add(new PlanLine([p.Node, n], p.X, p.Y, 90.0 * p.Q, p.W, l));
            return Ahead(p, l) with { Node = n };
        }

        /// <summary>A straight stretch along a resonator's coupled direction (an arm, an end, a feed): a line, or with
        /// <see cref="Pool"/> a stretch held for <see cref="Group"/>.</summary>
        public Pen Strip(Pen p, double l)
        {
            if (Pool is null || l <= 1e-12) return Line(p, l);
            return Run(p, l).End;
        }

        public Pen Corner(Pen p, int turn)
        {
            int n = Node();
            var bend = Bend(p.W, turn) with { Nodes = [p.Node, n], X = p.X, Y = p.Y, RotationDeg = 90.0 * p.Q };
            Sections.Add(bend);
            return Ahead(Ahead(p, bend.Arm) with { Q = (p.Q + turn) & 3 }, bend.Arm) with { Node = n };
        }

        public (Pen End, Stretch Run) Run(Pen p, double l, int? endNode = null)
        {
            if (l <= 1e-9)
                throw CoupledResonatorSpec.Refusal("too-short", "{where} has no length left to couple ({l} m).",
                    ("where", Context), ("l", l));
            int n = endNode ?? Node();
            var run = new Stretch(p.Node, n, p.X, p.Y, p.Q, p.W, l);
            Pool?.Add(run);
            return (Ahead(p, l) with { Node = n }, run);
        }

        /// <summary>A free stretch of <paramref name="len"/> with an MTEE at <paramref name="t"/> along it, its branch
        /// leaving to the <paramref name="side"/> (+1 left, −1 right) for pin <paramref name="pin"/>: through arms half the
        /// width each side of the junction, the branch one width from it.</summary>
        public Pen Tapped(Pen p, double len, double t, int side, int pin)
        {
            double w = p.W;
            if (t < w / 2.0 - 1e-12 || len - t < w / 2.0 - 1e-12)
                throw CoupledResonatorSpec.Refusal("tap-off-line", "{where}'s tap falls {t} m into a {len} m stretch that "
                  + "couples to nothing; Tap must put it half a width clear of both ends.", ("where", Context), ("t", t),
                    ("len", len));
            double before = t - w / 2.0;
            p = Strip(p, before);
            int n = Node();
            var far = Ahead(p, w);
            // MTEE's branch leaves to the right of its through line; turned half round, to the left.
            Sections.Add(side < 0
                ? new PlanTee(w, w, w, w / 2.0, w / 2.0, w, [p.Node, n, pin], p.X, p.Y, 90.0 * p.Q)
                : new PlanTee(w, w, w, w / 2.0, w / 2.0, w, [n, p.Node, pin], far.X, far.Y, 90.0 * ((p.Q + 2) & 3)));
            if (pin == 1) Pin1 = Aside(Ahead(p, w / 2.0), side, w);
            return Strip(far with { Node = n }, Math.Max(0.0, len - before - w));
        }

        /// <summary>An open end where the pen stands.</summary>
        public void End(Pen p)
        {
            Sections.Add(new PlanOpen([p.Node], p.X, p.Y, 90.0 * p.Q, p.W));
            _ends.Add(p.Node);
        }

        /// <summary>An MGAP of <paramref name="gap"/> from the pen's line end to node <paramref name="to"/> ahead; with
        /// <paramref name="ends"/> its two nodes are line ends with nothing else on them (a loop's opening), numbered as
        /// open ends are.</summary>
        public void Gap(Pen p, double gap, int to, bool ends)
        {
            Sections.Add(new PlanGap([p.Node, to], p.X, p.Y, 90.0 * p.Q, p.W, gap));
            if (ends) _ends.AddRange([p.Node, to]);
        }

        /// <summary>
        /// Two runs side by side as one coupled section: the run with the other on its right is its first strip, the
        /// other's end level with its start the second strip's near end. Pairs: an MCLIN pair (<see cref="PlanCoupledLine"/>),
        /// so the two take one width. Full: an N-line section of the two (<see cref="PlanCoupledStrips"/>), each strip its
        /// own width. With <see cref="Pool"/> (Full hairpins) nothing here: <see cref="Group"/> places every stretch.
        ///
        /// <para>ponytail: only neighbours. Full adds a hairpin's own arms; strips further over in a straight or zig-zag
        /// chain, a loop's other neighbours (the cross square's diagonal) and the bandstop's resonators across the through
        /// line stay out, as listed in docs/design/rf-passives.md.</para>
        /// </summary>
        public void Couple(Stretch a, Stretch b, double gap)
        {
            if (Pool is not null) return;
            if (!Full && Math.Abs(a.W - b.W) > 1e-12)
                throw CoupledResonatorSpec.Refusal("unequal-pair", "two neighbours {wa} m and {wb} m wide are coupled side by "
                  + "side; a pair of unequal strips needs the N-strip coupled section, so give neighbours one W.",
                    ("wa", a.W), ("wb", b.W));
            double Left(Stretch from, Stretch to) { var (dx, dy) = Dir(from.Q); return -(to.X - from.X) * dy + (to.Y - from.Y) * dx; }
            if (Left(a, b) > 0) (a, b) = (b, a);
            var (ux, uy) = Dir(a.Q);
            bool same = a.Q == b.Q;
            var (nx, ny) = same ? (b.X, b.Y) : (b.X + Dir(b.Q).X * b.L, b.Y + Dir(b.Q).Y * b.L);
            double along = (nx - a.X) * ux + (ny - a.Y) * uy;
            if (((a.Q - b.Q) & 1) != 0 || Math.Abs(Left(a, b) + (a.W + b.W) / 2.0 + gap) > 2e-9 || Math.Abs(along) > 2e-9
                || Math.Abs(a.L - b.L) > 2e-9)
                throw new InvalidOperationException($"{Context}: the coupled runs do not lie side by side, {gap} m apart.");
            int[] nodes = [a.Start, a.End, same ? b.Start : b.End, same ? b.End : b.Start];
            Sections.Add(Full
                ? new PlanCoupledStrips(nodes, a.X, a.Y, 90.0 * a.Q, [a.W, b.W], [gap], a.L)
                : new PlanCoupledLine(nodes, a.X, a.Y, 90.0 * a.Q, a.W, gap, a.L));
        }

        /// <summary>
        /// Full coupling for hairpins: every pooled stretch is cut wherever a parallel one starts or ends, and the pieces
        /// that span the same stretch of the axis are one N-line section, side by side in order across it, the gaps the
        /// spaces between them; a piece alone is a line. Aligned hairpins come out as one section of every arm (feeds
        /// included); staggered stepped-impedance ones as a section of every wide end and one of each row's narrow
        /// arms; a tap's tee cuts the arms beside it, and couples to nothing.
        /// </summary>
        private void Group()
        {
            static long Nm(double v) => (long)Math.Round(v * 1e9);
            foreach (int axis in new[] { 0, 1 })
            {
                double A(double x, double y) => axis == 0 ? x : y;
                double C(double x, double y) => axis == 0 ? y : x;
                var along = Pool!.Where(t => (t.Q & 1) == axis).ToList();
                var cuts = along.SelectMany(t => new[] { A(t.X, t.Y), A(t.X, t.Y) + (t.Q < 2 ? t.L : -t.L) })
                    .DistinctBy(Nm).Order().ToList();
                var pieces = new List<(double Lo, double Hi, double C, double W, int Near, int Far)>();
                foreach (var t in along)
                {
                    bool forward = t.Q < 2;   // heading +x or +y
                    double lo = forward ? A(t.X, t.Y) : A(t.X, t.Y) - t.L, hi = lo + t.L;
                    var at = cuts.Where(c => c > lo + 1e-12 && c < hi - 1e-12).ToList();
                    var points = new List<double> { lo };
                    points.AddRange(at);
                    points.Add(hi);
                    var nodes = new List<int> { forward ? t.Start : t.End };
                    nodes.AddRange(at.Select(_ => Node()));
                    nodes.Add(forward ? t.End : t.Start);
                    for (int i = 0; i + 1 < points.Count; i++)
                        pieces.Add((points[i], points[i + 1], C(t.X, t.Y), t.W, nodes[i], nodes[i + 1]));
                }
                // Strips stack to the right of the section's heading: +x heads with the top strip first, +y with the left.
                foreach (var g in pieces.GroupBy(x => (Nm(x.Lo), Nm(x.Hi))))
                {
                    var row = (axis == 0 ? g.OrderByDescending(x => x.C) : g.OrderBy(x => x.C)).ToList();
                    var first = row[0];
                    double px = axis == 0 ? first.Lo : first.C, py = axis == 0 ? first.C : first.Lo, rot = 90.0 * axis;
                    if (row.Count == 1)
                    {
                        Sections.Add(new PlanLine([first.Near, first.Far], px, py, rot, first.W, first.Hi - first.Lo));
                        continue;
                    }
                    var gaps = row.Zip(row.Skip(1), (u, v) => Math.Abs(v.C - u.C) - (u.W + v.W) / 2.0).ToList();
                    if (gaps.Any(x => x <= 1e-12))
                        throw new InvalidOperationException($"Coupled strips overlap: gaps {string.Join(", ", gaps)} m.");
                    Sections.Add(new PlanCoupledStrips([.. row.SelectMany(x => new[] { x.Near, x.Far })], px, py, rot,
                        [.. row.Select(x => x.W)], gaps, first.Hi - first.Lo));
                }
            }
        }

        /// <summary>The plan, its open ends numbered first of the internal nodes (eliminated last: a resonator's end has a
        /// near-zero self admittance at resonance and the local block does not pivot), and moved so pin 1 is at the
        /// origin.</summary>
        public PassivePlan Plan()
        {
            if (Pool is not null) Group();
            var map = new int[_next];
            map[1] = 1;
            map[2] = 2;
            int next = 3;
            foreach (int n in _ends.Distinct().Where(n => n > 2)) map[n] = next++;
            for (int n = 3; n < _next; n++) if (map[n] == 0) map[n] = next++;
            var sections = Sections.Select(x => x with
            {
                Nodes = [.. x.Nodes.Select(n => map[n])], X = x.X - Pin1.X, Y = x.Y - Pin1.Y,
            }).ToList();
            return new PassivePlan(Pins, sections, _next - 1);
        }
    }
}
