// brief-emi-5 R-emi5-3 — will an EMerge run fit? Answered from the mesh, the moment the driver has made it, through the run's
// MemoryGate exactly as Palace's after-meshing check is: a warning past 75 % of this machine, a confirmation past 150 %.
// brief-emi-12 — priced for the linear solver the run will use, each on its own line measured on EMerge 3.0.10, and offering
// EMerge's iterative solver (CORVID) as the remedy a run takes itself when the direct solve does not fit.

using System.Globalization;
using CircuitRF.Design.Layout.Em;

namespace CircuitRF.Design.Em3d;

/// <summary>brief-emi-12 R-emi12-1 — the linear solver an EMerge run factorises or iterates with, each with its own memory
/// line.</summary>
public enum EmergeLinearSolve
{
    /// <summary>SPARTA on the CPU: EMerge's own pick for a driven system wherever it is installed (every recipe since 3.0.10).
    /// Another fast direct solver an older or own install picks instead is priced on this line too: the macOS one measured
    /// below it on 3.0.10.</summary>
    Sparta,

    /// <summary>SPARTA on the GPU (Device GPU, a Mac's Metal GPU, whose memory is the machine's).</summary>
    SpartaGpu,

    /// <summary>CORVID, EMerge's iterative solver (EMerge.LinearSolver: Iterative, or the memory check's remedy).</summary>
    Corvid,

    /// <summary>SciPy's SuperLU on a driven run: an install with no faster direct solver.</summary>
    SuperLu,

    /// <summary>SciPy's SuperLU inside EMerge's eigensolver (shift-and-invert ARPACK): every eigenmode run, whatever else is
    /// installed. Its own line: its peak is not a driven SuperLU solve's.</summary>
    SuperLuEigenmode,
}

public static class EmergeMemory
{
    /// <summary>
    /// The unknowns EMerge solves for on a mesh: two per edge and two per triangle at order 2, one per edge at order 1 (its
    /// Nédélec bases). Measured to the unit on the Cavity at order 2: 21,817 edges and 32,966 triangles, 109,566 unknowns.
    /// </summary>
    public static long Unknowns(long edges, long triangles, int elementOrder)
        => elementOrder == 1 ? edges : 2 * (edges + triangles);

    /// <summary>
    /// brief-emi-12 R-emi12-1 — the solver a run of <paramref name="settings"/> will use, known before it solves: an eigenmode
    /// run's is SuperLU; a driven run's is CORVID when the setting (or the memory check's answer) says iterative and it is not a
    /// scattering run; SPARTA on the GPU when Device hands this many unknowns to it (<paramref name="gpuFromUnknowns"/>, as the
    /// driver decides); otherwise what the probe found EMerge's automatic routine picks for a large system
    /// (<paramref name="tier"/>: a fast solver is SPARTA's line, the baseline or an unreported one SuperLU's, which errs high).
    /// </summary>
    public static EmergeLinearSolve SolverFor(long unknowns, EmergeSpeedTier tier, bool eigenmode, bool iterative,
                                              long? gpuFromUnknowns = null, bool scattering = false)
    {
        if (eigenmode) return EmergeLinearSolve.SuperLuEigenmode;
        if (iterative && !scattering) return EmergeLinearSolve.Corvid;
        if (gpuFromUnknowns is { } from && unknowns >= from) return EmergeLinearSolve.SpartaGpu;
        return tier == EmergeSpeedTier.Fast ? EmergeLinearSolve.Sparta : EmergeLinearSolve.SuperLu;
    }

