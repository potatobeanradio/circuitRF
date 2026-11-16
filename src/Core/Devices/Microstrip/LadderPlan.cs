using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Core.Expressions;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>One section of a microstrip ladder (MLAD, <see cref="LadderPlan"/>): physical geometry, in metres.</summary>
public abstract record LadderSection;

/// <summary>A series line, W wide and L long.</summary>
public sealed record LadderLine(double W, double L) : LadderSection;

/// <summary>A series end-to-end gap S, between two ends of the width the ladder has there.</summary>
public sealed record LadderGap(double S) : LadderSection;

/// <summary>A series linear taper from W1 to W2 over L.</summary>
public sealed record LadderTaper(double W1, double W2, double L) : LadderSection;

/// <summary>A shunt stub at a junction: W wide and L long from the through line's edge to its end, the end open or
/// shorted (a pin for a via to ground), on one side of the line or both (a cross, an arm of W and L each side).</summary>
public sealed record LadderStub(double W, double L, LadderStubEnd End = LadderStubEnd.Open,
    LadderStubSide Side = LadderStubSide.Up) : LadderSection;

/// <summary>How a stub ends: in the open, or on a pin for a via to ground.</summary>
public enum LadderStubEnd { Open, Short }

/// <summary>Which side of the through line a stub leaves on: +y (<see cref="Up"/>), −y, or both.</summary>
public enum LadderStubSide { Up, Down, Both }

/// <summary>What one section is at F, for the readout.</summary>
/// <param name="Z">A line's or stub's MLIN impedance; a taper's at its W1 end; 0 for a gap.</param>
/// <param name="Z2">A taper's impedance at its W2 end; otherwise <paramref name="Z"/>.</param>
/// <param name="ThetaDeg">Electrical length as drawn: a line's, a taper's (its widths' phase per metre over L) or a
/// stub arm's from the through line's edge; 0 for a gap.</param>
/// <param name="OpenEndDeg">An open stub's end extension (<see cref="MicrostripOpenEnd"/>), as degrees of that stub.</param>
/// <param name="SeriesF">A gap's series capacitance (MGAP's Cg).</param>
/// <param name="ShuntF">A gap's shunt capacitance at each end (MGAP's Cp).</param>
/// <param name="WAt">The width the ladder has at a gap or at a stub's junction.</param>
public sealed record LadderSectionAtF(double Z, double Z2, double ThetaDeg, double OpenEndDeg, double SeriesF,
    double ShuntF, double WAt);

/// <summary>A ladder on one substrate: its sections, what each is at F, and the plan the model stamps and the PCell
/// draws.</summary>
/// <param name="W0Meters">The width of the ports' lines, Z0 at F: what a gap or a stub at either end sits on.</param>
public sealed record LadderDesign(double Z0, double FreqHz, double W0Meters, IReadOnlyList<LadderSection> Sections,
    IReadOnlyList<LadderSectionAtF> AtF, PassivePlan Plan);

