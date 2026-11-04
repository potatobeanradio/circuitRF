using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Optimization;

/// <summary>
/// What an optimization run says about itself — its refusals (exit 1), the notes it makes once, and
/// the errors a goal can raise. RETURNED, never posted: the Optimizer window, the CLI and MCP decide
/// where each one goes.
/// </summary>
public static class OptimizationDiagnostics
{
    public static Diagnostic NoGoals() => Diagnostic.Create(
        "opt.nothing.goals", DiagnosticSeverity.Error,
        "Nothing to optimize: the design has no enabled goal.");

    /// <summary>Goals are enabled, but every one is a yield spec only — the check box alone does not say so.</summary>
    public static Diagnostic OnlyYieldGoals(string names) => Diagnostic.Create(
        "opt.nothing.goals-yield-only", DiagnosticSeverity.Error,
        "Nothing to optimize: every enabled goal is a yield spec only (use=yield), which the optimizer does not aim for: {names}. Set its use to opt or both.",
        ("names", names));

    public static Diagnostic NoVariables() => Diagnostic.Create(
        "opt.nothing.variables", DiagnosticSeverity.Error,
        "Nothing to optimize: no value is enabled for optimizing (opt=1 on a tune line).");

    public static Diagnostic VariableSwept(string key, string why) => Diagnostic.Create(
        "opt.variable.swept", DiagnosticSeverity.Error,
        "{key} cannot be optimized while it is {why}; disable that sweep or the value's opt flag.",
        ("key", key), ("why", why));

    /// <summary>overview D18: a complex value has two degrees of freedom.</summary>
    public static Diagnostic TooManyComplexParts(string parts, string count, string whole) => Diagnostic.Create(
        "opt.complex.too-many-parts", DiagnosticSeverity.Error,
        "{parts} are {count} parts of {whole}, which has two degrees of freedom; optimize at most two — the others' ranges still limit it.",
        ("parts", parts), ("count", count), ("whole", whole));

    public static Diagnostic ComplexNoStart(string whole, string held) => Diagnostic.Create(
        "opt.complex.no-start", DiagnosticSeverity.Error,
        "{whole}: no value inside every range of its parts can be reached by moving {held}; widen a range or optimize another part.",
        ("whole", whole), ("held", held));

    public static Diagnostic AlgorithmUnavailable(string algorithm, string available) => Diagnostic.Create(
        "opt.algorithm.unavailable", DiagnosticSeverity.Error,
        "algorithm={algorithm} is not available; choose one of {available}.",
        ("algorithm", algorithm), ("available", available));

    public static Diagnostic LeastSquaresOnly(string label) => Diagnostic.Create(
        "opt.algorithm.lsq-only", DiagnosticSeverity.Error,
        "{label} minimizes the sum of squared violations and cannot use cost=minimax; choose Minimax or Auto, or set cost=lsq.",
        ("label", label));


    public static Diagnostic StartMoved(string key, string value, string min, string max, string start) => Diagnostic.Create(
        "opt.start.outside", DiagnosticSeverity.Warning,
        "{key} = {value} is outside its range {min} .. {max}; the run starts at {start}.",
        ("key", key), ("value", value), ("min", min), ("max", max), ("start", start));

    public static Diagnostic ComplexStartMoved(string whole, string value, string start) => Diagnostic.Create(
        "opt.start.complex-outside", DiagnosticSeverity.Warning,
        "{whole} = {value} is outside the ranges of its parts; the run starts at {start}, the nearest value inside them.",
        ("whole", whole), ("value", value), ("start", start));

    public static Diagnostic RangeAssumed(string key, string min, string max) => Diagnostic.Create(
        "opt.range.assumed", DiagnosticSeverity.Info,
        "{key} states no range; the run uses {min} .. {max}.", ("key", key), ("min", min), ("max", max));

    // ── Discrete and preferred values (brief-tuneopt-8) ─────────────────────

    public static Diagnostic DiscreteOnPart(string key, string discrete) => Diagnostic.Create(
        "opt.discrete.part", DiagnosticSeverity.Error,
        "{key}: discrete={discrete} is not offered on a part of a complex value — a part is continuous; use step= instead.",
        ("key", key), ("discrete", discrete));

    public static Diagnostic PreferredNoLadder(string key, string unit) => Diagnostic.Create(
        "opt.discrete.no-ladder", DiagnosticSeverity.Error,
        "{key}: discrete=preferred needs a capacitance, an inductance or a resistance; a value in '{unit}' has no preferred-value ladder.",
        ("key", key), ("unit", unit));

