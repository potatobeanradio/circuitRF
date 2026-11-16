using System.Globalization;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>What one end of a coupled array's strip is (docs/design/rf-passives.md §3.9). Its letter in <c>Ends</c> is
/// the first of its name, but C for <see cref="Site"/>.</summary>
public enum StripEnd
{
    /// <summary>Nothing attached: the strip's fringing end (<see cref="PlanOpen"/>, one strip's form).</summary>
    Open,
    /// <summary>A pin for an ordinary via to ground (VIAGND), the MCBL G-pin rule.</summary>
    Short,
    /// <summary>A pin for an ordinary capacitor to ground, of the strip's <c>C</c>: a shunt site. The strip still
    /// fringes there.</summary>
    Site,
    /// <summary>Joined to the next joined end on the same side, top down, folding the two strips into one hairpin, a
    /// half-wave resonator: across the gap when they are neighbours, else by a wall beyond the ends of the strips between
    /// (the pseudo-interdigital pair's interleaved hairpins).</summary>
    Join,
    /// <summary>A port, 1 on the top strip and 2 on the bottom one: the coupled feed's terminating strips.</summary>
    Port,
}

/// <summary>The end pattern a coupled array is given. <see cref="Custom"/> reads <c>Ends</c>; the others fill it.</summary>
public enum CoupledArrayForm { Interdigital, Combline, PseudoInterdigital, Custom }

/// <summary>How a coupled array is fed: tapped onto its outer strips, or through outer strips of its own.</summary>
public enum CoupledArrayFeed { Tapped, Coupled }

/// <summary>
/// A coupled array's stored geometry, as the part's parameters hold it (every length in metres, C in farads): the
/// parameters a synthesis fills.
/// </summary>
/// <param name="W">Each strip's width, top strip first (N of them).</param>
/// <param name="S">The gap below each strip but the last (N − 1).</param>
/// <param name="L">Every strip's length.</param>
/// <param name="Ends"><c>Ends</c> as written; read only with <see cref="CoupledArrayForm.Custom"/>.</param>
/// <param name="Tap">Tapped feed: from each outer strip's grounded end to the tap.</param>
/// <param name="Wf">Tapped feed: the width of the line from each tap to its port, which is as long as it is wide.</param>
/// <param name="C">Each strip's loading capacitance, on every <see cref="StripEnd.Site"/> end of it.</param>
/// <param name="G">A join's wall past the strips between: its clearance from their ends.</param>
public sealed record CoupledArraySpec(IReadOnlyList<double> W, IReadOnlyList<double> S, double L,
    CoupledArrayForm Form, CoupledArrayFeed Feed, string? Ends, double Tap, double Wf, IReadOnlyList<double> C,
    double G = 0.5e-3)
{
    /// <summary>Strips, N.</summary>
    public int N => W.Count;

    /// <summary>The spec from a part's parameters, read through <paramref name="real"/> (name, fallback) and
    /// <paramref name="text"/> (a name's word, or null) so the model, the artwork and the readout read them alike.
    /// <c>W[k]</c>, <c>S[k]</c> and <c>C[k]</c> rows state a strip's or a gap's own, as MCLINN's do. A
    /// <c>Form</c> or <c>Feed</c> that names none is refused.</summary>
    public static CoupledArraySpec Of(Func<string, double, double> real, Func<string, string?> text)
    {
        int n = (int)Math.Round(real("N", CoupledArrayPlan.DefaultStrips));
        var (w, s) = MicrostripMultiLineModel.StripsOf(n, real, ComponentModelFactory.DefaultCoupledWidthMeters,
            ComponentModelFactory.DefaultCoupledGapMeters);
        double c = real("C", CoupledArrayPlan.DefaultC);
        return new(w, s, real("L", 10e-3), Named("Form", text("Form"), CoupledArrayForm.Interdigital),
            Named("Feed", text("Feed"), CoupledArrayFeed.Tapped), text("Ends"), real("Tap", 2e-3), real("Wf", 1.1e-3),
            [.. Enumerable.Range(1, Math.Max(n, 0)).Select(k => real($"C[{k}]", c))], real("G", 0.5e-3));
    }

    private static T Named<T>(string name, string? word, T fallback) where T : struct, Enum
        => string.IsNullOrWhiteSpace(word) ? fallback
         : Enum.TryParse(word.Trim(), ignoreCase: true, out T v) && Enum.IsDefined(v) ? v
         : throw CoupledArrayPlan.Refuse("name-unknown", "{name} = {word} names none of {names}.", ("name", name),
               ("word", word), ("names", string.Join(", ", Enum.GetNames<T>())));
}

