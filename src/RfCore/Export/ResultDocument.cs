// ================================================================
//  ResultDocument.cs — the ONE structured shape every verb's `--json` emits.
//
//  brief-automation-1-structured-output.md R-aut1-3: one schema across every verb, so a caller that
//  can read one verb's output can find its way around another's. R-aut1-9 puts it here rather than
//  in src/Cli, because a protocol adapter must be able to emit the identical document without
//  reaching into a console program (docs/design/automation-architecture.md R-aut-13).
//
//  WHAT THIS FILE DOES NOT DO: decide anything. Every number in it came from a DataCube or from
//  LoadpullResultSummary, unrounded and unscaled. R-aut1-2 — the human column widths, the dB and
//  percent presentation, the row truncation, are all terminal concerns and none of them is encoded
//  here.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Diagnostics;
using RfCore.Data;
using RfCore.Loadpull;

namespace RfCore.Export
{
    /// <summary>How the run ended, in the three states a caller has to tell apart.</summary>
    public static class ResultStatus
    {
        /// <summary>Ran, and produced something usable.</summary>
        public const string Ok = "ok";
        /// <summary>Ran, but did not converge — <c>cli.md</c> §7's exit code 2, whose test is
        /// deliberately per-verb (R-aut-8).</summary>
        public const string NotConverged = "not-converged";
        /// <summary>Could not run, refused, or was stopped.</summary>
        public const string Failed = "failed";

        /// <summary>The mapping the CLI's own exit codes already imply, in one place.</summary>
        public static string FromExitCode(int exitCode) => exitCode switch
        {
            0 => Ok,
            2 => NotConverged,
            _ => Failed,
        };
    }

    /// <param name="Version">circuitRF's own version, so a document found on disk says which build
    /// produced it.</param>
    /// <param name="Verb">The verb as typed.</param>
    public sealed record ResultHeader(string Version, string Verb);

    /// <param name="Path">The input file, as given.</param>
    /// <param name="Analysis">
    /// The chain that ACTUALLY ran after <c>SelectTop</c>'s promotion (<c>cli.md</c> §4) — not what
    /// was requested. A caller that asked for an inner analysis and got its wrapper must be able to
    /// see that from the document alone, because the difference is a whole sweep axis.
    /// </param>
    public sealed record ResultInput(string? Path, string? Analysis);

    /// <param name="Kind">What the file is — <c>touchstone</c>, <c>npy</c>, <c>mat</c>, <c>tsv</c>,
    /// <c>spl</c>, <c>lpcwave</c>, <c>gamma-grid</c>, <c>layout</c>.</param>
    public sealed record ResultOutput(string Kind, string Path);

    /// <param name="Name">The axis name, which is what the grid axis is located BY — never its
    /// position, since a sweep prepends one axis per nesting level.</param>
    public sealed record AxisJson(string Name, string Unit, int Length, double[] Values, string[]? Labels);

    /// <summary>
    /// One cube. <paramref name="Kind"/> is the <see cref="DataKind"/>, and it decides what
    /// <paramref name="Values"/> holds: bare numbers for a real cube, <c>[re, im]</c> pairs for a
    /// complex one. There is no third encoding and the imaginary part is never dropped.
    /// </summary>
    /// <param name="Unit">
    /// What the VALUES are in (AUT-9 R-aut9-3) — always present, never omitted. The axes have
    /// carried a unit since they were written and the values did not, which is how one result file
    /// came to hold the same quantity twice in two units with nothing to say which was which.
    /// <c>"1"</c> is a dimensionless quantity, <c>"index"</c> a flag or a count, and
    /// <c>"unknown"</c> is circuitRF saying it cannot state this one — a designer's own
    /// <c>measure</c> expression, most often. See <see cref="ResultUnits"/>.
    /// </param>
    public sealed record CubeJson(string Kind, string Unit, IReadOnlyList<AxisJson> Axes, object Values);

    /// <param name="Shape">
    /// What this result HOLDS, without the values: groups, cube names, kinds, units, axis names,
    /// lengths and extents (AUT-9 R-aut9-10). Present on every run and every <c>read</c> that
    /// produced a <see cref="DataSet"/> — including the ones whose <see cref="Groups"/> are
    /// deliberately not inline — so "what may I ask for" is answerable without first paying for the
    /// answer to a question the caller has not chosen yet.
    /// </param>
    /// <param name="Narrowed">
    /// What <c>--at</c> and <c>--range</c> actually did, per axis: the value asked for, the value
    /// returned, and whether it was the nearest grid point or an interpolation (R-aut9-9). Absent
    /// when nothing was narrowed.
    /// </param>
    /// <param name="Groups">
    /// The <see cref="DataSet"/> as it stands: groups, then named cubes. The default group's key is
    /// the empty string, which is the group's own name — the console renders it as "(default)", and
    /// that is a console decision.
    /// </param>
    /// <param name="Summary">
    /// A loadpull's or pursuit's one-row-per-grid-point projection, when the result is one. Present
    /// for the same reason the console prints it rather than the cubes: it is the useful projection,
    /// not a terminal compromise (R-aut1-5).
    /// </param>
    /// <param name="Check">
    /// What <c>check</c> looked at and what it found, in counts. The findings themselves are
    /// <c>diagnostics</c> — this is the tally, so a caller can tell "checked nothing" from
    /// "checked everything and it was clean" without counting an array
    /// (brief-automation-4-check-and-explain.md R-aut4-10).
    /// </param>
    /// <param name="Explain">
    /// What <c>explain</c> resolved, and the WALK it performed to get there. The walk is not
    /// decoration: two of these start from different files and can legitimately land on different
    /// workspaces (<c>cli.md</c> §8.1), and that is exactly the thing a caller cannot otherwise see.
    /// </param>
    /// <param name="Document">
    /// What <c>read</c> handed back when the path named one of circuitRF's own documents rather than
    /// a result file. See <see cref="DocumentJson"/>.
    /// </param>
    /// <param name="Reference">
    /// What <c>reference</c> was asked: the topic list, one topic's text, or the component
    /// catalogue. It is about no document at all, which is why it is its own payload and not a mode
    /// of <c>explain</c> — every <c>explain</c> answer is anchored to a path
    /// (brief-automation-6-reference-and-components.md §5).
    /// </param>
    public sealed record ResultPayload(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        LoadpullSummaryJson? Summary,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, CubeJson>>? Groups,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        CheckReportJson? Check = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ExplainReportJson? Explain = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        DocumentJson? Document = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ReferenceReportJson? Reference = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        HistoryReportJson? History = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RenderReportJson? Render = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        FindReportJson? Find = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ResultShapeJson? Shape = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<NarrowingJson>? Narrowed = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<WsProbeJson>? Wsprobes = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        NdfReportJson? Ndf = null);

    /// <summary>
    /// What an <c>NDF=yes</c> run found (brief-wsprobe-6 R-wsp6-2): the right-half-plane pole count
    /// of the WHOLE network, the net encirclement it was rounded from, and the property findings the
    /// engine raised on its own output.
    /// </summary>
    /// <param name="Poles">Clockwise encirclements of the origin by <c>NDF(jω)</c> over the closed
    /// Nyquist contour, rounded — the number of right-half-plane poles (Platzker, §8 p. 112).</param>
    /// <param name="Encirclements">The unrounded net count, so a caller can see how far from an
    /// integer the sweep actually got. A value well away from one says the grid is too coarse or
    /// stops too low, and the findings say which.</param>
    /// <param name="AtFmax">The NDF at the top of the sweep, which must tend to 1 (property 3).</param>
    /// <param name="AtFmin">The NDF at the bottom, whose imaginary part must tend to 0 (property 5).</param>
    /// <param name="Findings">The <c>ndf.*</c> diagnostic keys the run raised, in the order raised.
    /// Empty when every property held.</param>
    public sealed record NdfReportJson(
        int      Poles,
        double   Encirclements,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double[]? AtFmax = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double[]? AtFmin = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? Findings = null);