    public static Diagnostic PreferredNoneInRange(string key, string min, string max) => Diagnostic.Create(
        "opt.discrete.none-in-range", DiagnosticSeverity.Error,
        "{key}: no preferred or series value lies in its range {min} .. {max}; widen the range or choose another series.",
        ("key", key), ("min", min), ("max", max));

    public static Diagnostic DiscreteOnlyParts() => Diagnostic.Create(
        "opt.discrete.only-parts", DiagnosticSeverity.Error,
        "Discrete needs integer, stepped or preferred values; every optimized value here is a part of a complex value, and a part is continuous.");

    public static Diagnostic DiscreteHasContinuous(string keys) => Diagnostic.Create(
        "opt.discrete.continuous", DiagnosticSeverity.Error,
        "Discrete needs every optimized value to be an integer, a step, a preferred value or an E series; these are continuous: {keys}. Give each discrete= or step=, or choose another algorithm.",
        ("keys", keys));

    // ── Vendor parts (brief vendorlib-4) ────────────────────────────────────

    public static Diagnostic PartNoCandidates(string key, string selection, string summary) => Diagnostic.Create(
        "opt.part.no-candidates", DiagnosticSeverity.Error,
        "{key} has no part to choose among ({selection}: {summary}).",
        ("key", key), ("selection", selection), ("summary", summary));

    public static Diagnostic PartStartMoved(string key, string placed, string start) => Diagnostic.Create(
        "opt.part.start-moved", DiagnosticSeverity.Info,
        "{key}: the placed part {placed} is not among its candidates; the search starts from {start}, the nearest by value.",
        ("key", key), ("placed", placed), ("start", start));

    /// <summary>R-vl4-5b: a continuous-only algorithm on a problem of vendor parts alone.</summary>
    public static Diagnostic PartsOnlyDiscrete(string algorithm, string keys) => Diagnostic.Create(
        "opt.part.discrete-only", DiagnosticSeverity.Error,
        "{algorithm} searches continuous values, and every optimized value here is a vendor part ({keys}), whose levels are real parts with nothing between them. Choose Discrete or Auto.",
        ("algorithm", algorithm), ("keys", keys));

    /// <summary>R-vl4-4b: a chosen part whose stated frequency range the analyses leave — said once, for the chosen part only.</summary>
    public static Diagnostic ChosenPartRange(string sentence) => Diagnostic.Create(
        "opt.part.range", DiagnosticSeverity.Warning, "{sentence}", ("sentence", sentence));

    /// <summary>R-vl4-3b: what the ideal stand-ins hid — the cost with them, and with the real parts snapped in.</summary>
    public static Diagnostic PartSnapReport(int parts, double ideal, double real) => Diagnostic.Create(
        "opt.part.snap-report", DiagnosticSeverity.Info,
        "With ideal components in place of the {parts} vendor part(s) the cost was {ideal}; with the real parts snapped in it is {real}. The difference is what the parts' own parasitics do.",
        ("parts", parts), ("ideal", ideal.ToString("G4", System.Globalization.CultureInfo.InvariantCulture)),
        ("real", real.ToString("G4", System.Globalization.CultureInfo.InvariantCulture)));

    public static Diagnostic SnapReport(int snapped, double before, double snappedCost, double after, bool polished) => Diagnostic.Create(
        "opt.snap.report", DiagnosticSeverity.Info,
        polished
            ? "Snapping {snapped} value(s) to legal values moved the cost from {before} to {snappedCost}; polishing the continuous values brought it to {after}."
            : "Snapping {snapped} value(s) to legal values moved the cost from {before} to {snappedCost}.",
        ("snapped", snapped), ("before", before.ToString("G4", System.Globalization.CultureInfo.InvariantCulture)),
        ("snappedCost", snappedCost.ToString("G4", System.Globalization.CultureInfo.InvariantCulture)),
        ("after", after.ToString("G4", System.Globalization.CultureInfo.InvariantCulture)));

    public static Diagnostic NothingToSnap() => Diagnostic.Create(
        "opt.snap.nothing", DiagnosticSeverity.Error,
        "Snap and polish needs an integer, stepped or preferred value among the optimized values, and a run that found a point.");

    /// <summary>brief-sar-10 R-sar10-2 — no cost hint exists for an algorithm to read, so the expensive goals are said.</summary>
    public static Diagnostic FullAverageGoals(string names) => Diagnostic.Create(
        "opt.goal.full-average", DiagnosticSeverity.Info,
        "{names} read psSAR: every evaluation that scores them recombines the whole field set and averages it in full, never an " +
        "approximation, and that average is most of the evaluation's cost. B1, SARwb and Pabs read only their region or the tissue matrix.",
        ("names", names));

