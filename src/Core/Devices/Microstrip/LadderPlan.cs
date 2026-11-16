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

/// <summary>A shunt stub at a junction: W wide and L long from the through line's edge to its end, on one side of the
/// line or both (a cross, an arm of W and L each side). The end is open, shorted (a pin for a via to ground), or a radial
/// stub (MRADIAL) of radius <paramref name="Ro"/> from its apex and <paramref name="AngleDeg"/>, its narrow end W wide on
/// the stub's end, one sector or a butterfly's two.</summary>
public sealed record LadderStub(double W, double L, LadderStubEnd End = LadderStubEnd.Open,
    LadderStubSide Side = LadderStubSide.Up, double Ro = 0.0, double AngleDeg = LadderPlan.DefaultRadialAngleDeg)
    : LadderSection;

/// <summary>How a stub ends: in the open, on a pin for a via to ground, or on a radial stub, one sector or a butterfly's
/// two (MRADIAL's <c>Sides</c> 1 or 2).</summary>
public enum LadderStubEnd { Open, Short, Radial, Butterfly }

/// <summary>A place for one lumped part (a <see cref="PlanSite"/>): an R, L or C of <paramref name="Value"/> (Ω, H, F),
/// series (across the through line, which stops a site span short of the part's two pins) or shunt (from a pin at the
/// end of a pad off the line, on <paramref name="Side"/>, to ground). The part is an ordinary one on the ladder's pins.</summary>
public sealed record LadderSite(PlanSiteRole Role, double Value, PlanSiteKind Kind = PlanSiteKind.Series,
    LadderStubSide Side = LadderStubSide.Up) : LadderSection;

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
/// <param name="WAt">The width the ladder has at a gap, a stub's junction or a site.</param>
/// <param name="EndOhms">A radial stub's input reactance at its narrow end (a butterfly's two sectors together).</param>
public sealed record LadderSectionAtF(double Z, double Z2, double ThetaDeg, double OpenEndDeg, double SeriesF,
    double ShuntF, double WAt, double EndOhms = 0.0);

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
///   stub(W=, L=, end=radial|butterfly, Ro=, angle=, side=…)   a shunt stub ending on a radial stub (angle 60 unless stated)
///   site(role=R|L|C, value=, type=series|shunt, side=up|down)   a lumped part's place; series unless stated
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
/// a pin for an ordinary VIAGND, as MCBL's grounds are; a radial end is a <see cref="PlanRadialStub"/> on the stub's end,
/// fanning away from the line (a butterfly's two sectors either side of it). Stub ends that are not pins are numbered
/// first of the internal nodes: a quarter-wave open stub's end has a near-zero self admittance at resonance, and the
/// local block does not pivot.</para>
///
/// <para><b>Sites.</b> A series site stops the copper a site span short (the part's <c>Gap</c>, or its
/// <c>Footprint</c>'s land, applied where the substrate is), its two ends pins for the part; a shunt site is a square pad
/// of the width there off a tee, its end a pin for the part to ground. A series site at either end, after a gap or beside
/// another series site, has a pad of its own on each such side, a square of the width there, so an LC ladder of sites
/// alone is a row of pads. Every site is declared in <see cref="PassivePlan.Sites"/>, a shorted stub's end as a via site,
/// so the readout lists them and Place Site Parts fills them.</para>
///
/// <para><b>Pins.</b> 1 and 2, then G1, G2, … in list order, one per shorted stub arm (up before down), one per shunt
/// site and two per series site. The pin count alone draws the symbol, so every such pin takes the one name: a list with
/// no site has exactly the pins it always had.</para>
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

    /// <summary>A placed ladder's series-site span: an 0402's land at nominal density, MWPD's default Gap.</summary>
    public const double DefaultGap = 1.35e-3;

    /// <summary>A radial stub's sector angle where the list states none, MRADIAL's own default.</summary>
    public const double DefaultRadialAngleDeg = 60.0;

    private const string Kinds = "line, gap, taper, stub, site";

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
            "stub" => ["W", "L", "end", "side", "Ro", "angle"],
            "site" => ["role", "value", "type", "side"],
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

        ParameterRefusalException NotFor(string field, string what)
            => Refuse("device.ladder.unknown-field", "Ladder: section {n} ({item}): {field} is for {what}.",
                ("n", n), ("item", item), ("field", field), ("what", what));

        switch (kind)
        {
            case "line": return new LadderLine(Len("W"), Len("L"));
            case "gap": return new LadderGap(Len("S"));
            case "taper": return new LadderTaper(Len("W1"), Len("W2"), Len("L"));
            case "site":
            {
                if (!fields.TryGetValue("role", out string? r))
                    throw Refuse("device.ladder.missing-field", "Ladder: section {n} ({item}): site needs role.",
                        ("n", n), ("item", item));
                var role = r.ToUpperInvariant() switch
                {
                    "R" => PlanSiteRole.R,
                    "L" => PlanSiteRole.L,
                    "C" => PlanSiteRole.C,
                    _ => throw Refuse("device.ladder.bad-value", "Ladder: section {n} ({item}): role = {value} is not R, L or C.",
                        ("n", n), ("item", item), ("value", r)),
                };
                if (!fields.TryGetValue("value", out string? v))
                    throw Refuse("device.ladder.missing-field", "Ladder: section {n} ({item}): site needs value.",
                        ("n", n), ("item", item));
                if (!TryValue(v, role, out double value))
                    throw Refuse("device.ladder.bad-value",
                        "Ladder: section {n} ({item}): value = {value} is not a number of {unit} (or with an SI prefix).",
                        ("n", n), ("item", item), ("value", v), ("unit", UnitOf(role)));
                if (!(value > 0.0) || !double.IsFinite(value))
                    throw Refuse("device.ladder.not-positive", "Ladder: section {n} ({item}): value = {value} must be positive.",
                        ("n", n), ("item", item), ("value", v));
                var type = Word("type", PlanSiteKind.Series);
                if (type == PlanSiteKind.Series && fields.ContainsKey("side")) throw NotFor("side", "a shunt site");
                var side = Word("side", LadderStubSide.Up);
                if (side == LadderStubSide.Both) throw NotFor("side=both", "a stub; a shunt site is on one side");
                return new LadderSite(role, value, type, side);
            }
            default:
            {
                var end = Word("end", LadderStubEnd.Open);
                bool radial = end is LadderStubEnd.Radial or LadderStubEnd.Butterfly;
                if (!radial)
                {
                    if (fields.ContainsKey("Ro")) throw NotFor("Ro", "end=radial or end=butterfly");
                    if (fields.ContainsKey("angle")) throw NotFor("angle", "end=radial or end=butterfly");
                    return new LadderStub(Len("W"), Len("L"), end, Word("side", LadderStubSide.Up));
                }
                double angle = DefaultRadialAngleDeg;
                if (fields.TryGetValue("angle", out string? a) && !TryDegrees(a, out angle))
                    throw Refuse("device.ladder.bad-value", "Ladder: section {n} ({item}): angle = {value} is not a number of degrees.",
                        ("n", n), ("item", item), ("value", a));
                var stub = new LadderStub(Len("W"), Len("L"), end, Word("side", LadderStubSide.Up), Len("Ro"), angle);
                if (MicrostripRadialStub.Refusal(stub.W, stub.Ro, angle, end == LadderStubEnd.Butterfly ? 2 : 1) is { } why)
                    throw Refuse("device.ladder.bad-value", "Ladder: section {n} ({item}): {why}",
                        ("n", n), ("item", item), ("why", why.Render()));
                return stub;
            }
        }
    }

    private static string UnitOf(PlanSiteRole role) => role switch { PlanSiteRole.C => "F", PlanSiteRole.L => "H", _ => "Ohm" };

    /// <summary>A site's value: a number in SI, or with the role's unit or a bare SI prefix glued on (<c>3.3pF</c>,
    /// <c>12nH</c>, <c>4.7kOhm</c>, <c>2.2p</c>).</summary>
    private static bool TryValue(string text, PlanSiteRole role, out double value)
    {
        string v = string.Concat(text.Where(c => !char.IsWhiteSpace(c))).Replace('µ', 'u').Replace('μ', 'u').Replace("Ω", "Ohm");
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
        if (Units.TrySplitGluedNumber(v, out string number, out string unit) && Units.Scale(unit) is { } scale
            && (Units.BaseUnit(unit) == UnitOf(role) || unit is "T" or "G" or "M" or "k" or "m" or "u" or "n" or "p" or "f")
            && double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double x))
        {
            value = x * scale;
            return true;
        }
        value = 0.0;
        return false;
    }

    private static bool TryDegrees(string text, out double degrees)
    {
        string v = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        if (v.EndsWith("deg", StringComparison.OrdinalIgnoreCase)) v = v[..^3];
        else if (v.EndsWith('°')) v = v[..^1];
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out degrees);
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
                          + $"side={b.Side.ToString().ToLowerInvariant()}"
                          + (b.End is LadderStubEnd.Radial or LadderStubEnd.Butterfly
                              ? $", Ro={Mm(b.Ro)}, angle={b.AngleDeg.ToString("0.######", CultureInfo.InvariantCulture)}" : "") + ")",
            LadderSite t => $"site(role={t.Role}, value={Value(t)}, type={t.Kind.ToString().ToLowerInvariant()}"
                          + (t.Kind == PlanSiteKind.Shunt ? $", side={t.Side.ToString().ToLowerInvariant()}" : "") + ")",
            _ => throw new NotSupportedException(s.GetType().Name),
        }));

    /// <summary>A site's value in the unit a part states it in: pF, nH or Ω, to six decimals.</summary>
    private static string Value(LadderSite t) => t.Role switch
    {
        PlanSiteRole.C => (t.Value * 1e12).ToString("0.######", CultureInfo.InvariantCulture) + "pF",
        PlanSiteRole.L => (t.Value * 1e9).ToString("0.######", CultureInfo.InvariantCulture) + "nH",
        _ => t.Value.ToString("0.######", CultureInfo.InvariantCulture) + "Ohm",
    };

    private static string Mm(double m) => (m * 1e3).ToString("0.######", CultureInfo.InvariantCulture) + "mm";

    // ── The pins ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ladder's pins: 1 and 2, then G1… in list order.</summary>
    public static IReadOnlyList<string> PinsFor(int gPins)
        => ["1", "2", .. Enumerable.Range(1, Math.Max(0, gPins)).Select(k => $"G{k}")];

    /// <summary>How many G pins <paramref name="sections"/> has, where a via or a lumped part goes: one per shorted stub
    /// arm, one per shunt site and two per series site.</summary>
    public static int Grounds(IEnumerable<LadderSection> sections) => sections.Sum(GPinsOf);

    /// <summary>The G pins of a list as text, 0 when it does not parse (the symbol then draws the two-pin ladder).</summary>
    public static int Grounds(string? text)
    {
        try { return Grounds(Parse(text)); }
        catch (ParameterRefusalException) { return 0; }
    }

    private static int GPinsOf(LadderSection s) => s switch
    {
        LadderStub { End: LadderStubEnd.Short } b => Arms(b),
        LadderSite { Kind: PlanSiteKind.Series } => 2,
        LadderSite => 1,
        _ => 0,
    };

    private static int Arms(LadderStub s) => s.Side == LadderStubSide.Both ? 2 : 1;

    // ── The plan ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ladder for a list as text.</summary>
    public static LadderDesign Build(string? sections, double z0, double fHz, MicrostripSubstrate s, double gapMeters = DefaultGap)
        => Build(Parse(sections), z0, fHz, s, gapMeters);

    /// <param name="gapMeters">The span of every series site: the part's <c>Gap</c>, or its <c>Footprint</c>'s land.</param>
    public static LadderDesign Build(IReadOnlyList<LadderSection> sections, double z0, double fHz, MicrostripSubstrate s,
        double gapMeters = DefaultGap)
    {
        foreach (var (name, value) in new[] { ("Z0", z0), ("F", fHz), ("Gap", gapMeters) })
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
            LadderStub b => b with { W = PassivePlan.OnGrid(b.W), L = PassivePlan.OnGrid(b.L), Ro = PassivePlan.OnGrid(b.Ro) },
            LadderSite t => t,
            _ => throw new NotSupportedException(sec.GetType().Name),
        };
        var list = sections.Select(OnGrid).ToArray();
        int n = list.Length;
        double w0 = Width(z0, fHz, s), span = PassivePlan.OnGrid(gapMeters);

        // Nodes: the pins (1, 2, G1…; G k is node 2 + k, each item's first in firstG), then the stub ends that are not
        // pins (open and radial), then the rest in list order.
        var firstG = new int[n];
        for (int i = 0, g = 1; i < n; i++) { firstG[i] = g; g += GPinsOf(list[i]); }
        var pins = PinsFor(Grounds(list));
        int stubEnd = pins.Count + 1;
        int next = pins.Count + list.OfType<LadderStub>().Where(b => b.End != LadderStubEnd.Short).Sum(Arms);
        static int G(int k) => 2 + k;
        bool SeriesSite(int i) => i < n && list[i] is LadderSite { Kind: PlanSiteKind.Series };
        // The node a section ending in copper hands on: pin 2 after the last item, the next series site's first pin, or
        // a new internal node.
        int Next(int i) => i == n - 1 ? 2 : SeriesSite(i + 1) ? G(firstG[i + 1]) : ++next;

        var plan = new List<PlanSection>();
        var sites = new List<PlanSite>();
        var atF = new List<LadderSectionAtF>();
        int cur = 1;
        bool copper = false;   // whether copper ends at cur (a gap's far end and a series site's second pin are bare)
        double x = 0.0, w = w0;
        void Pad(int to)
        {
            plan.Add(new PlanLine([cur, to], x, 0.0, 0.0, w, w));
            (x, cur, copper) = (x + w, to, true);
        }
        double After(int i) => i == n - 1 ? w0 : list[i + 1] switch
        {
            LadderLine l => l.W,
            LadderTaper t => t.W1,
            _ => w,
        };

        for (int i = 0; i < n; i++)
        {
            switch (list[i])
            {
                case LadderLine l:
                {
                    int to = Next(i);
                    plan.Add(new PlanLine([cur, to], x, 0.0, 0.0, l.W, l.L));
                    atF.Add(Arm(l.W, l.L, open: false, fHz, s));
                    (x, w, cur, copper) = (x + l.L, l.W, to, true);
                    break;
                }
                case LadderGap g:
                {
                    int to = i == n - 1 ? 2 : ++next;
                    plan.Add(new PlanGap([cur, to], x, 0.0, 0.0, w, g.S));
                    var gap = new MicrostripGapModel(w, g.S, s.HMeters, s.EpsR, "(ladder)");
                    atF.Add(new LadderSectionAtF(0.0, 0.0, 0.0, 0.0, gap.SeriesCapacitance, gap.ShuntCapacitance, w));
                    (x, cur, copper) = (x + g.S, to, false);
                    break;
                }
                case LadderTaper t:
                {
                    int to = Next(i);
                    plan.Add(new PlanTaper([cur, to], x, 0.0, 0.0, t.W1, t.W2, t.L));
                    atF.Add(Taper(t, fHz, s));
                    (x, w, cur, copper) = (x + t.L, t.W2, to, true);
                    break;
                }
                case LadderStub b:
                {
                    int to = Next(i);
                    double after = After(i);
                    double reach = Math.Max(w, after) / 2.0 + b.L;   // centre to the stub's end
                    int g = firstG[i];
                    int End() => b.End == LadderStubEnd.Short ? G(g++) : stubEnd++;
                    var tip = new List<(int Node, double X, double Y, bool Up)>();
                    if (b.Side == LadderStubSide.Both)
                    {
                        double arm = b.W / 2.0 + CrossThroughPast, xc = x + arm;
                        int up = End(), down = End();
                        plan.Add(new PlanCross([to, up, cur, down], xc, 0.0, 0.0, after, b.W, w, b.W, arm, reach, arm, reach));
                        tip.Add((up, xc, reach, true));
                        tip.Add((down, xc, -reach, false));
                        x += 2.0 * arm;
                    }
                    else
                    {
                        int end = End();
                        bool up = b.Side == LadderStubSide.Up;
                        plan.Add(Tee(cur, to, w, after, b.W, reach, up, end, x));
                        tip.Add((end, x + b.W / 2.0, up ? reach : -reach, up));
                        x += b.W;
                    }
                    foreach (var e in tip)
                    {
                        switch (b.End)
                        {
                            case LadderStubEnd.Open:
                                plan.Add(new PlanOpen([e.Node], e.X, e.Y, e.Up ? 90.0 : 270.0, b.W));
                                break;
                            case LadderStubEnd.Short:
                                sites.Add(new PlanSite(PlanSiteRole.Via, PlanSiteKind.Shunt, pins[e.Node - 1], null, null, null));
                                break;
                            // A sector fans away from the line along its own +x, so it is turned to face out; a
                            // butterfly's two fan along its own ±x with the feed from −y, so it is turned to take the
                            // feed from the line: either side of the stub's end, parallel to the line.
                            case LadderStubEnd.Radial:
                                plan.Add(new PlanRadialStub([e.Node], e.X, e.Y, e.Up ? 90.0 : 270.0, b.W, b.Ro, b.AngleDeg, 1));
                                break;
                            default:
                                plan.Add(new PlanRadialStub([e.Node], e.X, e.Y, e.Up ? 0.0 : 180.0, b.W, b.Ro, b.AngleDeg, 2));
                                break;
                        }
                    }
                    atF.Add(Arm(b.W, b.L, b.End == LadderStubEnd.Open, fHz, s) with
                    {
                        WAt = w,
                        EndOhms = b.End is LadderStubEnd.Radial or LadderStubEnd.Butterfly ? SectorOhms(b, fHz, s) : 0.0,
                    });
                    (w, cur, copper) = (after, to, true);
                    break;
                }
                case LadderSite { Kind: PlanSiteKind.Shunt } t:
                {
                    // A square pad of the width there off a tee, as a one-sided stub; its end is the part's pin.
                    int to = Next(i), pin = G(firstG[i]);
                    double after = After(i);
                    bool up = t.Side == LadderStubSide.Up;
                    plan.Add(Tee(cur, to, w, after, w, Math.Max(w, after) / 2.0 + w, up, pin, x));
                    sites.Add(new PlanSite(t.Role, PlanSiteKind.Shunt, pins[pin - 1], null, null, t.Value));
                    atF.Add(new LadderSectionAtF(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, w));
                    (x, w, cur, copper) = (x + w, after, to, true);
                    break;
                }
                case LadderSite t:
                {
                    // The copper stops a site span short; each side without copper of its own gets a pad.
                    int a = G(firstG[i]), b = a + 1;
                    if (!copper) Pad(a);
                    sites.Add(new PlanSite(t.Role, PlanSiteKind.Series, pins[a - 1], pins[b - 1], span, t.Value));
                    atF.Add(new LadderSectionAtF(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, w));
                    (x, cur, copper) = (x + span, b, false);
                    if (i == n - 1) Pad(2);
                    else if (list[i + 1] is LadderGap) Pad(++next);
                    else if (SeriesSite(i + 1)) Pad(G(firstG[i + 1]));
                    break;
                }
            }
        }
        return new LadderDesign(z0, fHz, w0, list, atF, new PassivePlan(pins, plan, next) { Sites = sites });

    }

    /// <summary>A one-sided junction at <paramref name="x"/> from <paramref name="from"/> (width <paramref name="wIn"/>)
    /// to <paramref name="to"/> (<paramref name="wOut"/>): its through arms end at the branch's edges, and its branch,
    /// <paramref name="wb"/> wide, reaches <paramref name="reach"/> from the centre to <paramref name="end"/>. MTEE's branch
    /// leaves along its own −y. Down: the tee as it stands. Up: turned over, its pin 1 at the junction's right edge.</summary>
    private static PlanTee Tee(int from, int to, double wIn, double wOut, double wb, double reach, bool up, int end, double x)
        => up
            ? new PlanTee(wOut, wIn, wb, wb / 2.0, wb / 2.0, reach, [to, from, end], x + wb, 0.0, 180.0)
            : new PlanTee(wIn, wOut, wb, wb / 2.0, wb / 2.0, reach, [from, to, end], x, 0.0, 0.0);

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

    /// <summary>A radial end's input reactance at F, its sectors together; 0 where the readout's lossless substrate leaves
    /// it undefined.</summary>
    private static double SectorOhms(LadderStub b, double fHz, MicrostripSubstrate s)
    {
        var y = new MicrostripRadialStubModel(b.W, b.Ro, b.AngleDeg, b.End == LadderStubEnd.Butterfly ? 2 : 1, s, "(ladder)")
            .Admittance(fHz);
        double x = (1.0 / y).Imaginary;
        return double.IsFinite(x) ? x : 0.0;
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