    /// <summary>
    /// One WSProbe of an S-parameter run: the document's Label and its <c>idx</c>
    /// (brief-wsprobe-1 R-wsp1-12(a)). The <c>idx</c> is reported rather than left to be inferred
    /// because it depends on the other probes, and a caller building <c>wsp(2·idx−1, 2·idx)</c>
    /// from a guess would read the wrong probe's block in silence.
    /// </summary>
    /// <param name="SmY0Min">The smallest <c>SM_Y0</c> over the sweep, LINEAR — dB is a display
    /// convention (<c>20·log10</c>) and a JSON document carries the number, not its rendering.
    /// Null when the run reported no margin for this probe.</param>
    /// <param name="SmY0MinHz">The frequency at which <paramref name="SmY0Min"/> sits, Hz.</param>
    /// <param name="SmH0Min">The smallest <c>SM_H0</c> over the sweep, linear.</param>
    /// <param name="SmH0MinHz">The frequency at which <paramref name="SmH0Min"/> sits, Hz.</param>
    public sealed record WsProbeJson(
        string Label,
        int    Idx,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? SmY0Min = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? SmY0MinHz = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? SmH0Min = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? SmH0MinHz = null);

    // ── the shape of a result, without its values (R-aut9-10) ────────────────

    /// <summary>
    /// One axis, as the shape reports it: no values, but its extents — which is what a caller needs
    /// to write an <c>--at</c> or a <c>--range</c> that will land.
    /// </summary>
    public sealed record ShapeAxisJson(
        string  Name,
        string  Unit,
        int     Length,
        double? First,
        double? Last);

    /// <param name="Elements">How many numbers this cube holds. The size a caller is choosing
    /// whether to pay for.</param>
    public sealed record ShapeCubeJson(
        string                          Kind,
        string                          Unit,
        long                            Elements,
        IReadOnlyList<ShapeAxisJson>    Axes);

    /// <summary>
    /// Every group, every cube, and the shape of each — and no values at all.
    ///
    /// <para><b>Why it is always there.</b> <c>run sparam</c> returned its whole result inline while
    /// <c>run lpp</c> returned <c>status: ok</c>, a written path and nothing else, with nothing in
    /// either tool's schema to say which a caller would get. Whichever the payload rule is, the
    /// SHAPE is uniform: a caller always learns what the run produced, and then decides what to ask
    /// for.</para>
    /// </summary>
    public sealed record ResultShapeJson(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ShapeCubeJson>> Groups);

    /// <summary>
    /// One of circuitRF's own documents, read back verbatim.
    ///
    /// <para><b>Verbatim is the whole point.</b> The formats are the interface
    /// (<c>automation-architecture.md</c> §4) — a client authors a design by WRITING one of these
    /// files — so what it needs back is the file, not a re-serialization of a model parsed out of
    /// it. A round trip through a reader and a writer would hand back something that differs from
    /// what is on disk wherever the reader is lossy, and the difference would be invisible.</para>
    ///
    /// <para>A result file does not come back this way: it becomes <see cref="Groups"/>, through the
    /// same readers the GUI's own source library uses, because a <c>.npy</c> or a Touchstone answers
    /// a question about numbers rather than about text.</para>
    /// </summary>
    /// <param name="Kind">What the path was taken to be, spelled as <c>check</c> spells it.</param>
    /// <param name="Text">The file's content, exactly as it is on disk.</param>
    public sealed record DocumentJson(string Path, string Kind, string Text);

    // ── check and explain, on the wire (R-aut4-10) ───────────────────────────

    /// <param name="Kind">
    /// What the path was taken to BE — <c>workspace</c>, <c>cell</c>, <c>schematic</c>,
    /// <c>symbol</c>, <c>layout</c>, <c>technology</c>, <c>em-setup</c>, <c>netlist</c>,
    /// <c>assembly-rules</c>, <c>folder</c>. Inferred from the path exactly as <c>convert</c> infers
    /// a format (R-aut4-11): by extension, and for a directory by what it contains.
    /// </param>
    public sealed record CheckedDocumentJson(string Path, string Kind, int Errors, int Warnings);

    /// <param name="Severity">
    /// The threshold the exit code was decided at — <c>warning</c> or <c>error</c>. Carried because
    /// a document holding warnings and <c>exitCode: 0</c> is only readable next to the threshold
    /// that made it so (R-aut4-5).
    /// </param>
    public sealed record CheckReportJson(
        string                             Root,
        string                             Severity,
        int                                DocumentsChecked,
        int                                Errors,
        int                                Warnings,
        int                                Notes,
        IReadOnlyList<CheckedDocumentJson> Documents);

    // ── find, on the wire (brief-automation-11-missing-verbs.md R-aut11-3) ───

    /// <param name="Type">schematic, symbol or layout.</param>
    /// <param name="File">
    /// The file the view resolves to, or null when the sub-folder does not resolve to one — which is
    /// a state a caller has to be able to see, not an omission.
    /// </param>
    /// <param name="State"><c>CellFolder.ResolvePrimary</c>'s own five-branch answer, verbatim.</param>
    public sealed record FoundViewJson(
        string  Type,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? File,
        string  State);

    /// <param name="Analyses">
    /// The analyses the cell's primary schematic declares, by name. Empty when it declares none;
    /// absent when the extraction could not be performed at all, which is a different answer and is
    /// reported as a note.
    /// </param>
    public sealed record FoundCellJson(
        string                        Name,
        string                        Path,
        IReadOnlyList<FoundViewJson>  Views,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>?        Analyses);

    /// <param name="Path">The workspace DIRECTORY — what every other verb takes.</param>
    /// <param name="Technology">The `.cws`'s own default technology reference, or null for none.</param>
    public sealed record FoundWorkspaceJson(
        string                        Path,
        string                        Name,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                       Technology,
        IReadOnlyList<FoundCellJson>  Cells);

    /// <param name="Depth">How deep the walk was allowed to go below the root.</param>
    /// <param name="Truncated">
    /// True when the walk stopped at its own bound rather than at the end of the tree. A listing that
    /// silently stopped short is the one failure this verb must not have: a caller would conclude the
    /// workspace it is looking for does not exist.
    /// </param>
    public sealed record FindReportJson(
        string                              Root,
        int                                 Depth,
        bool                                Truncated,
        IReadOnlyList<FoundWorkspaceJson>   Workspaces);

    /// <summary>
    /// One step of a resolution walk: what was being resolved, what it started from, what it landed
    /// on, and by which rule. <paramref name="Resolved"/> is null when the step found nothing —
    /// which is an answer, not an omission (R-aut4-8).
    /// </summary>
    public sealed record ResolutionStepJson(
        string  Step,
        string? From,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Resolved,
        string  How);

    /// <param name="Scale">
    /// What the stated unit's coefficients were multiplied by to reach base SI. Reported ALONGSIDE
    /// <paramref name="StatedUnit"/> and the base-SI numbers, because reading a unit's mark without
    /// its scale has already produced a sweep that ran at 2 Hz and looked entirely normal (R-aut4-9).
    /// </param>
    /// <param name="Step">
    /// The step SIZE in base SI for a step-size sweep; null for a point-count or explicit-list sweep,
    /// where there is no step to state and a computed one would be an invention.
    /// </param>
    public sealed record ExplainSweepJson(
        string  Variable,
        int     Points,
        double  Start,
        double  Stop,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Step,
        string  BaseUnit,
        string  StatedUnit,
        double  Scale,
        string  Kind);

