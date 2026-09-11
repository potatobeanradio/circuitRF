// ================================================================
//  WspTrace.cs  —  the WSProbe metrics a trace can be, and the ONE
//  function that produces their samples
//
//  WSP-4 (R-wsp4-5, R-wsp4-6, R-wsp4-11). Every value here is a call
//  into src/RfCore/Stability/ — the same functions the S-parameter
//  engine computes a run's H0:/Y0:/ZG:/ZL:/SM_Y0:/SM_H0: cubes with,
//  and the same ones `wsp_H0(SP1.wsp, idx)` resolves to in a measure
//  line (overview D-2/D-6). Nothing in this file does arithmetic on a
//  wsp entry: a second implementation of one of these formulae would
//  agree with the library on the fixtures and drift on a real circuit,
//  which is the failure the bit-identity gate (R-wsp4-14a) exists to
//  make impossible rather than unlikely.
//
//  It lives BELOW the firewall for the reason RND-1 put the renderers
//  there: `src/Ui`'s trace card and `src/Cli`'s `plot` verb both build
//  the same trace, and a probe metric that meant one thing in the
//  window and another headlessly would be invisible in both.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using RfCore.Data;
using RfCore.Stability;

namespace CircuitRF.Render.DataDisplay;

/// <summary>The sections the trace card groups the metric list into (R-wsp4-5's table).</summary>
public enum WspMetricGroup
{
    DrivingPoint,
    Bidirectional,
    LoopGain,
    Match,
    Margin,
    Pair,
    ProbeSet,
    Envelope,
}

/// <summary>
/// One quantity of T. A. Winslow, <i>General Circuit Analysis Using The WSProbe</i> (2023) that a
/// Data Display trace can be, under the document's own name (overview §1: nothing is renamed to
/// something more circuitRF-flavoured).
///
/// <para><b>Append-only.</b> The member NAME is what a <c>.cdd</c> carries, so a member may be
/// added but never renamed, reordered out of meaning, or removed — the same rule
/// <see cref="DerivedParameters"/> is under, and for the same reason: a saved display would come
/// back drawing a different quantity with no error anywhere.</para>
/// </summary>
public enum WspMetric
{
    None = 0,

    // ── Driving point (§4.9–4.10, Eq. 50–52, 105–108) ────────────────────────
    H0, Y0, InvH0, InvY0,

    // ── Bidirectional immittances (Eq. 61–78, 89–95) ─────────────────────────
    ZG, ZL, YG, YL, Zop, Yop,

    // ── Loop gains (§4.6, §4.8, Eq. 92–104) ──────────────────────────────────
    LG, F, LGF, LGR, LG_H, LG_MF, LG_MR, LG_MGF, LG_MGR,

    // ── Match (App. E.7) ─────────────────────────────────────────────────────
    NodalGamma,

    // ── Stability margin ([M] Eq. 9/10 and its four proxies) ─────────────────
    SM_Y0, SM_H0, SM, rY, iY, rH, iH,

    // ── Probe pair — wsp_block_calc {9..16} (Eq. 144–151) ────────────────────
    F_LGa, F_LGf, F_LGH, F_LGM, LGa, LGf, LGH, LGM,

    // ── Probe set — Ohtomo's global loop gains (Eq. 177–180) ─────────────────
    OhtomoG,

    // ── Envelope — the re-terminated circuit over a Γ grid (§9; [E] Fig. 6–9) ─
    EnvInvH0, EnvInvY0, EnvUnstable, SMenv, NDFenc,
}

/// <summary>What the trace card needs to know about one metric, in one row.</summary>
/// <param name="Metric">The member itself.</param>
/// <param name="Name">The document's own spelling — what the card lists and what
/// <c>plot --trace metric=</c> takes.</param>
/// <param name="Short">The wording the PICKER carries: no citation, and short enough that the
/// combo box shows the whole of it rather than an ellipsis. A trace card is a column a few inches
/// wide with four other controls on the same row, so a picker line that reads
/// "LG_MGR — general feedback theorem, reverse (Eq. 104)" arrives as
/// "LG_MGR — general feedback t…" — which is the half that carries no information, since the
/// equation number is a pointer into a document the reader is not holding while choosing from a
/// list. The full line is still on the item's TOOLTIP, citation and all.</param>
/// <param name="Description">The full line, citation included — the tooltip, and what the
/// documentation figures pick a metric by.</param>
/// <param name="Group">Which section of the card it sits in.</param>
/// <param name="IsReal">True for a real scalar (every margin quantity); false for a complex one.</param>
/// <param name="OnSmith">Whether a Smith chart can carry it — true only for a quantity that IS an
/// immittance or a reflection coefficient.</param>
/// <param name="Unit">The engine unit of the value, for the axis label.</param>
public readonly record struct WspMetricInfo(
    WspMetric      Metric,
    string         Name,
    string         Short,
    string         Description,
    WspMetricGroup Group,
    bool           IsReal,
    bool           OnSmith,
    string         Unit);

