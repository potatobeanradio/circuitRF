using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Statistics;

/// <summary>
/// What <see cref="StatisticsValidator"/> says about a setup's statistical part (docs/design/yield.md) —
/// <c>check</c> reports these, and a Monte Carlo or yield run refuses on the errors among them.
/// <c>{who}</c> names the line: <c>tune R1.R</c>, <c>correlate R1.R R2.R</c>, <c>statistics</c>,
/// <c>corner SS_hot</c>, <c>goal G1</c>.
/// </summary>
public static class StatisticsDiagnostics
{
    public static Diagnostic NotTunable(string who) => Diagnostic.Create(
        "yield.dist.not-tunable", DiagnosticSeverity.Error,
        "{who}: a distribution needs a value this design offers for tuning, and this key names none.", ("who", who));

    public static Diagnostic SpreadMissing(string who, string dist, string needs) => Diagnostic.Create(
        "yield.spread.missing", DiagnosticSeverity.Error,
        "{who}: dist={dist} needs {needs}.", ("who", who), ("dist", dist), ("needs", needs));

    public static Diagnostic SpreadExtra(string who, string dist, string key) => Diagnostic.Create(
        "yield.spread.extra", DiagnosticSeverity.Error,
        "{who}: {key}= is not a spread of dist={dist}.", ("who", who), ("key", key), ("dist", dist));

    public static Diagnostic TolPartNotAPart(string who) => Diagnostic.Create(
        "yield.tol-part.not-a-part", DiagnosticSeverity.Error,
        "{who}: tol=part is a vendor part's own tolerance, and this is not a vendor part's value. Write tol= as a value or a percent.",
        ("who", who));

    public static Diagnostic TolPartUnknown(string who, string part, string reason) => Diagnostic.Create(
        "yield.tol-part.unknown", DiagnosticSeverity.Error,
        "{who}: tol=part, and the tolerance of {part} is not known — {reason}. Write tol= explicitly.",
        ("who", who), ("part", part), ("reason", reason));

    public static Diagnostic SpreadNotANumber(string who, string key, string text) => Diagnostic.Create(
        "yield.spread.not-a-number", DiagnosticSeverity.Error,
        "{who}: {key}={text} is not a number, a percent or a value with its unit.", ("who", who), ("key", key), ("text", text));

    public static Diagnostic SpreadWrongUnit(string who, string key, string text, string unit) => Diagnostic.Create(
        "yield.spread.wrong-unit", DiagnosticSeverity.Error,
        "{who}: {key}={text} is not in the value's own kind of unit, {unit} — write it in {unit}, or as a percent.",
        ("who", who), ("key", key), ("text", text), ("unit", unit));

    public static Diagnostic SpreadNotPositive(string who, string key, string text) => Diagnostic.Create(
        "yield.spread.not-positive", DiagnosticSeverity.Error,
        "{who}: {key}={text} must be above zero.", ("who", who), ("key", key), ("text", text));

    public static Diagnostic SpreadInverted(string who, string lo, string hi) => Diagnostic.Create(
        "yield.spread.inverted", DiagnosticSeverity.Error,
        "{who}: lo={lo} is not below hi={hi}.", ("who", who), ("lo", lo), ("hi", hi));

    public static Diagnostic StepNotDividing(string who, string by, string lo, string hi) => Diagnostic.Create(
        "yield.spread.step", DiagnosticSeverity.Error,
        "{who}: by={by} does not step from lo={lo} to hi={hi} a whole number of times.",
        ("who", who), ("by", by), ("lo", lo), ("hi", hi));

    public static Diagnostic LogNormNotPositive(string who, string value) => Diagnostic.Create(
        "yield.dist.lognorm-nonpositive", DiagnosticSeverity.Error,
        "{who}: dist=lognorm needs a value above zero, and the value is {value}.", ("who", who), ("value", value));

    public static Diagnostic ContinuousOnInteger(string who, string dist) => Diagnostic.Create(
        "yield.dist.integer", DiagnosticSeverity.Error,
        "{who}: the value takes whole numbers only, so dist={dist} cannot draw it; use discrete or unif.",
        ("who", who), ("dist", dist));