    public static Diagnostic Serial(string reason) => Diagnostic.Create(
        "opt.parallel.serial", DiagnosticSeverity.Info,
        "Evaluating one point at a time: {reason}", ("reason", reason));

    public static Diagnostic NoneConverged(long count, string first) => Diagnostic.Create(
        "opt.none-converged", DiagnosticSeverity.Error,
        "No evaluation converged ({count} tried). The first failure: {first}",
        ("count", count), ("first", first));

    // ── Goals (R-to6-2) ─────────────────────────────────────────────────────

    public static Diagnostic GoalComplex(string goal) => Diagnostic.Create(
        "opt.goal.complex", DiagnosticSeverity.Error,
        "goal {goal}: the value is complex, and a goal compares real numbers. Write dB(…), mag(…), phase(…) or real(…) of it.",
        ("goal", goal));

    public static Diagnostic GoalNotANumber(string goal) => Diagnostic.Create(
        "opt.goal.kind", DiagnosticSeverity.Error,
        "goal {goal}: the value is not a number.", ("goal", goal));

    public static Diagnostic GoalNoAxis(string goal, string axis, string axes) => Diagnostic.Create(
        "opt.goal.no-axis", DiagnosticSeverity.Error,
        "goal {goal}: the value has no '{axis}' axis. Its axes are: {axes}.",
        ("goal", goal), ("axis", axis), ("axes", axes));

    public static Diagnostic GoalScalarRange(string goal, string axis) => Diagnostic.Create(
        "opt.goal.scalar-range", DiagnosticSeverity.Error,
        "goal {goal}: the value is a single number, so it has no '{axis}' axis to take a range over.",
        ("goal", goal), ("axis", axis));

    public static Diagnostic GoalRangeEmpty(string goal, string axis, double lo, double hi, double min, double max) => Diagnostic.Create(
        "opt.goal.range-empty", DiagnosticSeverity.Error,
        "goal {goal}: no '{axis}' point lies in [{lo}, {hi}]; the axis runs from {min} to {max}.",
        ("goal", goal), ("axis", axis), ("lo", lo), ("hi", hi), ("min", min), ("max", max));

    public static Diagnostic GoalLimitNotANumber(string goal, string text) => Diagnostic.Create(
        "opt.goal.limit", DiagnosticSeverity.Error,
        "goal {goal}: the limit '{text}' is not a number.", ("goal", goal), ("text", text));

    public static Diagnostic GoalNonFinite(string goal) => Diagnostic.Create(
        "opt.goal.non-finite", DiagnosticSeverity.Error,
        "goal {goal}: the value is not finite at some point of its range.", ("goal", goal));

    public static Diagnostic GoalFailed(string goal, string message) => Diagnostic.Create(
        "opt.goal.failed", DiagnosticSeverity.Error,
        "goal {goal}: {message}", ("goal", goal), ("message", message));

    public static Diagnostic EvaluationFailed(string message) => Diagnostic.Create(
        "opt.evaluation.failed", DiagnosticSeverity.Error, "{message}", ("message", message));
    // ── brief-sdl-7: optimising geometry by EM ──────────────────────────────

    public static Diagnostic EmNotInvolved(string mode) => Diagnostic.Create(
        "opt.em.not-involved", DiagnosticSeverity.Info,
        "em={mode}: no goal reads an analysis simulated by EM and no part is set to EM, so the run uses circuit models.",
        ("mode", mode));

    /// <summary>brief-sdl-10 D2 — em= unstated, and an EM part has no circuit model: the run selects direct.</summary>
    public static Diagnostic EmDirectForNoCircuitModel(string parts) => Diagnostic.Create(
        "opt.em.no-circuit-model", DiagnosticSeverity.Info,
        "{parts} has no circuit model (a 3D-parametric cell with no schematic), so em=correct has nothing to search on: the run " +
        "uses em=direct, every evaluation an EM solve.", ("parts", parts));

    /// <summary>brief-sdl-10 D2 — em=correct or em=off asked for, and an EM part has no circuit model.</summary>
    public static Diagnostic EmModeNeedsCircuitModel(string mode, string parts) => Diagnostic.Create(
        "opt.em.needs-circuit-model", DiagnosticSeverity.Error,
        "em={mode} needs a circuit model, and {parts} has none (a 3D-parametric cell with no schematic): it can only be simulated " +
        "by EM. Use em=direct, or give the cell a schematic as its circuit model.", ("mode", mode), ("parts", parts));