    /// <summary>
    /// The peak memory of an EMerge run solving <paramref name="unknowns"/> with <paramref name="solve"/>: a fixed part (Python,
    /// EMerge, the mesh) and a part per unknown.
    ///
    /// <para><b>Measured on EMerge 3.0.10</b> (src/Design/RESOLVED.md, "EMI-12"), an M4 with 16 GB: the FOOTPRINT of the
    /// driver's whole process tree (resident plus compressed and swapped pages, what the run's ceiling holds), every 100 ms, at
    /// order 2, each problem run at least twice, every core (CORVID's and SPARTA's threads) in use. The resident size is not the
    /// figure: on a machine under memory pressure the operating system compresses pages of a large factorisation, and one run
    /// read 2.4 GB resident and 4.0 GB footprint; two runs of one problem agree within 6 % in footprint on CORVID and 10 % on
    /// SPARTA (a parallel factorisation holds a different set of fronts at once from run to run), where they differed by up to
    /// 40 % resident. Each line lies at or above every measurement of its solver (<see cref="Measurements"/>) and as close
    /// to it as one fixed part and one part per unknown can be: <see cref="Ceiling"/> is the most it reads over the larger run
    /// of a problem, held by a test.</para>
    ///
    /// <para><b>The unknowns alone cannot hold a direct solver to 1.3 times.</b> The 3D Wave Ports Launch and a thin sheet line
    /// need 20 to 40 % more per unknown on SPARTA and SuperLU than the Cavity or the Coupled Microstrip, and nothing in the mesh's
    /// counts tells them apart: a line in the unknowns that holds them reads the Cavity at 54,000 unknowns 1.42 times high. So a
    /// SPARTA run is judged at SPARTA's own analysis of its first system instead (<see cref="PeakBytesFromAnalysis"/>), which does
    /// tell them apart, within 1.3; this line is what <c>explain</c> quotes before there is a mesh. CORVID factorises only the
    /// first-order part, where the difference is small. An adaptive sweep peaks up to 7 % above a point sweep on the same mesh, so
    /// both are among the measurements.</para>
    ///
    /// <para><b>SPARTA on the GPU</b> is measured the same way: on Apple silicon the GPU's memory is the machine's, and its
    /// buffers are charged to the footprint of the process that allocated them, so the figure is everything the run takes from
    /// the machine. It read within 10 % of SPARTA on the CPU at every size. <b>The eigenmode line</b> was measured on the Cavity
    /// alone, where 300 MB + 17.3 kB per unknown holds within 1.16 times; it is set at 19.6 kB, still within 1.28 times there,
    /// because no other geometry's eigenmode run was measured and the Launch's direct solves ran a quarter above the Cavity's.</para>
    /// </summary>
    public static long PeakBytes(long unknowns, EmergeLinearSolve solve)
    {
        var (fixedBytes, perUnknown) = Line(solve);
        return fixedBytes + perUnknown * unknowns;
    }

    /// <summary>The fixed part and the part per unknown of <paramref name="solve"/>'s line, bytes.</summary>
    public static (long Fixed, long PerUnknown) Line(EmergeLinearSolve solve) => solve switch
    {
        EmergeLinearSolve.Sparta    => (SpartaFixed, SpartaPerUnknown),
        EmergeLinearSolve.SpartaGpu => (GpuFixed, GpuPerUnknown),
        EmergeLinearSolve.Corvid    => (CorvidFixed, CorvidPerUnknown),
        EmergeLinearSolve.SuperLu   => (SuperLuFixed, SuperLuPerUnknown),
        _                           => (EigenFixed, EigenPerUnknown),
    };

    /// <summary>The most each unknowns-only line reads over the larger measured run of a problem (<see cref="Measurements"/>): 1.3
    /// for CORVID and the eigensolver. SPARTA's and the GPU's are what one line in the unknowns alone can do, and are not what a
    /// run is judged on: a SPARTA run is priced from its analysis (<see cref="PeakBytesFromAnalysis"/>, within
    /// <see cref="AnalysisCeiling"/>), and its unknowns line is the figure <c>explain</c> quotes before there is a mesh. Driven
    /// SuperLU's 1.48 stands: SciPy's factorisation makes no analysis a run could read first, and a driven run reaches it only on an
    /// install with no faster solver.</summary>
    public static double Ceiling(EmergeLinearSolve solve) => solve switch
    {
        EmergeLinearSolve.Sparta    => 1.42,
        EmergeLinearSolve.SpartaGpu => 1.37,
        EmergeLinearSolve.SuperLu   => 1.48,
        _                           => 1.30,
    };

    private const long SpartaFixed = 430_000_000, SpartaPerUnknown = 18_200;
    private const long GpuFixed    = 330_000_000, GpuPerUnknown    = 17_700;
    private const long CorvidFixed = 300_000_000, CorvidPerUnknown = 12_000;
    private const long SuperLuFixed = 0,          SuperLuPerUnknown = 24_400;
    private const long EigenFixed  = 300_000_000, EigenPerUnknown  = 19_600;

