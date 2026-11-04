namespace CircuitRF.Core.Netlist;

/// <summary>
/// One of the directives of a TestBench's tuning setup — <c>tune</c>, <c>preset</c>, <c>goal</c>,
/// <c>optimize</c>, and the statistical <c>correlate</c>, <c>statistics</c>, <c>center</c>, <c>doe</c> and <c>corner</c> — with every
/// key it takes and the bare words its grammar uses.
/// </summary>
/// <param name="Keyword">The line's first word.</param>
/// <param name="Topic">The reference topic that prints it: <c>tuning</c>, <c>goals</c> or <c>statistics</c>.</param>
/// <param name="Syntax">The line's shape, for the reference page.</param>
/// <param name="Summary">One paragraph: what the line says.</param>
/// <param name="Keys">Every <c>key=value</c> key the reader knows, in the order the writer writes them.</param>
/// <param name="BareWords">Bare words the grammar uses, with what each means.</param>
/// <param name="Example">One line, as the writer writes it.</param>
public sealed record TuningDirectiveSpec(
    string                                   Keyword,
    string                                   Topic,
    string                                   Syntax,
    string                                   Summary,
    IReadOnlyList<AnalysisDirectiveKey>      Keys,
    IReadOnlyList<(string Word, string Summary)> BareWords,
    string                                   Example);

public static partial class AnalysisDirectiveSchema
{
    /// <summary>The <c>tuning</c> reference topic's name.</summary>
    public const string TuningTopic = "tuning";

    /// <summary>The <c>goals</c> reference topic's name.</summary>
    public const string GoalsTopic = "goals";

    /// <summary>The <c>statistics</c> reference topic's name (docs/design/yield.md).</summary>
    public const string StatisticsTopic = "statistics";

    // Enum spellings — the reader parses against these and the page prints them, so the two
    // cannot disagree about what is legal.
    public static readonly IReadOnlyList<string> ScaleTokens    = ["auto", "lin", "log"];
    public static readonly IReadOnlyList<string> DiscreteTokens = ["none", "integer", "preferred", "e6", "e12", "e24", "e48", "e96"];
    public static readonly IReadOnlyList<string> GoalTypeTokens = ["le", "ge", "eq", "in", "out"];
    public static readonly IReadOnlyList<string> CostTokens     = ["lsq", "minimax"];
    public static readonly IReadOnlyList<string> ScopeTokens    = ["goals", "all"];
    /// <summary>brief-sdl-7 D1 — <c>em=</c> on <c>optimize</c>, in <see cref="CircuitRF.Core.Design.OptimizerEmMode"/>'s order.</summary>
    public static readonly IReadOnlyList<string> EmModeTokens   = ["off", "correct", "direct"];
    public static readonly IReadOnlyList<string> DistTokens     = ["none", "gauss", "unif", "lognorm", "discrete"];
    public static readonly IReadOnlyList<string> UseTokens      = ["both", "opt", "yield"];
    public static readonly IReadOnlyList<string> SamplingTokens = ["random", "lhs", "sobol"];
    public static readonly IReadOnlyList<string> NonConvergedTokens = ["fail", "warn"];
    public static readonly IReadOnlyList<string> SurrogateTokens = ["none", "quadratic"];
    public static readonly IReadOnlyList<string> DoeDesignTokens   = ["full2", "frac", "pb", "ccf"];
    public static readonly IReadOnlyList<string> DoeFactorTokens   = ["opt", "stat"];
    public static readonly IReadOnlyList<string> DoeResponseTokens = ["goals", "all"];
    public static readonly IReadOnlyList<string> SensFactorTokens  = ["all", "stat", "opt"];

