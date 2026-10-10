// Trace Impedance Analysis — every trace on the chosen layers, end to end, against a target Z0 and a
// tolerance, with where the return path under each one breaks (round-8 field report; the layout
// review a board designer carries out before sending artwork to a fabricator with an impedance-control
// note). The probe (TraceImpedanceProbe) answers "what is THIS trace" at one click; this answers it for
// the whole board, station by station, from the same cross-section solve.
//
// ── FINDING TRACES IN COPPER THAT HAS NO TRACES ────────────────────────────────────────────────
//
// Imported artwork is unioned polygons: a trace is not an object, it is a stretch of copper with two
// long parallel edges facing each other. So that is what is looked for.
//
// 1. PIECES. Every pair of long edges on one layer that are anti-parallel (within 2°), face each other
//    across copper (each on the other's copper side, and nothing in between — a ray from one hits the
//    other first), are at most MaxWidth apart and overlap along their length, bounds a strip: a PIECE,
//    with a centre line, a width and a direction. Pieces tile a trace: where either edge ends (a
//    vertex, a jog, a bend, a pad) one piece ends and the next begins.
// 2. POURS. A copper island whose area is many times the area of the pieces found in it is a pour or a
//    plane, and its pieces are the slivers between its antipads — not traces. So is an island that
//    carries a row of vias. Those are skipped, and the report says how many.
// 3. CHAINS. Piece ends within about one width of each other, on the same island, are joined —
//    straight through a jog, round a bend or a mitre, across a width step. An end with TWO such
//    neighbours is a junction, and every trace meeting there ends there. The corner region of a bend
//    is not cut (a hard angle has no single width, the probe's own rule) and is not flagged. A via
//    ends a trace: what continues on another layer is that layer's trace.
// 4. STATIONS. Each piece is cut every half width along its centre line. Every cut is the probe's
//    own cut (TraceCrossSection) — the reference found by COPPER COVERAGE, every other conductor in
//    reach held at ground — and cuts with the same geometry share one solve, which is what makes a
//    whole board affordable: on a real board almost every station along a trace is a repeat.
// 5. FINDINGS, per trace: where Z0 leaves target ± tolerance; where the nearest layer below (or
//    above) stops covering the trace while covering it elsewhere along the same trace (a broken
//    return); where the reference itself steps to another layer; and what each end is. Each finding
//    is a WARNING or a FAIL (brief-impedance-1): a review needs "look at this" apart from "this is
//    wrong", and the table at Assemble is the product's position on which is which.

using System.Diagnostics;
using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Engine;

namespace CircuitRF.Design.Layout.Em;

/// <summary>What <see cref="TraceImpedanceAnalysis.Analyze"/> is asked for.</summary>
public sealed record TraceImpedanceOptions
{
    /// <summary>The characteristic impedance every trace is held to.</summary>
    public double TargetOhms { get; init; } = DefaultTargetOhms;

    /// <summary>± this many percent of <see cref="TargetOhms"/> passes.</summary>
    public double TolerancePercent { get; init; } = DefaultTolerancePercent;

    /// <summary>± this many percent of <see cref="TargetOhms"/> is a WARNING rather than a fail: a
    /// stretch outside the tolerance but inside this band warns. A second percentage, not a multiple
    /// of the tolerance (owner, 2026-09-26); it must be wider than the tolerance and below 100 %.</summary>
    public double WarningPercent { get; init; } = DefaultWarningPercent;

    /// <summary>The highest frequency the traces carry, or null. When set, a stretch outside the
    /// warning band that is shorter than λ/<see cref="TraceImpedanceAnalysis.ShortFraction"/> there
    /// (λ from each station's own ε_eff) is a warning, not a fail: a neck-down into a pad is not what
    /// fails a line at the frequency it carries. Null turns the rule off.</summary>
    public double? MaxFrequencyHz { get; init; }

    /// <summary>The copper layers to analyse; null for every drawing layer bound to a conductor of the
    /// stackup that carries copper.</summary>
    public IReadOnlyList<LayerKey>? Layers { get; init; }

    /// <summary>The widest copper read as a trace; null derives it per layer from the stackup
    /// (ten times the distance to the nearest other conductor, between 1 and 8 mm).</summary>
    public double? MaxWidthMicrons { get; init; }

    /// <summary>How lengths and coordinates read in the report. Null — the default — is the
    /// LAYOUT's own display unit (the <c>.clay</c>'s <c>DisplayUnit</c>), which is what a report
    /// about that layout must speak (owner, 2026-09-25); µm where there is no layout to ask.</summary>
    public LayoutUnit? DisplayUnit { get; init; }

    /// <summary>Which traces are reviewed; null — or an empty scope — for every trace, as before
    /// there was a scope (brief-impedance-2).</summary>
    public TraceImpedanceScope? Scope { get; init; }

    /// <summary>
    /// Trace within this distance of the land of a via ON that trace is not checked; 0 checks it all.
    /// A plane is normally cleared round a signal via (the antipad), so the last stretch of every
    /// trace into one has no reference under it by design, and flagging it flagged every layer
    /// transition on a board (round-10 field report). The stretch is reported as not checked — a
    /// note on the trace naming the via — never dropped silently: the transition is an EM run's to
    /// verify, not this review's to pass.
    /// </summary>
    public double ViaTransitionMicrons { get; init; } = DefaultViaTransitionMicrons;

    public const double DefaultTargetOhms = 50;

    /// <summary>
    /// 400 µm (≈ 16 mil). An antipad is usually the via land plus 0.2–0.3 mm of clearance all round;
    /// a cut stands for half a width of trace on either side of it; and the reference takes about a
    /// dielectric height beyond the plane's edge to settle. On the six-layer board the report came
    /// from, every broken-return stretch at a via ended within 280 µm of the land.
    /// </summary>
    public const double DefaultViaTransitionMicrons = 400;
    public const double DefaultTolerancePercent = 10;
    public const double DefaultWarningPercent = 20;
}

/// <summary>What kind of finding a <see cref="TraceIssue"/> is.</summary>
public enum TraceIssueKind
{
    /// <summary>Z0 is outside target ± tolerance over this stretch.</summary>
    OutOfTolerance,
    /// <summary>The nearest layer below or above stops covering the trace here, and covers it
    /// elsewhere along the same trace: the return path is broken.</summary>
    ReturnBroken,
    /// <summary>The nearest layer covers only part of the trace's width here.</summary>
    PartialReference,
    /// <summary>The reference steps from one layer to another here.</summary>
    ReferenceStep,
    /// <summary>No copper covers the trace on either side here.</summary>
    NoReference,
    /// <summary>The cross-section could not be solved here.</summary>
    Unsolved,
}

/// <summary>How much a finding counts against its trace.</summary>
public enum IssueSeverity
{
    /// <summary>Look at this: the reviewer decides.</summary>
    Warning,
    /// <summary>This is wrong.</summary>
    Fail,
}

/// <summary>One finding on one trace, at a stretch of it. Coordinates in DBU.</summary>
public sealed record TraceIssue(
    TraceIssueKind Kind, IssueSeverity Severity, long X0, long Y0, long X1, long Y1, string Text)
{
    public long X => (X0 + X1) / 2;
    public long Y => (Y0 + Y1) / 2;

    /// <summary>Whether this finding fails the trace.</summary>
    public bool Fails => Severity == IssueSeverity.Fail;

    /// <summary>The acceptance this finding is covered by, or null (brief-impedance-5 R-imp5-2a). An
    /// accepted finding is still reported, marked ACCEPTED; it only stops counting against the verdict.
    /// Set by <see cref="TraceImpedanceAcceptance.Apply"/>, never by the analysis.</summary>
    public TraceImpedanceAcceptance? Accepted { get; init; }

    /// <summary>The finding's own sentence when <see cref="Text"/> carries more — an acceptance that no
    /// longer covers it adds "Accepted at 55.5 Ω, now 58.1 Ω." — or null when it carries nothing more.</summary>
    public string? Finding { get; init; }

    /// <summary>The finding's own sentence, as the analysis wrote it.</summary>
    public string FindingText => Finding ?? Text;
}

/// <summary>One cut along a trace. Coordinates and lengths in DBU.</summary>
public sealed record TraceStation
{
    public long X { get; init; }
    public long Y { get; init; }

    /// <summary>The cut direction (across the trace), unit.</summary>
    public double Ux { get; init; }
    public double Uy { get; init; }

    /// <summary>Distance along the trace from its start to this cut.</summary>
    public double S { get; init; }

    /// <summary>The length of trace this cut stands for.</summary>
    public double Length { get; init; }

    public double Width { get; init; }
    public double? Z0 { get; init; }
    public double? Eeff { get; init; }
    public string? Configuration { get; init; }
    public string? ReferenceBelow { get; init; }
    public string? ReferenceAbove { get; init; }
    public double? GapLeft { get; init; }
    public double? GapRight { get; init; }

    /// <summary>The dielectric height the classifier's coplanar threshold is measured against, DBU — to the
    /// reference below, else above, else the width (<c>TraceCut.HDbu</c>). Artwork recognition re-applies
    /// that threshold with a factor of its own (brief-artsch-5 R-as5-2).</summary>
    public double? H { get; init; }

    /// <summary>Why there is no Z0 here, or null.</summary>
    public string? Refusal { get; init; }
}

/// <summary>
/// A bend between two pieces of one trace (brief-artsch-5 R-as5-1): where the centre lines meet, how far
/// the trace turns there, and the chamfer measured on the outer corner. DBU.
/// </summary>
/// <param name="After">The piece before the corner, an index into <see cref="TraceRun.Pieces"/>.</param>
/// <param name="X">The corner point — the two pieces' centre lines meet here.</param>
/// <param name="Y">DBU.</param>
/// <param name="TurnDeg">How far the trace turns, degrees: positive to the left (counter-clockwise), 0
/// straight on, ±90 a right angle.</param>
/// <param name="Width">The trace's width at the corner — the mean of the two pieces'.</param>
/// <param name="CutLeg">The chamfer on the outer corner, measured along each outer edge back from where the
/// sharp corner would be (<c>MicrostripDiscontinuities.MiterCutLength</c>'s own measure); 0 for a square
/// corner.</param>
public sealed record TraceCorner(int After, long X, long Y, double TurnDeg, double Width, double CutLeg);

/// <summary>One arm of a <see cref="TraceJunction"/>: a trace, and which of its ends is there.</summary>
public sealed record TraceJunctionArm(string TraceId, bool AtStart);

/// <summary>
/// Where three traces or more meet (brief-artsch-5 R-as5-1): the point their centre lines come closest to
/// (least squares), DBU, and every trace with an end there. A member the review left out — a pad-length
/// stub, a trace outside the scope — is not listed, so a junction may have fewer arms than copper meets it.
/// </summary>
public sealed record TraceJunction(string Id, LayerKey Layer, long X, long Y, IReadOnlyList<TraceJunctionArm> Arms);

/// <summary>A trace's result. <see cref="Warning"/> is APPENDED rather than placed between Pass and
/// Fail, so a number stored anywhere keeps its meaning.</summary>
public enum TraceVerdict { Pass, Fail, Unsolved, Warning }

/// <summary>One trace, end to end on one layer.</summary>
public sealed record TraceRun
{
    /// <summary>"T1", "T2", … — numbered across the whole report, so a row and a label on a page
    /// name the same trace.</summary>
    public string Id { get; init; } = "";

    public LayerKey Layer { get; init; }
    public string LayerName { get; init; } = "";

    /// <summary>The cuts, in order from <see cref="StartX"/>, <see cref="StartY"/>.</summary>
    public IReadOnlyList<TraceStation> Stations { get; init; } = [];

    /// <summary>The centre line as drawn by the pieces, start to end: pairs of points, each pair
    /// one straight piece, with the gaps between pairs being bends, jogs or steps. DBU.</summary>
    public IReadOnlyList<(long X0, long Y0, long X1, long Y1, double Width)> Pieces { get; init; } = [];

    /// <summary>The bends between consecutive pieces — a turn of <see cref="TraceImpedanceAnalysis.CornerMinDeg"/>
    /// or more — in order along the trace.</summary>
    public IReadOnlyList<TraceCorner> Corners { get; init; } = [];

    public long StartX { get; init; }
    public long StartY { get; init; }
    public long EndX { get; init; }
    public long EndY { get; init; }

    /// <summary>What each end is: "via", "pad", "junction", "open end" or "continues".</summary>
    public string StartsAt { get; init; } = "";
    public string EndsAt { get; init; } = "";

    /// <summary>The <see cref="TraceJunction.Id"/> the start (end) is on, or null.</summary>
    public string? StartJunction { get; init; }
    public string? EndJunction { get; init; }

    public double Length { get; init; }
    public double WidthMin { get; init; }
    public double WidthMax { get; init; }

    public double? Z0Min { get; init; }
    public double? Z0Max { get; init; }

    /// <summary>Length-weighted over the solved stations.</summary>
    public double? Z0Mean { get; init; }

    /// <summary>The fraction of the solved length inside target ± tolerance, 0–1.</summary>
    public double InTolerance { get; init; }

    /// <summary>The line type over most of the trace — "grounded coplanar waveguide", "microstrip",
    /// "stripline"… (<see cref="TraceStation.Configuration"/>'s own names).</summary>
    public string Configuration { get; init; } = "";

    /// <summary>Every line type the trace is along some of its length, with its share of the solved
    /// length (0–1), most first. A trace is often BOTH: grounded CPW where the side ground runs beside
    /// it and microstrip where the ground falls away.</summary>
    public IReadOnlyList<(string Name, double Share)> Configurations { get; init; } = [];

