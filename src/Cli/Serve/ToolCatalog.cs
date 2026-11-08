using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Diagnostics;

namespace CircuitRF.Cli.Serve;

/// <summary>How a tool argument is spelled on the command line.</summary>
internal enum OptKind
{
    /// <summary>A boolean that becomes a bare flag when true, and nothing when false.</summary>
    Flag,
    Str,
    Number,
    Integer,
    /// <summary>A string resolved and confined by <see cref="PathRoot"/>.</summary>
    Path,
    /// <summary>An array joined with commas into one argument — <c>--views a,b</c>.</summary>
    StrList,
    /// <summary>An array emitted as the flag repeated — <c>--set a=1 --set b=2</c>.</summary>
    StrRepeat,
    /// <summary>As <see cref="StrRepeat"/>, with every item resolved and confined by
    /// <see cref="PathRoot"/> — <c>render --data a.npy --data b.npy</c>. A repeated flag whose items
    /// are FILES needs both halves; neither existing kind has both.</summary>
    PathRepeat,
    /// <summary>
    /// A string that is confined by <see cref="PathRoot"/> only when it names a FILE.
    ///
    /// <para><c>render --theme</c> is the case, and it is genuinely two things: a theme NAME resolved
    /// through a chain of directories the server root has nothing to do with (the workspace, the user
    /// themes folder, the shipped themes), or a <c>.ccolor</c> file the caller points at. Confining
    /// the first would turn <c>dark</c> into <c>&lt;root&gt;/dark</c>, which resolves to no theme at
    /// all; not confining the second would let a client name a file outside the root and learn from
    /// the refusal whether it exists.</para>
    ///
    /// <para>The split is <c>Render.ResolveTheme</c>'s own — a value carrying an extension, a
    /// directory separator or a root is a path — and the two must agree, which is why this comment
    /// names the other end.</para>
    /// </summary>
    PathOrName,
    /// <summary>A string whose flag takes its value OPTIONALLY: an empty string becomes the bare
    /// flag. <c>explain --analysis</c> is the case — with a name it asks about one chain, without
    /// one it asks about all of them, and passing "" as a value asks about a chain called "".
    /// </summary>
    StrOptional,
    /// <summary>
    /// A JSON OBJECT emitted as the flag repeated, one <c>key=value</c> per entry —
    /// <c>render --layer-colors "Top Copper=#e04030" --layer-colors "Silk Top=#202020"</c>.
    ///
    /// <para>An object rather than an array of <c>"k=v"</c> strings because that is the shape the
    /// thing IS, and a client that has to assemble the <c>=</c> itself will eventually assemble it
    /// wrong. Repeated rather than comma-joined for the reason a map has that an array does not: the
    /// KEY is a layer name chosen by a technology author, and one containing a comma would split into
    /// two names that resolve to nothing. The verb accepts both spellings.</para>
    /// </summary>
    StrMap,
    /// <summary>
    /// As <see cref="StrMap"/>, with every VALUE a file resolved and confined by <see cref="PathRoot"/> —
    /// <c>export part --build win-x64=&lt;root&gt;/builds/x.osdi</c> (brief vendorlib-15). The key is a name and is
    /// passed as written; only the value names a file, and a file outside the root is a refusal as for
    /// <see cref="Path"/>.
    /// </summary>
    PathMap,
}

/// <param name="Json">The argument's name in the tool call.</param>
/// <param name="Cli">The flag it becomes. Empty for a positional.</param>
internal sealed record ToolOption(string Json, string Cli, OptKind Kind, string Description);

/// <param name="Json">The argument's name in the tool call.</param>
/// <param name="IsPath">Whether it is confined to the root.</param>
/// <param name="Required">
/// Whether the verb refuses without it. Every positional was required until <c>reference</c>, whose
/// no-argument form is the topic LIST — a real answer, not a usage error — and whose second
/// positional only means anything after the first.
///
/// <para>An optional positional is emitted only while the ones before it were given: argv is
/// positional, so passing a second argument with the first absent would silently make it the FIRST,
/// which is a different query answered with nothing said about it. That case is a refusal naming the
/// argument the caller left out.</para>
/// </param>
internal sealed record ToolPositional(string Json, bool IsPath, string Description, bool Required = true);

/// <param name="Verb">The argv prefix — <c>["hb"]</c>, <c>["new","workspace"]</c>.</param>
internal sealed record ToolMode(
    string                        Selector,
    string[]                      Verb,
    ToolPositional[]              Positionals,
    ToolOption[]                  Options,
    string                        Description);

/// <param name="SelectorName">The argument that chooses the mode, or null for a single-mode tool.</param>
/// <param name="Adapter">
/// Arguments the ADAPTER itself consumes and never forwards. Advertised like any other, translated
/// into nothing.
///
/// <para><b>This is the one deliberate exception to "every argument is named after the CLI flag it
/// becomes", and it exists for exactly one thing</b> (R-rnd5-4): whether a tool result carries the
/// rendered picture back as protocol content. There is no CLI spelling of it and there should not be
/// — a command line writes the file and the person opens it, where a protocol client may have no way
/// to read the path it was handed. So it is a property of the ENVELOPE, not of the render.</para>
///
/// <para>Kept as its own list rather than as a flavour of <see cref="ToolOption"/> so that the
/// invariant stays checkable by reading: everything in <c>Modes</c> becomes argv, and the only things
/// that do not are here, where <c>ServeProtocolAdapterTests</c>'
/// <c>EveryAdvertisedArgument_IsAFlagTheVerbActuallyReads</c> exempts them by name.</para>
/// </param>
internal sealed record ToolSpec(
    string        Name,
    string        Description,
    string?       SelectorName,
    string?       SelectorDescription,
    ToolMode[]    Modes,
    ToolOption[]? Adapter = null);

/// <summary>
/// The tool surface, and the ONLY thing that translates a tool call into a command line.
///
/// <para><b>Small and broad (R-aut5-4, R-aut-9).</b> EIGHTEEN tools, not one per verb — the seventeen
/// here plus <c>HistoryBatch</c>'s, which is advertised beside them because it is the one tool that is
/// not a command line. A client that discovers tools up front carries every description for the whole
/// session whether or not it calls one, so the surface is a standing cost paid on every interaction.
/// <c>run</c> selects its analysis with an argument rather than being six tools; <c>create</c>,
/// <c>import</c> and <c>history</c> each carry the noun the CLI verb already takes; and RND-5's
/// <c>render</c> is one tool over every document kind rather than one per view (R-rnd0-4).</para>
///
/// <para><b>The schema is GENERATED from the same table that builds the command line.</b> That is
/// the point of the table: a description that says an argument exists and a translation that drops
/// it cannot happen, because there is one list. Adding a flag is one row.</para>
///
/// <para><b>The adapter makes no decisions (R-aut-1).</b> Every argument here is named after the CLI
/// flag it becomes, and nothing is defaulted, reshaped or inferred. Where the CLI refuses — a `--grid`
/// handed to a pursuit, an unstated Excellon coordinate format, a source folder holding several parts
/// — the refusal arrives from the verb, not from here. What this file refuses is only what it alone
/// can see: a tool that does not exist, an argument that does not belong to the chosen mode, an
/// argument of the wrong JSON type, and a path outside the root.</para>
///
/// <para><b>Descriptions are terse, English and invariant</b> (R-aut5-7, <c>cli.md</c> §7A). They are
/// read by a machine that pays for every word.</para>
/// </summary>
internal static class ToolCatalog
{
    // ── shared option groups ─────────────────────────────────────────────────

    private static readonly ToolOption Json  = new("only",  "--only",  OptKind.StrList,
        "Narrow the result to these cube names.");
    private static readonly ToolOption Group = new("group", "--group", OptKind.StrList,
        "Narrow the result to these group names.");

    // AUT-9 R-aut9-9. `only` and `group` narrow by cube NAME, which does nothing at all when the
    // result has one cube — and a 551-point two-port S-parameter run is 173 KB inline to answer what
    // one entry is at one frequency. These four narrow by AXIS, and they are offered on every tool
    // that returns a DataSet because the flags are taken once, before dispatch, for exactly that
    // reason.
    private static readonly ToolOption At = new("at", "--at", OptKind.StrList,
        "Pick one point per axis, as axis=value with the axis's own unit (freq=2GHz). Nearest grid "
      + "point unless interp is set; the result says which point it returned.");

    private static readonly ToolOption Range = new("range", "--range", OptKind.StrList,
        "Keep a band of an axis, as axis=lo:hi with units (freq=1GHz:3GHz).");

    private static readonly ToolOption Interp = new("interp", "--interp", OptKind.Flag,
        "Make every 'at' interpolate between the bracketing grid points instead of snapping to the "
      + "nearest. Returns a number the run did not compute, which is why it is never the default.");

    // `result`, not `format`: `render`'s own `format` argument means the picture's file format, and
    // one word meaning two things across two tools is what a caller gets wrong once and forever.
    private static readonly ToolOption Result = new("result", "--result", OptKind.Str,
        "full (default: the values) or summary (the shape, units and extents, and no values). The "
      + "shape comes back either way. On optimize, montecarlo, yield, corners, center and doe, summary is the "
      + "outcome, the values as text and each goal's margin and where it binds, without per-trial or per-corner "
      + "records; on import library, the counts per library and folder and the tolerance note.");

    /// <summary>Every DataSet-returning tool's narrowing set, written once (R-aut9-9).</summary>
    private static readonly ToolOption[] Narrowing = [Json, Group, At, Range, Interp, Result];

    /// <summary>
    /// R-aut9-11. The Gerber import's diagnostics are the best-written text on this surface and are
    /// not weakened by this — they are simply not the right default payload for a listing call whose
    /// answer is one cell name.
    /// </summary>
    private static readonly ToolOption Summary = new("summary", "--summary", OptKind.Flag,
        "Report the notes as counts by severity instead of in full. Every warning and error still "
      + "travels; only the informational notes are collapsed.");

    /// <summary>The CLI's global <c>--workspace</c> (CliEntry.HostWorkspace), on the tools that read a
    /// schematic's cells without running it. <c>run</c> declares its own, which <c>em</c> shares.</summary>
    private static readonly ToolOption HostWorkspace = new("workspace", "--workspace", OptKind.Path,
        "The workspace a loose .csch (kept outside any workspace folder) is read from: a cell reference "
      + "that names nothing is looked for there. Ignored for a document inside a workspace.");

    private static readonly ToolOption Set = new("set", "--set", OptKind.StrRepeat,
        "Override a global variable, as name=expr. Applied before elaboration.");

    /// <summary>brief-sdl-6 R-sdl6-1 / D5 — the run-wide model switch and the solve limit, on every circuit run.</summary>
    private static readonly ToolOption[] EmOptions =
    [
        new("em", "--em", OptKind.Str,
            "on runs every analysis by EM of the schematic's own layout (and every EM-capable cell instance by EM); off runs "
          + "everything by circuit models. Absent: as each analysis's model= and each instance's Model= say."),
        new("emMaxSolves", "--em-max-solves", OptKind.Str,
            "The most uncached EM solves the run may start (default 20). The solves are counted before any starts; a run "
          + "needing more is refused with the count and solves nothing. When the EM view is a setup of the cell's 3D view "
          + "(emSetup=board#Driven1) each solve takes minutes to hours: start such a run with async=true."),
    ];

    private static readonly ToolOption Analysis = new("name", "-a", OptKind.Str,
        "Which declared analysis to run. An inner analysis is promoted to the sweep that wraps it.");

    private static readonly ToolOption Output = new("output", "-o", OptKind.Path,
        "Where the result file is written. The extension picks the format.");

    private static readonly ToolOption All = new("all", "--all", OptKind.Flag,
        "Return every cube instead of the per-grid-point summary.");

    // Every option here is a flag the VERB'S OWN argument loop reads. That is not a comment, it is
    // a gate: ServeToolCatalogParityTests scans each verb's parser for the literal and fails on one
    // that is not there. It exists because an advertised flag a verb does not take does not read as
    // "unknown option" — the value is taken as the input PATH ("File not found: 3") or, where the
    // verb reads its path positionally, dropped in silence, which is a run answering a different
    // question than the one asked (R-aut-1: the adapter offers what the capability has, and nothing
    // else).
    private static readonly ToolOption MaxHarm = new("maxharm",  "--maxharm",  OptKind.Integer,
        "Override MaxHarm.");
    private static readonly ToolOption Tol     = new("tol",      "--tol",      OptKind.Number,
        "Override the convergence tolerance.");
    private static readonly ToolOption MaxIter = new("maxIter",  "--max-iter", OptKind.Integer,
        "Override the iteration cap.");

    /// <summary>What <c>hb</c> takes. <c>--maxmix</c> is HB's alone — the loadpull verbs' own loop
    /// does not read it.</summary>
    private static readonly ToolOption[] SolverOptions =
    [
        MaxHarm,
        new("maxmix", "--maxmix", OptKind.Integer, "Override MaxMixOrder (multi-tone)."),
        Tol,
        MaxIter,
    ];

    /// <summary>What <c>lp</c> and <c>lpp</c> take: <see cref="SolverOptions"/> without
    /// <c>--maxmix</c>.</summary>
    private static readonly ToolOption[] LoadpullSolverOptions = [MaxHarm, Tol, MaxIter];

    /// <summary>What <c>dc</c> takes. A DC solve has no harmonics and no tolerance override — its
    /// three knobs are the iteration cap, the bias ramp and the conductance floor.</summary>
    private static readonly ToolOption[] DcOptions =
    [
        MaxIter,
        new("dcSteps", "--dc-steps", OptKind.Integer, "Override the DC bias ramp step count."),
        new("gmin",    "--gmin",     OptKind.Number,  "Override the conductance floor."),
    ];

    private static readonly ToolOption[] LoadpullOptions =
    [
        new("pin",         "--pin",         OptKind.Str,     "Override the drive ladder, as start:step:max in dBm."),
        new("compression", "--compression", OptKind.Number,  "Override the compression target, dB."),
        new("grid",        "--grid",        OptKind.Path,    "lp only: the Gamma grid to read."),
        new("outGrid",     "--out-grid",    OptKind.Path,    "lpp only: where found terminations are written."),
    ];

    /// <summary>What both statistical run modes take (brief-yield-5 R-ya5-5) — every one a flag the verb's own
    /// loop reads. They override the file's statistics line for this run only.</summary>
    private static readonly ToolOption[] YieldOptions =
    [
        new("trials",       "--trials",       OptKind.Integer, "Trials to run; with autostop, the most it runs."),
        new("seed",         "--seed",         OptKind.Integer, "The seed every draw is a function of, with the trial number."),
        new("sampling",     "--sampling",     OptKind.Str,     "random, lhs or sobol."),
        new("confidence",   "--confidence",   OptKind.Str,     "The confidence of the yield interval, as a percent (95%)."),
        new("nonconverged", "--nonconverged", OptKind.Str,     "fail (a trial that does not evaluate counts as a fail) or warn (left out)."),
        new("save",         "--save",         OptKind.Str,     "scalars, all, a trial count, or auto: which trials keep their full results."),
        new("process",      "--process",      OptKind.Str,     "1 or 0: kit process draws on or off."),
        new("mismatch",     "--mismatch",     OptKind.Str,     "1 or 0: kit mismatch draws on or off."),
        new("sigmaScale",   "--sigma-scale",  OptKind.Number,  "Scales every kit sigma."),
        new("parallel",     "--parallel",     OptKind.Integer, "Trials evaluated at once."),
        new("analyses",     "--analyses",     OptKind.Str,     "goals (only the analyses a goal names) or all."),
        new("vars",         "--vars",         OptKind.StrList, "Draw only these statistical entries; the rest stay at nominal."),
        new("goals",        "--goals",        OptKind.StrList, "Score only these of the enabled goals."),
        new("contributions","--contributions",OptKind.Flag,    "Also return what drives each goal's and measurement's spread."),
        new("trial",        "--trial",        OptKind.Integer, "Re-run only this trial and return what it drew and how it scored (result.yield.trial)."),
        new("savePreset",   "--save-preset",  OptKind.Str,     "A .csch only, with trial: add that trial's values as a preset of this name."),
        new("saveCorner",   "--save-corner",  OptKind.Str,     "A .csch only, with trial: add a statistical corner of this name naming that trial."),
        new("output",       "-o",             OptKind.Path,    "The result .npy; default <design>.yield.npy beside the design. With trial: that trial's results."),
        new("corners",      "--corners",      OptKind.StrList, "Run at each of these enabled corners (the statistics line's corners= otherwise), process draws off there; result.corners."),
        // brief-sdl-7 D8: a trial count of EM solves is refused; em=off runs the trials by circuit models.
        EmOptions[0],
    ];

    // brief-img-11 R-im11-5 — a picture source's flags, the same on convert and recognize. Every one is read by
    // PictureFlags.Take, which both verbs' argument loops call first.
    private static readonly ToolOption[] PictureTraceOptions =
    [
        new("scale",       "--scale",        OptKind.Str,
            "A picture's scale: a length per pixel with its unit (25.4um/px), or auto (accept land-pattern evidence when "
          + "at least 3 parts agree). With no scale, dpi or scalePoints a picture is refused, listing the evidence found."),
        new("dpi",         "--dpi",          OptKind.Number, "A picture's scale as pixels per inch, stated by you."),
        new("scalePoints", "--scale-points", OptKind.Str,    "A picture's scale from two pixel points and their distance: x0,y0,x1,y1=12.7mm."),
        new("layerMap",    "--layer-map",    OptKind.Str,
            "A picture's colours: #rrggbb=<layer>[+<layer>]|drill|outline|silk|background|ignore, comma-separated; a colour "
          + "not named is ignored. Without it the automatic map is used and returned in layers, in this spelling. "
          + "A dxf source instead takes LAYER=target,... where target is ignore, add or a layer of tech, optionally target:invert "
          + "or target:drawn: a layer on a conductor fills, a drill layer makes vias, the outline layer closes, and a layer not named is refused."),
        new("preset",      "--preset",       OptKind.Str,    "A saved image-trace preset (map and options) by name."),
        new("minFeature",  "--min-feature",  OptKind.Number, "Specks smaller than this many square pixels are dropped."),
        new("simplify",    "--simplify",     OptKind.Number, "Outline simplification tolerance, pixels."),
        new("edgeStroke",  "--edge-stroke",  OptKind.Number,
            "The width, pixels, of a line the picture draws on every shape's edge, taken back off each traced shape; a "
          + "circuitRF layout picture draws one 2 px wide."),
        new("snapDeg",     "--snap-deg",     OptKind.Number, "Edges within this many degrees of an axis (or 45) are snapped; 0 snaps none."),
        new("snap45",      "--snap45",       OptKind.Str,    "on or off: snap to 45 degrees as well as to the axes."),
    ];