    /// <summary>Each distribution a <c>tune</c> line's <c>dist=</c> names: the spread keys it reads, what
    /// they mean, and one line as the writer writes it (yield overview D2).</summary>
    public static readonly IReadOnlyList<(string Dist, string Spread, string Meaning, string Example)> Distributions =
    [
        ("gauss", "sd=<v> | tol=<v> sigmas=<k> [trunc=<k>]",
         "Normal around the nominal. sd is 1 sigma; tol is the spread at sigmas sigma, so tol=5% sigmas=3 is sigma = 5/3 %.",
         "tune R1.R dist=gauss sd=2%"),
        ("unif", "tol=<v> | lo=<v> hi=<v>",
         "Uniform on nominal +- tol, or on [lo, hi]. On a whole-number value the draw is rounded.",
         "tune C1.C dist=unif tol=0.1 pF"),
        ("lognorm", "sd=<v> | tol=<v> sigmas=<k> [trunc=<k>]",
         "The value's logarithm is normal; sd is the value's relative 1 sigma. Positive values only, and it never draws one at or below zero.",
         "tune L1.L dist=lognorm tol=5% sigmas=3"),
        ("discrete", "lo=<v> hi=<v> by=<v>",
         "Equally likely values lo, lo+by, ... up to hi; by must step from lo to hi a whole number of times.",
         "tune X1.Nf dist=discrete lo=4 hi=8 by=2"),
    ];

    /// <summary>The keys of a <c>tune</c> line that state its statistical part, in the order the writer
    /// writes them.</summary>
    public static readonly IReadOnlyList<string> StatKeys = ["stat", "dist", "sd", "tol", "sigmas", "lo", "hi", "by", "trunc"];

    /// <summary>The prefix of an algorithm option key on an <c>optimize</c> line: <c>alg.popsize=20</c>.</summary>
    public const string AlgorithmOptionPrefix = "alg.";

