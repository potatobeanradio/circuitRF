namespace CircuitRF.WBond.Mom;

/// <summary>
/// What a distributed (MoM) run will cost, <b>before it costs it</b> — RW2, and WM-3 §5/§6.
///
/// <code>
/// setup       ~ a·N_s²  +  b·N_s³
/// per point   ~ c·N_s³
/// sweep       ~ setup   +  points · per point / threads
/// </code>
///
/// <h3>The constants are measured, and they are stamped with where</h3>
/// <para>Fitted from the WM-3 §1 table taken on <b>Apple Silicon (M-series, 10 cores), Release,
/// .NET 10, 2026-08-18</b>, after M1's explicit inverses and M2's complex-symmetric factorisation. The
/// same convention <see cref="PotentialCoefficients.FarThresholdFactor"/> uses: a measured constant
/// with no machine and no date on it is a number nobody can ever re-derive or challenge.</para>
///
/// <h3>It is an estimate on unknown hardware and it is gated as one</h3>
/// <para>A prediction that is systematically wrong is worse than none, so a routine test asserts the
/// predicted sweep time at size S is within <b>2×</b> of the measured one. Two× is deliberately loose:
/// this is a user-facing "should I press Run" number on a machine that may be a third the speed of the
/// one it was fitted on, not a contract.</para>
///
/// <h3>Why the cubic term dominates everything, and why segmentation is the real knob</h3>
/// <para><c>N_s = wires × segments-per-wire</c> and both the setup and the per-point cost are cubic in
/// it, so <b>halving the segment count is an eightfold speedup</b> — larger than every other
/// optimisation in WM-3 put together. That is why <see cref="SegmentsForBudget"/> exists and why the
/// ladder in <see cref="WireMomSettings.Ladder"/> is presented to the user rather than buried.</para>
/// </summary>
public static class WireMomCost
{
    // ---- the fitted constants. Apple Silicon, Release, .NET 10, 2026-08-18. See RESOLVED.md for the
    // table they were fitted to and the residuals.

    /// <summary>The quadratic term of the setup, in seconds per N_s² — the two fills and the two scatters.</summary>
    public const double SetupQuadraticCoefficient = 2.94e-7;

    /// <summary>The cubic term of the setup, in seconds per N_s³ — the two Choleskys and the two inverses.</summary>
    public const double SetupCubicCoefficient = 2.51e-10;

    /// <summary>
    /// The per-frequency cubic term, in seconds per N_s³ — <see cref="ComplexLdlt"/>'s <c>N³/3</c>.
    /// </summary>
    public const double PerPointCubicCoefficient = 1.25e-10;

    /// <summary>
    /// The per-frequency quadratic term, in seconds per N_s² — forming <c>M̃</c> and the T solves.
    ///
    /// <para><b>WM-3 §6 writes the per-point cost as <c>c·N³</c> alone; that is 0.65× at N_s = 192</b>,
    /// because at the small end the quadratic assembly of <c>M̃</c> is a third of the point. Since the
    /// prediction's own accuracy gate is taken at size S, dropping the term would have made the gate
    /// measure the model's missing term rather than the machine.</para>
    /// </summary>
    public const double PerPointQuadraticCoefficient = 1.37e-8;

    /// <summary>
    /// How far short of <c>×threads</c> the frequency-parallel sweep falls, as the serial fraction of an
    /// Amdahl curve: <c>speedup = threads / (1 + f(threads − 1))</c>.
    ///
    /// <para><b>Measured 4.00× at size S, 3.83× at M and 2.97× at reduced-L on ten cores</b> — not 10×,
    /// and not because of the fan-out. Each point's factorisation streams an N_s × N_s complex matrix
    /// repeatedly, so ten of them at once are competing for memory bandwidth rather than for cores.
    /// <c>f = 0.167</c> is what reproduces 4× at ten threads. <b>A prediction that assumed linear
    /// scaling would be 2.5× optimistic on exactly the runs where the number matters.</b></para>
    /// </summary>
    public const double ParallelContentionFraction = 0.167;

    /// <summary>
    /// Predicted seconds for everything frequency-independent: both fills, both inverses, and
    /// <c>K̃</c>/<c>W</c>/<c>H</c>.
    /// </summary>
    public static double SetupSeconds(int segments)
    {
        double n = segments;
        return SetupQuadraticCoefficient * n * n + SetupCubicCoefficient * n * n * n;
    }

    /// <summary>Predicted seconds for one frequency point: <c>M̃</c>, its factorisation and the T solves.</summary>
    public static double PerPointSeconds(int segments)
    {
        double n = segments;
        return PerPointCubicCoefficient * n * n * n + PerPointQuadraticCoefficient * n * n;
    }

    /// <summary>
    /// The speedup <paramref name="threads"/> concurrent frequency points actually deliver — an Amdahl
    /// curve through the measured 4× at ten, not <paramref name="threads"/>. See
    /// <see cref="ParallelContentionFraction"/>.
    /// </summary>
    public static double ParallelSpeedup(int threads)
    {
        if (threads <= 1) return 1.0;
        return threads / (1.0 + ParallelContentionFraction * (threads - 1));
    }