    // brief-img-14 R-im14-14 — a plot picture's flags, each read by PlotFlags.Take in recognize's argument loop.
    private static readonly ToolOption[] PlotOptions =
    [
        new("pictures",    "--picture",      OptKind.PathRepeat,
            "Plots: more pictures read into the same session as path (an S21 plot and an S11 chart make one .s2p)."),
        new("plot",        "--plot",         OptKind.Str,    "Plots: read only these plots, by session number (1,3); the rest are drawn, not read."),
        new("xAxis",       "--x-axis",       OptKind.StrRepeat,
            "Plots: calibrate an axis whose labels were not read: <px>=<value>,<px>=<value>[@plot], pixels along the axis "
          + "and their values (120px=1GHz,900px=5GHz@2). Plot 1 without @."),
        new("yAxis",       "--y-axis",       OptKind.StrRepeat, "Plots: as xAxis, for the y axis (80px=0,560px=-40)."),
        new("y2Axis",      "--y2-axis",      OptKind.StrRepeat,
            "Plots: as xAxis, for a second y axis on the right of a plot whose first is on the left (80px=180,560px=-180)."),
        new("xLog",        "--x-log",        OptKind.Flag,   "Plots: every x axis is logarithmic (otherwise the better fit is taken)."),
        new("yLog",        "--y-log",        OptKind.Flag,   "Plots: every y axis is logarithmic."),
        new("y2Log",       "--y2-log",       OptKind.Flag,   "Plots: every right-hand second y axis is logarithmic."),
        new("trace",       "--trace",        OptKind.StrRepeat,
            "Plots: assign a trace: <T3|#rrggbb|solid|dashed|dotted>[@plot]=S21:db, quantities db, mag, phase, rad, re, im, "
          + "complex, vswr, rl, il; or =ignore. On a plot with a y axis on both sides, append ,axis=left|right to say which "
          + "one it is read against (T3=S21:phase,axis=right), or give the axis alone (T3=axis=right)."),
        new("fit",         "--fit",          OptKind.StrRepeat,
            "Plots: fit a curve to a rectangular trace, written in place of the raw reading in the Touchstone and as y_fit in the "
          + "CSV: <T3|#rrggbb|solid|dashed|dotted|all>[@plot]=linear|exp|poly[:<degree 1-8>]|spline[:<RMS in the trace's units>]|none "
          + "(T3=poly:3, all=linear, T2=spline:0.05). Each fit's expression, coefficients and residual are in the result."),
        new("probe",       "--probe",        OptKind.StrRepeat,
            "Plots: read every rectangular trace whose range covers this x, interpolated between its samples (log x on a log "
          + "axis), never extrapolated: a frequency with its unit (2.4GHz) or a plain number in the axis's own units. Each "
          + "value is in result.recognize.plots.probes, with a readout to the picture's resolution."),
        new("smithZ0",     "--smith-z0",     OptKind.Str,    "Plots: a Smith chart's reference impedance (50Ohm, the stated default)."),
        new("freq",        "--freq",         OptKind.StrRepeat,
            "Plots: a chart trace's start and stop frequency, <trace>=<start>..<stop> (T4=1GHz..5GHz); combines with markers."),
        new("phase",       "--phase",        OptKind.Str,
            "Plots: a magnitude with no phase: S21=min (minimum phase, the default, an estimate)|zero|delay:<t>, comma-separated."),
        new("fill",        "--fill",         OptKind.Str,
            "Plots: an entry nobody plotted: S12=S21|zero|default|unassigned, S22=S11, comma-separated."),
        new("prefer",      "--prefer",       OptKind.Str,    "Plots: of two traces of one entry, which is written: S11=chart|rect|T3."),
        new("grid",        "--grid",         OptKind.Str,    "Plots: the Touchstone's grid, <start>..<stop>/<points>[log] (1GHz..5GHz/81)."),
        new("format",      "--format",       OptKind.Str,    "Plots: db (default), ma or ri."),
        new("ports",       "--ports",        OptKind.Integer,
            "Plots: the file's ports, 1-4 (default: the highest port any trace names). A trace naming a port beyond it waits."),
        new("csv",         "--csv",          OptKind.Path,   "Plots: also write the raw digitised traces as CSV, in each plot's own units. Never waits."),
        new("keepSources", "--keep-sources", OptKind.Str,    "Plots: on (default) or off — keep the source pictures beside the Touchstone."),
        // brief-pdg-4 §4 — digitising by hand: the dialog's Manual steps. No frame is found and no tick label read.
        new("manualBox",   "--manual-box",   OptKind.Str,
            "Plots, by hand: the plot area as two opposite corners in pixels, optionally turned counter-clockwise about its centre "
          + "(120px,40px,900px,560px@3). Its left/right edges are xRange's low/high, its bottom/top yRange's; needs both."),
        new("xRange",      "--x-range",      OptKind.Str,
            "Plots, by hand: the x values at the box's left and right edges, <low>..<high>[:log] — frequencies with units "
          + "(100MHz..10GHz:log) or plain numbers for an x that is not frequency."),
        new("yRange",      "--y-range",      OptKind.Str,    "Plots, by hand: the y values at the box's bottom and top edges, <low>..<high>[:log] (-40..0)."),
        new("y2Range",     "--y2-range",     OptKind.Str,    "Plots, by hand: a right-hand y scale's values at the bottom and top edges (-180..180): two y scales."),
        new("curve",       "--curve",        OptKind.StrRepeat,
            "Plots, by hand: a curve's points in pixels, <name>=<px>,<py>;<px>,<py>;…[,axis=left|right][,is=S21:db]. The points "
          + "ARE the curve; is= makes it an S-parameter quantity a Touchstone (output) can take, otherwise it is data."),
        new("follow",      "--follow",       OptKind.StrRepeat,
            "Plots, by hand: <name>=<px>,<py>[,axis=left|right][,is=S11:db] — one click on a curve; it is followed by colour "
          + "inside the box and thinned to fixpoints for the curve of that name, a coloured run preferred to grey or black "
          + "grid ink. The result names the colour followed and any grid ink passed over. is= makes it an S-parameter quantity "
          + "a Touchstone (output) can take, as curve's does. A click on blank paper follows nothing and says so."),
        new("allTraces",   "--all-traces",   OptKind.Flag,
            "Plots: list every trace of a reading that made no Touchstone and named no trace. Default: the count and the "
          + "five longest."),
        new("sample",      "--sample",       OptKind.Integer, "Plots, by hand: each curve sampled at this many x values between its first and last point (101)."),
        new("sampleStep",  "--sample-step",  OptKind.Str,    "Plots, by hand: sample every step in x instead (50MHz, or a plain number)."),
        new("method",      "--method",       OptKind.Str,
            "Plots, by hand: linear (straight between points, in log coordinates on a log axis) or smooth (a monotone cubic that "
          + "never overshoots). Nothing is extrapolated."),
    ];

    private static readonly ToolOption PictureUnderlay = new("underlay", "--underlay", OptKind.Str,
        "on or off: lay the picture, locked at 35 %, under what is written. Default on.");

    private static readonly ToolOption PictureOverlayPath = new("overlayPath", "--overlay", OptKind.Path,
        "Also keep the overlay picture (the source with what was read drawn over it) as this .png. Written even "
      + "when the reading is refused.");

    /// <summary>The adapter argument that returns the overlay as image content (R-im11-5). Named here so the row that
    /// advertises it and the code that reads it cannot part company.</summary>
    public const string OverlayArgument = "overlay";

    private static readonly ToolOption PictureOverlay = new(OverlayArgument, "", OptKind.Flag,
        "Return the overlay picture (the source with what was read drawn over it, unknowns in amber) as image content "
      + "beside the JSON. Default true when the path is a picture. Without overlayPath it is returned, not kept.");

    /// <summary>
    /// Whether a <c>convert</c> or <c>recognize</c> call wants the overlay back (R-im11-5): as it says, else whenever its
    /// path is a picture. The one default the adapter supplies, and the brief's: an agent reading a picture should
    /// SEE what was read, without knowing to ask.
    /// </summary>
    public static bool OverlayAsked(string tool, JsonObject? arguments, string[] argv)
    {
        if (tool is not ("convert" or "recognize")) return false;
        if (arguments?[OverlayArgument] is { } node && node.GetValue<JsonElement>().ValueKind is JsonValueKind.True or JsonValueKind.False)
            return node.GetValue<bool>();
        return argv.Length > 1 && DocumentKinds.Classify(argv[1]) == DocumentKind.Picture;
    }

    /// <summary>
    /// The largest rendered file handed back inside a tool result (R-rnd5-4).
    ///
    /// <para><b>The number comes from the measured table in <c>src/Cli/RESOLVED.md</c> (RND-2), not
    /// from taste.</b> A real 6-layer board at the default 1600x1200 page is 1.4 MB as a PNG, 9.6 MB
    /// as a <c>--detail full</c> PDF and 23.5 MB as a <c>--detail full</c> SVG; the same board at
    /// <c>--detail screen</c> is 1.0 MB / 2.6 MB / 6.2 MB. Four mebibytes therefore admits every
    /// raster this verb plausibly produces — which is the format an agent wants when it wants to LOOK
    /// at something — and every schematic and symbol in any format, while refusing exactly the two
    /// cases where a caller genuinely should have narrowed: a whole dense board as vector geometry.
    /// Base64 adds a third on top, which is why the cap is on the file rather than on the frame.</para>
    ///
    /// <para>A file over it is not truncated and not dropped in silence — see
    /// <c>CliDiagnostics.ServeImageTooLarge</c>.</para>
    /// </summary>
    public const long AttachmentCapBytes = 4L * 1024 * 1024;

    /// <summary>EMB-5 R-emb5-1 — the adapter argument that starts a <c>run</c> or <c>check</c> as a job
    /// (<see cref="ServeJobs"/>). Named here so the row that advertises it and the code that reads it cannot part company.</summary>
    public const string AsyncArgument = "async";

    private static readonly ToolOption AsyncCall = new(AsyncArgument, "", OptKind.Flag,
        "Return at once with a job id instead of waiting for the result; job action=status|cancel|result id=<id> "
      + "then reports progress, stops it, and returns the result this call would have returned. Default false.");

    /// <summary>Whether this call asked to be a job.</summary>
    public static bool AsyncAsked(JsonObject? arguments)
        => arguments?[AsyncArgument] is { } node && node.GetValue<JsonElement>().ValueKind == JsonValueKind.True;

    /// <summary>The adapter argument that asks for the rendered file back in the result. Named here
    /// so the row that advertises it and the code that reads it cannot part company.</summary>
    public const string AttachImageArgument = "attachImage";

    /// <summary>Whether this call asked for the picture. False for every tool that has no such
    /// argument, and false for the one that does unless the client said so — R-rnd5-4's default.</summary>
    public static bool AttachmentAsked(JsonObject? arguments)
        => arguments?[AttachImageArgument] is { } node
        && node.GetValue<JsonElement>().ValueKind == JsonValueKind.True;

    // ── the sixteen tools ─────────────────────────────────────────────────────