    /// <param name="Chain">The chain from this root inward, outermost first.</param>
    /// <param name="Dispatched">
    /// True for the one chain <c>SelectTop</c> would run. False on every other, INCLUDING a runnable
    /// one that simply is not first — which is what makes the ambiguity visible without a run.
    /// </param>
    /// <param name="DispatchedBy">
    /// WHICH verb would run it — <c>hb</c>, <c>lp</c> or <c>lpp</c>. Chain selection is per KIND, so
    /// one netlist can declare an HB chain and a loadpull chain and each verb dispatches its own;
    /// a single "dispatched" flag with no verb beside it would read as a claim that only one runs.
    /// </param>
    /// <param name="PromotedFrom">
    /// The inner analysis whose name would have been promoted to this chain. Non-null only when the
    /// caller named one, and the whole reason this option exists (<c>cli.md</c> §4).
    /// </param>
    /// <param name="Runnable">
    /// Whether this chain would actually run — the chain bottoms out in an enabled analysis AND every
    /// reference the analysis names by string resolves.
    ///
    /// <para><b>Both halves are needed (AUT-8 R-aut8-8).</b> This used to be the first half alone, so
    /// a loadpull-pursuit naming a load tuner and a source tuner that do not exist in the design was
    /// reported <c>runnable: true, dispatched: true</c>. Whether a thing will run is the question
    /// <c>explain</c> exists to answer, and answering it optimistically is worse than not answering:
    /// a caller acts on the yes, and the refusal it eventually gets is about something it has already
    /// been told is fine.</para>
    /// </param>
    /// <param name="Unresolved">
    /// The references that did not resolve, one line each, naming the key and what it pointed at.
    /// Null when everything resolved — which is what makes the false case say WHICH reference failed
    /// rather than merely that one did.
    /// </param>
    public sealed record ExplainAnalysisJson(
        string                Name,
        string                Kind,
        bool                  Enabled,
        bool                  Runnable,
        bool                  IsRoot,
        IReadOnlyList<string> Chain,
        bool                  Dispatched,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?               DispatchedBy,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?               PromotedFrom,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ExplainSweepJson?     Sweep,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? Unresolved = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int?                  Ports = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ExplainWsProbeJson>? WsProbes = null,
        /// <summary>The effective <c>MarginThreshold=</c> in dB, or the string <c>"none"</c> when
        /// the margin report is disabled (WSP-9 R-wsp9-4). Reported for the kinds that read it —
        /// <c>sparam</c> and <c>hb</c> — because the default is not visible in the document.</summary>
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?               MarginThreshold = null,
        /// <summary>What each placed component's passivation would do if this analysis were run
        /// with <c>NDF=yes</c>, and why the run would be refused when it would (brief-wsprobe-6
        /// R-wsp6-2). Null when the analysis is not one that can carry the knob.</summary>
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ExplainNdfJson?       Ndf = null);

    /// <summary>
    /// <c>explain --analysis</c>'s answer to "what would the NDF do here" — the way to see a
    /// refusal coming without running (brief-wsprobe-6 §2).
    /// </summary>
    /// <param name="Enabled">Whether the directive actually carries <c>NDF=yes</c>. The listing is
    /// reported either way, because the question "could this design yield an NDF" is worth asking
    /// before the knob is turned on.</param>
    /// <param name="Refusal">The whole refusal the run would raise, or null when it would proceed.</param>
    /// <param name="Instances">One row per placed component, in netlist order.</param>
    public sealed record ExplainNdfJson(
        bool Enabled,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Refusal,
        IReadOnlyList<ExplainNdfInstanceJson> Instances);

    /// <param name="Activity">The model's own answer: <c>passive</c>, <c>activeExact</c>,
    /// <c>activeUserScaled</c> or <c>blackBox</c>.</param>
    /// <param name="Passivation">What the passive assembly does with it, in one line.</param>
    public sealed record ExplainNdfInstanceJson(
        string Instance, string Type, string Activity, string Passivation,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Refusal = null);

    /// <summary>
    /// One WSProbe as <c>explain --analysis</c> reports it for an S-parameter analysis
    /// (brief-wsprobe-1 R-wsp1-12(b)): its label, its <c>idx</c>, and BOTH terminal nets — G first,
    /// L second — because the orientation is what every bidirectional quantity is relative to.
    /// </summary>
    public sealed record ExplainWsProbeJson(string Label, int Idx, string G, string L);

    /// <param name="Kind">The <c>ValueKind</c> — <c>real</c>, <c>complex</c>, <c>bool</c>, or
    /// whatever else the engine produced. Reported rather than coerced: a Bool forced to a number is
    /// a different answer.</param>
    /// <param name="Text">The engine's own rendering, so a caller sees what an expression that is
    /// not a number at all evaluated to.</param>
    public sealed record ExplainExpressionJson(
        string    Expression,
        string    Kind,
        string    Text,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?   Real,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double[]? Complex,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool?     Boolean);

    /// <param name="State">
    /// <c>resolved</c>, <c>not-found</c>, or <c>primary-missing</c> — the three states
    /// <c>CellSymbolResolver</c> already keeps distinct, forwarded rather than collapsed.
    /// </param>
    /// <param name="OutsideWorkspace">
    /// True when the reference resolves to somewhere outside the referring document's own workspace.
    /// Null when there is no workspace to be outside of.
    /// </param>
    public sealed record ExplainReferenceJson(
        string  Ref,
        string  From,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ResolvedPath,
        string  State,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool?   OutsideWorkspace,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Redirect);

    // ── the three questions a caller asks BEFORE it can render (RND-3) ───────
    //
    // R-rnd3-1: options on `explain`, never three new verbs. Each is asking what circuitRF DECIDED —
    // which file is a cell's schematic, which layers the resolved technology defines, how big the
    // document is — which is the question this verb exists for, and `serve`'s tool count is capped
    // deliberately (cli.md §11.3).

    /// <summary>
    /// One VIEW of one cell, reported as a RESOLUTION rather than as a directory listing (R-rnd3-3).
    /// </summary>
    /// <param name="Type"><c>schematic</c>, <c>symbol</c> or <c>layout</c>.</param>
    /// <param name="Primary">The file <c>CellFolder.ResolvePrimary</c> chose, or absent where it chose
    /// none — which is a real, ordinary state and not an error.</param>
    /// <param name="State">
    /// <c>PrimaryState</c>, spelled the way this document spells every enum. The five stay five:
    /// <c>sole-file</c>, <c>named-present</c>, <c>missing-named-primary</c>, <c>no-primary</c>,
    /// <c>no-view</c>. <b>Collapsing them is what makes this worth more than <c>ls</c></b> — a cell
    /// whose schematic folder holds three files and names no primary is listed WITH its ambiguity, not
    /// omitted and not silently resolved to the alphabetically first one.
    /// </param>
    /// <param name="Candidates">Every view file in the sub-folder, so an ambiguity says what it is
    /// ambiguous between.</param>
    /// <param name="Defect"><c>CellViewFileValidator.DescribeDefect</c>'s own sentence about the
    /// primary — the same one <c>check</c> reports. Absent when there is nothing wrong with it.</param>
    public sealed record ExplainCellViewJson(
        string                Type,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?               Primary,
        string                State,
        IReadOnlyList<string> Candidates,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?               Defect);

    /// <param name="Folder">The cell folder itself — what <c>render --cell</c> ends up drawing from,
    /// and what makes this listing composable with that verb.</param>
    /// <param name="OutsideWorkspace">True when the cell folder is not under the workspace the path
    /// resolves through. Null when there is no workspace to be outside of.</param>
    /// <param name="Generated">
    /// True for a cell under the reserved <c>.generated-cells</c> folder — one content-addressed cell
    /// per distinct PCell placement, which the project tree hides and this listing hides with it
    /// (R-rnd3-4). Only ever present when <c>--all</c> asked for them, because a listing that does not
    /// contain them has nothing to mark.
    /// </param>
    public sealed record ExplainCellJson(
        string                             Name,
        string                             Folder,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool?                              OutsideWorkspace,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool?                              Generated,
        IReadOnlyList<ExplainCellViewJson> Views);

    /// <param name="Number">The drawing layer's own number and <paramref name="Datatype"/> — the pair
    /// that IS the layer's identity in every interchange format, and the spelling a caller that came
    /// from a GDSII import has.</param>
    /// <param name="Color">Literal <c>#rrggbb</c>. A layer's colour is process data rather than a
    /// themeable role (<c>layout-view.md</c> §2.2), so it comes from the technology, not the theme.</param>
    /// <param name="Fill">The fill pattern's name, or <c>solid</c> — which is what every layer said
    /// before stipples existed and still renders as.</param>
    /// <param name="Shapes">
    /// <b>The field that makes this worth having</b> (R-rnd3-5). How many shapes this document draws
    /// on the layer, hierarchy included and arrays multiplied (R-rnd3-6) — a technology defines every
    /// layer a process has and a given <c>.clay</c> draws on a handful, so a caller told only the
    /// technology's list will ask <c>render --layers</c> for empty layers and conclude the render is
    /// broken.
    ///
    /// <para><b>Absent, not zero, where there is no document to count against</b> — a <c>.ctech</c> or
    /// a workspace on its own. Zero would be a claim, and it would be a false one.</para>
    /// </param>
    /// <param name="InstancesUsing">How many of the document's own top-level instance PLACEMENTS
    /// contribute geometry on this layer. An array counts once — it is one placement, and its
    /// multiplication is already in <paramref name="Shapes"/>. Absent with <paramref name="Shapes"/>.</param>
    public sealed record ExplainLayerJson(
        string  Name,
        int     Number,
        int     Datatype,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Purpose,
        bool    Visible,
        bool    Selectable,
        string  Color,
        string  Fill,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long?   Shapes,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int?    InstancesUsing);