/// <summary>What a coupled array comes out to: its spec, each strip's two ends (near, the x = 0 end, then far), and the
/// plan.</summary>
public sealed record CoupledArrayDesign(CoupledArraySpec Spec, IReadOnlyList<(StripEnd Near, StripEnd Far)> Ends,
    PassivePlan Plan);

/// <summary>
/// The coupled array (MCLA): N parallel strips over one N-line section, every strip coupled to every other
/// (<see cref="PlanCoupledStrips"/>, MCLINN's model), each strip end open, shorted through a via pin, loaded through a
/// capacitor pin, joined to its neighbour, or a port. Pure: <see cref="CoupledArraySpec"/> plus a substrate in,
/// <see cref="CoupledArrayDesign"/> out. One part for every coupled-array filter; the form is only the end pattern.
///
/// <para><b>Forms</b> (strip 1 the top one; near is x = 0):</para>
/// <list type="bullet">
/// <item><b>Interdigital</b> (Matthaei, <a href="https://doi.org/10.1109/TMTT.1962.1125556">10.1109/TMTT.1962.1125556</a>;
/// Wenzel, <a href="https://doi.org/10.1109/TMTT.1965.1126051">10.1109/TMTT.1965.1126051</a>): strip k shorted at its
/// near end when k is odd, at its far end when even, the other end open. With the coupled feed strips 1 and N are the
/// terminating lines, each shorted where the pattern says and the port at its other end.</item>
/// <item><b>Combline</b> (Matthaei, <i>Microwave J.</i>, vol. 6, Aug. 1963, no DOI): every strip shorted at its near end
/// and a capacitor site at its far end; with the coupled feed strips 1 and N have the port in place of the site.</item>
/// <item><b>PseudoInterdigital</b> (Hong and Lancaster, <a href="https://doi.org/10.1109/75.401073">10.1109/75.401073</a>):
/// the resonating strips in fours, each four a pair of interleaved hairpins: the first and third joined at their far
/// ends, the second and fourth at their near ends, each wall <c>G</c> clear of the end it passes. No vias. With the
/// coupled feed each port is on its terminating strip's end beside its neighbour's open end, the other end open.</item>
/// <item><b>Custom</b>: <c>Ends</c>, one two-letter token per strip, near end then far: O open, S short, C capacitor
/// site, J joined to the next joined end on that side, P port.</item>
/// </list>
///
/// <para><b>Feeds.</b> Coupled: the ports are strip ends (P). Tapped: every strip resonates, and a line <c>Wf</c> wide
/// and <c>Wf</c> long leaves the top strip upward and the bottom strip downward at <c>Tap</c> from that strip's
/// grounded end (its shorted end, else its joined one), its far end the port. The tap is a node on the strip: the
/// coupled section is cut there into sections of the same strips, which cascade exactly; the junction's own reactance
/// is not modelled.</para>
///
/// <para><b>Pins.</b> 1 and 2, then one pin for each shorted end and each capacitor site, <c>E1</c>, <c>E2</c>, …,
/// strip by strip from the top, near end before far. A pin's role is the plan's site there (a via or a C of the
/// strip's <c>C</c>), so the names follow the pin count alone, as every variadic part's do.</para>
///
/// <para><b>Ends in the model.</b> An open end and a site's end are each one strip's <see cref="PlanOpen"/>, MLIN's
/// end on the strip's own width: neighbouring open ends are not given the coupled pair's form, which would need equal
/// widths and pairs. A join of neighbours is an MLIN of the narrower strip's width across the gap, flush with the ends;
/// a join past other strips is each strip run on by <c>G</c> and half a wall, and the wall, MLINs all. Their corners are
/// not modelled, nor the wall's coupling to the ends it passes. Open-end nodes are numbered first of the internal
/// nodes, to be eliminated last.</para>
/// </summary>
public static class CoupledArrayPlan
{
    /// <summary>The strips a part has when nothing says otherwise.</summary>
    public const int DefaultStrips = 3;