    public static readonly ToolSpec[] Tools =
    [
        new("run",
            "Run one analysis on a netlist or an EM setup and return its result document. "
          // R-aut9-10: whichever payload rule applies, the SHAPE is uniform — `sparam` returned its
          // whole result inline while `lpp` returned a written path and nothing else, with nothing
          // in this schema to say which a caller would get.
          + "Every run returns result.shape — the groups, cubes, units and axis extents it produced. "
          + "sparam, dc, hb and em return the values inline as well; fields and env return all but their large cubes (a pattern; waveforms and a spectrum); lp and lpp return their "
          + "one-row-per-grid-point summary instead, and hand over the cubes when asked with all, "
          + "only or group. Narrow a large result with at, range or result=summary rather than "
          + "receiving it whole. A 3D em setup's Problem3D decides what comes back: Driven an S-matrix, "
          + "Electrostatic/Magnetostatic a C or L matrix, Eigenmode the modes (f, Q, Participation) and "
          + "no Touchstone, Thermal temperatures. optimize runs the design's tune, goal and optimize lines "
          + "and returns result.optimize — the best values as the text to write into the file, each goal's "
          + "margin. With showIterations it also returns each iteration and sends a progress notification per iteration. "
          + "When a goal reads an analysis run by EM, optimize checks the layout's connectivity at every candidate, reports "
          + "each variable's connected range, spends at most emBudget EM solves and returns only an EM-verified best point "
          + "(result.optimize.em); apply writes it into the schematic and its layout. "
          + "montecarlo and yield run the tolerances on the tune lines (and a kit's statistics) and return result.yield — "
          + "the yield with its interval, each goal's yield and worst trial, the spread of every margin and measurement; "
          + "trial=<n> re-runs one trial. corners evaluates every enabled corner and returns result.corners — a corner × goal "
          + "margin table and each goal's worst corner; generate returns the corner lines a cross product makes. "
          + "center moves the opt=1 nominals to maximize yield and returns result.center — the centred values as text, "
          + "the yield vs iteration, and the start and centred yields verified on the same fresh trials. "
          + "doe runs a design of experiments over the opt=1 ranges (or the tolerances) and returns result.doe — each "
          + "response's effects, which are active, and what each is confounded with; optimum adds the fitted model's best "
          + "point and its confirming simulation. "
          + "sensitivity steps each value + and - about the nominal and returns result.sensitivity — per goal, the values "
          + "that move it most, by how much per tolerance or per 1 %, and which respond nonlinearly.",
            "analysis",
            "Which analysis to run.",
            [
                // No `-a`: RunSparam takes the first typed SParameterAnalysis — src/Cli/RESOLVED.md
                // records that gap. `--set` it reads, through hb's own override path.
                new("sparam", ["sparam"],
                    [new("path", true, "The .cnl to run.")],
                    [Set, Output, .. Narrowing, .. EmOptions,
                     new("freq", "--freq", OptKind.Str, "Frequency sweep as start:stop:step (1GHz, 100MHz, 1e9).")],
                    "S-parameters. -o writes a Touchstone (.s1p … .s99p) or the cubes (.npy, .mat, .txt); "
                  + "an extension naming neither is refused."),
                // DcSettingsFrom's own three knobs rather than HB's, and the same `--set`.
                new("dc", ["dc"],
                    [new("path", true, "The .cnl to run.")],
                    [Set, .. Narrowing, .. DcOptions, .. EmOptions],
                    "DC operating point."),
                new("hb", ["hb"],
                    [new("path", true, "The .cnl to run.")],
                    [Analysis, Set, Output, All, .. Narrowing, .. SolverOptions, .. EmOptions],
                    "Harmonic balance. Runs the parametric sweep when one wraps it."),
                // The `env` verb: a MOD_Source's modulated signal through the circuit. Its waveforms and spectrum stay
                // out of the default document (JsonRun.InlineCubeLimit); all, only or group bring them in.
                new("env", ["env"],
                    [new("path", true, "The .cnl or .csch whose envelope (type=env) analysis to run.")],
                    [Analysis, Set, Output, All, .. Narrowing,
                     new("stop",     "--stop",     OptKind.Str, "Simulated time, s (200e-6)."),
                     new("step",     "--step",     OptKind.Str, "Envelope step, s, or auto: the signal's sample rate times the accuracy."),
                     new("accuracy", "--accuracy", OptKind.Str, "draft (1x the signal rate), standard (4x) or fine (8x)."),
                     new("envelope", "--envelope", OptKind.Str, "auto, single or lattice: whether widely spaced carriers get their own fundamental."),
                     new("segments", "--segments", OptKind.Str, "Parallel-in-time segments: auto (one per core) or a count; 1 runs sequentially."),
                     new("warmup",   "--warmup",   OptKind.Str, "Warm-up before each segment, s, or auto."),
                     new("probes",   "--probes",   OptKind.Str, "Deprecated: nodes, separated by commas, whose voltages are recorded beside the labeled nets'.")],
                    "Circuit envelope: the circuit driven by its MOD_Source's LTE, NR, CW or I/Q signal. Every labeled net "
                  + "records its voltage (V, and VSD in dBV/Hz); every IProbe its delivered power, on a branch axis: ACLR "
                  + "(channel power, ACPR1/ACPR2 per carrier), PSD, AM-AM/AM-PM, IMD for two carriers, Pavg_dBm, Gt_dB, "
                  + "DE_pct, NMSE_dB and PAPR_dB; Pin_avg_dBm and Pdc_W are scalars. The time waveforms (V, I) and spectra are in "
                  + "the shape and in -o's file; ask for them with all, only or group. Runs the parametric sweep when one wraps it. "
                  + "Minutes, not seconds: 200 us of an NR 20 MHz signal is about a minute on 10 cores. To plot the power spectrum "
                  + "at an IProbe, write output=<f.npy> and plot it: cube=PSD[\"Iout\",:],y=real with freqUnit=MHz. PSD is already in dBm/Hz, "
                  + "so y=real rather than db10, and its branch axis is named after the IProbes. 'reference simulations' lists every cube."),
                new("lp", ["lp"],
                    [new("path", true, "The .cnl to run.")],
                    [Analysis, Set, Output, All, .. Narrowing, .. LoadpullSolverOptions, .. LoadpullOptions, .. EmOptions],
                    "Loadpull over the directive's Gamma grid. The default result is one row per grid point "
                  + "plus the shape; ask for the cubes with all, only or group."),
                new("lpp", ["lpp"],
                    [new("path", true, "The .cnl to run.")],
                    [Analysis, Set, Output, All, .. Narrowing, .. LoadpullSolverOptions, .. LoadpullOptions, .. EmOptions],
                    "Loadpull pursuit: searches for the MXP and MXE terminations. Returns the optima and the "
                  + "result's shape; ask for the cubes with all, only or group."),
                // brief-sar-9: the `fields` verb — an EM block's per-port fields recombined by the circuit, no EM run.
                new("fields", ["fields"],
                    [new("path", true, "The .cnl or .csch whose fields analyses to run.")],
                    [Analysis, Set, Output],
                    "An EM Field Data block's saved per-port results (its .cemr names them) recombined with the waves this circuit delivers to "
                  + "its ports. A far field (a 3D run with Radiation pattern) gives F1.farfield.U, Etheta, Ephi and every antenna metric, at one "
                  + "frequency or freq=all. A field set (Sar.PerPortFields) gives SAR, B1± and their scalars at one frequency, per watt at the "
                  + "drive port. Either gives the circuit's F1.S and F1.Z0. Starts no EM run, so a part value changed with set re-runs in seconds. "
                  + "A pattern stays out of the document for its size: write it with output and read or plot that file: plot type=polar "
                  + "radial=db dbUnit=dBi trace=cube=F1.farfield.U,cut=0,y=db10. Every enabled fields analysis unless analysis names one. "
                  + "'reference cosimulation' has the workflow and every metric."),
                // brief-de-4: the `trl` verb — two fixture files from three measured standards on a bench; solves no circuit.
                new("trl", ["trl"],
                    [new("path", true, "The .cnl or .csch whose TRL analyses to run.")],
                    [Analysis, Set, Output,
                     new("fixtureA", "--fixture-a", OptKind.Path, "Where fixture A is written, replacing the directive's fixtureA (one analysis only)."),
                     new("fixtureB", "--fixture-b", OptKind.Path, "Where fixture B is written, replacing the directive's fixtureB (one analysis only).")],
                    "Thru-Reflect-Line calibration: three SnP blocks on the bench holding the measured Thru, Reflect and Line give the two "
                  + "fixtures between the instrument and the part, written as two .s2p files a Deembed block reads (A: port 1 the instrument "
                  + "side; B: port 2 the instrument side). The result's SA and SB are the fixtures, LineDeg the Line's extra length over the "
                  + "Thru (trusted inside 20-160 degrees, with a warning outside), Gamma the solved reflect. The reference plane is the "
                  + "middle of the Thru, and a part de-embedded with the fixtures is referenced to the Line's impedance. 'reference analyses' "
                  + "has the directive."),
                // brief-tuneopt-11 R-to11-5: the `opt` verb, so the document is the verb's `--json`
                // object and nothing here reshapes it. The final result by default; showIterations
                // asks for each iteration too.
                new("optimize", ["opt"],
                    [new("path", true, "The .csch or .cnl whose tune, goal and optimize lines to run.")],
                    [Set, Result,
                     new("algorithm",  "--algorithm",  OptKind.Str,     "An algorithm id for this run (reference optimizers lists them)."),
                     MaxIter,
                     new("maxEvals",   "--max-evals",  OptKind.Integer, "Evaluation limit for this run."),
                     new("time",       "--time",       OptKind.Str,     "Wall-clock limit: seconds, or a number and s, ms, min or h."),
                     new("cost",       "--cost",       OptKind.Str,     "lsq or minimax."),
                     new("analyses",   "--analyses",   OptKind.Str,     "goals (only the analyses a goal names) or all."),
                     new("parallel",   "--parallel",   OptKind.Integer, "How many evaluations may run at once."),
                     new("seed",       "--seed",       OptKind.Integer, "Random seed, for a repeatable run."),
                     new("vars",       "--vars",       OptKind.StrList, "Optimize only these of the file's opt=1 keys. A complex value is named by its parts: mag(ZL), phase(ZL)."),
                     new("goals",      "--goals",      OptKind.StrList, "Count only these of the file's enabled goals."),
                     new("corners",    "--corners",    OptKind.Str,     "all, none, or corner names separated by commas: meet every goal at the nominal and at each corner at once "
                                                                     + "(one evaluation per corner per point). result.optimize.goals names each goal's binding corner."),
                     new("snap",       "--snap",       OptKind.Flag,    "Snap integer, stepped and preferred values at the end and polish the rest."),
                     new("sensitivity","--sensitivity",OptKind.Flag,    "Report each variable's sensitivity at the best point (n more evaluations)."),
                     new("showIterations","--show-iterations",OptKind.Flag, "Also return each iteration's state (result.optimize.perIteration) and send a progress notification per iteration. Default: the final result only."),
                     new("output",     "-o",           OptKind.Path,    "A .npy for the best point's full results plus the opt history group."),
                     new("history",    "--history",    OptKind.Path,    "A .npy for the opt history group alone."),
                     new("savePreset", "--save-preset",OptKind.Str,     "A .csch only: add the best values as a preset of this name."),
                     // brief-sdl-7: the optimize line's em= and emBudget=, and Apply (R-sdl7-8).
                     new("emMode",     "--em",         OptKind.Str,     "off, correct or direct: how analyses simulated by EM are optimized. correct (the default when a goal "
                                                                     + "reads one) searches on the circuit models and corrects them with a few EM solves; direct makes every "
                                                                     + "evaluation an EM solve (pattern, simplex, trust_region or bayes); off uses circuit models alone."),
                     new("emBudget",   "--em-budget",  OptKind.Integer, "The most EM solves the run may make (default 12 for correct, 30 for direct); cached geometries are free. "
                                                                     + "It stops there and keeps its best EM-verified point."),
                     new("apply",      "--apply",      OptKind.Flag,    "A .csch run by EM only: write the best values into the schematic's VAR block, update its layout from the "
                                                                     + "schematic, and check the saved layout is the geometry EM verified (result.optimize.em.applied).")],
                    "Optimization. These arguments override the file's optimize line for this run only; nothing is "
                  + "written to the design's values unless apply or savePreset is given. Exit 0: every goal met. Exit 3: it ran and a goal is unmet — "
                  + "result.optimize.goals says which, by how much and where. Exit 2: no evaluation converged."),
                // brief-yield-5 R-ya5-5: the `yield` verb's two run nouns, so the document is the verb's `--json`
                // object and nothing here reshapes it. `trial` re-runs one trial in place of the run.
                new("montecarlo", ["yield", "mc"],
                    [new("path", true, "The .csch or .cnl whose tune tolerances, goals and statistics line to run.")],
                    [Set, Result, .. YieldOptions],
                    "Monte Carlo: the spread alone, scoring every enabled goal. Exit 0 unless it could not run; exit 2: "
                  + "no trial evaluated. A progress notification per batch."),
                new("yield", ["yield", "estimate"],
                    [new("path", true, "The .csch or .cnl whose tune tolerances, use=yield|both goals and statistics line to run.")],
                    [Set, Result,
                     new("target",    "--target",    OptKind.Str,  "The yield to meet, as a percent (95%). Exit 3 below it."),
                     new("autostop",  "--autostop",  OptKind.Flag, "Stop once the interval clears target either way (not with lhs)."),
                     .. YieldOptions],
                    "Yield against the yield specs, with its Clopper-Pearson interval. Exit 3: below target — "
                  + "result.yield.goals says which goal and its worst trial. A progress notification per batch."),
                // brief-yield-6 R-ya6-6: `yield corners`, the same verb's fourth noun.
                new("corners", ["yield", "corners"],
                    [new("path", true, "The .csch or .cnl whose corner lines, goals and statistics line to run.")],
                    [Set, Result,
                     new("corners",  "--corners",  OptKind.StrList, "Only these enabled corners."),
                     new("mc",       "--mc",       OptKind.Flag,    "A Monte Carlo (a yield, with a yield goal) at each corner instead of one evaluation; result.corners.yields."),
                     new("generate", "--generate", OptKind.Str,     "Return the corners a cross product makes instead of running, e.g. \"axis=tt,ss;temp=-40,25,85;Vdd=3.0,3.6\" "
                                                                  + "(a kit axis by its name; temp; any variable or tunable key). Writes nothing."),
                     new("write",    "--write",    OptKind.Flag,    "With generate, a .csch only: append the generated corners after a history checkpoint."),
                     new("output",   "-o",         OptKind.Path,    "The result .npy; default <design>.corners.npy (with mc, <design>.yield.npy).")],
                    "Corner analysis: the design at every enabled corner (kit corner selections, temp, values; a statistical "
                  + "corner replays its trial). Exit 0: every goal met at every corner. Exit 3: a goal fails at a corner — "
                  + "result.corners.worst names the corner per goal. Exit 2: no corner evaluated."),
                // brief-yield-11 R-ya11-8: `yield center`, the same verb's fifth noun. A progress notification per
                // iteration (iteration, best yield, simulations).
                new("center", ["yield", "center"],
                    [new("path", true, "The .csch or .cnl whose opt=1 ranges, tolerances, use=yield|both goals and center line to run.")],
                    [Set, Result,
                     new("algorithm", "--algorithm", OptKind.Str,     "The search: " + string.Join(", ", CircuitRF.Core.Design.OptimizerAlgorithms.ForNoisyObjective) + " (default cmaes)."),
                     new("trials",    "--trials",    OptKind.Integer, "M, the common trials every candidate is scored on (default 200)."),
                     new("verify",    "--verify",    OptKind.Integer, "Fresh trials the start and the best point are each verified on (default 1000)."),
                     MaxIter,
                     new("maxEvals",  "--max-evals", OptKind.Integer, "Simulation limit, verification not counted."),
                     new("time",      "--time",      OptKind.Str,     "Wall-clock limit: seconds, or a number and s, ms, min or h."),
                     new("width",     "--width",     OptKind.Number,  "The smooth yield's logistic width, a fraction of each goal's scale (default 0.05)."),
                     new("surrogate", "--surrogate", OptKind.Str,     "none (default) or quadratic: score each candidate on a quadratic fit of its yield-goal margins and 10,000 virtual trials — fewer simulations with few statistical variables and smooth margins; the reported yield is still the simulated verification."),
                     new("parallel",  "--parallel",  OptKind.Integer, "Simulations evaluated at once."),
                     new("seed",      "--seed",      OptKind.Integer, "The common trials' seed; the verification uses the next."),
                     new("target",    "--target",    OptKind.Str,     "The yield the verified best point must reach, as a percent (95%). Exit 3 below it."),
                     new("confidence","--confidence",OptKind.Str,     "The confidence of the yield intervals, as a percent (95%)."),
                     new("nonconverged","--nonconverged",OptKind.Str, "fail (a trial that does not evaluate counts as a fail) or warn (left out)."),
                     new("vars",      "--vars",      OptKind.StrList, "Draw only these statistical entries; the rest stay at nominal."),
                     new("goals",     "--goals",     OptKind.StrList, "Score only these of the enabled goals."),
                     new("savePreset","--save-preset",OptKind.Str,    "A .csch only: add the centred nominals as a preset of this name. The only write to the design."),
                     new("output",    "-o",          OptKind.Path,    "The best point's verification .npy; default <design>.yield.npy beside the design.")],
                    "Design centering (yield optimization): every candidate is scored on the same common trials; the start and the "
                  + "best point are then verified on fresh trials, each with its interval. Exit 0: done (and the verified yield met "
                  + "target). Exit 3: the verified yield is below target. Exit 2: no candidate evaluated. A progress notification "
                  + "per iteration."),
                // brief-yield-14 R-ya14-8: `yield doe`, the same verb's sixth noun. A progress notification per batch of runs.
                new("doe", ["yield", "doe"],
                    [new("path", true, "The .csch or .cnl whose opt=1 ranges (or stat=1 tolerances), goals, measures and doe line to run.")],
                    [Set, Result,
                     new("design",     "--design",     OptKind.Str,     "full2 (default; at most 10 factors), frac (fractional factorial), pb (Plackett-Burman: screen many factors in 12, 20 or 24 runs) or ccf (face-centred central composite: a quadratic model)."),
                     new("resolution", "--resolution", OptKind.Integer, "frac: 4 (default) or 5."),
                     new("factors",    "--factors",    OptKind.Str,     "opt (default: the opt=1 entries over their ranges) or stat (the stat=1 entries at nominal +- k sigma)."),
                     new("levels",     "--levels",     OptKind.Str,     "range (opt factors) or sigma:k (stat factors; default sigma:1)."),
                     new("centre",     "--centre",     OptKind.Integer, "Centre points added (default 1) — the curvature check."),
                     new("responses",  "--responses",  OptKind.Str,     "goals (default: the goals the factors are for) or all (every enabled goal). Every scalar measure is a response."),
                     new("optimum",    "--optimum",    OptKind.Flag,    "factors=opt: search the fitted model for the goals' best point inside the ranges, then confirm it by one simulation — result.doe.optimum gives predicted vs simulated per goal."),
                     new("parallel",   "--parallel",   OptKind.Integer, "Simulations evaluated at once."),
                     new("goals",      "--goals",      OptKind.StrList, "Analyse only these of the enabled goals."),
                     new("output",     "-o",           OptKind.Path,    "The result .npy; default <design>.doe.npy beside the design.")],
                    "Design of experiments: which factors matter and how they interact. Each response's effects on coded -1/+1 "
                  + "factors, active when beyond Lenth's margin; a fractional or Plackett-Burman design lists what every effect is "
                  + "confounded with (result.doe.responses[].effects[].aliases). Screen with design=pb first, then design=ccf on the "
                  + "few active factors. Exit 0 unless it could not run; exit 2: no run evaluated. Writes nothing to the design."),
                // brief-sens-1 R-sens1-7: `yield sens`, the same verb's seventh noun. A progress notification per batch.
                new("sensitivity", ["yield", "sens"],
                    [new("path", true, "The .csch or .cnl whose tunables, tolerances, goals, measures and sensitivity line to run.")],
                    [Set, EmOptions[0],
                     new("factors",   "--factors",   OptKind.Str,     "all (default: every tunable — at +-k sigma when it has a tolerance, at +-step otherwise), stat (the stat=1 entries) or opt (the opt=1 entries, at +-step of their value)."),
                     new("step",      "--step",      OptKind.Str,     "The +- step of a value with no tolerance, as a percent of its value (default 1%). A phase steps +-1 deg."),
                     new("sigma",     "--sigma",     OptKind.Number,  "k: a toleranced value steps +-k sigma through its own distribution (default 1)."),
                     new("ports",     "--ports",     OptKind.Str,     "1 or 0 (default): with factors=all, also step the ports' own values (their reference impedances)."),
                     new("snp",       "--snp",       OptKind.Str,     "on (default) or off: off leaves every SnP block's S-parameter entries out."),
                     new("snpExclude","--snp-exclude",OptKind.StrList, "SnP instances to leave out (SNP4, DUT:SNP4), replacing the sensitivity line's list; result.sensitivity.excluded names them."),
                     new("vars",      "--vars",      OptKind.StrList, "Step only these of the factors."),
                     new("goals",     "--goals",     OptKind.StrList, "Rank exactly these enabled goals, whatever their use."),
                     new("responses", "--responses", OptKind.Str,     "goals (default: the use=yield|both goals, else the use=opt|both ones) or all (every enabled goal). Every scalar measure is a response."),
                     new("parallel",  "--parallel",  OptKind.Integer, "Simulations evaluated at once."),
                     new("output",    "-o",          OptKind.Path,    "The result .npy; default <design>.sens.npy beside the design.")],
                    "Sensitivity: which values move each goal most at the nominal design, and by how much per tolerance or per 1 %. "
                  + "Each factor is stepped + and - alone (2k+1 simulations, deterministic); result.sensitivity.goals[] ranks the "
                  + "contributors by |delta| of the goal's normalized margin, with each one's basis (sigma, 1 %, 1 deg), share and "
                  + "status (nonlinear: the two steps disagree — confirm with doe design=ccf or a Monte Carlo). An estimated pass "
                  + "fraction is given only when every factor steps by its tolerance. Each S-parameter of an SnP block is a factor "
                  + "too (SNP3.SdB_3_1, SNP3.Sdeg_3_1, stepped +-0.1 dB / +-1 deg); each goal also carries entries[] (an "
                  + "S-parameter's dB and phase together), ports[] (overlapping) and instances[] (with factor counts). Exit 0 "
                  + "done; exit 2: the nominal did not evaluate. Writes nothing to the design."),
                new("em", ["em"],
                    [new("path", true, "The .cem or .c3d to run; with component, the workspace folder (or .cws, "
                                     + "or .ctech) whose technology the part is drawn on.")],
                    [Output, .. Narrowing,
                     // brief-em3d-42 R-em3d42-6 — every shipped eigenmode example embeds a Driven setup
                     // beside its eigenmode one, so without this a .c3d run over MCP could only be refused.
                     new("setup", "--setup", OptKind.Str,
                         "Which of a .c3d's embedded setups to run, by name. Needed when it embeds several; "
                       + "explain lists them."),
                     // brief-emb-1 R-emb1-8 — `em --component-ports`; brief-lcp-2 R-lcp2-4 — a .cem's as well as a .c3d's.
                     new("componentPorts", "--component-ports", OptKind.StrList,
                         "Component ports for this run only, on a .c3d or a .cem: reference designators of parts placed on its layout, "
                       + "or [\"*\"] for every two-terminal part. Each gets a gap port across its own pads, built and verified before "
                       + "meshing and never written to the file: a lumped port on a 3D setup (a .c3d, a .cem naming one, or a 3D .cem on a "
                       + "layout, built in a 3D view of it made in memory), a delta gap across a strip joining the two pads on a planar "
                       + ".cem. A thermal setup and an electrostatic one are refused. A finished planar or driven 3D run also writes <setup>_cosim.cnl, the board's N-port with "
                       + "each part across its port. explain componentPorts=true shows what would be built."),
                     // brief-sar-12 R-sar12-3 — a .c3d's band sweep: one setup per band from its template, run in turn.
                     new("bands", "--bands", OptKind.Str,
                         "Run a .c3d's band sweep, by name: one setup per band made from its template (each at that SAR "
                       + "frequency), then the table of psSAR1g and psSAR10g at the rated power beside the reference values."),
                     new("force", "--force", OptKind.Flag,
                         "Start a run whose memory estimate is past this machine's, which is otherwise refused: a "
                       + "Palace run past 150 % of it, a planar run past it (or past memoryLimitGB) — the panel's "
                       + "Simulate Anyway."),
                     // A thermal setup driven from a circuit: one of the CIRCUIT's globals (brief-em3d-79).
                     Set,
                     new("workspace", "--workspace", OptKind.Path,
                         "The workspace this run is on behalf of. For em, the .cws paths resolve against "
                       + "(default: the nearest one above the .cem). For a loose .csch kept outside any "
                       + "workspace folder, where a cell reference that names nothing is looked for."),
                     new("solver", "--solver", OptKind.Str,
                         "A 3D setup's solver for this run only: " + CircuitRF.Design.Em3d.Em3dSolvers.CliWordList + ". Default: the .cem's Solver3D."),
                     // brief-emi-6 R-emi6-5 — a comparison's reference, as `em --reference`.
                     new("reference", "--reference", OptKind.Str,
                         "With a solver list or compare: the solver every other one is compared against. Default: Palace when "
                       + "it is compared, else the first."),
                     // brief-em3d-136 R-em3d136-4d — `em --device`, through the CLI's own parsing and refusals.
                     new("device", "--device", OptKind.Str,
                         "Where Palace and EMerge solve: auto (default), cpu or gpu. For Palace, auto is the CPU; gpu needs a "
                       + "Palace built with GPU support and runs one process per GPU it finds, and one that cannot is refused "
                       + "before meshing ('solver' with capability=gpu asks first). For EMerge, auto uses its GPU sparse solver "
                       + "for a driven problem large enough to be faster on it and solves a sweep again on the CPU if the GPU's "
                       + "does not converge; eigenmode runs always solve on the CPU. 'solver' says whether EMerge's GPU solving "
                       + "is enabled."),
                     // brief-emi-10 R-emi10-4 — `em --memory-limit`, through the CLI's own parsing and refusals.
                     new("memoryLimitGB", "--memory-limit", OptKind.Number,
                         "A run's memory ceiling, GB, below this machine's memory. A 3D run's solver and mesher processes "
                       + "are held to it: one reaching it is stopped and the run refused, writing nothing. A planar run's size "
                       + "is checked against it before anything is filled. Default: this machine's memory; a run started "
                       + "with force past its size check keeps none."),
                     new("cores", "--cores", OptKind.Integer,
                         "How many Palace processes or solver threads this run may use: Settings ▸ Solvers ▸ Cores for this run only. "
                       + "Default: one per physical core; 'solver' says how many this machine has, and more than that is refused. Each "
                       + "Palace process holds its own share of the mesh and its own solver data, so more processes take more memory and "
                       + "fewer take less, more slowly. dryRun's memory estimate does not follow the count: when a run stops at its memory "
                       + "limit, lower this before coarsening the mesh."),
                     new("component", "--component", OptKind.Str,
                         "Extract one built-in component's drawn part instead of a .cem: its type and parameters as on a "
                       + ".cnl instance line, e.g. \"SPIRAL N=3 W=10 um S=8 um Din=100 um\". Port n is terminal n. "
                       + "Needs output."),
                     new("freq", "--freq", OptKind.Str,
                         "With component: the sweep, start:stop:step, e.g. 1GHz:20GHz:1GHz. Default 1-20 GHz."),
                     // EMB-5 R-emb5-2 — `em --dry-run`, through the CLI's own parsing: check's and explain's reports of
                     // the same file in one document, and their numbers restated in em.dry-run.estimate.
                     new("dryRun", "--dry-run", OptKind.Flag,
                         "Estimate instead of running: the kernel, unknowns, memory and factor time per point and in "
                       + "total (planar), or each 3D solver's size (Palace's element, unknown and memory estimate), the "
                       + "verdict against this machine's memory, and every refusal check would raise — the numbers "
                       + "check and explain report, in result.check, result.explain and the em.dry-run.estimate "
                       + "diagnostic. Solves nothing and writes nothing; a planar setup is meshed as check meshes it.")],
                    "Electromagnetic extraction of the layout the .cem names, a .c3d's embedded setup, or one "
                  + "component's drawn part. Writes a Touchstone and a .npy; a 3D setup run with several solvers "
                  + "writes each one's and a comparison .npy; a static, eigenmode or thermal setup writes the "
                  + ".npy only. result.scratch says where the run made its temporary files (the Scratch folder Settings ▸ "
                  + "Solvers names, or the system temp folder) and whether that folder exists, refused run or not."),
            ],
            Adapter: [AsyncCall]),

        new("check",
            "Validate a workspace, a cell folder or one document. Runs no analysis and writes nothing.",
            null, null,
            [
                new("", [ "check" ],
                    [new("path", true, "A workspace, a cell folder, or one .csch .csym .clay .ctech .cem .cnl .wasm.")],
                    [
                        new("recursive", "--recursive", OptKind.Flag, "Descend a plain folder. A workspace always descends."),
                        new("severity",  "--severity",  OptKind.Str,
                            "What decides the exit code: warning or error. Default error; warnings are reported either way."),
                        Summary,
                        HostWorkspace,
                    ],
                    ""),
            ],
            Adapter: [AsyncCall]),

        new("explain",
            "Report what circuitRF resolved a document to, and the walk it took. Runs no analysis and writes nothing.",
            null, null,
            [
                new("", [ "explain" ],
                    [new("path", true, "The document to explain.")],
                    [
                        Set,
                        HostWorkspace,
                        new("expr",     "--expr",     OptKind.Str, "Evaluate an expression in the design's resolved scope."),
                        // StrOptional, not Str: `explain` reads the name as an OPTIONAL token after
                        // the flag, so an empty string handed through as a value is a name — and
                        // `--analysis ""` is refused with "No analysis named ''". The empty string
                        // has to become the bare flag for the description below to be true.
                        new("analysis", "--analysis", OptKind.StrOptional,
                            "Report the analysis chains. A name asks about that one; an empty string asks about all of them."),
                        new("ref",      "--ref",      OptKind.Str, "What a relative cell reference resolves to from this document."),

                        // RND-3's three, and the two arguments that ANSWER their refusals. The
                        // brief asks for three; `--all` and `--view` are here because R-aut-13 says
                        // nothing reachable from the command line may be unreachable here, and
                        // without them a client that asks `--extents` of a cell folder holding two
                        // views is handed a refusal naming a flag it cannot pass. See
                        // src/Cli/RESOLVED.md, RND-5.
                        new("cells",   "--cells",   OptKind.Flag,
                            "List the cells and, for each, which file each view resolves to and in what state."),
                        new("layers",  "--layers",  OptKind.Flag,
                            "List the layers of the resolved technology, with how many shapes the document draws on each."),
                        new("extents", "--extents", OptKind.Flag,
                            "The document's bounding box, in base SI with its unit and scale — what render --fit frames on. "
                          + "Each box also comes back as a `window` string in the spelling render --window takes, whole "
                          + "document and per layer, so no conversion is needed to frame one."),
                        // brief-ipx-1 R-ipx1-3. R-aut-13 again: it is reachable from the command line, so it is reachable here.
                        new("nets", "--nets", OptKind.Flag,
                            "The nets a layout names, as the Nets panel lists them: name, shape count and layers reached, in "
                          + "natural order. Counts the cell's own shapes; the walk's nets step says what that leaves out."),
                        // brief-footprint-4 R-fp4-4b. R-aut-13 again: it is reachable from the
                        // command line, so it is reachable here.
                        new("footprints", "--footprints", OptKind.Flag,
                            "Per component of a schematic: the footprint it states, what that resolved to, how many pads "
                          + "against how many ports, and — for a built-in case size — the technology the land pattern "
                          + "would be generated against."),
                        new("tunables", "--tunables", OptKind.Flag,
                            "Every value of a schematic or netlist that can be tuned or optimized, at any depth, with its key "
                          + "(R1.R, Wline, DUT:R3.R), value, default range and whether Push can write it; and the keys the "
                          + "tuning setup names that resolve to nothing. An SnP block's offset rows are folded to its one line in "
                          + "snp unless snpEntries names it."),
                        new("snpEntries", "--snp-entries", OptKind.StrList,
                            "With tunables: the SnP instances (SNP3, DUT:SNP3) whose S-parameter offset rows to list, or all. "
                          + "Default: none — each block is its one summary line."),
                        // brief-sar-2 R-sar2-7 — R-aut-13: reachable from the command line, so reachable here.
                        new("materials", "--materials", OptKind.Flag,
                            "A 3D setup's (.c3d's or .cem's) materials as its solver receives them; a frequency-dependent "
                          + "one (a tissue, a phantom liquid) with the frequency it was evaluated at, the sweep's centre."),
                        new("all",     "--all",     OptKind.Flag,
                            "Only with cells: include generated cells, which are hidden by default. On its own it is refused."),
                        new("view",    "--view",    OptKind.Str,
                            "Which view of a cell folder: schematic, symbol or layout. Spelled as render spells it."),
                        new("setup",   "--setup",   OptKind.Str,
                            "Which of a 3D view's (.c3d's) embedded setups to explain, by name. Needed when it embeds several."),
                        // brief-emb-1 R-emb1-8 — `explain --component-ports`; brief-lcp-2 R-lcp2-4 — a .cem's as well.
                        new("componentPorts", "--component-ports", OptKind.Flag,
                            "An EM setup's component ports (a .c3d's or a .cem's), built as a run would build them and verified, "
                          + "without meshing: per port its refdes, pads, conductors, gap, width and verdict, with the landing "
                          + "rectangles of a 3D one; a planar .cem's line says it is a delta gap across a strip."),
                        new("device",  "--device",  OptKind.Str,
                            "A 3D setup's report as a run with this device would see it: auto (default), cpu or gpu."),
                        new("memoryLimitGB", "--memory-limit", OptKind.Number,
                            "A 3D setup's report with the memory ceiling a run given this limit (GB) would keep."),
                        new("cores", "--cores", OptKind.Integer,
                            "A 3D setup's report as a run given this many cores would see it: result.explain.em3d.device.processes and "
                          + "physicalCores, and a warning when it is more than this machine has, which a run refuses."),
                    ],
                    ""),
            ]),

        new("create",
            "Create a correct initial document — a workspace, a cell folder with its view files, a 3D view of a cell's layout, "
          + "or a model in a model library exported from a cell or a Model Composer model (Export to Library).",
            "what",
            "What to create.",
            [
                new("workspace", ["new", "workspace"],
                    [new("path", true, "The workspace directory, or its parent when name is given.")],
                    [
                        new("name", "--name", OptKind.Str,  "The workspace name."),
                        new("tech", "--tech", OptKind.Str,  "A technology id, or none. Default: the one the New Workspace dialog pre-selects."),
                        new("lengthUnit", "--length-unit", OptKind.Str, "um, mm or mil: the copied technology's display unit. Default: the technology's own."),
                        new("vendorLibrary", "--vendor-library", OptKind.StrRepeat, "<name>=<folder>: a vendor part library to reference from the start. None by default."),
                    ],
                    "A workspace, with a technology copied in."),
                new("cell", ["new", "cell"],
                    [
                        new("workspace", true,  "The workspace the cell is created in."),
                        new("name",      false, "The cell name."),
                    ],
                    [new("views", "--views", OptKind.StrList, "Which views to create: schematic, symbol, layout, 3d (an empty .c3d whose units come from the cell's layout, else the technology; a 3D view of an existing layout places it as an instance with View Layout), or none (the cell folder alone). Default schematic.")],
                    "A cell folder and one empty-but-valid file per view."),
                // brief-emb-1 R-emb1-1 — Design ▸ New 3D View from Layout.
                new("view3d", ["new", "view3d"],
                    [new("cell", true, "The cell folder whose layout the 3D view places.")],
                    [
                        new("fromLayout", "--from-layout", OptKind.Flag, "Place the cell's own layout as an instance with View Layout, its 3D setups copied in as embedded setups, each Planar .cem with component ports made a 3D setup carrying that list (copied once: the two are separate afterwards), and its port labels as ports; a layout made from the cell's schematic also brings one VAR per cell parameter and schematic VAR, linked by name. Required."),
                        new("name", "--name", OptKind.Str, "The .c3d's name. Default: the cell's."),
                    ],
                    "A .c3d of the cell's layout, ready for a 3D run. Component ports need no 3D view: a .cem on the layout, planar or "
                  + "3D, names them too."),
                // brief vendorlib-10 R-vl10-4 — Export to Library. A mode of `create` rather than an eighteenth tool: what
                // it makes is a new document (a model folder), and the surface is a standing cost (R-aut5-4).
                new("libraryPart", ["export", "part"],
                    // brief vendorlib-17 R-vl17-3 — a .cmc or .cmodel publishes the model through the cell Model Composer's
                    // own button writes for it; the name stays `cell` so existing calls keep working.
                    [new("cell", true, "What to export: a cell folder (a saved cell whose primary schematic has at least one Pin), or a "
                                     + "Model Composer model — a .cmc, which publishes the .cmodel its Export step wrote beside it, or a "
                                     + ".cmodel itself. A model is placed as one ComposedFET with pins g, d, s (and t when it self-heats) "
                                     + "and parameters N and Temp; a passive .cmc publishes the cell its Export step wrote. Refused, "
                                     + "naming the step to run, when the .cmc's model is not exported or the document changed since.")],
                    [
                        new("library",            "--library",              OptKind.Path,       "The model library folder. An empty or new folder starts a new library."),
                        new("category",           "--category",             OptKind.Str,        "Category folders under the library, a/b; created as needed."),
                        new("part",               "--part",                 OptKind.Str,        "The part number. Default: the cell's name, or the .cmodel's file name."),
                        new("description",        "--description",          OptKind.Str,        "The part's description."),
                        new("kind",               "--kind",                 OptKind.Str,        "active or passive. Default: active when any component is nonlinear."),
                        new("value",              "--value",                OptKind.Str,        "A passive's value (10 pF); makes it findable by value."),
                        new("freqLow",            "--freq-low",             OptKind.Str,        "The low end of the range the model is valid over (0 Hz). Default for a .cmc: its measured data's lowest frequency."),
                        new("freqHigh",           "--freq-high",            OptKind.Str,        "The high end of that range (20 GHz). Default for a .cmc: its measured data's highest frequency."),
                        new("conditions",         "--conditions",           OptKind.Str,        "Free text: bias, temperature."),
                        new("footprint",          "--footprint",            OptKind.Str,        "The footprint's case pair."),
                        new("doc",                "--doc",                  OptKind.PathRepeat, "A document copied into the model's docs/."),
                        new("symbol",             "--symbol",               OptKind.Str,        "own, fet, r, c, l or box. Default: own when the cell has a symbol, else the glyph its pins and value imply, else box."),
                        new("replace",            "--replace",              OptKind.Flag,       "Replace a part already exported under this number; its revision goes up by one."),
                        new("includeVendorParts", "--include-vendor-parts", OptKind.Flag,       "Copy vendor parts the cell places into the model, with their libraries' terms."),
                        new("libraryName",        "--library-name",         OptKind.Str,        "A new library's name. Default: its folder's."),
                        new("publisher",          "--publisher",            OptKind.Str,        "A new library's publisher."),
                        new("license",            "--license",              OptKind.Str,        "A new library's license."),
                        new("libraryVersion",     "--library-version",      OptKind.Str,        "A new library's version. Default 1.0.0."),
                        // brief vendorlib-11 R-vl11-5 — the plan technology-bound parts are sampled over.
                        new("sweep",              "--sweep",                OptKind.StrRepeat,
                            "One segment of the frequency plan the parts that take their substrate from the technology are sampled "
                          + "over, in the sparam directive's keys: 'start=1 stop=20 npts=101 unit=GHz', or step= for npts=, with log "
                          + "optional; unit defaults to Hz, as in the directive. One item per segment. Default: 0 to 20 GHz, 1001 points. "
                          + "With protect=encapsulate it is also the plan the S-parameter block is sampled over: make it reach the highest "
                          + "harmonic a run will use. Refused when no part uses the technology and nothing is encapsulated."),
                        // brief vendorlib-15 R-vl15-1d — compiled models.
                        new("compileModels",      "--compile-models",       OptKind.Flag,
                            "Compile each Verilog-A source the cell places, and each ComposedFET (converted to Verilog-A first, then "
                          + "proved against the cell at checkBias points), and ship the build in models/<platform>/ instead of the "
                          + "source: a protected model. Off unless true. Refused when the cell places neither or no Verilog-A "
                          + "compiler is found. A placed .osdi with no source ships as this platform's build either way."),
                        new("builds",             "--build",                OptKind.PathMap,
                            "Builds of the compiled model made on OTHER platforms, runtime identifier to file: "
                          + "{\"win-x64\": \"builds/TSTFET100.osdi\"}. Each is copied into models/<rid>/, not run; this platform's "
                          + "build is the one the export runs. Identifiers: win-x64, win-arm64, win-x86, osx-arm64, osx-x64, "
                          + "linux-x64, linux-arm64. With several compiled models, a build is matched to one by its file name."),
                        // brief vendorlib-19 — the way the model is protected.
                        new("protect",            "--protect",              OptKind.Str,
                            "Protect the model: 'encapsulate' or 'compile'. encapsulate (Encapsulated netlist) samples EVERY linear "
                          + "element of the flattened cell — R, L, C, mutuals, transmission lines, technology parts and the SnPs it "
                          + "places — into ONE N-port S-parameter block over the sweep plan (0 Hz always included, as the DC "
                          + "solution); devices, sources and probes stay outside as instances. Ports: the pins the block touches, "
                          + "then each net where it meets a device. A cell parameter that reaches the block is frozen at its default "
                          + "and dropped from the part (a spread or corner on one is refused); one reaching only devices stays. No "
                          + "compiler needed. Proved against the cell (DC currents and Y at three sampled frequencies) or nothing is "
                          + "written. compile is compileNetlist. Off when absent."),
                        // brief vendorlib-16 — Compile netlist.
                        new("compileNetlist",     "--compile-netlist",      OptKind.Flag,
                            "Compile the whole cell into ONE protected Verilog-A module and publish the compiled build in place of "
                          + "the cell: R, C, L, Mutual, Short and ComposedFET go inside; SnPs, transmission lines, technology parts and "
                          + "external models stay outside in a wrapper netlist, each joining net an extra terminal. The generated source "
                          + "is written outside the library (sourceOut). Every platform the compiler can reach is tried; the export is "
                          + "proved against the cell (DC currents and Y at three frequencies, 1e-6 relative) or nothing is written. Off "
                          + "unless true; needs a Verilog-A compiler. The same as protect=compile."),
                        new("sourceOut",          "--source-out",           OptKind.Path,
                            "With compileNetlist: where the generated .va goes. Default protected-source/<Part>/<Part>.va beside the "
                          + "workspace. Refused inside the library."),
                        new("checkBias",          "--check-bias",           OptKind.StrRepeat,
                            "With protect, compileNetlist, or compileModels converting a ComposedFET: one bias point of the proof per item, 'g=-0.5 d=5' (pins not named are at 0 V). "
                          + "Default: every pin at 0 V, then each pair of pins at +1 V and -1 V across them."),
                        // brief vendorlib-12 R-vl12-4 — after a model's .csch was edited by writing it.
                        new("refresh",            "--refresh",              OptKind.Flag,
                            "cell is a library MODEL folder whose schematic was edited in place: copy in what it newly references, "
                          + "convert newly added technology parts, extract its .cnl again and update part.cpart's Pins and Parameters. "
                          + "PublishedHash and Revision are left as they are. Takes no other option."),
                    ],
                    "A self-contained model folder: the cell, its sub-cells and every file it uses copied in, its netlist, part.cpart, "
                  + "checked and self-checked against the source before it appears. Prints every path created."),
            ]),

        // `import` and `convert` are two tools because they are two verbs. They were one tool with a
        // `what` selector, which put every EXPORT — a .clay to GDSII, Gerber, DXF, STEP — under a tool
        // named and described as bringing artwork IN, where an agent looking for an export does not
        // look. Both are single-mode now, like `render`.
        new("import",
            "Bring something in: a component as a cell (a footprint and its symbol); a process design kit, referenced from the "
          + "workspace with its parts placeable; a library model copied in as an editable cell; a schematic design (.kicad_sch, EDIF "
          + "2.0.0, or a binary .DSN of version 2.0 or 3.2) as a new cell with its own schematic, reporting the connectivity check against the source (exit 1 when "
          + "it is not matched, the cell still written); a segmented voxel model (NIfTI, raw, a "
          + "DiscMaterial .h5, or a tetrahedral .node/.ele voxelised) or per-tissue closed surfaces (STL, OBJ, PLY) into a .c3d. "
          + "To convert artwork between formats, including exporting a layout or importing a whole board (.kicad_pcb, Gerber, IPC-2581), use convert.",
            "what",
            "What to import.",
            [
                new("part", ["import", "part"],
                    [new("path", true, "The component file or folder.")],
                    [
                        new("into",       "--into",        OptKind.Path, "The workspace the cell is created in."),
                        new("cell",       "--cell",        OptKind.Str,  "Which part, when the source holds several."),
                        new("variant",    "--variant",     OptKind.Str,  "Which variant, when the part has several."),
                        new("listParts",  "--list-parts",  OptKind.Flag, "Report what the source holds and create nothing."),
                        new("tech",       "--tech",        OptKind.Path, "The .ctech the layers reconcile against."),
                        new("addLayers",  "--add-layers",  OptKind.Flag, "Write the part's new layers into that technology."),
                        Summary,
                    ],
                    "A footprint and its symbol, as a cell."),
                // brief vendorlib-12 R-vl12-4 — Copy to Workspace. Re-pointing placed instances at the copy has no
                // spelling here: a caller rewrites the reference in the .csch.
                new("model", ["import", "model"],
                    [new("path", true, "The library model folder (the one holding part.cpart).")],
                    [
                        new("into", "--into", OptKind.Path, "The workspace (or a folder in one) the cell is created in."),
                    ],
                    "A library model copied into the workspace as an ordinary, editable cell named after its part number "
                  + "(made unique): models/, cells/ and docs/ come with it, every file reference is rewritten workspace-relative, "
                  + "part.cpart and the .cnl are left behind, and the .ccell records CopiedFrom. Placed instances are not "
                  + "re-pointed; rewrite their CellRef to point one at the copy."),
                // brief-gdf-1 R-gdf1-4c — File ▸ Import PDK. --allow-code is deliberately NOT offered: a client
                // granting itself consent to run a kit's code is what the consent exists to stop. The
                // operator's `serve --trust-kit` and a decision recorded at the keyboard still apply.
                new("pdk", ["import", "pdk"],
                    [new("path", true, "The kit's folder, or a .zip of one (unpacked into the workspace's kits/ folder first).")],
                    [
                        new("into", "--into", OptKind.Path, "The workspace whose .cws references the kit."),
                        new("name", "--name", OptKind.Str,  "The name its parts are referenced by. Default: the kit folder's name."),
                    ],
                    "A process design kit, read in place and referenced from the workspace — never copied. Prints the "
                  + "import's tally, how many parts reach the Library palette with a symbol, the corners it declares and "
                  + "every finding. Its layout generators are declared, and started only with consent."),
                // brief vendorlib-1 R-vl1-5c — a manufacturer's part library, referenced in place.
                new("library", ["import", "library"],
                    [new("path", true, "The library's root folder of plain-text SPICE files.")],
                    [
                        new("into", "--into", OptKind.Path, "The workspace whose .cws references the library."),
                        new("name", "--name", OptKind.Str,  "The name parts reach it by (vendorlib://<name>/…). Default: the folder's name."),
                        Result,
                    ],
                    "A vendor part library, referenced from the workspace and indexed — never copied. "
                  + "result.library has the part count and folder groups; encrypted files are counted, and a folder with "
                  + "no readable part is refused."),
                // brief-schimp-5 R-si5-6 — the dialog's fields as arguments; every default is the dialog's.
                new("schematic", ["import", "schematic"],
                    [new("path", true, "The schematic design file: .kicad_sch, EDIF 2.0.0 (.edf, .edn, .edif) or a binary .DSN. Classified by content.")],
                    [
                        new("into",        "--into",        OptKind.Path,      "The workspace or folder the cell is created in."),
                        new("cell",        "--cell",        OptKind.Str,       "The new cell's name. Default: the file's stem."),
                        new("port",        "--port",        OptKind.StrRepeat, "A connector's reference or a net's name to make a circuitRF Port, in port order. "
                                                                             + "None by default; with any, the default S-parameter analysis is added."),
                        new("symbolCell",  "--symbol-cell", OptKind.StrRepeat, "A reference to import as the source's own symbol (a cell with no model) "
                                                                             + "instead of a native R, L or C."),
                        new("codepage",    "--codepage",    OptKind.Integer,   "The ANSI code page an EDIF or binary .DSN file's text is read in. Default 1252."),
                        new("list",        "--list",        OptKind.Flag,      "Report the pages, what each part becomes and what may be a port; write nothing."),
                        Summary,
                    ],
                    "A new cell with its own schematic, and the connectivity check against the source."),
                new("voxels", ["import", "voxels"],
                    [new("path", true, "The label volume: .nii, .nii.gz, .h5 (DiscMaterial), a raw file, or a .node/.ele mesh.")],
                    [
                        new("into",      "--into",       OptKind.Path,    "The .c3d the body goes into; created when absent."),
                        new("labels",    "--labels",     OptKind.Path,    "A label list CSV: label,name[,material]."),
                        new("material",  "--material",   OptKind.StrMap,  "Per label: its material, or air. {\"4\": \"Muscle\"}."),
                        new("name",      "--name",       OptKind.Str,     "The object's name. Default: the file's stem."),
                        new("dims",      "--dims",       OptKind.Str,     "A raw volume's voxels: nx,ny,nz."),
                        new("type",      "--type",       OptKind.Str,     "A raw volume's integer type: uint8, int8, uint16, int16, uint32, int32."),
                        new("voxel",     "--voxel",      OptKind.Str,     "A raw volume's voxel size, or a tetrahedral mesh's, with its unit: 2mm or 1mm,1mm,2mm."),
                        new("bigEndian", "--big-endian", OptKind.Flag,    "A raw volume is big-endian."),
                        new("header",    "--header",     OptKind.Integer, "Bytes to skip at the start of a raw volume."),
                        new("unit",      "--unit",       OptKind.Str,     "A tetrahedral mesh's length unit. Default mm."),
                        new("crop",      "--crop",       OptKind.Str,     "Keep voxels i0,j0,k0,i1,j1,k1 (upper ends exclusive)."),
                        new("gridStep",  "--grid-step",  OptKind.Str,     "The FDTD cell inside the body, with its unit. Default: a line on every voxel boundary."),
                        new("report",    "--report",     OptKind.Flag,    "Print the size report and the label table; write nothing."),
                    ],
                    "A segmented voxel model, as one VoxelBody whose labels map to materials."),
                new("surfaces", ["import", "surfaces"],
                    [new("path", true, "A closed surface file (STL, OBJ, PLY), or a folder of them, one per tissue.")],
                    [
                        new("into",      "--into",       OptKind.Path,    "The .c3d the surfaces go into; created when absent."),
                        new("unit",      "--unit",       OptKind.Str,     "The files' length unit. Default mm."),
                        new("material",  "--material",   OptKind.StrMap,  "Per file (name or stem): its material."),
                        new("group",     "--group",      OptKind.Str,     "The group the surfaces are gathered in."),
                        new("report",    "--report",     OptKind.Flag,    "Check and order the surfaces; write nothing."),
                    ],
                    "Per-tissue closed surfaces, as Mesh objects nested outermost first."),
            ]),

        new("convert",
            "Convert artwork from one format to another, in either direction: export a layout (.clay) to "
          + "GDSII, OASIS, Gerber, DXF, a .kicad_pcb board, STEP or glTF, or import the interchange formats, and IPC-2581, which is import only, to .clay. Formats come from the paths; "
          + "from and to override. OASIS, and GDSII with engine gdstk, go through the gdstk worker. "
          + "A STEP product of several solids imports as one object per solid, gathered in a group named after the file. "
          + "A picture of a layout (png, jpeg, bmp, gif, webp) is traced onto layers at a stated scale, then written as any "
          + "target; result.convert says how it was read. To correct a reading, pin the colours with layerMap.",
            null, null,
            [
                new("", ["convert"],
                    [new("path", true, "The file or folder to convert. A folder is a Gerber file set; a picture is traced.")],
                    [
                        Output,
                        new("from",       "--from",        OptKind.Str,  "The source format when the path does not say: clay, gdsii, oasis, dxf, gerber, board, ipc2581, image."),
                        new("to",         "--to",          OptKind.Str,  "The target format when the path does not say."),
                        new("cell",       "--cell",        OptKind.Str,  "Which cell, when the source holds several."),
                        new("name",       "--name",        OptKind.Str,  "What to call the written file set (gerber)."),
                        new("listCells",  "--list-cells",  OptKind.Flag, "Report what the input holds and write nothing."),
                        new("step",       "--step",        OptKind.Str,  "An IPC-2581 source: the step to import. A file holding several is refused until one is named."),
                        new("listSteps",  "--list-steps",  OptKind.Flag, "An IPC-2581 source: every step with its layer and net counts, writing nothing."),
                        new("tech",       "--tech",        OptKind.PathOrName,
                            "The technology to convert against: a .ctech path, or for a picture also a technology id (the default "
                          + "is a new workspace's)."),
                        new("intoTech",   "--into-tech",   OptKind.Path,
                            "Gerber, board or IPC-2581 source, clay target: import into this .ctech, reference it, and write none. Refused with --tech, and when its copper count or order disagrees with the set's."),
                        new("keepCells",  "--keep-cells",  OptKind.Path, "Keep the cells the import produced, here."),
                        new("workspace",  "--workspace",   OptKind.Path,
                            "The .cws the layers graft onto. Without one a .ctech of its own is written."),
                        new("dbu",        "--dbu",         OptKind.Integer, "Database units per micron. Default 1000."),
                        new("engine",     "--engine",      OptKind.Str,
                            "native (default) or gdstk: which GDSII reader or writer a gdsii source or target goes through."),
                        new("oasCompression", "--oas-compression", OptKind.Integer, "An oasis target's compression level, 0 to 9. Default 6."),
                        new("oasValidation",  "--oas-validation",  OptKind.Str,  "An oasis target's validation: none, crc32 (default) or checksum32."),
                        new("oasStandardProperties", "--oas-standard-properties", OptKind.Flag, "Write OASIS's standard properties."),
                        new("dxfVersion", "--dxf-version", OptKind.Str,  "AC1015, AC1018 or AC1032."),
                        new("dxfUnits",   "--dxf-units",   OptKind.Integer, "The DXF units code."),
                        new("joinTolerance", "--join-tolerance", OptKind.Str,
                            "With layerMap: how far apart two open ends may close, a length with its unit. Default 1um."),
                        new("drillUnits", "--drill-units", OptKind.Str,  "mm or inch."),
                        new("drillFormat","--drill-format",OptKind.Str,  "Excellon coordinate format, as int:dec."),
                        new("drillZeros", "--drill-zeros", OptKind.Str,  "leading or trailing suppression."),
                        new("acceptInferredDrillFormat", "--accept-inferred-drill-format", OptKind.Flag,
                            "Proceed on a guessed Excellon format. Unstated, it is refused: the two readings differ by four orders of magnitude."),
                        new("arcTolerance", "--arc-tolerance", OptKind.Str,
                            "A gerber source's arc flattening, a length with its unit from 1nm to 100um. Default 1um."),
                        .. PictureTraceOptions,
                        new("region",     "--region",      OptKind.Str,
                            "A picture: trace only x0,y0,x1,y1, each in pixels with px. A gltf target: the region to export."),
                        new("crop",       "--crop",        OptKind.Str,
                            "An import (gerber, gdsii, oasis, dxf, board, ipc2581): Crop to Rectangle on what was imported, x0,y0,x1,y1, " +
                            "every coordinate with its unit (124mm,114.5mm,129.3mm,119.5mm). A .clay source is refused."),
                        PictureUnderlay,
                        PictureOverlayPath,
                        Summary,
                    ],
                    "One import and one export, between any two interchange formats. A clay target is a "
                  + "DIRECTORY of cell folders plus a technology, not a file; a file-shaped path there is refused."),
            ],
            Adapter: [PictureOverlay]),

        // RND-5's one new tool, and it is one (R-rnd0-4 / R-rnd5-1): the document kind comes from the
        // path exactly as `check`'s does, so there is no `render-schematic` and no selector. Making
        // the view type a selector would advertise three modes where there is one verb.
        //
        // `output` is a Path like every other, and nothing about the confinement changes because this
        // is the first tool whose PURPOSE is to write a file: it writes only where the caller named,
        // and there is still no tool that deletes (cli.md 11.5).
        new("render",
            "Draw a schematic, a symbol, a layout, a data display, or a 3D EM setup's model as a picture: "
          + ".svg, .pdf or .png. The document kind comes from the path. Ask explain --extents and --layers "
          + "first if you need a window or a layer name. A .c3d's field plot is drawn with field; listFields "
          + "says which plots it holds and whether their data is there.",
            null, null,
            [
                new("", [ "render" ],
                    [new("path", true,
                         "The .csch .csym .clay .cdd, 3D .cem or .c3d, a cell folder, or a workspace with cell.")],
                    [
                        HostWorkspace,
                        new("output", "-o", OptKind.Path,
                            "Required, except with listFields. Where the picture is written; its extension picks the format: "
                          + ".svg, .pdf or .png."),
                        new("format", "--format", OptKind.Str,
                            "Override the format the extension implies: svg, pdf or png."),
                        new("view", "--view", OptKind.Str,
                            "Which view of a cell folder: schematic, symbol or layout. More than one is refused, not ordered."),
                        new("cell", "--cell", OptKind.Str,
                            "Which cell, when the path is a workspace."),

                        // The three viewport modes. They are REFUSED together rather than ordered
                        // (R-rnd2-3), so all three are advertised and the verb decides.
                        new("fit", "--fit", OptKind.Flag,
                            "Frame the whole document. The default."),
                        new("window", "--window", OptKind.Str,
                            "An explicit world rectangle, x0,y0,x1,y1. ON A LAYOUT EVERY COORDINATE CARRIES AN SI UNIT, "
                          + "zero included (0um,0um,500um,300um): a bare number is refused, because DBU, um and mm are "
                          + "three plausible pictures. A schematic or a symbol takes bare design units."),
                        new("center", "--center", OptKind.Str,
                            "The centre of the window, x,y. Same unit rule. Needs span."),
                        new("span", "--span", OptKind.Str,
                            "The width of the window; the height follows the output aspect. Same unit rule."),
                        new("margin", "--margin", OptKind.Number,
                            "Fraction of the page left around a fit. 0 to 0.45."),

                        new("size", "--size", OptKind.Str,
                            "Output size as WxH. Device pixels for png, points for svg and pdf. Default 1600x1200."),
                        new("scale", "--scale", OptKind.Number,
                            "png only: raster multiplier. Refused on a vector format, and refused together with dpi."),
                        new("dpi", "--dpi", OptKind.Number,
                            "png only: the same multiplier spelled relative to 96 dpi."),

                        new("layers", "--layers", OptKind.StrList,
                            "Layout only: draw only these layers. An undefined name is refused — ask explain --layers."),
                        new("hideLayers", "--hide-layers", OptKind.StrList,
                            "Layout only: draw everything but these. Refused together with layers."),
                        new("fitLayers", "--fit-layers", OptKind.StrList,
                            "Layout only: frame the fit on these layers and draw everything. A fit already frames only "
                          + "what is DRAWN, so hiding a layer removes it from the framing too; this narrows the framing "
                          + "without narrowing the picture — an imported board's drill map sits far outside the board "
                          + "and shrinks it to a fraction of the frame. Refused with window or center/span, which state "
                          + "the frame outright."),
                        new("layerColors", "--layer-colors", OptKind.StrMap,
                            "Layout only: how a layer DRAWS, for this render only — {\"Top Copper\": \"#e04030\"}. "
                          + "Nothing is written to the technology. #rgb, #rrggbb or #rrggbbaa; the eight-digit form "
                          + "sets the layer's fill opacity, which is the alpha the renderer paints through. Use it when "
                          + "an import gave several layers near-identical colours and an overlay is unreadable. "
                          + "The layer report in the result carries each layer's colour as drawn."),
                        new("detail", "--detail", OptKind.Str,
                            "full (default: every level-of-detail tier off, what is stored is what is drawn), screen "
                          + "(the tiers as a canvas engages them), or a pixel budget. A vector file is several times "
                          + "larger at full."),

                        new("theme", "--theme", OptKind.PathOrName,
                            "A colour theme name, or a .ccolor file. Default: the workspace's own, else the shipped one."),
                        new("variant", "--variant", OptKind.Str, "light or dark. Default light."),
                        new("background", "--background", OptKind.Str, "opaque or transparent. Default opaque."),
                        new("grid", "--grid", OptKind.Flag, "Draw the grid. Off by default, as every export is."),
                        new("noRulers", "--no-rulers", OptKind.Flag,
                            "Leave the rulers out. They are on by default because a ruler is in the document."),
                        new("outlineText", "--outline-text", OptKind.Flag,
                            "svg only: write the text as glyph outlines instead of live text, so a viewer or importer "
                          + "without IBM Plex Sans draws the letters circuitRF drew rather than substituting a font. "
                          + "The text is no longer editable as text. Refused on png and pdf."),

                        // The .cdd half (RND-4). Options of the one verb, not a second tool: a data
                        // display is a document kind like the other three.
                        new("data", "--data", OptKind.PathRepeat,
                            ".cdd only: a result file to bind. The first binds the document's own selected source. "
                          + "A .cdd whose sources do not resolve is a refusal, not an empty plot."),
                        new("tab", "--tab", OptKind.Str, ".cdd only: which tab, by name or by number."),
                        new("plot", "--plot", OptKind.Integer, ".cdd only: which plot on that tab, from 1."),
                        new("allTabs", "--all-tabs", OptKind.Flag,
                            ".cdd only: every tab, one page each. pdf only."),

                        // brief-em3d-5: a 3D setup takes exactly one of these two, refused together.
                        new("section", "--section", OptKind.Str,
                            "3D .cem or .c3d only: a section through the model — z=<length> (the XY plane at that height), "
                          + "xz@y=<length> or yz@x=<length>. EVERY LENGTH CARRIES AN SI UNIT (z=35um); a bare number "
                          + "is refused. A solid is drawn where its bottom <= plane < its top, so z at the top of "
                          + "the copper shows the layer above it."),
                        new("iso", "--iso", OptKind.Flag,
                            "3D .cem or .c3d only: an isometric outline — silhouettes and sharp edges, with no hidden-line "
                          + "removal. Refused together with section."),
                        // brief-em3d-88: the 3D view's own chrome.
                        new("axes", "--axes", OptKind.Flag,
                            "3D .cem or .c3d only: the 3D view's axis indicator in the picture's bottom-left corner."),
                        new("scaleBar", "--scale-bar", OptKind.Flag,
                            "3D .cem or .c3d section only: the 3D view's scale bar in the bottom-right corner, in the view's "
                          + "display unit. Refused with iso, which has no one scale."),

                        // brief-em3d-84: a .c3d's field plot, drawn from its own run.
                        new("field", "--field", OptKind.Str,
                            ".c3d only: draw this field plot (by exact name) over the section it cuts. A clip-plane plot IS "
                          + "a section, so leave section out; a different one is refused. The plot's run, solution and "
                          + "quantity must exist: a missing one is refused with the reason, never replaced by the nearest. "
                          + "A temperature plot paints each bond wire from its own T(s) and ranges the true minimum to maximum. "
                          + "Surfaces and faces plots are not drawn headlessly yet. A hidden plot draws the same."),
                        new("listFields", "--list-fields", OptKind.Flag,
                            ".c3d only: list its field plots — setup, solution, quantity, where, and whether the data is "
                          + "there (the reason when not). Draws nothing and takes no output."),
                        new("phase", "--phase", OptKind.Number,
                            "With field: the instant of the cycle to draw, degrees (default 0). Only for a plot reading a "
                          + "quantity instantaneously; refused otherwise."),
                        new("mip", "--mip", OptKind.Str,
                            "With field: x, y or z. A SAR plot drawn as its maximum-intensity projection along that axis: each "
                          + "point shows the largest SAR on the line through it, on a plane through the middle of the tissue. "
                          + "Refused for any quantity but SAR; a plot whose own Mip is on is drawn so without it."),
                        new("noLegend", "--no-legend", OptKind.Flag, "With field: leave the field's legend off."),
                        new("noThin", "--no-thin", OptKind.Flag,
                            "With field: draw every slice triangle in an svg or pdf, however many. By default a vector "
                          + "slice above 50,000 triangles merges neighbours of one colour step, and the result says how many "
                          + "it drew them as."),
                        new("labels", "--labels", OptKind.Flag, "With field: write each solid's material inside it, where it fits."),
                        new("tight", "--tight", OptKind.Flag,
                            "With field: crop the page to the section (the width kept, the height following it), the legend "
                          + "inset in its corner, no caption, and no margin unless margin says otherwise."),
                        new("setup", "--setup", OptKind.Str,
                            ".c3d only: which of its embedded setups the picture is drawn through (its air box and problem), "
                          + "by name. Needed when it embeds several."),
                    ],
                    ""),
            ],
            // R-rnd5-4, and the whole point of this tool for an agent: a client that receives only a
            // path has to be able to READ that path, and many cannot. Default OFF because an image is
            // expensive in a way a JSON document is not.
            Adapter:
            [
                // R-aut10-5: what the cap DOES was stated only in the result, so a caller that asked
                // for bytes and got a path had to work out why from the note it may not have read.
                new(AttachImageArgument, "", OptKind.Flag,
                    "Return the rendered file itself in the result, as well as writing it. Off by default. "
                  + "The file is written either way, and its path is always in outputs. If the written file "
                  + "exceeds " + (AttachmentCapBytes / (1024 * 1024)) + " MB the result carries the PATH and "
                  + "no image, plus a warning naming the size and what to narrow (--detail screen, a window, "
                  + "fewer layers, or png instead of svg) — the call still succeeds and is not retried for you."),
            ]),

        // R-aut11-1, and the tool that unblocks the netlist half of this surface entirely: without it
        // nothing here could simulate a design anyone had actually DRAWN. It is also the reference
        // answer a client checks its own authoring against — AUT-7 §3's false defect report came
        // from an instance line one look at a known-good extraction would have settled.
        // A BOARD is the same sentence about a different document
        // (brief-authored-board-3-companion-writers.md R-ab3-2), so it is this tool and not a fourth
        // one — and R-aut-13 puts it here for the reason stated on `explain --footprints`: it is
        // reachable from the command line, so it is reachable here. Without these three a caller
        // handed a .clay gets only the refusal that names flags this tool does not offer.
        new("netlist",
            "Extract the .cnl netlist a schematic simulates as — the same extraction Simulate "
          + "performs, byte for byte. Without output the netlist comes back as text. Every run "
          + "analysis also takes a .csch directly and extracts in memory; this is for reading what "
          + "it will run, and for checking your own authoring against a known-good one. Given a "
          + ".clay it PROJECTS the board instead: a board netlist, a placement table and a bill of "
          + "materials, from one reading of the artwork, so the three cannot disagree. With "
          + "toSchematic it runs the other way: a .cnl you wrote becomes a readable drawn .csch.",
            null, null,
            [
                new("", [ "netlist" ],
                    [new("path", true,
                        "A .csch, a cell folder, or a workspace with cell — or a .clay, with at least "
                      + "one of ipc/placement/bom — or a .cnl, with toSchematic.")],
                    [
                        HostWorkspace,
                        new("output", "-o", OptKind.Path,
                            "Where the .cnl is written. Without it the text comes back in the result."),
                        new("cell", "--cell", OptKind.Str, "Which cell, when the path is a workspace."),
                        new("toSchematic", "--to-schematic", OptKind.Flag,
                            "The other direction: path is a .cnl, and the result is a DRAWN schematic (.csch) — "
                          + "the signal path left to right with the ports at its ends, shunt elements dropped "
                          + "below, a ground symbol on every terminal on net 0, orthogonal wires, and every net "
                          + "labelled with its netlist name. It extracts back to the same instances, nets and "
                          + "values. Refused, with every cause named, for a netlist that defines cells or uses a "
                          + "type with no schematic symbol. output, when given, must end in .csch."),
                        // Named individually because two of the three are .csv and an extension
                        // cannot say which — R-ab3-2b, which is why `-o` on a board is a refusal.
                        new("ipc", "--ipc", OptKind.Path,
                            "Board only. Where the board netlist (IPC-D-356) is written — one record per "
                            + "pad and per via that carries a net."),
                        new("placement", "--placement", OptKind.Path,
                            "Board only. Where the placement table is written — a centroid, a rotation "
                            + "and a side per reference designator."),
                        new("bom", "--bom", OptKind.Path,
                            "Board only. Where the bill of materials is written — the value, footprint "
                            + "and type each placement resolves to. More than one of the three in a run "
                            + "is encouraged: they come from one projection."),
                    ],
                    ""),
            ]),

        // R-aut11-2. Not a second plotting path: it builds the same document `render` consumes and
        // hands it to the same composer, and `writeCdd` gives that document back so a caller can
        // edit it and carry on with `render`.
        new("plot",
            "Draw one picture from one result file, with no data display to author first: .svg, "
          + ".pdf or .png. Give a trace per curve. Ask read first for the cube names and their axes. An antenna pattern is a polar "
          + "plot with radial=db and a cut= on its trace; without radial=db its decibels fall outside the disc and it draws no curve.",
            null, null,
            [
                new("", [ "plot" ],
                    [new("path", true, "The result to plot: a .npy, a Touchstone .sNp or a binary simulation dataset .ds.")],
                    [
                        new("output", "-o", OptKind.Path,
                            "Required. Where the picture is written; its extension picks the format: .svg, .pdf or .png."),
                        new("format", "--format", OptKind.Str,
                            "Override the format the extension implies: svg, pdf or png."),
                        new("trace", "--trace", OptKind.StrRepeat,
                            "One curve, as comma-separated key=value: cube=S,i=2,j=1,y=db20. cube is the trace "
                          + "card's own shorthand, so a bracketed slice (cube=S[:,1,0]) and a transform "
                          + "(cube=mag(Pout)) work too; i and j pin the cube's i/j axes by PORT NUMBER and are "
                          + "refused alongside a bracketed slice. y is db20 (S, V, I), db10 (power), mag, phase, real or "
                          + "imag; a bare db is refused as ambiguous. "
                          + "axis is left (default) or right. label=\"As built\" names the trace: it replaces the "
                          + "generated name in the legend, the axis title and the marker box. A cube already in dB (PSD, VSD) takes y=real. "
                          + "A labelled axis pins by name: cube=PSD[\"Iout\",:]. An antenna pattern: cube=farfield.U (F1.farfield.U from a "
                          + "fields analysis), cut=<phi deg> for the plane at that bearing or cut=all for every phi, port=<n> for a port "
                          + "of a per-port far field, freq=2.45G for one frequency; decibels unless y says. ref=<dBm> reads a dBm level "
                          + "against another input power. At least one is required — a plot with no trace is refused, not drawn empty."),
                        new("type", "--type", OptKind.Str,
                            "rect (default), smith, polar, table or surface (a 3D antenna pattern: trace cut=all)."),
                        new("freqUnit", "--freq-unit", OptKind.Str,
                            "Hz, kHz, MHz or GHz. Default GHz. It is also the unit x is read in."),
                        new("radial", "--radial", OptKind.Str,
                            "A polar plot's radius: linear (default) or db, which makes it an antenna pattern plot. Refused on any other type."),
                        new("dbFloor", "--db-floor", OptKind.Number, "A db plot's centre, in dB relative to the outer ring. Default -40."),
                        new("dbRing", "--db-ring", OptKind.Number, "A db plot's ring spacing, dB. Default 10."),
                        new("dbRef", "--db-ref", OptKind.Str,
                            "A db plot's outer ring: peak (the data's own, normalised; the default) or a level in dB."),
                        new("dbUnit", "--db-unit", OptKind.Str, "What a db plot's radial numbers are in: dBi, dB(W/sr)."),
                        new("wholePlane", "--whole-plane", OptKind.Flag,
                            "Draw each cut= as one trace across -theta max to +theta max instead of two. Needs type=polar radial=db."),
                        new("angleLabels", "--angle-labels", OptKind.Flag,
                            "A polar plot's bearing every 30 degrees outside the disc, with a spoke to each."),
                        new("legend", "--legend", OptKind.Str, "Where the legend goes: off, inside, below or floating."),
                        new("view", "--view", OptKind.Str, "A surface plot's camera: iso, broadside, phi0 or phi90."),
                        new("rotate", "--rotate", OptKind.Str, "A surface plot's camera turned further, as azimuth,elevation in degrees."),
                        new("zoom", "--zoom", OptKind.Number, "A surface plot's camera zoom. Default 1."),
                        new("colorMap", "--color-map", OptKind.Str, "A surface plot's colour map, by name; a name it does not know is refused with the list."),
                        new("specLines", "--spec-lines", OptKind.Flag, "Draw the limits of the goals the result records. The default."),
                        new("noSpecLines", "--no-spec-lines", OptKind.Flag, "Leave out the limits of the goals the result records."),
                        new("title",   "--title",   OptKind.Str, "Plot title."),
                        new("xlabel",  "--xlabel",  OptKind.Str, "X axis label."),
                        new("ylabel",  "--ylabel",  OptKind.Str, "Left Y axis label."),
                        new("y2label", "--y2label", OptKind.Str, "Right Y axis label."),
                        new("x",  "--x",  OptKind.Str,
                            "X window as lo:hi, in the plot's own units. Omit for autoscale. Refused on smith/polar."),
                        new("y",  "--y",  OptKind.Str, "Left Y window as lo:hi. Omit for autoscale."),
                        new("y2", "--y2", OptKind.Str, "Right Y window as lo:hi. Omit for autoscale."),
                        new("size", "--size", OptKind.Str,
                            "Page size as WxH. Device pixels for png, points for svg and pdf. Default 792x612."),
                        new("scale", "--scale", OptKind.Number,
                            "png only: raster multiplier. Refused on a vector format, and refused together with dpi."),
                        new("dpi", "--dpi", OptKind.Number,
                            "png only: the same multiplier spelled relative to 96 dpi."),
                        new("variant", "--variant", OptKind.Str, "light or dark. Default light."),
                        new("background", "--background", OptKind.Str, "opaque or transparent."),
                        new("outlineText", "--outline-text", OptKind.Flag,
                            "svg only: the text as glyph outlines, as render spells it. Refused on png and pdf."),
                        new("writeCdd", "--write-cdd", OptKind.Path,
                            "Also write the data display this drew, so you can edit it and go on with render."),
                    ],
                    ""),
            ],
            Adapter:
            [
                new(AttachImageArgument, "", OptKind.Flag,
                    "Return the rendered file itself in the result, as well as writing it. Off by default. "
                  + "The file is written either way, and its path is always in outputs. If the written file "
                  + "exceeds " + (AttachmentCapBytes / (1024 * 1024)) + " MB the result carries the PATH and "
                  + "no image, plus a warning naming the size and what to narrow — the call still succeeds "
                  + "and is not retried for you."),
            ]),

        // R-aut11-3: what EXISTS. Every document below is one this server already knew how to read;
        // what was missing was any way to ask what is there, which sent a client outside the surface
        // to search the filesystem.
        new("find",
            "List what is under a directory: the workspaces, their cells, each cell's views and the "
          + "analyses it declares. The walk is bounded — the result says how deep it went and whether "
          + "it stopped short. With parts, search a workspace's vendor libraries instead: each match "
          + "carries the SpiceModel instance parameters to write (File, Name, Glyph, and Footprint on a board when the part states its case size). Reads no result and writes nothing.",
            null, null,
            [
                new("", [ "find" ],
                    [new("path", true, "The directory to look under. A workspace itself is fine.")],
                    [
                        new("depth", "--depth", OptKind.Integer,
                            "How many levels below the root a workspace is looked for. Default 4, at most 12."),
                        new("noAnalyses", "--no-analyses", OptKind.Flag,
                            "Skip the analyses, which cost one extraction per cell."),
                        new("parts", "--parts", OptKind.Str,
                            "Search the workspace's vendor-library parts: every word must match the part name, "
                          + "description, file or folder, or read as a value (10p, 4n7, 2.2nH) equal within 0.5 %."),
                        new("library", "--library", OptKind.Str, "With parts: search only this library."),
                        new("limit", "--limit", OptKind.Integer, "With parts: at most this many matches. Default 50."),
                    ],
                    ""),
            ]),

        // R-lvs11-5a/5b: `lvs` falls out of the verb with no second implementation, which is the
        // whole point of the automation architecture. It matters more here than almost anywhere
        // else in this table, because an agent that wrote a `.clay` CANNOT LOOK AT THE SCREEN —
        // asking whether the artwork it just authored implements the drawing is the only way it
        // can find out, and the answer comes back as typed findings it can act on.
        new("lvs",
            "Compare a cell's layout against its schematic: does the artwork implement the drawing? "
          + "Reports every divergence as a typed lvs. finding with the objects it is about. Runs no "
          + "analysis and writes nothing unless output is given. A two-pin part placed end for end "
          + "comes back in each cell's uncapped 'turned' list with both lands: turn it by moving the "
          + "instance to Land1 + Land2 - (x, y) and adding 180 degrees to its rotation.",
            null, null,
            [
                new("", [ "lvs" ],
                    [new("path", true, "A cell folder, a workspace, a .clay or a .csch. A workspace "
                       + "compares every cell holding both views; one holding a single view is "
                       + "reported and skipped.")],
                    [
                        new("output", "-o", OptKind.Path,
                            "Write the human report here. Nothing is written without it."),
                        new("flat", "--flat", OptKind.Flag,
                            "Read every placed cell as a leaf: its copper joins one partition and "
                          + "nothing is descended into."),
                        new("flattenCell", "--flatten-cell", OptKind.StrRepeat,
                            "Flatten this one cell, by cell folder name. Repeatable."),
                        new("testbench", "--testbench", OptKind.Flag,
                            "Compare the fixture too — ports, terminations, sources and tuners. By "
                          + "default the unit of comparison is the cell, which is what a layout draws."),
                        new("noReduce", "--no-reduce", OptKind.Flag,
                            "Do not collapse series, parallel or jumper groups on either side."),
                        new("recognize", "--recognize", OptKind.Flag,
                            "Also read devices out of COPPER, by the technology's DeviceRules deck. "
                          + "Off by default and for a design circuitRF authored: a placed instance "
                          + "already says what a part is, and the instance always wins. It is for "
                          + "artwork carrying no instances at all. A technology stating no deck "
                          + "recognises nothing, which is not an error. The shipped mmic-GaAs "
                          + "technology carries one ('reference technologies' lists its rules); a "
                          + "recognised part is matched by name when its body shapes carry Component."),
                        Set,
                        new("severity", "--severity", OptKind.Str,
                            "What decides the exit code: warning or error. Default error; warnings "
                          + "are reported either way."),
                        Summary,
                    ],
                    ""),
            ]),

        // brief-artsch-7 R-as7-7: Create Schematic from Artwork, falling out of the `recognize` verb with no second
        // implementation. The same read-only default, the same refusals, the same result document.
        new("recognize",
            "Read a board's artwork (a .clay) as a circuit: ground, vias, ports, parts and lines become native "
          + "components (MLIN, CPWG, SLIN, TLIN, VIA, parts as R/L/C or an SnP; unknown values as tunable variables) "
          + "with an S-parameter analysis. Read-only unless into or output is given: the result is the report and "
          + "the parts table. Review parts (partsOut, edit the CSV, pass it back as parts) before writing. The "
          + "result is a schematic (into) or a .cnl (output) to simulate with run analysis=sparam. A picture is read too: "
          + "a layout picture is traced at a stated scale and then read as artwork; a schematic picture's wires, symbols "
          + "and words become the circuit. To correct a picture's reading: edit the parts CSV and pass it back (parts), "
          + "flip a crossing (connect/disconnect at its x,y from result.recognize.crossings), or write the .cnl yourself — "
          + "the format is the contract. A PLOT picture (a rectangular S-parameter plot, a Smith or polar chart) is "
          + "digitised: several pictures (path plus pictures) are one session, and output=<x.s2p> writes the Touchstone "
          + "their traces describe; result.recognize.plots says what every trace was read as, on what evidence, and "
          + "where each matrix entry came from. Look at the returned overlay, then answer an unassigned trace with "
          + "trace, a chart trace with no frequency with freq, an unread axis with xAxis/yAxis.",
            null, null,
            [
                new("", [ "recognize" ],
                    [new("path", true, "A .clay, a cell folder holding a layout view, a workspace (with cell), or a picture (the first, for plots).")],
                    [
                        new("cell", "--cell", OptKind.Str, "With a workspace path: the cell whose layout to read."),
                        new("output", "-o", OptKind.Path,
                            "Write the circuit as this .cnl — or, for plot pictures, the Touchstone (.s1p … .s4p, the port "
                          + "count the traces make; the source pictures are kept beside it). Nothing else."),
                        new("into", "--into", OptKind.Str,
                            "Write the schematic: new:<name> (a new cell beside the artwork's) or artwork (the "
                          + "artwork's own cell, only when it has no schematic view)."),
                        new("replace", "--replace", OptKind.Flag,
                            "Replace a schematic this command wrote earlier (a history checkpoint is taken first). "
                          + "A hand-drawn schematic is never replaced."),
                        new("partsOut", "--parts-out", OptKind.Path, "Write the parts table as CSV."),
                        new("parts", "--parts", OptKind.Path,
                            "An edited parts table (CSV): its Kind, Value, Variable, Model and ModelFile override "
                          + "what was read; a value clears the variable."),
                        new("bom", "--bom", OptKind.Path, "A bill of materials: part values and descriptions."),
                        new("placement", "--placement", OptKind.Path, "A placement file: where each part sits."),
                        new("placementOrigin", "--placement-origin", OptKind.Str,
                            "symbol, body or pin1 — required when the placement file does not declare it."),
                        new("placementUnit", "--placement-unit", OptKind.Str, "mm, mil or in."),
                        new("region", "--region", OptKind.Str,
                            "Read only this rectangle: x0,y0,x1,y1, every coordinate with a unit (um, mm, mil); on a "
                          + "picture, in pixels with px. A line it cuts becomes a port."),
                        new("ground", "--ground", OptKind.Str, "The net to read as ground."),
                        new("groundAt", "--ground-at", OptKind.Str, "x,y with units: the copper there is ground."),
                        new("vias", "--vias", OptKind.Str, "model (default: VIAGND components) or ground (plain GND)."),
                        new("coplanar", "--coplanar", OptKind.Str,
                            "auto (default), microstrip or gcpw: how a line with side ground close by is read."),
                        new("coplanarFactor", "--coplanar-factor", OptKind.Number,
                            "Under auto: GCPW when both gaps are at most this many substrate heights. Default 3."),
                        new("start", "--start", OptKind.Str, "Sweep start with its unit, e.g. 100MHz."),
                        new("stop", "--stop", OptKind.Str, "Sweep stop with its unit, e.g. 6GHz."),
                        new("npts", "--npts", OptKind.Integer, "Sweep points. Default 201."),
                        new("digits", "--digits", OptKind.Integer,
                            "Significant digits every value in the circuit is written with, 1 to 15. Default 6."),
                        new("freeOrientation", "--free-orientation", OptKind.Flag,
                            "Draw every shunt part below its line, not on the side of the line its copper is on."),
                        new("imageKind", "--image-kind", OptKind.Str,
                            "A picture: auto (default), schematic, layout or plot. A picture read as no drawing is refused, naming this."),
                        new("tech", "--tech", OptKind.PathOrName,
                            "A picture: the technology, a .ctech path or a technology id. Default: the picture's workspace's."),
                        .. PictureTraceOptions,
                        PictureUnderlay,
                        new("crossings", "--crossings", OptKind.Str,
                            "A schematic picture: never, dot (default) or always — when two crossing wires join."),
                        new("arrange", "--arrange", OptKind.Str,
                            "A layout: board (default: each part roughly where it sits on the board) or tidy (the main line and its "
                          + "hangers). A schematic picture: drawn (default: parts where the picture draws them) or tidy."),
                        new("connect", "--connect", OptKind.StrRepeat,
                            "A schematic picture: join the wires crossing at x,y (source pixels), or x,y:x2,y2 to join two ends."),
                        new("disconnect", "--disconnect", OptKind.StrRepeat,
                            "A schematic picture: separate the wires crossing at x,y (source pixels)."),
                        new("threshold", "--threshold", OptKind.Number,
                            "A schematic picture: the ink threshold (Sauvola's k), between 0 and 1."),
                        new("profile", "--profile", OptKind.Str,
                            "A schematic picture: auto (default: read a few ways, keep the most plausible) or one reading alone — measured, enlarged, crossings or bold."),
                        new("pen", "--pen", OptKind.Number,
                            "A schematic picture: the width its lines are drawn with, source pixels; measured when omitted."),
                        new("textHeight", "--text-height", OptKind.Number,
                            "A schematic picture: the height of its capital letters, source pixels; measured when omitted."),
                        new("symbolScale", "--symbol-scale", OptKind.Number,
                            "A schematic picture: the length of a capacitor's plate or a ground's bar, source pixels; measured when omitted."),
                        PictureOverlayPath,
                        .. PlotOptions,
                    ],
                    ""),
            ],
            Adapter: [PictureOverlay]),

        // Trace Impedance Analysis — the `impedance` verb with no second implementation. A reviewer's
        // question an agent is well placed to ask on a board it did not draw: which traces are not
        // the target impedance, and where does the return path under them break.
        new("impedance",
            "Trace Impedance Analysis of a layout: find every trace on the chosen copper layers, cut it "
          + "along its length with a quasi-static cross-section solve, and report per trace Z0 min/max/mean, "
          + "the share inside target +/- tolerance, the line type (microstrip, grounded CPW, stripline…), "
          + "and every finding: Z0 out of band, a return path that breaks or steps to another layer, and (with maxFreq) "
          + "a test pad hanging off a trace that loads the line there (kind pad-shunt, with its C, L, resonance and |S11|). "
          + "Each trace is PASS, WARNING or FAIL, and each finding a warning or a fail. "
          + "Exit 0 when no trace fails (warnings are reported and still exit 0 unless severity is "
          + "warning), 1 when one fails or is unsolved. Writes nothing unless output is given. "
          + "The review saved on the layout in the editor (settings, layers and scope) applies unless an "
          + "argument overrides it, and so do the findings accepted there: each is reported accepted with its "
          + "reason and does not count against its trace or the exit code. To accept one headlessly, add it to "
          + "the .clay's ImpedanceAcceptances (reference topic layout); there is no accept argument. "
          + "LINE CALCULATOR: with tech (and no path) nothing is drawn or read: give layers (one copper layer) and "
          + "width and/or z0, and each row reports the circuit model's answer (an elaborated MLIN, CPWG or SLIN: "
          + "static and dispersive Z0, eeff, loss, guided wavelength) beside the quasi-static cross-section's (what "
          + "this tool reports on a line drawn at that width), with the difference. z0 synthesises a width in each "
          + "column. The model is MLIN, or CPWG when gap is given, or SLIN when the layer has a ground plane on "
          + "both sides; gap on such a layer is a stripline with coplanar ground, which only the cross-section answers.",
            null, null,
            [
                new("", [ "impedance" ],
                    [new("path", true, "A .clay, or a cell folder holding one. Required, except with tech.", Required: false)],
                    [
                        new("target", "--target", OptKind.Number, "The target Z0 in ohms. Default the saved value, else 50."),
                        new("tolerance", "--tol", OptKind.Number,
                            "The tolerance, percent either side of the target. Default 10."),
                        new("warn", "--warn", OptKind.Number,
                            "The warning band, percent either side of the target: outside the tolerance but "
                          + "inside this is a warning, not a fail. Must be wider than the tolerance. Default 20."),
                        new("maxFreq", "--max-freq", OptKind.Str,
                            "The highest frequency the traces carry, WITH its unit (6GHz); a bare number is "
                          + "refused. A stretch outside the warning band shorter than lambda/20 there is a "
                          + "warning, not a fail, and a test pad hanging off a trace is judged here. Default off."),
                        new("severity", "--severity", OptKind.Str,
                            "What decides the exit code: warning or fail. Default fail; warnings are reported either way."),
                        new("layers", "--layers", OptKind.StrList,
                            "The copper layers to analyse, by the technology's layer names. Default the saved "
                          + "layers, else every copper layer. A name that is not a copper layer is refused with the names that are. "
                          + "Line calculator: exactly one, the layer the line is on."),
                        new("maxWidth", "--max-width", OptKind.Number,
                            "The widest copper read as a trace, in um. Default from the stackup."),
                        new("width", "--width", OptKind.StrRepeat,
                            "Line calculator (with tech): widths to analyse, \"70um\" or \"10um,70um\", one row each. "
                          + "Otherwise: review only the traces of these widths on one layer: \"<layer>=<width>[,<width>...]\", "
                          + "a bare width in um or with a unit (18mil). Replaces the saved width classes for that "
                          + "layer for this run; a layer given none is reviewed at every width. A trace's width is "
                          + "the width over most of its length. Out-of-scope traces are not cut, solved or listed, "
                          + "only counted, and still count as grounded neighbours."),
                        new("region", "--region", OptKind.StrRepeat,
                            "Review only the traces any part of whose centre line lies inside this rectangle, "
                          + "\"[<layer>@]x0,y0,x1,y1\", EVERY coordinate with its unit (Bottom@10mm,5mm,30mm,20mm); "
                          + "a bare number is refused. With a layer it selects on that layer only; without, on every "
                          + "layer. A trace crossing the edge is reviewed whole. Replaces the saved regions (saved "
                          + "lasso polygons included) for this run. explain --extents prints coordinates in this spelling."),
                        new("net", "--net", OptKind.StrRepeat,
                            "Review only the traces on copper carrying this net name (from Gerber X2 attributes or "
                          + "a board netlist). Replaces the saved nets for this run."),
                        new("pick", "--pick", OptKind.StrRepeat,
                            "Review the trace whose copper holds this point, \"<layer>@<x>,<y>\" with units; "
                          + "\":connected\" appended takes every trace galvanically joined to it through vias "
                          + "(a series part breaks it). Replaces the saved picks for this run. Regions, nets and "
                          + "picks SELECT (a trace any one chooses is in); widths then FILTER what they chose."),
                        new("wholeLayer", "--whole-layer", OptKind.StrRepeat,
                            "Review every trace on this copper layer while regions, picks or nets narrow the others. "
                          + "With any of those set, a layer none of them reaches is not reviewed at all; this is how "
                          + "to keep a whole layer without drawing round it. Replaces the saved whole layers for this run."),
                        new("noScope", "--no-scope", OptKind.Flag,
                            "Ignore the scope saved on the layout and review every trace."),
                        new("ignoreAccepted", "--ignore-accepted", OptKind.Flag,
                            "Report as if nothing had been accepted: every finding counts, and the exit code with it."),
                        new("survey", "--survey", OptKind.Flag,
                            "List the trace widths per layer — count, total length and one typical Z0 per width "
                          + "class — and analyse nothing. The way to choose width."),
                        new("output", "-o", OptKind.Path,
                            "Write the PDF report here (the one the layout editor exports). Must end in .pdf."),
                        new("tech", "--tech", OptKind.PathOrName,
                            "Line calculator: the technology — a .ctech, a workspace or document inside one (its "
                          + "technology as a schematic there resolves it), or a shipped technology id (reference "
                          + "topic technologies). Selects the calculator; a path beside it is refused."),
                        new("z0", "--z0", OptKind.StrList,
                            "Line calculator: impedances in ohms to synthesise a width for, one row each. Both "
                          + "columns solve for their own width: the model by bisection on its static Z0, the "
                          + "cross-section to one DBU (1 nm)."),
                        new("gap", "--gap", OptKind.Str,
                            "Line calculator: a coplanar line, ground copper on the same layer this far from each "
                          + "edge (25um, 6mil; a bare number is um)."),
                        new("freq", "--freq", OptKind.Str,
                            "Line calculator: the frequency WITH its unit (10GHz) for the dispersive Z0 and eeff, "
                          + "loss per length and guided wavelength. Without it only static values are given."),
                        Summary,
                    ],
                    ""),
            ]),

        // The Layout Review — the `review` verb with no second implementation (brief-review-2
        // R-rev2-11). A reviewer's checklist an agent can run on a board it did not draw, and cannot
        // look at: copper at the board edge, and the checks the series adds.
        new("review",
            "Layout Review of a board: the built-in checks a person reviewing artwork runs by eye, each marking "
          + "the places worth a look with a sentence saying why. Beside DRC, not part of it: a finding is advice "
          + "(a warning), never an error, and a check whose layer role the technology lacks (no board outline, no "
          + "ground reference flagged) is reported not run with the one sentence that fixes it (a note). Layers "
          + "are found by role, never by number, so it runs on shipped, imported and hand-built technologies alike. "
          + "Findings are grouped per check and per layer. The review saved on the layout (checks on or off, "
          + "thresholds) applies unless an argument overrides it, and so do the findings accepted there. To accept "
          + "one headlessly, add it to the .clay's ReviewAcceptances with the finding's key (reference topic layout). "
          + "Exit 0 when it ran, whatever it found; 1 on a refusal, or with severity warning when an unaccepted "
          + "warning remains. Writes nothing unless output is given.",
            null, null,
            [
                new("", [ "review" ],
                    [new("path", true, "A .clay, a cell folder holding one, or a workspace with cell. Required, except with listChecks.",
                         Required: false)],
                    [
                        new("cell", "--cell", OptKind.Str, "With a workspace: the cell whose layout to review."),
                        new("checks", "--checks", OptKind.StrList, "Run only these checks, by id (listChecks lists them)."),
                        new("skip", "--skip", OptKind.StrList, "Do not run these checks, by id."),
                        new("set", "--set", OptKind.StrRepeat,
                            "Override a threshold for this run: \"<check>.<threshold>=<value>\", a length WITH its unit "
                          + "(edge-copper.pullback=0.3mm), an area WITH its unit squared (thermal-vias.thermal-pad-area=2mm2); "
                          + "a bare number for either is refused."),
                        new("defaults", "--defaults", OptKind.Flag, "Ignore the review saved on the layout and use the defaults."),
                        new("ignoreAccepted", "--ignore-accepted", OptKind.Flag,
                            "Report as if nothing had been accepted: every finding counts."),
                        new("as", "--as", OptKind.Str,
                            "Review as pcb (a board) or mmic (a die): which checks run. The layout's saved choice applies "
                          + "otherwise, pcb when it has none. Naming a check the other kind runs is refused."),
                        new("all", "--all", OptKind.Flag,
                            "List every finding in the text report; by default at most 20 per check per layer. The JSON always carries every finding."),
                        new("severity", "--severity", OptKind.Str,
                            "What decides the exit code: warning (an unaccepted warning exits 1) or none (the default)."),
                        new("listChecks", "--list-checks", OptKind.Flag,
                            "List every check with its thresholds, defaults and the layout's saved values, and run nothing."),
                        new("output", "-o", OptKind.Path,
                            "Write the review as a PDF here (the one the Review panel exports): a summary page, then per "
                          + "check with findings a map per layer and one page of findings. Must end in .pdf."),
                        Summary,
                    ],
                    ""),
            ]),

        new("read",
            "Read a file back: a result file as cubes, or one of circuitRF's own documents as its own text.",
            null, null,
            [
                // R-aut10-5. The declared list used to omit `.cdd`, which this verb has always
                // accepted — a schema that under-promises costs a caller exactly what one that
                // over-promises does, because a client believes it either way.
                new("", [ "read" ],
                    [new("path", true, "A .npy, Touchstone (.sNp) or binary simulation dataset (.ds) result, or a .cws .csch .csym "
                       + ".clay .ctech .cem .cnl .cdd document. A cell folder or a workspace is "
                       + "refused — check and explain answer what is IN one; an interchange file is "
                       + "refused and names convert; a .wasm assembly-rule module is compiled, not text.")],
                    [.. Narrowing],
                    ""),
            ]),

        // The Model Composer, headless (brief-modelcomposer-13 R-mc13-6): the `model` verb's nouns as modes, one tool, so
        // the surface grows by one description rather than nine. Every mode is a command line like any other row.
        new("model",
            "Model Composer: build a FET model (.cmodel) or a passive cell from measured or simulated data. "
          + "new -> import -> deembed -> fit (step=all) -> check-charge -> export -> validate. 'reference model-data' is the "
          + "data: what a model needs, what to run in another simulator, every file format with an example. 'reference cmc' and "
          + "'reference cmodel' hold the formats; a .cmodel of your own (equations, a table, an ANN) needs no .cmc, and "
          + "'reference cmodel' is how to write one. An export to a .va output writes Verilog-A source for other simulators, "
          + "from a .cmc or a .cmodel, and refuses table constituents. Exit 3: a check failed (charge conservation, a validation tolerance).",
            "noun",
            "What to do.",
            [
                new("new", ["model", "new"],
                    [new("path", true, "The .cmc to create.")],
                    [
                        new("kind",      "--kind",       OptKind.Str,        "active (a FET) or passive (an N-port)."),
                        new("network",   "--network",    OptKind.PathOrName, "Active: a stock network id (gan-two-shell, classic-shell, series-only) or a six-pin .csch."),
                        new("fingers",   "--fingers",    OptKind.Integer,    "Active: N0, the measured device's finger count."),
                        new("unitWidth", "--unit-width", OptKind.Str,        "Active: W0, its unit gate width with a unit (125 um)."),
                    ],
                    "Create a .cmc."),
                new("import", ["model", "import"],
                    [
                        new("path",   true, "The .cmc."),
                        new("source", true, "A source file, or a folder of them (a Touchstone set is a folder)."),
                    ],
                    [
                        new("as",            "--as",             OptKind.Str,        "The data's kind: DcIv, PulsedIv, SParams, PulsedSParams, SimulatedTables, LargeSignal, PassiveSParams."),
                        new("name",          "--name",           OptKind.Str,        "The data set's name. Default: the source's."),
                        new("format",        "--format",         OptKind.Str,        "touchstone, csv, npy, mat, npz, ds (a binary simulation dataset) or loadpull. Default: from the extension."),
                        new("pattern",       "--pattern",        OptKind.Str,        "Touchstone: the bias pattern, fields {vgs} {vds} {T} {id} {ig} {geometry} (Vgs{vgs}_Vds{vds})."),
                        new("patternSource", "--pattern-source", OptKind.Str,        "Touchstone: name (default) or comment."),
                        new("map",           "--map",            OptKind.StrRepeat,  "CSV: column=quantity[:unit] (Vg=vgs:V); npy/mat/npz/ds: array=quantity[:unit] (f=freq:GHz), a ds array named Group.Variable (SP1.S), a leading - reading it negated (-DC1.VD.i=id). Optional for an array file: omitted, the map comes from the array names and is reported."),
                        new("condition",     "--condition",      OptKind.StrRepeat,  "A measurement condition as the .cmc names it: ReferencePlane=device, AmbientC=25, Quiescent.Vds=28."),
                        new("noHeader",      "--no-header",      OptKind.Flag,       "CSV: the first row is data."),
                        new("delimiter",     "--delimiter",      OptKind.Str,        "CSV: the delimiter. Default: inferred."),
                        new("z0",            "--z0",             OptKind.Number,     "The reference impedance a Y table converts to S at, ohms."),
                        new("freq",          "--freq",           OptKind.Str,        "npy/mat/npz/ds: the frequency of a table with no frequency array (2GHz)."),
                        new("dryRun",        "--dry-run",        OptKind.Flag,       "Read the sources and write nothing: what the file holds, the map, what would be read and how, every warning. The .cmc need not exist yet."),
                    ],
                    "Add a data set to the .cmc and import it (dryRun: check the data first). The formats, with examples, and what to run in another simulator: 'reference model-data'."),
                new("deembed", ["model", "deembed"],
                    [new("path", true, "The .cmc.")], [],
                    "Remove the pads and the network: one line per data set of intrinsic data."),
                new("fit", ["model", "fit"],
                    [new("path", true, "The .cmc.")],
                    [
                        new("step",      "--step",       OptKind.Str,        "network, thermal, current, traps, charge, passive, or all."),
                        new("method",    "--method",     OptKind.Str,        "The step's method: network ColdFet|MultiBias|ColdFetThenMultiBias; current, charge ANN|Table|Equation; traps the trap model."),
                        new("seed",      "--seed",       OptKind.Integer,    "The seed the fit is deterministic for."),
                        new("topology",  "--topology",   OptKind.PathOrName, "passive: a library topology id or a .csch."),
                        new("fingers",   "--fingers",    OptKind.Integer,    "Record N0 first, when the document has none."),
                        new("unitWidth", "--unit-width", OptKind.Str,        "Record W0 first, with fingers."),
                    ],
                    "Fit one step, or all of them; the results are written into the .cmc."),
                new("check-charge", ["model", "check-charge"],
                    [new("path", true, "The .cmc, or a .cnl holding the device.")],
                    [
                        new("device",    "--device",    OptKind.Str, "A .cnl: the instance to check (X1)."),
                        new("threshold", "--threshold", OptKind.Str, "The spurious DC, as a percentage of the fundamental gate current, above which it fails (0.1%)."),
                        new("bias",      "--bias",      OptKind.Str, "vgs,vds at the intrinsic plane; required for a .cnl."),
                        new("region",    "--region",    OptKind.Str, "A .cnl: vgs0,vgs1,vds0,vds1 to check the capacitances' curl over."),
                        new("freq",      "--freq",      OptKind.Str, "The drive frequency (1GHz)."),
                    ],
                    "Check charge conservation; exit 3 above the threshold."),
                new("explain", ["model", "explain"],
                    [new("path", true, "The .cmc.")], [],
                    "Data sets, intrinsic ranges, N0/W0, the temperature mode, each step's state, network values and trap levels."),
                new("export", ["model", "export"],
                    [new("path", true, "The .cmc, or a .cmodel to write as Verilog-A.")],
                    [
                        new("output",  "-o",        OptKind.Path,       "Active: the file to write; its extension picks the format, .cmodel or .va/.vams (Verilog-A)."),
                        new("module",  "--module",  OptKind.Str,        "Verilog-A: the module's name. Default: the model's file name."),
                        new("symbol",  "--symbol",  OptKind.PathOrName, "Active .cmodel: a library symbol name or a .csym; Passive: a .csym."),
                        new("created", "--created", OptKind.Str,        "Active .cmodel: the provenance timestamp. Default: now."),
                        new("cell",    "--cell",    OptKind.Path,       "Passive: the workspace (or library folder) the cell is created in."),
                        new("name",    "--name",    OptKind.Str,        "Passive: the cell's name."),
                    ],
                    "Write the model: a .cmodel, Verilog-A source, or a cell."),
                new("validate", ["model", "validate"],
                    [new("path", true, "The .cmc, or a .cmodel with cmc.")],
                    [
                        new("model",     "--model",     OptKind.Path,      "With a .cmc: the .cmodel to validate. Default: the document's current state."),
                        new("cmc",       "--cmc",       OptKind.Path,      "With a .cmodel: the .cmc whose data it is validated against."),
                        new("against",   "--against",   OptKind.StrList,   "Only these data sets."),
                        new("tolerance", "--tolerance", OptKind.StrRepeat, "Key=value: SMedian, S95, IvRelative, PowerDb, EfficiencyPoints, AmPmDegrees, ImpedanceVswr."),
                        new("compression", "--compression", OptKind.Number, "The Gt compression, dB, a loadpull is compared at."),
                    ],
                    "Compare the model with the data; exit 3 when a tolerance is missed."),
                new("report", ["model", "report"],
                    [new("path", true, "The .cmc.")],
                    [
                        new("model",       "--model",       OptKind.Path,    "The .cmodel to report. Default: the document's current state."),
                        new("output",      "-o",            OptKind.Path,    "The folder. Default: <name>-report beside the .cmc."),
                        new("sections",    "--sections",    OptKind.StrList, "Dc, Thermal, Charge, CapacitanceSlices, SParams, PaModes."),
                        new("classes",     "--classes",     OptKind.StrList, "PA classes: B, J, F, FInverse, …"),
                        new("freq",        "--freq",        OptKind.Str,     "The PA fundamental (2GHz)."),
                        new("bias",        "--bias",        OptKind.Str,     "The PA bias vgs,vds at the device plane."),
                        new("source2f0",   "--source-2f0",  OptKind.Str,     "The source's 2f0 termination re,im, ohms."),
                        new("ropt",        "--ropt",        OptKind.Number,  "R_opt, ohms."),
                        new("disc",        "--disc",        OptKind.Str,     "The loadpull disc: radius,rings,spokes."),
                        new("pin",         "--pin",         OptKind.Str,     "The drive: start,step,max in dBm."),
                        new("compression", "--compression", OptKind.Number,  "The compression, dB."),
                        new("pdf",         "--pdf",         OptKind.Path,    "Also write the report's display as a PDF, one page per section."),
                    ],
                    "Write the Model Validation Report folder and its Data Display document."),
            ]),

        // The seventh, and the only one that names no file. It is also reachable as MCP RESOURCES,
        // which is the cheaper channel — a resource costs a URI and a title until it is read, where
        // this description is a standing per-session cost. It exists anyway because not every client
        // surfaces resources to the model, and a capability the model cannot reach is not a
        // capability (R-aut6-4). Two lines, and it earns them by being the thing that unblocks
        // writing a document at all.
        // R-rc5-23, R-rc5-6h. Three of the four nouns are the CLI's verbs by construction; the
        // batch's own open and close are NOT here, because they hold session state and a process
        // that exits after one command cannot. They live on this server (see HistoryBatchTool),
        // which is the only surface that can hold them.
        new("history",
            "This workspace's restore points: keep one, list them, or put the workspace back to one. "
          + "Open a batch with 'batch' BEFORE your first modification — a restore point taken "
          + "afterwards protects nothing.",
            "action",
            "What to do.",
            [
                new("checkpoint", ["history", "checkpoint"],
                    [new("path", true, "The workspace folder.")],
                    [
                        new("intent", "--intent", OptKind.Str,
                            "One line saying what this state is. It is the label the designer reads."),
                        new("note", "--note", OptKind.Str,
                            "More detail, as long as it needs to be — what the one line has no room "
                          + "for. It is kept with the entry and the designer can read and correct it."),
                        new("leaveOut", "--leave-out", OptKind.StrRepeat,
                            "A workspace-relative path to leave out of this one."),
                        new("includeLarge", "--include-large", OptKind.Flag,
                            "Keep unusually large new files too. Without this, an unanswered one is "
                          + "a refusal naming it: keeping a file cannot be undone, leaving it out can."),
                    ],
                    "Keep a restore point of the workspace as it stands."),
                new("list", ["history", "list"],
                    [new("path", true, "The workspace folder.")],
                    [new("limit", "--limit", OptKind.Integer, "Return at most this many, newest first.")],
                    "The restore points, newest first."),
                new("restore", ["history", "restore"],
                    [new("path", true, "The workspace folder.")],
                    [new("point", "--point", OptKind.Integer,
                         "Which restore point, as the number 'list' reports.")],
                    "Put the workspace back to one restore point. The state being replaced is kept first."),
            ]),

        // The external 3D solvers: what is installed and what each build can do. `install` and `remove`
        // stay off this surface — both download or delete on the user's machine behind a consent that
        // is the PERSON's to give, and a tool call is not that person.
        new("solver",
            "Which external 3D solvers (Palace, Gmsh, openEMS, EMerge) are installed here and what each build can do: "
          + "driven solves, wave ports, eigenmode solves, and with capability=gpu whether Palace solves on a GPU and "
          + "how many it finds; for EMerge, its sparse-solver tier and whether GPU solving is enabled, with the command "
          + "that enables it. result.machine is this machine's physical and logical cores and its memory, which is what "
          + "run's cores is chosen against: more Palace processes take more memory. Ask before writing a 3D setup that needs one. Reads "
          + "no file and writes nothing.",
            null, null,
            [
                new("", [ "solver", "list" ], [],
                    [new("capability", "--capability", OptKind.Str,
                         "gpu: also ask whether Palace solves on a GPU (a one-element solve, so only when asked).")], ""),
            ]),

        new("reference",
            "What may be written in circuitRF's documents: the reference pages, the generated " +
            "catalogue of every netlist primitive (start from 'component-index'), and the " +
            "technologies and materials that ship with circuitRF. " +
            "No arguments lists the topics and their sizes. Reads no file and writes nothing.",
            null, null,
            [
                new("", [ "reference" ],
                    [
                        new("topic", false,
                            "Which topic. Omit for the list. 'component-index' lists every primitive in a line each; " +
                            "'technologies' and 'shipped-materials' are what ships.",
                            Required: false),
                        new("type",  false,
                            "With topic 'components', one primitive's .cnl type token — MLIN, SDD, FET_Statz. " +
                            "With 'analyses', one type= token; with 'technologies', one technology id.",
                            Required: false),
                    ],
                    [
                        new("tables", "--tables", OptKind.Str,
                            "A document format's topic (schematic, layout, data-display, …): text (default — the page with its "
                          + "field tables) or schema (the tables as reference.schema, and the text without them). One or the "
                          + "other, never both."),
                    ],
                    ""),
            ]),
    ];