    /// <param name="Technology">The resolved technology's name, or absent where nothing resolved —
    /// which is an answer, not a gap (R-rnd3-7).</param>
    /// <param name="ResolvedFrom">The <c>.ctech</c> the layers came from. Absent where nothing did.</param>
    /// <param name="ResolvedBy">The RULE that answered, in the same words the walk uses. Where nothing
    /// resolved this names the FALLBACK PALETTE, because that is what <c>render</c> will actually draw
    /// with (R-rnd2-1) and a caller needs to know the colours it gets are not the process's.</param>
    /// <param name="Truncated">True when the hierarchical shape count hit its budget and the numbers
    /// are a floor rather than a total. Never silently absorbed.</param>
    public sealed record ExplainLayersJson(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                          Technology,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                          ResolvedFrom,
        string                           ResolvedBy,
        IReadOnlyList<ExplainLayerJson>  Layers,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool?                            Truncated = null);

    /// <param name="Name">The layer's name, as <see cref="ExplainLayerJson.Name"/> spells it.</param>
    /// <param name="Window">This layer's box in the spelling <c>render --window</c> takes, so a
    /// caller can frame on one layer without doing the arithmetic. See
    /// <see cref="ExplainExtentsJson.Window"/>.</param>
    public sealed record ExplainLayerExtentJson(
        string Name, double X0, double Y0, double X1, double Y1,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Window = null);

    /// <summary>
    /// How big the document is — <b>the same box <c>render --fit</c> frames on, from the same function</b>
    /// (R-rnd3-9). If these two could disagree the number would be worse than useless, because a
    /// caller uses this one to compute a <c>--window</c> for that one.
    /// </summary>
    /// <param name="Empty">
    /// True when the document holds no geometry at all. <b>Then there are no coordinates</b> —
    /// <c>(0,0,0,0)</c> is a point at the origin, which is a different fact and one a caller would
    /// happily divide by (R-rnd3-10).
    /// </param>
    /// <param name="Unit">
    /// The BASE SI unit the coordinates are in — <c>m</c> for a layout; <c>design-units</c> for a
    /// schematic or a symbol, whose coordinates are dimensionless and are said to be, rather than
    /// dressed up in metres (R-rnd0-5). <b>The same spelling <c>render</c> uses</b>: two spellings of
    /// one fact across two verbs is the drift this series exists to prevent.
    /// </param>
    /// <param name="Scale">What multiplies the DOCUMENT's own display unit to reach
    /// <paramref name="Unit"/> — 1e-6 for a layout drawn in micrometres, 2.54e-5 for one drawn in
    /// mils, 1 where the coordinates are dimensionless. Reading a mark without its scale has already
    /// produced a run at 2 Hz that looked entirely normal (R-aut4-9).</param>
    /// <param name="Window">
    /// <b>The same box written as <c>render --window</c> takes it</b>, so the output of this verb is
    /// the input of that one (R-aut12-3). The numeric fields are base SI and <c>--window</c> refuses
    /// a bare number — correctly, because DBU, micrometres and millimetres are three plausible
    /// pictures — so without this every windowed render needed a hand conversion, which is exactly
    /// the arithmetic-in-the-caller this surface exists to remove.
    ///
    /// <para>A layout is spelled in the document's own DISPLAY unit, with enough decimal places that
    /// the string reads back to the same DBU; a schematic or a symbol is spelled as the bare design
    /// units <c>--window</c> takes there. Absent when the document is empty, which is the one case
    /// with no box to frame.</para>
    /// </param>
    /// <param name="PerLayer">
    /// Layout only, and only the layers that have geometry. <b>The document's OWN shapes</b> — an
    /// instance's content is measured as ONE box for the whole placement and cannot be split per layer
    /// without a second walk free to disagree with the first.
    /// </param>
    /// <param name="Note">
    /// What a FIT adds that this box does not carry, where the document has any: a symbol pin's name
    /// and a Fixed-mode ruler's readout are drawn in PIXELS at a size with a floor, so they have no
    /// world extent until a page is chosen. Reporting a zoom-dependent number from a zoom-independent
    /// verb is the mistake this field exists instead of.
    /// </param>
    public sealed record ExplainExtentsJson(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? X0,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Y0,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? X1,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Y1,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Width,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Height,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Window,
        string  Unit,
        double  Scale,
        bool    Empty,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ExplainLayerExtentJson>? PerLayer,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Note = null);

    public sealed record ExplainReportJson(
        string                              Path,
        string                              Kind,
        IReadOnlyList<ResolutionStepJson>   Walks,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ExplainAnalysisJson>? Analyses,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ExplainExpressionJson?              Expression,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ExplainReferenceJson?               Reference,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ExplainCellJson>?     Cells = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ExplainLayersJson?                  Layers = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ExplainExtentsJson?                 Extents = null);

    // ── reference, on the wire (brief-automation-6-reference-and-components.md) ──

    /// <param name="Topic">The name a caller asks for, which is also the resource URI's last
    /// segment.</param>
    /// <param name="Bytes">
    /// How large the topic's text is, in UTF-8 bytes, AS SERVED — after the front matter and the
    /// documentation generator's placeholders are removed, not the authored file's size.
    ///
    /// <para>Carried because reading is the expensive direction and a client pays for every byte
    /// (<c>automation-architecture.md</c> R-aut-10). A list that hides the cost makes the cheap
    /// topics and the expensive ones look alike, and the spread here is roughly fifteen-fold
    /// (R-aut6-3).</para>
    /// </param>
    /// <param name="Text">The topic itself. Absent in a LISTING, present when one topic was
    /// asked for — so a list costs a line per topic rather than the whole library.</param>
    public sealed record ReferenceTopicJson(
        string  Topic,
        string  Title,
        string  Summary,
        int     Bytes,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Text = null);

    /// <param name="Expression">The default as an EXPRESSION — the string a freshly-placed component
    /// carries, not an evaluated number.</param>
    /// <param name="Meaning">What the parameter is for, where the registry knows. Absent where it
    /// does not: an invented meaning is worse than none (R-aut6-8).</param>
    public sealed record ReferenceParameterJson(
        string  Name,
        string  Expression,
        string  Unit,
        string  Dimension,
        bool    ShowOnSchematic,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Meaning);

    /// <summary>
    /// How many nets an instance line writes, and — where that is not fixed — what decides it.
    ///
    /// <para><b><paramref name="Count"/> and <paramref name="DeterminedBy"/> are mutually
    /// exclusive, and BOTH may be absent.</b> A number printed where the truth is "it depends" is
    /// the failure class <c>sweep-unit-scale-and-mark</c> records — plausible, specific and wrong,
    /// with nothing reporting it (R-aut6-9).</para>
    /// </summary>
    /// <param name="Names">The terminals, in the order a <c>.cnl</c> line writes their nets.</param>
    /// <param name="ListedAt">The port count <paramref name="Names"/> was listed at, when the count
    /// is parameter-determined — so an example cannot be read as an answer.</param>
    /// <param name="OrderNote">What the terminal ORDER means: whether the two ends may be swapped,
    /// and what tells them apart when they may not. Absent where nobody has stated it. Most
    /// two-terminal parts have no terminal NAMES to give, so this is the only thing
    /// <paramref name="Names"/> could not already say — without it the terminal listing for an R, an
    /// L or a C restates its own index and nothing else.</param>
    public sealed record ReferencePortsJson(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int?                  Count,
        IReadOnlyList<string> Names,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?               DeterminedBy,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int?                  ListedAt,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?               OrderNote = null);

    /// <param name="Kind">The palette entry's own name — what the editor and the <c>.csch</c> call
    /// it, which is not always what the <c>.cnl</c> calls it.</param>
    /// <param name="SearchTerms">How a person goes looking for this part, so a client can find "the
    /// thing that does X" without reading the whole catalogue.</param>
    public sealed record ReferenceSymbolJson(
        string                                  Kind,
        string                                  DisplayName,
        string                                  Category,
        IReadOnlyList<string>                   SearchTerms,
        ReferencePortsJson                      Ports,
        IReadOnlyList<ReferenceParameterJson>   Parameters);