    public static Diagnostic MixedParts(string whole, string parts) => Diagnostic.Create(
        "yield.complex.mixed", DiagnosticSeverity.Error,
        "{whole}: tolerances on {parts} mix a rectangular and a polar part; a draw of the two describes no complex " +
        "number. Toleranced parts are real with imag, or mag with phase.",
        ("whole", whole), ("parts", parts));

    public static Diagnostic TooManyParts(string whole, string parts) => Diagnostic.Create(
        "yield.complex.too-many", DiagnosticSeverity.Error,
        "{whole}: tolerances on {parts}; at most two parts of one value carry one.", ("whole", whole), ("parts", parts));

    public static Diagnostic CorrelateNotStatistical(string who, string key) => Diagnostic.Create(
        "yield.correlate.not-stat", DiagnosticSeverity.Error,
        "{who}: {key} is not a statistical entry (a tune line with dist= and stat on).", ("who", who), ("key", key));

    public static Diagnostic CorrelateSameKey(string who) => Diagnostic.Create(
        "yield.correlate.same-key", DiagnosticSeverity.Error,
        "{who}: correlates a key with itself.", ("who", who));

    public static Diagnostic RhoOutOfRange(string who, double rho) => Diagnostic.Create(
        "yield.correlate.rho", DiagnosticSeverity.Error,
        "{who}: rho={rho} is not strictly between -1 and 1.", ("who", who), ("rho", rho));

    public static Diagnostic LhsWithAutoStop() => new(
        "yield.statistics.lhs-autostop", DiagnosticSeverity.Error,
        "statistics: sampling=lhs places every trial up front, so it cannot stop early; use sampling=sobol or random with autostop=1.");

    public static Diagnostic AutoStopNeedsTarget() => new(
        "yield.statistics.autostop-target", DiagnosticSeverity.Error,
        "statistics: autostop=1 stops when the yield is confidently above or below target=, and no target is given.");

    public static Diagnostic SettingOutOfRange(string key, string value, string range) => Diagnostic.Create(
        "yield.statistics.range", DiagnosticSeverity.Error,
        "statistics: {key}={value} is out of range; it must be {range}.", ("key", key), ("value", value), ("range", range));

    public static Diagnostic CornerUnknownKey(string who, string key) => Diagnostic.Create(
        "yield.corner.unknown-key", DiagnosticSeverity.Error,
        "{who}: {key} is neither a variable of this design nor a tunable key it offers.", ("who", who), ("key", key));

    public static Diagnostic CornerTempNotANumber(string who, string temp) => Diagnostic.Create(
        "yield.corner.temp", DiagnosticSeverity.Error,
        "{who}: temp={temp} is not a number of degC.", ("who", who), ("temp", temp));

    public static Diagnostic CornerTrialIncomplete(string who, string missing) => Diagnostic.Create(
        "yield.corner.trial-incomplete", DiagnosticSeverity.Error,
        "{who}: a statistical corner names its run too; {missing} is missing.", ("who", who), ("missing", missing));

    public static Diagnostic NonPhysical(string who, double probability) => Diagnostic.Create(
        "yield.dist.nonphysical", DiagnosticSeverity.Warning,
        "{who}: a draw at or below zero has probability {probability}; truncate it (trunc=) or use dist=lognorm.",
        ("who", who), ("probability", probability.ToString("G2", System.Globalization.CultureInfo.InvariantCulture)));

    public static Diagnostic DistributionOff(string who) => Diagnostic.Create(
        "yield.dist.off", DiagnosticSeverity.Warning,
        "{who}: stat=0 keeps the distribution but does not draw it.", ("who", who));

    public static Diagnostic NothingVaries(string goal) => Diagnostic.Create(
        "yield.goal.nothing-varies", DiagnosticSeverity.Warning,
        "goal {goal}: use=yield, and no entry is statistical and no kit statistics are selected, so every trial is the nominal.",
        ("goal", goal));