    /// <summary>One measured problem: its unknowns at order 2, its solver and each run's peak footprint, MB; for a SPARTA run, the
    /// factor and fronts SPARTA's analysis predicted for its first system, MB.</summary>
    public sealed record Measurement(string Problem, long Unknowns, EmergeLinearSolve Solve, IReadOnlyList<int> PeakMegabytes,
                                     int? AnalysisMegabytes = null)
    {
        /// <summary>The larger run's peak, bytes.</summary>
        public long LargestBytes => PeakMegabytes.Max() * 1_000_000L;
    }

    private static Measurement M(string problem, long unknowns, EmergeLinearSolve solve, int[] mb, int? analysisMb = null)
        => new(problem, unknowns, solve, mb, analysisMb);

    /// <summary>
    /// brief-emi-12 R-emi12-1 — every measurement the lines are fitted to: EMerge 3.0.10 on an Apple M4 (10 cores, 16 GB), the
    /// examples' 3D Eigenmode Cavity and 3D Wave Ports Launch and Coupled Microstrip, each meshed at several sizes (a preset, or
    /// the largest element in wavelengths), solved at three frequencies with no fields saved (an eigenmode run: Count 3, at
    /// 10 GHz), a point sweep (and, where a problem has four runs, its 5-point and adaptive sweeps from R-emi12-4 as the last
    /// two), with one frequency's matrices assembled at a time as the driver does since EMI-12. The driven SuperLU runs are one
    /// frequency each. The sheet line is EM3D-139's 5 µm line drawn as a 1 µm sheet. src/Design/RESOLVED.md, "EMI-12", has the
    /// table.
    /// </summary>
    public static IReadOnlyList<Measurement> Measurements { get; } =
    [
        M("Cavity, driven, Draft", 54090, EmergeLinearSolve.Sparta, [1000, 998], 292),
        M("5 µm line as a 1 µm sheet", 75602, EmergeLinearSolve.Sparta, [1745, 1754, 1804], 729),
        M("Launch, Draft", 92108, EmergeLinearSolve.Sparta, [1753, 1816], 700),
        M("Cavity, driven, Standard", 108668, EmergeLinearSolve.Sparta, [1674, 1698], 606),
        M("Launch, Standard", 151670, EmergeLinearSolve.Sparta, [3068, 3076, 3132], 1358),
        M("Cavity, driven, 0.1 λ", 255076, EmergeLinearSolve.Sparta, [3726, 3491], 1587),
        M("Coupled Microstrip, Standard", 337980, EmergeLinearSolve.Sparta, [4862, 4746, 4970], 1930),
        M("Cavity, driven, 0.085 λ", 361012, EmergeLinearSolve.Sparta, [5408, 5326], 2328),
        M("Coupled Microstrip, 0.12 λ", 363540, EmergeLinearSolve.Sparta, [5430, 5450], 2227),
        M("Cavity, driven, 0.075 λ", 493846, EmergeLinearSolve.Sparta, [7125, 6690, 6945], 3043),
        M("Cavity, driven, Draft", 54090, EmergeLinearSolve.SpartaGpu, [943, 942], 146),
        M("Launch, Draft", 92108, EmergeLinearSolve.SpartaGpu, [1819, 1822], 350),
        M("Launch, Standard", 151670, EmergeLinearSolve.SpartaGpu, [2515, 3013], 679),
        M("Cavity, driven, 0.1 λ", 255076, EmergeLinearSolve.SpartaGpu, [3629, 3628], 793),
        M("Coupled Microstrip, Standard", 337980, EmergeLinearSolve.SpartaGpu, [5080, 5007], 965),
        M("Cavity, driven, 0.075 λ", 493846, EmergeLinearSolve.SpartaGpu, [6634, 6588], 1522),
        M("Cavity, driven, Draft", 54090, EmergeLinearSolve.Corvid, [733, 737]),
        M("5 µm line as a 1 µm sheet", 75602, EmergeLinearSolve.Corvid, [1082, 1072]),
        M("Launch, Draft", 92108, EmergeLinearSolve.Corvid, [1301, 1298]),
        M("Cavity, driven, Standard", 108668, EmergeLinearSolve.Corvid, [1282, 1289]),
        M("Launch, Standard", 151670, EmergeLinearSolve.Corvid, [1908, 1997, 2112, 2112]),
        M("Cavity, driven, 0.1 λ", 255076, EmergeLinearSolve.Corvid, [2668, 2512]),
        M("Coupled Microstrip, Standard", 337980, EmergeLinearSolve.Corvid, [3664, 3559, 3936, 4092]),
        M("Cavity, driven, 0.085 λ", 361012, EmergeLinearSolve.Corvid, [3658, 3652]),
        M("Coupled Microstrip, 0.12 λ", 363540, EmergeLinearSolve.Corvid, [4021, 3962]),
        M("Cavity, driven, 0.075 λ", 493846, EmergeLinearSolve.Corvid, [4714, 4735, 4733, 5048]),
        M("Cavity, driven, Draft", 54090, EmergeLinearSolve.SuperLu, [983, 986]),
        M("Launch, Draft", 92000, EmergeLinearSolve.SuperLu, [1964, 1809, 2036, 2042, 1812]),
        M("Cavity, driven, Standard", 107778, EmergeLinearSolve.SuperLu, [1778, 1671]),
        M("Launch, Standard", 150966, EmergeLinearSolve.SuperLu, [3492, 3493, 3452, 3683, 3282]),
        M("Cavity, eigenmode, 0.27 λ", 55324, EmergeLinearSolve.SuperLuEigenmode, [1084, 1085]),
        M("Cavity, eigenmode, Draft", 109924, EmergeLinearSolve.SuperLuEigenmode, [2106, 2105]),
        M("Cavity, eigenmode, Standard", 205224, EmergeLinearSolve.SuperLuEigenmode, [3465, 3838]),
    ];