    /// <param name="Type">The token a <c>.cnl</c> writes — the thing a caller cannot guess and is
    /// blocked without.</param>
    /// <param name="Simulatable">False for a token the palette draws and the engine cannot build.</param>
    /// <param name="Placeable">False for a token a <c>.cnl</c> may write that nothing draws.</param>
    /// <param name="Note">Why the two disagree, when they do. The mismatch is part of the answer and
    /// is never filtered out (R-aut6-10).</param>
    /// <param name="Nets">How many nets this type's <c>.cnl</c> INSTANCE LINE binds — the netlist
    /// contract, and the field to read before writing a line. <paramref name="Ports"/> is the
    /// SYMBOL's pin count, which is a different quantity: a <c>Tuner</c> draws one pin and its line
    /// takes two (AUT-10 R-aut10-2).</param>
    public sealed record ReferenceComponentJson(
        string                               Type,
        bool                                 Simulatable,
        bool                                 Placeable,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                              Note,
        ReferenceNetsJson                    Nets,
        ReferencePortsJson                   Ports,
        IReadOnlyList<ReferenceSymbolJson>   Symbols);

    /// <summary>
    /// The netlist net contract of one type, on the wire. Exactly one of the three carries the
    /// answer; a reader that finds no <c>count</c> must read <c>rule</c> rather than assume one.
    /// </summary>
    public sealed record ReferenceNetsJson(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int?    Count,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? DeterminedBy,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Rule);

    /// <summary>
    /// What <c>reference</c> answered. Exactly one of the three is present.
    /// </summary>
    /// <param name="Topics">The topic list, with each topic's served size.</param>
    /// <param name="Topic">One topic, with its text.</param>
    /// <param name="Components">The generated catalogue, or the one primitive that was asked for.</param>
    /// <summary>
    /// What <c>history</c> answered — the restore-point list, or what one boundary or one restore did
    /// (<c>docs/design/revision-control.md</c> §5.3d, RC-5 R-rc5-23).
    ///
    /// <para><b>No git vocabulary reaches this document</b> (R-rc0-6). What travels is what a designer
    /// would be shown: a time, a label, how it came about, and whether anything was left out of it. The
    /// identity of the underlying object is deliberately absent — this is the same surface the panel
    /// renders, and RC-7's explicit commit is the only place an identifier is ever named.</para>
    /// </summary>
    /// <param name="Points">The list, newest first, when the caller asked for one.</param>
    /// <param name="Recorded">Whether a boundary wrote anything. False when nothing had changed.</param>
    /// <param name="Point">The entry a boundary produced, or the one a restore went to.</param>
    /// <param name="FilesWritten">How many files a restore brought back.</param>
    /// <param name="FilesRemoved">How many a restore took away.</param>
    /// <param name="Versions">
    /// RC-7's narrative — the versions a designer kept deliberately, newest first. <b>A different list
    /// from <paramref name="Points"/> and never merged with it</b> (R-rc7-9): one is the sparse,
    /// human-written history that gets shared, the other the dense, machine-written safety net that
    /// does not leave the machine.
    /// </param>
    /// <param name="Version">The version an explicit commit produced.</param>
    /// <param name="Changes">What differs between two versions, at the granularity of documents
    /// (R-rc7-11). Naming a changed document is the answer; what changed inside one is not.</param>
    /// <param name="Copy">
    /// Where a copy of a workspace landed, and whether it is one (RC-9 R-rc9-1). <b>Named
    /// <c>Copy</c> rather than <c>Clone</c> for a language reason, not a vocabulary one</b> — a record
    /// may not declare a member called <c>Clone</c> — and it happens to match what the user-facing
    /// wording calls it.
    /// </param>
    /// <param name="Pins">
    /// RC-9's pins — which version of each referenced workspace this design is built against
    /// (R-rc9-8, R-rc9-9). <b>One entry per ALIAS, never per cell</b>: one referenced workspace is one
    /// repository with one commit identity, and a per-cell shape would let a build machine reproduce a
    /// design against two mutually inconsistent versions of one library.
    /// </param>
    /// <param name="Exchange">What a fetch or a send did (R-rc9-6).</param>
    public sealed record HistoryReportJson(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<RestorePointJson>? Points = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool?                            Recorded = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RestorePointJson?                Point = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int?                             FilesWritten = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int?                             FilesRemoved = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<VersionJson>?      Versions = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        VersionJson?                     Version = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<DocumentChangeJson>? Changes = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        CloneJson?                         Copy = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<PinJson>?            Pins = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ExchangeJson?                      Exchange = null);

    /// <summary>Where a copy of a workspace landed (RC-9 R-rc9-1).</summary>
    /// <param name="Destination">The folder that was created.</param>
    /// <param name="Workspace">Its <c>.cws</c>, or absent when what arrived is not a workspace.</param>
    /// <param name="RestorePoints">
    /// Always <c>false</c>, and present rather than implied (R-rc9-5a, §5.2a). <b>A clone carries the
    /// narrative and not the safety net</b>, because git's default fetch takes branches and tags and
    /// nothing under a private namespace. The three ways a workspace leaves a machine disagree
    /// deliberately, and a caller who is not told will assume the strongest of the three.
    /// </param>
    public sealed record CloneJson(
        string  Destination,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Workspace,
        bool    RestorePoints);

    /// <summary>
    /// One referenced workspace's pin — <b>the identity a build machine reproduces a signed-off result
    /// from</b>, which is the reason RC-9 has a headless spelling at all (R-rc9-20).
    /// </summary>
    /// <param name="Alias">The name <c>ws://alias/…</c> uses.</param>
    /// <param name="Pin">The identity this design is built against, or absent when unpinned.</param>
    /// <param name="Status">
    /// <c>unpinned</c>, <c>current</c>, <c>newer-available</c>, <c>cannot-be-honoured</c> or
    /// <c>no-history-there</c>. <b><c>cannot-be-honoured</c> is a failure and never a fall-back</b>
    /// (R-rc9-16): the cells behind that alias do not resolve, and reporting the newest version instead
    /// would be the silent wrong answer the pin exists to prevent.
    /// </param>
    /// <param name="Newest">The newest identity that workspace has, when it keeps a history.</param>
    public sealed record PinJson(
        string  Alias,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Pin,
        string  Status,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Newest);

    /// <summary>What a fetch or a send did (RC-9 R-rc9-6).</summary>
    /// <param name="Remote">What the other copy is called.</param>
    /// <param name="Changed">Whether anything actually moved.</param>
    public sealed record ExchangeJson(string Remote, bool Changed);

    /// <summary>
    /// One version a designer kept (RC-7 R-rc7-1, R-rc7-5).
    ///
    /// <para><b>This is the one place in the whole feature where an object identity travels</b>, and
    /// R-rc7-4 is why: the caller asked for this commit, and the identifier is what makes §4.1's
    /// escape hatch usable. The restore-point list beside it carries none, deliberately.</para>
    /// </summary>
    /// <param name="Id">Its identity — what to give anyone helping outside circuitRF.</param>
    /// <param name="Kept">When, in ISO-8601 UTC.</param>
    /// <param name="Title">The line the designer wrote.</param>
    /// <param name="Who">Who kept it.</param>
    /// <param name="RestoredFrom">
    /// What the workspace had been brought back from when this was kept, or absent. <b>Present on
    /// exactly the versions that need it</b> (R-rc7-6): without it, two consecutive versions where the
    /// second reverts the first read as a change of mind with no record of the moment.
    /// </param>
    /// <param name="Note">The longer note the designer wrote, or absent. Omitted on the versions
    /// nobody wrote one for, which is most of them.</param>
    public sealed record VersionJson(
        string  Id,
        string  Kept,
        string  Title,
        string  Who,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? RestoredFrom = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Note = null);

    /// <summary>One document that differs between two versions.</summary>
    /// <param name="Path">Where it is in the workspace.</param>
    /// <param name="Change"><c>added</c>, <c>changed</c>, <c>removed</c> or <c>renamed</c>.</param>
    /// <param name="Was">Where it used to be, on a rename.</param>
    public sealed record DocumentChangeJson(
        string  Path,
        string  Change,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Was = null);