    public static Diagnostic CorrelationRepaired(double largestChange) => Diagnostic.Create(
        "yield.correlate.repair", DiagnosticSeverity.Warning,
        "The correlations are not a valid correlation matrix; the nearest one is used, which changes an entry by up to {change}.",
        ("change", largestChange.ToString("G3", System.Globalization.CultureInfo.InvariantCulture)));

    // ── YA-2: sampling and trial values ─────────────────────────────────────

    public static Diagnostic SobolPastTable(int dimensions, int rest) => Diagnostic.Create(
        "yield.sampling.sobol-dimensions", DiagnosticSeverity.Warning,
        "sampling=sobol has {dimensions} dimensions; the other {rest} statistical variable(s) are drawn at random.",
        ("dimensions", dimensions), ("rest", rest));

    public static Diagnostic TrialNonPhysical(string key, string value) => Diagnostic.Create(
        "yield.trial.nonphysical", DiagnosticSeverity.Error,
        "{key} draws {value}, which is not physical; the trial does not evaluate.", ("key", key), ("value", value));

    public static Diagnostic TrialNoNominal(string key) => Diagnostic.Create(
        "yield.trial.no-nominal", DiagnosticSeverity.Error,
        "{key} names no value of this design; the trial does not evaluate.", ("key", key));

    public static Diagnostic TrialNoDraw(string key) => Diagnostic.Create(
        "yield.trial.no-draw", DiagnosticSeverity.Error,
        "{key} has no draw in this trial's sample; the trial does not evaluate.", ("key", key));

    public static Diagnostic TrialNoDistribution(string key) => Diagnostic.Create(
        "yield.trial.no-distribution", DiagnosticSeverity.Error,
        "{key}: its spread does not resolve to a distribution at this nominal; the trial does not evaluate.", ("key", key));

    public static Diagnostic TrialNoComplexValue(string key) => Diagnostic.Create(
        "yield.trial.complex", DiagnosticSeverity.Error,
        "{key}: no complex value has the drawn parts; the trial does not evaluate.", ("key", key));

    // ── YA-3: kit and expression statistics ─────────────────────────────────

    public static Diagnostic RunNothingVaries() => Diagnostic.Create(
        "yield.run.nothing-varies", DiagnosticSeverity.Error,
        "Nothing varies: no tune entry has a tolerance (dist=) and the design holds no distribution call, so every trial would be the nominal.");

    public static Diagnostic StatisticalSectionNotSelected(string axis, string sections) => Diagnostic.Create(
        "yield.kit.section-not-selected", DiagnosticSeverity.Info,
        "Kit corner {axis}: statistical {sections} not selected, so its models do not vary.",
        ("axis", axis), ("sections", sections));

    public static Diagnostic TrialDrawFailed(string problem) => Diagnostic.Create(
        "yield.trial.draw-failed", DiagnosticSeverity.Error,
        "A distribution could not be drawn ({problem}); the trial does not evaluate.", ("problem", problem));

    // ── YA-4: the run ──────────────────────────────────────────────────────

    public static Diagnostic RunNoYieldGoal() => Diagnostic.Create(
        "yield.run.no-yield-goal", DiagnosticSeverity.Error,
        "A yield run needs an enabled goal with use=yield or use=both to pass or fail each trial against; run a Monte Carlo to see the spread alone.");

    public static Diagnostic RunNominalFailed(string reason) => Diagnostic.Create(
        "yield.run.nominal-failed", DiagnosticSeverity.Error,
        "The nominal design does not evaluate ({reason}), so no trial would mean anything.", ("reason", reason));

    public static Diagnostic RunNoneEvaluated(int trials, string first) => Diagnostic.Create(
        "yield.run.none-evaluated", DiagnosticSeverity.Error,
        "None of the {trials} trials evaluated; the first: {first}.", ("trials", trials), ("first", first));

    public static Diagnostic RunDidNotEvaluate(int count, int trials, bool counted) => Diagnostic.Create(
        "yield.run.did-not-evaluate", DiagnosticSeverity.Warning,
        counted ? "{count} of {trials} trials did not evaluate — counted as fails."
                : "{count} of {trials} trials did not evaluate — excluded from the yield (nonconverged=warn).",
        ("count", count), ("trials", trials));

