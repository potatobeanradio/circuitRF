// brief-emi-2 — EMerge: a finite-element (FEM) solver that runs as a Python program. This folder is everything circuitRF
// knows about EMerge: the program it finds and installs and the probe it asks (EMI-2/EMI-3), its descriptor, its settings
// and its lowering (brief-emi-4), and its run and the results it reads back (brief-emi-5).

using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

public static class EmergeSolver
{
    public const Em3dSolverId Id = Em3dSolverId.Emerge;

    /// <summary>
    /// The oldest EMerge circuitRF's driver runs on. 3.0.8 put every port's modes through one procedure
    /// (<c>Microwave3D._port_modes</c>), which the driver calls for every wave port and which no earlier EMerge has, and dropped
    /// what the driver used to repair for 3.0.0 itself; 3.0.2 had already fixed thin conductors, which 3.0.0 reads twice as
    /// conductive (src/Design/RESOLVED.md, "EMerge 3.0.10").
    /// </summary>
    public const string MinimumVersion = "3.0.8";

    /// <summary>
    /// The refusal of an EMerge older than <see cref="MinimumVersion"/>, with how to upgrade it: circuitRF's own install is
    /// updated from Settings (or <c>circuitrf solver install emerge</c>), a Python of the user's own is upgraded with pip. Null
    /// for an EMerge that is new enough, or whose version is unknown (the untested warning speaks for that).
    /// </summary>
    public static string? TooOldRefusal(SolverInstallation found)
    {
        string? version = found.Emerge?.Emerge ?? found.Version;
        if (version is null || !IsBefore(version, MinimumVersion)) return null;
        string newest = SolverDiscovery.Emerge.ValidatedVersions.Select(v => v.Release).Where(r => !IsBefore(r, MinimumVersion))
                                       .DefaultIfEmpty(MinimumVersion).MaxBy(r => r, Comparer<string>.Create(Compare))!;
        string how = found.HowFound == SolverHowFound.Installed
            ? $"Update it in Settings ▸ Solvers, where the EMerge row offers Update to EMerge {newest}…, or from a terminal with " +
              "`circuitrf solver install emerge --yes`. The new version installs beside this one, and circuitRF uses it as soon as it is in."
            : $"This EMerge is a Python environment of your own, at {found.Path}. Upgrade it there with " +
              $"`\"{found.Path}\" -m pip install --upgrade \"emerge[corvid]=={newest}\"`, or let circuitRF install its own: clear the " +
              $"EMerge path in Settings ▸ Solvers (and unset {SolverDiscovery.Emerge.EnvironmentVariable} if it is set), then choose " +
              "Install EMerge….";
        return $"EMerge {version} is older than {MinimumVersion}, the first EMerge circuitRF's driver is written for: its wave ports " +
               "and port modes changed in 3.0.8, and thin conductors read twice as conductive before 3.0.2, so this setup does not run " +
               $"on it. {how}";
    }

    /// <summary>Whether EMerge version <paramref name="a"/> comes before <paramref name="b"/>: numeric parts in turn, and a
    /// pre-release (3.0.0b2) before its release.</summary>
    public static bool IsBefore(string a, string b) => Compare(a, b) < 0;