/// <summary>
/// The metric table, its plot-type gating, and the name a <c>.cdd</c> or a <c>--trace</c> spells
/// each member with.
/// </summary>
public static class WspMetrics
{
    /// <summary>Every metric a WSProbe trace can be, in the card's own order.</summary>
    public static readonly IReadOnlyList<WspMetricInfo> All =
    [
        new(WspMetric.H0, "H0", "driving-point Z",
            "driving-point impedance, vP/iP (Eq. 50)",   WspMetricGroup.DrivingPoint, false, true,  "Ohm"),
        new(WspMetric.Y0, "Y0", "driving-point Y",
            "driving-point admittance, iS/vS (Eq. 52)",  WspMetricGroup.DrivingPoint, false, true,  "S"),
        new(WspMetric.InvH0, "1/H0", "Kurokawa locus on H0",
            "Kurokawa's locus on H0 (Eq. 107)",          WspMetricGroup.DrivingPoint, false, false, "S"),
        new(WspMetric.InvY0, "1/Y0", "Kurokawa locus on Y0",
            "Kurokawa's locus on Y0 (Eq. 108)",          WspMetricGroup.DrivingPoint, false, false, "Ohm"),

        new(WspMetric.ZG, "ZG", "Z out of G, series",
            "impedance looking out of G, series stimulus (Eq. 65)", WspMetricGroup.Bidirectional, false, true, "Ohm"),
        new(WspMetric.ZL, "ZL", "Z out of L, series",
            "impedance looking out of L, series stimulus (Eq. 66)", WspMetricGroup.Bidirectional, false, true, "Ohm"),
        new(WspMetric.YG, "YG", "Y out of G, shunt",
            "admittance looking out of G, shunt stimulus (Eq. 72)", WspMetricGroup.Bidirectional, false, true, "S"),
        new(WspMetric.YL, "YL", "Y out of L, shunt",
            "admittance looking out of L, shunt stimulus (Eq. 77)", WspMetricGroup.Bidirectional, false, true, "S"),
        new(WspMetric.Zop, "Zop", "open-port Z",
            "open-port impedance, 1/(y11+y22) (Eq. 89)",            WspMetricGroup.Bidirectional, false, true, "Ohm"),
        new(WspMetric.Yop, "Yop", "open-port Y",
            "open-port admittance, 1/(z11+z22) (Eq. 90)",           WspMetricGroup.Bidirectional, false, true, "S"),

        new(WspMetric.LG, "LG", "bilateral (Tian)",
            "bilateral (Tian) loop gain (Eq. 92)",          WspMetricGroup.LoopGain, false, false, ""),
        new(WspMetric.F, "F", "return difference, 1 - LG",
            "return difference, 1 - LG (Eq. 53)",           WspMetricGroup.LoopGain, false, false, ""),
        new(WspMetric.LGF, "LGF", "circulator, forward",
            "forward synthetic-circulator loop gain (Eq. 99)",  WspMetricGroup.LoopGain, false, false, ""),
        new(WspMetric.LGR, "LGR", "circulator, reverse",
            "reverse synthetic-circulator loop gain (Eq. 97)",  WspMetricGroup.LoopGain, false, false, ""),
        new(WspMetric.LG_H, "LG_H", "Hurst",
            "Hurst loop gain (Eq. 100)",                    WspMetricGroup.LoopGain, false, false, ""),
        new(WspMetric.LG_MF, "LG_MF", "Middlebrook forward",
            "Middlebrook forward (Eq. 101)",                WspMetricGroup.LoopGain, false, false, ""),
        new(WspMetric.LG_MR, "LG_MR", "Middlebrook reverse",
            "Middlebrook reverse (Eq. 102)",                WspMetricGroup.LoopGain, false, false, ""),
        new(WspMetric.LG_MGF, "LG_MGF", "GFT forward",
            "general feedback theorem, forward (Eq. 103)",  WspMetricGroup.LoopGain, false, false, ""),
        new(WspMetric.LG_MGR, "LG_MGR", "GFT reverse",
            "general feedback theorem, reverse (Eq. 104)",  WspMetricGroup.LoopGain, false, false, ""),

        new(WspMetric.NodalGamma, "nodal gamma", "nodal conjugate Γ",
            "nodal conjugate reflection coefficient (App. E.7)",
                                                                                        WspMetricGroup.Match, false, true, ""),

        new(WspMetric.SM_Y0, "SM_Y0", "margin on 1/Y0",
            "stability margin on 1/Y0 ([M] Eq. 9)",   WspMetricGroup.Margin, true, false, ""),
        new(WspMetric.SM_H0, "SM_H0", "margin on 1/H0",
            "stability margin on 1/H0 ([M] Eq. 10)",  WspMetricGroup.Margin, true, false, ""),
        new(WspMetric.SM, "SM", "the smaller margin",
            "the smaller of the two margins",         WspMetricGroup.Margin, true, false, ""),
        new(WspMetric.rY, "rY", "real proxy of ZG, ZL",
            "real-part proxy of ZG, ZL ([M] M-rY)",   WspMetricGroup.Margin, true, false, ""),
        new(WspMetric.iY, "iY", "imag proxy of ZG, ZL",
            "imaginary-part proxy of ZG, ZL ([M] M-iY)", WspMetricGroup.Margin, true, false, ""),
        new(WspMetric.rH, "rH", "real proxy of YG, YL",
            "real-part proxy of YG, YL ([M] M-rH)",   WspMetricGroup.Margin, true, false, ""),
        new(WspMetric.iH, "iH", "imag proxy of YG, YL",
            "imaginary-part proxy of YG, YL ([M] M-iH)", WspMetricGroup.Margin, true, false, ""),

        new(WspMetric.F_LGa, "F_LGa", "return difference, 1 - LGa",
            "return difference 1 - LGa (Eq. 144)", WspMetricGroup.Pair, false, false, ""),
        new(WspMetric.F_LGf, "F_LGf", "return difference, 1 - LGf",
            "return difference 1 - LGf (Eq. 145)", WspMetricGroup.Pair, false, false, ""),
        new(WspMetric.F_LGH, "F_LGH", "return difference, 1 - LGH",
            "return difference 1 - LGH (Eq. 146)", WspMetricGroup.Pair, false, false, ""),
        new(WspMetric.F_LGM, "F_LGM", "return difference, 1 - LGM",
            "return difference 1 - LGM (Eq. 147)", WspMetricGroup.Pair, false, false, ""),
        new(WspMetric.LGa, "LGa", "two-block",
            "two-block loop gain (Eq. 148)",       WspMetricGroup.Pair, false, false, ""),
        new(WspMetric.LGf, "LGf", "feedback as synthetic FET",
            "feedback-as-synthetic-FET loop gain (Eq. 149)", WspMetricGroup.Pair, false, false, ""),
        new(WspMetric.LGH, "LGH", "two-block Hurst",
            "two-block Hurst loop gain (Eq. 150)", WspMetricGroup.Pair, false, false, ""),
        new(WspMetric.LGM, "LGM", "two-block Middlebrook",
            "two-block Middlebrook loop gain (Eq. 151)", WspMetricGroup.Pair, false, false, ""),

        new(WspMetric.OhtomoG, "G", "Ohtomo global G_i",
            "Ohtomo global loop gain G_i over the probe set (Eq. 179)",
                                                                             WspMetricGroup.ProbeSet, false, false, ""),

        new(WspMetric.EnvInvH0, "1/H0env", "Kurokawa locus on H0'",
            "Kurokawa's locus on H0' of the re-terminated circuit (Eq. 187-191)",
                                                                             WspMetricGroup.Envelope, false, false, "S"),
        new(WspMetric.EnvInvY0, "1/Y0env", "Kurokawa locus on Y0'",
            "Kurokawa's locus on Y0' of the re-terminated circuit (Eq. 187-191)",
                                                                             WspMetricGroup.Envelope, false, false, "Ohm"),
        new(WspMetric.EnvUnstable, "unstable", "start-up frequencies, counted",
            "Kurokawa start-up frequencies found at each termination, counted",
                                                                             WspMetricGroup.Envelope, true,  false, ""),
        new(WspMetric.SMenv, "SMenv", "margin minimum vs termination",
            "the stability margin's minimum over frequency, per termination ([E] Fig. 6-9)",
                                                                             WspMetricGroup.Envelope, true,  false, ""),
        new(WspMetric.NDFenc, "NDFenc", "NDF encirclements",
            "NDF origin encirclements at each termination, from a passivated run ([E] Fig. 6-9)",
                                                                             WspMetricGroup.Envelope, true,  false, ""),
    ];