    public static Diagnostic RunSaved(string sentence) => Diagnostic.Create(
        "yield.run.saved", DiagnosticSeverity.Info, "{sentence}", ("sentence", sentence));

    public static Diagnostic RunWriteFailed(string path, string reason) => Diagnostic.Create(
        "yield.run.write-failed", DiagnosticSeverity.Warning,
        "The results could not be written to {path}: {reason}", ("path", path), ("reason", reason));

    public static Diagnostic TrialOutOfRange(int trial, string why) => Diagnostic.Create(
        "yield.trial.out-of-range", DiagnosticSeverity.Error,
        "There is no trial {trial}: {why}", ("trial", trial), ("why", why));

    public static Diagnostic ContributionUnknown(string name, string known) => Diagnostic.Create(
        "yield.contrib.unknown", DiagnosticSeverity.Error,
        "'{name}' is neither a goal the run scored nor a scalar measurement; it scored: {known}.", ("name", name), ("known", known));

    public static Diagnostic ContributionTooFew(string name, int trials) => Diagnostic.Create(
        "yield.contrib.too-few", DiagnosticSeverity.Error,
        "'{name}' has a value in {trials} trial(s); contributions need at least three.", ("name", name), ("trials", trials));

    public static Diagnostic ContributionNothingVaries() => Diagnostic.Create(
        "yield.contrib.nothing-varies", DiagnosticSeverity.Error,
        "No statistical variable was drawn in the run, so nothing can contribute.");

    /// <summary>brief-yield-16 R-ya16-3: a run at each corner holds one run per corner, each with its own trials.</summary>
    public static Diagnostic ContributionCornerStacked(string name, string corners) => Diagnostic.Create(
        "yield.contrib.corner-stacked", DiagnosticSeverity.Error,
        "This result is a Monte Carlo run at each corner ({corners}), stacked under a corner axis, so there is no single "
      + "set of trials to rank '{name}' over. Rank it on a run at one corner: yield mc or estimate without --corners.",
        ("name", name), ("corners", corners));

    // ── YA-9: a trial re-run from the Data Display ─────────────────────────

    public static Diagnostic ReplayNoDesign(string result) => Diagnostic.Create(
        "yield.replay.no-design", DiagnosticSeverity.Error,
        "{result} has no design beside it to run the trial again from — a result is re-run from the schematic or netlist of the same name.",
        ("result", result));

    public static Diagnostic ReplayNoData(int trial) => Diagnostic.Create(
        "yield.replay.no-data", DiagnosticSeverity.Error,
        "Trial {trial} ran but produced no analysis result to show.", ("trial", trial));

    public static Diagnostic ReplayRunUnrecorded(string result, int trial, string missing) => Diagnostic.Create(
        "yield.replay.run-unrecorded", DiagnosticSeverity.Error,
        "{result} does not record the {missing} its trials were drawn with, so trial {trial} cannot be drawn again as the same trial.",
        ("result", result), ("trial", trial), ("missing", missing));

    // ── YA-6: corners ──────────────────────────────────────────────────────

    public static Diagnostic ReplayStreamsGone(int trial, IReadOnlyList<string> streams) => Diagnostic.Create(
        "yield.corner.streams-gone", DiagnosticSeverity.Warning,
        "Trial {trial} drew {streams}, which this design no longer has (renamed or removed); the corner is replayed without.",
        ("trial", trial), ("streams", string.Join(", ", streams)));

    public static Diagnostic ReplayNotRecorded(string corner) => Diagnostic.Create(
        "yield.corner.not-recorded", DiagnosticSeverity.Info,
        "corner {corner}: the run it names is not at hand, so its trial is drawn afresh from its seed — the same draws for every stream the run had, but a renamed or added variable cannot be told apart.",
        ("corner", corner));

    public static Diagnostic CornerUnknown(string name, string known) => Diagnostic.Create(
        "yield.corner.unknown", DiagnosticSeverity.Error,
        "There is no enabled corner '{name}'; the enabled corners are: {known}.", ("name", name), ("known", known));