    /// <summary>
    /// brief-emi-12 — a SPARTA run's peak from SPARTA's own symbolic analysis of its first system (the driver's "analyzed" line, made
    /// before anything is factorised): a fixed part, a part per unknown and a share of the factor and fronts SPARTA predicts. This is
    /// what the check prices a direct SPARTA solve from, because the unknowns alone cannot tell two meshes of one size apart: the
    /// Launch and a thin sheet line need 20 to 40 % more per unknown than the Cavity, and SPARTA's analysis says so (its factor
    /// reads 4.1-4.4 kB per unknown on them against 2.7-3.2 kB). Measured within <see cref="AnalysisCeiling"/> of every run
    /// (<see cref="Measurements"/>, held by a test).
    /// </summary>
    public static long PeakBytesFromAnalysis(long unknowns, long factorAndFrontBytes, EmergeLinearSolve solve)
    {
        var (fixedBytes, perUnknown, share) = solve == EmergeLinearSolve.SpartaGpu
            ? (GpuAnalysisFixed, GpuAnalysisPerUnknown, GpuAnalysisShare)
            : (SpartaAnalysisFixed, SpartaAnalysisPerUnknown, SpartaAnalysisShare);
        return fixedBytes + perUnknown * unknowns + (long)Math.Round(share * factorAndFrontBytes);
    }

    /// <summary>Whether <paramref name="solve"/> is priced from SPARTA's analysis when one is reported.</summary>
    public static bool PricedFromAnalysis(EmergeLinearSolve solve) => solve is EmergeLinearSolve.Sparta or EmergeLinearSolve.SpartaGpu;

    /// <summary>The most the analysis-based estimate reads over the larger measured run of a problem: the brief's 1.3.</summary>
    public const double AnalysisCeiling = 1.30;

    // Fitted to the measurements (minimax over each problem's larger run: SPARTA 320 MB + 5.2 kB per unknown + 1.50 × factor and
    // fronts within 1.08×, the GPU 330 MB + 7.4 kB + 2.34× within 1.14×), then set 10 % above the fit, the run-to-run spread SPARTA's
    // parallel factorisation shows: every measurement then reads 1.10-1.19× (the GPU's 1.10-1.25×).
    private const long SpartaAnalysisFixed = 352_000_000, SpartaAnalysisPerUnknown = 5_720;
    private const double SpartaAnalysisShare = 1.65;
    private const long GpuAnalysisFixed = 363_000_000, GpuAnalysisPerUnknown = 8_140;
    private const double GpuAnalysisShare = 2.574;