    /// <summary>One entry, as the list and the panel both show it.</summary>
    /// <param name="Sequence">circuitRF's own monotonic ordering. <b>Not the clock</b> — a wall clock
    /// is user-writable state, and it supplies the label a human reads and nothing else.</param>
    /// <param name="Taken">When, in ISO-8601 UTC, for that label.</param>
    /// <param name="Origin">How it came about: <c>save-point</c>, <c>workspace-closed</c>,
    /// <c>before-batch</c>, <c>before-restore</c>, <c>recording-off</c> or <c>recording-on</c>.</param>
    /// <param name="Label">The line a designer reads.</param>
    /// <param name="Kept">Whether retention may never thin it.</param>
    /// <param name="LeftOut">Paths left out at a boundary nobody was at. A non-empty list means the
    /// entry is INCOMPLETE and says so.</param>
    /// <param name="Thinned">
    /// Whether retention has tidied this one away. <b>It is still listed and still restorable</b> —
    /// thinning drops the pointer and leaves the state, and nothing reclaims it unless a person asks.
    /// Omitted when false, which is every entry written before RC-6.
    /// </param>
    /// <param name="Note">The longer note somebody wrote, or absent. Omitted on the entries nobody
    /// wrote one for, which is most of them.</param>
    public sealed record RestorePointJson(
        long                  Sequence,
        string                Taken,
        string                Origin,
        string                Label,
        bool                  Kept,
        IReadOnlyList<string> LeftOut,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool                  Thinned = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?               Note = null);

    public sealed record ReferenceReportJson(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ReferenceTopicJson>?     Topics,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ReferenceTopicJson?                    Topic,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ReferenceComponentJson>? Components,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ReferenceAnalysisJson>?  Analyses = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ReferenceSchemaTypeJson>? Schema  = null);

    /// <summary>One object type of a JSON document format, generated from the type the reader
    /// deserialises into (AUT-10 R-aut10-3/4). The first entry is the root.</summary>
    public sealed record ReferenceSchemaTypeJson(
        string                                  Type,
        IReadOnlyList<ReferenceSchemaFieldJson> Fields);

    /// <param name="Type">The JSON shape, not the CLR type name.</param>
    /// <param name="Default">What the field is when a document omits it, read off a
    /// default-constructed instance. Empty where nothing could be read, never a guessed zero.</param>
    public sealed record ReferenceSchemaFieldJson(string Name, string Type, string Default);

    /// <summary>
    /// One <c>analysis type=</c> token and every key its directive may carry — generated from the
    /// registry the reader validates against, so a key here is a key the reader reads and a key the
    /// reader reads is a key here (AUT-10 R-aut10-1).
    /// </summary>
    /// <param name="Aliases">Other spellings of <paramref name="Type"/> the reader accepts. The
    /// schematic's own serialisation tags are among them, which is what cost one client eight
    /// guesses to find <c>loadpull_pursuit</c>.</param>
    /// <param name="BareWords">Keywords legal with no <c>=</c>.</param>
    /// <param name="RequiredOneOf">Groups where exactly one member must be written. Not expressible
    /// as a per-key <c>required</c>: a multi-tone HB writes <c>Tone[1]</c> and never a bare
    /// <c>Tone=</c>.</param>
    public sealed record ReferenceAnalysisJson(
        string                                    Type,
        IReadOnlyList<string>                     Aliases,
        IReadOnlyList<ReferenceAnalysisKeyJson>   Keys,
        IReadOnlyList<string>                     BareWords,
        IReadOnlyList<IReadOnlyList<string>>      RequiredOneOf);

    /// <param name="Universal">True for a key legal on EVERY directive whatever its type, listed
    /// against each one so a reader of a single type's entry has the whole legal set.</param>
    /// <param name="Indexed">True for a key written <c>Name[i]</c>. <paramref name="Name"/> then
    /// carries the <c>[i]</c>, because that is the form a caller writes.</param>
    public sealed record ReferenceAnalysisKeyJson(
        string  Name,
        bool    Required,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Default,
        string  Summary,
        bool    Indexed,
        bool    Universal);

    // ── the loadpull summary, on the wire ────────────────────────────────────

    /// <param name="Unit">What <c>value</c> itself is in (AUT-9 R-aut9-3). Read this one, not
    /// <paramref name="ConsoleUnit"/>: MXE's value is a FRACTION that the terminal prints as a
    /// percentage, and the two used to be reported under one field that named the terminal's.</param>
    /// <param name="ConsoleScale">What the TERMINAL multiplies <c>value</c> by. Carried so a reader
    /// can reproduce the printed table exactly; <c>value</c> itself is the engine's own number.</param>
    /// <param name="ConsoleUnit">The unit that scaled number is in — what the terminal's own label
    /// says.</param>
    public sealed record PursuitOptimumJson(
        string    Tag,
        bool      Converged,
        string    ValueCube,
        double    Value,
        string    Unit,
        double    ConsoleScale,
        string    ConsoleUnit,
        double[]  ZLoad,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double[]? ZSource);

    public sealed record PursuitOptimaJson(
        PursuitOptimumJson Mxp,
        PursuitOptimumJson Mxe,
        double             Queried,
        double             Unscorable,
        double             Recommended);

    /// <param name="Unit">What this column's RAW values are in — <c>%</c> out of an enriched run's
    /// <c>Efficiency</c> cube and <c>1</c> out of a pursuit's unenriched <c>DE</c>, which is the
    /// same quantity in two units under one column heading (AUT-9 R-aut9-3).</param>
    /// <param name="ConsoleUnit">What the terminal's column header says, after
    /// <paramref name="ConsoleScale"/>.</param>
    public sealed record FomColumnJson(
        string  Column,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? SourceCube,
        string  Unit,
        double  ConsoleScale,
        string  ConsoleUnit);

    /// <param name="DriveIndex">The drive step the FOMs were read at; <c>-1</c> when this point has
    /// no converged, non-tickle step at all, which is what makes every FOM NaN.</param>
    public sealed record GridRowJson(
        long                              Sweep,
        int                               Index,
        double                            StopCode,
        string                            Stop,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double[]?                         GammaLoad,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double[]?                         ZLoad,
        int                               DriveIndex,
        IReadOnlyDictionary<string, double> Fom);

    public sealed record GridSummaryJson(
        int                            GridPoints,
        long                           SweepPoints,
        int                            PinSteps,
        int                            Compressed,
        int                            MaxDrive,
        int                            NotConverged,
        IReadOnlyList<AxisJson>        SweepAxes,
        IReadOnlyList<FomColumnJson>   Columns,
        IReadOnlyList<GridRowJson>     Rows);

    public sealed record LoadpullSummaryJson(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?             Group,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        PursuitOptimaJson?  Optima,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        GridSummaryJson?    Grid);

    // ── render, on the wire (brief-render-2-render-verb.md R-rnd2-11) ────────

    /// <summary>
    /// What <c>render</c> DECIDED, which is the half a caller cannot see by looking at the file it got
    /// back.
    ///
    /// <para><b>Every field here answers a question the picture itself cannot.</b> A caller that asked
    /// for <c>--fit</c> has no way to know what was fitted; one that asked for a window has no way to
    /// know whether it was letterboxed; one that misspelled a layer would otherwise see a picture
    /// missing that layer and be unable to tell it from a layer that is genuinely empty. The refusals
    /// close most of that (R-rnd2-7), and this closes the rest.</para>
    ///
    /// <para><b>There is no duration.</b> <see cref="Counters"/> is <c>LayoutRenderResult</c>'s own
    /// work count — deterministic and machine-independent by construction — which is what lets a gate
    /// assert about work done rather than about a shared runner's wall clock
    /// (<c>feedback-no-new-timing-benchmark-tests</c>).</para>
    /// </summary>
    /// <param name="Kind">What the path was taken to be, spelled as <c>check</c> spells it.</param>
    /// <param name="View">Which view of a cell folder was drawn, or absent for a file named directly.</param>
    /// <param name="Layers">Layout only. Absent for a schematic or a symbol, which have no layers —
    /// an empty list there would read as "this document defines none", which is a claim.</param>
    /// <param name="Detail">Layout only, for the same reason: the LOD tiers <c>--detail</c> governs
    /// are <c>LayoutRenderer</c>'s.</param>
    /// <param name="Bytes">The size of the file written. Reported beside
    /// <see cref="RenderCountersJson.VerticesEmitted"/> because those two together are the whole of
    /// what <c>--detail</c> trades (R-rnd2-6): an undecimated vector export of a real board carries
    /// ~7.6x the vertices of the on-screen one, and every one of them is in the file.</param>
    public sealed record RenderReportJson(
        string                          Path,
        string                          Kind,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                         View,
        string                          Format,
        // Both absent for a data display, which has no world coordinates at all: its plots carry
        // their own axis windows and the page is laid out by a bounding-box fit over them. Reporting
        // a made-up viewport there would be worse than reporting none — see DataDisplay below.
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RenderViewportJson?             Viewport,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RenderExtentsJson?              Extents,
        RenderSizeJson                  Size,
        RenderThemeJson                 Theme,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<RenderLayerJson>? Layers,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RenderDetailJson?               Detail,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RenderCountersJson?             Counters,
        long                            Bytes,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RenderDataDisplayJson?          DataDisplay = null);