    private static readonly TuningDirectiveSpec[] _tuning =
    [
        new("tune", TuningTopic,
            "tune <key> [kind=same | parts=\"<query>\"] [min=<v> [unit]] [max=<v> [unit]] [scale=…] [step=<v> [unit]] [discrete=…] [tune=1] [opt=1] " +
            "[dist=… <spread>] [stat=0]",
            "Makes one value tunable and states its range. <key> is Instance.Parameter (R1.R), a variable's name " +
            "(Wline), or either of those inside a sub-cell as Cell:… (DUT:R3.R, DUT:Wline), where Cell is spelled as " +
            "the cell's instance lines spell its type. An instance's own cell parameter is Instance.Parameter " +
            "(X1.Rbias), one key per instance. Only a value written as a plain number with an optional unit can be " +
            "tuned; an expression cannot, and the variable it reads can. A complex value written with numbers only " +
            "(40+15j, complex(40,15), polar(42.7,20.6), with an optional unit) is tuned by its parts, each a key of " +
            "its own: real(<key>), imag(<key>), mag(<key>) and phase(<key>), the phase in deg. Any parts may be " +
            "combined; moving one holds its partner (real with imag, mag with phase), and the ranges of all of a " +
            "value's parts always hold together, so ranges that leave no value inside all of them are an error. " +
            "A complex value that reads a name (4+j*X) is an expression. One range serves both tuning and optimizing. " +
            "dist= and its spread give the value a tolerance for Monte Carlo and yield; reference statistics describes them. " +
            "A placed vendor library part offers one key, Instance.Part, whose value is the part itself: its candidates " +
            "are the parts of the same kind (kind=same, the default) or a part search over its own library (parts=), " +
            "narrowed by min/max on the stated value, at most 200 of them; the optimizer chooses among them, and a " +
            "part is never toleranced.",
            [
                new("kind",     Default: "same", Summary: "Instance.Part only: same = the parts of the same kind as the placed one (same file, same Description fields but the value)."),
                new("parts",    Summary: "Instance.Part only: a part search over the part's own library, quoted (parts=\"0603 C0G 50V\"); the same grammar as the palette's search. Not with kind=."),
                new("min",      Summary: "Lower bound, in the value's own unit. On Instance.Part: the lowest stated value a candidate may have."),
                new("max",      Summary: "Upper bound."),
                new("scale",    Default: "auto", Summary: "auto | lin | log. auto is log when min > 0 and max/min >= 10."),
                new("step",     Summary: "Slider step. Absent = continuous."),
                new("discrete", Default: "none", Summary: "none | integer | preferred | e6 | e12 | e24 | e48 | e96 — preferred snaps a capacitance, inductance or resistance to the user's preferred-value ladder (shipped: E12 C and L, E24 R); e6 … e96 snap any positive value to that IEC 60063 series. Not on a part of a complex value."),
                new("tune",     Default: "0", Summary: "1 = offered in the Tuning window."),
                new("opt",      Default: "0", Summary: "1 = varied by the optimizer."),
                new("stat",     Default: "1 with dist", Summary: "0 keeps the distribution but does not draw it."),
                new("dist",     Default: "none", Summary: "gauss | unif | lognorm | discrete — the distribution a Monte Carlo trial draws from."),
                new("sd",       Summary: "gauss, lognorm: 1 sigma, a percent of the nominal (2%) or a value in the parameter's unit."),
                new("tol",      Summary: "gauss, lognorm: the spread at sigmas sigma. unif: the half-width around the nominal. tol=part on a placed vendor part's value (C1.C, L2.L) is that part's own tolerance, read from its part number or header."),
                new("sigmas",   Summary: "gauss, lognorm: how many sigma tol is (tol=5% sigmas=3 is sigma = 5/3 %). Needed with tol."),
                new("lo",       Summary: "unif, discrete: the lower end, absolute or a percent of the nominal."),
                new("hi",       Summary: "unif, discrete: the upper end."),
                new("by",       Summary: "discrete: the step from lo to hi."),
                new("trunc",    Summary: "gauss, lognorm: truncate at +-trunc sigma, by sampling the truncated distribution."),
            ],
            [],
            "tune R1.R min=10 Ohm max=200 Ohm scale=log tune=1 opt=1 dist=gauss sd=2%"),

        new("preset", TuningTopic,
            "preset \"<name>\" [created=<UTC>] [lasttuned=1] [cost=<c>] <key>=<v> [unit] ...",
            "A named set of tuned values. The values are the text the schematic would hold, so recalling one is " +
            "the same as typing it — a complex value whole, under its own key (Zsrc=40+15j Ohm), never by its parts. " +
            "created, lasttuned and cost come before the values. A key that names nothing in the " +
            "design is skipped when the preset is recalled, and check reports it as a warning.",
            [
                new("created",   Summary: "When it was locked in: yyyy-MM-ddTHH:mm:ssZ."),
                new("lasttuned", Default: "0", Summary: "1 = the automatic \"Last tuned\" preset (at most one)."),
                new("cost",      Summary: "The optimizer's cost at these values, when the Optimizer locked them in."),
            ],
            [],
            "preset \"wide band\" created=2026-10-07T12:00:00Z R1.R=47 Ohm DUT:Wline=212 um"),

        new("goal", GoalsTopic,
            "goal <Name> = <expression> [analysis=<A>] [over=<axis> lo=<v> [unit] hi=<v> [unit]] <type> <limit(s)> " +
            "[weight=<w>] [scale=<v> [unit]] [enabled=false] [use=opt|yield]",
            "One optimization goal. The expression is written in the measure language and is evaluated on the " +
            "named analysis; quote it if it contains a word this table uses. over= restricts it to one swept axis " +
            "(freq by default), lo..hi inclusive, and the range must hold at least one grid point. A goal with no " +
            "analysis reads variables only and costs no simulation. Inside a goal line, a length in inches is " +
            "spelled inch, because in is a goal type.",
            [
                new("analysis", Summary: "The analysis the expression reads."),
                new("over",     Default: "freq", Summary: "The swept axis lo and hi are on: freq, a sweep variable, a power or harmonic axis."),
                new("lo",       Summary: "Low end of the range on that axis, inclusive."),
                new("hi",       Summary: "High end of the range, inclusive."),
                new("weight",   Default: "1", Summary: "Multiplies this goal's violation in the cost."),
                new("scale",    Summary: "What one unit of violation is worth. Default: the band's width for in/out, otherwise the larger of |limit| and 1 in the limit's own unit."),
                new("enabled",  Default: "true", Summary: "false keeps the goal without optimizing for it."),
                new("use",      Default: "both", Summary: "opt | yield | both — the optimizer aims for it, a yield trial passes only when it is met, or both."),
            ],
            [
                ("le",  "le <limit> [to <limit>] — at or below. With to, the limit slopes linearly from lo to hi."),
                ("ge",  "ge <limit> [to <limit>] — at or above."),
                ("eq",  "eq <limit> [to <limit>] — equal to."),
                ("in",  "in <a> <b> — inside the band [a, b]."),
                ("out", "out <a> <b> — outside the band [a, b]."),
                ("to",  "Separates a sloped limit's value at lo from its value at hi."),
            ],
            "goal G1 = dB(SP1.S(2,1)) analysis=SP1 over=freq lo=1 GHz hi=2 GHz ge -0.5"),

        new("optimize", TuningTopic,
            "optimize [algorithm=<id>] [maxiter=<n>] [maxevals=<n>] [timelimit=<v> [unit]] [cost=…] [analyses=…] " +
            "[seed=<n>] [parallel=<n>] [corners=…] [nominal=0] [em=off|correct|direct] [emBudget=<n>] [alg.<option>=<v>] ...",
            "The optimizer's settings. At most one line. Keys starting alg. are the chosen algorithm's own options.",
            [
                new("algorithm", Default: "auto", Summary: "One of: " + string.Join(", ", CircuitRF.Core.Design.OptimizerAlgorithms.Ids) + ". reference optimizers describes each."),
                new("maxiter",   Summary: "Iteration limit."),
                new("maxevals",  Summary: "Evaluation limit."),
                new("timelimit", Summary: "Wall-clock limit: a number and s, ms, min or h (60 s)."),
                new("cost",      Default: "lsq", Summary: "lsq (sum of squared violations) | minimax (the worst one)."),
                new("analyses",  Default: "goals", Summary: "goals (only the analyses a goal names) | all."),
                new("seed",      Summary: "Random seed, for a repeatable run."),
                new("parallel",  Summary: "How many evaluations may run at once."),
                new("corners",   Default: "none", Summary: "none | all | corner names separated by commas: every goal must be met at the " +
                                                           "nominal and at each of these corners at once — one evaluation per corner per point."),
                new("nominal",   Default: "1", Summary: "With corners, 0 leaves the nominal out: the corners alone are scored."),
                new("em",        Default: "correct when a goal reads an analysis run by EM, else off",
                                 Summary: "off | correct | direct. off: every evaluation by circuit models. correct: search on the " +
                                          "circuit models, then correct them with a few EM solves of the layout. direct: every " +
                                          "evaluation is an EM solve (cached by geometry); pattern, simplex, trust_region or bayes only."),
                new("emBudget",  Default: "12 for correct, 30 for direct",
                                 Summary: "The most EM solves the run may make (cached geometries are free); it stops there and keeps " +
                                          "its best EM-verified point."),
                new(AlgorithmOptionPrefix + "<option>", Summary: "An option of the chosen algorithm."),
            ],
            [],
            "optimize algorithm=lm maxiter=200 seed=1"),

        new("correlate", StatisticsTopic,
            "correlate <key> <key> rho=<v>",
            "Correlates two statistical entries, of any distributions, through a Gaussian copula: the two draws are " +
            "correlated standard normals, each mapped through its own distribution. -1 < rho < 1. A set of " +
            "correlations that is not a valid correlation matrix is repaired to the nearest one, and the repair is " +
            "reported with its largest change.",
            [
                new("rho", Summary: "The correlation coefficient, strictly between -1 and 1."),
            ],
            [],
            "correlate R1.R R2.R rho=0.9"),

        new("statistics", StatisticsTopic,
            "statistics [trials=<n>] [seed=<n>] [sampling=…] [target=<p>%] [confidence=<p>%] [autostop=1] " +
            "[nonconverged=…] [save=…] [process=0] [mismatch=0] [sigmascale=<k>] [parallel=<n>] [analyses=…] [corners=…] " +
            "[nominalparts=<part>,<part>]",
            "The Monte Carlo and yield settings. At most one line, and a default is never written.",
            [
                new("trials",       Default: CircuitRF.Core.Design.StatisticsSettings.DefaultTrials.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                    Summary: "How many trials; with autostop=1, the most it runs."),
                new("seed",         Default: "1", Summary: "The random seed: the same seed draws the same trials."),
                new("sampling",     Default: "random", Summary: "random | lhs (Latin hypercube; not with autostop) | sobol (scrambled low-discrepancy)."),
                new("target",       Summary: "The yield the design must reach, a percent (95%)."),
                new("confidence",   Default: "95%", Summary: "The confidence of the yield interval."),
                new("autostop",     Default: "0", Summary: "1 = stop once the yield interval is wholly above or below target (needs target)."),
                new("nonconverged", Default: "fail", Summary: "fail (a trial that does not evaluate counts as a fail) | warn (it is left out of the yield)."),
                new("save",         Default: "auto", Summary: "auto | scalars | all | <n> — which trials keep their whole sweep."),
                new("process",      Default: "1", Summary: "0 = no kit process draws."),
                new("mismatch",     Default: "1", Summary: "0 = no kit mismatch draws."),
                new("sigmascale",   Default: "1", Summary: "Multiplies every kit sigma."),
                new("parallel",     Summary: "How many trials may run at once."),
                new("analyses",     Default: "goals", Summary: "goals (only the analyses a yield goal names) | all."),
                new("corners",      Default: "none", Summary: "none | all | <name>,<name> — the corners each trial runs at."),
                new("nominalparts", Default: "none", Summary: "Library part numbers, separated by commas, whose own " +
                                    "distributions are held at their nominal (the Yield panel's Library parts group, unticked). " +
                                    "mismatch=0 holds every part's too: a draw inside a part's model is a mismatch draw."),
            ],
            [],
            "statistics trials=500 seed=7 sampling=lhs target=95%"),

        new("center", StatisticsTopic,
            "center [algorithm=<id>] [trials=<M>] [verify=<n>] [maxiter=<n>] [maxevals=<n>] [timelimit=<v> [unit]] " +
            "[width=<w>] [parallel=<n>] [seed=<n>] [surrogate=none|quadratic]",
            "Design centering: moves the nominals of the opt=1 entries, within their ranges, to maximize the yield " +
            "against the use=yield|both goals, while their tolerances ride along (a percent spread follows the moved " +
            "nominal). Every candidate is scored on ONE fixed set of M trials, so two candidates differ by the design and " +
            "not by sampling noise. The algorithm sees a smooth yield — per trial, the smallest margin over the yield " +
            "goals divided by each goal's scale, through a logistic of width w — and the plain yield on the same trials " +
            "is reported beside it. The best point and the start are then each run on verify fresh trials (the next " +
            "seed) with their intervals. At most one line, and a default is never written.",
            [
                new("algorithm", Default: CircuitRF.Core.Design.CenteringSettings.DefaultAlgorithm,
                                 Summary: "One of: " + string.Join(", ", CircuitRF.Core.Design.OptimizerAlgorithms.ForNoisyObjective) +
                                          " — the registry's algorithms that suit a yield's piecewise-flat objective."),
                new("trials",    Default: CircuitRF.Core.Design.CenteringSettings.DefaultTrials.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                 Summary: "M: the common trials every candidate is scored on. One candidate costs M simulations."),
                new("verify",    Default: CircuitRF.Core.Design.CenteringSettings.DefaultVerify.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                 Summary: "Fresh trials the start and the best point are each verified on."),
                new("maxiter",   Default: "100", Summary: "Iteration limit."),
                new("maxevals",  Summary: "Simulation limit, verification not counted."),
                new("timelimit", Summary: "Wall-clock limit: a number and s, ms, min or h."),
                new("width",     Default: "0.05", Summary: "The logistic's width, as a fraction of each goal's scale."),
                new("parallel",  Summary: "How many simulations may run at once."),
                new("seed",      Default: "1", Summary: "The seed of the common trials; the verification uses the next one."),
                new("surrogate", Default: "none",
                                 Summary: "quadratic: each candidate's yield-goal margins are fitted as a quadratic in the " +
                                          "statistical variables from 2k+1 simulations (plus the cross terms when k <= 12) " +
                                          "and a few common trials, and the yield is estimated on 10,000 virtual trials of " +
                                          "the fit. A search accelerator: the result's yield is always the simulated " +
                                          "verification, and three poor fits running switch back to simulated trials."),
            ],
            [],
            "center algorithm=cmaes trials=200 verify=1000 maxiter=40"),

        new("doe", StatisticsTopic,
            "doe [design=full2|frac|pb|ccf] [resolution=4|5] [factors=opt|stat] [levels=range|sigma:<k>] [centre=<n>] " +
            "[responses=goals|all] [parallel=<n>]",
            "Design of experiments: a structured set of runs over the factors — the opt=1 entries at the ends of their " +
            "ranges, or the stat=1 entries at nominal +- k sigma — then each response's main effects and two-factor " +
            "interactions on coded -1/+1 factors, with Lenth's margin separating active effects from noise-sized ones. " +
            "A fractional or Plackett-Burman design reports what each effect is confounded with. Simulation is " +
            "deterministic, so runs are neither replicated nor randomized. At most one line, and a default is never written.",
            [
                new("design",     Default: "full2",
                                  Summary: "full2 (2^k runs; at most 10 factors) | frac (2^(k-p) at resolution=, from a published " +
                                           "minimum-aberration table) | pb (Plackett-Burman, 12, 20 or 24 runs: screening many " +
                                           "factors) | ccf (face-centred central composite: a quadratic model, to find a best point)."),
                new("resolution", Default: "4", Summary: "frac: 4 (main effects clear of two-factor interactions) | 5 (two-factor interactions clear of each other)."),
                new("factors",    Default: "opt", Summary: "opt (the opt=1 entries over their ranges) | stat (the stat=1 entries at nominal +- k sigma)."),
                new("levels",     Default: "range (opt), sigma:1 (stat)",
                                  Summary: "range with factors=opt; sigma:<k> with factors=stat — the level is z = +-k through the " +
                                           "entry's own distribution, exactly nominal +- k sigma for a Gaussian."),
                new("centre",     Default: "1", Summary: "Centre points added — they check for curvature; 0 adds none."),
                new("responses",  Default: "goals",
                                  Summary: "goals (the goals the factors are for — use=opt|both with opt factors, use=yield|both " +
                                           "with stat factors) | all (every enabled goal). Every scalar measure is a response too."),
                new("parallel",   Summary: "How many simulations may run at once."),
            ],
            [],
            "doe design=frac resolution=5 centre=3"),

        new("sensitivity", StatisticsTopic,
            "sensitivity [factors=all|stat|opt] [step=<pct>%] [sigma=<k>] [ports=on|off] [snp=on|off] [snpexclude=<inst>,<inst>] " +
            "[snpdb=<dB>] [snpdeg=<deg>] [responses=goals|all] [parallel=<n>]",
            "Sensitivity: which values move each goal most at the nominal design. Each factor is stepped + and - on its " +
            "own, every other value at nominal (2k+1 simulations, no random draws): a toleranced value by +-k sigma of its " +
            "own distribution, any other by +-step of its value. Per goal, each factor's half-difference of the goal's " +
            "normalized margin, its share of the sum of squares, and a nonlinear flag where the two steps disagree. The " +
            "settings the Sensitivity panel and yield sens run with; at most one line, and a default is never written.",
            [
                new("factors",    Default: "all",
                                  Summary: "all (every tunable: a toleranced one at +-k sigma, any other at +-step) | stat (the " +
                                           "stat=1 entries at +-k sigma) | opt (the opt=1 entries at +-step of their value)."),
                new("step",       Default: "1%", Summary: "The +- step of a value with no tolerance, a percent of its value. A phase steps +-1 deg."),
                new("sigma",      Default: "1", Summary: "k: a toleranced value steps +-k sigma through its own distribution."),
                new("ports",      Default: "off", Summary: "on | off: whether factors=all steps the ports' own values (their reference impedances). A port named on an opt=1 or stat=1 line is stepped either way."),
                new("snp",        Default: "on", Summary: "on | off: whether an SnP block's S-parameters are factors. off keeps the snpexclude list, unused."),
                new("snpexclude", Summary: "SnP instances left out, separated by commas, named as their keys name them (SNP4, DUT:SNP4). Wins over a tolerance; a name that is no SnP instance is a warning, ignored."),
                new("snpdb",      Default: "0.1", Summary: "The +- step of an SnP entry's magnitude with no tolerance, in dB."),
                new("snpdeg",     Default: "1", Summary: "The +- step of an SnP entry's phase with no tolerance, in degrees."),
                new("responses",  Default: "goals",
                                  Summary: "goals (the enabled use=yield|both goals, or the use=opt|both ones when there are none) | " +
                                           "all (every enabled goal). Every scalar measure is a response too."),
                new("parallel",   Summary: "How many simulations may run at once."),
            ],
            [],
            "sensitivity factors=stat sigma=2"),

        new("corner", StatisticsTopic,
            "corner <Name> [enabled=0] [trial=<n> seed=<n> sampling=… trials=<n>] [temp=<v>] [part:<part>=<corner>] <key>=<v> [unit] ...",
            "A named corner: a value for the ambient temperature (temp, in degC) and for any global variable or " +
            "tunable key. A schematic's corner also selects the kit's own corner sections, and netlisting it writes " +
            "the values those sections bind. A statistical corner names a trial of a Monte Carlo run instead — " +
            "trial= with that run's seed, sampling and trials — and replays its draws around the current nominal. " +
            "enabled and the trial keys come before the values. part:<part number>=<corner> sets a library part's own " +
            "corner (a corner line inside its model's define) on EVERY instance of that part — one lot — overriding " +
            "each instance's Corner; an instance's explicit value still wins, and in a Monte Carlo at this corner that " +
            "part's own distributions are held at nominal (the corner is its limit).",
            [
                new("enabled",  Default: "1", Summary: "0 keeps the corner without running it."),
                new("trial",    Summary: "A statistical corner: the trial to replay, from 1."),
                new("seed",     Summary: "With trial: the seed of the run it came from."),
                new("sampling", Summary: "With trial: the sampling of the run it came from."),
                new("trials",   Summary: "With trial: the trial count of the run it came from."),
                new("temp",     Summary: "The ambient temperature, in degC."),
            ],
            [],
            "corner SS_hot temp=85 part:TSTFET100=min Vdd=3.0 V R1.R=47 Ohm"),
    ];

    /// <summary>The ten directives, in the order a <c>.cnl</c> writes them.</summary>
    public static IReadOnlyList<TuningDirectiveSpec> TuningDirectives => _tuning;

    /// <summary>The directive spec for a keyword, or null.</summary>
    public static TuningDirectiveSpec? FindTuningDirective(string keyword)
        => _tuning.FirstOrDefault(s => s.Keyword.Equals(keyword, StringComparison.Ordinal));

    /// <summary>True when <paramref name="key"/> is one this directive knows (case-insensitive;
    /// <c>alg.*</c> on <c>optimize</c>).</summary>
    public static bool IsKnownTuningKey(TuningDirectiveSpec spec, string key)
        => spec.Keys.Any(k => k.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
        || (spec.Keyword == "optimize" && key.StartsWith(AlgorithmOptionPrefix, StringComparison.OrdinalIgnoreCase)
            && key.Length > AlgorithmOptionPrefix.Length);
}