    /// <summary>The solver as a sentence names it.</summary>
    public static string Describe(EmergeLinearSolve solve) => solve switch
    {
        EmergeLinearSolve.Sparta    => "its direct solver (SPARTA)",
        EmergeLinearSolve.SpartaGpu => "its direct solver on the GPU (SPARTA)",
        EmergeLinearSolve.Corvid    => "its iterative solver (CORVID)",
        EmergeLinearSolve.SuperLu   => "its single-threaded direct solver (SuperLU)",
        _                           => "its eigensolver, which factorises with SuperLU on one core",
    };

    /// <summary>The remedy that solves with CORVID: the run takes it itself (<see cref="Em3dMemoryRemedy.Iterative"/>), so the
    /// question offers it as a button.</summary>
    public const string IterativeRemedyText =
        "EMerge's iterative solver, CORVID (EMerge.LinearSolver: Iterative; S within about 1e-7 of the direct solve, usually " +
        "a little slower where the direct one fits)";

    /// <summary>
    /// The verdict for a mesh's counts, against <paramref name="physicalBytes"/>, for the solver <paramref name="solve"/> the
    /// run will use, with the remedies a mesh this size can be priced for without meshing again: EMerge's iterative solver when
    /// the run would solve a driven problem directly and CORVID is installed (<paramref name="iterativeAvailable"/>), and
    /// first-order elements. The iterative remedy is listed by its estimate like the others, and offered first as the
    /// question's button (<see cref="Em3dMemoryVerdict.Applicable"/>), the way Palace's iterative solver is.
    /// </summary>
    public static Em3dMemoryVerdict Verdict(EmergeMeshCounts mesh, EmergeSettings settings, EmergeLinearSolve solve,
                                            long physicalBytes, bool iterativeAvailable = false, bool eigenmode = false,
                                            bool scattering = false, EmergeAnalysis? analysis = null)
    {
        long unknowns = Unknowns(mesh.Edges, mesh.Triangles, settings.ElementOrder);
        // brief-emi-12 — a SPARTA run with its analysis in hand is priced from it; otherwise, from the unknowns
        bool fromAnalysis = PricedFromAnalysis(solve) && analysis?.FactorAndFrontBytes is not null;
        long estimate = fromAnalysis ? PeakBytesFromAnalysis(unknowns, analysis!.FactorAndFrontBytes!.Value, solve) : PeakBytes(unknowns, solve);
        var remedies = new List<Em3dMemoryRemedy>();
        // EMerge's eigensolver does not use CORVID, and a scattering run is solved directly: neither is offered it.
        if (solve != EmergeLinearSolve.Corvid && iterativeAvailable && !eigenmode && !scattering)
            remedies.Add(new(IterativeRemedyText, PeakBytes(unknowns, EmergeLinearSolve.Corvid)) { Iterative = true });
        if (settings.ElementOrder != 1)
            remedies.Add(new("first-order elements (EMerge.ElementOrder: 1), which move a resonance by a few steps of a fine sweep",
                             PeakBytes(Unknowns(mesh.Edges, mesh.Triangles, 1), solve)));
        return Em3dMemoryVerdict.Evaluate(estimate, physicalBytes, [.. remedies.OrderBy(r => r.EstimateBytes ?? long.MaxValue)],
            basis: $"EMerge made {mesh.Tetrahedra.ToString("N0", CultureInfo.InvariantCulture)} tetrahedra, " +
                   $"{unknowns.ToString("N0", CultureInfo.InvariantCulture)} unknowns, for {Describe(solve)}" +
                   (fromAnalysis ? $", whose analysis predicts a {MachineMemory.Format(analysis!.FactorBytes!.Value)} factor and " +
                                   $"{MachineMemory.Format(analysis.FrontBytes!.Value)} of fronts." : "."),
            solver: "EMerge");
    }
}