    // ── translation ──────────────────────────────────────────────────────────

    /// <summary>
    /// Turns one tool call into the argument vector the CLI would have been given, or refuses.
    /// <c>--json</c> is appended here and not offered as an argument: a document is what a tool
    /// call returns, always (R-aut5-5).
    /// </summary>
    public static string[]? ToArgv(string tool, JsonObject? arguments, PathRoot root, out Diagnostic? refusal)
    {
        refusal = null;

        var spec = Tools.FirstOrDefault(t => t.Name == tool);
        if (spec is null)
        {
            refusal = CliDiagnostics.ServeUnknownTool(tool, string.Join(", ", Tools.Select(t => t.Name)));
            return null;
        }

        arguments ??= [];

        ToolMode mode;
        if (spec.SelectorName is { } selector)
        {
            string? chosen = arguments[selector]?.GetValue<JsonElement>().ValueKind == JsonValueKind.String
                ? arguments[selector]!.GetValue<string>()
                : null;

            if (chosen is null)
            {
                refusal = CliDiagnostics.ServeArgumentRequired(tool, selector);
                return null;
            }
            if (spec.Modes.FirstOrDefault(m => m.Selector == chosen) is not { } found)
            {
                refusal = CliDiagnostics.ServeArgumentUnknownValue(
                    tool, selector, chosen, string.Join(", ", spec.Modes.Select(m => m.Selector)));
                return null;
            }
            mode = found;
        }
        else mode = spec.Modes[0];

        var argv = new List<string>(mode.Verb);

        // Positionals first, in declaration order — the verbs take them that way.
        //
        // An OPTIONAL one that is absent stops the run: everything after it is positional too, so
        // emitting a later argument into an earlier slot would answer a different question in
        // silence. That is refused by name instead.
        bool stopped = false;
        foreach (var p in mode.Positionals)
        {
            if (arguments[p.Json] is not { } node)
            {
                if (p.Required)
                {
                    refusal = CliDiagnostics.ServeArgumentRequired(tool, p.Json);
                    return null;
                }
                stopped = true;
                continue;
            }
            if (stopped)
            {
                refusal = CliDiagnostics.ServeArgumentRequired(tool, LastMissingBefore(mode, p));
                return null;
            }

            string? text = AsString(node, tool, p.Json, ref refusal);
            if (text is null) return null;

            if (p.IsPath)
            {
                text = root.Resolve(text, out refusal);
                if (text is null) return null;
            }
            argv.Add(text);
        }

        // Then every option the call carries. An argument that belongs to no other mode of this tool
        // is unknown; one that belongs to another mode is named as such, because "unknown option
        // grid" is a worse answer than "grid is lp's, not lpp's".
        foreach (var (name, node) in arguments)
        {
            if (name == spec.SelectorName) continue;
            if (mode.Positionals.Any(p => p.Json == name)) continue;
            // An adapter argument becomes nothing. It is still TYPE-checked, because a client that
            // wrote `"attachImage": "yes"` and was handed a picture-less result with nothing said
            // would have no way to find its own mistake.
            if (spec.Adapter?.FirstOrDefault(o => o.Json == name) is { } own)
            {
                if (node is not null && !Emit(null, own, node, tool, root, ref refusal)) return null;
                continue;
            }
            if (node is null) continue;

            if (mode.Options.FirstOrDefault(o => o.Json == name) is not { } opt)
            {
                refusal = spec.Modes.Any(m => m.Options.Any(o => o.Json == name) ||
                                              m.Positionals.Any(p => p.Json == name))
                    ? CliDiagnostics.ServeArgumentNotForMode(tool, name, mode.Selector.Length == 0 ? tool : mode.Selector)
                    : CliDiagnostics.ServeArgumentUnknownValue(
                          tool, "arguments", name,
                          string.Join(", ", mode.Options.Select(o => o.Json)
                                                        .Concat(mode.Positionals.Select(p => p.Json))));
                return null;
            }

            if (!Emit(argv, opt, node, tool, root, ref refusal)) return null;
        }

        argv.Add("--json");
        return [.. argv];
    }