    private static readonly Dictionary<WspMetric, WspMetricInfo> ByMetric =
        All.ToDictionary(m => m.Metric);

    /// <summary>The document's own spellings, EXACTLY — including case.</summary>
    private static readonly Dictionary<string, WspMetric> ByExactName =
        All.ToDictionary(m => m.Name, m => m.Metric, StringComparer.Ordinal);

    /// <summary>
    /// The case- and punctuation-insensitive lookup, built ONLY from names that have no collision
    /// under it.
    ///
    /// <para><b>Case is load-bearing in this notation and cannot be folded away.</b> The document
    /// writes <c>LGF</c> for the forward synthetic-circulator loop gain of ONE probe (Eq. 99) and
    /// <c>LGf</c> for the feedback-as-synthetic-FET loop gain of a probe PAIR (Eq. 149); likewise
    /// <c>LG_H</c> and <c>LGH</c>, <c>LG_MF</c>/<c>LG_MR</c> and <c>LGM</c>. Folding case would
    /// silently answer one question with the other's number. So a name whose canonical form is
    /// shared is simply not in this table, and only its exact spelling resolves.</para>
    /// </summary>
    private static readonly Dictionary<string, WspMetric> ByLooseName = BuildLooseNames();

    private static Dictionary<string, WspMetric> BuildLooseNames()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in All)
        {
            string k = Canonical(m.Name);
            counts[k] = counts.TryGetValue(k, out int n) ? n + 1 : 1;
        }
        var loose = new Dictionary<string, WspMetric>(StringComparer.Ordinal);
        foreach (var m in All)
        {
            string k = Canonical(m.Name);
            if (counts[k] == 1) loose[k] = m.Metric;
        }
        return loose;
    }

    /// <summary>The row for <paramref name="m"/>, or null for <see cref="WspMetric.None"/>.</summary>
    public static WspMetricInfo? Info(WspMetric m)
        => ByMetric.TryGetValue(m, out var info) ? info : null;

    /// <summary>The document's spelling, or "" for <see cref="WspMetric.None"/>.</summary>
    public static string Name(WspMetric m) => Info(m)?.Name ?? "";

    /// <summary>
    /// A metric from the name a <c>--trace metric=</c> or a <c>.cdd</c> carries.
    ///
    /// <para>Four passes, tightest first: the document's exact spelling; the enum member's own name
    /// (case-sensitively — <c>LGF</c> and <c>LGf</c> are two members and two quantities); the
    /// command-line aliases for the names the document writes with characters a shell does not take
    /// (<c>1/H0</c> is <c>invH0</c>); and finally the loose form, for the majority of names that
    /// have no case- or punctuation-collision. A name that collides resolves only exactly — see
    /// <see cref="ByLooseName"/> for why that is deliberate.</para>
    /// </summary>
    public static bool TryParse(string? name, out WspMetric metric)
    {
        metric = WspMetric.None;
        if (string.IsNullOrWhiteSpace(name)) return false;
        string text = name.Trim();

        if (ByExactName.TryGetValue(text, out metric)) return true;
        if (Enum.TryParse(text, ignoreCase: false, out metric) && metric != WspMetric.None) return true;

        metric = Canonical(text) switch
        {
            "invh0" => WspMetric.InvH0,
            "invy0" => WspMetric.InvY0,
            // The placeholders WSP-9 retired, pointed at the published quantities that replaced
            // them, so a display or a script written against them keeps working rather than
            // failing with a name nobody can look up any more (overview D-12).
            "nz" => WspMetric.InvH0,
            "ny" => WspMetric.InvY0,
            "gamma" or "nodalgamma" or "wspnodalgamma" => WspMetric.NodalGamma,
            "ohtomo" or "ohtomog" or "gi" => WspMetric.OhtomoG,
            "invh0env" or "1h0env" => WspMetric.EnvInvH0,
            "invy0env" or "1y0env" => WspMetric.EnvInvY0,
            "stabilitymargin" or "sm" => WspMetric.SM,
            _ => WspMetric.None,
        };
        if (metric != WspMetric.None) return true;

        return ByLooseName.TryGetValue(Canonical(text), out metric) && metric != WspMetric.None;
    }

    private static string Canonical(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        int n = 0;
        foreach (char c in s)
            if (char.IsLetterOrDigit(c)) buf[n++] = char.ToLowerInvariant(c);
        return new string(buf[..n]);
    }

    /// <summary>True when the metric needs a SECOND probe ("with probe") to be computable.</summary>
    public static bool NeedsPair(WspMetric m) => Info(m)?.Group == WspMetricGroup.Pair;

    /// <summary>True when the metric needs an ordered probe SET.</summary>
    public static bool NeedsProbeSet(WspMetric m) => Info(m)?.Group == WspMetricGroup.ProbeSet;

    /// <summary>True when the metric is read off the stability ENVELOPE — a source probe, a load
    /// probe and the two Γ ladders on the card's Envelope sub-card, rather than the one probe every
    /// other metric needs (R-wsp4-9).</summary>
    public static bool NeedsEnvelope(WspMetric m) => Info(m)?.Group == WspMetricGroup.Envelope;

    /// <summary>
    /// True when the metric reads the <c>Z0</c> field on the card: the two synthetic-circulator loop
    /// gains normalise by it (Eq. 96–99), every pair metric scatters its two blocks at it
    /// (Eq. 142/143), and Ohtomo's <c>SA</c>/<c>SP</c> are reflection coefficients in it. Everything
    /// else ignores it, and the card hides the field rather than showing an inert one.
    /// </summary>
    public static bool UsesZ0(WspMetric m)
        => m is WspMetric.LGF or WspMetric.LGR
        || Info(m)?.Group is WspMetricGroup.Pair or WspMetricGroup.ProbeSet or WspMetricGroup.Envelope;

    /// <summary>
    /// Whether <paramref name="m"/> can be drawn on <paramref name="plotType"/>, and why not when it
    /// cannot (R-wsp4-5's gating, R-wsp4-14g). The rule is the metric's OWN kind rather than a
    /// per-member list, so a metric added later cannot be forgotten here.
    ///
    /// <para>A Table carries anything: it renders complex and scalar cells alike, which is the same
    /// exemption <see cref="DerivedParameters"/>'s gating already makes.</para>
    /// </summary>
    public static string? DisabledReasonOn(WspMetric m, PlotType plotType)
    {
        if (Info(m) is not { } info) return null;
        if (plotType == PlotType.Table) return null;

        // A 3D surface is a function of DIRECTION, and not one probe quantity is — every one of them
        // is a number, or a point in a plane, per frequency. Asked before the kind rules below,
        // because the answer is the same for all three groups (owner, 2026-09-11).
        if (SurfaceResolve.DisabledReasonOn(plotType, null) is { } surfaceReason) return surfaceReason;

        bool complexPlane = plotType is PlotType.Smith or PlotType.Polar;

        if (info.Group == WspMetricGroup.Margin)
            return complexPlane
                ? "The stability margin is a real number in [0, 1] versus frequency — add it to a "
                + "rectangular (or table) plot. (Winslow, EuMIC 2024.)"
                : null;

        // Every OTHER real envelope quantity is a number per termination, not a point in a plane:
        // the same rule as the margin, stated in its own terms so the reason names the quantity the
        // reader picked rather than a family they did not.
        if (info.Group == WspMetricGroup.Envelope && info.IsReal)
            return complexPlane
                ? $"{info.Name} is one real number per termination, not a point in a plane — add it "
                + "to a rectangular (or table) plot against \u03b8S or \u03b8L. (Winslow, EuMIC 2025.)"
                : null;

        if (!complexPlane) return null;                       // every complex quantity reads on Rect
        if (plotType == PlotType.Polar) return null;          // and every one of them on Polar
        return info.OnSmith
            ? null
            : $"{info.Name} is not an immittance or a reflection coefficient, so a Smith chart has "
            + "no grid for it — add it to a Polar or a rectangular plot.";
    }

    /// <summary>
    /// The SHORT name of this trace — the document's own name for the quantity and the probe it is
    /// taken at, <c>1/Y0 @ GATE</c>, <c>LGa @ G1→D1</c>, <c>SMenv @ PG</c>.
    ///
    /// <para>The axis and legend label, where <see cref="AccessorText"/> is the measure line. The
    /// two are different jobs: a plot carrying <c>1/H0</c> and <c>1/Y0</c> of ONE probe — which is
    /// the reading the reference document's §4.10 is entirely about — labelled both traces
    /// <c>SP1.wsp</c> until this existed, because the label was built from the raw matrix the values
    /// are computed FROM rather than from the quantity. The full accessor is correct and is far too
    /// long for an axis.</para>
    /// </summary>
    public static string ShortLabel(WspTraceSpec spec)
    {
        string name = Name(spec.Metric);
        if (name.Length == 0) return "";
        if (NeedsPair(spec.Metric) && spec.With.Length > 0)
            return $"{name} @ {spec.Probe}\u2192{spec.With}";
        if (spec.Metric == WspMetric.OhtomoG)
            return spec.Set.Count > 0
                ? $"G{spec.SetIndex} @ {string.Join(",", spec.Set)}"
                : $"G{spec.SetIndex}";
        return spec.Probe.Length > 0 ? $"{name} @ {spec.Probe}" : name;
    }

    /// <summary>
    /// What a PINNED axis of a WSProbe trace is called in its label, or null when it says nothing
    /// and should be left out.
    ///
    /// <para><c>row</c> and <c>col</c> always say nothing: they index the raw <c>wsp</c> matrix the
    /// value is computed FROM, and the quantity is already named. The four envelope grid axes are
    /// named from the SPEC rather than from the axis, because the cube the label is built against is
    /// <c>wsp</c> itself, which does not carry them — so the generic path prints the bare index, and
    /// <c>rhoS=0</c> for a ladder whose only rung is 0.9 reads as an unpulled source, which is the
    /// opposite of what the trace shows. A side that is not pulled contributes nothing at all.</para>
    /// </summary>
    public static string? PinToken(WspTraceSpec spec, string axisName, int index)
    {
        switch (axisName)
        {
            case "row" or "col":
                return null;

            case "rhoS" or "thetaS" when spec.SourceProbe.Length == 0 || spec.GammaSMags.Count == 0:
            case "rhoL" or "thetaL" when spec.LoadProbe.Length   == 0 || spec.GammaLMags.Count == 0:
                return null;

            case "rhoS": return MagToken("|ΓS|", spec.GammaSMags, index);
            case "rhoL": return MagToken("|ΓL|", spec.GammaLMags, index);
            case "thetaS" or "thetaL":
                return $"θ{(axisName[^1] == 'S' ? "S" : "L")}="
                     + (index * spec.ThetaStepDeg).ToString("0.##", CultureInfo.InvariantCulture) + "°";
            default:
                return "";   // "" = no opinion; the generic path names it
        }
    }

    private static string? MagToken(string name, IReadOnlyList<double> mags, int index)
        => index >= 0 && index < mags.Count
            ? $"{name}={mags[index].ToString("0.####", CultureInfo.InvariantCulture)}"
            : null;

    /// <summary>
    /// The measure-line spelling of this trace — the accessor grammar of overview D-5, which is what
    /// the document itself writes and what a <c>measure</c> line would type to get the same numbers.
    ///
    /// <para>It is a NAME for what the card's own pickers author, not a cube shorthand the spec
    /// parser reads back: the card shows it so a reader can carry the trace into a measure line, and
    /// <c>TraceRowViewModel.CommitSpec</c> treats retyping it unchanged as a no-op for that reason.</para>
    /// </summary>
    public static string AccessorText(WspTraceSpec spec, string? cubeSpec)
    {
        string wsp   = cubeSpec ?? "wsp";
        string group = wsp.LastIndexOf('.') is int d && d > 0 ? wsp[..d] : "";
        string idx   = group.Length > 0 ? $"{group}.idx(\"{spec.Probe}\")" : $"idx(\"{spec.Probe}\")";

        if (NeedsPair(spec.Metric))
        {
            string idx2 = group.Length > 0 ? $"{group}.idx(\"{spec.With}\")" : $"idx(\"{spec.With}\")";
            int one = spec.Metric switch
            {
                WspMetric.F_LGa => 9,  WspMetric.F_LGf => 10, WspMetric.F_LGH => 11, WspMetric.F_LGM => 12,
                WspMetric.LGa   => 13, WspMetric.LGf   => 14, WspMetric.LGH   => 15, WspMetric.LGM   => 16,
                _ => 0,
            };
            return $"wsp_block_calc({wsp}, {idx}, {idx2}){{{one}}}";
        }

        if (NeedsEnvelope(spec.Metric))
        {
            string idxS = spec.SourceProbe.Length > 0
                ? (group.Length > 0 ? $"{group}.idx(\"{spec.SourceProbe}\")" : $"idx(\"{spec.SourceProbe}\")")
                : "0";
            string idxL = spec.LoadProbe.Length > 0
                ? (group.Length > 0 ? $"{group}.idx(\"{spec.LoadProbe}\")" : $"idx(\"{spec.LoadProbe}\")")
                : "0";
            string gS = GammaGridText(spec.GammaSMags, spec.ThetaStepDeg);
            string gL = GammaGridText(spec.GammaLMags, spec.ThetaStepDeg);
            string args = $"{wsp}, {idxS}, {idxL}, {idx}, {gS}, {gL}";
            return spec.Metric switch
            {
                WspMetric.EnvInvH0    => $"1 / wsp_loadpull({args}){{H0env}}",
                WspMetric.EnvInvY0    => $"1 / wsp_loadpull({args}){{Y0env}}",
                WspMetric.EnvUnstable => $"wsp_loadpull_unstable({args}){{unstable}}",
                WspMetric.SMenv       => $"wsp_loadpull_margin_env({args})",
                _                     => $"wsp_loadpull_ndf_enc({wsp}, {PassiveText(spec, wsp)}, {idxS}, {idxL}, "
                                       + $"\"all\", {gS}, {gL})",
            };
        }

        if (spec.Metric == WspMetric.OhtomoG)
        {
            string set = string.Join(", ", spec.Set.Select(l => group.Length > 0
                ? $"{group}.idx(\"{l}\")" : $"idx(\"{l}\")"));
            return $"wsp_loopgain_ohtomo({wsp}, [{set}], \"{spec.ActiveSide}\"){{{spec.SetIndex}}}";
        }

        return spec.Metric switch
        {
            WspMetric.H0    => $"wsp_H0({wsp}, {idx})",
            WspMetric.Y0    => $"wsp_Y0({wsp}, {idx})",
            WspMetric.InvH0 => $"1 / wsp_H0({wsp}, {idx})",
            WspMetric.InvY0 => $"1 / wsp_Y0({wsp}, {idx})",
            WspMetric.ZG    => $"wsp_ZG({wsp}, {idx})",
            WspMetric.ZL    => $"wsp_ZL({wsp}, {idx})",
            WspMetric.YG    => $"wsp_YG({wsp}, {idx})",
            WspMetric.YL    => $"wsp_YL({wsp}, {idx})",
            WspMetric.Zop   => $"wsp_zop({wsp}, {idx})",
            WspMetric.Yop   => $"wsp_yop({wsp}, {idx})",
            WspMetric.LG    => $"wsp_loopgain({wsp}, {idx}, \"BI\")",
            WspMetric.F     => $"1 - wsp_loopgain({wsp}, {idx}, \"BI\")",
            WspMetric.LGF   => $"wsp_loopgain({wsp}, {idx}, \"UNI\")",
            WspMetric.LGR   => $"wsp_loopgain({wsp}, {idx}, \"REV\")",
            WspMetric.LG_H  => $"wsp_loopgain({wsp}, {idx}, \"HST\")",
            WspMetric.LG_MF => $"wsp_loopgain({wsp}, {idx}, \"MB\")",
            WspMetric.LG_MR => $"wsp_loopgain({wsp}, {idx}, \"MBR\")",
            WspMetric.LG_MGF => $"wsp_loopgain({wsp}, {idx}, \"GFT\")",
            WspMetric.LG_MGR => $"wsp_loopgain({wsp}, {idx}, \"GFTR\")",
            WspMetric.NodalGamma => $"wsp_nodal_gamma({wsp}, {idx})",
            WspMetric.SM_Y0 => $"wsp_SM_Y0({wsp}, {idx})",
            WspMetric.SM_H0 => $"wsp_SM_H0({wsp}, {idx})",
            WspMetric.SM    => $"wsp_stability_margin({wsp}, {idx})",
            WspMetric.rY    => $"wsp_rY({wsp}, {idx})",
            WspMetric.iY    => $"wsp_iY({wsp}, {idx})",
            WspMetric.rH    => $"wsp_rH({wsp}, {idx})",
            WspMetric.iH    => $"wsp_iH({wsp}, {idx})",
            _ => wsp,
        };
    }

    /// <summary>The <c>"|\u0393|:count@start"</c> grid spelling <c>wsp_loadpull</c>'s own argument
    /// takes, for one side's ladder — one term per rung, since a ladder is several circles rather
    /// than one. An empty ladder is the side's "off" state and spells as <c>0</c>.</summary>
    private static string GammaGridText(IReadOnlyList<double> mags, double thetaStepDeg)
    {
        if (mags.Count == 0) return "0";
        int count = ThetaCount(thetaStepDeg);
        return mags.Count == 1
            ? $"\"{mags[0].ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}:{count}\""
            : "[" + string.Join(", ", mags.Select(m =>
                $"\"{m.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}:{count}\"")) + "]";
    }

    /// <summary>The passivated run a <c>NDFenc</c> trace reads, as the measure line would name it.</summary>
    private static string PassiveText(WspTraceSpec spec, string wsp)
        => spec.PassiveSource.Length > 0 ? spec.PassiveSource : PassiveCubeSpecOf(wsp);

    /// <summary>The <c>wsp_passive</c> cube beside a group's own <c>wsp</c> — the default a
    /// <c>NDFenc</c> trace reads when the card names no second run (WSP-6's own emission).</summary>
    public static string PassiveCubeSpecOf(string wspCubeSpec)
    {
        int dot = wspCubeSpec.LastIndexOf('.');
        return dot < 0 ? "wsp_passive" : $"{wspCubeSpec[..dot]}.wsp_passive";
    }

    /// <summary>The number of \u03b8 steps a step size gives over one full turn, at least 1. A step
    /// that does not divide 360 is rounded to the nearest whole number of points rather than
    /// refused, and the axis carries the angles it actually used.</summary>
    public static int ThetaCount(double stepDeg)
    {
        if (!(stepDeg > 0.0) || double.IsNaN(stepDeg)) return 1;
        int n = (int)Math.Round(360.0 / stepDeg);
        return Math.Max(1, n);
    }

    /// <summary>
    /// The driving-point function whose Kurokawa search PAIRS with a margin trace: <c>SM_Y0</c> is
    /// the distance and <c>1/Y0</c> is the detector, so the card reads both on one line (WSP-9
    /// §2.1f, R-wsp4-7). <see cref="WspMetric.None"/> for anything that is not a margin.
    /// </summary>
    public static WspMetric MarginCompanion(WspMetric m) => m switch
    {
        WspMetric.SM_Y0 or WspMetric.rY or WspMetric.iY => WspMetric.Y0,
        WspMetric.SM_H0 or WspMetric.rH or WspMetric.iH => WspMetric.H0,
        _ => WspMetric.None,
    };

    /// <summary>The default Rect transform for a freshly picked metric: dB for a margin (the
    /// paper's own axis, overview D-16), magnitude for everything else.</summary>
    public static CubeTransform DefaultTransform(WspMetric m, PlotType plotType)
    {
        if (plotType is PlotType.Smith or PlotType.Polar) return CubeTransform.None;
        // A COUNT is drawn as it stands: |x| would fold a negative encirclement count (a
        // counter-clockwise net, which is a real answer) onto the positive one silently.
        if (m is WspMetric.EnvUnstable or WspMetric.NDFenc) return CubeTransform.None;
        return Info(m)?.Group is WspMetricGroup.Margin || m == WspMetric.SMenv
            ? CubeTransform.dB20
            : CubeTransform.Mag;
    }
}