    /// <summary>The line types in short form with their shares — "GCPW 80%, Microstrip 20%" — or the one
    /// type alone when there is only one.</summary>
    public string TypeSummary => Configurations.Count switch
    {
        0 => "—",
        1 => TraceImpedanceReport.ShortType(Configurations[0].Name),
        _ => string.Join(", ", Configurations.Select(c => $"{TraceImpedanceReport.ShortType(c.Name)} {c.Share * 100:0}%")),
    };

    public IReadOnlyList<string> References { get; init; } = [];
    public IReadOnlyList<TraceIssue> Issues { get; init; } = [];

    /// <summary>Things said about the trace that are not faults — a layer cleared under the whole
    /// length, for instance.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public TraceVerdict Verdict { get; init; }

    /// <summary>The findings on this trace covered by an acceptance.</summary>
    public int AcceptedCount => Issues.Count(i => i.Accepted is not null);

    /// <summary>A trace's verdict, from its UN-accepted findings only (R-imp5-2a): unsolved when no cut
    /// was solved, else fail, warning or pass by the worst of them.</summary>
    public static TraceVerdict VerdictOf(bool anySolved, IEnumerable<TraceIssue> issues)
    {
        if (!anySolved) return TraceVerdict.Unsolved;
        var counted = issues.Where(i => i.Accepted is null).ToList();
        return counted.Any(i => i.Fails) ? TraceVerdict.Fail : counted.Count > 0 ? TraceVerdict.Warning : TraceVerdict.Pass;
    }
}

/// <summary>One analysed layer.</summary>
public sealed record TraceLayerResult
{
    public LayerKey Layer { get; init; }
    public string Name { get; init; } = "";
    public IReadOnlyList<TraceRun> Traces { get; init; } = [];

    /// <summary>Where three traces or more meet on this layer.</summary>
    public IReadOnlyList<TraceJunction> Junctions { get; init; } = [];

    /// <summary>The layer's unioned copper, as flat x,y rings in DBU — what the page draws under the
    /// map.</summary>
    public IReadOnlyList<long[]> Copper { get; init; } = [];

    /// <summary>Copper islands read as pours or planes and not analysed.</summary>
    public int PoursSkipped { get; init; }

    /// <summary>Traces found on this layer and left out by the scope — counted, never listed.</summary>
    public int OutOfScope { get; init; }

    /// <summary>Those traces' keys (<see cref="TraceImpedanceAcceptance.TraceKeyOf"/>), so an acceptance
    /// of one is neither applied nor called stale (R-imp5-2c).</summary>
    public IReadOnlyList<string> OutOfScopeTraceKeys { get; init; } = [];

    /// <summary>Traces found on this layer whose copper carries no net, or two (R-imp4-3b) — which no
    /// net selector can choose. Zero when the artwork carries no nets.</summary>
    public int NetlessTraces { get; init; }

    /// <summary>The widest copper read as a trace on this layer, DBU.</summary>
    public double MaxWidth { get; init; }
}

/// <summary>The whole analysis.</summary>
public sealed record TraceImpedanceReport
{
    /// <summary>Why nothing was analysed, or null.</summary>
    public string? Refusal { get; init; }

    public bool Ok => Refusal is null;

    public string Title { get; init; } = "";
    public string? SourcePath { get; init; }
    public string TechnologyName { get; init; } = "";

    /// <summary>The technology the layout resolved, for the report's stackup drawing.</summary>
    public Technology? Technology { get; init; }

    /// <summary>The <c>.ctech</c> it was read from, or null for one that is not a file.</summary>
    public string? TechnologyPath { get; init; }
    public double TargetOhms { get; init; }
    public double TolerancePercent { get; init; }
    public double LowOhms => TargetOhms * (1 - TolerancePercent / 100);
    public double HighOhms => TargetOhms * (1 + TolerancePercent / 100);

    /// <summary>The warning band: outside the pass band but inside this warns.</summary>
    public double WarningPercent { get; init; }
    public double WarnLowOhms => TargetOhms * (1 - WarningPercent / 100);
    public double WarnHighOhms => TargetOhms * (1 + WarningPercent / 100);

    /// <summary>The frequency the electrically-short rule was judged at, or null when it was off.</summary>
    public double? MaxFrequencyHz { get; init; }

    /// <summary>Trace within this many µm of the land of a via on it was not checked (0: all of it was) —
    /// <see cref="TraceImpedanceOptions.ViaTransitionMicrons"/>.</summary>
    public double ViaTransitionMicrons { get; init; }

    public int DbuPerMicron { get; init; } = LayoutUnits.DefaultDbuPerMicron;
    public LayoutUnit DisplayUnit { get; init; } = LayoutUnit.Um;

    public IReadOnlyList<TraceLayerResult> Layers { get; init; } = [];

    /// <summary>Every layer the run set out to analyse, by name — more than <see cref="Layers"/>
    /// when it was cancelled.</summary>
    public IReadOnlyList<string> LayersRequested { get; init; } = [];

    /// <summary>The run was cancelled; <see cref="Layers"/> holds the layers it finished.</summary>
    public bool Cancelled { get; init; }

    /// <summary>The layout was edited after this run (brief-impedance-3 R-imp3-2e). Set by the editor on
    /// the copy it exports, so a stale PDF says so on its summary page and cannot pass as current.</summary>
    public bool Stale { get; init; }

    /// <summary>What a stale report says about itself — the panel's banner and the PDF's alike.</summary>
    public const string StaleText = "The layout has changed since this run";

    /// <summary>The whole artwork's extent, DBU — every layer page is framed on it so the pages line up.</summary>
    public Bbox Extent { get; init; } = Bbox.Empty;

    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The scope the run reviewed, or null for every trace.</summary>
    public TraceImpedanceScope? Scope { get; init; }

    /// <summary>The scope in words — built here so the PDF, the CLI and the panel say the same sentence:
    /// "Top Copper at 457 µm (1 trace). 21 traces on Top Copper and 19 on Inner 1 are outside the scope
    /// and were not analysed."</summary>
    public string ScopeText { get; init; } = "";

    public int OutOfScopeCount => Layers.Sum(l => l.OutOfScope);

    /// <summary>Whether any copper shape carries a net — the case in which <see cref="NetlessCount"/>
    /// means something.</summary>
    public bool HasNets { get; init; }

    public int NetlessCount => Layers.Sum(l => l.NetlessTraces);

    /// <summary>The picks with no copper under them at this run (R-imp4-2d) — kept in the scope and
    /// listed, never silently dropped.</summary>
    public IReadOnlyList<TracePick> PicksWithoutCopper { get; init; } = [];

    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public int StationCount { get; init; }
    public int SolveCount { get; init; }
    public TimeSpan Elapsed { get; init; }

    public IEnumerable<TraceRun> AllTraces => Layers.SelectMany(l => l.Traces);
    public int TraceCount => Layers.Sum(l => l.Traces.Count);
    public int PassCount => AllTraces.Count(t => t.Verdict == TraceVerdict.Pass);
    public int WarningCount => AllTraces.Count(t => t.Verdict == TraceVerdict.Warning);
    public int FailCount => AllTraces.Count(t => t.Verdict == TraceVerdict.Fail);

    /// <summary>Findings covered by an acceptance (brief-impedance-5).</summary>
    public int AcceptedCount => AllTraces.Sum(t => t.AcceptedCount);

    /// <summary>Acceptances saved on the layout that matched no finding in this run — the trace was moved
    /// or fixed. Listed, never deleted automatically (R-imp5-2c). One whose trace was out of scope is not
    /// here: it was not reviewed, so it is neither applied nor stale.</summary>
    public IReadOnlyList<TraceImpedanceAcceptance> StaleAcceptances { get; init; } = [];

    /// <summary>
    /// The verdict first, then the counts (R-imp5-4a) — "PASS — 3 traces reviewed, 3 pass (1 with an
    /// accepted finding), 0 warnings, 0 failures". A review is FAIL when a trace fails or could not be
    /// solved, WARN when one warns, PASS otherwise.
    /// </summary>
    public string VerdictSentence
    {
        get
        {
            int unsolved = AllTraces.Count(t => t.Verdict == TraceVerdict.Unsolved);
            int passAccepted = AllTraces.Count(t => t.Verdict == TraceVerdict.Pass && t.AcceptedCount > 0);
            string verdict = FailCount > 0 || unsolved > 0 ? "FAIL" : WarningCount > 0 ? "WARN" : "PASS";
            static string N(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
            return $"{verdict} — {N(TraceCount, "trace", "traces")} reviewed, {PassCount} pass" +
                   (passAccepted > 0 ? $" ({passAccepted} with an accepted finding)" : "") +
                   $", {N(WarningCount, "warning", "warnings")}, {N(FailCount, "failure", "failures")}" +
                   (unsolved > 0 ? $", {unsolved} unsolved" : "");
        }
    }

    // ── the strings a trace row prints — the PDF's table and the panel's rows share them (R-imp3-2c) ──

    /// <summary>A Z0 as a table cell: "55.4", or "—" when there is none.</summary>
    public static string OhmsText(double? z) => z is { } v ? $"{v:0.0}" : "—";

    public static string VerdictText(TraceVerdict v) => v switch
    {
        TraceVerdict.Pass    => "PASS",
        TraceVerdict.Warning => "WARN",
        TraceVerdict.Fail    => "FAIL",
        _ => "—",
    };

    public static string SeverityText(TraceIssue i) => i.Fails ? "FAIL" : "WARN";

    /// <summary>The share of the trace's solved length inside the pass band: "84%".</summary>
    public static string InToleranceText(TraceRun t) => $"{t.InTolerance:0%}";

    /// <summary>A trace's width, bare, in the report's unit: one number when it is uniform, else min–max.</summary>
    public string WidthText(TraceRun t) =>
        Math.Abs(t.WidthMax - t.WidthMin) < 0.5 * DbuPerMicron
            ? Num(t.WidthMin, 1)
            : $"{Num(t.WidthMin, 0)}–{Num(t.WidthMax, 0)}";

    /// <summary>A frequency as a reviewer writes it — "6 GHz", "900 MHz".</summary>
    public static string Hz(double hz) => hz switch
    {
        >= 1e9 => $"{hz / 1e9:0.###} GHz",
        >= 1e6 => $"{hz / 1e6:0.###} MHz",
        >= 1e3 => $"{hz / 1e3:0.###} kHz",
        _      => $"{hz:0.###} Hz",
    };

    /// <summary>The decimals a number needs in the report's unit to say what
    /// <paramref name="umDecimals"/> decimals say in µm — 0.1 µm is three more places in mm.</summary>
    public int Decimals(int umDecimals) => Math.Max(0, umDecimals + DisplayUnit switch
    {
        LayoutUnit.Nm   => -3,
        LayoutUnit.Mm   => 3,
        LayoutUnit.Mil  => 1,
        LayoutUnit.Inch => 4,
        _               => 0,
    });

    /// <summary>A length in the report's unit, bare.</summary>
    public string Num(double dbu, int umDecimals = 1) =>
        LayoutUnits.Format((long)Math.Round(dbu), DisplayUnit, DbuPerMicron, Decimals(umDecimals));

    /// <summary>The report's unit suffix — the layout's own.</summary>
    public string Unit => LayoutUnits.Suffix(DisplayUnit);

    /// <summary>A length in the report's unit, with its suffix.</summary>
    public string Len(double dbu) => $"{Num(dbu)} {Unit}";

    /// <summary>A point in the report's unit, with its suffix.</summary>
    public string Pt(long x, long y) => $"({Num(x)}, {Num(y)}) {Unit}";

    internal static TraceImpedanceReport Refused(string why) => new() { Refusal = why };

    /// <summary>The short name a table column holds for a line type.</summary>
    public static string ShortType(string configuration) => configuration switch
    {
        "microstrip"                                    => "Microstrip",
        "grounded coplanar waveguide"                   => "GCPW",
        "microstrip with coplanar ground on one side"   => "GCPW 1-side",
        "stripline"                                     => "Stripline",
        "stripline with coplanar ground"                => "Stripline + CPW",
        "microstrip (reference above)"                  => "Microstrip (ref above)",
        "grounded coplanar waveguide (reference above)" => "GCPW (ref above)",
        "coplanar waveguide (no ground plane)"          => "CPW",
        _                                               => configuration,
    };

    /// <summary>What the short names stand for, for a legend.</summary>
    public static readonly IReadOnlyList<(string Short, string Long)> TypeLegend =
    [
        ("Microstrip", "a reference plane below, nothing beside"),
        ("GCPW", "grounded coplanar waveguide: a plane below and ground on the same layer both sides"),
        ("GCPW 1-side", "a plane below and ground on the same layer on one side only"),
        ("Stripline", "a reference plane below and above"),
        ("CPW", "ground on the same layer only, no plane"),
    ];
}

public static partial class TraceImpedanceAnalysis
{
    /// <summary>Two edges are parallel within this.</summary>
    public const double ParallelToleranceDeg = 2.0;

    /// <summary>An island is a pour when its area is more than this many times its pieces' area.</summary>
    public const double PourAreaRatio = 8;

    /// <summary>An island carrying at least this many vias is a pour, a plane or a ground strip.</summary>
    public const int PourViaCount = 4;

    /// <summary>A chain shorter than this many of its widths is a pad, not a trace — unless a selector
    /// chooses it (<see cref="SelectedMinAspect"/>).</summary>
    public const double MinAspect = 4;

    /// <summary>
    /// A chain a region, a pick or a net CHOOSES is a trace from this many of its widths. A wide line on a
    /// thick board, cut into sections by series parts, is 2-4 widths long between them (field report: a
    /// 2.83 mm grounded coplanar line on 1.5 mm, 6.3-7.7 mm between parts, every section but one read
    /// as a pad), and the reviewer pointing at it is saying it is a line. Left at
    /// <see cref="MinAspect"/> for a run nothing points with, where the same rule on a fine-pitch board
    /// admits every fan-out stub under a package (24 more, nearly all failing, on the reported QFN board).
    /// </summary>
    public const double SelectedMinAspect = 2;

