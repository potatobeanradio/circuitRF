using CircuitRF.Design.Em3d;
using CircuitRF.Design.Em3d.Install;
using CircuitRF.Engine;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf solver &lt;list|install|remove&gt;</c> — the install assistant's headless spelling
/// (brief-em3d-24 R-em3d24-7, em-3d.md §7.2: "a build machine installs the same way the GUI does, from the
/// same recipe and the same install record"). ONE verb with nouns, on <c>history</c>'s pattern (owner
/// decision D1); brief 25 added <c>remove</c>.
///
/// <para><b>This file holds no install or removal logic</b>: argument parsing, the consent refusal, progress on
/// stderr and reporting. <c>list</c> is <see cref="SolverStatus.Of"/> — what each Settings ▸ 3D EM row
/// shows — and <c>install</c> is <see cref="SolverInstaller.Consent"/> then
/// <see cref="SolverInstaller.Install"/>, which the Settings row and a refusal's <i>Install …</i> action
/// call too; <c>remove</c> is <see cref="SolverUninstaller.PlanOne"/> or <see cref="SolverUninstaller.PlanAll"/>
/// then <see cref="SolverUninstaller.Remove"/>, which Settings and <i>Uninstall circuitRF…</i> call. A
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

    private static int Usage()
    {
        Console.Error.WriteLine("usage: circuitrf solver list");
        Console.Error.WriteLine("       circuitrf solver install <palace|gmsh|openems> [--version <v>] [--yes]");
        Console.Error.WriteLine("       circuitrf solver remove <palace|gmsh|openems> [--version <v>] [--yes]");
        Console.Error.WriteLine("       circuitrf solver remove --all [--yes]");
        return 1;
    }

    // ── solver list ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Each tool, as its Settings row shows it: found or not, where, by which route, the version,
    /// whether it is validated, what its build can do, and what would install it here.</summary>
    private static int List(string[] args)
    {
        if (args.Length > 0) return JsonRun.Fail(CliDiagnostics.SolverUnknownOption("list", args[0]));

        foreach (var tool in Enum.GetValues<SolverTool>())
        {
            var s = SolverStatus.Of(tool);
            Console.WriteLine($"{s.Name}: {(s.Found is null ? "not found" : s.InstalledByCircuitRf ? "installed by circuitRF" : "found")}");
            Console.WriteLine($"  {s.Summary}");
            if (s.Found is { } found)
            {
                Console.WriteLine($"  route: {Route(found.HowFound)}; validated: {(found.Validated ? "yes" : "no")}");
                JsonRun.AddOutput("solver", found.Path);
            }
            foreach (var c in s.Capabilities)
                Console.WriteLine($"  {Describe(c.Capability)}: {(c.Available ? "yes" : "no")} — {c.Detail}");
            // brief-em3d-25 — only what circuitRF installed has a remove command; the rest is the user's.
            foreach (var home in new SolverUninstaller().Installed(tool))
                Console.WriteLine($"  installed by circuitRF: {home.Version} at {home.Home}; " +
                                  $"remove: circuitrf solver remove {SolverHomes.ToolId(tool)} --version {home.Version}");
            if (s.OfferInstall)
                Console.WriteLine($"  install: circuitrf solver install {SolverHomes.ToolId(tool)}   (recipe {s.Recipe!.Id})");
            else if (s.Recipe is null && s.Found is not { Validated: true })
                Console.WriteLine($"  no install recipe for this machine; recipes exist for {SolverRecipes.PlatformsFor(tool)}");
        }
        return 0;
    }

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

    private static string Describe(SolverCapability c) => c switch
    {
        SolverCapability.DrivenLumpedPorts => "driven solves with lumped ports",
        SolverCapability.WavePorts         => "wave ports",
        _                                  => "eigenmode solves",
    };

    // ── solver install ────────────────────────────────────────────────────────────────────────────

    private static int Install(string[] args)
    {
        string? name    = null;
        string? version = null;
        bool    yes     = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--yes" or "-y":                            yes = true; break;
                case "--version" when i + 1 < args.Length:       version = args[++i]; break;
                case var a when a.StartsWith('-'):               return JsonRun.Fail(CliDiagnostics.SolverUnknownOption("install", a));
                case var a when name is null:                    name = a; break;
                case var a:                                      return JsonRun.Fail(CliDiagnostics.SolverUnknownOption("install", a));
            }
        }
        if (name is null) return JsonRun.Fail(CliDiagnostics.SolverToolRequired());
        if (SolverHomes.ToolFromId(name) is not { } tool) return JsonRun.Fail(CliDiagnostics.SolverUnknownTool(name));

        string display = SolverDiscovery.For(tool).Name;
        if (SolverRecipes.For(tool, version) is not { } recipe)
            return JsonRun.Fail(CliDiagnostics.SolverNoRecipe(display, version, Here(), SolverRecipes.PlatformsFor(tool)));

        var installer = new SolverInstaller();
        Console.Error.WriteLine(installer.Consent(recipe));
        Console.Error.WriteLine();
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
                    return 0;
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
