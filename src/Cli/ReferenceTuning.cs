using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Statistics;
using CircuitRF.Engine.Optimization;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// The <c>tuning</c> and <c>goals</c> reference topics (TO-1 R-to1-8) — generated from
/// <see cref="AnalysisDirectiveSchema.TuningDirectives"/>, the table <c>CnlReader</c> reads the four
/// directives against, so the page cannot describe a key the reader does not know or miss one it does —
/// and the <c>optimizers</c> topic (brief-tuneopt-7 R-to7-7), generated from
/// <see cref="OptimizerAlgorithms"/>, the registry the optimizer itself reads its option defaults from.
/// </summary>
internal static partial class Reference
{
    internal const string OptimizersTopic = "optimizers";
    private const string OptimizersTitle = "Optimizer algorithms";
    private const string OptimizersSummary =
        "Generated from the algorithm registry the optimizer reads its defaults from: every algorithm= id " +
        "with its menu label, when to use it, the cost forms it accepts, whether it estimates gradients, " +
        "and every alg.<option> with its default.";

    private const string StatisticsTitle = "Monte Carlo, yield and corners";
    private const string StatisticsPageSummary =
        "Generated from the schema the .cnl reader validates against: a tolerance on a tune line (each " +
        "distribution, its spread, percent or absolute, truncation), correlate, statistics and corner with every " +
        "key and default, statistical corners, goal use=, how a trial passes and how yield and its interval are " +
        "reported, the yield verb's nouns, flags and exit codes, the result file's layout, with a worked example " +
        "and one run per mode.";

    private const string TuningTitle = "Tuning directives";
    private const string GoalsTitle  = "Optimization goals";

    private const string TuningSummary =
        "Generated from the schema the .cnl reader validates against: the tune, preset and optimize " +
        "directives — which values can be tuned and how they are named, the range keys, presets and the " +
        "optimizer's settings — with a worked example.";

    private const string GoalsSummary =
        "Generated from the schema the .cnl reader validates against: the goal directive — its expression, " +
        "analysis, range over a swept axis, the five goal types, sloped limits and weights — with a worked example.";

    private static int TuningTopic(string topic)
    {
        string text = RenderTuning(topic);
        bool goals  = topic == AnalysisDirectiveSchema.GoalsTopic;
        JsonRun.Reference = new ReferenceReportJson(
            null,
            new ReferenceTopicJson(topic, goals ? GoalsTitle : TuningTitle, goals ? GoalsSummary : TuningSummary,
                                   ByteLength(text), text),
            null);
        Console.Out.Write(text);
        return 0;
    }

    private static int StatisticsTopicRun()
    {
        string text = RenderStatistics();
        JsonRun.Reference = new ReferenceReportJson(
            null, new ReferenceTopicJson(AnalysisDirectiveSchema.StatisticsTopic, StatisticsTitle, StatisticsPageSummary,
                                         ByteLength(text), text), null);
        Console.Out.Write(text);
        return 0;
    }

    /// <summary>
    /// The <c>statistics</c> page (docs/design/yield.md): the tune line's statistical keys and goal's
    /// <c>use=</c> out of their own specs, the three statistical directives, the distribution table, and
    /// the rules a run applies — every key and default from the schema the reader reads against.
    /// </summary>
    internal static string RenderStatistics()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Monte Carlo, yield and corners — a tolerance is part of a tune line, a yield spec is a goal,");
        sb.AppendLine("and correlate, statistics and corner lines sit beside them in a .cnl. A schematic holds the");
        sb.AppendLine("same content in its \"Tuning\" block. A key this page does not list is kept and reported as a");
        sb.AppendLine("warning naming it; a default is never written.");
        sb.AppendLine();