    private static int Compare(string a, string b)
    {
        static (int[] Parts, bool Pre) Parse(string v)
        {
            var m = System.Text.RegularExpressions.Regex.Match(v.Trim(), @"^(\d+(?:\.\d+)*)(.*)$");
            if (!m.Success) return ([], true);
            return ([.. m.Groups[1].Value.Split('.').Select(x => int.Parse(x, CultureInfo.InvariantCulture))], m.Groups[2].Value.Length > 0);
        }
        var (pa, preA) = Parse(a);
        var (pb, preB) = Parse(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int x = i < pa.Length ? pa[i] : 0, y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return preA == preB ? 0 : preA ? -1 : 1;
    }

    public static Em3dSolverDescriptor Descriptor { get; } = new()
    {
        Id = Id,
        DisplayName = "EMerge",
        Spelling = "Emerge",
        ResultToken = "emerge",
        Method = Em3dSolverFit.Fem,
        Quality = s => s.Emerge?.Quality ?? PalaceQuality.Standard,
        Capabilities = new()
        {
            Problems = new HashSet<Em3dProblemType> { Em3dProblemType.Driven, Em3dProblemType.Eigenmode },
            MeshRegions = true,
            // brief-emi-7 — in the .vtu layout Palace's fields are read in, and a far field on an absorbing box
            Fields = true,
            RadiationPattern = true,
            // brief-hzn-12 — a scattering run: EMerge's scattered-field boundary on the absorbing faces
            PlaneWave = true,
        },
        Tools = [SolverTool.Emerge],
        SetupDescription =
            "Generates a 3D model from the layout, its technology and any bond wires and solves it with EMerge, which meshes " +
            "it with its own Gmsh; installed separately (Settings ▸ Solvers).",
        SweepText = SweepText,
        SavedFieldsGHz = setup => setup.Emerge?.SaveFieldsGHz,
        SetSavedFieldsGHz = (setup, ghz) =>
        {
            var section = setup.Emerge?.Clone() ?? new CemEmerge();
            section.SaveFieldsGHz = ghz;
            setup.Emerge = section;
        },
        Refusal = Refusal,
        // brief-emw-3 R-emw3-6 — what it refuses of a terminal group, so a comparison goes on without it and says so
        PortRefusal = EmergeLoweringRules.TerminalRefusal,
        Check = Check,
        NewLeg = () => new Em3dRunService.EmergeLeg(),
    };

    /// <summary>R-emi4-4 — what EMerge's run in circuitRF does not solve, refused before anything is looked for, naming the
    /// solver that does.</summary>
    private static string? Refusal(EmSetup setup)
    {
        if (!setup.IsStatic3D) return null;
        var able = Em3dSolvers.With(c => c.Problems.Contains(setup.Problem3D));
        string kind = setup.Problem3D == Em3dProblemType.Electrostatic ? "an electrostatic" : "a magnetostatic";
        return $"This setup asks for {kind} solve on EMerge, and only {Em3dSolvers.Names(able)} solves the static problems: " +
               "circuitRF runs EMerge for driven and eigenmode problems. " +
               $"Set the setup's Solver3D to {able[0].Spelling}, or run it with `circuitrf em --solver {able[0].ResultToken}`.";
    }

    /// <summary>R-emi4-4 — what EMerge's lowering would refuse of a built problem (each sentence the run's own), and what
    /// it goes on without.</summary>
    private static Em3dSolverCheck Check(Em3dProblem problem, EmSetup setup, string? settingsPath)
    {
        var settings = EmergeSettings.Resolve(setup.Emerge);
        if (settings.Problems().Concat(EmergeFields.Problems(setup.Emerge)).ToList() is { Count: > 0 } bad) return new(bad, []);
        var notes = new List<string>();
        if (EmergeScriptWriter.RadiationPatternNote(setup, problem) is { } noPattern) notes.Add(noPattern);
        if (EmergeScriptWriter.Write(problem, settings).Refusal is { } refused) return new([refused], notes);
        return new([], notes);
    }

    /// <summary>What EMerge does with a frequency sweep, as the Messages panel's sweep row says it.</summary>
    private static string SweepText(EmSetup setup)
    {
        var settings = EmergeSettings.Resolve(setup.Emerge);
        return settings.AdaptiveSweep
            ? $"EMerge builds the sweep adaptively (tolerance {settings.Tolerance.ToString("G3", CultureInfo.InvariantCulture)} on |S|): " +
              "it solves the full problem at as few frequencies as reach that tolerance and fits every requested point from them"
            : "EMerge solves the full problem at every point";
    }

    /// <summary>The program it needs: a Python interpreter with EMerge importable (R-emi2-3a).</summary>
    public static IReadOnlyList<SolverTool> Tools { get; } = [SolverTool.Emerge];

    /// <summary>The program names discovery looks for: a Python interpreter. Here rather than in SolverDiscovery's table, so
    /// the one file that names openEMS starts no interpreter (tests/Firewall.Tests' GPL-boundary scan).</summary>
    public static string[] ProgramNames => ["python"];

    /// <summary>
    /// R-emi2-3b — the probe, run as <c>&lt;python&gt; -I -c &lt;this&gt;</c>. A constant: no user string is ever
    /// spliced into it. It prints ONE JSON line: EMerge's, Python's and Gmsh's versions, the platform, the logical
    /// core count, the optional sparse solvers EMerge reports available, and the direct solver EMerge itself picks
    /// for a large driven system.
    ///
    /// <para><b>EMerge's internals are asked in two layouts.</b> From 3.0 a solver is a class in
    /// <c>emerge/_emerge/solvers</c> with an <c>available</c> flag, listed in <c>PUBLIC_SOLVERS</c>; before it the flags were
    /// module-level <c>_&lt;NAME&gt;_AVAILABLE</c> booleans in <c>emerge/_emerge/solver.py</c>. An EMerge a user installed
    /// themselves may be either, so both are read, and the optional-solver list keeps the older spelling: lower case, without
    /// the solvers every EMerge has. Its own choice is <c>AutomaticRoutine().pick_solver</c>, which reads only the matrix's
    /// size and kind (here 20,000 unknowns, complex symmetric: what a driven FEM system is), with the GPU solver out of it as
    /// the driver has it on a run on the CPU, and the x64 library's solver out of it on arm64, where EMerge wrongly reports it on
    /// Linux, so the answer is what such a run uses. Each is asked inside its own
    /// <c>try</c>, and a missing one reads as "not reported" — never as a failed probe. Only <c>import emerge</c> failing
    /// makes the answer a refusal.</para>
    /// </summary>
    public const string ProbeSource = """
        import json, os, platform, sys
        out = {"probe": 1, "python": platform.python_version(), "platform": sys.platform, "machine": platform.machine(), "cores": os.cpu_count()}
        try:
            import emerge
            out["emerge"] = getattr(emerge, "__version__", None)
        except Exception as e:
            out["error"] = "import emerge failed: %s: %s" % (type(e).__name__, e)
            print(json.dumps(out)); sys.exit(0)
        try:
            import gmsh
            out["gmsh"] = getattr(gmsh, "__version__", None)
        except Exception:
            out["gmsh"] = None
        try:
            from emerge._emerge import solvers as s
            from emerge._emerge.solveroutine import AutomaticRoutine
            if platform.machine().lower() in ("aarch64", "arm64"):
                for n, c in s.PUBLIC_SOLVERS.items():
                    if n not in ("SuperLU", "UMFPACK", "MUMPS", "AASDS", "CHOLMOD", "RSLab", "Sparta", "BiCGSTAB", "CG", "Corvid", "LAPACK", "ARPACK"):
                        c.available = False
            standard = {"SuperLU", "BiCGSTAB", "CG", "Corvid", "LAPACK", "ARPACK"}
            # SPARTA is listed as the GPU sparse solver only on a Mac with a Metal GPU, the one GPU circuitRF has tested it on: elsewhere
            # its wheel finds Vulkan, and Mesa's llvmpipe, a CPU rasteriser, answers as a GPU on any Linux machine with Mesa
            out["sparseSolvers"] = sorted(n.lower() for n, c in s.PUBLIC_SOLVERS.items()
                                          if c.available and n not in standard and (n != "Sparta" or (c.gpu and sys.platform == "darwin")))
        except Exception:
            out["sparseSolvers"] = []
        try:
            out["iterativeSolver"] = bool(s.SolverCorvid.available)
        except Exception:
            out["iterativeSolver"] = False
        try:
            import scipy.sparse as sp
            out["directSolver"] = type(AutomaticRoutine().pick_solver(sp.csc_matrix((20000, 20000), dtype=complex), None, s.MatrixType.COMPLEX_SYMMETRIC)).__name__
        except Exception:
            out["directSolver"] = None
        print(json.dumps(out))
        """;

    /// <summary>How long the probe may take. A first <c>import emerge</c> in an environment without compiled bytecode
    /// took 17 s (macOS arm64, measured); the installer compiles it, so a probe of circuitRF's own install takes ~0.6 s.</summary>
    public static TimeSpan ProbeTimeout { get; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// The interpreter a NAMED folder stands for (R-emi2-3a: Settings may name a Python, or a folder holding a venv):
    /// the folder of a virtual environment (<c>bin/python</c>, <c>Scripts\python.exe</c>) or of a circuitRF EMerge home
    /// (its <c>env/</c>). Null when it holds none.
    /// </summary>
    public static string? InterpreterIn(string folder)
    {
        string[] candidates =
        [
            Path.Combine(folder, "bin", "python"), Path.Combine(folder, "Scripts", "python.exe"),
            Path.Combine(folder, "env", "bin", "python"), Path.Combine(folder, "env", "Scripts", "python.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The folders whose change means a different EMerge behind the same interpreter: the environment's
    /// <c>site-packages</c>. A <c>pip install</c> of another version renames a <c>.dist-info</c> folder in it, which
    /// moves its timestamp, while the interpreter itself — a link into a Python installation — stays as it was. Part
    /// of the probe cache's key (R-emi2-3c).
    /// </summary>
    internal static IEnumerable<string> SitePackages(string interpreter)
    {
        string? root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(interpreter)));
        if (root is null) yield break;
        string windows = Path.Combine(root, "Lib", "site-packages");
        if (Directory.Exists(windows)) yield return windows;
        string lib = Path.Combine(root, "lib");
        if (!Directory.Exists(lib)) yield break;
        foreach (string py in Directory.EnumerateDirectories(lib, "python*").Order(StringComparer.Ordinal))
        {
            string site = Path.Combine(py, "site-packages");
            if (Directory.Exists(site)) yield return site;
        }
    }
}