    /// <summary>Copper at a trace's end that widens past this many of the trace's width is a pad: the
    /// end-trim takes it off the trace and <c>EndKind</c> names the end "pad" — one number, so the two
    /// never disagree. A trace's own width drifts by far less; a land is rarely under a fifth wider.</summary>
    internal const double PadWidthStep = 1.2;

    /// <summary>Two pieces whose directions differ by this or more meet at a <see cref="TraceCorner"/>; a
    /// smaller change is a jog or a facet of a curve, and the trace runs straight on through it.</summary>
    public const double CornerMinDeg = 15;

    /// <summary>Cuts per width along a piece.</summary>
    public const double StationsPerWidth = 1;

    /// <summary>At most this many cuts on one piece.</summary>
    public const int MaxStationsPerPiece = 400;

    /// <summary>With <see cref="TraceImpedanceOptions.MaxFrequencyHz"/> set, a stretch outside the
    /// warning band shorter than λ over this is electrically short, and warns.</summary>
    public const double ShortFraction = 20;

    /// <summary>
    /// The analysis of a layout file on disk — read, its technology resolved as the editor resolves
    /// it, its placed cells flattened as a DRC run flattens them. What <c>circuitrf impedance</c> calls.
    /// </summary>
    public static TraceImpedanceReport AnalyzeFile(
        string clayPath, TraceImpedanceOptions options, RunControl? control = null)
    {
        var source = LoadLayout(clayPath);
        return source.Refusal is { } why ? TraceImpedanceReport.Refused(why) : Analyze(source, options, control);
    }

    /// <summary>A layout on disk read for analysis: the view (whose saved
    /// <see cref="LayoutView.ImpedanceReview"/> the caller may apply), its technology resolved as the
    /// editor resolves it, and its placed cells flattened as a DRC run flattens them — read ONCE, so a
    /// caller that needs the saved review and the analysis does not read a large board twice.</summary>
    public sealed record LayoutSource(
        string Path, LayoutView View, Technology? Technology, string? TechnologyPath,
        IReadOnlyList<LayoutShape> Shapes, string? Refusal);

    public static LayoutSource LoadLayout(string clayPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clayPath);
        string full = Path.GetFullPath(clayPath);
        var view = LayoutPersistence.LoadFromFile(full);
        var cache = new TechnologyCache();
        var (resolved, _) = TechnologyResolver.ResolveForDocument(view.TechRef, full, null, cache);
        if (resolved.Tech is not { } tech)
            return new LayoutSource(full, view, null, null, [],
                $"'{Path.GetFileName(full)}' resolves no technology, so it has no stackup: an impedance needs " +
                "the heights and the dielectric. Give the layout a technology, or open it in a workspace that has one.");

        string cellDir = Path.GetDirectoryName(Path.GetDirectoryName(full) ?? "") ?? "";
        var flat = LayoutDesignFlatten.Flatten(
            view, cellDir, tech,
            (techRef, subLayoutDir) => TechnologyResolver.ResolveForDocument(
                techRef, Path.Combine(subLayoutDir, "x.clay"), null, cache).Resolution,
            resolvedCrossTechMappings: null);
        IReadOnlyList<LayoutShape> shapes = flat.ExceedsCeiling ? view.Shapes : flat.Shapes;
        return new LayoutSource(full, view, tech, resolved.ResolvedPath, shapes, null);
    }

    /// <summary>The analysis of a layout read by <see cref="LoadLayout"/>.</summary>
    public static TraceImpedanceReport Analyze(LayoutSource source, TraceImpedanceOptions options, RunControl? control = null)
    {
        if (source.Technology is null)
            return TraceImpedanceReport.Refused(source.Refusal ?? "The layout resolves no technology.");
        var report = Analyze(source.Shapes, source.Technology, source.View.DbuPerMicron,
                             options with { DisplayUnit = options.DisplayUnit ?? source.View.DisplayUnit }, control);
        return report with
        {
            Title = CellTitle(source.Path),
            SourcePath = source.Path,
            TechnologyPath = source.TechnologyPath,
        };
    }

    /// <summary>The survey of a layout read by <see cref="LoadLayout"/>.</summary>
    public static TraceWidthSurvey Survey(LayoutSource source, TraceImpedanceOptions options, RunControl? control = null)
    {
        if (source.Technology is null)
            return TraceWidthSurvey.Refused(source.Refusal ?? "The layout resolves no technology.");
        return Survey(source.Shapes, source.Technology, source.View.DbuPerMicron, options, control);
    }

    /// <summary>The cell's name for a layout inside a cell folder, the file's name otherwise.</summary>
    public static string CellTitle(string clayPath)
    {
        string? layoutDir = Path.GetDirectoryName(Path.GetFullPath(clayPath));
        string? cellDir = layoutDir is null ? null : Path.GetDirectoryName(layoutDir);
        if (layoutDir is not null && Path.GetFileName(layoutDir).Equals("layout", StringComparison.OrdinalIgnoreCase)
            && cellDir is not null)
            return Path.GetFileName(cellDir);
        return Path.GetFileNameWithoutExtension(clayPath);
    }

    /// <summary>
    /// Every trace on the chosen layers of <paramref name="shapes"/>, analysed. <b>The shapes are the
    /// FLATTENED artwork</b>, as the probe takes them.
    /// </summary>
    public static TraceImpedanceReport Analyze(
        IReadOnlyList<LayoutShape> shapes, Technology tech, int dbuPerMicron,
        TraceImpedanceOptions options, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(tech);
        ArgumentNullException.ThrowIfNull(options);
        if (dbuPerMicron <= 0) dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        if (!(options.TargetOhms > 0))
            return TraceImpedanceReport.Refused("The target impedance must be a positive number of ohms.");
        if (!(options.TolerancePercent > 0) || options.TolerancePercent >= 100)
            return TraceImpedanceReport.Refused("The tolerance must be more than 0 % and less than 100 %.");
        if (!(options.WarningPercent > options.TolerancePercent) || options.WarningPercent >= 100)
            return TraceImpedanceReport.Refused(
                $"The warning band (± {options.WarningPercent:0.##} %) must be wider than the tolerance " +
                $"(± {options.TolerancePercent:0.##} %) and less than 100 %.");
        if (options.MaxFrequencyHz is { } fMax && !(fMax > 0 && double.IsFinite(fMax)))
            return TraceImpedanceReport.Refused("The highest frequency must be a positive number of hertz, or left out.");
        if (!(options.ViaTransitionMicrons >= 0) || !double.IsFinite(options.ViaTransitionMicrons))
            return TraceImpedanceReport.Refused("The distance round a via that is not checked must be zero or a positive length.");

        var clock = Stopwatch.StartNew();
        var ct = control?.Token ?? CancellationToken.None;

        var (prep, refusal) = Prepare(shapes, tech, dbuPerMicron, options, control, ct);
        if (prep is null) return TraceImpedanceReport.Refused(refusal!);
        var ctx = prep.Ctx;
        var analysed = prep.Analysed;
        var notes = prep.Notes;
        var scope = options.Scope is { IsEmpty: false } s ? s : null;
        var selection = new Selection(scope, prep, shapes, tech);

        // ── layer by layer ──────────────────────────────────────────────────────────────────────
        // Each layer is found, cut, solved and assembled before the next is started, so a run that
        // is cancelled still has every layer it FINISHED — and the report is written for those
        // (owner, 2026-09-25: a long run cancelled part-way must not throw away what it has done).
        // Progress: Completed/Total counts layers; the stage counts the layer's solves.
        int id = 0, jid = 0, stationCount = 0, solveCount = 0;
        var layers = new List<TraceLayerResult>();
        bool cancelled = false;
        for (int li = 0; li < analysed.Count; li++)
        {
            var (key, band) = analysed[li];
            string name = prep.LayerName(key);
            string tag = $"{name} ({li + 1} of {analysed.Count})";
            try
            {
                ct.ThrowIfCancellationRequested();
                control?.BeginStage($"{tag}: finding traces");
                var lw = prep.FindLayer(key, name, band, options, ct);

                // The scope, BEFORE cutting, so an out-of-scope trace is never cut or solved — that is
                // the solve time saved, not just the rows. It selects TRACES, never COPPER: the chains
                // dropped here leave the layer's copper (lw.Copper) and every other layer's (ctx.Copper)
                // untouched, so the out-of-scope trace running beside a trace under review is still a
                // grounded neighbour in that trace's cross-section, exactly as without a scope.
                // The selectors (regions, picks, nets) choose; the widths then filter what they chose.
                // A SHORT chain (SelectedMinAspect to MinAspect widths) is a trace only where a selector
                // chooses it; one no selector chooses is a pad, as it is in a run with no scope, and is
                // neither reviewed nor counted as a trace left out.
                int outOfScope = 0;
                var outOfScopeKeys = new List<string>();
                var chosen = scope is null ? null : selection.Select(lw);
                var eligible = new List<ChainWork>();
                var selected = new List<bool>();
                for (int i = 0; i < lw.Chains.Count; i++)
                {
                    if (lw.Chains[i].Short && chosen?[i] != true) continue;
                    eligible.Add(lw.Chains[i]);
                    selected.Add(chosen?[i] ?? true);
                }
                lw = lw with { Chains = eligible };
                int netless = selection.HasNets ? lw.Chains.Count(c => selection.NetOf(lw, c) is null) : 0;
                if (scope is not null)
                {
                    var kept = lw.Chains.Where((c, i) => selected[i]
                                                         && scope.Includes(name, DominantWidth(c, dbuPerMicron) / dbuPerMicron)).ToList();
                    outOfScope = lw.Chains.Count - kept.Count;
                    foreach (var c in lw.Chains.Except(kept))
                    {
                        var (sx, sy, ex, ey) = ChainEnds(c);
                        outOfScopeKeys.Add(TraceImpedanceAcceptance.TraceKeyOf(name, sx, sy, ex, ey, dbuPerMicron));
                    }
                    lw = lw with { Chains = kept };
                }

                control?.BeginStage($"{tag}: cutting");
                var solves = new Dictionary<string, TraceCut>();
                stationCount += Cut(lw, ctx, solves, dbuPerMicron, ct);

                var keys = solves.Keys.ToArray();
                control?.BeginStage($"{tag}: solving", keys.Length, "cross-sections");
                var answers = new System.Collections.Concurrent.ConcurrentDictionary<string, (double C, double C0, string? Refusal)>();
                Parallel.ForEach(keys,
                    new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
                    k =>
                    {
                        answers[k] = TraceCrossSection.Solve(ctx, solves[k], ct);
                        control?.TickStage();
                    });
                ct.ThrowIfCancellationRequested();
                solveCount += keys.Length;

                var runs = new List<TraceRun>();
                var junctionIds = new Dictionary<int, string>();
                var arms = new Dictionary<int, List<TraceJunctionArm>>();
                foreach (var chain in lw.Chains)
                {
                    var run = Assemble(chain, lw, answers, options, dbuPerMicron, ref id);
                    string? JunctionOf(int key, bool atStart)
                    {
                        if (key < 0) return null;
                        if (!junctionIds.TryGetValue(key, out var jName)) { junctionIds[key] = jName = $"J{++jid}"; arms[key] = []; }
                        arms[key].Add(new TraceJunctionArm(run.Id, atStart));
                        return jName;
                    }
                    runs.Add(run with { StartJunction = JunctionOf(chain.StartKey, true), EndJunction = JunctionOf(chain.EndKey, false) });
                }
                var junctions = junctionIds.Select(kv =>
                {
                    var (jx, jy) = lw.JunctionCentres[kv.Key];
                    return new TraceJunction(kv.Value, lw.Key, (long)Math.Round(jx), (long)Math.Round(jy), arms[kv.Key]);
                }).ToList();
                layers.Add(new TraceLayerResult
                {
                    Layer = lw.Key,
                    Name = lw.Name,
                    Traces = runs,
                    Junctions = junctions,
                    PoursSkipped = lw.Pours,
                    OutOfScope = outOfScope,
                    OutOfScopeTraceKeys = outOfScopeKeys,
                    NetlessTraces = netless,
                    MaxWidth = lw.MaxWidth,
                    Copper = lw.Copper is null ? [] : [.. lw.Copper.Paths.Select(p =>
                    {
                        var xy = new long[p.Count * 2];
                        for (int i = 0; i < p.Count; i++) { xy[2 * i] = p[i].X; xy[2 * i + 1] = p[i].Y; }
                        return xy;
                    })],
                });
                control?.Tick();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                cancelled = true;
                var rest = analysed.Skip(li).Select(a => $"'{prep.LayerName(a.Key)}'");
                notes.Add($"Cancelled after {li} of {analysed.Count} layer{(analysed.Count == 1 ? "" : "s")}; " +
                          $"not analysed: {string.Join(", ", rest)}.");
                break;
            }
        }

        // A pick is kept whatever it resolves to, and said (R-imp4-2d): the artwork changing under a
        // saved pick is exactly what the reviewer must see.
        var noCopper = new List<TracePick>();
        foreach (var (pick, state) in selection.Picks)
        {
            string at = $"{pick.LayerName} at {new TraceImpedanceReport { DbuPerMicron = dbuPerMicron, DisplayUnit = options.DisplayUnit ?? LayoutUnit.Um }.Pt(pick.X, pick.Y)}";
            if (!state.OnCopper)
            {
                noCopper.Add(pick);
                notes.Add($"The pick on {at} has no copper under it now; it selected nothing.");
            }
            else if (state.Traces == 0 && !cancelled)
                notes.Add($"The pick on {at} is on copper that carries no trace on the layers analysed (a pour or a pad, " +
                          "or a layer that was not analysed); it selected nothing.");
        }

        if (!cancelled && layers.All(l => l.Traces.Count == 0 && l.OutOfScope == 0))
            notes.Add("No traces were found on the layers analysed: no copper there has two long parallel " +
                      "edges facing each other outside a pour.");

        var report = new TraceImpedanceReport
        {
            TechnologyName = tech.Name,
            Technology = tech,
            TargetOhms = options.TargetOhms,
            TolerancePercent = options.TolerancePercent,
            WarningPercent = options.WarningPercent,
            MaxFrequencyHz = options.MaxFrequencyHz,
            ViaTransitionMicrons = options.ViaTransitionMicrons,
            DbuPerMicron = dbuPerMicron,
            DisplayUnit = options.DisplayUnit ?? LayoutUnit.Um,
            Layers = layers,
            LayersRequested = [.. analysed.Select(a => prep.LayerName(a.Key))],
            Cancelled = cancelled,
            Extent = prep.Extent,
            Notes = notes,
            Scope = scope,
            HasNets = selection.HasNets,
            PicksWithoutCopper = noCopper,
            StationCount = stationCount,
            SolveCount = solveCount,
            Elapsed = clock.Elapsed,
        };
        return report with { ScopeText = DescribeScope(report) };
    }