        sb.AppendLine("A tolerance on a tune line");
        sb.AppendLine();
        var tune = AnalysisDirectiveSchema.FindTuningDirective("tune")!;
        foreach (var k in tune.Keys.Where(k => AnalysisDirectiveSchema.StatKeys.Contains(k.Name)))
            sb.AppendLine($"  {k.Name,-16} {k.Default ?? "-",-12} {k.Summary}".TrimEnd());
        sb.AppendLine();
        sb.AppendLine("Distributions");
        sb.AppendLine();
        foreach (var (dist, spread, meaning, example) in AnalysisDirectiveSchema.Distributions)
        {
            sb.AppendLine($"  {dist,-10} {spread}");
            foreach (var line in Wrap(meaning, 92)) sb.AppendLine("             " + line);
            sb.AppendLine("             e.g.  " + example);
        }
        sb.AppendLine();
        foreach (var line in Wrap(
            "Percent or absolute. A spread value is a percent of the nominal (2%), which follows the nominal when " +
            "tuning or centering moves it, or a value in the parameter's unit (0.1 pF, or 0.1 meaning the parameter's own " +
            "unit), which does not. A percent lo, hi or by is that percent OF the nominal: lo=90% hi=110% is 0.9 to 1.1 " +
            "times it. The line is written back in the form it was written.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        foreach (var line in Wrap(
            "Truncation. trunc=k (gauss, lognorm) draws from the distribution truncated at +-k sigma — the probability is " +
            "renormalized over the window, never clipped onto its edges. A value that must be positive (a resistance, " +
            "capacitance, inductance, conductance, length or magnitude) and can be drawn at or below zero with " +
            $"probability above {StatisticsValidator.NonPhysicalThreshold.ToString("G2", System.Globalization.CultureInfo.InvariantCulture)} " +
            "is a check warning naming trunc= and lognorm; a gaussian resistance wider than about 16 % untruncated is " +
            "one. stat=0 keeps the distribution without drawing it. A whole-number value takes discrete or unif only. " +
            "On a complex value, a tolerance goes on one part or on a same-system pair (real with imag, mag with " +
            "phase), each drawn independently; real with mag is refused.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();

        foreach (var spec in AnalysisDirectiveSchema.TuningDirectives.Where(d => d.Topic == AnalysisDirectiveSchema.StatisticsTopic))
        {
            sb.AppendLine("    " + spec.Syntax);
            sb.AppendLine();
            foreach (var line in Wrap(spec.Summary, 96)) sb.AppendLine("  " + line);
            sb.AppendLine();
            foreach (var k in spec.Keys)
                sb.AppendLine($"  {k.Name,-16} {k.Default ?? "-",-12} {k.Summary}".TrimEnd());
            sb.AppendLine();
            sb.AppendLine("  e.g.  " + spec.Example);
            sb.AppendLine();
        }

        foreach (var line in Wrap(
            "Statistical corners. corner <Name> trial=<n> seed=<s> sampling=<m> trials=<N> replays trial n of the run " +
            "those three identify. A trial is stored as its standard-normal draws, so replaying it against a moved " +
            "nominal applies the same relative deviation — the corner moves with tuning and centering. Kit corners: " +
            "a schematic's corner also selects kit corner sections, and netlisting it writes what they bind.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();

        var use = AnalysisDirectiveSchema.FindTuningDirective("goal")!.Keys.First(k => k.Name == "use");
        sb.AppendLine("Yield specs are goals");
        sb.AppendLine();
        sb.AppendLine($"  {use.Name,-16} {use.Default ?? "-",-12} {use.Summary}");
        sb.AppendLine();
        foreach (var line in Wrap(
            "A trial passes a goal when the goal is met at that trial — the optimizer's own rule: its worst violation is " +
            $"within {GoalResiduals.MetTolerance.ToString("G2", System.Globalization.CultureInfo.InvariantCulture)} of its " +
            "scale. It passes when it passes every enabled goal whose use is yield or both. Centre the design against " +
            "tight goals, then loosen them (or keep tight copies with use=opt) for yield.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        sb.AppendLine("How yield is reported");
        sb.AppendLine();
        foreach (var line in Wrap(
            "Yield is passes / counted trials, overall and per goal, with a Clopper-Pearson interval at confidence=. " +
            "A trial that does not evaluate (no convergence, a non-physical draw, an engine error) counts as a fail " +
            "and is reported apart with nonconverged=fail; with nonconverged=warn it is left out of the count and " +
            "reported as a warning. With autostop=1 the run stops once the interval's lower end reaches target (pass) " +
            "or its upper end falls below it (fail), never before 50 counted trials; trials= is the most it runs. " +
            "explain <file> --analysis reports how wide the interval of the configured trial count is expected to be " +
            $"at a yield of {StatisticsSummary.QuotedYield.ToString(System.Globalization.CultureInfo.InvariantCulture)} %, and the " +
            $"trial count that narrows it to under +-{StatisticsSummary.QuotedHalfWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)} %.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        sb.AppendLine("The statistics of a run");
        sb.AppendLine();
        foreach (var line in Wrap(
            "A run writes <design>.yield.npy: every analysis cube with an outer trial axis, the trials group " +
            "(stat:<key>, z:<key>, goal:<name>:pass|margin|worst, pass, status), the nominal group and the yield " +
            "summary. These reduce over the trial axis by default (else freq), skipping a trial with no value: " +
            "mean_over, std_over, median_over, pctl_over(x, p), skew_over, kurt_over, " +
            "yield_over(condition), cpk(x, lo, hi) (either limit \"none\"), sigma_to(x, limit) — min_over and " +
            "max_over take the same default axis but keep a missing value missing; and these build an " +
            "axis: histogram(x, bins[, lo, hi][, \"percent\"]), cdf(x), normq(x), yield_sens(pass, x, bins). They work in measure lines " +
            "and goals. A goal written over the trials' spread (std_over of a trial-stacked " +
            "quantity) is legal, but scoring it takes a whole Monte Carlo run per evaluation.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();

        // brief-yield-11 R-ya11-8: design centering, beside the runs it is built from.
        sb.AppendLine("Design centering");
        sb.AppendLine();
        foreach (var line in Wrap(
            "Centering moves the nominals of the opt=1 entries, within their ranges, to maximize the yield against the " +
            "use=yield|both goals; an entry that is both opt=1 and toleranced is the usual case, and its percent spread " +
            "follows the moved nominal. Every candidate is scored on the same M trials (center trials=, default " +
            $"{CenteringSettings.DefaultTrials}): trial t's standard-normal draws are fixed once, from the center seed, and " +
            "re-applied around each candidate, so two candidates' yields differ by the design and not by sampling noise. " +
            "The search sees a smooth yield: per trial, the smallest margin over the yield goals divided by each goal's " +
            "scale (the goal's scale=, or its default), through a logistic of width w (default " +
            $"{CenteringSettings.DefaultWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)}), averaged over the " +
            "trials; the plain yield on the same trials is reported beside it. A trial that does not evaluate scores as a " +
            "fail, or is left out with nonconverged=warn. The algorithms are the optimizer's that need no derivative, since " +
            "a yield over finitely many trials is flat between them: " +
            string.Join(", ", OptimizerAlgorithms.ForNoisyObjective) + " (default " + CenteringSettings.DefaultAlgorithm + "). " +
            "One iteration costs its candidates × M simulations; explain <file> --analysis states the estimated total " +
            "before anything runs. At the end the start and the best point are each run on the same verify= fresh trials " +
            "(the next seed) with their intervals, so 'yield 71 % -> 94 %' compares like with like, and the report says " +
            "whether the gain lies within the intervals' overlap. The best point's verification is the result file, " +
            "<design>.yield.npy. Stop keeps the best point and still verifies it.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        sb.AppendLine("    circuitrf yield center pad.cnl --trials 200 --verify 1000 --target 95%");
        sb.AppendLine("    circuitrf yield center amp.csch --save-preset centred      # the centred nominals as a preset");
        sb.AppendLine();
        foreach (var line in Wrap(
            "Over MCP: run analysis=center with the same fields (algorithm, trials, verify, maxIter, maxEvals, time, width, " +
            "target, savePreset, output); a progress notification arrives per iteration, then read the verified yield " +
            "in result.center.verification and the written <design>.yield.npy. Exit 3: the verified yield is below target.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();

        // brief-yield-12 R-ya12-4: the quadratic surrogate — when it helps, and when it does not.
        sb.AppendLine("The quadratic surrogate (center surrogate=quadratic, --surrogate quadratic)");
        sb.AppendLine();
        foreach (var line in Wrap(
            "A search accelerator for centering. For each candidate, the design is simulated at 2k+1 points in the " +
            "statistical variables' z-space (the centre and +/-1 on each), plus one cross point per pair when k <= " +
            $"{QuadraticSurrogate.FullQuadraticLimit} (a full quadratic; beyond that the fit is diagonal and the report says so), " +
            "plus k+2 of the common trials so the least-squares fit is over-determined. Each yield goal is fitted as a " +
            "quadratic in z — a goal on a single number on its value, its margin then computed by the goal's own rule; a " +
            "goal over a sweep on its margin — and the smooth yield and the yield are counted on " +
            $"{QuadraticSurrogate.VirtualTrials:N0} virtual trials of the fits, drawn from the center seed. k counts each " +
            "toleranced value, each kit process draw, and each instance's kit mismatch draws as ONE coordinate (they move " +
            "together by x/sqrt(n)); a design whose points per candidate are no fewer than M is refused with the counts. " +
            "Every iteration reports the fit's R² per goal: below 0.9 is a warning on that iteration, and three running " +
            "switch the search back to simulated trials with a note. The result's yield is ALWAYS the simulated " +
            "verification; the surrogate only chooses where to look.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        foreach (var line in Wrap(
            "It helps when the statistical variables are few (a handful of toleranced values), the margins are smooth in " +
            "them (a gain, a return loss, a centre frequency moving gently with the parts), and a simulation is expensive " +
            "(HB, a long sweep): a candidate then costs a dozen simulations instead of M. It does not help with many " +
            "statistical variables (kit mismatch on many instances), a margin that is a min over a sweep whose worst point " +
            "jumps between frequencies, a pass/fail that switches abruptly (a mode hop, an oscillation), or a cheap " +
            "simulation where M trials cost little anyway — leave surrogate=none there.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        sb.AppendLine("    circuitrf yield center amp.csch --surrogate quadratic --trials 200 --verify 1000");
        sb.AppendLine();

        // brief-yield-14 R-ya14-8: design of experiments.
        sb.AppendLine("Design of experiments (yield doe, the doe line)");
        sb.AppendLine();
        foreach (var line in Wrap(
            "A design of experiments sets the factors at the points of a structured design and analyses which of them " +
            "move each response. factors=opt (the default) varies the opt=1 entries between the ends of their ranges — " +
            "the centre is the range's midpoint on the entry's own scale, so a log-scaled one's is geometric; " +
            "factors=stat varies the stat=1 entries to nominal +- k sigma (levels=sigma:k), a tolerance study in a " +
            "handful of runs. A whole-number or discrete factor takes its nearest allowed values, and the report says " +
            "so. Designs: full2 (2^k runs, at most 10 factors), frac (a 2^(k-p) fraction at resolution 4 or 5 from a " +
            "published minimum-aberration table, 4 to 11 factors), pb (Plackett-Burman, 12, 20 or 24 runs for up to 23 " +
            "factors) and ccf (the face-centred central composite — cube, face points and centre — for a quadratic " +
            "model). centre= adds centre points, which check a two-level model for curvature. Simulation is " +
            "deterministic, so runs are neither replicated nor randomized, and a repeated centre point costs nothing.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        foreach (var line in Wrap(
            "Responses are each goal's worst value and margin and every scalar measure. Each is fitted on coded -1/+1 " +
            "factors — main effects and two-factor interactions, and the pure quadratics for ccf — and an effect (twice " +
            "the coded coefficient: the change from low to high) is ACTIVE when it exceeds Lenth's margin, t(0.975; " +
            "m/3) x 1.5 x the median of the small effects; a deterministic simulation has no pure error to test " +
            "against. A fractional or Plackett-Burman design cannot tell some effects apart, and every effect it reports " +
            "carries its alias set: res IV keeps main effects clear of two-factor interactions, res V keeps those clear " +
            "of each other, Plackett-Burman confounds each main effect partially with the interactions of the others. " +
            "--optimum searches the fitted model (the optimizer's algorithms, microseconds per evaluation) for the point " +
            "where the goals are best met inside the ranges, then confirms it by one real simulation, reported as " +
            "predicted vs simulated for every goal: the model is never the answer, the confirmation is.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        foreach (var line in Wrap(
            "Screen with design=pb first, then design=ccf on the few active factors. The result is <design>.doe.npy: " +
            "the runs group (coded and actual factors, every response), the effects group (effects, Lenth margin, R², " +
            "alias sets, the effects-Pareto bars), and the main and interaction groups the plots draw.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        sb.AppendLine("    doe design=pb");
        sb.AppendLine("    circuitrf yield doe amp.csch --design pb                 # which of many factors matter");
        sb.AppendLine("    circuitrf yield doe amp.csch --design ccf --optimum      # a response surface and its confirmed best point");
        sb.AppendLine();
        foreach (var line in Wrap(
            "Over MCP: run analysis=doe with the same fields (design, resolution, factors, levels, centre, responses, " +
            "optimum); read result.doe.responses[].effects[] (term, effect, active, aliases) and the written " +
            "<design>.doe.npy.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();

        // brief-sens-1 R-sens1-8: sensitivity.
        sb.AppendLine("Sensitivity (yield sens, the sensitivity line)");
        sb.AppendLine();
        foreach (var line in Wrap(
            "A sensitivity run answers which values move each goal most at the nominal design. Each factor is stepped + " +
            "and - on its own, every other value at nominal: 2k+1 simulations, no random draws, so it is deterministic " +
            "and needs no seed. A toleranced value (stat=1) steps +-k sigma through its own distribution (sigma=, default " +
            "1) — the same values doe levels=sigma:k sets; any other value steps +-step % of its value (step=, default " +
            "1%), and a phase +-1 deg. factors=all (the default) takes every tunable the design offers, stat only the " +
            "toleranced ones, opt only the opt=1 ones. all leaves out a port's own values (its reference impedance) unless " +
            "ports=on, and a tone source's frequency, which harmonic balance fixes to its tone grid. Also left out, and " +
            "named in the notes: a vendor part's choice of part, " +
            "a whole-number value, a value a sweep sets. Factors move alone (correlations are not applied) and a kit's " +
            "statistics are not factors.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        foreach (var line in Wrap(
            "Each goal's metric is its margin at the tightest point divided by its scale — the normalization centering " +
            "uses — so goals in dB and in volts rank on one footing. Per goal: delta = (m+ - m-)/2 per factor, ranked by " +
            "|delta|, each one's share delta^2 / sum(delta^2), and the predicted spread sqrt(sum(delta^2)). Every " +
            "contributor states its basis (sigma, 1 %, 1 deg): a ranking by tolerance is what production will do, a " +
            "ranking by 1 % is what a designer could change. Only when every basis is sigma is a first-order pass " +
            "estimate Phi(m0/spread) given, labelled estimated — the Monte Carlo is the yield. A factor whose two steps " +
            "disagree (asymmetry above 0.5) is flagged nonlinear: a goal over a sweep whose tightest point moves, or a " +
            "value near a resonance — such a factor wants doe design=ccf or a Monte Carlo. A step that does not " +
            "evaluate leaves its factor failed (or one-sided, from the side that ran) and the rest still rank; the " +
            "nominal must evaluate (exit 2 otherwise). Every scalar measure is ranked too, in its own unit.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        foreach (var line in Wrap(
            "A good order: sensitivity first, then a Monte Carlo on what it ranks (yield mc --vars), then doe design=ccf " +
            "on anything flagged nonlinear. The result is <design>.sens.npy, group sens: contrib:<goal> (the shares, " +
            "largest first, which the Data Display's contribution Pareto draws), delta, plus, minus, nominal, asymmetry " +
            "and status per goal, spread and estimate, and basis, step and notes.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        foreach (var line in Wrap(
            "Every S-parameter of an SnP block is a factor too: SNP3.SdB_3_1 (dB) and SNP3.Sdeg_3_1 (phase), stepped " +
            "+-snpdb dB / +-snpdeg deg (0.1 dB, 1 deg) or by a tolerance on a tune line; a reciprocal file offers its lower " +
            "triangle, one offset moving S31 and S13 together. The ranking rolls up by entry (an S-parameter's dB and " +
            "phase together), by port (shares overlap) and by instance (with its factor count): contrib:<goal>:by=entry|" +
            "port|instance, yield sens --by. snp=off or snpexclude=SNP4 leaves blocks out — because each factor is stepped " +
            "on its own, that changes no other factor's delta: it renormalizes the shares over what is left, so a share " +
            "then reads as a share of what the designer can change; the excluded blocks are listed (excluded).", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        sb.AppendLine("    sensitivity factors=stat sigma=2");
        sb.AppendLine("    circuitrf yield sens divider.cnl                  # what moves each goal, at tolerance or 1 %");
        sb.AppendLine("    circuitrf yield sens divider.cnl --factors stat   # by tolerance only, with an estimated pass");
        sb.AppendLine();
        foreach (var line in Wrap(
            "Over MCP: run analysis=sensitivity with the same fields (factors, step, sigma, vars, goals, responses); read " +
            "result.sensitivity.goals[].contributors[] (key, basis, delta, share, status).", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();

        // brief-yield-5 R-ya5-7: the run, generated from the verb's own noun and flag tables.
        sb.AppendLine("Running it");
        sb.AppendLine();
        sb.AppendLine("    circuitrf yield mc|estimate|trial|corners|center|doe|sens <file.csch|file.cnl> [flags]");
        sb.AppendLine();
        foreach (var (noun, summary) in Yield.Nouns)
            sb.AppendLine($"  {noun,-16} {summary}");
        sb.AppendLine();
        foreach (var line in Wrap(
            "Each flag overrides the file's statistics line for this run only, and nothing is written to the design's " +
            "values: --save-preset and --save-corner are the only writes, to a .csch, after a history checkpoint. " +
            "Over MCP: run analysis=montecarlo or analysis=yield, with these flags as fields (trials, seed, sampling, " +
            "target, autostop, nonconverged, save, output, contributions, trial, savePreset, saveCorner); a progress " +
            "notification arrives per batch.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        foreach (var line in Wrap(
            "Each flag is honoured by the runs in its brackets and refused by every other, by name (mc and estimate "
          + "with --trial re-run one trial, so they take what trial takes).", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        foreach (var (flag, takes, nouns, summary) in Yield.Flags)
            sb.AppendLine($"  {(flag + (takes.Length > 0 ? " " + takes : "")),-30} {summary} [{string.Join(", ", nouns)}]");
        sb.AppendLine();
        sb.AppendLine("Exit codes");
        sb.AppendLine();
        sb.AppendLine("  0    finished; the yield met --target, or there was no target (mc has none)");
        sb.AppendLine("  3    finished, and the yield is below --target");
        sb.AppendLine("  1    refused before the first trial");
        sb.AppendLine("  2    no trial evaluated");
        sb.AppendLine("  130  cancelled; nothing is written");
        sb.AppendLine();
        sb.AppendLine("The result file");
        sb.AppendLine();
        foreach (var line in Wrap(
            "<design>.yield.npy beside the design (never run.npy, which Simulate owns). Every analysis group the nominal " +
            "produced, each cube with an outer trial axis (1..N) over the trials the save policy kept, a real scalar " +
            "measurement for every trial; trials: stat:<key> (base SI), z:<key>, z:process:<stream>, " +
            "z:mismatch:<stream>, goal:<name>:pass, goal:<name>:margin, goal:<name>:worst, pass, status (0 evaluated, " +
            "k the k-th entry of reasons) and reasons; nominal: each cube with no trial axis, named by its address " +
            "(SP1.S, trials.pass); yield: trials, did_not_evaluate, passes, counted, yield, lower, upper, the same per " +
            "goal as goal:<name>:yield, and confidence, target, seed, saved_trials, mode, sampling, nonconverged, save, " +
            "stopped. read prints the yield group first; --at trial=417 narrows to one trial.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();

        sb.AppendLine("Worked example");
        sb.AppendLine();
        foreach (var line in StatisticsExample) sb.AppendLine(line.Length == 0 ? "" : "    " + line);
        sb.AppendLine();
        sb.AppendLine("  and one run of each mode on it:");
        sb.AppendLine();
        sb.AppendLine("    circuitrf yield mc       pad.cnl --trials 200                  # the spread of S21, no pass/fail");
        sb.AppendLine("    circuitrf yield estimate pad.cnl --target 95% --autostop --sampling sobol");
        sb.AppendLine("    circuitrf yield trial    pad.cnl --trial 417                   # what the worst trial drew");
        sb.AppendLine("    circuitrf plot pad.yield.npy -o s21.svg --trace \"cube=histogram(trials.goal:S21:worst, 20)\"");
        sb.AppendLine();
        sb.AppendLine("See also: reference tuning (the rest of a tune line), reference goals (what a goal can say);");
        sb.AppendLine("explain <file> --tunables shows each tolerance in numbers, --analysis the statistics settings,");
        sb.AppendLine("the analyses a yield run evaluates and its cost.");
        return sb.ToString();
    }

    private static readonly string[] StatisticsExample =
    [
        "Vdd = 3.3 V",
        "Port:P1 in 0 Num=1 Z=50 Ohm",
        "R:R1 in mid R=50 Ohm",
        "R:R2 mid 0 R=50 Ohm",
        "C:C1 mid out C=2 pF",
        "Port:P2 out 0 Num=2 Z=50 Ohm",
        "analysis SP1 type=sparam start=1 stop=2 npts=11 Unit=GHz",
        "tune R1.R min=25 Ohm max=100 Ohm opt=1 dist=gauss sd=2%",
        "tune R2.R dist=gauss tol=5% sigmas=3 trunc=3",
        "tune C1.C dist=unif tol=0.1 pF",
        "goal S21 = dB(SP1.S(2,1)) analysis=SP1 over=freq lo=1 GHz hi=2 GHz ge -10 use=yield",
        "correlate R1.R R2.R rho=0.9",
        "statistics trials=500 seed=7 sampling=lhs target=95%",
        "corner Hot temp=85 Vdd=3.0 V R1.R=47 Ohm",
        "corner Worst_S21 trial=417 seed=7 sampling=lhs trials=500",
    ];

    private static int OptimizersTopicRun()
    {
        string text = RenderOptimizers();
        JsonRun.Reference = new ReferenceReportJson(
            null, new ReferenceTopicJson(OptimizersTopic, OptimizersTitle, OptimizersSummary, ByteLength(text), text), null);
        Console.Out.Write(text);
        return 0;
    }

    /// <summary>The <c>optimizers</c> page: one block per registry entry, in menu order.</summary>
    internal static string RenderOptimizers()
    {
        static string CostToken(OptimizerCost c) => c == OptimizerCost.Minimax ? "minimax" : "lsq";

        var sb = new StringBuilder();
        sb.AppendLine("Optimizer algorithms — the optimize line names one with algorithm=<id>, and");
        sb.AppendLine("alg.<option>=<value> sets one of its options. Every coordinate an algorithm moves is a");
        sb.AppendLine("variable's position in its range, from 0 to 1, so steps and sizes below are fractions of a range.");
        sb.AppendLine();
        sb.AppendLine("    optimize algorithm=de maxevals=2000 seed=1 alg.population=40");
        sb.AppendLine();
        foreach (var a in OptimizerAlgorithms.All)
        {
            sb.AppendLine($"{a.Id,-16}{a.Label}{(OptimizerFactory.IsBuilt(a.Id) ? "" : "  (not in this build)")}");
            foreach (var line in Wrap(a.UseWhen, 94)) sb.AppendLine("  " + line);
            sb.AppendLine($"  cost: {string.Join(", ", a.Costs.Select(CostToken))}" +
                          $"{(a.Costs.Count == 1 ? " (choosing it sets this form)" : "")}" +
                          $" · gradients: {(a.NeedsGradients ? "finite differences, n extra evaluations a step" : "none")}");
            foreach (var o in a.Options) sb.AppendLine($"  {o.Name,-16} {o.Default,-8} {o.Summary}".TrimEnd());
            sb.AppendLine();
        }
        sb.AppendLine("Every algorithm also takes:");
        foreach (var o in OptimizerAlgorithms.CommonOptions) sb.AppendLine($"  {o.Name,-16} {o.Default,-8} {o.Summary}".TrimEnd());
        sb.AppendLine();
        // brief-sdl-7: optimizing geometry by EM.
        sb.AppendLine("Optimizing by EM — when a goal reads an analysis with model=em (the schematic simulated by EM of its own");
        sb.AppendLine("layout), em= on the optimize line says how:");
        sb.AppendLine($"  {"correct",-16} {"default",-8} The algorithm above searches the circuit models; each EM solve records every goal's offset");
        sb.AppendLine($"  {"",-16} {"",-8} (EM minus circuit, sample by sample) and the next search adds it, within a step of the last EM point.");
        sb.AppendLine($"  {"",-16} {"",-8} A response EM shifts in frequency, rather than lifts, is corrected slowly: then run a short direct pass.");
        sb.AppendLine($"  {"direct",-16} {"",-8} Every evaluation is an EM solve, cached by geometry. Only {string.Join(", ", OptimizerAlgorithms.ForEmDirect)};");
        sb.AppendLine($"  {"",-16} {"",-8} auto runs {OptimizerAlgorithms.EmDirectAuto}.");
        sb.AppendLine($"  {"off",-16} {"",-8} Circuit models only.");
        sb.AppendLine("emBudget= caps the uncached solves (12 for correct, 30 for direct); the run stops there and returns its best");
        sb.AppendLine("EM-verified point. A length is never stepped by less than two database units of the layout. Every candidate");
        sb.AppendLine("is planned first and refused, with no solve, when its copper would open a joint or touch copper of another");
        sb.AppendLine("net; each variable's connected range is reported before the first solve.");
        sb.AppendLine();
        sb.AppendLine("    optimize algorithm=pattern em=direct emBudget=20");
        sb.AppendLine();
        sb.AppendLine("See also: reference tuning (the optimize line's other keys), reference goals (what it minimizes).");
        return sb.ToString();
    }

    internal static string RenderTuning(string topic)
    {
        bool goals = topic == AnalysisDirectiveSchema.GoalsTopic;
        var sb = new StringBuilder();
        sb.AppendLine(goals
            ? "Optimization goals — one goal line each, in a .cnl beside the analysis and measure lines:"
            : "Tuning directives — one line each, in a .cnl beside the analysis and measure lines:");
        sb.AppendLine();
        sb.AppendLine("A schematic holds the same content in its \"Tuning\" block, and netlisting it writes these");
        sb.AppendLine("lines. A key this table does not list is kept and reported as a warning naming it.");
        sb.AppendLine("Values are written the way the schematic holds them: a number, then its unit after a space.");
        sb.AppendLine();

        foreach (var spec in AnalysisDirectiveSchema.TuningDirectives.Where(d => d.Topic == topic))
        {
            sb.AppendLine("    " + spec.Syntax);
            sb.AppendLine();
            foreach (var line in Wrap(spec.Summary, 96)) sb.AppendLine("  " + line);
            sb.AppendLine();
            foreach (var k in spec.Keys)
                sb.AppendLine($"  {k.Name,-16} {k.Default ?? "-",-8} {k.Summary}".TrimEnd());
            foreach (var (word, summary) in spec.BareWords)
                sb.AppendLine($"  {word,-16} {"",-8} {summary}".TrimEnd());
            sb.AppendLine();
            sb.AppendLine("  e.g.  " + spec.Example);
            sb.AppendLine();
        }

        if (goals) AppendScoringAndFunctions(sb);

        sb.AppendLine("Worked example");
        sb.AppendLine();
        foreach (var line in goals ? GoalsExample : TuningExample) sb.AppendLine(line.Length == 0 ? "" : "    " + line);
        sb.AppendLine();
        sb.AppendLine(goals
            ? "See also: reference tuning (which values can vary), reference analyses (what a goal reads);\n" +
              "circuitrf opt <file> runs them (run analysis=optimize over the protocol) — exit 3 when a goal is unmet."
            : "See also: reference goals (what the optimizer aims for), reference optimizers (each algorithm and its options);\n" +
              "explain <file> --tunables lists every key a design offers; circuitrf opt <file> runs the optimize line,\n" +
              "and its flags override it for one run without changing the file.");
        return sb.ToString();
    }

    /// <summary>
    /// The goals page's two generated sections (brief-tuneopt-11 R-to11-6): how a goal is scored —
    /// <see cref="GoalResiduals"/>' own rule and tolerance — and the goal functions, which are the
    /// Optimizer window's template catalog (<see cref="GoalTemplates"/>) run over a bench declaring an
    /// S-parameter analysis, a WSProbe and an HB efficiency measure, so a template added there is a
    /// line here with nothing to edit.
    /// </summary>
    private static void AppendScoringAndFunctions(StringBuilder sb)
    {
        sb.AppendLine("How a goal is scored");
        sb.AppendLine();
        foreach (var line in Wrap(
            "Each grid point's violation is divided by the goal's scale (scale=, else the band's width for in and out, " +
            "else the larger of |limit| and 1 in the limit's own unit) and multiplied by its weight and by 1/sqrt(points), " +
            "so dB, degrees and ohms mix and a swept goal weighs the same as a single number. The cost is the sum of " +
            "their squares (cost=lsq) or the largest one (cost=minimax); a goal is met when its worst violation is within " +
            $"{GoalResiduals.MetTolerance.ToString("G2", System.Globalization.CultureInfo.InvariantCulture)} of its scale, " +
            "and every goal met is a cost of 0.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();

        sb.AppendLine("Goal functions — the Optimizer's templates, each as the goal line it writes. A template fills");
        sb.AppendLine("over=, lo= and hi= with the analysis's swept range; <limit> is yours. Output power, efficiency and");
        sb.AppendLine("PAE are a measure line you write and then name in a goal, as Eff below.");
        sb.AppendLine();
        var (_, bench) = new CircuitRF.Core.Netlist.CnlReader().Read(string.Join('\n', FunctionsBench), "tb", null);
        string? group = null;
        foreach (var t in GoalTemplates.For(bench))
        {
            string heading = t.Group switch
            {
                GoalTemplateGroup.SParameters  => "S-parameters",
                GoalTemplateGroup.WsProbe      => "WSProbe (stability-wsprobe metrics of one probe)",
                GoalTemplateGroup.Fields       => "Fields (SAR and B1 of a type=fields analysis, per watt as it normalises)",
                GoalTemplateGroup.Measurements => "Measurements (any measure line, by name)",
                _                              => "Custom",
            };
            if (heading != group) { sb.AppendLine("  " + heading); group = heading; }
            var g = t.Make();
            string type = t.Group is GoalTemplateGroup.Measurements or GoalTemplateGroup.Custom
                ? "le|ge" : g.Type.ToString().ToLowerInvariant();
            string limit = g.Limit.Length > 0 ? g.Limit : g.Type is GoalType.In or GoalType.Out ? "<a> <b>" : "<limit>";
            string expr = g.Expression.Length > 0 ? g.Expression : "<expression>";
            sb.AppendLine($"    {t.Label,-44} goal {g.Name} = {expr}{(g.Analysis is { } a ? $" analysis={a}" : "")} {type} {limit}");
        }
        sb.AppendLine();
        foreach (var line in Wrap(
            "The field functions read a type=fields analysis by name: B1pMean(F, region), B1pMin(F, region), B1pCoV(F, region), " +
            "psSAR(F, mass), SARwb(F, region), SarEff(F, region, mass) and Pabs(F). A region is a string: a tissue (\"brain\", or " +
            "its whole path \"head/brain\"), tissues joined by + (\"skin+skull\"), a phantom's name for all its layers, \"all\", or " +
            "\"box(x0, x1, y0, y1, z0, z1)\" with a length unit on each number (none is metres); omitted, the setup's own " +
            "(Sar.B1Region for B1, Sar.Region for SAR). A mass is in grams. Each value is per watt as the analysis's norm= says " +
            "(accepted by default): B1 in T per sqrt(W), SAR in W/kg per W, Pabs in W per W, SarEff in T per sqrt(W/kg). B1 and " +
            "SARwb read only their region's cells; psSAR is always the full average of the whole field, once per evaluation that " +
            "scores it, so a goal on psSAR is the expensive one; Pabs is free. A goal on them names the fields analysis " +
            "(analysis=F1) and runs no EM.", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
        // brief ENV-MEAS-2 R-envm2-4: the power through an IProbe, the everyday spelling of Pout, Pdc and PAE.
        foreach (var line in Wrap(
            "The probe-power functions read an envelope or harmonic-balance analysis by name, through an IProbe, in W " +
            "(first node to second): A.P(\"probe\") is the fundamental-band power, A.P(\"probe\", k) the power at harmonic k " +
            "(0 = DC, V0*I0) or at a mixing product by its tag (\"(1,0)\"), and A.Pt(\"probe\") the instantaneous " +
            "fundamental-band power against time (envelope, SaveWaveforms=yes). Every sweep axis passes through, so one line " +
            "is a number, a curve or a surface as sweeps are added; one point is at(A.P(\"probe\"), \"Pin\", i), i its position along Pin (0 first, -1 last). So: " +
            "measure Pout_dBm = dBm(ENV1.P(\"Iout\")), measure PAE_pct = 100*(ENV1.P(\"Iout\") - ENV1.P(\"Iin\"))/ENV1.P(\"IDC\", 0).", 96))
            sb.AppendLine("  " + line);
        sb.AppendLine();
    }

    /// <summary>The bench the goal-function list is generated over: one S-parameter analysis with a
    /// WSProbe, and an HB analysis with an efficiency measure built from two others.</summary>
    internal static readonly string[] FunctionsBench =
    [
        "f0 = 2 GHz",
        "Port:P1 in 0 Num=1 Z=50 Ohm",
        "WSProbe:PS in mid",
        "R:R1 mid out R=10 Ohm",
        "Port:P2 out 0 Num=2 Z=50 Ohm",
        "analysis SP1 type=sparam start=1 stop=2 npts=11 Unit=GHz",
        "analysis HB1 type=hb Tone=f0 MaxHarm=4",
        "measure Pout_W = 0.5*real(HB1.V(\"out\", 1)*conj(HB1.I(\"P2\", 1)))",
        "measure Pdc_W = real(HB1.V(\"vdd\", 0)*HB1.I(\"Vdd\", 0))",
        "measure Eff = 100*Pout_W/Pdc_W",
        "analysis F1 type=fields block=EM1 freq=298 freqUnit=MHz",
    ];

    /// <summary>A complete netlist both worked examples build on, so either one can be copied into a
    /// file and checked as it stands.</summary>
    private static readonly string[] ExampleCircuit =
    [
        "define DUT (a b)",
        "  parameters Rbias=1 kOhm",
        "  R:R3 a b R=10 Ohm",
        "  R:R4 b 0 R=Rbias",
        "end DUT",
        "",
        "Rload = 50 Ohm",
        "Zsrc = 40+15j Ohm",
        "Port:P1 in 0 Num=1 Z=50 Ohm",
        "R:R1 in mid R=50 Ohm",
        "DUT:X1 mid out Rbias=1 kOhm",
        "Port:P2 out 0 Num=2 Z=Rload",
        "analysis SP1 type=sparam start=0.5 stop=3 npts=51 Unit=GHz",
    ];

    private static readonly string[] TuningExample =
    [
        .. ExampleCircuit,
        "tune R1.R min=10 Ohm max=200 Ohm scale=log tune=1 opt=1",
        "tune Rload min=25 Ohm max=100 Ohm opt=1",
        "tune X1.Rbias min=500 Ohm max=2 kOhm tune=1",
        "tune DUT:R3.R min=5 Ohm max=50 Ohm opt=1",
        "tune mag(Zsrc) min=20 Ohm max=80 Ohm tune=1",
        "tune phase(Zsrc) min=-45 deg max=45 deg scale=lin tune=1",
        "preset \"wide band\" created=2026-10-07T12:00:00Z R1.R=47 Ohm Rload=60 Ohm Zsrc=45+10j Ohm",
        "optimize algorithm=lm maxiter=200 seed=1",
    ];

    private static readonly string[] GoalsExample =
    [
        .. ExampleCircuit,
        "tune R1.R min=10 Ohm max=200 Ohm opt=1",
        "tune Rload min=25 Ohm max=100 Ohm opt=1",
        "goal Gain = dB(SP1.S(2,1)) analysis=SP1 over=freq lo=1 GHz hi=2 GHz ge -12",
        "goal Match = dB(SP1.S(1,1)) analysis=SP1 over=freq lo=1 GHz hi=2 GHz le -15 to -10 weight=2",
        "goal Flat = dB(SP1.S(2,1)) analysis=SP1 over=freq lo=1 GHz hi=2 GHz in -12 -6",
        "goal Floor = Rload - 30 ge 0",
    ];

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }
}