    /// <summary>
    /// What <c>render</c> decided about a <c>.cdd</c> (RND-4), which is a different set of questions
    /// from a drawing's: WHICH tab and which plots were drawn, and — the one that matters most —
    /// WHERE each of the document's data sources came from.
    ///
    /// <para><b>The sources are the point.</b> A `.cdd` holds no data; every curve in the picture was
    /// re-resolved from a file this run had to find. A caller looking at the picture cannot tell
    /// which file each trace read, and "the plot is empty" and "the plot read the wrong run" look
    /// identical. R-rnd4-4 makes an UNRESOLVED source a refusal; this reports the resolved ones.</para>
    /// </summary>
    /// <param name="Pages">1 for a single tab, or the tab count under <c>--all-tabs</c>.</param>
    public sealed record RenderDataDisplayJson(
        string                            Tab,
        int                               TabIndex,
        int                               TabCount,
        int                               Pages,
        int                               Plots,
        IReadOnlyList<RenderSourceJson>   Sources);

    /// <param name="Reference">The logical reference as the document spells it — the
    /// <c>run.npy</c> sentinel, a name relative to the results root, or an absolute path.</param>
    /// <param name="Path">The file it resolved to.</param>
    /// <param name="BoundBy">
    /// <c>--data</c> when the caller named the file, <c>document</c> when the reference resolved
    /// beside the `.cdd` on its own. Reported because those are two very different provenances for
    /// the same picture.
    /// </param>
    public sealed record RenderSourceJson(string Reference, string Path, string BoundBy);

    /// <param name="Mode"><c>fit</c>, <c>window</c> or <c>center</c> — which of the three the caller
    /// asked for. The three are refused TOGETHER rather than ordered (R-rnd2-3), so exactly one is
    /// ever in force and naming it costs nothing.</param>
    /// <param name="Letterboxed">
    /// True when the requested window's aspect differed from the output's and the extra was filled
    /// rather than cropped (R-rnd2-5). <b>The four coordinates are the window AFTER that</b> — what
    /// the picture actually shows. A caller that asked for a region and silently got less of it than
    /// it asked for has no way to notice; one that got more can see it here.
    /// </param>
    /// <param name="Unit">
    /// The BASE SI unit the four coordinates are in — <c>m</c> for a layout, <c>design-units</c> for a
    /// schematic or a symbol, whose coordinates are dimensionless and are reported as what they are
    /// rather than dressed up in metres (R-rnd0-5, R-rnd2-4).
    /// </param>
    /// <param name="Scale">
    /// What multiplies the DOCUMENT's own display unit to reach <paramref name="Unit"/> — 1e-6 for a
    /// layout drawn in micrometres, 2.54e-5 for one drawn in mils, 1 where the coordinates are
    /// dimensionless. Carried for the reason <c>explain --analysis</c> carries it: a mark read without
    /// its scale has already produced a run at 2 Hz that looked entirely normal.
    /// </param>
    /// <param name="Zoom">Output units per world unit — device pixels per DBU for a png, points per
    /// DBU for an svg or a pdf.</param>
    public sealed record RenderViewportJson(
        string Mode,
        double X0, double Y0, double X1, double Y1,
        string Unit,
        double Scale,
        double Zoom,
        bool   Letterboxed);

    /// <summary>The whole document's extents, in the same base SI units
    /// <see cref="RenderViewportJson"/> reports — so a caller that windowed can see how much of the
    /// document it asked for, and one that fitted can see what was fitted.</summary>
    public sealed record RenderExtentsJson(
        double X0, double Y0, double X1, double Y1, string Unit, double Scale);

    /// <param name="UnitKind"><c>device-pixels</c> for a png, <c>points</c> for an svg or a pdf. The
    /// two are not interchangeable and a bare number would leave a caller to guess which it got.</param>
    /// <param name="Scale">The raster multiplier <c>--scale</c>/<c>--dpi</c> resolved to. Always 1 for
    /// a vector format, which has no pixels to multiply.</param>
    public sealed record RenderSizeJson(int Width, int Height, string UnitKind, double Scale);

    /// <param name="ResolvedFrom">
    /// WHICH step of <c>ThemeResolver</c>'s chain answered — <c>file</c>, <c>workspace</c>,
    /// <c>user</c>, <c>shipped</c>. The chain is four steps deep and its last step always succeeds, so
    /// without this a theme that quietly fell through to the built-in palette is indistinguishable
    /// from one that resolved (R-rnd2-9).
    /// </param>
    public sealed record RenderThemeJson(string Name, string Variant, string ResolvedFrom);

    /// <param name="Rendered">Whether the layer was drawn. False for one the technology marks
    /// invisible, and for one <c>--layers</c>/<c>--hide-layers</c> excluded.</param>
    /// <param name="Shapes">
    /// Shapes on that layer in the document, drawn or not — so an empty layer and an excluded one are
    /// two different answers.
    ///
    /// <para><b>Hierarchy included and arrays multiplied</b> (RND-3 R-rnd3-6): a layer used only inside
    /// a placed sub-cell is used, and a 2x3 array of a cell drawing one rectangle contributes six. The
    /// same number <c>explain --layers</c> reports, from the same walk — <b>and deliberately NOT
    /// <see cref="RenderCountersJson.ShapesDrawn"/></b>, which counts only the top-level shapes this
    /// frame issued a draw call for; an instance's interior is accounted in
    /// <see cref="RenderCountersJson.InstancesDrawn"/> instead.</para>
    /// </param>
    /// <param name="Rendered">Whether this render painted it — <c>LayerDef.Visible</c> on the
    /// technology the drawing was taken through, after any <c>--layers</c> / <c>--hide-layers</c>.</param>
    /// <param name="Shapes">How many shapes the document draws on it, hierarchy included and arrays
    /// multiplied. An EXCLUDED layer and an EMPTY one are two different answers.</param>
    /// <param name="Color">
    /// The <c>#rrggbb</c> the layer was drawn in, <b>after any <c>--layer-colors</c> override</b>
    /// (R-aut12-1). This is what makes the override checkable: a colour change is the one thing a
    /// caller receiving only a picture cannot verify from the numbers beside it.
    /// </param>
    /// <param name="FillOpacity">What its fill was painted at, 0 to 1 — the layer's real alpha, since
    /// the renderer takes only R, G and B from the colour.</param>
    /// <param name="Framed">
    /// Whether the fit was framed on this layer. Present only when <c>--fit-layers</c> narrowed the
    /// framing (R-aut12-2); absent means the ordinary rule, which is that a fit frames everything
    /// this render draws.
    /// </param>
    public sealed record RenderLayerJson(
        string Name, bool Rendered, long Shapes,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Color = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? FillOpacity = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool? Framed = null);

    /// <param name="Mode"><c>full</c>, <c>screen</c>, or the pixel budget as written.</param>
    /// <param name="ToleranceDbu">
    /// The decimation tolerance the budget actually resolved to at this zoom, in DBU. Reported because
    /// <c>LayoutRenderDetail</c> buckets it DOWN to a power of two — so the effective tolerance is not
    /// the number the caller typed, and the difference is up to a factor of two. Absent where nothing
    /// decimates.
    /// </param>
    public sealed record RenderDetailJson(
        string Mode,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long?  ToleranceDbu);

    /// <summary><c>LayoutRenderResult</c>'s own per-frame work counters, forwarded unchanged. Layout
    /// only — the schematic and symbol renderers keep none.</summary>
    public sealed record RenderCountersJson(
        int ShapesExamined,
        int ShapesDrawn,
        int VerticesEmitted,
        int InstancesExamined,
        int InstancesDrawn,
        int PathsConstructed,
        int DrawCalls,
        int LayersVisited);

    // ── the document ─────────────────────────────────────────────────────────