    /// <summary>
    /// The traces on the chosen layers grouped into width classes, with nothing solved but ONE typical
    /// cut per class (brief-impedance-2 R-imp2-1) — what a reviewer chooses a scope from. The copper is
    /// read and the traces found by the same code <see cref="Analyze(IReadOnlyList{LayoutShape},
    /// Technology, int, TraceImpedanceOptions, RunControl?)"/> runs; it then stops short of cutting.
    /// The scope in <paramref name="options"/> is ignored: a survey shows every width, so a class the
    /// scope leaves out can be ticked. Cancelling throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static TraceWidthSurvey Survey(
        IReadOnlyList<LayoutShape> shapes, Technology tech, int dbuPerMicron,
        TraceImpedanceOptions options, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(tech);
        ArgumentNullException.ThrowIfNull(options);
        if (dbuPerMicron <= 0) dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        var clock = Stopwatch.StartNew();
        var ct = control?.Token ?? CancellationToken.None;

        var (prep, refusal) = Prepare(shapes, tech, dbuPerMicron, options, control, ct);
        if (prep is null) return TraceWidthSurvey.Refused(refusal!);

        var layers = new List<TraceWidthLayer>();
        int solveCount = 0;
        var selection = new Selection(null, prep, shapes, tech);
        for (int li = 0; li < prep.Analysed.Count; li++)
        {
            var (key, band) = prep.Analysed[li];
            string name = prep.LayerName(key);
            string tag = $"{name} ({li + 1} of {prep.Analysed.Count})";
            ct.ThrowIfCancellationRequested();
            control?.BeginStage($"{tag}: finding traces");
            var lw = prep.FindLayer(key, name, band, options, ct);
            // A survey chooses nothing, so a short chain is a pad here as in a run with no scope.
            lw = lw with { Chains = [.. lw.Chains.Where(c => !c.Short)] };

            var widths = lw.Chains.Select(c => DominantWidth(c, dbuPerMicron)).ToArray();
            var lengths = lw.Chains.Select(ChainLength).ToArray();
            var clusters = Cluster(widths, lengths, dbuPerMicron);

            control?.BeginStage($"{tag}: typical Z0", clusters.Count, "cross-sections");
            var classes = new List<TraceWidthClass>();
            foreach (var members in clusters)
            {
                ct.ThrowIfCancellationRequested();
                double total = members.Sum(i => lengths[i]);
                var (z0, why) = TypicalZ0(lw with { Chains = [lw.Chains[members.MaxBy(i => lengths[i])]] },
                                          prep.Ctx, dbuPerMicron, ct, ref solveCount);
                classes.Add(new TraceWidthClass
                {
                    NominalMicrons = members.Sum(i => widths[i] * lengths[i]) / total / dbuPerMicron,
                    MinMicrons = members.Min(i => widths[i]) / dbuPerMicron,
                    MaxMicrons = members.Max(i => widths[i]) / dbuPerMicron,
                    TraceCount = members.Count,
                    TotalLengthMicrons = total / dbuPerMicron,
                    TypicalZ0 = z0,
                    TypicalRefusal = why,
                });
                control?.TickStage();
            }
            layers.Add(new TraceWidthLayer
            {
                Layer = key, Name = name, Classes = classes, PoursSkipped = lw.Pours,
                NetlessTraces = selection.HasNets ? lw.Chains.Count(c => selection.NetOf(lw, c) is null) : 0,
            });
            control?.Tick();
        }
        return new TraceWidthSurvey
        {
            Layers = layers, Notes = prep.Notes, Nets = selection.NetNames, SolveCount = solveCount, Elapsed = clock.Elapsed,
        };
    }

    /// <summary>One cut at the middle of the one chain in <paramref name="lw"/> — the nearest station to
    /// the middle whose cut is solvable — solved. Cutting the chain is <see cref="Cut"/>'s own work.</summary>
    private static (double? Z0, string? Refusal) TypicalZ0(LayerWork lw, TraceStack ctx, int dbuPerMicron,
                                                          CancellationToken ct, ref int solveCount)
    {
        var chain = lw.Chains[0];
        Cut(lw, ctx, new Dictionary<string, TraceCut>(), dbuPerMicron, ct);
        double mid = 0.5 * chain.Length;
        var station = chain.Stations.Where(s => s.Cut is { Refusal: null })
                                    .OrderBy(s => Math.Abs(s.S - mid)).FirstOrDefault();
        if (station is null)
            return (null, chain.Stations.Select(s => s.Cut?.Refusal).FirstOrDefault(r => r is not null)
                          ?? "the cut left the copper");
        var (c, c0, refusal) = TraceCrossSection.Solve(ctx, station.Cut!, ct);
        solveCount++;
        return refusal is null ? (TraceCrossSection.Z0(c, c0), null) : (null, refusal);
    }

    /// <summary>Two widths merge into one class within this: max(1 %, 1 µm).</summary>
    public static double MergeToleranceMicrons(double widthMicrons) => Math.Max(0.01 * widthMicrons, 1.0);

    /// <summary>
    /// Widths grouped into classes: sorted, and a width joins the class of the narrowest width within
    /// <see cref="MergeToleranceMicrons"/> of it. Each class is its members' indices. DBU in.
    /// </summary>
    private static List<List<int>> Cluster(double[] widths, double[] weights, int dbuPerMicron)
    {
        var order = Enumerable.Range(0, widths.Length).OrderBy(i => widths[i]).ToArray();
        var classes = new List<List<int>>();
        int a = 0;
        while (a < order.Length)
        {
            double w0 = widths[order[a]];
            double tol = MergeToleranceMicrons(w0 / dbuPerMicron) * dbuPerMicron;
            var members = new List<int>();
            while (a < order.Length && widths[order[a]] - w0 <= tol) members.Add(order[a++]);
            classes.Add(members);
        }
        return classes;
    }

    /// <summary>
    /// A chain's DOMINANT width, DBU: the width over the largest share of its length, its pieces'
    /// widths grouped as a survey groups traces — a 99–650 µm taper is classed by the width most of it
    /// has, not by its narrowest or widest end.
    /// </summary>
    private static double DominantWidth(ChainWork chain, int dbuPerMicron)
    {
        var widths = chain.Pieces.Select(p => p.Piece.Width).ToArray();
        var lengths = chain.Pieces.Select(p => p.Piece.Length).ToArray();
        var best = Cluster(widths, lengths, dbuPerMicron).MaxBy(m => m.Sum(i => lengths[i]))!;
        return best.Sum(i => widths[i] * lengths[i]) / best.Sum(i => lengths[i]);
    }

    /// <summary>A chain's length along its centre line, DBU — its pieces and the joins between them,
    /// as <see cref="Cut"/> measures it.</summary>
    private static double ChainLength(ChainWork chain)
    {
        double s = 0;
        for (int i = 0; i < chain.Pieces.Count; i++)
        {
            var (p, reversed) = chain.Pieces[i];
            s += p.Length;
            if (i == 0) continue;
            var (q, qReversed) = chain.Pieces[i - 1];
            var (qx, qy) = qReversed ? (q.Ax, q.Ay) : (q.Bx, q.By);
            var (ax, ay) = reversed ? (p.Bx, p.By) : (p.Ax, p.Ay);
            s += Math.Sqrt(Sq(ax - qx) + Sq(ay - qy));
        }
        return s;
    }

    /// <summary>
    /// The scope in words: per analysed layer what was reviewed and how many traces that is, then the
    /// traces left out, counted — never listed (the series rule: what was not reviewed is said, not
    /// hidden).
    /// </summary>
    private static string DescribeScope(TraceImpedanceReport r) =>
        ScopeSentence([.. r.Layers.Select(l => (l.Name, l.Traces.Count, l.OutOfScope))], r.Scope, r, beforeRun: false);

    /// <summary>
    /// The scope in the report's own words BEFORE a run, from a survey (brief-impedance-3 R-imp3-1b: the
    /// panel's Scope line, live as the scope is edited). A width class counts as in scope by its nominal
    /// width; a run decides trace by trace, so the counts are the survey's and the run's may differ
    /// where a class straddles a selector's edge.
    /// </summary>
    public static string DescribeScope(
        TraceWidthSurvey survey, IReadOnlyCollection<string>? layerNames, TraceImpedanceScope? scope,
        int dbuPerMicron, LayoutUnit displayUnit)
    {
        ArgumentNullException.ThrowIfNull(survey);
        if (scope is { IsEmpty: true }) scope = null;
        var layers = new List<(string, int, int)>();
        foreach (var l in survey.Layers)
        {
            if (layerNames is not null && !layerNames.Contains(l.Name, StringComparer.OrdinalIgnoreCase)) continue;
            int all = l.Classes.Sum(c => c.TraceCount);
            int inScope = l.Classes.Where(c => scope?.Includes(l.Name, c.NominalMicrons) ?? true).Sum(c => c.TraceCount);
            layers.Add((l.Name, inScope, all - inScope));
        }
        return ScopeSentence(layers, scope, new TraceImpedanceReport { DbuPerMicron = dbuPerMicron, DisplayUnit = displayUnit },
                             beforeRun: true);
    }

    /// <summary>One sentence for both: what selected the traces (regions, picks, nets), the layers with
    /// the widths reviewed on each and how many traces that is, then how many were left out.
    /// <paramref name="units"/> formats the widths. Before a run, a scope with selectors cannot count
    /// what they select — that is geometry the run resolves — so it says so rather than guess.</summary>
    private static string ScopeSentence(
        IReadOnlyList<(string Name, int InScope, int OutOfScope)> layers, TraceImpedanceScope? scope, TraceImpedanceReport units,
        bool beforeRun)
    {
        static string Traces(int n) => n == 1 ? "1 trace" : $"{n} traces";
        if (layers.Count == 0) return "";
        if (scope is null)
            return $"Every trace on {JoinAnd([.. layers.Select(l => l.Name)])}.";

        bool counted = !(beforeRun && scope.HasSelectors);
        var clauses = new List<string>();
        foreach (var l in layers)
        {
            // brief-impedance-6: while selectors are set, a layer none of them reaches is not reviewed —
            // said by name, because "0 traces" reads as a layer with no copper.
            if (!scope.Reaches(l.Name))
            {
                clauses.Add($"{l.Name}, nothing selected");
                continue;
            }
            var widths = scope.WidthsOn(l.Name).Select(w => w.NominalMicrons).Distinct().OrderBy(w => w).ToList();
            string count = counted ? $" ({Traces(l.InScope)})" : "";
            clauses.Add(widths.Count == 0
                ? $"{l.Name}, every width{count}"
                : $"{l.Name} at {JoinAnd([.. widths.Select(w => units.Num(w * units.DbuPerMicron))])} {units.Unit}{count}");
        }
        string text = (scope.HasSelectors ? $"Selected by {SelectorText(scope)}. " : "") + string.Join("; ", clauses) + ".";

        if (!counted)
            return text + " The traces the selectors choose are counted when the analysis runs.";
        var outside = layers.Where(l => l.OutOfScope > 0).ToList();
        if (outside.Count > 0)
        {
            var parts = outside.Select((l, i) => i == 0 ? $"{Traces(l.OutOfScope)} on {l.Name}" : $"{l.OutOfScope} on {l.Name}").ToList();
            bool one = outside.Count == 1 && outside[0].OutOfScope == 1;
            text += $" {JoinAnd(parts)} {(one ? "is" : "are")} outside the scope and " +
                    (beforeRun ? "will not be analysed." : $"{(one ? "was" : "were")} not analysed.");
        }
        return text;
    }

    /// <summary>The selectors in words (R-imp4-4a): "2 regions ('RF front end', 'antenna'); 1 pick (Top
    /// Copper, connected); net RF_OUT".</summary>
    public static string SelectorText(TraceImpedanceScope scope)
    {
        var parts = new List<string>();
        if (scope.Regions.Count > 0)
        {
            var names = scope.Regions.Where(r => !string.IsNullOrWhiteSpace(r.Name)).Select(r => $"'{r.Name}'").ToList();
            var onLayers = scope.Regions.Select(r => r.LayerName ?? "every layer").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            parts.Add((scope.Regions.Count == 1 ? "1 region" : $"{scope.Regions.Count} regions") +
                      (names.Count > 0 ? $" ({string.Join(", ", names)})" : "") + " on " + JoinAnd(onLayers));
        }
        if (scope.Picks.Count > 0)
            parts.Add((scope.Picks.Count == 1 ? "1 pick" : $"{scope.Picks.Count} picks") + " (" +
                      string.Join("; ", scope.Picks.Select(p => p.Extent == TracePickExtent.Connected ? $"{p.LayerName}, connected" : p.LayerName)) + ")");
        if (scope.Nets.Count > 0)
            parts.Add((scope.Nets.Count == 1 ? "net " : "nets ") + JoinAnd(scope.Nets));
        if (parts.Count > 0 && scope.WholeLayers.Count > 0)
            parts.Add("all of " + JoinAnd(scope.WholeLayers));
        return string.Join("; ", parts);
    }

    private static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    /// <summary>What <see cref="Analyze(IReadOnlyList{LayoutShape}, Technology, int, TraceImpedanceOptions,
    /// RunControl?)"/> and <see cref="Survey(IReadOnlyList{LayoutShape}, Technology, int,
    /// TraceImpedanceOptions, RunControl?)"/> both start from: the whole artwork's copper, per band, and
    /// the layers to look at.</summary>
    private sealed class Prepared
    {
        public required TraceStack Ctx { get; init; }
        public required List<(LayerKey Key, CrossSectionExtractor.Band Band)> Analysed { get; init; }
        public required List<string> Notes { get; init; }
        public required Bbox Extent { get; init; }
        public required Func<LayerKey, string> LayerName { get; init; }
        public required Dictionary<int, (int[] IslandOfRing, double[] IslandArea, int[] HoleCount)> IslandsOf { get; init; }
        public required Dictionary<int, List<(double X, double Y, double R)>> Vias { get; init; }
        /// <summary>Per band, every via whose BARREL passes it but whose pad is on another layer — see
        /// <see cref="LayerWork.TransitionVias"/>.</summary>
        public required Dictionary<int, List<(double X, double Y, double R)>> Barrels { get; init; }
        public required int DbuPerMicron { get; init; }

        public LayerWork FindLayer(LayerKey key, string name, CrossSectionExtractor.Band band,
                                   TraceImpedanceOptions options, CancellationToken ct) =>
            TraceImpedanceAnalysis.FindLayer(key, name, band, Ctx.Copper, IslandsOf, Vias, Barrels, options, Ctx.Bands, DbuPerMicron, ct);
    }

    private static (Prepared? Prep, string? Refusal) Prepare(
        IReadOnlyList<LayoutShape> shapes, Technology tech, int dbuPerMicron,
        TraceImpedanceOptions options, RunControl? control, CancellationToken ct)
    {
        var stackNotes = new List<string>();
        var (stack, bands, bandOf, stackRefusal) = TraceStack.StackOf(tech, shapes, stackNotes);
        if (stackRefusal is not null) return (null, stackRefusal);
        if (bands.Count == 0)
            return (null,
                $"The technology '{tech.Name}' has no conductor in its stackup, so there is no copper to analyse. " +
                "Add the stackup on the technology's Stackup tab.");

        string LayerName(LayerKey k) => tech.Layers.FirstOrDefault(l => l.Key == k)?.Name is { Length: > 0 } n
            ? n : $"layer {k.Layer}/{k.Datatype}";

        // ── the copper, per band, for the whole artwork ─────────────────────────────────────────
        control?.BeginStage("Reading copper");
        var subjects = new Dictionary<int, Paths64>();
        var vias = new Dictionary<int, List<(double X, double Y, double R)>>();
        var barrels = new Dictionary<int, List<(double X, double Y, double R)>>();
        var extent = Bbox.Empty;
        foreach (var shape in shapes)
        {
            if (shape is not ViaShape && !bandOf.ContainsKey(shape.Layer)) continue;
            DrcRegions.Expand(shape, tech, _ => long.MaxValue, (lk, _, paths) =>
            {
                if (!bandOf.TryGetValue(lk, out var band)) return;
                if (!subjects.TryGetValue(band.Index, out var list)) subjects[band.Index] = list = [];
                list.AddRange(paths);
                foreach (var p in paths)
                    foreach (var pt in p) extent = extent.Union(new Bbox(pt.X, pt.Y, pt.X, pt.Y));
                if (shape is ViaShape v)
                {
                    if (!vias.TryGetValue(band.Index, out var vl)) vias[band.Index] = vl = [];
                    double r = 0;
                    foreach (var p in paths)
                        foreach (var pt in p) r = Math.Max(r, Math.Sqrt((double)(pt.X - v.X) * (pt.X - v.X) + (double)(pt.Y - v.Y) * (pt.Y - v.Y)));
                    vl.Add((v.X, v.Y, r));
                }
            });
            if (shape is ViaShape via)
                foreach (var b in BandsSpanned(via, tech, bands))
                {
                    if (via.LandingLayer is { } land && bandOf.TryGetValue(land, out var lb) && lb.Index == b.Index) continue;
                    if (!barrels.TryGetValue(b.Index, out var bl)) barrels[b.Index] = bl = [];
                    bl.Add((via.X, via.Y, 0.5 * Math.Max(via.PadSize, via.DrillSize)));
                }
        }
        ct.ThrowIfCancellationRequested();

        // Which layers: those asked for, or every bound drawing layer with copper — one per band,
        // the band's first drawing layer, because a band is ONE copper layer however many drawing
        // layers are bound to it.
        var requested = options.Layers ?? [.. bands.Where(b => subjects.ContainsKey(b.Index))
                                                     .Select(b => b.Layer.DrawingLayers.First(dl => bandOf[dl] == b))];
        var analysed = new List<(LayerKey Key, CrossSectionExtractor.Band Band)>();
        var notes = new List<string>(stackNotes);
        foreach (var key in requested)
        {
            if (!bandOf.TryGetValue(key, out var band))
            {
                notes.Add($"'{LayerName(key)}' is not bound to a conductor of the stackup, so it has no height " +
                          "and was not analysed.");
                continue;
            }
            if (analysed.Any(a => a.Band.Index == band.Index)) continue;
            analysed.Add((key, band));
        }
        // Top of the stack first, as the stackup lists them and as a reviewer reads a board.
        analysed.Sort((a, b) => a.Band.Index.CompareTo(b.Band.Index));
        if (analysed.Count == 0)
            return (null, "None of the layers asked for is a copper layer bound to the stackup, so there is nothing to analyse.");

        var copper = new Dictionary<int, TraceCopper>();
        var islandsOf = new Dictionary<int, (int[] IslandOfRing, double[] IslandArea, int[] HoleCount)>();
        foreach (var (index, list) in subjects)
        {
            ct.ThrowIfCancellationRequested();
            bool analysedBand = analysed.Any(a => a.Band.Index == index);
            if (!analysedBand)
            {
                copper[index] = new TraceCopper(Clipper.Union(list, LayoutClipper.Rule), indexed: true);
                continue;
            }
            // The partition's own hairline join (LayerRegions.JoinHairlineGaps): a trace and the trace it
            // continues, rounded 0.75 µm apart by a file's grid, are one run here as they are one net there.
            var joined = CircuitRF.Design.Layout.Extraction.LayerRegions.JoinHairlineGaps(
                Clipper.Union(list, LayoutClipper.Rule), dbuPerMicron).Region;
            var tree = new PolyTree64();
            Clipper.BooleanOp(ClipType.Union, joined, new Paths64(), tree, LayoutClipper.Rule);
            var rings = new Paths64();
            var ringIsland = new List<int>();
            var areas = new List<double>();
            var holes = new List<int>();
            void Walk(PolyPath64 node)
            {
                for (int i = 0; i < node.Count; i++)
                {
                    var outer = node[i];
                    int island = areas.Count;
                    double area = Math.Abs(Clipper.Area(outer.Polygon!));
                    rings.Add(outer.Polygon!); ringIsland.Add(island);
                    int h = 0;
                    for (int j = 0; j < outer.Count; j++)
                    {
                        var hole = outer[j];
                        rings.Add(hole.Polygon!); ringIsland.Add(island);
                        area -= Math.Abs(Clipper.Area(hole.Polygon!));
                        h++;
                    }
                    areas.Add(area);
                    holes.Add(h);
                    for (int j = 0; j < outer.Count; j++) Walk(outer[j]);
                }
            }
            Walk(tree);
            copper[index] = new TraceCopper(rings, indexed: true);
            islandsOf[index] = ([.. ringIsland], [.. areas], [.. holes]);
        }

        var ctx = new TraceStack
        {
            Stack = stack, Bands = bands, BandOf = bandOf, Copper = copper, Tech = tech, DbuPerMicron = dbuPerMicron,
        };
        return (new Prepared
        {
            Ctx = ctx, Analysed = analysed, Notes = notes, Extent = extent, LayerName = LayerName,
            IslandsOf = islandsOf, Vias = vias, Barrels = barrels, DbuPerMicron = dbuPerMicron,
        }, null);
    }

    /// <summary>Pieces, pours and chains on one layer.</summary>
    private static LayerWork FindLayer(
        LayerKey key, string name, CrossSectionExtractor.Band band,
        Dictionary<int, TraceCopper> copper,
        Dictionary<int, (int[] IslandOfRing, double[] IslandArea, int[] HoleCount)> islandsOf,
        Dictionary<int, List<(double X, double Y, double R)>> vias,
        Dictionary<int, List<(double X, double Y, double R)>> barrels,
        TraceImpedanceOptions options, List<CrossSectionExtractor.Band> bands, int dbuPerMicron,
        CancellationToken ct)
    {
        if (!copper.TryGetValue(band.Index, out var cu))
            return new LayerWork(key, name, band, null, [], 0, 0);

        var (minW, maxW) = WidthRange(options, bands, band, dbuPerMicron);
        var pieces = FindPieces(cu, minW, maxW, dbuPerMicron, ct);
        var (islandOfRing, islandArea, holeCount) = islandsOf[band.Index];
        var bandVias = vias.GetValueOrDefault(band.Index) ?? [];

        // Pours: pieces' area against the island's, and the vias landing on it.
        var pieceArea = new double[islandArea.Length];
        foreach (var p in pieces) pieceArea[islandOfRing[p.Ring]] += p.Length * p.Width;
        var viaCount = new int[islandArea.Length];
        foreach (var (vx, vy, _) in bandVias)
        {
            int e = cu.NearestEdge(vx, vy, maxW);
            if (e >= 0 && cu.Contains(vx, vy)) viaCount[islandOfRing[cu.RingOf[e]]]++;
        }
        var pour = new bool[islandArea.Length];
        int pours = 0;
        for (int i = 0; i < pour.Length; i++)
        {
            if (pieceArea[i] <= 0) continue;
            pour[i] = islandArea[i] > PourAreaRatio * pieceArea[i] || viaCount[i] >= PourViaCount
                      || (holeCount[i] >= 3 && islandArea[i] > 3 * pieceArea[i]);
            if (pour[i]) pours++;
        }
        pieces = [.. pieces.Where(p => !pour[islandOfRing[p.Ring]])];

        // A barrel counts on this layer only where this layer's copper meets it — a trace or pad joined to
        // the via here — so a via passing through a plane's antipad beside a trace is not on that trace.
        var transitionVias = new List<(double X, double Y, double R)>(bandVias);
        foreach (var b in barrels.GetValueOrDefault(band.Index) ?? [])
            if (cu.Contains(b.X, b.Y)) transitionVias.Add(b);

        var chains = Chain(pieces, cu, islandOfRing, out var junctionCentres);
        return new LayerWork(key, name, band, cu, chains, pours, maxW)
        {
            Vias = bandVias, TransitionVias = transitionVias, IslandOfRing = islandOfRing, JunctionCentres = junctionCentres,
        };
    }

    /// <summary>
    /// The conductor bands a via's barrel passes: from its stackup via entry's SpanFromLayer to its
    /// SpanToLayer, and every band when the technology states no span for it — a drill with no span is a
    /// through hole, which is what a Gerber drill file is. A via's own copper is only its landing pad
    /// (<see cref="DrcRegions.Expand"/>), so without this a trace on an inner layer running onto a
    /// through via never knew the via was there (round-10 report: the imported board's 1,291 vias all
    /// land on the top layer).
    /// </summary>
    private static IEnumerable<CrossSectionExtractor.Band> BandsSpanned(ViaShape via, Technology tech,
                                                                       List<CrossSectionExtractor.Band> bands)
    {
        var entry = tech.Stackup.Layers.FirstOrDefault(l => l.Kind == StackupKind.Via && l.DrawingLayers.Contains(via.Layer));
        var from = bands.FirstOrDefault(b => entry?.SpanFromLayer is { } n && string.Equals(b.Layer.Name, n, StringComparison.OrdinalIgnoreCase));
        var to = bands.FirstOrDefault(b => entry?.SpanToLayer is { } n && string.Equals(b.Layer.Name, n, StringComparison.OrdinalIgnoreCase));
        if (from is null || to is null) return bands;
        double lo = Math.Min(from.BottomM, to.BottomM), hi = Math.Max(from.TopM, to.TopM), eps = 1e-12;
        return bands.Where(b => b.BottomM >= lo - eps && b.TopM <= hi + eps);
    }

    /// <summary>The stations along every chain of one layer, their cuts, and the unique solves they
    /// need. Returns the station count.</summary>
    private static int Cut(LayerWork lw, TraceStack ctx, Dictionary<string, TraceCut> solves, int dbuPerMicron,
                           CancellationToken ct)
    {
        int stationCount = 0;
        foreach (var chain in lw.Chains)
        {
            ct.ThrowIfCancellationRequested();
            double sAt = 0;
            bool first = true;
            foreach (var (piece, reversed) in chain.Pieces)
            {
                var (ax, ay, bx, by) = reversed ? (piece.Bx, piece.By, piece.Ax, piece.Ay) : (piece.Ax, piece.Ay, piece.Bx, piece.By);
                if (!first) sAt += Math.Sqrt(Sq(ax - chain.LastX) + Sq(ay - chain.LastY));
                first = false;
                double len = piece.Length;
                double dx = (bx - ax) / len, dy = (by - ay) / len;
                double ux = -dy, uy = dx;
                int n = Math.Clamp((int)Math.Ceiling(len / (piece.Width / StationsPerWidth)), 1, MaxStationsPerPiece);
                double step = len / n;
                for (int i = 0; i < n; i++)
                {
                    double s = (i + 0.5) * step;
                    double qx = ax + s * dx, qy = ay + s * dy;
                    var st = new StationWork { S = sAt + s, Length = step, Ux = ux, Uy = uy };
                    if (lw.Copper!.ChordAt(qx, qy, ux, uy, 2 * lw.MaxWidth) is { } q && q.E0 >= 0 && q.E1 >= 0)
                    {
                        qx += 0.5 * (q.T0 + q.T1) * ux; qy += 0.5 * (q.T0 + q.T1) * uy;
                        double w = q.T1 - q.T0;
                        var cut = TraceCrossSection.Cut(ctx, lw.Band, qx, qy, ux, uy, -0.5 * w, 0.5 * w,
                                                        double.MaxValue, Math.Max(1.0 * dbuPerMicron, 0.015 * w));
                        st.Cut = cut;
                        if (cut.Refusal is null) solves.TryAdd(cut.Key, cut);
                    }
                    st.X = qx; st.Y = qy;
                    chain.Stations.Add(st);
                    stationCount++;
                }
                sAt += len;
                chain.LastX = bx; chain.LastY = by;
            }
            chain.Length = sAt;
        }
        return stationCount;
    }

    private static double Sq(double v) => v * v;

    /// <summary>The narrowest and widest copper read as a trace on <paramref name="band"/>, DBU — both
    /// from the distance to the nearest other conductor: a sliver a twentieth of that is an artefact of
    /// the union, and a strip ten times it is a pour's neck, not a line.</summary>
    private static (double Min, double Max) WidthRange(TraceImpedanceOptions options,
        List<CrossSectionExtractor.Band> bands, CrossSectionExtractor.Band band, int dbuPerMicron)
    {
        double nearest = double.MaxValue;
        foreach (var b in bands)
        {
            if (b.Index == band.Index) continue;
            double gap = b.TopM <= band.BottomM ? band.BottomM - b.TopM
                       : b.BottomM >= band.TopM ? b.BottomM - band.TopM : double.MaxValue;
            nearest = Math.Min(nearest, gap);
        }
        double nearestUm = nearest == double.MaxValue ? 800 : nearest * 1e6;
        double minUm = Math.Max(1, 0.05 * nearestUm);
        double maxUm = options.MaxWidthMicrons is { } mw && mw > 0 ? mw : Math.Clamp(10 * nearestUm, 1000, 8000);
        return (minUm * dbuPerMicron, maxUm * dbuPerMicron);
    }

    /// <summary>
    /// The widest copper this review reads as a trace on each drawing layer bound to a conductor, DBU —
    /// <see cref="WidthRange"/>'s own number with no width override, so artwork recognition's "pad-sized"
    /// and "wider than any trace" are the review's (<c>brief-artsch-3-board-graph.md</c> R-as3-4). Empty
    /// when the stackup is refused.
    /// </summary>
    internal static IReadOnlyDictionary<LayerKey, double> WidestTraceDbu(
        Technology tech, IReadOnlyList<LayoutShape>? shapes, int dbuPerMicron)
    {
        var (_, bands, bandOf, refusal) = TraceStack.StackOf(tech, shapes);
        var widest = new Dictionary<LayerKey, double>();
        if (refusal is not null) return widest;
        var options = new TraceImpedanceOptions();
        foreach (var (key, band) in bandOf) widest[key] = WidthRange(options, bands, band, dbuPerMicron).Max;
        return widest;
    }

    // ── 1. pieces ───────────────────────────────────────────────────────────────────────────────

    private sealed record Piece(double Ax, double Ay, double Bx, double By, double Width, int Ring, int EdgeA, int EdgeB)
    {
        public double Length => Math.Sqrt(Sq(Bx - Ax) + Sq(By - Ay));
    }

    private static List<Piece> FindPieces(TraceCopper cu, double minW, double maxW, int dbuPerMicron, CancellationToken ct)
    {
        int n = cu.Ax.Length;
        double minEdge = 10.0 * dbuPerMicron;
        double cosTol = Math.Cos(ParallelToleranceDeg * Math.PI / 180);
        double probe = Math.Max(1, 0.25 * dbuPerMicron);

        // Unit direction and inward (copper-side) normal of every long edge.
        var dx = new double[n]; var dy = new double[n]; var nx = new double[n]; var ny = new double[n];
        var len = new double[n];
        var longEdge = new bool[n];
        for (int e = 0; e < n; e++)
        {
            len[e] = cu.Length(e);
            if (len[e] < minEdge) continue;
            longEdge[e] = true;
            dx[e] = (cu.Bx[e] - cu.Ax[e]) / len[e]; dy[e] = (cu.By[e] - cu.Ay[e]) / len[e];
            nx[e] = -dy[e]; ny[e] = dx[e];
            double mx = 0.5 * (cu.Ax[e] + cu.Bx[e]), my = 0.5 * (cu.Ay[e] + cu.By[e]);
            if (!cu.Contains(mx + probe * nx[e], my + probe * ny[e])) { nx[e] = -nx[e]; ny[e] = -ny[e]; }
        }

        var pieces = new List<Piece>();
        for (int e = 0; e < n; e++)
        {
            if (!longEdge[e]) continue;
            if ((e & 255) == 0) ct.ThrowIfCancellationRequested();
            double ax = cu.Ax[e], ay = cu.Ay[e];
            double x0 = Math.Min(ax, cu.Bx[e]), x1 = Math.Max(ax, cu.Bx[e]);
            double y0 = Math.Min(ay, cu.By[e]), y1 = Math.Max(ay, cu.By[e]);
            var near = cu.EdgesNear(Math.Min(x0, x0 + maxW * nx[e]), Math.Min(y0, y0 + maxW * ny[e]),
                                    Math.Max(x1, x1 + maxW * nx[e]), Math.Max(y1, y1 + maxW * ny[e]));
            foreach (int f in near)
            {
                if (f <= e || !longEdge[f]) continue;
                if (dx[e] * dx[f] + dy[e] * dy[f] > -cosTol) continue;   // anti-parallel only

                // f on e's copper side, at width w; e on f's copper side.
                double da = (cu.Ax[f] - ax) * nx[e] + (cu.Ay[f] - ay) * ny[e];
                double db = (cu.Bx[f] - ax) * nx[e] + (cu.By[f] - ay) * ny[e];
                if (da < minW || db < minW || da > maxW || db > maxW) continue;
                if ((ax - cu.Ax[f]) * nx[f] + (ay - cu.Ay[f]) * ny[f] <= 0) continue;
                double w = 0.5 * (da + db);
                if (Math.Abs(da - db) > Math.Max(0.05 * w, 2.0 * dbuPerMicron)) continue;

                // Overlap along e.
                double pa = (cu.Ax[f] - ax) * dx[e] + (cu.Ay[f] - ay) * dy[e];
                double pb = (cu.Bx[f] - ax) * dx[e] + (cu.By[f] - ay) * dy[e];
                double o0 = Math.Max(0, Math.Min(pa, pb)), o1 = Math.Min(len[e], Math.Max(pa, pb));
                if (o1 - o0 < Math.Max(minEdge, 0.5 * w)) continue;

                // Nothing between: a ray from e at the quarter, middle and three-quarter points hits f.
                bool clear = true;
                foreach (double frac in (ReadOnlySpan<double>)[0.25, 0.5, 0.75])
                {
                    double s = o0 + frac * (o1 - o0);
                    double px = ax + s * dx[e] + probe * nx[e], py = ay + s * dy[e] + probe * ny[e];
                    if (cu.ChordAt(px, py, nx[e], ny[e], maxW * 1.5) is not { } c || c.E1 != f) { clear = false; break; }
                }
                if (!clear) continue;

                double hx = 0.5 * w * nx[e], hy = 0.5 * w * ny[e];
                pieces.Add(new Piece(ax + o0 * dx[e] + hx, ay + o0 * dy[e] + hy,
                                     ax + o1 * dx[e] + hx, ay + o1 * dy[e] + hy, w, cu.RingOf[e], e, f));
            }
        }
        return pieces;
    }

    // ── 3. chains ───────────────────────────────────────────────────────────────────────────────

    private sealed class ChainWork
    {
        public List<(Piece Piece, bool Reversed)> Pieces { get; } = [];
        public List<StationWork> Stations { get; } = [];
        public string StartsAt = "", EndsAt = "";
        public bool StartJunction, EndJunction;

        /// <summary>The junction each end is on — a key into <see cref="LayerWork.JunctionCentres"/> — or -1.</summary>
        public int StartKey = -1, EndKey = -1;
        public double LastX, LastY, Length;

        /// <summary>Shorter than <see cref="MinAspect"/> widths: a trace only where a selector chooses it.</summary>
        public bool Short;
    }

    private sealed class StationWork
    {
        public double X, Y, Ux, Uy, S, Length;
        public TraceCut? Cut;
    }

    private sealed record LayerWork(
        LayerKey Key, string Name, CrossSectionExtractor.Band Band, TraceCopper? Copper,
        List<ChainWork> Chains, int Pours, double MaxWidth)
    {
        public List<(double X, double Y, double R)> Vias { get; init; } = [];
        /// <summary><see cref="Vias"/> and every via whose barrel this layer's copper meets: what a trace
        /// can run onto. Only the via-transition rule reads it — the pour count and the end names keep
        /// <see cref="Vias"/>, the pads that are this layer's own copper.</summary>
        public List<(double X, double Y, double R)> TransitionVias { get; init; } = [];
        public int[] IslandOfRing { get; init; } = [];

        /// <summary>Each junction's centre, by its key (<see cref="ChainWork.StartKey"/>).</summary>
        public Dictionary<int, (double X, double Y)> JunctionCentres { get; init; } = [];
    }

    private static List<ChainWork> Chain(List<Piece> pieces, TraceCopper cu, int[] islandOfRing,
                                         out Dictionary<int, (double X, double Y)> junctionCentres)
    {
        int m = pieces.Count;
        // Ends: 2i is piece i's A end, 2i+1 its B end.
        (double X, double Y) End(int k) => k % 2 == 0 ? (pieces[k / 2].Ax, pieces[k / 2].Ay) : (pieces[k / 2].Bx, pieces[k / 2].By);

        var candidates = new List<int>[2 * m];
        for (int k = 0; k < 2 * m; k++) candidates[k] = [];

        // Sort ends by x so the neighbour search is a sweep, not all pairs.
        var order = Enumerable.Range(0, 2 * m).OrderBy(k => End(k).X).ToArray();
        double maxWidth = pieces.Count == 0 ? 0 : pieces.Max(p => p.Width);
        for (int a = 0; a < order.Length; a++)
        {
            int i = order[a];
            var (ix, iy) = End(i);
            for (int b = a + 1; b < order.Length; b++)
            {
                int j = order[b];
                var (jx, jy) = End(j);
                if (jx - ix > 1.25 * maxWidth) break;
                if (i / 2 == j / 2) continue;
                var pi = pieces[i / 2]; var pj = pieces[j / 2];
                double r = 1.25 * Math.Max(pi.Width, pj.Width);
                double d2 = Sq(jx - ix) + Sq(jy - iy);
                if (d2 > r * r) continue;
                if (islandOfRing[pi.Ring] != islandOfRing[pj.Ring]) continue;
                if (!cu.Contains(0.5 * (ix + jx), 0.5 * (iy + jy))) continue;
                // The two pieces must LEAVE the meeting point in different directions: an end that
                // meets the side of a parallel piece running alongside is not a continuation.
                var (oix, oiy) = End(i ^ 1); var (ojx, ojy) = End(j ^ 1);
                double dix = oix - ix, diy = oiy - iy, djx = ojx - jx, djy = ojy - jy;
                double cos = (dix * djx + diy * djy) / Math.Max(1e-9, Math.Sqrt((dix * dix + diy * diy) * (djx * djx + djy * djy)));
                if (cos > 0.2) continue;
                candidates[i].Add(j);
                candidates[j].Add(i);
            }
        }

        var link = new int[2 * m];
        var junction = new bool[2 * m];
        for (int k = 0; k < 2 * m; k++)
        {
            link[k] = -1;
            if (candidates[k].Count >= 2) junction[k] = true;
        }
        // A junction ends every trace that meets it.
        for (int k = 0; k < 2 * m; k++)
            if (junction[k]) foreach (int j in candidates[k]) junction[j] = true;
        for (int k = 0; k < 2 * m; k++)
        {
            if (junction[k] || candidates[k].Count != 1) continue;
            int j = candidates[k][0];
            if (junction[j] || candidates[j].Count != 1 || candidates[j][0] != k) continue;
            link[k] = j;
        }

        // Which junction each junction end is on: the ends joined by candidate links, all of them junction
        // ends, are one junction (brief-artsch-5 R-as5-1). Its centre is the point the member pieces'
        // centre lines come closest to — the crossing of a T's through line and its branch — read here,
        // where every member piece is still known, before a trim or the scope drops one.
        var root = new int[2 * m];
        for (int k = 0; k < 2 * m; k++) root[k] = k;
        int Root(int x) { while (root[x] != x) x = root[x] = root[root[x]]; return x; }
        for (int k = 0; k < 2 * m; k++)
            if (junction[k])
                foreach (int j in candidates[k])
                    if (junction[j]) root[Root(j)] = Root(k);
        junctionCentres = [];
        foreach (var group in Enumerable.Range(0, 2 * m).Where(k => junction[k]).GroupBy(Root))
            junctionCentres[group.Key] = Closest([.. group.Select(k =>
            {
                var (x, y) = End(k);
                var (ox, oy) = End(k ^ 1);
                return (x, y, ox - x, oy - y);
            })]);

        var used = new bool[m];
        var chains = new List<ChainWork>();
        void Walk(int startEnd)
        {
            var chain = new ChainWork { StartJunction = junction[startEnd], StartKey = junction[startEnd] ? Root(startEnd) : -1 };
            int end = startEnd;
            while (true)
            {
                int p = end / 2;
                if (used[p]) break;
                used[p] = true;
                bool reversed = end % 2 == 1;   // entering at B → walk B→A
                chain.Pieces.Add((pieces[p], reversed));
                int exit = end ^ 1;
                chain.EndJunction = junction[exit];
                chain.EndKey = junction[exit] ? Root(exit) : -1;
                if (link[exit] < 0) break;
                end = link[exit];
            }
            chains.Add(chain);
        }
        for (int k = 0; k < 2 * m; k++)
            if (!used[k / 2] && link[k] < 0) Walk(k);
        for (int k = 0; k < 2 * m; k++)          // closed loops, if any
            if (!used[k / 2]) Walk(k);

        // A piece at a chain's END that is shorter than it is wide is the land the trace runs onto — a
        // component pad the trace overlaps, joined to it as a "width step" — not a line: round 8's
        // 0201 pads read 57.6 Ω as the last 18.7 mil of a 50 Ω trace. So is one that steps up to half
        // as wide again as the piece it joins (EndKind's own "pad") and would not be a trace on its
        // own, shorter than MinAspect of its widths: a square pad is exactly as long as it is wide, and
        // left on the chain it made the chain's width the PAD's, which dropped the whole trace as
        // Short (brief-impedance-7). Trimmed, repeatedly, from both ends; a piece like that INSIDE a
        // chain is a genuine step and stays. The step is PadWidthStep, not the 1.5 it was: a trace into
        // a pad only a little wider than itself (a fine-pitch land, a 0402 pad on a 0.3 mm line) kept
        // the pad as its last stretch and read its Z0 there, while the same trace into a wider pad did
        // not — "through the pad, but not always" (round-10 field report).
        static bool Land((Piece Piece, bool) end, (Piece Piece, bool) next) =>
            end.Piece.Length < end.Piece.Width
            || (end.Piece.Width > PadWidthStep * next.Piece.Width && end.Piece.Length < MinAspect * end.Piece.Width);
        foreach (var c in chains)
        {
            while (c.Pieces.Count > 1 && Land(c.Pieces[0], c.Pieces[1])) { c.Pieces.RemoveAt(0); c.StartJunction = false; c.StartKey = -1; }
            while (c.Pieces.Count > 1 && Land(c.Pieces[^1], c.Pieces[^2])) { c.Pieces.RemoveAt(c.Pieces.Count - 1); c.EndJunction = false; c.EndKey = -1; }
        }

        // A chain shorter than SelectedMinAspect widths is a pad; one piece shorter than a width is a
        // corner. One shorter than MinAspect widths is kept, marked Short: only a selector admits it.
        var kept = new List<ChainWork>();
        foreach (var c in chains)
        {
            double length = c.Pieces.Sum(p => p.Piece.Length);
            double width = c.Pieces.Max(p => p.Piece.Width);
            if (length < SelectedMinAspect * width || !c.Pieces.Any(p => p.Piece.Length >= width)) continue;
            c.Short = length < MinAspect * width;
            kept.Add(c);
        }
        return kept;
    }

    /// <summary>The point closest, in least squares, to every line (x, y) + t·(dx, dy); the mean of the
    /// points when the lines are (nearly) parallel and have no such point.</summary>
    private static (double X, double Y) Closest(IReadOnlyList<(double X, double Y, double Dx, double Dy)> lines)
    {
        double a = 0, b = 0, c = 0, rx = 0, ry = 0;
        foreach (var (x, y, dx, dy) in lines)
        {
            double l = Math.Sqrt(dx * dx + dy * dy);
            if (l <= 0) continue;
            double ux = dx / l, uy = dy / l;
            // (I − u uᵀ) p = (I − u uᵀ) q for each line, summed.
            double m00 = 1 - ux * ux, m01 = -ux * uy, m11 = 1 - uy * uy;
            a += m00; b += m01; c += m11;
            rx += m00 * x + m01 * y; ry += m01 * x + m11 * y;
        }
        double det = a * c - b * b;
        if (Math.Abs(det) < 1e-6 * Math.Max(1, a * c))
            return (lines.Average(l => l.X), lines.Average(l => l.Y));
        return ((c * rx - b * ry) / det, (a * ry - b * rx) / det);
    }

    /// <summary>
    /// The bends of a chain (brief-artsch-5 R-as5-1): every join between consecutive pieces that turns by
    /// <see cref="CornerMinDeg"/> or more, at the point the two centre lines meet, with the chamfer the outer
    /// corner carries — measured by walking out from the corner point along the outward bisector to the
    /// copper's edge, against where a square corner's edge would be.
    /// </summary>
    private static List<TraceCorner> Corners(ChainWork chain, TraceCopper copper)
    {
        var corners = new List<TraceCorner>();
        for (int i = 1; i < chain.Pieces.Count; i++)
        {
            var (p, pr) = chain.Pieces[i - 1];
            var (q, qr) = chain.Pieces[i];
            var (pax, pay, pbx, pby) = pr ? (p.Bx, p.By, p.Ax, p.Ay) : (p.Ax, p.Ay, p.Bx, p.By);
            var (qax, qay, qbx, qby) = qr ? (q.Bx, q.By, q.Ax, q.Ay) : (q.Ax, q.Ay, q.Bx, q.By);
            double d1x = (pbx - pax) / p.Length, d1y = (pby - pay) / p.Length;
            double d2x = (qbx - qax) / q.Length, d2y = (qby - qay) / q.Length;
            double cross = d1x * d2y - d1y * d2x, dot = d1x * d2x + d1y * d2y;
            double turn = Math.Atan2(cross, dot) * 180 / Math.PI;
            if (Math.Abs(turn) < CornerMinDeg) continue;

            // Where the centre lines meet: pb + t·d1 = qa − u·d2.
            double w = 0.5 * (p.Width + q.Width);
            double ex = qax - pbx, ey = qay - pby;
            double t = (ex * d2y - ey * d2x) / cross;
            double cx = pbx + t * d1x, cy = pby + t * d1y;
            if (Math.Abs(t) > 3 * w) { cx = 0.5 * (pbx + qax); cy = 0.5 * (pby + qay); }

            // The outer corner lies along d1 − d2 at (w/2)/cos(θ/2); a chamfer of leg m brings the copper's
            // edge m·cos(θ/2) nearer along that line.
            double half = 0.5 * Math.Abs(turn) * Math.PI / 180;
            double bx = d1x - d2x, by = d1y - d2y, bl = Math.Sqrt(bx * bx + by * by);
            double leg = 0;
            if (bl > 0 && copper.ChordAt(cx, cy, bx / bl, by / bl, 4 * w) is { E1: >= 0 } chord)
            {
                double outer = 0.5 * w / Math.Cos(half);
                leg = Math.Max(0, (outer - chord.T1) / Math.Cos(half));
                if (leg < 0.02 * w) leg = 0;
            }
            corners.Add(new TraceCorner(i - 1, (long)Math.Round(cx), (long)Math.Round(cy), turn, w, leg));
        }
        return corners;
    }

    // ── 5. findings ─────────────────────────────────────────────────────────────────────────────

    private static string EndKind(LayerWork lw, double x, double y, double dirX, double dirY, double w, bool junction)
    {
        if (junction) return "junction";
        foreach (var (vx, vy, r) in lw.Vias)
            if (Math.Sqrt(Sq(vx - x) + Sq(vy - y)) <= r + w) return "via";
        // Walk out along the trace's own direction for two widths: copper that widens past half as
        // much again is a pad; copper that stops at once is the end of the copper, unless it goes on
        // in some other forward direction (a curve, or a bend too gentle to have been joined).
        for (int k = 1; k <= 8; k++)
        {
            double f = 0.25 * k * w;
            double px = x + dirX * f, py = y + dirY * f;
            if (!lw.Copper!.Contains(px, py))
            {
                if (k > 1) return "continues";
                for (int j = -4; j <= 4; j++)
                {
                    double a = j * Math.PI / 9, cs = Math.Cos(a), sn = Math.Sin(a);
                    double qx = x + w * (cs * dirX - sn * dirY), qy = y + w * (sn * dirX + cs * dirY);
                    if (j != 0 && lw.Copper.Contains(qx, qy)) return "continues";
                }
                return "open end";
            }
            if (lw.Copper.ChordAt(px, py, -dirY, dirX, 4 * lw.MaxWidth) is { } c && c.T1 - c.T0 > PadWidthStep * w) return "pad";
        }
        return "continues";
    }

    /// <summary>A chain's two end points as its <see cref="TraceRun"/> reports them, DBU — the first
    /// piece's start and the last piece's end, walked in chain order. What an acceptance's key is made of,
    /// for a trace in scope and one left out alike.</summary>
    private static (long X0, long Y0, long X1, long Y1) ChainEnds(ChainWork chain)
    {
        var first = chain.Pieces[0]; var last = chain.Pieces[^1];
        var (sx, sy) = first.Reversed ? (first.Piece.Bx, first.Piece.By) : (first.Piece.Ax, first.Piece.Ay);
        var (ex, ey) = last.Reversed ? (last.Piece.Ax, last.Piece.Ay) : (last.Piece.Bx, last.Piece.By);
        return ((long)Math.Round(sx), (long)Math.Round(sy), (long)Math.Round(ex), (long)Math.Round(ey));
    }

    /// <summary>
    /// Which stations are a via TRANSITION: within <paramref name="reach"/> (DBU) of the land of a via
    /// this trace runs onto. A via is on the trace when a station comes within the land's radius plus
    /// that station's width of its centre — the reach <see cref="EndKind"/> names a "via" end by — so
    /// a ground via stitching beside a trace, which clears no plane, never excuses anything. Each via
    /// that excused a stretch is named in <paramref name="notes"/>, with how much it excused.
    /// </summary>
    private static bool[] ViaTransitions(List<TraceStation> stations, List<(double X, double Y, double R)> vias,
                                         double reach, TraceImpedanceReport fmt, List<string> notes)
    {
        var near = new bool[stations.Count];
        if (!(reach > 0) || vias.Count == 0 || stations.Count == 0) return near;

        static double Dist(TraceStation s, double x, double y) => Math.Sqrt(Sq(s.X - x) + Sq(s.Y - y));
        foreach (var (vx, vy, r) in vias)
        {
            if (!stations.Any(s => Dist(s, vx, vy) <= r + s.Width)) continue;
            double excused = 0;
            for (int i = 0; i < stations.Count; i++)
            {
                if (Dist(stations[i], vx, vy) > r + reach) continue;
                if (!near[i]) excused += stations[i].Length;
                near[i] = true;
            }
            if (excused > 0)
                notes.Add($"Not checked for {fmt.Len(excused)} into the via at {fmt.Pt((long)Math.Round(vx), (long)Math.Round(vy))} " +
                          $"(within {fmt.Len(reach)} of its land): a plane is normally cleared round a via, so this " +
                          "stretch has no reference under it by design. Verify the transition with an EM run.");
        }
        return near;
    }

    private static TraceRun Assemble(
        ChainWork chain, LayerWork lw,
        System.Collections.Concurrent.ConcurrentDictionary<string, (double C, double C0, string? Refusal)> answers,
        TraceImpedanceOptions options, int dbuPerMicron, ref int id)
    {
        var fmt = new TraceImpedanceReport { DbuPerMicron = dbuPerMicron, DisplayUnit = options.DisplayUnit ?? LayoutUnit.Um };
        double lo = options.TargetOhms * (1 - options.TolerancePercent / 100);
        double hi = options.TargetOhms * (1 + options.TolerancePercent / 100);
        double wlo = options.TargetOhms * (1 - options.WarningPercent / 100);
        double whi = options.TargetOhms * (1 + options.WarningPercent / 100);

        var stations = new List<TraceStation>();
        foreach (var st in chain.Stations)
        {
            var cut = st.Cut;
            double? z0 = null, eeff = null;
            string? refusal = cut is null ? "the cut left the copper" : cut.Refusal;
            if (cut is not null && cut.Refusal is null && answers.TryGetValue(cut.Key, out var a))
            {
                if (a.Refusal is null) { z0 = TraceCrossSection.Z0(a.C, a.C0); eeff = a.C / a.C0; }
                else refusal = a.Refusal;
            }
            stations.Add(new TraceStation
            {
                X = (long)Math.Round(st.X), Y = (long)Math.Round(st.Y), Ux = st.Ux, Uy = st.Uy,
                S = st.S, Length = st.Length,
                Width = cut?.Width ?? 0,
                Z0 = z0, Eeff = eeff,
                Configuration = cut?.Configuration,
                ReferenceBelow = cut?.LowerRef?.Layer.Name ?? (cut?.Plane == true ? "stackup bottom ground" : null),
                ReferenceAbove = cut?.UpperRef?.Layer.Name,
                GapLeft = cut?.GapL, GapRight = cut?.GapR,
                H = cut?.HDbu,
                Refusal = refusal,
            });
        }

        var ends = ChainEnds(chain);
        var first = chain.Pieces[0]; var last = chain.Pieces[^1];
        var (sx, sy, sdx, sdy) = first.Reversed
            ? (first.Piece.Bx, first.Piece.By, first.Piece.Bx - first.Piece.Ax, first.Piece.By - first.Piece.Ay)
            : (first.Piece.Ax, first.Piece.Ay, first.Piece.Ax - first.Piece.Bx, first.Piece.Ay - first.Piece.By);
        var (ex, ey, edx, edy) = last.Reversed
            ? (last.Piece.Ax, last.Piece.Ay, last.Piece.Ax - last.Piece.Bx, last.Piece.Ay - last.Piece.By)
            : (last.Piece.Bx, last.Piece.By, last.Piece.Bx - last.Piece.Ax, last.Piece.By - last.Piece.Ay);
        double sl = Math.Max(1e-9, Math.Sqrt(sdx * sdx + sdy * sdy)), el = Math.Max(1e-9, Math.Sqrt(edx * edx + edy * edy));
        string startsAt = EndKind(lw, sx, sy, sdx / sl, sdy / sl, first.Piece.Width, chain.StartJunction);
        string endsAt = EndKind(lw, ex, ey, edx / el, edy / el, last.Piece.Width, chain.EndJunction);

        // ── the findings ────────────────────────────────────────────────────────────────────────
        var issues = new List<TraceIssue>();
        var runNotes = new List<string>();

        // The via transitions: stations within ViaTransitionMicrons of the land of a via this trace
        // runs onto are not checked — no finding starts, runs or steps there, and they are left out of
        // the trace's numbers — and each via so treated is named in a note.
        var nearVia = ViaTransitions(stations, lw.TransitionVias, options.ViaTransitionMicrons * dbuPerMicron, fmt, runNotes);

        void Runs(Func<int, bool> flaggedAnywhere, Action<int, int> emit)
        {
            bool flagged(int k) => !nearVia[k] && flaggedAnywhere(k);
            int i = 0;
            while (i < stations.Count)
            {
                if (!flagged(i)) { i++; continue; }
                int j = i;
                while (j + 1 < stations.Count && flagged(j + 1)) j++;
                emit(i, j);
                i = j + 1;
            }
        }
        // From the start of station i's stretch to the end of station j's, along the trace.
        (long, long, long, long) Span(int i, int j)
        {
            var a = stations[i]; var b = stations[j];
            return ((long)Math.Round(a.X - 0.5 * a.Length * a.Uy), (long)Math.Round(a.Y + 0.5 * a.Length * a.Ux),
                    (long)Math.Round(b.X + 0.5 * b.Length * b.Uy), (long)Math.Round(b.Y - 0.5 * b.Length * b.Ux));
        }
        double SpanLen(int i, int j) => stations[j].S - stations[i].S + 0.5 * (stations[i].Length + stations[j].Length);

        // ── severities ─────────────────────────────────────────────────────────────────────────
        // Every finding kind is a Warning or a Fail, and this is the one place that says which:
        //
        //   OutOfTolerance    Warning inside the warning band, Fail outside it — and, with a highest
        //                     frequency given, Warning again when the stretch is under λ/20 there: a
        //                     neck-down into a pad is not what fails a line at the frequency it carries.
        //   ReturnBroken      Fail. The return current has to go round the gap, however short it is.
        //   NoReference       Fail. There is no impedance to speak of without a return.
        //   PartialReference  Warning. A plane edge under the trace moves Z0, and the Z0 finding (if
        //                     any) already says by how much.
        //   ReferenceStep     Warning. Often a designed transition; the reviewer decides.
        //   Unsolved          Warning. Part of the trace was not checked, which is not the same as wrong.
        //
        // Not configurable per kind: the table is the product's position (brief-impedance-1 §6).

        // Z0 outside the band.
        Runs(i => stations[i].Z0 is { } z && (z < lo || z > hi), (i, j) =>
        {
            var run = stations.Skip(i).Take(j - i + 1).ToList();
            var zs = run.Select(s => s.Z0!.Value).ToList();
            var (x0, y0, x1, y1) = Span(i, j);
            string range = zs.Min() == zs.Max() || Math.Abs(zs.Max() - zs.Min()) < 0.05
                ? $"{zs[0]:0.0} Ω" : $"{zs.Min():0.0}–{zs.Max():0.0} Ω";
            string head = $"Z0 {range} over {fmt.Len(SpanLen(i, j))} from {fmt.Pt(x0, y0)} to {fmt.Pt(x1, y1)}, outside ";
            if (zs.All(z => z >= wlo && z <= whi))
            {
                issues.Add(new TraceIssue(TraceIssueKind.OutOfTolerance, IssueSeverity.Warning, x0, y0, x1, y1,
                    head + $"{options.TargetOhms:0.#} Ω ± {options.TolerancePercent:0.#} %, inside ± {options.WarningPercent:0.#} %."));
                return;
            }
            string text = head + $"{options.TargetOhms:0.#} Ω ± {options.WarningPercent:0.#} %";
            var severity = IssueSeverity.Fail;
            if (options.MaxFrequencyHz is { } f)
            {
                // Electrical length over free-space λ: Σ Length·√ε_eff, each station its own ε_eff.
                double metres = run.Sum(s => s.Length * Math.Sqrt(s.Eeff ?? 1)) / dbuPerMicron * 1e-6;
                double waves = metres * f / 299_792_458.0;
                string at = $"{waves:0.000} λ at {TraceImpedanceReport.Hz(f)}";
                if (waves < 1 / ShortFraction) { severity = IssueSeverity.Warning; text += $"; {at} — electrically short"; }
                else text += $"; {at}";
            }
            issues.Add(new TraceIssue(TraceIssueKind.OutOfTolerance, severity, x0, y0, x1, y1, text + "."));
        });

        // The nearest layer on each side, walked along the whole trace.
        void Side(Func<TraceCut, List<CrossSectionExtractor.Band>> sideOf,
                  Func<TraceCut, CrossSectionExtractor.Band?> refOf,
                  Func<TraceCut, List<(CrossSectionExtractor.Band Band, double Cov)>> skippedOf, string where)
        {
            var cuts = chain.Stations.Select(s => s.Cut).ToList();
            // The nearest layer as the first CHECKED cut sees it: at a via transition it is often another
            // layer's trace running into the same via.
            var first = cuts.Where((c, i) => c is not null && !nearVia[i]).FirstOrDefault() ?? cuts.FirstOrDefault(c => c is not null);
            var nearest = first is { } c0 ? sideOf(c0).FirstOrDefault() : null;
            if (nearest is null) return;

            // Per station: 2 covered, 1 partly, 0 missing, −1 not cut.
            int Status(int i) => cuts[i] is not { } c ? -1
                : ReferenceEquals(refOf(c), nearest) ? 2
                : skippedOf(c).FirstOrDefault().Cov > 0.005 ? 1 : 0;
            var status = Enumerable.Range(0, cuts.Count).Select(Status).ToArray();
            // Over the stations checked: copper over a via transition — another layer's trace running into
            // the same via — is not "present elsewhere along it" for the rest of the trace.
            bool anyCovered = status.Where((_, i) => !nearVia[i]).Any(s => s == 2);
            bool anyPartial = status.Where((_, i) => !nearVia[i]).Any(s => s == 1);

            if (!anyCovered && !anyPartial)
            {
                var refs = cuts.Where(c => c is not null).Select(c => refOf(c!)?.Layer.Name).Distinct().ToList();
                string then = refs.Count == 1 && refs[0] is { } r ? $", so the reference {where} is '{r}'" : "";
                runNotes.Add($"'{nearest.Layer.Name}' has no copper {where} this trace anywhere along it{then} — " +
                             "what a layer cleared under a trace on purpose looks like.");
            }
            else
            {
                Runs(i => status[i] == 0, (i, j) =>
                {
                    var (x0, y0, x1, y1) = Span(i, j);
                    string? refThere = cuts[i] is { } c ? refOf(c)?.Layer.Name : null;
                    issues.Add(new TraceIssue(TraceIssueKind.ReturnBroken, IssueSeverity.Fail, x0, y0, x1, y1,
                        $"'{nearest.Layer.Name}' is missing {where} the trace for {fmt.Len(SpanLen(i, j))}, from " +
                        $"{fmt.Pt(x0, y0)} to {fmt.Pt(x1, y1)}, but present elsewhere along it — the return path " +
                        $"is broken there ({(refThere is null ? $"no reference {where} there" : $"the reference there is '{refThere}'")})."));
                });
            }
            Runs(i => status[i] == 1, (i, j) =>
            {
                var (x0, y0, x1, y1) = Span(i, j);
                double cov = skippedOf(cuts[i]!).First().Cov;
                issues.Add(new TraceIssue(TraceIssueKind.PartialReference, IssueSeverity.Warning, x0, y0, x1, y1,
                    $"'{nearest.Layer.Name}' covers only {cov:P0} of the width {where} the trace for " +
                    $"{fmt.Len(SpanLen(i, j))}, from {fmt.Pt(x0, y0)} to {fmt.Pt(x1, y1)}: copper edge under the trace."));
            });

            // The reference stepping between two stations neither of which the nearest layer covers.
            for (int i = 1; i < cuts.Count; i++)
            {
                if (cuts[i - 1] is not { } a || cuts[i] is not { } b) continue;
                if (status[i - 1] == 2 || status[i] == 2 || nearVia[i - 1] || nearVia[i]) continue;
                string? ra = refOf(a)?.Layer.Name, rb = refOf(b)?.Layer.Name;
                if (ra == rb || ra is null || rb is null) continue;
                var s = stations[i];
                issues.Add(new TraceIssue(TraceIssueKind.ReferenceStep, IssueSeverity.Warning, s.X, s.Y, s.X, s.Y,
                    $"The reference {where} steps from '{ra}' to '{rb}' at {fmt.Pt(s.X, s.Y)}."));
            }
        }
        if (chain.Stations.Select(st => st.Cut?.ImpliedBelow).FirstOrDefault(b => b is not null) is { } implied)
            runNotes.Add($"Nothing is drawn on '{implied.Layer.Name}', which the technology marks as the ground " +
                         "reference, so it was taken as a solid plane under the trace.");
        Side(c => c.Below, c => c.LowerRef, c => c.SkippedBelow, "below");
        Side(c => c.Above, c => c.UpperRef, c => c.SkippedAbove, "above");

        // No reference at all, and cuts that could not be solved.
        Runs(i => stations[i].Refusal == "no return conductor", (i, j) =>
        {
            var (x0, y0, x1, y1) = Span(i, j);
            issues.Add(new TraceIssue(TraceIssueKind.NoReference, IssueSeverity.Fail, x0, y0, x1, y1,
                $"No copper covers the trace on any layer, and nothing is beside it, for {fmt.Len(SpanLen(i, j))} " +
                $"from {fmt.Pt(x0, y0)} to {fmt.Pt(x1, y1)}: there is no return path to take an impedance against."));
        });
        Runs(i => stations[i].Refusal is { } r && r != "no return conductor", (i, j) =>
        {
            var (x0, y0, x1, y1) = Span(i, j);
            issues.Add(new TraceIssue(TraceIssueKind.Unsolved, IssueSeverity.Warning, x0, y0, x1, y1,
                $"The cross-section could not be solved over {fmt.Len(SpanLen(i, j))} from {fmt.Pt(x0, y0)}: {stations[i].Refusal}."));
        });

        // ── the numbers ─────────────────────────────────────────────────────────────────────────
        // Over the stations that were checked; a trace that is all via transition keeps its own.
        var counted = stations.Where((_, i) => !nearVia[i]).ToList();
        if (counted.Count == 0) counted = stations;
        var solved = counted.Where(s => s.Z0 is not null).ToList();
        double solvedLen = solved.Sum(s => s.Length);
        double inTol = solved.Where(s => s.Z0 >= lo && s.Z0 <= hi).Sum(s => s.Length);
        var configurations = solvedLen <= 0 ? [] : solved.GroupBy(s => s.Configuration ?? "")
            .Select(g => (Name: g.Key, Share: g.Sum(s => s.Length) / solvedLen))
            .OrderByDescending(c => c.Share)
            .Where(c => c.Share >= 0.02)     // a station or two at a pad's edge is not a second type
            .ToList();
        string config = configurations.FirstOrDefault().Name ?? "";
        var references = counted.SelectMany(s => new[] { s.ReferenceBelow, s.ReferenceAbove })
                                 .Where(r => r is not null).Distinct().Select(r => r!).ToList();

        id++;
        return new TraceRun
        {
            Id = $"T{id}",
            Layer = lw.Key,
            LayerName = lw.Name,
            Stations = stations,
            Pieces = [.. chain.Pieces.Select(p => p.Reversed
                ? ((long)Math.Round(p.Piece.Bx), (long)Math.Round(p.Piece.By), (long)Math.Round(p.Piece.Ax), (long)Math.Round(p.Piece.Ay), p.Piece.Width)
                : ((long)Math.Round(p.Piece.Ax), (long)Math.Round(p.Piece.Ay), (long)Math.Round(p.Piece.Bx), (long)Math.Round(p.Piece.By), p.Piece.Width))],
            Corners = Corners(chain, lw.Copper!),
            StartX = ends.X0, StartY = ends.Y0, EndX = ends.X1, EndY = ends.Y1,
            StartsAt = startsAt, EndsAt = endsAt,
            Length = chain.Length,
            WidthMin = chain.Pieces.Min(p => p.Piece.Width),
            WidthMax = chain.Pieces.Max(p => p.Piece.Width),
            Z0Min = solved.Count > 0 ? solved.Min(s => s.Z0) : null,
            Z0Max = solved.Count > 0 ? solved.Max(s => s.Z0) : null,
            Z0Mean = solvedLen > 0 ? solved.Sum(s => s.Z0!.Value * s.Length) / solvedLen : null,
            InTolerance = solvedLen > 0 ? inTol / solvedLen : 0,
            Configuration = config,
            Configurations = configurations,
            References = references,
            Issues = issues,
            Notes = runNotes,
            Verdict = TraceRun.VerdictOf(solved.Count > 0, issues),
        };
    }
}
