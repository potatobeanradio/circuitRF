using System.Globalization;

namespace CircuitRF.Core.Design;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
//  The optimizer's algorithm registry (brief-tuneopt-7 R-to7-7, overview D13): ONE list of what each
//  algorithm is called, when to use it, the options it reads with their defaults, which cost forms it
//  accepts and whether it differentiates. The Optimizer window's menu and tooltips, the `optimize`
//  directive's `algorithm=` summary, `check`, `reference tuning` (which the MCP reference serves
//  byte for byte) and the algorithms' own option defaults all read it — so none of them can describe
//  an algorithm, or a default, the others do not.
//
//  It is here, beside the setup that names an algorithm, because the `.cnl` reader and `check` read
//  the ids and src/Core references nothing above it. The algorithms themselves are numerics in
//  src/Engine/Optimization; `OptimizerFactory` there says which of these ids this build implements.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One option an algorithm reads from <c>alg.&lt;name&gt;=</c>. <paramref name="Default"/> is
/// the value text a run uses when the option is not given — a number, a word, or <c>auto</c> when the
/// default depends on the number of variables (the summary says how).</summary>
public sealed record OptimizerOptionInfo(string Name, string Default, string Summary)
{
    /// <summary>The words a word option accepts; null for a number option. A value outside them is a
    /// refusal at <c>check</c> and at Run, so an algorithm never receives one.</summary>
    public IReadOnlyList<string>? Choices { get; init; }

    /// <summary>The default as a number, or null when it is a word or <c>auto</c>.</summary>
    public double? NumericDefault
        => double.TryParse(Default, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
}

/// <summary>One algorithm on the menu.</summary>
/// <param name="Id">The stable id a file names (<c>algorithm=de</c>); never changes once written.</param>
/// <param name="Label">The menu label.</param>
/// <param name="UseWhen">One sentence: when to choose it — the menu tooltip and the reference line.</param>
/// <param name="Options">The options it reads, in the order the reference lists them.</param>
/// <param name="Costs">The cost forms it accepts. A method that accepts only one SETS it when chosen,
/// unless it is the other form the setup names explicitly — then the run is refused.</param>
/// <param name="NeedsGradients">Whether it estimates derivatives by finite differences — n extra
/// evaluations per step, and a smooth response assumed.</param>
public sealed record OptimizerAlgorithmInfo(
    string                             Id,
    string                             Label,
    string                             UseWhen,
    IReadOnlyList<OptimizerOptionInfo> Options,
    IReadOnlyList<OptimizerCost>       Costs,
    bool                               NeedsGradients)
{
    public bool Accepts(OptimizerCost cost) => Costs.Contains(cost);

    /// <summary>Whether it suits a noisy or piecewise-flat objective — a yield over finitely many trials, which design
    /// centering maximizes (brief-yield-11 R-ya11-4). A method that differentiates does not: a finite difference of a
    /// yield is zero almost everywhere.</summary>
    public bool SuitsNoisyObjective { get; init; }

    /// <summary>The option called <paramref name="name"/>, or null.</summary>
    public OptimizerOptionInfo? Option(string name) => Options.FirstOrDefault(o => o.Name == name);
}

/// <summary>The registry (R-to7-7). <see cref="All"/> is the menu, in the menu's order.</summary>
public static class OptimizerAlgorithms
{
    public const string Auto = "auto";

    private static readonly OptimizerCost[] Both    = [OptimizerCost.LeastSquares, OptimizerCost.Minimax];
    private static readonly OptimizerCost[] LsqOnly = [OptimizerCost.LeastSquares];
    private static readonly OptimizerCost[] MaxOnly = [OptimizerCost.Minimax];

    private static OptimizerOptionInfo O(string name, string def, string summary) => new(name, def, summary);

    private static readonly OptimizerOptionInfo FdStep =
        O("fdstep", "1e-6", "Finite-difference step, as a fraction of each variable's range.");