    public static Diagnostic CornerNone() => Diagnostic.Create(
        "yield.corner.none", DiagnosticSeverity.Error,
        "The design has no enabled corner to run; add a corner line (yield corners --generate writes some).");

    public static Diagnostic CornerSetConflict(string corner, string name) => Diagnostic.Create(
        "yield.corner.set-conflict", DiagnosticSeverity.Error,
        "'{name}' is set both by --set and by corner {corner}; remove one of the two.", ("corner", corner), ("name", name));

    public static Diagnostic CornerProcessDoubleCounted() => Diagnostic.Create(
        "yield.corner.process-double-counted", DiagnosticSeverity.Warning,
        "statistics: corners= with process=1 — a process corner and a process draw answer one question, so a Monte Carlo at each corner draws mismatch only and leaves process at the corner.");

    public static Diagnostic CornerNominalFailed(string reason) => Diagnostic.Create(
        "yield.corner.nominal-failed", DiagnosticSeverity.Error,
        "No corner evaluated, the nominal included; the nominal: {reason}.", ("reason", reason));

    public static Diagnostic GeneratorTooMany(int count, int cap) => Diagnostic.Create(
        "yield.corner.generate-too-many", DiagnosticSeverity.Error,
        "That cross product is {count} corners; at most {cap} are generated. Fewer options, temperatures or values.",
        ("count", count), ("cap", cap));

    public static Diagnostic GeneratorMalformed(string part, string why) => Diagnostic.Create(
        "yield.corner.generate-malformed", DiagnosticSeverity.Error,
        "'{part}': {why}", ("part", part), ("why", why));

    public static Diagnostic CornerStatisticalNoMonteCarlo(string corner) => Diagnostic.Create(
        "yield.corner.statistical-no-mc", DiagnosticSeverity.Info,
        "corner {corner} is one trial of a run, so it has no Monte Carlo of its own; it is left out of this one.",
        ("corner", corner));

    // ── YA-7: tune and optimize across corners ─────────────────────────────

    public static Diagnostic CornerReplayRefused(string corner, string reason) => Diagnostic.Create(
        "yield.corner.replay-refused", DiagnosticSeverity.Error,
        "corner {corner} cannot be replayed: {reason}", ("corner", corner), ("reason", reason));

    public static Diagnostic CornerPointFailed(string corner, string reason) => Diagnostic.Create(
        "yield.corner.point-failed", DiagnosticSeverity.Error,
        "at corner {corner}: {reason}", ("corner", corner), ("reason", reason));

    // ── YA-11: design centering ────────────────────────────────────────────

    public static Diagnostic CenterNoDesignable() => Diagnostic.Create(
        "yield.center.no-designable", DiagnosticSeverity.Error,
        "Centering moves the nominals of the opt=1 entries, and this setup has none: add opt=1 and a range to the tune line of each value to centre.");

    public static Diagnostic CenterAlgorithm(string id, string suited) => Diagnostic.Create(
        "yield.center.algorithm", DiagnosticSeverity.Error,
        "center algorithm={id} is not one centering offers: a yield over a fixed set of trials is flat between them, " +
        "so it takes one of the methods that need no derivative — {suited}.", ("id", id), ("suited", suited));

    public static Diagnostic CenterNoneEvaluated(long evaluations, string first) => Diagnostic.Create(
        "yield.center.none-evaluated", DiagnosticSeverity.Error,
        "No candidate evaluated a single trial in {evaluations} simulations; the first: {first}.",
        ("evaluations", evaluations), ("first", first));

    public static Diagnostic CenterVerifyFailed(string point, string reason) => Diagnostic.Create(
        "yield.center.verify-failed", DiagnosticSeverity.Warning,
        "The verification of the {point} point did not run: {reason}", ("point", point), ("reason", reason));

    // ── YA-12: the quadratic surrogate ─────────────────────────────────────