/// <summary>
/// The microstrip ladder (MLAD): an ordered list of series lines, gaps and tapers and shunt stubs along one through
/// line, from pin 1 to pin 2. It stores physical geometry, so a filter synthesis or a designer writes the list and the
/// plan builds it as it stands. Pure: the list, Z0, F and a substrate in, <see cref="LadderDesign"/> out.
///
/// <para><b>The list</b> (<see cref="Parse"/>, <see cref="Format"/>): items separated by <c>;</c>, each
/// <c>kind(field=value, …)</c>; kinds and fields in any case, whitespace anywhere. A length is a number in metres or a
/// number with a length unit glued on (<c>mm</c>, <c>um</c>, <c>nm</c>, <c>cm</c>, <c>mil</c>, <c>in</c>):</para>
/// <code>
///   line(W=, L=)                                  a series line
///   gap(S=)                                       a series gap, at the width the ladder has there
///   taper(W1=, W2=, L=)                           a series linear taper
///   stub(W=, L=, end=open|short, side=up|down|both)   a shunt stub; end open and side up unless stated
/// </code>
/// <para>A new kind is a new word, so a stored list never changes meaning when one is added. The width "there" starts as
/// Z0's on the substrate at F (<see cref="LadderDesign.W0Meters"/>): the ports' own lines, which a gap or a stub at an
/// end sits on; past a line or a taper it is that section's (W2). The pins sit exactly at the first section's start and
/// the last one's end: no line is added, so a transformer or a matcher ends on its own load.</para>
///
/// <para><b>The pieces.</b> A line is an MLIN, a gap an MGAP at the width there, a taper an MTAPER. A stub on one side
/// is an MTEE whose through arms end at the stub's edges and whose branch carries the whole stub, L past the wider
/// through arm's edge — MTEE's own reference plane, so the junction is MTEE's and L is the line beyond it. A stub on
/// both sides is an MCROSS likewise, its two arms the stub; MCROSS treats a pair of arms ending at the other pair's
/// edges as a straight line with no junction, so the through arms reach <see cref="CrossThroughPast"/> past the stubs.
/// Each junction's through arms are the widths either side of it. An open end is <see cref="PlanOpen"/>; a shorted end is
/// a pin, G1, G2, … in list order (up before down), for an ordinary VIAGND, as MCBL's grounds are. Open ends are numbered
/// first of the internal nodes: a quarter-wave open stub's end has a near-zero self admittance at resonance, and the
/// local block does not pivot.</para>
///
/// <para><b>Not modelled:</b> the step between two widths (no MSTEP), so a stepped-impedance design is its lines as
/// cascaded; the coupling between neighbouring stubs.</para>
/// </summary>
public static class LadderPlan
{
    /// <summary>A placed ladder's list: three sections of a stepped-impedance lowpass on 20 mil RO4350B.</summary>
    public const string DefaultSections = "line(W=0.3mm, L=4mm); line(W=3mm, L=4mm); line(W=0.3mm, L=4mm)";

    /// <summary>How far a cross's through arms reach past its stubs' edges: enough that MCROSS keeps its junction, far
    /// under anything the ladder resolves (a hundredth of a degree at 10 GHz).</summary>
    public const double CrossThroughPast = 1e-6;

    private const string Kinds = "line, gap, taper, stub";

    // ── The list ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The sections of <paramref name="text"/>, or a coded refusal naming the item that is wrong
    /// (<c>device.ladder.*</c>). Empty items (a trailing <c>;</c>) are skipped; items are numbered from 1 without them.
    /// A list quoted whole is read inside its quotes.</summary>
    public static IReadOnlyList<LadderSection> Parse(string? text)
    {
        string body = (text ?? "").Trim();
        if (body.Length >= 2 && body[0] == '"' && body[^1] == '"') body = body[1..^1];
        var items = body.Split(';').Select(i => i.Trim()).Where(i => i.Length > 0).ToArray();
        if (items.Length == 0)
            throw Refuse("device.ladder.empty", "Ladder: the section list is empty; it needs at least one section ({kinds}).",
                ("kinds", Kinds));
        return [.. items.Select((item, k) => ParseItem(item, k + 1))];
    }

    private static readonly Regex Item = new(@"^([A-Za-z]+)\s*\((.*)\)$", RegexOptions.Singleline);