    /// <summary>The positional the caller left out — named, so the refusal says what to add rather
    /// than what to remove.</summary>
    private static string LastMissingBefore(ToolMode mode, ToolPositional given)
        => mode.Positionals.TakeWhile(p => p.Json != given.Json).Last().Json;

    /// <summary>
    /// Appends one argument's flag and value to <paramref name="argv"/>, or refuses.
    ///
    /// <para><paramref name="argv"/> is null for an ADAPTER argument (<see cref="ToolSpec.Adapter"/>),
    /// which becomes no command line at all: the call then does the type check and nothing else, so
    /// there is one place a JSON type is decided rather than two that could disagree.</para>
    /// </summary>
    private static bool Emit(List<string>? argv, ToolOption opt, JsonNode node, string tool,
                             PathRoot root, ref Diagnostic? refusal)
    {
        switch (opt.Kind)
        {
            case OptKind.Flag:
            {
                if (node.GetValue<JsonElement>().ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    refusal = CliDiagnostics.ServeArgumentWrongType(tool, opt.Json, "a boolean");
                    return false;
                }
                if (node.GetValue<bool>()) argv?.Add(opt.Cli);
                return true;
            }

            case OptKind.Number or OptKind.Integer:
            {
                if (node.GetValue<JsonElement>().ValueKind != JsonValueKind.Number)
                {
                    refusal = CliDiagnostics.ServeArgumentWrongType(tool, opt.Json, "a number");
                    return false;
                }
                argv?.Add(opt.Cli);
                // Invariant, because the CLI parses it invariantly (cli.md §7A) — a decimal comma
                // here would be a number the verb cannot read, on one machine only.
                argv?.Add(node.GetValue<JsonElement>().GetRawText());
                return true;
            }

            case OptKind.StrOptional:
            {
                string? optional = AsString(node, tool, opt.Json, ref refusal);
                if (optional is null) return false;
                argv?.Add(opt.Cli);
                if (optional.Length > 0) argv?.Add(optional);
                return true;
            }

            case OptKind.Str or OptKind.Path or OptKind.PathOrName:
            {
                string? text = AsString(node, tool, opt.Json, ref refusal);
                if (text is null) return false;
                if (opt.Kind == OptKind.Path || (opt.Kind == OptKind.PathOrName && NamesAFile(text)))
                {
                    text = root.Resolve(text, out refusal);
                    if (text is null) return false;
                }
                argv?.Add(opt.Cli);
                argv?.Add(text);
                return true;
            }

            case OptKind.StrMap or OptKind.PathMap:
            {
                if (node is not JsonObject map)
                {
                    refusal = CliDiagnostics.ServeArgumentWrongType(tool, opt.Json, "an object of name to value");
                    return false;
                }
                foreach (var (key, value) in map)
                {
                    string? text = value is null ? null : AsString(value, tool, opt.Json, ref refusal);
                    if (text is null)
                    {
                        refusal ??= CliDiagnostics.ServeArgumentWrongType(tool, opt.Json, "an object of name to value");
                        return false;
                    }
                    if (opt.Kind == OptKind.PathMap)
                    {
                        text = root.Resolve(text, out refusal);
                        if (text is null) return false;
                    }
                    argv?.Add(opt.Cli);
                    argv?.Add(key + "=" + text);
                }
                return true;
            }

            case OptKind.StrList or OptKind.StrRepeat or OptKind.PathRepeat:
            {
                var items = AsStrings(node, tool, opt.Json, ref refusal);
                if (items is null) return false;
                if (items.Count == 0) return true;

                if (opt.Kind == OptKind.StrList) { argv?.Add(opt.Cli); argv?.Add(string.Join(',', items)); }
                else foreach (string item in items)
                {
                    string value = item;
                    if (opt.Kind == OptKind.PathRepeat)
                    {
                        string? resolved = root.Resolve(value, out refusal);
                        if (resolved is null) return false;
                        value = resolved;
                    }
                    argv?.Add(opt.Cli);
                    argv?.Add(value);
                }
                return true;
            }

            default: return true;
        }
    }