    public static Diagnostic CenterSurrogateTooMany(int k, int entries, int process, int mismatchStreams, int instances,
                                                    int perCandidate, int trials) => Diagnostic.Create(
        "yield.center.surrogate-too-many", DiagnosticSeverity.Error,
        "surrogate=quadratic is refused for this design: its {k} statistical coordinates ({entries} toleranced values, " +
        "{process} kit process draws, {mismatch} kit mismatch draws grouped into {instances} instances) need {per} " +
        "simulations per candidate, no fewer than the {trials} common trials it would replace. Centre with surrogate=none, " +
        "or narrow the drawn values with --vars.",
        ("k", k), ("entries", entries), ("process", process), ("mismatch", mismatchStreams), ("instances", instances),
        ("per", perCandidate), ("trials", trials));

    public static Diagnostic CenterSurrogateDiagonal(int k, int limit) => Diagnostic.Create(
        "yield.center.surrogate-diagonal", DiagnosticSeverity.Info,
        "The surrogate fits {k} statistical coordinates without cross terms (a diagonal quadratic): a full one is fitted up " +
        "to {limit}.", ("k", k), ("limit", limit));

    public static Diagnostic CenterSurrogatePoorFit(int iteration, string goal, double rSquared) => Diagnostic.Create(
        "yield.center.surrogate-poor-fit", DiagnosticSeverity.Warning,
        "iteration {iteration}: the surrogate's fit of goal {goal} has R² {r2}, below 0.9.",
        ("iteration", iteration), ("goal", goal),
        ("r2", rSquared.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)));

    public static Diagnostic CenterSurrogateSwitchedBack(int iteration, int running) => Diagnostic.Create(
        "yield.center.surrogate-switched-back", DiagnosticSeverity.Warning,
        "After {running} poor surrogate fits running (to iteration {iteration}), centering switched back to simulated " +
        "trials for the rest of the search.", ("running", running), ("iteration", iteration));

    public static Diagnostic CenterSnapped(int count, string before, string after) => Diagnostic.Create(
        "yield.center.snapped", DiagnosticSeverity.Info,
        "{count} standard-value nominal(s) moved to the series: smooth yield {before} searched continuously, {after} on the series.",
        ("count", count), ("before", before), ("after", after));

    public static Diagnostic CenterSnapFailed(int count) => Diagnostic.Create(
        "yield.center.snap-failed", DiagnosticSeverity.Warning,
        "No candidate with the {count} standard-value nominal(s) on the series could be evaluated; the best point is left between rungs.",
        ("count", count));

    // ── YA-14: design of experiments ───────────────────────────────────────

    public static Diagnostic DoeNoFactors(string source) => Diagnostic.Create(
        "yield.doe.no-factors", DiagnosticSeverity.Error,
        "doe factors={source} has no factor: mark entries {flag} in the Tuning, Optimizer or Yield panel, or add {flag} to " +
        "a tune line.", ("source", source), ("flag", source == "stat" ? "with a distribution (stat=1)" : "opt=1"));

    public static Diagnostic DoeTooMany(string design, int k, string runs, string instead) => Diagnostic.Create(
        "yield.doe.too-many", DiagnosticSeverity.Error,
        "doe design={design} is refused for {k} factors: it would take {runs} runs. {instead}",
        ("design", design), ("k", k), ("runs", runs), ("instead", instead));

    public static Diagnostic DoeNoFraction(int k, int resolution, string held) => Diagnostic.Create(
        "yield.doe.no-fraction", DiagnosticSeverity.Error,
        "doe design=frac resolution={resolution}: the fractional-factorial table holds no such design for {k} factors. {held}",
        ("resolution", resolution), ("k", k), ("held", held));

    public static Diagnostic DoeLevelsMismatch(string levels, string factors) => Diagnostic.Create(
        "yield.doe.levels-mismatch", DiagnosticSeverity.Error,
        "doe levels={levels} does not go with factors={factors}: opt factors take their ranges (levels=range), stat factors " +
        "nominal ± k sigma (levels=sigma:<k>).", ("levels", levels), ("factors", factors));

    public static Diagnostic DoeMixedPair(string value, string parts) => Diagnostic.Create(
        "yield.doe.mixed-pair", DiagnosticSeverity.Error,
        "doe: {value} has factors on {parts} — one rectangular and one polar part — and no corner of their two ranges need " +
        "describe a complex number. Make both factors parts of one system (real and imag, or mag and phase).",
        ("value", value), ("parts", parts));

    public static Diagnostic DoeNoResponse() => new(
        "yield.doe.no-response", DiagnosticSeverity.Error,
        "doe: nothing to analyse — no goal the factors are for, and no measure that gives a single number. Add a goal in the " +
        "Optimizer window, a scalar measure, or responses=all.");

    public static Diagnostic DoeSnapped(string key, string low, string high) => Diagnostic.Create(
        "yield.doe.snapped", DiagnosticSeverity.Info,
        "doe: {key} takes only allowed values, so its levels are the nearest of them — low {low}, high {high}.",
        ("key", key), ("low", low), ("high", high));

    public static Diagnostic DoeUncorrelated(int correlations) => Diagnostic.Create(
        "yield.doe.uncorrelated", DiagnosticSeverity.Info,
        "doe factors=stat sets each factor on its own: the {n} correlate line(s) are not applied to the design's levels.",
        ("n", correlations));

    public static Diagnostic DoeNoneEvaluated(int runs, string first) => Diagnostic.Create(
        "yield.doe.none-evaluated", DiagnosticSeverity.Error,
        "None of the {runs} runs evaluated; the first: {first}.", ("runs", runs), ("first", first));

    public static Diagnostic DoeNotFitted(string response, int evaluated, int terms) => Diagnostic.Create(
        "yield.doe.not-fitted", DiagnosticSeverity.Warning,
        "doe: {response} is not analysed — {evaluated} runs evaluated it, and its model has {terms} coefficients.",
        ("response", response), ("evaluated", evaluated), ("terms", terms));

    public static Diagnostic DoeCurvature(string response) => Diagnostic.Create(
        "yield.doe.curvature", DiagnosticSeverity.Warning,
        "doe: {response} curves — its centre points sit off the plane through the cube by more than the Lenth margin. A " +
        "two-level model does not describe it; design=ccf fits the quadratic.", ("response", response));

    public static Diagnostic DoeWriteFailed(string path, string reason) => Diagnostic.Create(
        "yield.doe.write-failed", DiagnosticSeverity.Warning,
        "The design-of-experiments result could not be written to {path}: {reason}", ("path", path), ("reason", reason));

    public static Diagnostic DoeOptimumNeedsOpt() => new(
        "yield.doe.optimum-needs-opt", DiagnosticSeverity.Error,
        "A model optimum searches the designable ranges, and factors=stat varies tolerances: run the design with factors=opt.");

    public static Diagnostic DoeOptimumNoGoal() => new(
        "yield.doe.optimum-no-goal", DiagnosticSeverity.Error,
        "A model optimum aims at the goals, and this design analysed none. Add a goal in the Optimizer window.");

    public static Diagnostic DoeOptimumNotFitted(string goal) => Diagnostic.Create(
        "yield.doe.optimum-not-fitted", DiagnosticSeverity.Error,
        "A model optimum needs every goal's model, and goal {goal} has none — too few of its runs evaluated.", ("goal", goal));

    public static Diagnostic DoeConfirmationFailed(string reason) => Diagnostic.Create(
        "yield.doe.confirmation-failed", DiagnosticSeverity.Warning,
        "The confirmation run at the model optimum did not evaluate: {reason}", ("reason", reason));

    // ── Sensitivity (brief-sens-1) ─────────────────────────────────────────────

    public static Diagnostic SensNoFactors(string source) => Diagnostic.Create(
        "yield.sens.no-factors", DiagnosticSeverity.Error,
        "sensitivity factors={source} has nothing to step: {what}", ("source", source),
        ("what", source switch
        {
            "stat" => "no tune line carries a tolerance (stat=1). Add one in the Yield panel, or use factors=all.",
            "opt"  => "no tune line is opt=1. Tick Opt in the Optimizer panel, or use factors=all.",
            _      => "the design offers no value that can be stepped.",
        }));

    public static Diagnostic SensNoResponse() => new(
        "yield.sens.no-response", DiagnosticSeverity.Error,
        "sensitivity: nothing to rank — the design has no enabled goal and no measure that gives a single number. Add a " +
        "goal (goal Vout = DC1.V(\"out\") analysis=DC1 in 0.49 0.51 use=yield) or a scalar measure (measure …).");

    public static Diagnostic SensNoScalarResponse() => new(
        "yield.sens.no-scalar-response", DiagnosticSeverity.Error,
        "sensitivity: nothing to rank — the design has no enabled goal, and none of its measures is a single number (a " +
        "measure over a sweep is a curve). Add a goal over the range that matters (goal Pout = Pout_dBm " +
        "analysis=HB1_sweep_Pin over=Pin lo=28 hi=30 ge 40), which ranks by its tightest point.");

    public static Diagnostic SensNominalFailed(string reason) => Diagnostic.Create(
        "yield.sens.nominal-failed", DiagnosticSeverity.Error,
        "sensitivity: the nominal design did not evaluate, so nothing can be stepped from it: {reason}", ("reason", reason));

    public static Diagnostic SensLeftOut(string key, string reason) => Diagnostic.Create(
        "yield.sens.left-out", DiagnosticSeverity.Info,
        "sensitivity leaves {key} out: {reason}.", ("key", key), ("reason", reason));

    public static Diagnostic SensUncorrelated(int correlations) => Diagnostic.Create(
        "yield.sens.uncorrelated", DiagnosticSeverity.Info,
        "sensitivity steps each factor on its own (uncorrelated): the {n} correlate line(s) are not applied.", ("n", correlations));

    public static Diagnostic SensKitLeftOut(string streams) => Diagnostic.Create(
        "yield.sens.kit-left-out", DiagnosticSeverity.Info,
        "sensitivity steps the design's own values, not a kit's statistics: {streams} left out. A Monte Carlo draws them.",
        ("streams", streams));

    public static Diagnostic SensNoEstimate(string bases) => Diagnostic.Create(
        "yield.sens.no-estimate", DiagnosticSeverity.Info,
        "sensitivity gives no pass estimate: some factors step by {bases} rather than by their tolerance, so the spread is " +
        "what a designer could change, not what production will do.", ("bases", bases));

    public static Diagnostic SensWriteFailed(string path, string reason) => Diagnostic.Create(
        "yield.sens.write-failed", DiagnosticSeverity.Warning,
        "The sensitivity result could not be written to {path}: {reason}", ("path", path), ("reason", reason));

    public static Diagnostic SensVarsNotOffered(string key, string offered) => Diagnostic.Create(
        "yield.sens.vars-unknown", DiagnosticSeverity.Error,
        "--vars {key} is not one of the factors this run would step: {offered}.", ("key", key), ("offered", offered));

    // ── SnP entries (brief-sens-2) ─────────────────────────────────────────────

    public static Diagnostic SensSnpExcluded(string instance, int toleranced) => Diagnostic.Create(
        "yield.sens.snp-excluded", DiagnosticSeverity.Info,
        "{instance} excluded: {n} toleranced entr" + (toleranced == 1 ? "y" : "ies") + " not stepped.", ("instance", instance), ("n", toleranced));

    public static Diagnostic SensSnpExcludeUnknown(string name, string instances) => Diagnostic.Create(
        "yield.sens.snp-exclude-unknown", DiagnosticSeverity.Warning,
        "snpexclude names {name}, which is not an SnP instance of the design ({instances}); the run ignores it.",
        ("name", name), ("instances", instances));

    public static Diagnostic SnpTiedNotReciprocal(string instance, string pair, double db, string at) => Diagnostic.Create(
        "snp.tied-not-reciprocal", DiagnosticSeverity.Warning,
        "{instance} is Reciprocal=tied, and its file is not reciprocal: {pair} differ by up to {db} dB at {at}; one offset moves them together.",
        ("instance", instance), ("pair", pair), ("db", double.IsFinite(db) ? db.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : "∞"), ("at", at));

    public static Diagnostic SensGoalsUnknown(string name, string enabled) => Diagnostic.Create(
        "yield.sens.goals-unknown", DiagnosticSeverity.Error,
        "--goals {name} is not an enabled goal of the design: {enabled}.", ("name", name), ("enabled", enabled));
}