    /// <summary>Options every algorithm's run reads, whichever algorithm it is.</summary>
    public static IReadOnlyList<OptimizerOptionInfo> CommonOptions { get; } =
    [
        O("stall_tol",   "1e-9", "Stop when the best cost improves by less than this fraction of itself ..."),
        O("stall_iters", "25",   "... over this many iterations."),
    ];

    /// <summary>Every algorithm, in menu order. A file written today names the same algorithm tomorrow:
    /// an id is never renamed or reused.</summary>
    public static IReadOnlyList<OptimizerAlgorithmInfo> All { get; } =
    [
        new(Auto, "Auto",
            "When there is no reason to prefer another: CMA-ES for a modest budget, a local polish, then preferred values when any value is discrete.",
            [], Both, NeedsGradients: true),

        new("lm", "Gradient (Levenberg–Marquardt)",
            "When the start is close and the response is smooth: the fastest way to the nearest answer.",
            [FdStep, O("lambda", "1e-3", "Initial damping; larger starts closer to steepest descent.")],
            LsqOnly, NeedsGradients: true),

        new("bfgsb", "Quasi-Newton (BFGS-B)",
            "When the response is smooth and a variable may end on its bound.",
            [FdStep, O("memory", "5", "Curvature pairs remembered.")],
            Both, NeedsGradients: true),

        new("minimax", "Minimax",
            "For equal-ripple responses and worst-case specs: it lowers the single largest violation.",
            [FdStep,
             O("radius",    "0.1",  "Initial trust radius, as a fraction of each variable's range."),
             O("minradius", "1e-8", "The run ends when the trust radius falls below this.")],
            MaxOnly, NeedsGradients: true),

        new("simplex", "Simplex (Nelder–Mead)",
            "For a few variables when the response is noisy or not smooth and the start is reasonable.",
            [O("step",     "0.1",  "Initial simplex edge, as a fraction of each variable's range."),
             O("xtol",     "1e-6", "A simplex smaller than this has collapsed."),
             O("restarts", "1",    "Times a collapsed simplex is rebuilt around its best point.")],
            Both, NeedsGradients: false) { SuitsNoisyObjective = true },

        new("trust_region", "Trust-region model",
            "For 2 to 20 variables when each simulation is expensive and the response is smooth; no derivatives.",
            [O("points",    "auto", "Interpolation points; auto is 2n+1, at most (n+1)(n+2)/2."),
             O("radius",    "0.1",  "Initial trust radius, as a fraction of each variable's range."),
             O("minradius", "1e-6", "The run ends when the trust radius falls below this.")],
            Both, NeedsGradients: false),

        new("pattern", "Pattern search",
            "When some points fail to simulate or the response jumps, as an HB that does not always converge.",
            [O("poll",        "0.25", "Initial poll size, as a fraction of each variable's range."),
             O("minpoll",     "1e-6", "The run ends when the poll size falls below this."),
             O("speculative", "1",    "1 tries twice a successful step again beside the next poll; 0 does not."),
             O("model",       "1",    "1 adds the point a quadratic fit through the last poll predicts; 0 does not.")],
            Both, NeedsGradients: false) { SuitsNoisyObjective = true },

        new("random", "Random",
            "To survey the box or find a start for another method; it never converges, so set a limit.",
            [O("batch", "auto", "Points per batch; auto is max(8, 2n)."),
             O("lhs",   "1",    "1 samples each batch as a Latin hypercube; 0 as independent uniform points.")],
            Both, NeedsGradients: false),

        new("de", "Differential evolution",
            "From a bad start or on a response with many local minima: the genetic-family method.",
            [O("population",    "auto", "Initial population; auto is 18n."),
             O("minpopulation", "4",    "Population at the end of the budget; it shrinks linearly to this."),
             O("budget",        "auto", "Evaluations the population schedule plans for; auto is maxevals, else 1000n."),
             O("memory",        "6",    "Success-history entries remembered for the crossover rate and scale factor."),
             O("pbest",         "0.11", "Fraction of the population the current-to-pbest mutation draws from."),
             O("archive",       "2.6",  "Archive of replaced parents, as a multiple of the population."),
             O("xtol",          "1e-9", "The run ends when every member is this close to the best.")],
            Both, NeedsGradients: false),

        new("pso", "Particle swarm",
            "Like differential evolution, a global search from a bad start; often faster when variables interact weakly.",
            [O("swarm",    "auto", "Particles; auto is max(30, 10n)."),
             O("topology", "ring", "ring (each particle follows its two neighbours) or global (all follow the best).") with { Choices = ["ring", "global"] },
             O("vmax",     "0.5",  "Velocity limit, as a fraction of each variable's range per step."),
             O("c1",       "2.05", "Pull toward a particle's own best."),
             O("c2",       "2.05", "Pull toward its neighbourhood's best."),
             O("xtol",     "1e-9", "The run ends when every particle's best is this close to the swarm's and none moves.")],
            Both, NeedsGradients: false),

        new("cmaes", "CMA-ES",
            "On a multi-modal or badly scaled response; it restarts with a larger population when it stalls.",
            [O("sigma",    "0.3",   "Initial step size, as a fraction of each variable's range."),
             O("popsize",  "auto",  "Samples per generation; auto is 4 + 3 ln n."),
             O("ipop",     "1",     "1 restarts with twice the population when a run stalls; 0 stops instead."),
             O("restarts", "9",     "Restarts allowed."),
             O("tolfun",   "1e-12", "A run whose recent costs all lie within this of each other has stalled."),
             O("tolx",     "1e-11", "A run whose step size has fallen below this has stalled.")],
            Both, NeedsGradients: false) { SuitsNoisyObjective = true },

        new("bayes", "Bayesian (slow simulations)",
            "When each simulation takes seconds or more (HB, loadpull, EM): it spends computation choosing each point, O(N³) in the points it keeps.",
            [O("initial", "auto", "Initial Latin-hypercube points, the start among them; auto is 2n+1."),
             O("archive", "500",  "Most points the surrogate is fitted to; the most recent are kept, and the best."),
             O("batch",   "1",    "Points chosen per iteration by the constant liar; the run sets it to parallel= when the setup states one."),
             O("tr_dims", "10",   "Above this many variables a trust-region variant replaces the global surrogate.")],
            Both, NeedsGradients: false) { SuitsNoisyObjective = true },

        new("discrete", "Discrete",
            "When every variable is an integer, a step or a preferred value: it searches that grid itself.",
            [O("cap",      "2000", "Grids of at most this many points are searched exhaustively."),
             O("block",    "auto", "Grid points per batch; auto is a twentieth of the grid."),
             O("restarts", "10",   "Random restarts of the coordinate descent a larger grid gets.")],
            Both, NeedsGradients: false) { SuitsNoisyObjective = true },
    ];

    /// <summary>Every id, in menu order.</summary>
    public static IReadOnlyList<string> Ids { get; } = [.. All.Select(a => a.Id)];

    /// <summary>The ids that suit a noisy objective, in menu order — what design centering offers.</summary>
    public static IReadOnlyList<string> ForNoisyObjective { get; } = [.. All.Where(a => a.SuitsNoisyObjective).Select(a => a.Id)];

    /// <summary>The entry for <paramref name="id"/>, or null.</summary>
    public static OptimizerAlgorithmInfo? Find(string id) => All.FirstOrDefault(a => a.Id == id);

    /// <summary>brief-sdl-7 D3 — the algorithms <c>em=direct</c> accepts: each works in few evaluations, and every
    /// evaluation there is an EM solve. A population method would spend the budget on its first generation.</summary>
    public static IReadOnlyList<string> ForEmDirect { get; } = ["pattern", "simplex", "trust_region", "bayes"];

    /// <summary>What <c>auto</c> runs under <c>em=direct</c>: pattern search, which tolerates the steps a geometry
    /// rounded to its database unit takes.</summary>
    public const string EmDirectAuto = "pattern";
}