    private static LadderSection ParseItem(string item, int n)
    {
        var m = Item.Match(item);
        if (!m.Success)
            throw Refuse("device.ladder.syntax", "Ladder: section {n} ({item}) is not kind(field=value, …).", ("n", n), ("item", item));

        string kind = m.Groups[1].Value.ToLowerInvariant();
        string[] allowed = kind switch
        {
            "line" => ["W", "L"],
            "gap" => ["S"],
            "taper" => ["W1", "W2", "L"],
            "stub" => ["W", "L", "end", "side"],
            _ => throw Refuse("device.ladder.unknown-kind", "Ladder: section {n} ({item}): '{kind}' is not a section kind ({kinds}).",
                ("n", n), ("item", item), ("kind", m.Groups[1].Value), ("kinds", Kinds)),
        };

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string arg in m.Groups[2].Value.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0))
        {
            int eq = arg.IndexOf('=');
            string name = eq > 0 ? arg[..eq].Trim() : "";
            if (eq <= 0 || !allowed.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw Refuse("device.ladder.unknown-field", "Ladder: section {n} ({item}): '{field}' is not a field of {kind} ({fields}).",
                    ("n", n), ("item", item), ("field", arg), ("kind", kind), ("fields", string.Join(", ", allowed)));
            if (!fields.TryAdd(name, arg[(eq + 1)..].Trim()))
                throw Refuse("device.ladder.syntax", "Ladder: section {n} ({item}) states {field} twice.",
                    ("n", n), ("item", item), ("field", name));
        }

        double Len(string name)
        {
            if (!fields.TryGetValue(name, out string? v))
                throw Refuse("device.ladder.missing-field", "Ladder: section {n} ({item}): {kind} needs {field}.",
                    ("n", n), ("item", item), ("kind", kind), ("field", name));
            if (!TryLength(v, out double m))
                throw Refuse("device.ladder.bad-value",
                    "Ladder: section {n} ({item}): {field} = {value} is not a length (a number in metres, or with mm, um, nm, cm, mil or in).",
                    ("n", n), ("item", item), ("field", name), ("value", v));
            if (!(m > 0.0) || !double.IsFinite(m))
                throw Refuse("device.ladder.not-positive", "Ladder: section {n} ({item}): {field} = {value} must be positive.",
                    ("n", n), ("item", item), ("field", name), ("value", v));
            return m;
        }

        T Word<T>(string name, T fallback) where T : struct, Enum
        {
            if (!fields.TryGetValue(name, out string? v)) return fallback;
            if (Enum.TryParse(v, ignoreCase: true, out T w) && Enum.IsDefined(w) && !char.IsDigit(v.FirstOrDefault())) return w;
            throw Refuse("device.ladder.bad-value", "Ladder: section {n} ({item}): {field} = {value} is not one of {words}.",
                ("n", n), ("item", item), ("field", name), ("value", v),
                ("words", string.Join(", ", Enum.GetNames<T>().Select(x => x.ToLowerInvariant()))));
        }

        return kind switch
        {
            "line" => new LadderLine(Len("W"), Len("L")),
            "gap" => new LadderGap(Len("S")),
            "taper" => new LadderTaper(Len("W1"), Len("W2"), Len("L")),
            _ => new LadderStub(Len("W"), Len("L"), Word("end", LadderStubEnd.Open), Word("side", LadderStubSide.Up)),
        };
    }

    private static bool TryLength(string text, out double meters)
    {
        string v = string.Concat(text.Where(c => !char.IsWhiteSpace(c))).Replace('µ', 'u').Replace('μ', 'u');
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out meters)) return true;
        if (Units.TrySplitGluedNumber(v, out string number, out string unit) && Units.BaseUnit(unit) == "metre"
            && Units.Scale(unit) is { } scale
            && double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double x))
        {
            meters = x * scale;
            return true;
        }
        meters = 0.0;
        return false;
    }

    /// <summary>The list as text <see cref="Parse"/> reads back: every field stated, lengths in mm to the nanometre.</summary>
    public static string Format(IEnumerable<LadderSection> sections)
        => string.Join("; ", sections.Select(s => s switch
        {
            LadderLine l => $"line(W={Mm(l.W)}, L={Mm(l.L)})",
            LadderGap g => $"gap(S={Mm(g.S)})",
            LadderTaper t => $"taper(W1={Mm(t.W1)}, W2={Mm(t.W2)}, L={Mm(t.L)})",
            LadderStub b => $"stub(W={Mm(b.W)}, L={Mm(b.L)}, end={b.End.ToString().ToLowerInvariant()}, "
                          + $"side={b.Side.ToString().ToLowerInvariant()})",
            _ => throw new NotSupportedException(s.GetType().Name),
        }));

    private static string Mm(double m) => (m * 1e3).ToString("0.######", CultureInfo.InvariantCulture) + "mm";

    // ── The pins ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ladder's pins: 1 and 2, then G1… for each shorted stub arm in list order.</summary>
    public static IReadOnlyList<string> PinsFor(int grounds)
        => ["1", "2", .. Enumerable.Range(1, Math.Max(0, grounds)).Select(k => $"G{k}")];

    /// <summary>How many via pins <paramref name="sections"/> has: one per shorted stub arm.</summary>
    public static int Grounds(IEnumerable<LadderSection> sections)
        => sections.OfType<LadderStub>().Where(s => s.End == LadderStubEnd.Short).Sum(Arms);

    /// <summary>The via pins of a list as text, 0 when it does not parse (the symbol then draws the two-pin ladder).</summary>
    public static int Grounds(string? text)
    {
        try { return Grounds(Parse(text)); }
        catch (ParameterRefusalException) { return 0; }
    }

    private static int Arms(LadderStub s) => s.Side == LadderStubSide.Both ? 2 : 1;

    // ── The plan ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ladder for a list as text.</summary>
    public static LadderDesign Build(string? sections, double z0, double fHz, MicrostripSubstrate s)
        => Build(Parse(sections), z0, fHz, s);

    public static LadderDesign Build(IReadOnlyList<LadderSection> sections, double z0, double fHz, MicrostripSubstrate s)
    {
        foreach (var (name, value) in new[] { ("Z0", z0), ("F", fHz) })
            if (!(value > 0))
                throw Refuse("device.ladder.spec-not-positive", "Ladder: {name} = {value} must be positive.",
                    ("name", name), ("value", value));
        if (sections.Count == 0)
            throw Refuse("device.ladder.empty", "Ladder: the section list is empty; it needs at least one section ({kinds}).",
                ("kinds", Kinds));

        // Every dimension on the plans' 2 nm grid, so pieces meet to the unit in the artwork.
        static LadderSection OnGrid(LadderSection sec) => sec switch
        {
            LadderLine l => new LadderLine(PassivePlan.OnGrid(l.W), PassivePlan.OnGrid(l.L)),
            LadderGap g => new LadderGap(PassivePlan.OnGrid(g.S)),
            LadderTaper t => new LadderTaper(PassivePlan.OnGrid(t.W1), PassivePlan.OnGrid(t.W2), PassivePlan.OnGrid(t.L)),
            LadderStub b => b with { W = PassivePlan.OnGrid(b.W), L = PassivePlan.OnGrid(b.L) },
            _ => throw new NotSupportedException(sec.GetType().Name),
        };
        var list = sections.Select(OnGrid).ToArray();
        double w0 = Width(z0, fHz, s);

        // Nodes: the pins (1, 2, G1…), then the open ends, then the rest in list order.
        var pins = PinsFor(Grounds(list));
        int ground = 3, open = pins.Count + 1;
        int next = pins.Count + list.OfType<LadderStub>().Where(b => b.End == LadderStubEnd.Open).Sum(Arms);

        var plan = new List<PlanSection>();
        var atF = new List<LadderSectionAtF>();
        int cur = 1;
        double x = 0.0, w = w0;
        for (int i = 0; i < list.Length; i++)
        {
            int to = i == list.Length - 1 ? 2 : ++next;
            switch (list[i])
            {
                case LadderLine l:
                    plan.Add(new PlanLine([cur, to], x, 0.0, 0.0, l.W, l.L));
                    atF.Add(Arm(l.W, l.L, open: false, fHz, s));
                    x += l.L;
                    w = l.W;
                    break;
                case LadderGap g:
                    plan.Add(new PlanGap([cur, to], x, 0.0, 0.0, w, g.S));
                    var gap = new MicrostripGapModel(w, g.S, s.HMeters, s.EpsR, "(ladder)");
                    atF.Add(new LadderSectionAtF(0.0, 0.0, 0.0, 0.0, gap.SeriesCapacitance, gap.ShuntCapacitance, w));
                    x += g.S;
                    break;
                case LadderTaper t:
                    plan.Add(new PlanTaper([cur, to], x, 0.0, 0.0, t.W1, t.W2, t.L));
                    atF.Add(Taper(t, fHz, s));
                    x += t.L;
                    w = t.W2;
                    break;
                case LadderStub b:
                {
                    double after = i == list.Length - 1 ? w0 : list[i + 1] switch
                    {
                        LadderLine l => l.W,
                        LadderTaper t => t.W1,
                        _ => w,
                    };
                    double reach = Math.Max(w, after) / 2.0 + b.L;   // centre to the stub's end
                    bool isOpen = b.End == LadderStubEnd.Open;
                    int End() => isOpen ? open++ : ground++;
                    var tip = new List<(int Node, double X, double Y, double Rot)>();
                    if (b.Side == LadderStubSide.Both)
                    {
                        double arm = b.W / 2.0 + CrossThroughPast, xc = x + arm;
                        int up = End(), down = End();
                        plan.Add(new PlanCross([to, up, cur, down], xc, 0.0, 0.0, after, b.W, w, b.W, arm, reach, arm, reach));
                        tip.Add((up, xc, reach, 90.0));
                        tip.Add((down, xc, -reach, 270.0));
                        x += 2.0 * arm;
                    }
                    else
                    {
                        // MTEE's branch leaves along its own −y. Down: the tee as it stands. Up: turned over, its pin 1
                        // at the junction's right edge.
                        int end = End();
                        bool up = b.Side == LadderStubSide.Up;
                        plan.Add(up
                            ? new PlanTee(after, w, b.W, b.W / 2.0, b.W / 2.0, reach, [to, cur, end], x + b.W, 0.0, 180.0)
                            : new PlanTee(w, after, b.W, b.W / 2.0, b.W / 2.0, reach, [cur, to, end], x, 0.0, 0.0));
                        tip.Add((end, x + b.W / 2.0, up ? reach : -reach, up ? 90.0 : 270.0));
                        x += b.W;
                    }
                    if (isOpen) foreach (var e in tip) plan.Add(new PlanOpen([e.Node], e.X, e.Y, e.Rot, b.W));
                    atF.Add(Arm(b.W, b.L, isOpen, fHz, s) with { WAt = w });
                    w = after;
                    break;
                }
            }
            cur = to;
        }
        return new LadderDesign(z0, fHz, w0, list, atF, new PassivePlan(pins, plan, next));
    }

    /// <summary>The width of a line of <paramref name="z"/> at <paramref name="fHz"/> on the substrate (MLIN's dispersive
    /// Z0), on the 2 nm grid: what the ports' lines are, and what a writer of the list asks for an impedance.</summary>
    public static double Width(double z, double fHz, MicrostripSubstrate s)
        => PassivePlan.OnGrid(BranchLinePlan.Width(z, fHz, s, "Ladder", "device.ladder.width-unreachable"));

    /// <summary>A line or stub arm of <paramref name="w"/> and <paramref name="l"/> at F, with an open end's extension.</summary>
    private static LadderSectionAtF Arm(double w, double l, bool open, double fHz, MicrostripSubstrate s)
    {
        var p = BranchLinePlan.Line(w, s).LineParameters(fHz);
        double end = open ? MicrostripOpenEnd.Extension(w, s.HMeters, s.EpsR, p.EeffStatic) : 0.0;
        return new LadderSectionAtF(p.Z0, p.Z0, Deg(PlanarLineAngle.AngleRad(p.Eeff, l, fHz)),
            Deg(PlanarLineAngle.AngleRad(p.Eeff, end, fHz)), 0.0, 0.0, w);
    }

    /// <summary>A taper at F: its ends' impedances, and its phase as the mean phase per metre of its width over L, sampled
    /// at sixteen points (the MLIN sections MTAPER cascades are finer; this is a readout).</summary>
    private static LadderSectionAtF Taper(LadderTaper t, double fHz, MicrostripSubstrate s)
    {
        const int n = 16;
        double theta = Enumerable.Range(0, n).Sum(k =>
        {
            double wk = t.W1 + (t.W2 - t.W1) * (k + 0.5) / n;
            return PlanarLineAngle.AngleRad(BranchLinePlan.Line(wk, s).LineParameters(fHz).Eeff, t.L / n, fHz);
        });
        return new LadderSectionAtF(BranchLinePlan.Line(t.W1, s).LineParameters(fHz).Z0,
            BranchLinePlan.Line(t.W2, s).LineParameters(fHz).Z0, Deg(theta), 0.0, 0.0, 0.0, t.W1);
    }

    private static double Deg(double rad) => rad * 180.0 / Math.PI;

    private static ParameterRefusalException Refuse(string code, string template, params (string, object?)[] args)
        => new(Diagnostic.Create(code, DiagnosticSeverity.Error, template, args));
}
