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
        NdfReportJson? Ndf = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RailReportJson? Rail = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        SmithReportJson? Smith = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        LvsReportJson? Lvs = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ImpedanceReportJson? Impedance = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ImpedanceSurveyJson? ImpedanceSurvey = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<RenderFieldPlotJson>? FieldPlots = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ImpedanceLineJson? ImpedanceLine = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<SolverJson>? Solvers = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        OptimizeReportJson? Optimize = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        YieldReportJson? Yield = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        CornerReportJson? Corners = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        CenterReportJson? Center = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        DoeReportJson? Doe = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RecognizeReportJson? Recognize = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        StepImportReportJson? StepImport = null);

    /// <summary>
    /// What <c>convert x.step</c> created, or with <c>--list-parts</c> would create (brief-em3d-128 R-em3d128-3b): the
    /// group the objects are gathered in, and each object with the part and solid it is made from.
    /// </summary>
    /// <param name="Group">The import's group; null when it made one object, or was told to make none.</param>
    /// <param name="Objects">One per object created — under <c>--list-parts</c>, one per row of the table.</param>
    public sealed record StepImportReportJson(
        string?                             Group,
        IReadOnlyList<StepImportObjectJson> Objects);

    /// <summary>One Step object: its occurrence path, its solid (null for a product of one solid, imported whole), its
    /// name, its material and why (<c>by name</c>, <c>by colour</c>, <c>unmatched</c>, <c>chosen</c>), the group path it is
    /// in, the colour it was matched by (null: none, or mixed), and whether it is imported.</summary>
    public sealed record StepImportObjectJson(
        string  Part,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int?    Solid,
        string  Name,
        string? Material,
        string  Match,
        string? Group,
        string? Colour,
        bool    Imported);

    /// <summary>
    /// One external solver as <c>solver list</c> reports it — the Settings ▸ Solvers row. Carried as data
    /// because a caller deciding whether a 3D problem can run here (an eigenmode solve, a wave port) has
    /// no other way to learn it short of running one and being refused.
    /// </summary>
    /// <param name="Tool">The id <c>solver install</c> and <c>em --solver</c> take: palace, gmsh, openems.</param>
    /// <param name="State"><c>not found</c>, <c>found</c> or <c>installed by circuitRF</c>.</param>
    /// <param name="Summary">The row's own sentence.</param>
    /// <param name="Validated">Whether the version found is one circuitRF has run its references
    /// through. Null when nothing was found.</param>
    /// <param name="Capabilities">Each capability a run could ask of it, probed on the program found.
    /// Empty when nothing was found or the tool has none to probe.</param>
    /// <param name="Install">The command that would install it here, when one would.</param>
    public sealed record SolverJson(
        string Tool,
        string Name,
        string State,
        string Summary,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Path,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Version,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Route,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool? Validated,
        IReadOnlyList<SolverCapabilityJson> Capabilities,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Install);

    /// <param name="Capability">A stable id — <c>driven</c>, <c>wave-ports</c>, <c>eigenmode</c>.</param>
    /// <param name="Description">What it means, as the terminal prints it.</param>
    /// <param name="Detail">Why it is or is not available.</param>
    public sealed record SolverCapabilityJson(string Capability, string Description, bool Available, string Detail);

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
    /// <param name="Em">
    /// For a planar <c>.cem</c> that extracts and meshes, the size of the problem a run would solve.
    /// Absent for every other document, and for a setup that never reached a mesh. It is result
    /// data rather than a note so that <c>--summary</c>, which collapses notes into counts, cannot
    /// hide the one number that decides whether a run is affordable.
    /// </param>
    public sealed record CheckedDocumentJson(
        string Path, string Kind, int Errors, int Warnings,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        CheckedEmJson? Em = null);

    /// <param name="Kernel">The kernel the setup resolved to, as the run would name it.</param>
    /// <param name="Unknowns">The mesh's unknown count.</param>
    /// <param name="Ceiling">The unknown ceiling the count is judged against.</param>
    /// <param name="CeilingKind"><c>dense</c> or <c>accelerated</c> — which ceiling governs.</param>
    /// <param name="Verdict"><c>ok</c>, <c>warn</c> or <c>refused</c> — the mesher's own budget verdict.</param>
    public sealed record CheckedEmJson(
        string Kernel, int Unknowns,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? Ceiling,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? CeilingKind,
        string Verdict);

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
    /// <param name="Solved">brief-em3d-98 R-em3d98-8 — a 3D view's setups, each leg's state alone (the detail is
    /// <c>explain</c>'s); absent for any other view.</param>
    public sealed record FoundViewJson(
        string  Type,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? File,
        string  State,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<FoundSolvedJson>? Solved = null);

    /// <summary>brief-em3d-98 — one (setup, solver leg) of a 3D view, as <c>find</c> lists it.</summary>
    /// <param name="Solver"><c>fem</c> (Palace), <c>fdtd</c> (openEMS) or <c>thermal</c>.</param>
    /// <param name="State"><c>notRun</c>, <c>current</c> or <c>outOfDate</c>.</param>
    /// <param name="Partial">The run was cancelled, did not converge, failed or was interrupted.</param>
    public sealed record FoundSolvedJson(string Setup, string Solver, string State, bool Partial);

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
    /// <param name="MaterialLibraries">brief-em3d-53 R-em3d53-7 — every <c>.cmat</c> in the workspace and
    /// the <c>.ctech</c> files that name each; absent when there are none.</param>
    /// <param name="Pictures">brief-img-2 R-im2-6 — every picture in the workspace with the kind of drawing it reads
    /// as; absent when there are none.</param>
    public sealed record FoundWorkspaceJson(
        string                        Path,
        string                        Name,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                       Technology,
        IReadOnlyList<FoundCellJson>  Cells,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<FoundMaterialLibraryJson>? MaterialLibraries = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<FoundPictureJson>? Pictures = null);

    /// <summary>brief-img-2 R-im2-6 — one picture, as <c>find</c> lists it.</summary>
    /// <param name="Kind"><c>schematic</c>, <c>layout</c> or <c>none</c>; absent when it was not read
    /// (<c>--no-analyses</c>, or a picture that does not decode — then <see cref="Reason"/> is the decoder's refusal).</param>
    /// <param name="Reason">For <c>none</c>, what was seen (<c>a blank picture</c>, …).</param>
    public sealed record FoundPictureJson(
        string  Path,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Kind,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Confidence,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Reason);

    /// <param name="Path">The <c>.cmat</c>.</param>
    /// <param name="NamedBy">The <c>.ctech</c> files in the workspace whose <c>MaterialLibraries</c> name it —
    /// empty for a library nothing uses.</param>
    public sealed record FoundMaterialLibraryJson(string Path, IReadOnlyList<string> NamedBy);

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

    // ── `rail`: the railRF answer, and the provenance that has to travel with it ──────────────

    /// <param name="Name">The port, as the document spells it — <c>U1.VDD</c>, never a node number.</param>
    /// <param name="VoltageV">V(power) − V(reference) here, which is what the die sees.</param>
    /// <param name="DropV">How far below the source's open-circuit voltage, or null where no source
    /// on the rail stated one.</param>
    /// <param name="CurrentA">What it draws, or null.</param>
    /// <param name="ObservationOnly">
    /// True where the row states no current. <b>Reported rather than inferred from a null
    /// current</b>: Q-16's distinction is the one a caller most needs and the one a defaulted zero
    /// destroys — <i>not added</i> and <i>added with no current</i> must not look the same.
    /// </param>
    public sealed record RailPortJson(
        string  Name,
        double  VoltageV,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? DropV,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? CurrentA,
        bool    ObservationOnly);

    /// <param name="ElementCount">How many netlist elements this row aggregates — what tells a reader
    /// which SHAPE it is: one element is a part, thousands are a meshed trace section, and the
    /// arithmetic that produced the drop is different for each.</param>
    public sealed record RailBreakdownJson(
        string Label,
        double DropV,
        double ShareOfTotal,
        double ResistanceOhms,
        double CurrentA,
        int    ElementCount);

    /// <param name="WithinDropBudget">Null where the rail states no budget — <b>reported rather than
    /// substituted for</b>, because a defaulted budget is a pass nobody asked for.</param>
    public sealed record RailResultJson(
        string                            Rail,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?                           SourceVoltageV,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool?                             WithinDropBudget,
        IReadOnlyList<RailPortJson>       Ports,
        IReadOnlyList<RailBreakdownJson>  Breakdown,
        int                               ViaTransitions,
        int                               ViaFlags,
        IReadOnlyList<string>             Findings);

    /// <summary>
    /// What <c>rail</c> answered, and — the half that matters six months later — what it answered it
    /// ON (brief-railrf-10-cli-verb.md R-rail10-5).
    ///
    /// <para><b>The provenance is on the document, not only in the exported file.</b> Overview §4
    /// rule 1: a result read later has no status strip, and a caller that re-serialises this document
    /// somewhere else must carry which model produced it, which reference extent was used and at what
    /// temperature — otherwise a Fast reading and a meshed one are indistinguishable, and the Fast one
    /// is the optimistic of the two.</para>
    /// </summary>
    /// <param name="Model">
    /// <c>fast</c> or <c>accurate</c>. §2.9's first rule, and the reason it is a required field rather
    /// than an optional one.
    /// </param>
    /// <param name="Indicative">
    /// True where some part ESR resolved to a class default (Q-15), which makes any peak height
    /// derived from it indicative rather than measured. <b>Said, because an indicative number looks
    /// exactly as authoritative as a real one.</b>
    /// </param>
    /// <param name="Order">The dependency order the rails were solved in.</param>
    /// <param name="Rails">The rails REPORTED — the ones <c>--rail</c> selected, or all of them.</param>
    /// <param name="Solved">Every rail the run solved, which includes the upstream ones the chain
    /// needed and the caller did not name.</param>
    public sealed record RailReportJson(
        string                          Document,
        string                          Model,
        string                          ReferenceExtent,
        double                          TemperatureCelsius,
        int                             PartsWithoutBiasCurve,
        int                             PartsModelledFromFile,
        bool                            Indicative,
        IReadOnlyList<string>           Order,
        IReadOnlyList<RailResultJson>   Rails,
        IReadOnlyList<string>           Solved,
        int                             Pads = 0,
        int                             PadsFromBoardNetlist = 0,
        int                             PadsFromArtwork = 0);

    /// <summary>
    /// One node of the Smith Chart's walk: the impedance looking back toward the generator from the
    /// output of one element, and which element produced it
    /// (brief-smith-10-cli-verb.md; docs/design/smith-chart.md §3.2).
    /// </summary>
    /// <param name="Node">0 is the generator; the last is the load. <b>A DISABLED element occupies
    /// no node</b>, which is why the element fields are carried rather than left to be indexed
    /// out of the document by this number.</param>
    /// <param name="Element">The element's name, or null on node 0.</param>
    /// <param name="Kind">Its <c>SmithElementKind</c>, or null on node 0.</param>
    /// <param name="Placement">Series or Shunt, or null on node 0.</param>
    /// <param name="Z">[R, X] in ohms.</param>
    /// <param name="Gamma">[Re, Im] against the CHART's reference impedance, which is the
    /// document's own and not the generator's.</param>
    public sealed record SmithNodeJson(
        int      Node,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?  Element,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?  Kind,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?  Placement,
        double[] Z,
        double[] Gamma);

    /// <summary>
    /// The swept band, which is the generator table's own span walked at a fixed point count.
    /// </summary>
    /// <remarks>
    /// <b>There is no <c>Clamped</c> flag any more</b> (owner instruction, 2026-09-19). The band
    /// used to be a start/stop/npts block of its own that could ask for more than the table could
    /// answer for and be narrowed, which is what that flag reported. It is the table's span now, so
    /// there is nothing to narrow and nothing to report.
    /// </remarks>
    public sealed record SmithBandJson(
        double StartHz,
        double StopHz,
        int    Points);

    /// <summary>
    /// What <c>smith</c> answered (brief-smith-10-cli-verb.md R-smith10-2).
    /// </summary>
    /// <remarks>
    /// <b>The WALK is here and not only the reading.</b> The reading answers "is it matched"; the
    /// walk answers "where did it stop being matched", which is the question a caller has when the
    /// answer is no, and it is the half an exit code cannot carry.
    /// </remarks>
    /// <param name="Vswr">Null where |Γ| ≥ 1 — an active S2P or a Z1P with negative R legitimately
    /// puts the load there. <b>Null rather than a large number</b>: a finite VSWR reported for a
    /// reflection coefficient outside the unit circle is a lie about a stability result.</param>
    /// <param name="MismatchDb">Null for the same reason, at a total mismatch — it is the same
    /// |Γ| the VSWR beside it is, put through −10·log₁₀(1−|Γ|²).</param>
    public sealed record SmithReportJson(
        string                        Document,
        string                        Name,
        double                        Z0Ohm,
        double                        FrequencyHz,
        double[]                      GeneratorZ,
        double[]                      LoadZ,
        double[]                      Gamma,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?                       Vswr,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?                       MismatchDb,
        IReadOnlyList<SmithNodeJson>  Nodes,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        SmithBandJson?                Band);

    // ── `opt` / MCP `run analysis=optimize`: what the optimizer found (brief-tuneopt-11 R-to11-2) ──
    //
    // A PROJECTION of `OptimizationResult` — the verb and the MCP tool return this one object, and the
    // Optimizer window shows the same fields. Values are the text a schematic would hold, so a caller
    // can write one straight into the file; numbers that are not values (cost, margins) are plain.

    /// <summary>What one optimization run found.</summary>
    /// <param name="Outcome"><c>goalsMet</c>, <c>goalsUnmet</c>, <c>refused</c>, <c>noConvergence</c> or
    /// <c>cancelled</c> — the exit code's reason (0, 3, 1, 2, 130).</param>
    /// <param name="SavedPreset">The preset <c>--save-preset</c> added, when it added one.</param>
    /// <param name="PerIteration">Each iteration's state, in order — only with <c>--show-iterations</c>
    /// (<c>showIterations</c> over the protocol); absent otherwise, since the final result is the answer.</param>
    public sealed record OptimizeReportJson(
        string                            Document,
        string                            Algorithm,
        IReadOnlyList<string>             Stages,
        string                            Outcome,
        string                            FinishReason,
        double?                           BestCost,
        int                               Iterations,
        long                              Evaluations,
        long                              Failures,
        long                              Infeasible,
        long                              CacheHits,
        IReadOnlyList<OptimizeVariableJson> Variables,
        IReadOnlyList<OptimizeGoalJson>   Goals,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        OptimizeSnapJson?                 Snap = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        OptimizeSensitivityJson?          Sensitivity = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                           SavedPreset = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<OptimizeIterationJson>? PerIteration = null,
        /// <summary>The points each candidate was evaluated at — <c>nominal</c> and the corners (brief-yield-7
        /// R-ya7-1); null without corners.</summary>
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>?            Corners = null,
        /// <summary>Simulations one candidate cost: one per entry of <see cref="Corners"/> (R-ya7-5); null without
        /// corners, where it is one.</summary>
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int?                              EvaluationsPerPoint = null);

    /// <summary>One iteration of an optimization, as its progress line reports it.</summary>
    /// <param name="BestCost">The best cost so far; null before any point succeeded.</param>
    /// <param name="GoalsMet">How many enabled goals the best point so far meets.</param>
    /// <param name="Stage">The stage running (Auto, snap and polish); null for one algorithm alone.</param>
    /// <param name="BestValues">The best point so far's values, as the schematic would hold them.</param>
    public sealed record OptimizeIterationJson(
        int     Iteration,
        long    Evaluations,
        double? BestCost,
        int     GoalsMet,
        long    Failures,
        long    Infeasible,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Stage,
        IReadOnlyDictionary<string, string> BestValues);

    /// <summary>One optimized entry — a part of a complex value is its own row.</summary>
    /// <param name="Start">The design's value of this key, as text.</param>
    /// <param name="Best">Its value at the best point; null when nothing succeeded.</param>
    /// <param name="Railed"><c>min</c> or <c>max</c> when the best value is at that end of a range
    /// (overview D17); null otherwise.</param>
    /// <param name="RailedAgainst">The OTHER part whose range holds it there, for a complex value.</param>
    /// <param name="Whole">For a part: the whole value the best point composes to, as the schematic
    /// writes it — what <c>explain --tunables</c> puts beside a part.</param>
    /// <param name="WholeStart">For a part: the whole value the design holds.</param>
    public sealed record OptimizeVariableJson(
        string  Key,
        string  Start,
        string? Best,
        string  Min,
        string  Max,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Railed = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? RailedAgainst = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Whole = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? WholeStart = null);

    /// <summary>One enabled goal at the best point.</summary>
    /// <param name="Value">The expression's value at the worst point (unmet) or the tightest one (met).</param>
    /// <param name="At">Where that is on <paramref name="Axis"/>, in base SI; null for a single number.</param>
    /// <param name="Margin">How far inside its limit, in the expression's unit: −(violation) when unmet.</param>
    /// <param name="Corner">Across corners (brief-yield-7 R-ya7-2): the BINDING corner — where the worst violation
    /// is, or when met everywhere the tightest margin; every other member is that corner's. Null without corners.</param>
    /// <param name="PerCorner">Across corners: the goal at each corner, in evaluation order.</param>
    public sealed record OptimizeGoalJson(
        string  Name,
        bool    Met,
        double? Value,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? At,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Axis,
        double? Margin,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Corner = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<OptimizeGoalCornerJson>? PerCorner = null);

    /// <summary>One goal at one corner of the best point (brief-yield-7 R-ya7-2).</summary>
    public sealed record OptimizeGoalCornerJson(
        string  Corner,
        bool    Met,
        double? Value,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? At,
        double? Margin);

    /// <summary>What snap-and-polish did (<c>--snap</c>, or Auto's last stage).</summary>
    public sealed record OptimizeSnapJson(int Snapped, int Neighbours, double CostBefore, double CostSnapped,
                                          double CostAfter, bool Polished);

    /// <summary>A sensitivity pass at the best point (<c>--sensitivity</c>).</summary>
    public sealed record OptimizeSensitivityJson(
        double Cost,
        IReadOnlyList<OptimizeSensitivityVariableJson> Variables,
        IReadOnlyList<OptimizeSensitivityGoalJson>     Goals);

    /// <param name="PerRange">The cost change across the variable's whole range, to first order.</param>
    /// <param name="Share">|PerRange| as a fraction of the sum over all variables.</param>
    public sealed record OptimizeSensitivityVariableJson(string Key, double PerRange, double Share);

    /// <param name="MostSensitive">The variable that moves this goal's cost most; null when none does.</param>
    public sealed record OptimizeSensitivityGoalJson(
        string Goal,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? MostSensitive,
        double PerRange);

    // ── `yield` / MCP `run analysis=montecarlo|yield` (brief-yield-5 R-ya5-2) ──────────────────
    //
    // A PROJECTION of `StatisticalResult` — the verb and the MCP tool return this one object. A yield is a
    // fraction (0…1) here and a percent on screen; values are the text a schematic would hold.

    /// <summary>What one Monte Carlo or yield run found.</summary>
    /// <param name="Mode"><c>montecarlo</c> or <c>yield</c>.</param>
    /// <param name="Outcome"><c>finished</c>, <c>belowTarget</c>, <c>refused</c>, <c>noneEvaluated</c> or
    /// <c>cancelled</c> — the exit code's reason (0, 3, 1, 2, 130).</param>
    /// <param name="Target">The yield target as a fraction; null with none (and always for montecarlo).</param>
    /// <param name="Yield">Overall; null with no goal scored.</param>
    /// <param name="Statistics">Per goal margin and per real scalar measurement: mean, σ, min, max, median, Cpk.</param>
    /// <param name="Worst">Per goal, its tightest trials, tightest first.</param>
    /// <param name="Contributions">Only with <c>--contributions</c>: what drives each goal's and measurement's spread.</param>
    /// <param name="Trial">Only for a single-trial re-run (<c>yield trial</c>): that trial.</param>
    public sealed record YieldReportJson(
        string                            Document,
        string                            Mode,
        string                            Outcome,
        string                            FinishReason,
        YieldSettingsJson                 Settings,
        int                               Trials,
        int                               DidNotEvaluate,
        IReadOnlyList<YieldReasonJson>    Reasons,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?                           Target,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        YieldEstimateJson?                Yield,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool?                             TargetMet,
        IReadOnlyList<YieldGoalJson>      Goals,
        IReadOnlyList<YieldStatisticJson> Statistics,
        IReadOnlyList<YieldWorstJson>     Worst,
        YieldKitJson                      Kit,
        long                              Evaluations,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                           Output = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<YieldContributionJson>? Contributions = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        YieldTrialJson?                   Trial = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                           SavedPreset = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                           SavedCorner = null);

    /// <summary>The settings the run used — the file's statistics line with this run's flags over it.</summary>
    public sealed record YieldSettingsJson(
        int     Trials,
        int     Seed,
        string  Sampling,
        double  Confidence,
        bool    AutoStop,
        string  NonConverged,
        string  Save,
        bool    Process,
        bool    Mismatch,
        double  SigmaScale,
        int     Parallel,
        string  Analyses);

    /// <summary>A yield and its Clopper–Pearson interval, as fractions.</summary>
    public sealed record YieldEstimateJson(int Passes, int Counted, double? Yield, double? Lower, double? Upper);

    /// <summary>One goal's yield, and its worst margin over the trials (in the goal's own unit) and where.</summary>
    public sealed record YieldGoalJson(
        string            Name,
        YieldEstimateJson Yield,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?           WorstMargin,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int?              WorstTrial);

    /// <summary>Why trials did not evaluate: the reason and the trials.</summary>
    public sealed record YieldReasonJson(string Reason, int Count, IReadOnlyList<int> Trials);

    /// <summary>The descriptive statistics of one quantity over the evaluated trials.</summary>
    /// <param name="Of"><c>goal:&lt;name&gt;:margin</c> or a measurement's name.</param>
    /// <param name="Cpk">For a goal margin: against its limit (a margin below 0 fails); null otherwise.</param>
    public sealed record YieldStatisticJson(
        string  Of,
        string  Unit,
        int     Count,
        double? Mean,
        double? Sigma,
        double? Min,
        double? Max,
        double? Median,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Cpk);

    /// <summary>One goal's tightest trials (R-ya4-10).</summary>
    public sealed record YieldWorstJson(string Goal, IReadOnlyList<YieldWorstTrialJson> Trials);

    /// <param name="Values">The trial's drawn values, as the schematic would hold them.</param>
    public sealed record YieldWorstTrialJson(int Trial, double Margin, double Worst, IReadOnlyDictionary<string, string> Values);

    /// <summary>The kit statistics in use: the distribution calls the nominal design reached.</summary>
    public sealed record YieldKitJson(int Process, int Mismatch, IReadOnlyList<string> Sections);

    /// <summary>What drives one quantity's spread (R-ya4-9).</summary>
    public sealed record YieldContributionJson(
        string Of,
        double RSquared,
        bool   Underdetermined,
        IReadOnlyList<YieldContributorJson> Contributors,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Refused = null);

    public sealed record YieldContributorJson(string Name, string Kind, double Coefficient, double Share, double Spearman);

    /// <summary>One trial re-run alone (R-ya4-7): what it drew and how it scored.</summary>
    /// <param name="Values">Every statistical entry's drawn value, as the schematic would hold it.</param>
    /// <param name="Draws">The same, as numbers in base SI.</param>
    public sealed record YieldTrialJson(
        int Trial,
        bool Evaluated,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool? Pass,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Reason,
        IReadOnlyDictionary<string, string> Values,
        IReadOnlyDictionary<string, double> Draws,
        IReadOnlyList<YieldTrialGoalJson>   Goals,
        IReadOnlyDictionary<string, double> Measurements);

    public sealed record YieldTrialGoalJson(string Name, bool Met, double? Margin, double? Worst);

    // ── `yield corners` (brief-yield-6 R-ya6-6) ──────────────────────────────────────────────

    /// <summary>What <c>yield corners</c> found: a corner × goal table of margins (one evaluation per corner), or a
    /// yield per corner (<c>--mc</c>), or the corners <c>--generate</c> wrote.</summary>
    /// <param name="Outcome"><c>finished</c> (every goal met at every corner, every yield at its target),
    /// <c>failed</c> (a goal failed at a corner, a yield below its target), <c>refused</c>, <c>noneEvaluated</c>.</param>
    public sealed record CornerReportJson(
        string                             Document,
        string                             Outcome,
        IReadOnlyList<string>              Goals,
        IReadOnlyList<CornerRowJson>       Corners,
        IReadOnlyList<CornerWorstJson>     Worst,
        long                               Evaluations,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<CornerYieldJson>?    Yields = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                            WorstYield = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<CornerDefinitionJson>? Generated = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                            Output = null);

    /// <summary>A corner <c>--generate</c> made, in full (brief-yield-16 R-ya16-6): its temperature in °C, the values it
    /// binds, a schematic's kit corner selections, and for a netlist the <c>corner</c> line that says the same.</summary>
    public sealed record CornerDefinitionJson(
        string                                Name,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?                               Temp,
        IReadOnlyDictionary<string, string>   Values,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyDictionary<string, string>?  Axes,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                               Line);

    /// <summary>One corner: whether it evaluated, why not, and each goal's margin there (in the goal's unit; a margin
    /// below 0 fails).</summary>
    public sealed record CornerRowJson(
        string                                Name,
        bool                                  Statistical,
        bool                                  Evaluated,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool?                                 Pass,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                               Reason,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?                               Temp,
        IReadOnlyDictionary<string, string>   Values,
        IReadOnlyList<CornerGoalJson>         Goals);

    public sealed record CornerGoalJson(string Name, bool Met, double? Margin, double? Worst);

    /// <summary>A goal's worst corner — where its margin is smallest.</summary>
    public sealed record CornerWorstJson(string Goal, string? Corner, double? Margin, bool Met);

    /// <summary>One corner's Monte Carlo or yield (<c>--mc</c>): the run's own report, under the corner's name.</summary>
    public sealed record CornerYieldJson(string Corner, YieldReportJson Run);

    // ── `yield center` / MCP `run analysis=center` (brief-yield-11 R-ya11-7/8) ─────────────────
    //
    // A PROJECTION of `CenteringResult`. Yields are fractions; values are the text a schematic would hold.

    /// <summary>What one design-centering run found.</summary>
    /// <param name="Outcome"><c>finished</c>, <c>belowTarget</c>, <c>refused</c>, <c>noneEvaluated</c> or
    /// <c>cancelled</c> — the exit code's reason (0, 3, 1, 2, 130).</param>
    /// <param name="Evaluations">Simulations the search ran on the common trials.</param>
    /// <param name="StartValues">The designable values the search started from.</param>
    /// <param name="BestValues">The centred nominals — what <c>--save-preset</c> stores.</param>
    /// <param name="Railed">Designable values within 0.5 % of a bound, as <c>key@min</c> or <c>key@max</c>.</param>
    /// <param name="StartYield">The start's plain yield on the common trials.</param>
    /// <param name="BestYield">The best point's plain yield on the common trials.</param>
    /// <param name="Verification">The independent check; null when nothing evaluated.</param>
    /// <param name="History">Per iteration: the best plain yield, the best smooth objective, the simulations so far.</param>
    /// <param name="Surrogate"><c>quadratic</c> when the search ran on the quadratic surrogate (brief-yield-12); absent
    /// otherwise. StartYield/BestYield are then counted on its virtual trials, and only Verification is simulated.</param>
    /// <param name="SwitchedBackAt">The iteration after which poor fits switched the search back to simulated trials.</param>
    public sealed record CenterReportJson(
        string                               Document,
        string                               Outcome,
        string                               FinishReason,
        string                               Algorithm,
        int                                  Trials,
        int                                  Verify,
        double                               Width,
        int                                  Seed,
        int                                  Iterations,
        long                                 Evaluations,
        long                                 VerifyEvaluations,
        IReadOnlyDictionary<string, string>  StartValues,
        IReadOnlyDictionary<string, string>  BestValues,
        IReadOnlyList<string>                Railed,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        YieldEstimateJson?                   StartYield,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        YieldEstimateJson?                   BestYield,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?                              StartObjective,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?                              BestObjective,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        CenterVerificationJson?              Verification,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?                              Target,
        IReadOnlyList<CenterIterationJson>   History,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                              Output = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                              SavedPreset = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                              Surrogate = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int?                                 SwitchedBackAt = null);

    /// <summary>The start and the best point on the same fresh trials, each with its interval.</summary>
    /// <param name="WithinOverlap">The intervals overlap: the gain is not resolved at this many trials.</param>
    public sealed record CenterVerificationJson(
        int Seed, int Trials, YieldEstimateJson Start, YieldEstimateJson Best, bool WithinOverlap, string Sentence);

    /// <param name="RSquared">Under the surrogate, the poorest fit R² per yield goal over the iteration's candidates.</param>
    public sealed record CenterIterationJson(
        int Iteration, double? BestYield, double? BestObjective, long Evaluations,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyDictionary<string, double>? RSquared = null);

    // ── `yield doe` / MCP `run analysis=doe` (brief-yield-14 R-ya14-8) ─────────────────────────
    //
    // A PROJECTION of `DoeResult` (and, with --optimum, `DoeOptimum`). Effects are on coded -1/+1 factors.

    /// <summary>What one design of experiments found.</summary>
    /// <param name="Outcome"><c>finished</c>, <c>refused</c>, <c>noneEvaluated</c> or <c>cancelled</c> — exit 0, 1, 2, 130.</param>
    /// <param name="Design"><c>full2</c>, <c>frac</c>, <c>pb</c> or <c>ccf</c>.</param>
    /// <param name="Description">The design in words, with its run count.</param>
    /// <param name="Generators">A fraction's generators (<c>E=ABCD</c>); empty otherwise.</param>
    /// <param name="Factors"><c>opt</c> or <c>stat</c>.</param>
    /// <param name="FactorList">Each factor's letter, key and three levels as value text.</param>
    /// <param name="Evaluations">Simulations run — repeated centre points are one.</param>
    /// <param name="Optimum">With --optimum: the fitted model's best point and its confirmation.</param>
    public sealed record DoeReportJson(
        string                               Document,
        string                               Outcome,
        string                               FinishReason,
        string                               Design,
        string                               Description,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int?                                 Resolution,
        IReadOnlyList<string>                Generators,
        string                               Factors,
        string                               Levels,
        IReadOnlyList<DoeFactorJson>         FactorList,
        int                                  Runs,
        int                                  Evaluated,
        long                                 Evaluations,
        IReadOnlyList<DoeResponseJson>       Responses,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        DoeOptimumJson?                      Optimum,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                              Output = null);

    /// <summary>One factor: its letter in the effect names, the entry, and its low, centre and high levels.</summary>
    public sealed record DoeFactorJson(string Letter, string Key, string Low, string Centre, string High);

    /// <summary>One response analysed. <c>effects</c> is empty when too few runs evaluated it.</summary>
    /// <param name="Kind"><c>worst</c> (a goal's value), <c>margin</c> (a goal's margin) or <c>measure</c>.</param>
    /// <param name="Intercept">The fitted model's constant — the response at the design's centre.</param>
    /// <param name="LenthPse">Lenth's pseudo-standard-error of the effects.</param>
    /// <param name="LenthMargin">Lenth's margin of error: an effect beyond it is active.</param>
    /// <param name="Curvature">A two-level design with centre points: the cube mean less the centre mean.</param>
    public sealed record DoeResponseJson(
        string                               Name,
        string                               Kind,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                              Goal,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?                              Intercept,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?                              RSquared,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?                              LenthPse,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?                              LenthMargin,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?                              Curvature,
        bool                                 CurvatureActive,
        IReadOnlyList<DoeEffectJson>         Effects);

    /// <summary>One effect: twice the coded coefficient, whether it is active, and every effect it is confounded with
    /// (a partial one with its correlation, <c>BC (-0.33)</c>).</summary>
    public sealed record DoeEffectJson(string Term, double Effect, double Coefficient, bool Active, IReadOnlyList<string> Aliases);

    /// <summary>The model optimum and its confirmation run.</summary>
    /// <param name="Values">The point as a schematic would hold it — what Send to Tuning loads.</param>
    /// <param name="Coded">The point in coded units, after snapping to allowed values.</param>
    /// <param name="PredictedObjective">The smallest predicted goal margin, each over its scale.</param>
    /// <param name="Confirmed">The confirmation run evaluated.</param>
    public sealed record DoeOptimumJson(
        string                               Algorithm,
        IReadOnlyDictionary<string, string>  Values,
        IReadOnlyList<double>                Coded,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?                              PredictedObjective,
        bool                                 Confirmed,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                              Reason,
        IReadOnlyList<DoeGoalPredictionJson> Goals);

    /// <summary>One goal at the optimum: predicted by the model and simulated by the confirmation.</summary>
    public sealed record DoeGoalPredictionJson(
        string Goal,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PredictedValue,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PredictedMargin,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? SimulatedValue,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? SimulatedMargin,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Met);

    // ── `lvs`: what the comparison concluded (brief-lvs-11-cli-verb.md R-lvs11-3c) ───────────
    //
    // A PROJECTION of `LvsRunResult` and nothing else. Every number below is a field or a derived
    // accessor of that record, unrounded and unrenamed; nothing here decides anything, counts
    // anything a second time, or invents a verdict the run did not reach. That is the whole of
    // R-lvs11-3c: a second tally computed on the way out is a second answer to "what did the
    // comparison find", differing in what it counted, and nothing would report the drift.

    /// <summary>One side's tally, before and after the collapse — <c>LvsSideCounts</c> verbatim.</summary>
    /// <remarks>
    /// <b>Both numbers, always.</b> A caller reading <i>32 devices</i> against <i>8</i> needs to see
    /// the merge that explains it, and a document carrying only the compared count cannot say
    /// whether the reduction did anything at all.
    /// </remarks>
    public sealed record LvsSideCountsJson(
        int DevicesBefore, int DevicesAfter, int NetsBefore, int NetsAfter);

    /// <summary>
    /// One line of the report.
    /// </summary>
    /// <remarks>
    /// <b>The id is the contract and the sentence is not</b> (R-lvs8-2a): <paramref name="Id"/> and
    /// <paramref name="Arguments"/> are what a caller keys on and reads values out of, and
    /// <paramref name="Message"/> is the same English sentence the human report printed. A caller
    /// matching on the sentence is doing the thing this shape exists to make unnecessary.
    ///
    /// <para>Every finding is ALSO in the document's own <c>diagnostics</c> array, which is where a
    /// caller that does not care which cell produced what reads them. What this carries and that
    /// cannot is the rest of <c>LvsFinding</c>: the designer's own object names, the waiver state,
    /// and somewhere to look.</para>
    /// </remarks>
    /// <param name="Objects">The designer's own names for what this is about, <b>un-reduced</b>
    /// (R-lvs8-2b) — a collapsed four-finger device names all four. Empty on a run-level line.</param>
    /// <param name="Waived">A waived finding is <b>still reported</b> and merely not counted
    /// (R-lvs8-1a).</param>
    /// <param name="Marker">Where to look: <c>[minX, minY, maxX, maxY]</c> in the layout's own DBU.
    /// Absent where there is nothing to point at — a device the artwork does not have has no
    /// artwork to mark, and that absence IS the finding (R-lvs8-2c).</param>
    public sealed record LvsFindingJson(
        string                                Id,
        string                                Severity,
        string                                Message,
        IReadOnlyList<string>                 Objects,
        bool                                  Waived,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                               WaiverReason,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long[]?                               Marker,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyDictionary<string, object?>? Arguments);

    /// <summary>
    /// One part placed end for end — <c>TurnedPart</c> verbatim (brief LVS 16 R-lvs16-2d/3e).
    /// </summary>
    /// <remarks>
    /// <b>Everything an agent needs to write the fix itself</b>, which is the whole of the CLI's side
    /// of it: the verb is read-only (<c>cli.md</c> §19), and the turn is <c>T' = Land1 + Land2 − T</c>
    /// with 180° added to the rotation — each land then sits exactly where the other one was,
    /// whatever the footprint's own origin. Uncapped, unlike the findings.
    /// </remarks>
    /// <param name="Refdes">The part.</param>
    /// <param name="Instance">Its index in the cell's root <c>.clay</c> instance list.</param>
    /// <param name="Land1">Where the land the footprint calls pin 1 sits, <c>[x, y]</c> in DBU.</param>
    /// <param name="Land2">Where the other land sits.</param>
    /// <param name="Margin">How much more of the copper's evidence agrees with the turn than with
    /// the schematic's order.</param>
    public sealed record LvsTurnedPartJson(
        string Refdes, int Instance, long[] Land1, long[] Land2, int Margin);

    /// <summary>One cell compared — or one skipped, which is a different answer and is said rather
    /// than omitted (R-lvs11-2b).</summary>
    /// <param name="Compared">False for a cell holding only one of the two views. The ordinary
    /// mid-design state: <b>info, not an error</b>, and the reason travels as an <c>lvs.</c>
    /// diagnostic beside it.</param>
    /// <param name="Technology">Which process the layout was read against (R-lvs8-1a) — a workspace
    /// holding two has a default that may not be the one the designer has in mind.</param>
    /// <param name="Reduction"><c>on</c> or <c>off</c>. On the face of the result either way
    /// (R-lvs8-1d), because a result whose reduction mode is not stated is one two people can read
    /// differently.</param>
    /// <param name="SubCells">Every distinct sub-cell this design placed and compared on its own
    /// account — one entry per CELL, not per placement (R-lvs9-1a). Their findings are already in
    /// <paramref name="Findings"/>, re-reported under the placement that put them there.</param>
    /// <param name="Turned">Every part on this cell's own layout placed end for end, in full —
    /// brief LVS 16. The <c>lvs.device.turned</c> findings are capped per id; this is not.</param>
    public sealed record LvsCellJson(
        string                          Path,
        string                          Name,
        bool                            Compared,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                         Technology,
        string                          Reduction,
        LvsSideCountsJson               Schematic,
        LvsSideCountsJson               Layout,
        int                             Errors,
        int                             Warnings,
        int                             Waived,
        bool                            Clean,
        int                             Extractions,
        int                             CacheHits,
        IReadOnlyList<string>           SubCells,
        IReadOnlyList<LvsFindingJson>   Findings,
        IReadOnlyList<LvsTurnedPartJson> Turned);

    /// <summary>
    /// What <c>impedance</c> analysed and what it found — a projection of the Trace Impedance
    /// Analysis report. Coordinates and lengths are in µm; impedances in Ω.
    /// </summary>
    public sealed record ImpedanceReportJson(
        string                             Title,
        string                             Technology,
        double                             TargetOhms,
        double                             TolerancePercent,
        double                             WarningPercent,
        double?                            MaxFrequencyHz,
        string                             Scope,
        bool                               Cancelled,
        int                                Traces,
        int                                Pass,
        int                                WarningCount,
        int                                Fail,
        IReadOnlyList<ImpedanceLayerJson>  Layers,
        int                                Accepted = 0,
        IReadOnlyList<ImpedanceStaleAcceptanceJson>? StaleAcceptances = null);

    /// <summary>An acceptance saved on the layout that matched no finding in this run — the trace was
    /// moved or re-routed, or the finding is gone. Listed, never removed by a run.</summary>
    /// <param name="Key">The stored key: <c>layer|x,y|x,y|Kind</c>, the trace's end points in whole µm.</param>
    /// <param name="Summary">The finding's text when it was accepted.</param>
    public sealed record ImpedanceStaleAcceptanceJson(
        string Key, string Kind, string Layer, string Summary, string Reason, DateTime Date);

    /// <summary>Why a finding was accepted in the editor, and when (brief-impedance-5). An accepted finding is
    /// still listed; it does not count against its trace's verdict or the exit code.</summary>
    public sealed record ImpedanceAcceptedJson(string Reason, DateTime Date);

    /// <param name="OutOfScope">Traces found on the layer and left out by the scope — counted, not
    /// listed.</param>
    public sealed record ImpedanceLayerJson(
        string                             Layer,
        int                                PoursSkipped,
        int                                OutOfScope,
        IReadOnlyList<ImpedanceTraceJson>  Traces);

    /// <param name="Verdict"><c>pass</c>, <c>warning</c>, <c>fail</c> or <c>unsolved</c>.</param>
    /// <param name="StartsAt">What the start is: <c>via</c>, <c>pad</c>, <c>junction</c>,
    /// <c>open end</c> or <c>continues</c>. Likewise <paramref name="EndsAt"/>.</param>
    /// <param name="InTolerance">The share of the solved length inside the pass band, 0–1.</param>
    /// <param name="Configuration">The line type over most of the trace.</param>
    /// <param name="Types">Every line type the trace is along some of its length, most first — a
    /// trace that is grounded CPW where its side ground runs beside it and microstrip where it
    /// falls away is both, and says so.</param>
    public sealed record ImpedanceTraceJson(
        string                             Id,
        string                             Verdict,
        double[]                           Start,
        double[]                           End,
        string                             StartsAt,
        string                             EndsAt,
        double                             Length,
        double                             WidthMin,
        double                             WidthMax,
        double?                            Z0Min,
        double?                            Z0Max,
        double?                            Z0Mean,
        double                             InTolerance,
        string                             Configuration,
        IReadOnlyList<ImpedanceTypeJson>   Types,
        IReadOnlyList<string>              References,
        IReadOnlyList<ImpedanceIssueJson>  Issues,
        IReadOnlyList<string>              Notes);

    /// <summary>One line type a trace is along part of its length — microstrip, grounded coplanar
    /// waveguide, stripline… — and the share of its solved length it is, 0–1.</summary>
    public sealed record ImpedanceTypeJson(string Type, double Share);

    /// <param name="Kind"><c>out-of-tolerance</c>, <c>return-broken</c>, <c>partial-reference</c>,
    /// <c>reference-step</c>, <c>no-reference</c> or <c>unsolved</c>.</param>
    /// <param name="Severity"><c>warning</c> or <c>fail</c>.</param>
    /// <param name="Accepted">The acceptance covering this finding, or absent.</param>
    public sealed record ImpedanceIssueJson(
        string Kind, string Severity, double[] From, double[] To, string Message,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ImpedanceAcceptedJson? Accepted = null);

    /// <summary>
    /// What <c>impedance --survey</c> found: the traces on each layer grouped by width, with nothing
    /// solved but one typical cut per class — the headless way to choose <c>--width</c>. Widths and
    /// lengths in µm.
    /// </summary>
    public sealed record ImpedanceSurveyJson(
        string                                 Title,
        string                                 Technology,
        int                                    Solves,
        IReadOnlyList<ImpedanceSurveyLayerJson> Layers);

    public sealed record ImpedanceSurveyLayerJson(
        string                                 Layer,
        int                                    PoursSkipped,
        IReadOnlyList<ImpedanceWidthClassJson> Classes);

    /// <param name="Width">The class's nominal width — the length-weighted mean of its traces'
    /// dominant widths.</param>
    /// <param name="TypicalZ0">ONE cross-section at the middle of the class's longest trace: typical,
    /// not the class's Z0.</param>
    public sealed record ImpedanceWidthClassJson(
        double  Width,
        double  WidthMin,
        double  WidthMax,
        int     Traces,
        double  Length,
        double? TypicalZ0,
        string? TypicalRefusal);

    /// <summary>
    /// What <c>impedance --tech … --layer …</c> (the line calculator) answered: per row, the circuit
    /// model's answer (an elaborated MLIN, CPWG or SLIN) beside the quasi-static cross-section's (what
    /// <c>impedance</c> reports on a drawn line), with nothing drawn. Lengths in µm, loss in dB/mm.
    /// </summary>
    /// <param name="Ground">The conductor the MODEL's substrate is measured to; absent with no
    /// substrate (then <paramref name="SubstrateRefusal"/> says why).</param>
    /// <param name="GapUm">A coplanar line's gap to its ground either side; absent for a microstrip.</param>
    /// <param name="FreqHz">Where the dispersive values, loss and λg are given; absent when not asked.</param>
    /// <param name="ModelComponent">The circuit model the model column is: MLIN, CPWG (a gap was given) or
    /// SLIN (the layer has a plane on both sides); absent when no component models the line.</param>
    /// <param name="SubstrateHeight2Um">A stripline's dielectric to the plane below; then
    /// <paramref name="SubstrateHeightUm"/> is to the plane above and <paramref name="Ground"/> names both.</param>
    public sealed record ImpedanceLineJson(
        string                               Technology,
        string                               Layer,
        string                               Conductor,
        string?                              Ground,
        double?                              SubstrateHeightUm,
        double?                              ConductorThicknessUm,
        double?                              Er,
        double?                              TanD,
        double?                              SigmaSPerM,
        string?                              SubstrateRefusal,
        double?                              GapUm,
        double?                              FreqHz,
        IReadOnlyList<ImpedanceLineRowJson> Rows,
        IReadOnlyList<string>                Warnings,
        string?                              ModelComponent = null,
        double?                              SubstrateHeight2Um = null);

    /// <param name="TargetZ0">The impedance a width was synthesised for; absent on a row asked by width.</param>
    /// <param name="CrossSectionAtModelWidth">A synthesis row only: the cross-section of the MODEL's
    /// width — what a line drawn at the width the schematic uses would measure.</param>
    /// <param name="Z0DifferencePercent">Cross-section static Z0 against the model's static Z0 at the
    /// SAME width (the model's, on a synthesis row), percent of the model's.</param>
    /// <param name="WidthDifferencePercent">A synthesis row only: the cross-section's width against the
    /// model's, percent of the model's.</param>
    /// <param name="Solves">Cross-section analyses the row took.</param>
    public sealed record ImpedanceLineRowJson(
        double?                    TargetZ0,
        ImpedanceLineModelJson?    Model,
        string?                    ModelRefusal,
        ImpedanceLineSectionJson?  CrossSection,
        ImpedanceLineSectionJson?  CrossSectionAtModelWidth,
        double?                    Z0DifferencePercent,
        double?                    WidthDifferencePercent,
        int                        Solves);

    /// <param name="Z0">Dispersive Z0 at the frequency (Kirschning-Jansen), what a run stamps; absent
    /// without a frequency.</param>
    public sealed record ImpedanceLineModelJson(
        double                WidthUm,
        double                Z0Static,
        double                EeffStatic,
        double?               Z0,
        double?               Eeff,
        double?               LossDbPerMm,
        double?               ConductorLossDbPerMm,
        double?               DielectricLossDbPerMm,
        double?               LambdaGUm,
        IReadOnlyList<string> Warnings);

    /// <param name="Z0">The quasi-static Z0; the solve has no dispersion and no loss.</param>
    /// <param name="LambdaGUm">From the quasi-static εeff at the frequency.</param>
    public sealed record ImpedanceLineSectionJson(
        double                WidthUm,
        double?               Z0,
        double?               Eeff,
        double?               LambdaGUm,
        string                Configuration,
        string?               Refusal,
        IReadOnlyList<string> Notes);

    /// <summary>
    /// What <c>recognize</c> read off a board and what it wrote (brief-artsch-7 R-as7-5). A projection of the
    /// recognition's own result: the report's classes as the recognition stated them, and the parts table
    /// row for row in the CSV's own columns — the table <c>--parts-out</c> writes and <c>--parts</c> reads.
    /// </summary>
    /// <param name="Layout">The <c>.clay</c> recognised.</param>
    /// <param name="Scope"><c>whole</c>, or the <c>--region</c> as given.</param>
    /// <param name="Instances">How many instances the circuit holds; 0 when it was refused.</param>
    /// <param name="Parts">One object per part, keyed by the parts table's column names.</param>
    /// <param name="Schematic">The <c>.csch</c> written under <c>--into</c>, or null.</param>
    /// <param name="Cell">The cell it was written into, or null.</param>
    /// <param name="Netlist">The <c>.cnl</c> written under <c>-o</c>, or null.</param>
    /// <param name="PartsTable">The CSV written under <c>--parts-out</c>, or null.</param>
    /// <param name="CheckpointTaken">A history checkpoint was taken before a replace.</param>
    public sealed record RecognizeReportJson(
        string                                             Layout,
        string?                                            Technology,
        string                                             Scope,
        int                                                Instances,
        IReadOnlyList<RecognizeFindingJson>                Report,
        IReadOnlyList<IReadOnlyDictionary<string, string>> Parts,
        string?                                            Schematic,
        string?                                            Cell,
        string?                                            Netlist,
        string?                                            PartsTable,
        bool                                               CheckpointTaken);

    /// <summary>One class of the recognition's report: how many, its sentence, and the places it is about (DBU).</summary>
    public sealed record RecognizeFindingJson(
        string                             Class,
        int                                Count,
        string                             Sentence,
        IReadOnlyList<RecognizeAnchorJson> Anchors);

    /// <summary>A place, DBU, with its drawing layer as <c>layer/datatype</c> when one is known.</summary>
    public sealed record RecognizeAnchorJson(
        long    X,
        long    Y,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Layer);

    /// <summary>
    /// What <c>lvs</c> compared, and what it concluded.
    /// </summary>
    /// <param name="Severity">The threshold the exit code was decided at — <c>warning</c> or
    /// <c>error</c>. Carried for <c>check</c>'s own reason: a document holding warnings and
    /// <c>exitCode: 0</c> is only readable next to the threshold that made it so.</param>
    /// <param name="Flat"><c>--flat</c>: every placed cell read as a leaf (R-lvs9-4a).</param>
    /// <param name="Reduce">False for <c>--no-reduce</c>. Stated at the run level as well as per
    /// cell, because it is what a caller comparing two runs' counts needs first.</param>
    /// <param name="Skipped">How many cells held only one of the two views.</param>
    public sealed record LvsReportJson(
        string                      Root,
        string                      Kind,
        string                      Severity,
        bool                        Flat,
        bool                        Reduce,
        bool                        Testbench,
        int                         Compared,
        int                         Skipped,
        int                         Errors,
        int                         Warnings,
        int                         Waived,
        bool                        Clean,
        IReadOnlyList<LvsCellJson>  Cells);

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
        ExplainNdfJson?       Ndf = null,
        /// <summary>Under which <c>analyses=</c> scopes an optimization runs this chain — <c>goals</c>
        /// (a goal names it, or an analysis it wraps), <c>all</c> — and which scope the design's
        /// optimize line chose, last, as <c>setup=goals</c> or <c>setup=all</c> (brief-tuneopt-11
        /// R-to11-7). Null when the design has no enabled goal.</summary>
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? Optimize = null,
        /// <summary>The points an optimization evaluates each candidate at when its optimize line names corners —
        /// <c>nominal</c> and the corners — so its count is the evaluations one point costs (brief-yield-7 R-ya7-5).
        /// Null without corners, or where <see cref="Optimize"/> is.</summary>
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? OptimizeAt = null);

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
        string? Note = null)
    {
        /// <summary>brief-em3d-42 — a 3D view's z bound and depth, metres like the rest; null for a drawing.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Z0 { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Z1 { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Depth { get; init; }

        /// <summary>brief-em3d-42 — a 3D view's bound spelled in its display unit, <c>x0,y0,z0,x1,y1,z1</c>, each
        /// coordinate with its unit (LayoutUnits.Spell); null for a drawing, whose spelling is <see cref="Window"/>.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Bounds { get; init; }
    }

    /// <summary>
    /// One component's footprint, as <c>explain --footprints</c> reports it
    /// (brief-footprint-4 R-fp4-4b): what it STATES, what that resolved to, how many pads, and —
    /// for a built-in — the technology the pattern would be generated against.
    /// </summary>
    /// <param name="Walk">How the answer was reached, not only what it was. Resolution here is a
    /// walk like every other one in <c>explain</c>, and which step produced the answer is the part
    /// a caller cannot otherwise see.</param>
    /// <param name="Pads">-1 where nothing resolved, so a missing answer cannot be read as zero
    /// pads.</param>
    /// <param name="Technology">Only for a built-in, whose artwork does not exist until it is
    /// generated: a land pattern resolves its copper, mask and silkscreen BY ROLE against this, and
    /// the shipped technologies disagree about every layer key. Absent for a cell, whose artwork is
    /// already on disk on keys of its own.</param>
    public sealed record ExplainFootprintJson(
        string  Component,
        string  Stated,
        string  State,
        string  Walk,
        int     Pads,
        int     Ports,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ResolvedTo,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Technology,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Refusal);

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
        ExplainExtentsJson?                 Extents = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ExplainFootprintJson>? Footprints = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ExplainEm3dJson?                    Em3d = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ExplainSolvedJson>?   Solved = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ExplainTunablesJson?                Tunables = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ExplainStatisticsJson?              Statistics = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ExplainDistributionsJson?           Distributions = null);

    /// <summary>
    /// <c>explain --analysis</c>: the distribution calls the design holds — a kit's models, a VAR's
    /// <c>agauss(…)</c> — grouped as process (one draw per trial, shared) and mismatch (one per instance), each
    /// with its stream (docs/design/yield.md §7). Every ordinary run evaluates them at their nominal.
    /// </summary>
    public sealed record ExplainDistributionsJson(
        int Process,
        int Mismatch,
        IReadOnlyList<ExplainDistributionJson> Streams);

    /// <summary>One distribution call: its function, <c>process</c> or <c>mismatch</c>, and its stream.</summary>
    public sealed record ExplainDistributionJson(string Function, string Kind, string Stream);

    /// <summary>
    /// <c>explain --tunables</c> — every value of a schematic that can be tuned, at any depth
    /// (docs/design/tuning-optimization.md), and the keys its tuning setup names that resolve to nothing.
    /// </summary>
    public sealed record ExplainTunablesJson(
        IReadOnlyList<ExplainTunableJson> Tunables,
        IReadOnlyList<string>             Unresolved);

    /// <summary>One tunable.</summary>
    /// <param name="Key">The identity a tune line, a preset and the CLI use: <c>R1.R</c>, <c>Wline</c>, <c>DUT:R3.R</c>.</param>
    /// <param name="Location"><c>top</c>, or the cell and how many instances share it: <c>DUT · ×2</c>.</param>
    /// <param name="Kind"><c>parameter</c>, <c>variable</c> or <c>cellParameter</c> (an instance's own declared parameter).</param>
    /// <param name="Value">The value as the design holds it.</param>
    /// <param name="Number">The number in <paramref name="Unit"/>.</param>
    /// <param name="IsDefault">An inherited cell parameter: the value is the cell's default.</param>
    /// <param name="ReadOnly">Why Push cannot write it; absent when it can.</param>
    /// <param name="Disabled">Why it cannot be moved right now; absent when it can.</param>
    /// <param name="Min">The entry's min when the setup has one, else the default range's.</param>
    /// <param name="Max">The entry's max, else the default range's.</param>
    /// <param name="RangeGuessed">No entry, and the value is zero, so the default range is a guess.</param>
    /// <param name="Tune">The setup offers it in the Tuning window.</param>
    /// <param name="Opt">The setup varies it in the optimizer.</param>
    /// <param name="Whole">A part of a complex value (<c>mag(ZL)</c>): the whole value as the design holds
    /// it (<c>40+15j Ohm</c>), which is what a preset stores. Null for a real value.</param>
    public sealed record ExplainTunableJson(
        string  Key,
        string  Location,
        int     InstanceCount,
        string  Kind,
        string  Value,
        double  Number,
        string  Unit,
        bool    Integer,
        bool    IsDefault,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ReadOnly,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Disabled,
        string  Min,
        string  Max,
        bool    RangeGuessed,
        bool    Tune,
        bool    Opt,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Whole = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ExplainStatJson? Stat = null);

    /// <summary>
    /// A tunable's statistical part (docs/design/yield.md): its distribution in words and numbers. The
    /// spread is in base SI (<paramref name="Unit"/>) and, in <paramref name="Written"/>, as written —
    /// a percent of the nominal or a value with its unit.
    /// </summary>
    /// <param name="Distribution"><c>gauss</c>, <c>unif</c>, <c>lognorm</c> or <c>discrete</c>.</param>
    /// <param name="On">Drawn in a trial; false for <c>stat=0</c>.</param>
    /// <param name="Text">The spread in words: <c>gauss σ = 1 Ohm (2 %)</c>.</param>
    /// <param name="Sigma">gauss, lognorm: 1σ.</param>
    /// <param name="Lo">unif, discrete: the lower end.</param>
    /// <param name="Hi">unif, discrete: the upper end.</param>
    /// <param name="Step">discrete: the step.</param>
    /// <param name="Trunc">gauss, lognorm: the truncation, in σ.</param>
    public sealed record ExplainStatJson(
        string Distribution,
        bool   On,
        string Text,
        string Unit,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Sigma,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Lo,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Hi,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Step,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Trunc,
        IReadOnlyDictionary<string, string> Written);

    /// <summary>
    /// <c>explain --analysis</c>'s statistical part (docs/design/yield.md): the effective statistics
    /// settings, the goals by what they serve, the corners, the correlation matrix a run would use, and
    /// what the configured trial count can resolve — the expected half-width of the yield interval at a
    /// yield of <paramref name="AtYield"/> %, and the trial count that brings it under ±2 %.
    /// </summary>
    public sealed record ExplainStatisticsJson(
        int     Trials,
        int     Seed,
        string  Sampling,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Target,
        double  Confidence,
        bool    AutoStop,
        string  NonConverged,
        string  Save,
        bool    Process,
        bool    Mismatch,
        double  SigmaScale,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int?    Parallel,
        string  Analyses,
        string  Corners,
        IReadOnlyList<string>                 StatisticalEntries,
        IReadOnlyList<ExplainGoalUseJson>     Goals,
        IReadOnlyList<ExplainCornerJson>      CornerList,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ExplainCorrelationJson?               Correlation,
        double  AtYield,
        double  ExpectedHalfWidth,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int?    TrialsForTwoPercent,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ExplainYieldRunJson? Run = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ExplainCenterJson? Center = null);

    /// <summary>
    /// What a design-centering run would cost (brief-yield-11 R-ya11-6), stated before anything runs: evaluations per
    /// iteration are the algorithm's first batch of candidates × M common trials. Present when the setup has a
    /// <c>center</c> line.
    /// </summary>
    /// <param name="Total">The start, the iteration limit's worth of iterations (or the evaluation limit) and both
    /// verification runs, in simulations.</param>
    /// <param name="Refusal">What the run would refuse, in its own words; the numbers are then absent.</param>
    public sealed record ExplainCenterJson(
        string  Algorithm,
        int     Trials,
        int     Verify,
        double  Width,
        int     Seed,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int?    Variables,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int?    PointsPerIteration,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long?   EvaluationsPerIteration,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long?   Total,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Estimate,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Refusal = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Surrogate = null);

    /// <summary>
    /// What a yield run would execute and cost (brief-yield-5 R-ya5-8): the analysis chains under <c>analyses=goals</c>
    /// and under <c>analyses=all</c>, which of the two the setup selects, and the trial cost — an ESTIMATE, in units of
    /// one nominal evaluation, since <c>explain</c> runs nothing to time one.
    /// </summary>
    /// <param name="Evaluations">The nominal plus every trial.</param>
    /// <param name="Batches">Trials ÷ parallelism, rounded up; the run takes about one more than this many nominal
    /// evaluations, the nominal running first and alone.</param>
    /// <param name="Refusal">What the run would refuse, in its own words; null when it would start.</param>
    public sealed record ExplainYieldRunJson(
        IReadOnlyList<string> UnderGoals,
        IReadOnlyList<string> UnderAll,
        string                Selected,
        int                   Evaluations,
        int                   Parallel,
        int                   Batches,
        string                Estimate,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?               Refusal = null);

    /// <summary>One goal and what it serves: <c>opt</c>, <c>yield</c> or <c>both</c>.</summary>
    public sealed record ExplainGoalUseJson(string Name, string Use, bool Enabled);

    /// <summary>One corner: <c>value</c> or <c>statistical</c>, and what it binds.</summary>
    public sealed record ExplainCornerJson(
        string Name,
        bool   Enabled,
        string Kind,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Temp,
        IReadOnlyDictionary<string, string> Values,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int?   Trial,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ExplainCornerBindingJson>? Bindings = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ExplainCornerAxisJson>?    KitAxes = null);

    /// <summary>One binding a corner makes (brief-yield-6 R-ya6-7): as written, and in base SI with its base unit —
    /// <c>temp</c> in °C, the ambient global's unit. <paramref name="Si"/> is null for a value that is not a number.</summary>
    public sealed record ExplainCornerBindingJson(
        string  Name,
        string  Written,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Si,
        string  Unit);

    /// <summary>A kit corner axis at one corner: the section in force, and whether the corner SETS it or inherits the
    /// schematic's own selection.</summary>
    public sealed record ExplainCornerAxisJson(string Axis, string Section, bool Sets);

    /// <summary>The correlation matrix a run uses, over <paramref name="Keys"/>; repaired to the nearest
    /// valid one when <paramref name="Repaired"/>, with the largest change that made.</summary>
    public sealed record ExplainCorrelationJson(
        IReadOnlyList<string>                Keys,
        IReadOnlyList<IReadOnlyList<double>> Matrix,
        bool   Repaired,
        double LargestChange);

    /// <summary>
    /// brief-em3d-98 R-em3d98-8 — one (setup, solver leg) of a 3D view: whether it has a result of the model as it is now
    /// (C3dSolveStatus, the one answer the editor's glyphs read too).
    /// </summary>
    /// <param name="Solver"><c>fem</c> (Palace), <c>fdtd</c> (openEMS) or <c>thermal</c>.</param>
    /// <param name="State"><c>notRun</c>, <c>current</c> or <c>outOfDate</c>.</param>
    /// <param name="Partial">The run was cancelled, did not converge, failed or was interrupted — independent of
    /// <paramref name="State"/>.</param>
    /// <param name="Running">A run of this leg is under way in a live process.</param>
    /// <param name="EndedAs">How the last run ended: <c>complete</c>, <c>cancelled</c>, <c>notConverged</c>, <c>failed</c>,
    /// <c>interrupted</c>; absent when there is no record.</param>
    /// <param name="Solved">When it finished, ISO 8601 with offset.</param>
    /// <param name="TookSeconds">How long it ran; absent for a run kept before brief 98.</param>
    /// <param name="StaleWhat">What changed since: <c>the model</c>, <c>'Board.clay'</c>…; absent unless out of date.</param>
    /// <param name="Detail">The run's own sentence (why it did not converge or failed).</param>
    public sealed record ExplainSolvedJson(
        string  Setup,
        string  Solver,
        string  State,
        bool    Partial,
        bool    Running,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? EndedAs,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Solved,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? TookSeconds,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? StaleWhat,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Detail);

    // ── explain on a 3D EM setup (brief-em3d-5 R-em3d5-3) ────────────────────

    /// <summary>
    /// A 3D setup, as <c>explain</c> reports it: the solver and §4.3's guidance for this geometry, the
    /// temperature, the materials and where each resolved from, every solid and sheet, the wires, the
    /// ports, the air box and the size of the run before it starts.
    ///
    /// <para><b>Every length is base SI, and <see cref="LengthUnit"/>/<see cref="LengthScale"/> say
    /// so</b> (R-em3d5-3e) — <c>explain</c>'s standing rule, because a scale read without its unit once
    /// produced a 2 Hz run. Conductivity is S/m and temperature °C, named in the field.</para>
    /// </summary>
    /// <param name="Solvers">Each program the setup's solver needs and what discovery found (brief-em3d-6
    /// R-em3d6-4c) — reported even when the problem could not be built.</param>
    /// <param name="Refusal">Why the 3D problem could not be built; every other row is then empty.</param>
    public sealed record ExplainEm3dJson(
        string                               Solver,
        string                               LengthUnit,
        double                               LengthScale,
        IReadOnlyList<Em3dGuidanceJson>      Guidance,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        Em3dTemperatureJson?                 Temperature,
        IReadOnlyList<Em3dMaterialJson>      Materials,
        IReadOnlyList<Em3dSolidJson>         Solids,
        IReadOnlyList<Em3dWireJson>          Wires,
        IReadOnlyList<Em3dPortJson>          Ports,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        Em3dAirBoxJson?                      AirBox,
        IReadOnlyList<Em3dSizeJson>          Size,
        IReadOnlyList<Em3dSolverJson>        Solvers,
        IReadOnlyList<string>                Notes,
        IReadOnlyList<string>                Warnings,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                              Refusal)
    {
        /// <summary>brief-em3d-22 — a static setup's problem, terminals, ground and floating conductors;
        /// null for a driven setup, so a driven report is unchanged.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Em3dStaticJson? Static { get; init; }

        /// <summary>brief-em3d-116 R-em3d116-3 — how openEMS builds each wave port (its feed, source, planes, current boxes),
        /// as sentences; null when the setup runs no wave port on openEMS, so every earlier report is unchanged.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<string>? OpenEmsWavePorts { get; init; }

        /// <summary>brief-em3d-115 R-em3d115-6 — per multi-terminal wave port on Palace, the route a run takes (each terminal's
        /// Mode, the Active entry, the face's one MaxSize) or the sentence a run refuses it with; null when the setup runs no
        /// terminal port on Palace, so every earlier report is unchanged.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<string>? PalaceTerminalPorts { get; init; }

        /// <summary>brief-em3d-23 R-em3d23-4a — an eigenmode problem's mode count and target; null otherwise.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Em3dEigenmodeJson? Eigenmode { get; init; }

        /// <summary>brief-em3d-64 R-em3d64-6b — the geometry kernel and each object it builds; null for a 3D view that
        /// holds no kernel object, so every earlier report is unchanged.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Em3dGeometryKernelJson? GeometryKernel { get; init; }
    }

    /// <summary>brief-em3d-64 R-em3d64-6b — whether the geometry kernel is here, where it was found and which OCCT it is;
    /// with it absent, the reason and the action.</summary>
    public sealed record Em3dGeometryKernelJson(
        bool Available,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HowFound,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? WorkerPath,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OcctVersion,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason,
        IReadOnlyList<Em3dKernelObjectJson> Objects);

    /// <summary>One kernel object: its operands as an indented tree, whether the worker built it or a cache answered
    /// (<c>built</c>/<c>cache</c>), its face and edge counts, the smallest radius of curvature on it (metres; null when
    /// every face and edge is flat), the kernel's notes, and its refusal when it did not build.</summary>
    public sealed record Em3dKernelObjectJson(
        string Name, string Kind, IReadOnlyList<string> Operands, string Build, int Faces, int Edges,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? MinRadiusM,
        IReadOnlyList<string> Notes,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Refusal);

    /// <summary>brief-em3d-23 — how many modes an eigenmode run finds, above which frequency, and where each
    /// setting came from ("field" or "default").</summary>
    public sealed record Em3dEigenmodeJson(int Count, string CountFrom, double TargetHz, string TargetFrom);

    /// <summary>brief-em3d-22 R-em3d22-2b — which conductors are in which terminal, which are the ground,
    /// and which float, with what floating means for this problem.</summary>
    public sealed record Em3dStaticJson(string Problem, IReadOnlyList<Em3dTerminalJson> Terminals,
                                        IReadOnlyList<string> Ground, IReadOnlyList<string> Floating, string FloatingMeans);

    /// <summary>One terminal: its matrix index, name, net, conductors and (magnetostatic) source port.</summary>
    public sealed record Em3dTerminalJson(int Index, string Name, string Net, IReadOnlyList<string> Conductors,
                                          [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Source);

    /// <summary>One sentence of em-3d.md §4.3's guidance, with the row it came from.</summary>
    public sealed record Em3dGuidanceJson(int Row, string Favours, string Sentence);

    /// <param name="From"><c>field</c> when the <c>.cem</c> states it, <c>default</c> otherwise.</param>
    /// <param name="NoAlpha">Materials stating σ₂₀ but no α₂₀: σ₂₀ is used at every temperature.</param>
    /// <param name="UnknownTemperature">Conductor stackup entries naming no material: their σ is used
    /// as given, at no known temperature.</param>
    public sealed record Em3dTemperatureJson(
        double OperatingTempC, string From, IReadOnlyList<Em3dConductivityJson> Conductors,
        IReadOnlyList<string> NoAlpha, IReadOnlyList<string> UnknownTemperature);

    public sealed record Em3dConductivityJson(string Material, double SigmaSm);

    /// <param name="From">Where the values resolved from: the technology's Materials, the
    /// <c>.wBond</c>'s, a stackup entry's own numbers, or free space.</param>
    public sealed record Em3dMaterialJson(
        string Name, double Epsr,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<double>? EpsrTensor,
        double TanD, double Mur, double SigmaSm, string From);

    /// <param name="Kind"><c>solid</c> or <c>sheet</c>.</param>
    /// <param name="SheetReason">Why the generator made it a sheet (brief 3 §5f).</param>
    /// <param name="ThicknessM">A sheet's real thickness, carried with it.</param>
    public sealed record Em3dSolidJson(
        string Name, string Kind, string Role, string Material, string Primitive, int Order,
        double X0, double Y0, double Z0, double X1, double Y1, double Z1,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? SheetReason,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? ThicknessM);

    /// <summary>
    /// One bond wire as the 3D model built it. <b>Both loop heights, labelled</b> (brief-em3d-4 §5b):
    /// <see cref="AssemblyLoopHeightM"/> is em-3d.md §6.6's (the wire's top surface at its apex minus
    /// the lower pad's top), <see cref="WBondLoopHeightM"/> is wBond's own (the axis polyline's rise).
    /// </summary>
    /// <param name="FootLengthFrom">Which level set the foot length: <c>wire</c>, <c>array</c>,
    /// <c>assembly-rules</c> or <c>built-in</c>.</param>
    public sealed record Em3dWireJson(
        string Name, string Material, string Section, double DiameterM,
        Em3dWireEndJson Start, Em3dWireEndJson End,
        double FootLengthM, string FootLengthFrom,
        double BallDiameterM, string BallDiameterFrom, double BallHeightM, string BallHeightFrom,
        double AssemblyLoopHeightM, double WBondLoopHeightM);

    public sealed record Em3dWireEndJson(
        string Style, string Pad,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? FootLengthM,
        bool Neck, double OverhangM);

    /// <param name="ReferenceNormal">Unit normal INTO the structure.</param>
    public sealed record Em3dPortJson(
        int Number, string Name, string Positive, string Negative, double Z0Re, double Z0Im,
        IReadOnlyList<double> Min, IReadOnlyList<double> Max,
        IReadOnlyList<double> ReferenceOrigin, IReadOnlyList<double> ReferenceNormal, double ReferenceShiftM)
    {
        /// <summary>brief-em3d-23 — "wave" for a wave port; null (omitted) for a lumped one.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Kind { get; init; }

        /// <summary>brief-em3d-23 R-em3d23-2c — where a wave port's reference plane is, in words; null for a
        /// lumped port, whose plane is its sheet.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ReferencePlane { get; init; }
    }

    /// <param name="Enlargements">What a backend asks to add outside a face, as sentences — openEMS's
    /// PML on each absorbing face (brief-em3d-8 R-em3d8-4c).</param>
    public sealed record Em3dAirBoxJson(
        IReadOnlyList<double> Min, IReadOnlyList<double> Max, IReadOnlyList<Em3dFaceJson> Faces,
        IReadOnlyList<string> Enlargements);

    /// <param name="PaddingM">The face's distance from the outermost geometry, metres.</param>
    /// <param name="From"><c>setup</c> when the <c>.cem</c>'s AirBox states the face, <c>default</c>
    /// otherwise, <c>floor</c> for the PEC floor on an undrawn ground plane.</param>
    public sealed record Em3dFaceJson(string Face, string Boundary, string From);

    /// <summary>
    /// One backend's size. <see cref="Kind"/> says which kind of number it is, never confused:
    /// <c>estimate</c> (Palace — a mesher decides the real count), <c>exact</c> (openEMS — the grid
    /// generator IS the grid), or <c>unavailable</c> with <see cref="Note"/> saying why. An
    /// unavailable row carries no count at all, never a zero.
    /// </summary>
    /// <param name="Elements">Palace's tetrahedra; openEMS's cells (the product of the three line counts,
    /// openEMS's own count).</param>
    /// <param name="CellsPerAxis">openEMS only (brief-em3d-8 R-em3d8-5c): grid LINES on x, y, z.</param>
    /// <param name="SmallestCellM">openEMS only: the smallest cell of the grid.</param>
    /// <param name="SmallestCellAxis">…on which axis (<c>x</c>, <c>y</c>, <c>z</c>).</param>
    /// <param name="SmallestCellFeatures">…and the features that set it, as phrases naming the objects.</param>
    /// <param name="Steps">openEMS only: time steps to cover the pulse and a nominal ring-down.</param>
    /// <param name="Merges">openEMS only: every pair of grid lines merged for being closer than
    /// MinCell, as a sentence naming both features.</param>
    /// <param name="GridWarnings">openEMS only: fixed lines closer than MinCell, which were kept.</param>
    /// <param name="Refusal">openEMS only: the sentence a run would stop with because the grid would not
    /// fit in memory.</param>
    public sealed record Em3dSizeJson(
        string Backend, string Kind,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long? Elements,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long? Unknowns,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? TimeStepS,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long? MemoryBytes,
        string Note,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<long>? CellsPerAxis = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? SmallestCellM = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? SmallestCellAxis = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? SmallestCellFeatures = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long? Steps = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? Merges = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? GridWarnings = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Refusal = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? Snaps = null);

    /// <summary>
    /// One program a 3D run needs, as discovery found it (brief-em3d-6 R-em3d6-4c): the same answer the
    /// run gets at its top and the Settings page shows. <see cref="Proceeds"/> is whether a run would get
    /// past this program; <see cref="Refusal"/> is the sentence it would stop with.
    /// </summary>
    /// <param name="Tool"><c>Palace</c>, <c>Gmsh</c> or <c>openEMS</c>.</param>
    /// <param name="Version">What the program printed — a git hash for Palace and openEMS.</param>
    /// <param name="Release">The validated release that version is, or null when it is not one.</param>
    /// <param name="HowFound"><c>settings</c>, <c>environment</c>, <c>path</c> or <c>default-directory</c>.</param>
    /// <param name="Rejected">Every candidate tried before the answer, and why it was not taken.</param>
    public sealed record Em3dSolverJson(
        string Tool, bool Found,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Path,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Version,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Release,
        bool Validated,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? HowFound,
        IReadOnlyList<Em3dCapabilityJson> Capabilities,
        IReadOnlyList<string> Rejected,
        bool Proceeds,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Refusal);

    /// <param name="FromCache">True when the answer was the cached one for this binary, so no probe ran.</param>
    public sealed record Em3dCapabilityJson(string Capability, bool Available, string Detail, bool FromCache)
    {
        /// <summary>brief-em3d-23 R-em3d23-1b — how the capability is asked: a dry run, or a one-element solve.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Probe { get; init; }
    }

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
    /// <param name="Pins">Where each terminal sits on the drawn symbol, relative to the component's
    /// <c>X</c>/<c>Y</c> at rotation <c>R0</c> with no mirror (y grows downward) — what a wire in a
    /// hand-written <c>.csch</c> must end on. Absent where the symbol's pins come from a referenced
    /// cell.</param>
    /// <param name="Validity">What the model is an estimate of and the range it is stated over,
    /// where the component's model is a closed-form estimate rather than an ideal element. Absent
    /// otherwise.</param>
    public sealed record ReferenceSymbolJson(
        string                                  Kind,
        string                                  DisplayName,
        string                                  Category,
        IReadOnlyList<string>                   SearchTerms,
        ReferencePortsJson                      Ports,
        IReadOnlyList<ReferenceParameterJson>   Parameters,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ReferencePinJson>?        Pins = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?                                 Validity = null);

    /// <summary>One terminal of a drawn symbol: its name and its position at R0.</summary>
    public sealed record ReferencePinJson(string Name, double X, double Y);

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
        IReadOnlyList<ReferenceSchemaTypeJson>? Schema  = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ReferenceComponentIndexJson>? ComponentIndex = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ReferenceTechnologyJson>? Technologies = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ReferenceMaterialJson>? Materials = null);

    /// <summary>One row of the component INDEX — the type token and enough to choose it, without its
    /// parameters. The full catalogue is ~150 kB; the index is what a client reads first.</summary>
    /// <param name="Nets">How many nets an instance line binds, when that is one number.</param>
    /// <param name="NetsSetBy">The parameter that sets it, when it is not one number.</param>
    /// <param name="Description">The palette's display names for the type, joined.</param>
    public sealed record ReferenceComponentIndexJson(
        string                Type,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int?                  Nets,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?               NetsSetBy,
        IReadOnlyList<string> Categories,
        string                Description,
        bool                  Simulatable,
        bool                  Placeable);

    /// <summary>One technology that ships with circuitRF, read from its embedded <c>.ctech</c> through
    /// the reader a user's own file goes through.</summary>
    /// <param name="Id">What <c>new workspace --tech</c> and the <c>create</c> tool's <c>tech</c>
    /// take.</param>
    /// <param name="Stackup">Top to bottom, as the technology orders it.</param>
    /// <param name="MaterialLibraries">The <c>.cmat</c> references the technology names.</param>
    /// <param name="Materials">Every material name a 3D view or stackup entry of this technology may
    /// use: its own records and its libraries'.</param>
    public sealed record ReferenceTechnologyJson(
        string                                  Id,
        string                                  Name,
        bool                                    IsDefault,
        string                                  DisplayUnit,
        string                                  TopBoundary,
        string                                  BottomBoundary,
        IReadOnlyList<ReferenceStackupLayerJson> Stackup,
        IReadOnlyList<string>                   MaterialLibraries,
        IReadOnlyList<string>                   Materials,
        int                                     DrcRules,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ReferenceLayerJson>?      Layers = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<ReferenceRecognitionRuleJson>? RecognitionRules = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>?                  Constants = null);

    /// <summary>One drawing layer, by the Key a <c>.clay</c> shape names it with.</summary>
    /// <param name="InStackup">Whether a Conductor or Via stackup entry draws on it. False is a
    /// drawn-only layer — a resistive film, a dielectric window, a recognition marker — which joins
    /// nothing electrically.</param>
    public sealed record ReferenceLayerJson(string Name, int Layer, int Datatype, bool InStackup);

    /// <summary>One rule of the technology's device-recognition deck, which <c>lvs --recognize</c>
    /// reads. Body and Terminals are layer expressions over Keys (<c>and(1/0, 20/0)</c>).</summary>
    public sealed record ReferenceRecognitionRuleJson(
        string                               Name,
        string                               Kind,
        string                               Body,
        IReadOnlyList<string>                Terminals,
        IReadOnlyDictionary<string, string>  Parameters,
        bool                                 CopperBody,
        bool                                 GroundTerminal);

    /// <param name="ThicknessUm">Micrometres. Absent on a via, whose length is its span.</param>
    /// <param name="DrawingLayers">The layout layers drawn on this entry, by name.</param>
    /// <param name="SpanFrom">A via's upper conductor, by stackup name.</param>
    /// <param name="SpanTo">A via's lower conductor, by stackup name.</param>
    public sealed record ReferenceStackupLayerJson(
        string                Kind,
        string                Name,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?               ThicknessUm,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?               Material,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?               Epsr,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?               TanD,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double?               SigmaSPerM,
        IReadOnlyList<string> DrawingLayers,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        bool                  GroundReference = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?               SpanFrom = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string?               SpanTo = null);

    /// <summary>One material record from a library that ships with circuitRF. Null means the record
    /// does not state it, exactly as in the <c>.cmat</c>.</summary>
    /// <param name="Role">conductor, dielectric, air, both stated, or neither stated — what the record's
    /// stated values make it.</param>
    public sealed record ReferenceMaterialJson(
        string  Name,
        string  Library,
        string  Role,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Epsr,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? TanD,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Mur,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Sigma20,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Alpha20,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? ThermalK,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Source);

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
        RenderDataDisplayJson?          DataDisplay = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RenderEm3dJson?                 Em3d = null);

    /// <summary>
    /// What <c>render</c> drew of a 3D EM setup (brief-em3d-5): which picture, where the plane
    /// landed after snapping to a boundary, and which objects it cut — the answer to "is the copper
    /// in this section" without reading SVG.
    /// </summary>
    /// <param name="View"><c>section</c> or <c>iso</c>.</param>
    /// <param name="Plane"><c>xy</c>, <c>xz</c> or <c>yz</c> for a section; <c>iso</c> otherwise.</param>
    /// <param name="At">The plane along its normal AFTER snapping, in <paramref name="Unit"/>; absent
    /// for the isometric view.</param>
    /// <param name="Objects">Every solid and sheet the picture draws, in draw (construction) order.</param>
    public sealed record RenderEm3dJson(
        string View,
        string Plane,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Axis,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? At,
        string Unit,
        double Scale,
        IReadOnlyList<string> Objects,
        IReadOnlyList<int> Ports,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RenderFieldJson? Field = null)
    {
        /// <summary>brief-em3d-89 — a projection's direction: the unit vector from the model toward the viewer; absent for a
        /// section or the isometric outline.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<double>? Toward { get; init; }

        /// <summary>brief-em3d-110 — a realistic picture's Look as it was drawn, and the CPU rasteriser's work; absent for a plain one.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RenderLookJson? Look { get; init; }
    }

    /// <summary>
    /// brief-em3d-110 — <c>render --look realistic</c>: the Look the picture was drawn with (the file's, with each <c>--look-set</c>
    /// override named), the camera it was taken from, and the work counted — samples shaded, shadow-map texels written, pixels the
    /// occlusion was computed for — rather than timed.
    /// </summary>
    /// <param name="Environment">The environment as the 3D view's status line names it: a studio, or the <c>.hdr</c>'s file name.</param>
    /// <param name="Fallback">Why the Look's <c>.hdr</c> was not used (the picture was lit with Studio), or absent.</param>
    /// <param name="Camera"><c>Look.Camera</c>, <c>--iso</c> or <c>--view-dir</c>: where the camera came from.</param>
    public sealed record RenderLookJson(
        string Environment,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Fallback,
        double Exposure,
        string FieldStyle,
        int Supersample,
        string Camera,
        string Projection,
        IReadOnlyList<string> Overrides,
        long SamplesShaded,
        long ShadowTexelsWritten,
        long OcclusionPixels);

    /// <summary>
    /// brief-em3d-84 R-em3d84-4 — the field plot a section was drawn with: which plot, which run it read (the walk a caller
    /// cannot otherwise see), which saved solution by value, the quantity, how many slice triangles and how many the picture
    /// drew them as (fewer when a vector page was thinned), the colour range, and whether the model has moved on since the run.
    /// </summary>
    /// <param name="Solution">The solution as the <c>.c3d</c> spells it; absent when the plot names none (the first saved).</param>
    /// <param name="Label">The solution as the 3D view labels it: <c>Mode 1: 5.73 GHz, Q 1.2e+04</c>.</param>
    /// <param name="Mode">How the quantity is read: Peak, Instantaneous, Value.</param>
    /// <param name="Phase">The phase drawn, degrees — present for an instantaneous quantity only.</param>
    /// <param name="Run">The run directory the fields were read from.</param>
    public sealed record RenderFieldJson(
        string Plot,
        string Setup,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Solver,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RenderFieldSolutionJson? Solution,
        string Label,
        string Quantity,
        string Mode,
        string On,
        int Triangles,
        int TrianglesDrawn,
        RenderFieldRangeJson Range,
        bool Stale,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Run,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        double? Phase)
    {
        /// <summary>brief-em3d-88 — a temperature's bond wires as drawn from their own T(s); absent for an EM field.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<RenderFieldWireJson>? Wires { get; init; }

        /// <summary>brief-em3d-88 — a temperature's thermal boundaries, each as the page labels it; absent for an EM field.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<string>? Boundaries { get; init; }

        /// <summary>brief-em3d-89 — a Surfaces or Faces plot's reflections: how many symmetry planes the modelled part was
        /// mirrored across (0 when it was drawn alone); absent for a section.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Mirrored { get; init; }

        /// <summary>brief-em3d-89 — a temperature surface plot's hottest point, as the legend says it; absent otherwise.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? HotSpot { get; init; }
    }

    /// <summary>
    /// brief-em3d-88 — one bond wire in a temperature section: how many pieces of it the picture drew, the temperatures they
    /// span (°C), and every point its centreline crosses the plane — in the picture's own (u, v), metres, with its arc length
    /// from the start heel (metres) and the T there, read from the run's T(s).
    /// </summary>
    public sealed record RenderFieldWireJson(string Wire, int Pieces, double Lo, double Hi, IReadOnlyList<RenderFieldCrossingJson> Crossings);

    /// <summary>Where a wire's centreline crosses a section's plane: (u, v) and s in metres, T in °C.</summary>
    public sealed record RenderFieldCrossingJson(double U, double V, double S, double T);

    /// <summary>A field plot's solution as the <c>.c3d</c> spells it: exactly one of GHz, Mode, Terminal or Point.</summary>
    public sealed record RenderFieldSolutionJson(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonPropertyName("ghz")] double? GHz,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Port,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Mode,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Terminal,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Point);

    /// <summary>The colour range drawn: its ends, the unit they are stated in (dBµV/m, dBµA/m or "dB re 1 …" when <paramref name="Db"/> — designer feedback round 11), and the percentile the top is at.</summary>
    public sealed record RenderFieldRangeJson(double Lo, double Hi, string Unit, bool Db, double Percentile);

    /// <summary>
    /// brief-em3d-84 R-em3d84-2 — one field plot of a <c>.c3d</c> as <c>render --list-fields</c> reports it: what it shows,
    /// where, from which setup, and whether its data is there — the R-em3d83-5 sentence when it is not — so an agent can find
    /// out what it can draw without opening the file.
    /// </summary>
    /// <param name="Problem">Why the plot has nothing to draw (R-em3d83-5), or absent when its data is there.</param>
    /// <param name="Headless">Why <c>render --field</c> will not draw it yet (a Surfaces, Faces or temperature plot), or absent.</param>
    public sealed record RenderFieldPlotJson(
        string Name,
        string Setup,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Solver,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        RenderFieldSolutionJson? Solution,
        string Describe,
        string Quantity,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Mode,
        string On,
        string Target,
        bool Hidden,
        bool DataPresent,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Problem,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Headless);

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