    /// <summary>A site's capacitance when the part states none.</summary>
    public const double DefaultC = 1e-12;

    /// <summary>The pin name of the k-th shorted or loaded end (1-based).</summary>
    public static string EndPin(int k) => "E" + k.ToString(CultureInfo.InvariantCulture);

    /// <summary>The part's pins for <paramref name="endPins"/> shorted or loaded ends: 1, 2, E1..Ek.</summary>
    public static IReadOnlyList<string> PinsFor(int endPins)
        => ["1", "2", .. Enumerable.Range(1, Math.Max(endPins, 0)).Select(EndPin)];

    /// <summary>
    /// The pin count a part's written parameters give (N, Form, Feed, Ends as plain text, as the schematic holds them),
    /// or null when N is not a plain number or the pattern is refused: the symbol then draws the default part's.
    /// </summary>
    public static int? PinCountOf(string? n, string? form, string? feed, string? ends)
    {
        if (!int.TryParse(n?.Trim() ?? DefaultStrips.ToString(CultureInfo.InvariantCulture), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int strips)) return null;
        try
        {
            var spec = CoupledArraySpec.Of((name, fallback) => name == "N" ? strips : fallback,
                name => name switch { "Form" => form, "Feed" => feed, "Ends" => ends, _ => null });
            return 2 + EndPins(EndsOf(spec));
        }
        catch (ParameterRefusalException) { return null; }
    }

    /// <summary>The default part's pin count: the interdigital array of <see cref="DefaultStrips"/>, tapped.</summary>
    public static int DefaultPinCount => 2 + DefaultStrips;

    private static int EndPins(IReadOnlyList<(StripEnd Near, StripEnd Far)> ends)
        => ends.Sum(e => (e.Near is StripEnd.Short or StripEnd.Site ? 1 : 0) + (e.Far is StripEnd.Short or StripEnd.Site ? 1 : 0));