    /// <summary>
    /// Whether a <see cref="OptKind.PathOrName"/> value is a FILE rather than a name.
    ///
    /// <para>The rule is <c>Render.ResolveTheme</c>'s, one step earlier: a value carrying an
    /// extension, a directory separator or a drive/root is a path and is confined; anything else is a
    /// bare name that resolves through a chain of directories the server root has nothing to do with.
    /// Both ends have to make the same split, which is why each names the other.</para>
    /// </summary>
    private static bool NamesAFile(string value)
        => Path.IsPathRooted(value)
        || value.Contains('/') || value.Contains('\\')
        || Path.GetExtension(value).Length > 0;

    private static string? AsString(JsonNode node, string tool, string name, ref Diagnostic? refusal)
    {
        if (node.GetValue<JsonElement>().ValueKind == JsonValueKind.String) return node.GetValue<string>();
        refusal = CliDiagnostics.ServeArgumentWrongType(tool, name, "a string");
        return null;
    }

    private static List<string>? AsStrings(JsonNode node, string tool, string name, ref Diagnostic? refusal)
    {
        if (node is JsonArray array)
        {
            var items = new List<string>(array.Count);
            foreach (var element in array)
            {
                if (element is null || element.GetValue<JsonElement>().ValueKind != JsonValueKind.String)
                {
                    refusal = CliDiagnostics.ServeArgumentWrongType(tool, name, "an array of strings");
                    return null;
                }
                items.Add(element.GetValue<string>());
            }
            return items;
        }
        if (node.GetValue<JsonElement>().ValueKind == JsonValueKind.String) return [node.GetValue<string>()];

        refusal = CliDiagnostics.ServeArgumentWrongType(tool, name, "an array of strings");
        return null;
    }