    /// <summary>
    /// One JSON document per invocation, on stdout, and nothing else on stdout. A FAILED run still
    /// emits one — the failure is the payload — so a caller never has to tell "no output" apart from
    /// "output I could not parse".
    /// </summary>
    public sealed record ResultDocument(
        ResultHeader                  Circuitrf,
        ResultInput                   Input,
        string                        Status,
        int                           ExitCode,
        IReadOnlyList<ResultOutput>   Outputs,
        IReadOnlyList<DiagnosticJson> Diagnostics,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ResultPayload?                Result,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        DiagnosticSummaryJson?        DiagnosticSummary = null);

    /// <summary>
    /// The tally that replaces the <c>info</c> diagnostics under <c>--summary</c> (AUT-9 R-aut9-11),
    /// and the sentence saying how to get them back.
    ///
    /// <para><b>Present only when the caller asked for it.</b> Without <c>--summary</c> there is no
    /// such key, and <c>diagnostics</c> carries everything, exactly as it always has.</para>
    /// </summary>
    /// <param name="Omitted">How many diagnostics were left out of <c>diagnostics</c>. Never more
    /// than <paramref name="Info"/>: a warning and an error are never collapsed.</param>
    /// <param name="Full">In words, what returns the full text. There is no side file — nothing here
    /// writes one — so the answer is the invocation that reports everything.</param>
    public sealed record DiagnosticSummaryJson(
        int    Info,
        int    Warning,
        int    Error,
        int    Omitted,
        string Full);

    /// <summary>
    /// Builds and writes a <see cref="ResultDocument"/>. Nothing here consults a culture, a console
    /// width or a language setting.
    /// </summary>
    public static class ResultDocumentWriter
    {
        /// <summary>
        /// The one set of options, matching what the persistence types already do — enum-as-string
        /// is moot here (every enum is projected to a string by hand, so the wire spelling is this
        /// file's decision rather than C#'s casing), nulls are omitted, and numbers are written at
        /// full round-trippable double precision by System.Text.Json's default.
        ///
        /// <para><b>NaN and infinity are written as the named literals</b>
        /// (<see cref="JsonNumberHandling.AllowNamedFloatingPointLiterals"/>, so <c>"NaN"</c>) and
        /// that is not optional: a loadpull grid genuinely contains NaN wherever a point never
        /// converged, JSON has no number for it, and dropping the key or substituting a zero would
        /// turn "no measurement" into a measurement.</para>
        /// </summary>
        public static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented          = true,
            PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling         = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters             = { new JsonStringEnumConverter() },
        };

        public static string Serialize(ResultDocument doc) => JsonSerializer.Serialize(doc, Options);

        // ── projections ──────────────────────────────────────────────────────

        public static AxisJson ToJson(Axis a) => new(a.Name, a.Unit, a.Length, a.Values, a.Labels);

        /// <summary>The whole set's shape, values excluded. Cheap on any result — it is O(cubes),
        /// not O(numbers) — which is why it is emitted unconditionally.</summary>
        public static ResultShapeJson Shape(DataSet ds)
        {
            var outp = new Dictionary<string, IReadOnlyDictionary<string, ShapeCubeJson>>(StringComparer.Ordinal);
            foreach (string g in ds.Groups)
            {
                var into = new Dictionary<string, ShapeCubeJson>(StringComparer.Ordinal);
                foreach (var (name, cube) in ds.CubesIn(g))
                {
                    long elements = 1;
                    foreach (var ax in cube.Axes) elements *= ax.Length;

                    into[name] = new ShapeCubeJson(
                        cube.DataKind == DataKind.Real ? "real" : "complex",
                        ResultUnits.For(name, cube),
                        elements,
                        cube.Axes.Select(a => new ShapeAxisJson(
                            a.Name, a.Unit, a.Length,
                            a.Length > 0 ? a.Values[0]  : null,
                            a.Length > 0 ? a.Values[^1] : null)).ToArray());
                }
                outp[g] = into;
            }
            return new ResultShapeJson(outp);
        }

        public static CubeJson ToJson(DataCube c, string name = "")
        {
            var axes = c.Axes.Select(ToJson).ToArray();
            object values = c.DataKind == DataKind.Real
                ? c.RealValues
                : c.ComplexValues.Select(z => new[] { z.Real, z.Imaginary }).ToArray();
            return new CubeJson(
                c.DataKind == DataKind.Real ? "real" : "complex",
                ResultUnits.For(name, c), axes, values);
        }

        /// <summary>
        /// Every group and every cube, narrowed by <paramref name="groups"/> and
        /// <paramref name="cubes"/> when either is given (R-aut1-4). An unknown name is skipped
        /// silently on BOTH sides, matching <see cref="DataSetSubset.SelectGroups"/>' existing
        /// behaviour rather than inventing a second rule for the cube half.
        ///
        /// <para>The <c>__</c>-prefixed internal cubes are kept. The console skips them because they
        /// carry label metadata the axes already show; a machine caller asked for the DataSet, and
        /// this is what the DataSet holds.</para>
        /// </summary>
        public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, CubeJson>> ToJson(
            DataSet ds, IReadOnlyCollection<string>? groups = null, IReadOnlyCollection<string>? cubes = null)
        {
            var outp = new Dictionary<string, IReadOnlyDictionary<string, CubeJson>>(StringComparer.Ordinal);
            foreach (string g in ds.Groups)
            {
                if (groups is { Count: > 0 } && !groups.Contains(g, StringComparer.Ordinal)) continue;

                var into = new Dictionary<string, CubeJson>(StringComparer.Ordinal);
                foreach (var (name, cube) in ds.CubesIn(g))
                {
                    if (cubes is { Count: > 0 } && !cubes.Contains(name, StringComparer.Ordinal)) continue;
                    into[name] = ToJson(cube, name);
                }
                // A group narrowed to nothing is omitted rather than emitted empty: an empty object
                // reads as "this group holds no cubes", which is a claim about the run.
                if (into.Count > 0 || (cubes is null or { Count: 0 })) outp[g] = into;
            }
            return outp;
        }

        public static PursuitOptimumJson ToJson(PursuitOptimum o) => new(
            o.Tag, o.Converged, o.ValueCube, o.Value, o.ValueUnit, o.ValueScale, o.ConsoleUnit,
            [o.ZRe, o.ZIm],
            o.HasZsource ? [o.ZsourceRe, o.ZsourceIm] : null);

        public static GridSummaryJson ToJson(LoadpullGridSummary s)
        {
            var columns = s.Columns
                .Select(c => new FomColumnJson(c.Column, c.SourceCube, c.Unit, c.Scale, c.ConsoleUnit))
                .ToArray();
            var rows = s.Rows.Select(r =>
            {
                var fom = new Dictionary<string, double>(s.Columns.Count, StringComparer.Ordinal);
                for (int i = 0; i < s.Columns.Count; i++) fom[s.Columns[i].Column] = r.Fom[i];
                return new GridRowJson(
                    r.Outer, r.Index, r.StopCode, LoadpullResultSummary.StopName(r.StopCode),
                    r.GammaLoad is { } g ? [g.Real, g.Imaginary] : null,
                    r.ZLoad     is { } z ? [z.Real, z.Imaginary] : null,
                    r.DriveIndex, fom);
            }).ToArray();

            return new GridSummaryJson(
                s.GridPoints, s.OuterPoints, s.PinSteps,
                s.Compressed, s.MaxDrive, s.NotConverged,
                s.OuterAxes.Select(ToJson).ToArray(), columns, rows);
        }

        /// <summary>
        /// The loadpull projection for whichever group carries the surface — searched for by name
        /// rather than assumed to be the default, because a swept run leaves the cubes in the
        /// sweep's own group. Null when this DataSet is not a loadpull at all.
        /// </summary>
        public static LoadpullSummaryJson? SummarizeLoadpull(DataSet ds)
        {
            foreach (string g in ds.Groups)
            {
                var cubes = ds.CubesIn(g);
                bool pursuit = LoadpullResultSummary.HasPursuit(cubes);
                bool grid    = LoadpullResultSummary.HasGrid(cubes);
                if (!pursuit && !grid) continue;

                var optima = pursuit
                    ? LoadpullResultSummary.SummarizePursuit(cubes) is { } p
                        ? new PursuitOptimaJson(ToJson(p.Mxp), ToJson(p.Mxe), p.Queried, p.Unscorable, p.Recommended)
                        : null
                    : null;
                var gridJson = grid && LoadpullResultSummary.SummarizeGrid(cubes) is { } gs ? ToJson(gs) : null;
                if (optima is null && gridJson is null) continue;

                return new LoadpullSummaryJson(g, optima, gridJson);
            }
            return null;
        }
    }
}