    /// <summary>Each strip's ends for <paramref name="spec"/>'s form and feed; refused when they cannot be built.</summary>
    public static IReadOnlyList<(StripEnd Near, StripEnd Far)> EndsOf(CoupledArraySpec spec)
    {
        int n = spec.N;
        bool coupled = spec.Feed == CoupledArrayFeed.Coupled;
        if (n < 2 || coupled && n < 3)
            throw Refuse("strips-too-few", "N = {n} strips; a coupled array has at least two, three with the coupled feed "
                + "(two terminating strips and a resonator).", ("n", n));
        if (spec.Form != CoupledArrayForm.Custom && !string.IsNullOrWhiteSpace(spec.Ends))
            throw Refuse("ends-with-form", "Ends = {ends} is read only with Form = Custom; Form = {form} sets every end.",
                ("ends", spec.Ends), ("form", spec.Form));

        var ends = new (StripEnd Near, StripEnd Far)[n];
        (int first, int last) = coupled ? (1, n - 2) : (0, n - 1);    // the resonating strips, 0-based
        switch (spec.Form)
        {
            case CoupledArrayForm.Interdigital:
                for (int k = 0; k < n; k++)
                    ends[k] = k % 2 == 0 ? (StripEnd.Short, StripEnd.Open) : (StripEnd.Open, StripEnd.Short);
                if (coupled)
                {
                    ends[0] = (StripEnd.Short, StripEnd.Port);
                    ends[n - 1] = (n - 1) % 2 == 0 ? (StripEnd.Short, StripEnd.Port) : (StripEnd.Port, StripEnd.Short);
                }
                break;
            case CoupledArrayForm.Combline:
                for (int k = 0; k < n; k++) ends[k] = (StripEnd.Short, StripEnd.Site);
                if (coupled) ends[0] = ends[n - 1] = (StripEnd.Short, StripEnd.Port);
                break;
            case CoupledArrayForm.PseudoInterdigital:
                if ((last - first + 1) % 4 != 0)
                    throw Refuse("groups-of-four", "Form = PseudoInterdigital takes its resonating strips in fours, two "
                        + "interleaved hairpins each, and has {count}: N is a multiple of four with the tapped feed, two more "
                        + "with the coupled feed.", ("count", last - first + 1));
                for (int k = first; k <= last; k++)
                    ends[k] = (k - first) % 2 == 0 ? (StripEnd.Open, StripEnd.Join) : (StripEnd.Join, StripEnd.Open);
                if (coupled)
                {
                    // Each port beside its neighbour's open end, as an edge-coupled filter's end section is fed.
                    ends[0] = ends[1].Near == StripEnd.Open ? (StripEnd.Port, StripEnd.Open) : (StripEnd.Open, StripEnd.Port);
                    ends[n - 1] = ends[n - 2].Near == StripEnd.Open ? (StripEnd.Port, StripEnd.Open) : (StripEnd.Open, StripEnd.Port);
                }
                break;
            default:
                ends = Parse(spec.Ends, n);
                break;
        }

        // Ports: exactly the coupled feed's two, one on each outer strip.
        int ports = ends.Sum(e => (e.Near == StripEnd.Port ? 1 : 0) + (e.Far == StripEnd.Port ? 1 : 0));
        bool outer = ends[0].Near == StripEnd.Port ^ ends[0].Far == StripEnd.Port
                     && ends[n - 1].Near == StripEnd.Port ^ ends[n - 1].Far == StripEnd.Port;
        if (coupled ? ports != 2 || !outer : ports != 0)
            throw Refuse("ports-misplaced", coupled
                ? "Feed = Coupled puts port 1 on one end of strip 1 and port 2 on one end of strip N (P in Ends), and nowhere else."
                : "Feed = Tapped taps the ports onto the outer strips; no end is a port (P in Ends).");

        // Joins: each with the next joined end on the same side, top down.
        foreach (bool far in new[] { false, true })
        {
            var joined = Enumerable.Range(0, n).Where(k => Side(ends[k], far) == StripEnd.Join).ToList();
            if (joined.Count % 2 != 0)
                throw Refuse("join-unpaired", "Strip {k}'s {side} end is joined and no strip below it joins it: a join folds "
                    + "two strips at the same end, each with the next joined one.", ("k", joined[^1] + 1),
                    ("side", far ? "far" : "near"));
        }
        return ends;
    }

    /// <summary>Each join's two strips (0-based, top first) on one side: consecutive joined ends, top down.</summary>
    private static IEnumerable<(int Top, int Bottom)> Joins(IReadOnlyList<(StripEnd Near, StripEnd Far)> ends, bool far)
    {
        var joined = Enumerable.Range(0, ends.Count).Where(k => Side(ends[k], far) == StripEnd.Join).ToList();
        for (int i = 0; i + 1 < joined.Count; i += 2) yield return (joined[i], joined[i + 1]);
    }

    private static StripEnd Side((StripEnd Near, StripEnd Far) e, bool far) => far ? e.Far : e.Near;

    /// <summary><c>Ends</c>: two of O, S, C, J, P per strip (near end, far end), any spaces or commas between them
    /// ignored, since a netlist line drops a value's spaces (SO OS SO is written SOOSSO).</summary>
    private static (StripEnd, StripEnd)[] Parse(string? text, int n)
    {
        string letters = string.Concat((text ?? "").Where(c => !char.IsWhiteSpace(c) && c is not (',' or ';')));
        var tokens = Enumerable.Range(0, letters.Length / 2).Select(k => letters.Substring(2 * k, 2)).ToArray();
        if (letters.Length != 2 * n || !letters.All(c => "OSCJPoscjp".Contains(c)))
            throw Refuse("ends-unreadable", "Ends = {ends}: Form = Custom needs N = {n} tokens of two letters, near end then "
                + "far, each O (open), S (short), C (capacitor site), J (joined to the neighbour) or P (port).",
                ("ends", text ?? ""), ("n", n));
        static StripEnd Of(char c) => char.ToUpperInvariant(c) switch
        {
            'S' => StripEnd.Short, 'C' => StripEnd.Site, 'J' => StripEnd.Join, 'P' => StripEnd.Port, _ => StripEnd.Open,
        };
        return [.. tokens.Select(t => (Of(t[0]), Of(t[1])))];
    }