    // ── advertisement ────────────────────────────────────────────────────────

    /// <summary>The <c>tools/list</c> payload, built from the same table the translation reads.</summary>
    public static JsonArray Advertise()
    {
        var tools = new JsonArray();
        foreach (var spec in Tools)
        {
            var properties = new JsonObject();
            var required   = new JsonArray();

            if (spec.SelectorName is { } selector)
            {
                var values = new JsonArray();
                foreach (var m in spec.Modes) values.Add(m.Selector);
                properties[selector] = new JsonObject
                {
                    ["type"]        = "string",
                    ["enum"]        = values,
                    ["description"] = spec.SelectorDescription + " " +
                                      string.Join(" ", spec.Modes.Select(m => $"{m.Selector}: {m.Description}")).Trim(),
                };
                required.Add(selector);
            }

            // The union across modes. A per-mode requirement is enforced at call time with a refusal
            // naming the argument, which is the same shape every authoring verb already uses.
            foreach (var mode in spec.Modes)
            {
                foreach (var p in mode.Positionals)
                    properties[p.Json] ??= new JsonObject { ["type"] = "string", ["description"] = p.Description };

                foreach (var o in mode.Options)
                    properties[o.Json] ??= Describe(o);
            }

            // The adapter's own, advertised beside the rest because a client cannot tell — and does
            // not need to — which side of the translation an argument is read on.
            foreach (var o in spec.Adapter ?? [])
                properties[o.Json] ??= Describe(o);

            if (spec.SelectorName is null)
                foreach (var p in spec.Modes[0].Positionals.Where(p => p.Required)) required.Add(p.Json);

            tools.Add(new JsonObject
            {
                ["name"]        = spec.Name,
                ["description"] = spec.Description,
                ["inputSchema"] = new JsonObject
                {
                    ["type"]       = "object",
                    ["properties"] = properties,
                    ["required"]   = required,
                },
            });
        }
        return tools;
    }

    private static JsonObject Describe(ToolOption o) => o.Kind switch
    {
        OptKind.Flag    => new JsonObject { ["type"] = "boolean", ["description"] = o.Description },
        OptKind.Number  => new JsonObject { ["type"] = "number",  ["description"] = o.Description },
        OptKind.Integer => new JsonObject { ["type"] = "integer", ["description"] = o.Description },
        OptKind.StrList or OptKind.StrRepeat or OptKind.PathRepeat => new JsonObject
        {
            ["type"]        = "array",
            ["items"]       = new JsonObject { ["type"] = "string" },
            ["description"] = o.Description,
        },
        OptKind.StrMap or OptKind.PathMap => new JsonObject
        {
            ["type"]                 = "object",
            ["additionalProperties"] = new JsonObject { ["type"] = "string" },
            ["description"]          = o.Description,
        },
        _ => new JsonObject { ["type"] = "string", ["description"] = o.Description },
    };
}
