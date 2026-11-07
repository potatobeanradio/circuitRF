using CircuitRF.Design.Em3d;
using CircuitRF.Design.Em3d.Install;
using CircuitRF.Engine;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf solver &lt;list|install|remove&gt;</c> — the install assistant's headless spelling
/// (brief-em3d-24 R-em3d24-7, em-3d.md §7.2: "a build machine installs the same way the GUI does, from the
/// same recipe and the same install record"). ONE verb with nouns, on <c>history</c>'s pattern (owner
/// decision D1); brief 25 added <c>remove</c>.
///
/// <para><b>This file holds no install or removal logic</b>: argument parsing, the consent refusal, progress on
/// stderr and reporting. <c>list</c> is <see cref="SolverStatus.Of"/> — what each Settings ▸ Solvers row
/// shows — and <c>install</c> is <see cref="SolverInstaller.Consent"/> then
/// <see cref="SolverInstaller.Install"/>, which the Settings row and a refusal's <i>Install …</i> action
/// call too; <c>remove</c> is <see cref="SolverUninstaller.PlanOne"/> or <see cref="SolverUninstaller.PlanAll"/>
/// then <see cref="SolverUninstaller.Remove"/>, which Settings and the Windows Apps-list uninstall call. A
/// comment-stripped source scan in <c>SolverInstallTests</c> holds that, on
/// <c>Authoring.cs</c>' terms.</para>
///
/// <para><b>Nothing is fetched without <c>--yes</c></b> (R-em3d24-2a). Without it the verb prints the
/// consent text — every upstream URL, where it goes, what it cost when measured — and exits 1, which is
/// a refusal and creates nothing. Exit codes: 0 installed (or already installed), 1 refused or failed with
/// the report on stderr, 130 cancelled.</para>
/// </summary>
internal static class Solver
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            JsonRun.Report(CliDiagnostics.SolverNounRequired());
            return Usage();
        }

        string noun = args[0].ToLowerInvariant();
        JsonRun.Verb = "solver " + noun;
        return noun switch
        {
            "list"    => List(args[1..]),
            "install" => Install(args[1..]),
            "remove"  => Remove(args[1..]),
            _         => UnknownNoun(noun),
        };
    }

    private static int UnknownNoun(string noun)
    {
        JsonRun.Report(CliDiagnostics.SolverUnknownNoun(noun));
        return Usage();
    }

    /// <summary>The ids <c>install</c> and <c>remove</c> take, from the registry: <c>palace</c>, <c>gmsh</c>, …</summary>
    internal static IEnumerable<string> ToolIds => Em3dSolvers.Tools.Select(SolverHomes.ToolId);

    /// <summary><see cref="ToolIds"/> as a sentence lists them: <c>palace, gmsh, openems or emerge</c>.</summary>
    internal static string ToolIdSentence => string.Join(", ", ToolIds.SkipLast(1)) + " or " + ToolIds.Last();

    private static int Usage()
    {
        string tools = string.Join("|", ToolIds);
        Console.Error.WriteLine("usage: circuitrf solver list [--capability gpu]");
        Console.Error.WriteLine($"       circuitrf solver install <{tools}> [--version <v>] [--gpu] [--yes]");
        Console.Error.WriteLine($"       circuitrf solver remove <{tools}> [--version <v>] [--yes]");
        Console.Error.WriteLine("       circuitrf solver remove --all [--yes]");
        return 1;
    }

    // ── solver list ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Each tool, as its Settings row shows it: found or not, where, by which route, the version,
    /// whether it is validated, what its build can do, and what would install it here. brief-em3d-136 R-em3d136-4c —
    /// <c>--capability gpu</c> also asks Palace whether it solves on a GPU, and how many it sees: a one-element solve,
    /// so only when asked, as the Settings row asks only for a user who chose GPU.</summary>
    private static int List(string[] args)
    {
        bool gpu = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--capability" && i + 1 < args.Length)
            {
                string asked = args[++i];
                if (!string.Equals(asked, "gpu", StringComparison.OrdinalIgnoreCase))
                    return JsonRun.Fail(CliDiagnostics.SolverUnknownCapability(asked));
                gpu = true;
            }
            else return JsonRun.Fail(CliDiagnostics.SolverUnknownOption("list", args[i]));
        }

        var report = new List<SolverJson>();
        foreach (var tool in Em3dSolvers.Tools)
        {
            var s = SolverStatus.Of(tool, gpu: gpu);
            string state = s.Found is null ? "not found" : s.InstalledByCircuitRf ? "installed by circuitRF" : "found";
            report.Add(new SolverJson(
                SolverHomes.ToolId(tool), s.Name, state, s.Summary,
                s.Found?.Where, s.Found?.Version, s.Found is { } f ? Route(f.HowFound) : null, s.Found?.Validated,
                [.. s.Capabilities.Select(c => new SolverCapabilityJson(CapabilityId(c.Capability), Describe(c.Capability),
                                                                        c.Available, c.Detail) { Devices = c.Devices })],
                s.OfferInstall ? $"circuitrf solver install {SolverHomes.ToolId(tool)}" : null)
                { Tier = s.Found?.Emerge?.TierText, GpuSolving = EmergeGpu(s.Found), Update = s.OfferUpdate ? s.Recipe?.Version : null });
            Console.WriteLine($"{s.Name}: {(s.Found is null ? "not found" : s.InstalledByCircuitRf ? "installed by circuitRF" : "found")}");
            Console.WriteLine($"  {s.Summary}");
            if (s.Found is { } found)
            {
                // brief-em3d-135 — Gmsh's list is of TESTED versions; the JSON key stays "validated" (a contract).
                Console.WriteLine($"  route: {Route(found.HowFound)}; {SolverDiscovery.For(tool).ListWord}: {(found.Validated ? "yes" : "no")}");
                JsonRun.AddOutput("solver", found.Path);
                // brief-emi-2 R-emi2-5 — a probed program's own line (EMerge's).
                if (found.Emerge is { } probe)
                    Console.WriteLine($"  probe: {probe.Banner}; {probe.Cores?.ToString() ?? "unknown"} logical cores; {probe.TierText}");
                // brief-emi-9 R-emi9-4 — GPU solving, and the command that enables it.
                if (EmergeGpu(found) is { } gpuSolving)
                    Console.WriteLine($"  GPU solving: {(gpuSolving.Enabled ? "enabled" : "not enabled")} — {gpuSolving.Detail}" +
                                      (gpuSolving.Enable is { } enable ? $"; enable: {enable}" : ""));
            }
            foreach (var c in s.Capabilities)
                Console.WriteLine($"  {Describe(c.Capability)}: {(c.Available ? "yes" : "no")} — {c.Detail}");
            if (s.GpuLine is { } gpuLine) Console.WriteLine($"  {gpuLine}");
            // brief-em3d-25 — only what circuitRF installed has a remove command; the rest is the user's.
            foreach (var home in new SolverUninstaller().Installed(tool))
                Console.WriteLine($"  installed by circuitRF: {home.Version} at {home.Home}" +
                                  (home.Distribution is { } d ? $" in the Linux subsystem distribution '{d}'" : "") + "; " +
                                  $"remove: circuitrf solver remove {SolverHomes.ToolId(tool)} --version {home.Version}");
            if (s.OfferUpdate)
                Console.WriteLine($"  update: circuitrf solver install {SolverHomes.ToolId(tool)}   ({s.Found?.Version} to {s.Recipe?.Version})");
            if (s.OfferInstall)
                Console.WriteLine($"  install: circuitrf solver install {SolverHomes.ToolId(tool)}" +
                                  (s.Recipe is { } r ? $"   (recipe {r.Id})" : "   (inside the Linux subsystem)"));
            else if (s.Recipe is null && !SolverInstallPlan.HasRoute(tool) && s.Found is not { Validated: true })
                Console.WriteLine($"  no install recipe for this machine; recipes exist for {SolverRecipes.PlatformsFor(tool)}");
        }
        JsonRun.Solvers = report;
        var cores = CircuitRF.Engine.Em3d.PhysicalCores.Current;
        double memoryGB = Math.Round(CircuitRF.Design.Em3d.MachineMemory.PhysicalBytes / 1e9, 1);
        const string coresNote = "A 3D run defaults to one Palace process per physical core; em --cores <n> sets fewer and refuses more. "
                               + "Each Palace process holds its own share of the mesh and its own solver data, so more processes take more "
                               + "memory and fewer take less, more slowly. A run past its memory is stopped.";
        JsonRun.Machine = new MachineJson(cores.Count, cores.Measured, cores.How, Environment.ProcessorCount, memoryGB, coresNote);
        Console.WriteLine($"This machine: {cores.Count} physical core{(cores.Count == 1 ? "" : "s")} ({cores.How}), " +
                          $"{Environment.ProcessorCount} logical, {memoryGB:0.#} GB of memory.");
        Console.WriteLine($"  {coresNote}");
        return 0;
    }

    /// <summary>brief-emi-9 — EMerge's GPU solving as <c>solver list</c> reports it; null for any other tool or nothing found.</summary>
    private static SolverGpuJson? EmergeGpu(SolverInstallation? found)
        => found?.Emerge is not { } probe ? null
         : probe.Gpu ? new SolverGpuJson(true, "EMerge's GPU sparse solver is installed; a run uses it as its device says (em --device).", null)
         : new SolverGpuJson(false, EmergeAccelerator.GpuSentence(found) ?? "",
                             EmergeAccelerator.GpuEnableFor(found) is not null ? "circuitrf solver install emerge --gpu --yes" : null);

    private static string Route(SolverHowFound how) => how switch
    {
        SolverHowFound.Settings         => "settings",
        SolverHowFound.Environment      => "environment",
        SolverHowFound.Installed        => "installed",
        SolverHowFound.Path             => "path",
        SolverHowFound.DefaultDirectory => "default-directory",
        SolverHowFound.Spack            => "spack",
        _                               => "conda",
    };

    /// <summary>The capability's id in the result document — stable, unlike the sentence beside it.</summary>
    private static string CapabilityId(SolverCapability c) => c switch
    {
        SolverCapability.DrivenLumpedPorts => "driven",
        SolverCapability.WavePorts         => "wave-ports",
        SolverCapability.Gpu               => "gpu",
        _                                  => "eigenmode",
    };

    private static string Describe(SolverCapability c) => c switch
    {
        SolverCapability.DrivenLumpedPorts => "driven solves with lumped ports",
        SolverCapability.WavePorts         => "wave ports",
        SolverCapability.Gpu               => "solves on a GPU",
        _                                  => "eigenmode solves",
    };

    // ── solver install ────────────────────────────────────────────────────────────────────────────

    private static int Install(string[] args)
    {
        string? name    = null;
        string? version = null;
        bool    yes     = false;
        bool    gpu     = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--yes" or "-y":                            yes = true; break;
                case "--gpu":                                    gpu = true; break;
                case "--version" when i + 1 < args.Length:       version = args[++i]; break;
                case var a when a.StartsWith('-'):               return JsonRun.Fail(CliDiagnostics.SolverUnknownOption("install", a));
                case var a when name is null:                    name = a; break;
                case var a:                                      return JsonRun.Fail(CliDiagnostics.SolverUnknownOption("install", a));
            }
        }
        if (name is null) return JsonRun.Fail(CliDiagnostics.SolverToolRequired());
        if (SolverHomes.ToolFromId(name) is not { } tool) return JsonRun.Fail(CliDiagnostics.SolverUnknownTool(name));
        // brief-emi-9 R-emi9-2b — --gpu is Settings' Enable GPU solving…, for a machine with no dialog: an option of a tool
        // whose recipes build a GPU sparse solver, asked of the recipes so no tool is named here.
        if (gpu && !EmergeAccelerator.HasGpuGroup(tool)) return JsonRun.Fail(CliDiagnostics.SolverUnknownOption($"install {name}", "--gpu"));

        string display = SolverDiscovery.For(tool).Name;
        // brief-em3d-26 — on Windows, Palace's plan is the Linux recipe inside a subsystem distribution, and a
        // precondition that stops it (no subsystem, no distribution, WSL 1, …) is refused with its one step.
        if (SolverInstallPlan.For(tool, version, out string? refusal) is not { } plan)
            return JsonRun.Fail(refusal is not null
                ? CliDiagnostics.SolverInstallRefused(refusal)
                : CliDiagnostics.SolverNoRecipe(display, version, Here(), SolverRecipes.PlatformsFor(tool), SolverRecipes.ByHand(tool)));
        var (recipe, installer) = (plan.Recipe, plan.Installer);
        Console.Error.WriteLine(installer.Consent(recipe));
        Console.Error.WriteLine();
        // What --gpu adds is agreed to by the same --yes, so it is printed before that is asked for.
        if (gpu && EmergeAccelerator.GpuGroupOf(recipe) is { When.Bootstrap: not null } group &&
            installer.BootstrapConsent(recipe, group.When, installer.HomeFor(recipe)) is { } gpuConsent)
        {
            Console.Error.WriteLine("With --gpu, when the GPU sparse solver cannot be built with a Rust toolchain already on this machine:");
            Console.Error.WriteLine(gpuConsent);
            Console.Error.WriteLine();
        }
        if (!yes) return JsonRun.Fail(CliDiagnostics.SolverConsentRequired(SolverHomes.ToolId(tool)));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(RunHost.Cancellation);
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            // The first Ctrl-C cancels cleanly (the running step's process tree is stopped and nothing is
            // published); a second one is left to end the process at once.
            if (cts.IsCancellationRequested) return;
            e.Cancel = true;
            Console.Error.WriteLine("[circuitRF] cancelling the install…");
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            var outcome = installer.Install(recipe, ProgressToStderr(cts.Token, SolverInstaller.WorkUnits(recipe)));
            if (outcome.LogPath is { } log) JsonRun.AddOutput("log", log);
            switch (outcome.Status)
            {
                case InstallStatus.Installed or InstallStatus.AlreadyInstalled:
                    Console.WriteLine(outcome.Report);
                    JsonRun.AddOutput("solver", outcome.Record!.Program);
                    return gpu ? EnableEmergeGpu(cts.Token) : 0;
                case InstallStatus.Cancelled:
                    JsonRun.Report(CliDiagnostics.SolverInstallCancelled(outcome.Report));
                    return 130;
                case InstallStatus.Refused:
                    return JsonRun.Fail(CliDiagnostics.SolverInstallRefused(outcome.Report));
                default:
                    return JsonRun.Fail(CliDiagnostics.SolverInstallFailed(outcome.Report, outcome.FailedStep ?? "", outcome.LogPath ?? ""));
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>
    /// brief-emi-9 R-emi9-2b — <c>solver install emerge --gpu</c> after the install: what is due is built (a Rust toolchain
    /// already here builds it), and otherwise the recipe's toolchain is fetched into the home for the build and deleted after,
    /// exactly as Settings' button does. 0 when the GPU sparse solver is there at the end; 1 when it could not be; 130 cancelled.
    /// </summary>
    private static int EnableEmergeGpu(CancellationToken ct)
    {
        var found = SolverDiscovery.Emerge.Find(out _);
        var recipe = EmergeAccelerator.RecordOf(found) is { } r ? SolverRecipes.All.FirstOrDefault(x => x.Id == r.Recipe) : null;
        foreach (var o in EmergeAccelerator.FinishIfDue(found))
            Console.WriteLine(EmergeAccelerator.Outcome(o, recipe));
        found = SolverDiscovery.Emerge.Find(out _);
        if (found?.Emerge is { Gpu: true })
        {
            Console.WriteLine("GPU solving is enabled: EMerge's GPU sparse solver is installed.");
            return 0;
        }
        if (EmergeAccelerator.GpuEnableFor(found) is not ({ } gpuRecipe, { } record, { } when))
            return JsonRun.Fail(CliDiagnostics.SolverInstallRefused(
                "GPU solving cannot be enabled here. " + (EmergeAccelerator.GpuSentence(found) ?? "")));

        var done = EmergeAccelerator.EnableGpu(gpuRecipe, record, when, ProgressToStderr(ct, 0));
        foreach (var o in done)
        {
            Console.WriteLine(EmergeAccelerator.Outcome(o, gpuRecipe));
            if (o.LogPath is { } log) JsonRun.AddOutput("log", log);
        }
        if (ct.IsCancellationRequested)
        {
            JsonRun.Report(CliDiagnostics.SolverInstallCancelled("Enabling GPU solving was cancelled; the toolchain circuitRF fetched was deleted."));
            return 130;
        }
        if (SolverDiscovery.Emerge.Find(out _)?.Emerge is { Gpu: true }) return 0;
        var failed = done.FirstOrDefault(o => o.Status != OptionalStepsStatus.Built);
        return JsonRun.Fail(CliDiagnostics.SolverInstallFailed(
            failed is null ? "The GPU sparse solver was built, but EMerge does not report it." : EmergeAccelerator.Outcome(failed, gpuRecipe),
            failed?.When ?? "", failed?.LogPath ?? ""));
    }

    // ── solver remove ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// brief-em3d-25 R-em3d25-1/2 — the confirmation on stderr, then nothing without <c>--yes</c> (exit 1,
    /// gate 8). Exit codes: 0 removed; 1 refused (not installed by circuitRF, several versions and none
    /// named, in use, an install running) or incomplete, with the files left listed by path.
    /// </summary>
    private static int Remove(string[] args)
    {
        string? name    = null;
        string? version = null;
        bool    all     = false;
        bool    yes     = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--yes" or "-y":                            yes = true; break;
                case "--all":                                    all = true; break;
                case "--version" when i + 1 < args.Length:       version = args[++i]; break;
                case var a when a.StartsWith('-'):               return JsonRun.Fail(CliDiagnostics.SolverUnknownOption("remove", a));
                case var a when name is null:                    name = a; break;
                case var a:                                      return JsonRun.Fail(CliDiagnostics.SolverUnknownOption("remove", a));
            }
        }
        if (all && name is not null) return JsonRun.Fail(CliDiagnostics.SolverRemoveAllWithTool(name));
        if (!all && name is null)   return JsonRun.Fail(CliDiagnostics.SolverRemoveTargetRequired());
        if (all && version is not null) return JsonRun.Fail(CliDiagnostics.SolverUnknownOption("remove --all", "--version"));

        var uninstaller = new SolverUninstaller();
        SolverRemovalPlan plan;
        if (all) plan = uninstaller.PlanAll();
        else if (SolverHomes.ToolFromId(name) is { } tool) plan = uninstaller.PlanOne(tool, version);
        else return JsonRun.Fail(CliDiagnostics.SolverUnknownTool(name!));

        if (plan.Refusal is { } refusal) return JsonRun.Fail(CliDiagnostics.SolverRemoveRefused(refusal));

        // The confirmation is the question; with --yes it has been answered, and the report names what went.
        if (!yes)
        {
            Console.Error.WriteLine(plan.Confirmation);
            Console.Error.WriteLine();
        }
        string command = all ? "circuitrf solver remove --all"
                       : plan.Homes.Count > 0 ? $"circuitrf solver remove {name!.ToLowerInvariant()} --version {plan.Homes[0].Record.Version}"
                       : $"circuitrf solver remove {name!.ToLowerInvariant()}";
        if (!yes) return JsonRun.Fail(CliDiagnostics.SolverRemoveConsentRequired(command));

        var outcome = uninstaller.Remove(plan);
        foreach (string home in outcome.Removed) JsonRun.AddOutput("removed", home);
        switch (outcome.Status)
        {
            case RemovalStatus.Removed:
                Console.WriteLine(outcome.Report);
                return 0;
            case RemovalStatus.Refused:
                return JsonRun.Fail(CliDiagnostics.SolverRemoveRefused(outcome.Report));
            default:
                return JsonRun.Fail(CliDiagnostics.SolverRemoveIncomplete(outcome.Report));
        }
    }

    private static string Here() => SolverRecipes.Current() is { } h
        ? $"{SolverRecipes.Describe(h.Platform)} {SolverRecipes.Describe(h.Architecture)}"
        : "this platform";

    /// <summary>
    /// One stderr row per stage change, and the stage's live figure (bytes downloaded, the package being
    /// built) no more than every two seconds — a Spack build is an hour, and a row per compiler line would
    /// bury the ones that say where it is. Delivered on the reporting thread so rows stay in order.
    /// </summary>
    private static RunControl ProgressToStderr(CancellationToken token, long total)
    {
        string lastStage = "";
        var sinceDetail = System.Diagnostics.Stopwatch.StartNew();
        var observer = RunHost.Observer;
        return new RunControl
        {
            Token    = token,
            Total    = total,
            Progress = new Inline(p =>
            {
                observer?.Invoke(p);
                // Keyed on the stage's NAME: a step finishing moves the counter without starting anything,
                // and a second row for the same stage would read as the step having run twice.
                if (p.Stage != lastStage)
                {
                    lastStage = p.Stage;
                    Console.Error.WriteLine($"[{Math.Min(p.Completed + 1, Math.Max(p.Total, 1))}/{p.Total}] {p.Stage}");
                    sinceDetail.Restart();
                    return;
                }
                if (p.StageDetail.Length == 0 || sinceDetail.Elapsed.TotalSeconds < 2) return;
                sinceDetail.Restart();
                Console.Error.WriteLine($"      {p.StageDetail}");
            }),
        };
    }

    private sealed class Inline(Action<RunProgress> report) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => report(value);
    }
}