    /// <summary>
    /// Predicted seconds for a whole sweep at the thread count <see cref="SolveThreadCount"/> will
    /// actually choose. <b>Not</b> at <see cref="Environment.ProcessorCount"/>: the thread count is set
    /// by the memory budget, and a prediction that assumed all the cores would be off by that factor
    /// exactly where it matters most — the large designs.
    /// </summary>
    public static double SweepSeconds(int segments, int terminals, int points, WireMomSettings? settings = null)
    {
        int threads = Math.Min(SolveThreadCount(segments, terminals, settings), Math.Max(1, points));
        return SetupSeconds(segments) + points * PerPointSeconds(segments) / ParallelSpeedup(threads);
    }

    /// <summary>
    /// What one frequency point's workspace costs: <c>M̃</c> (complex, N_s × N_s) plus the T-column
    /// right-hand-side block and the diagonal. <b>This is the term that sets the thread count.</b>
    /// </summary>
    public static long BytesPerSolveThread(int segments, int terminals) =>
        16L * segments * segments + 16L * segments * Math.Max(1, terminals) + 16L * segments;

    /// <summary>
    /// How many frequency points a sweep may solve at once: cores, capped by
    /// <see cref="WireMomSettings.MaxSolveThreads"/> and by how many per-thread <c>M̃</c> buffers fit in
    /// <see cref="WireMomSettings.SolveMemoryBudgetBytes"/>. Never below 1 — a single point must always
    /// be attemptable, and if its own workspace does not fit then the failure belongs to the allocator,
    /// where the message is about memory, rather than here, where it would be about threads.
    /// </summary>
    public static int SolveThreadCount(int segments, int terminals, WireMomSettings? settings = null)
    {
        settings ??= WireMomSettings.Default;

        int cores = Math.Max(1, settings.MaxSolveThreads ?? Environment.ProcessorCount);
        long budget = settings.SolveMemoryBudgetBytes ?? DefaultMemoryBudgetBytes();
        long perThread = BytesPerSolveThread(segments, terminals);

        long affordable = perThread <= 0 ? cores : budget / perThread;
        return (int)Math.Clamp(affordable, 1, cores);
    }

    /// <summary>
    /// A quarter of what the runtime says is available. See
    /// <see cref="WireMomSettings.SolveMemoryBudgetBytes"/> for why a quarter and not all of it.
    /// </summary>
    public static long DefaultMemoryBudgetBytes()
    {
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (available <= 0) available = 4L * 1024 * 1024 * 1024;
        return available / 4;
    }

    /// <summary>
    /// The largest <b>segments per wire</b> whose predicted sweep fits in <paramref name="seconds"/> —
    /// the number a refusal or a warning can name instead of naming a direction.
    ///
    /// <para>Returns 0 when even one segment per polyline vertex does not fit, which is a real outcome
    /// on a 600-wire design and must be reported as "this lever is exhausted" rather than as 1.</para>
    /// </summary>
    public static int SegmentsForBudget(WBondDesign design, int points, double seconds,
                                        WireMomSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        settings ??= WireMomSettings.Default;

        for (int target = MaxLadderRung; target >= 1; target--)
        {
            var probe = settings with { TargetSegmentsPerWire = target };
            var report = WireMomMesh.Predict(design, probe);
            if (SweepSeconds(report.Segments, report.Terminals, points, probe) <= seconds) return target;
        }

        return 0;
    }

    /// <summary>
    /// The coarsest rung <see cref="SegmentsForBudget"/> will search down from. Above the Accurate rung
    /// the answer is never the one a budget question wants.
    /// </summary>
    private const int MaxLadderRung = 48;

    /// <summary>
    /// The one-line human form of a prediction — what the mesh report and the slow-run warning both
    /// print, so the two can never disagree about the same run.
    /// </summary>
    public static string Describe(int segments, int terminals, int points, WireMomSettings? settings = null)
    {
        int threads = Math.Min(SolveThreadCount(segments, terminals, settings), Math.Max(1, points));
        double setup = SetupSeconds(segments);
        double perPoint = PerPointSeconds(segments);
        double total = setup + points * perPoint / ParallelSpeedup(threads);

        string thread = threads == 1 ? "1 thread" : $"{threads} threads";
        return $"~{Duration(total)} for {points} point(s) ({segments:N0} unknowns, {thread}): " +
               $"~{Duration(setup)} of setup plus ~{Duration(perPoint)} per point.";
    }

    /// <summary>Seconds as something a person reads: ms below a second, minutes above a minute.</summary>
    public static string Duration(double seconds) =>
        seconds < 1.0 ? $"{seconds * 1000.0:0} ms"
        : seconds < 60.0 ? $"{seconds:0.#} s"
        : $"{seconds / 60.0:0.#} min";
}