    /// <summary>The <c>Ends</c> token of one strip, as Custom spells it.</summary>
    public static string Token((StripEnd Near, StripEnd Far) e)
    {
        static char Of(StripEnd x) => x == StripEnd.Site ? 'C' : x.ToString()[0];
        return $"{Of(e.Near)}{Of(e.Far)}";
    }

    public static CoupledArrayDesign Build(CoupledArraySpec spec, MicrostripSubstrate s)
    {
        var ends = EndsOf(spec);
        int n = spec.N;
        bool tapped = spec.Feed == CoupledArrayFeed.Tapped;
        foreach (var (name, value) in new[] { ("L", spec.L) }
                     .Concat(spec.W.Select((v, k) => ($"W[{k + 1}]", v)))
                     .Concat(spec.S.Select((v, k) => ($"S[{k + 1}]", v)))
                     .Concat(tapped ? [("Tap", spec.Tap), ("Wf", spec.Wf)] : [])
                     .Concat(new[] { false, true }.Any(far => Joins(ends, far).Any(j => j.Bottom > j.Top + 1)) ? [("G", spec.G)] : [])
                     .Concat(ends.Select((e, k) => (e, k)).Where(x => x.e.Near == StripEnd.Site || x.e.Far == StripEnd.Site)
                                 .Select(x => ($"C[{x.k + 1}]", spec.C[x.k]))))
            if (!(value > 0))
                throw Refuse("spec-not-positive", "{name} = {value} must be positive.", ("name", name), ("value", value));
        if (MicrostripMultiLineModel.Refusal(spec.W, spec.S, spec.L, s) is { } why) throw new ParameterRefusalException(why);

        double[] w = [.. spec.W.Select(PassivePlan.OnGrid)], gap = [.. spec.S.Select(PassivePlan.OnGrid)];
        double l = PassivePlan.OnGrid(spec.L), tap = PassivePlan.OnGrid(spec.Tap), wf = PassivePlan.OnGrid(spec.Wf);
        double clear = PassivePlan.OnGrid(spec.G);
        // Strip k's centre, below strip k − 1's by their half widths and the gap between them, as the drawer stacks them.
        var y = new double[n];
        for (int k = 1; k < n; k++) y[k] = y[k - 1] - (w[k - 1] + w[k]) / 2.0 - gap[k - 1];

        // Pins: 1, 2, then every shorted or loaded end in order. Internal nodes: every open end first, to be eliminated
        // last, then the joined ends and the cuts.
        int endPins = EndPins(ends), next = 3 + endPins;
        var pins = PinsFor(endPins);
        var endNode = new int[n, 2];
        int e = 0;
        for (int k = 0; k < n; k++)
            for (int side = 0; side < 2; side++)
            {
                var kind = side == 0 ? ends[k].Near : ends[k].Far;
                if (kind is StripEnd.Short or StripEnd.Site) endNode[k, side] = 3 + e++;
                else if (kind == StripEnd.Port) endNode[k, side] = k == 0 ? 1 : 2;
            }
        for (int k = 0; k < n; k++)
            for (int side = 0; side < 2; side++)
                if ((side == 0 ? ends[k].Near : ends[k].Far) == StripEnd.Open) endNode[k, side] = next++;
        for (int k = 0; k < n; k++)
            for (int side = 0; side < 2; side++)
                if (endNode[k, side] == 0) endNode[k, side] = next++;

        // The coupled length, cut at the taps.
        double TapX(int k)
        {
            var (near, far) = ends[k];
            bool fromNear = near == StripEnd.Short || near == StripEnd.Join && far != StripEnd.Short;
            if (!fromNear && far is not (StripEnd.Short or StripEnd.Join))
                throw Refuse("tap-ungrounded", "Feed = Tapped measures Tap from strip {k}'s shorted or joined end, and it has "
                    + "neither.", ("k", k + 1));
            if (tap >= l)
                throw Refuse("tap-outside", "Tap = {tap} m is not inside the strips' length L = {l} m.", ("tap", tap), ("l", l));
            return fromNear ? tap : PassivePlan.OnGrid(l - tap);
        }
        double[] taps = tapped ? [TapX(0), TapX(n - 1)] : [];
        double[] cuts = [.. new[] { 0.0, l }.Concat(taps).Distinct().Order()];
        var cutNode = new int[n, cuts.Length];
        for (int k = 0; k < n; k++)
        {
            cutNode[k, 0] = endNode[k, 0];
            cutNode[k, cuts.Length - 1] = endNode[k, 1];
            for (int i = 1; i < cuts.Length - 1; i++) cutNode[k, i] = next++;
        }

        var sections = new List<PlanSection>();
        for (int i = 0; i + 1 < cuts.Length; i++)
            sections.Add(new PlanCoupledStrips([.. Enumerable.Range(0, n).SelectMany(k => new[] { cutNode[k, i], cutNode[k, i + 1] })],
                cuts[i], 0.0, 0.0, w, gap, PassivePlan.OnGrid(cuts[i + 1] - cuts[i])));
        for (int k = 0; k < n; k++)
        {
            if (ends[k].Near is StripEnd.Open or StripEnd.Site)
                sections.Add(new PlanOpen([endNode[k, 0]], 0.0, y[k], 180.0, w[k]));
            if (ends[k].Far is StripEnd.Open or StripEnd.Site)
                sections.Add(new PlanOpen([endNode[k, 1]], l, y[k], 0.0, w[k]));
        }
        // A join of neighbours: a line of the narrower strip's width straight down across the gap, flush with the ends.
        // Past other strips: each strip run on by the clearance and half the wall, and the wall down between them.
        foreach (int side in new[] { 0, 1 })
            foreach (var (top, bottom) in Joins(ends, side == 1))
            {
                double wj = Math.Min(w[top], w[bottom]);
                if (bottom == top + 1)
                {
                    sections.Add(new PlanLine([endNode[top, side], endNode[bottom, side]], side == 0 ? wj / 2.0 : l - wj / 2.0,
                        y[top] - w[top] / 2.0, 270.0, wj, gap[top]));
                    continue;
                }
                double run = clear + wj / 2.0, x = side == 0 ? -run : l + run;
                int a = next++, b = next++;
                sections.Add(new PlanLine([endNode[top, side], a], side == 0 ? 0.0 : l, y[top], side == 0 ? 180.0 : 0.0, w[top], run));
                sections.Add(new PlanLine([endNode[bottom, side], b], side == 0 ? 0.0 : l, y[bottom], side == 0 ? 180.0 : 0.0, w[bottom], run));
                sections.Add(new PlanLine([a, b], x, y[top], 270.0, wj, PassivePlan.OnGrid(y[top] - y[bottom])));
            }
        if (tapped)
        {
            int i1 = Array.IndexOf(cuts, taps[0]), iN = Array.IndexOf(cuts, taps[1]);
            sections.Add(new PlanLine([cutNode[0, i1], 1], taps[0], y[0] + w[0] / 2.0, 90.0, wf, wf));
            sections.Add(new PlanLine([cutNode[n - 1, iN], 2], taps[1], y[n - 1] - w[n - 1] / 2.0, 270.0, wf, wf));
        }

        // A via on every shorted end, the strip's C on every loaded one.
        var sites = new List<PlanSite>();
        e = 0;
        for (int k = 0; k < n; k++)
            foreach (var kind in new[] { ends[k].Near, ends[k].Far })
                if (kind == StripEnd.Short) sites.Add(new(PlanSiteRole.Via, PlanSiteKind.Shunt, EndPin(++e), null, null, null));
                else if (kind == StripEnd.Site) sites.Add(new(PlanSiteRole.C, PlanSiteKind.Shunt, EndPin(++e), null, null, spec.C[k]));

        return new CoupledArrayDesign(spec, ends, new PassivePlan(pins, sections, next - 1) { Sites = sites });
    }

    internal static ParameterRefusalException Refuse(string code, string template,
        params (string Name, object? Value)[] args)
        => new(Diagnostic.Create("device.coupledarray." + code, DiagnosticSeverity.Error, "Coupled array: " + template, args));
}