    public static Diagnostic EmWithCorners(string mode) => Diagnostic.Create(
        "opt.em.corners", DiagnosticSeverity.Error,
        "em={mode} with corners: every corner of every point would be an EM solve. Optimise at the nominal by EM, or set em=off " +
        "to optimise across corners by circuit models.", ("mode", mode));

    public static Diagnostic EmStartRefused(string why) => Diagnostic.Create(
        "opt.em.start", DiagnosticSeverity.Error,
        "The EM evaluation at the start values cannot be made: {why} Nothing was solved.", ("why", why));

    /// <summary>D6 — a value the EM result never reads.</summary>
    public static Diagnostic EmNotThroughVariable(string key, string cell) => Diagnostic.Create(
        "opt.em.not-variable", DiagnosticSeverity.Error,
        "{key} does not reach the copper: the EM result of {cell}'s layout is built from the schematic's variables, and {key} " +
        "is not one. Give the part a variable (a VAR row) and optimise that, or set em=off.", ("key", key), ("cell", cell));

    /// <summary>D6 — SDL-6 D4's finding, as a refusal.</summary>
    public static Diagnostic EmUnreached(string key, string sentence) => Diagnostic.Create(
        "opt.em.unreached", DiagnosticSeverity.Error,
        "{key} cannot be optimised by EM: {sentence} Varying it would spend EM solves on no change. Take it out of the " +
        "optimisation, or restore the part's value from the schematic.", ("key", key), ("sentence", sentence));

    /// <summary>D6 — a variable whose whole range leaves the geometry unchanged.</summary>
    public static Diagnostic EmNoEffect(string key) => Diagnostic.Create(
        "opt.em.no-effect", DiagnosticSeverity.Error,
        "{key} does not change the copper anywhere in its range, so EM cannot see it vary. Take it out of the optimisation.",
        ("key", key));

    /// <summary>D9 — the start already disagrees with the schematic.</summary>
    public static Diagnostic EmStartDisagrees(string lines) => Diagnostic.Create(
        "opt.em.start-connectivity", DiagnosticSeverity.Error,
        "The layout at the start values does not agree with the schematic: {lines} Optimising its geometry would hold that " +
        "fixed. Correct the values or the layout first; nothing was solved.", ("lines", lines));

    public static Diagnostic EmConnectivityUnchecked() => Diagnostic.Create(
        "opt.em.connectivity-unchecked", DiagnosticSeverity.Info,
        "The layout's connectivity could not be measured, so candidate points are not checked for gaps or shorts.");

    /// <summary>brief-sdl-8 D8 — the default budget a 3D EM view takes.</summary>
    public static Diagnostic EmThreeDBudget(string mode, int budget) => Diagnostic.Create(
        "opt.em.budget-3d", DiagnosticSeverity.Info,
        "The EM view is a 3D setup, whose solves take minutes to hours: em={mode} stops at {budget} EM solves unless emBudget= " +
        "says otherwise.", ("mode", mode), ("budget", budget.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>brief-sdl-8 D7 — the step floor raised to the held 3D mesh's smallest element.</summary>
    public static Diagnostic EmMeshStepFloor(double metres) => Diagnostic.Create(
        "opt.em.mesh-step", DiagnosticSeverity.Info,
        "No length steps by less than {size}, the smallest element of the held 3D mesh: a smaller change is meshing noise, " +
        "not a slope.", ("size", CircuitRF.Engine.Em3d.FdtdGrid.FormatLength(metres)));

    /// <summary>D10.</summary>
    public static Diagnostic EmConnectedRange(string sentence) => Diagnostic.Create(
        "opt.em.connected-range", DiagnosticSeverity.Info, "{sentence}", ("sentence", sentence));

    public static Diagnostic EmOffsetMismatch(string goal) => Diagnostic.Create(
        "opt.em.offset", DiagnosticSeverity.Warning,
        "Goal '{goal}': its EM and circuit-model values do not line up sample for sample, so it is not corrected; the EM " +
        "solves still score it.", ("goal", goal));

    /// <summary>D2's limit, said where it bites.</summary>
    public static Diagnostic EmCorrectionShort(bool budget) => Diagnostic.Create(
        "opt.em.correction-short", DiagnosticSeverity.Info,
        (budget ? "The EM budget ran out before every goal was met. " : "The correction stopped before every goal was met on EM. ") +
        "em=correct lifts the circuit model's response by EM's offset; a response that EM moves in frequency is corrected " +
        "slowly. A short em=direct pass from this point (algorithm=pattern) searches on EM itself.");
}
