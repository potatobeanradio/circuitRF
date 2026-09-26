using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Net;
using CircuitRF.Engine;

namespace CircuitRF.Design.Em3d.Install;

/// <summary>How an install attempt ended.</summary>
public enum InstallStatus
{
    /// <summary>Installed, checked by discovery, and published.</summary>
    Installed,

    /// <summary>A published home for this version was already there; nothing was done.</summary>
    AlreadyInstalled,

    /// <summary>Refused before anything was fetched: a prerequisite is missing, no recipe fits, another
    /// install holds the lock, or the path cannot be built in.</summary>
    Refused,

    /// <summary>A step failed, or the installed program did not pass discovery's checks.</summary>
    Failed,

    /// <summary>Cancelled. Nothing was published.</summary>
    Cancelled,
}

/// <summary>What the prerequisite check found (R-em3d24-2b).</summary>
/// <param name="Missing">Each missing prerequisite, in words.</param>
/// <param name="Commands">The commands the user runs, one per distinct remedy, in recipe order.</param>
/// <param name="Variables">Resolved paths the steps may name (<c>${git}</c>, <c>${python}</c>).</param>
public sealed record PrerequisiteCheck(IReadOnlyList<string> Missing, IReadOnlyList<string> Commands,
                                       IReadOnlyDictionary<string, string> Variables)
{
    public bool Ok => Missing.Count == 0;
}

/// <summary>What an install attempt did, and the report a person reads.</summary>
/// <param name="Report">The whole report: success, refusal, or R-em3d24-3a's failure report.</param>
/// <param name="LogPath">The full log, when anything ran.</param>
/// <param name="FailedStep">The step that failed, by name.</param>
/// <param name="Verbatim">The upstream tool's own lines that the report quotes.</param>
/// <param name="Known">The known failure the lines matched, or null.</param>
public sealed record InstallOutcome(
    InstallStatus Status, string Report, InstallRecord? Record, string? LogPath,
    string? FailedStep = null, IReadOnlyList<string>? Verbatim = null, KnownFailure? Known = null);

/// <summary>
/// The install assistant: consent text, prerequisite check, then a recipe's steps, discovery's checks,
/// publication and the install record (brief-em3d-24, em-3d.md §7.2). <b>The one implementation</b> — the
/// Settings row, the refusal's action and <c>circuitrf solver install</c> all call it, which is the rule
/// <c>Authoring.cs</c> states for every capability with a headless spelling.
///
/// <para><b>circuitRF distributes nothing.</b> Every byte comes from the upstream a recipe names, under
/// that upstream's licence, at the user's request; the recipe is the only place a URL, a version or a
/// package appears.</para>
///
/// <para><b>No step runs a shell</b> (R-em3d24-1c). A step is a program and an argument list, started
/// through <see cref="Em3dProcessLauncher"/> with <see cref="ProcessStartInfo.ArgumentList"/>; the recipe's
/// <c>${…}</c> references are replaced inside single arguments, never spliced into a command line.</para>
///
/// <para><b>Nothing clever when it fails</b> (R-em3d24-3a): no retry, no second recipe, no patch. The
/// report names the step, quotes the tool's own lines, gives the log's path, and adds a known failure's
/// remedy only when circuitRF's own installs have met it.</para>
/// </summary>
public sealed class SolverInstaller
{
    /// <summary>The ParMETIS sentence (em-3d.md §2, §7.1): Palace's default build includes it, and the user
    /// who installs Palace accepts its terms directly — so the consent step says so, once, plainly.</summary>
    public const string ParmetisNote =
        "A default Palace build includes ParMETIS, whose licence allows commercial use for evaluation only. " +
        "By installing Palace you accept those terms.";

    public SolverInstaller(string? root = null) => Root = root ?? SolverHomes.DefaultRoot;

    /// <summary>Where every home goes (<see cref="SolverHomes.DefaultRoot"/> unless a test says otherwise).</summary>
    public string Root { get; }

    /// <summary>The discovery that checks an installed program — the shared instance, which a run also
    /// asks. A test hands in one of its own.</summary>
    public Func<SolverTool, SolverDiscovery> Discovery { get; init; } = SolverDiscovery.For;

    /// <summary>The HTTP client for circuitRF's own downloads; null creates one per install.</summary>
    public HttpClient? Http { get; init; }

    /// <summary>The environment every step inherits before the recipe's own changes — a seam so a test can
    /// plant a <c>SPACK_ROOT</c> without touching the process's environment.</summary>
    public Func<IReadOnlyDictionary<string, string>> InheritedEnvironment { get; init; } = ReadProcessEnvironment;

    /// <summary>The Linux distribution's <c>ID</c> and <c>ID_LIKE</c> words, for choosing a remedy command;
    /// null reads <c>/etc/os-release</c>.</summary>
    public IReadOnlyList<string>? Distribution { get; init; }

    /// <summary>The most bytes one circuitRF download may be. Upstream archives are tens of megabytes.</summary>
    public long MaxDownloadBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    /// <summary>How long a download may receive nothing before it is abandoned.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Outer work units an install of <paramref name="recipe"/> reports — each step, then the
    /// check. What a caller gives <see cref="RunControl.Total"/>.</summary>
    public static long WorkUnits(SolverRecipe recipe) => recipe.Steps.Count + 1;

    public string HomeFor(SolverRecipe recipe) => SolverHomes.Home(Root, recipe.Tool, recipe.Version);

    /// <summary>
    /// The tool id a refusal may offer <i>Install …</i> for, or null (brief-em3d-24 §0): offered when this
    /// machine has a recipe for the tool and installing would change the answer — nothing usable was found,
    /// or what was found is not a validated version. Not offered when Settings or the environment variable
    /// NAMES a program, because a named program outranks an installed one and the install would change
    /// nothing a run sees; nor for a missing capability in a validated build, whose refusal names the build
    /// option instead.
    /// </summary>
    public static string? OfferFor(SolverReadiness readiness)
        => !readiness.Proceeds && WouldHelp(SolverDiscovery.For(readiness.Tool), readiness.Installation)
           && SolverRecipes.For(readiness.Tool) is not null
            ? SolverHomes.ToolId(readiness.Tool)
            : null;

    /// <summary>Whether installing would change what <paramref name="discovery"/> finds: nothing usable was
    /// found or it is unvalidated, and no named route (Settings, the environment variable) outranks the
    /// Installed route.</summary>
    internal static bool WouldHelp(SolverDiscovery discovery, SolverInstallation? found)
    {
        if (found is { Validated: true }) return false;
        if (found?.HowFound is SolverHowFound.Settings or SolverHowFound.Environment or SolverHowFound.Installed) return false;
        return discovery.PreferredCommand?.Invoke()?.Trim() is not { Length: > 0 }
            && discovery.ReadEnvironment(discovery.EnvironmentVariable)?.Trim() is not { Length: > 0 };
    }

    // ── R-em3d24-2a: consent ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the user is agreeing to, before anything is fetched: the program and version, every upstream
    /// URL, where it goes, what it cost when measured and on what machine, the ParMETIS sentence for
    /// Palace, and that it runs in the background and can be cancelled.
    /// </summary>
    public string Consent(SolverRecipe recipe)
    {
        string name = Discovery(recipe.Tool).Name;
        var sb = new StringBuilder();
        sb.AppendLine($"Install {name} {recipe.Version} for {SolverRecipes.Describe(recipe.Platform)} " +
                      $"{SolverRecipes.Describe(recipe.Architecture)} (recipe {recipe.Id}).");
        sb.AppendLine();
        sb.AppendLine("It fetches from these upstream sources, and from nowhere else:");
        foreach (var s in recipe.Sources)
        {
            string check = s.Sha256 is { } sha
                ? $"checked against upstream's SHA-256 {sha[..Math.Min(12, sha.Length)]}…"
                : s.ChecksumNote!;
            sb.AppendLine($"  • {s.Url} — fetched by {s.FetchedBy}; {check}");
        }
        sb.AppendLine();
        sb.AppendLine($"It installs into {HomeFor(recipe)}, and changes nothing outside it.");
        sb.AppendLine(DescribeMeasured(recipe.Measured));
        if (recipe.Tool == SolverTool.Palace) sb.AppendLine(ParmetisNote);
        sb.AppendLine("It runs in the background and can be cancelled at any point. Cancelling leaves nothing " +
                      "circuitRF would find, and the next attempt removes what it left.");
        return sb.ToString().TrimEnd();
    }

    private static string DescribeMeasured(RecipeMeasured m)
    {
        if (m.Minutes is null && m.DiskGB is null) return $"Time and disk: not yet measured — {m.Source}.";
        string time = m.Minutes is { } min
            ? min >= 90 ? $"about {min / 60:0.#} hours" : min < 1.5 ? "about a minute" : $"about {min:0} minutes"
            : "an unmeasured time";
        string disk = m.DiskGB is { } gb ? (gb < 1 ? $"{gb * 1024:0} MB" : $"{gb:0.#} GB") : "an unmeasured amount of disk";
        return $"Measured: {time} and {disk} ({m.Source}).";
    }

    // ── R-em3d24-2b: prerequisites ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks every prerequisite, fetching nothing. A missing one names the ONE command the user runs for
    /// this distribution; circuitRF runs none of them, never runs <c>sudo</c> and never asks for a password.
    /// </summary>
    public PrerequisiteCheck CheckPrerequisites(SolverRecipe recipe)
    {
        var missing   = new List<string>();
        var commands  = new List<string>();
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        var env       = InheritedEnvironment();

        foreach (var p in recipe.Prerequisites)
        {
            string? found = null;
            string? why   = null;
            switch (p.Kind)
            {
                case "file" when p.Path is { } exactPath:
                    string path = ExpandStatic(exactPath, env);
                    if (File.Exists(path) || Directory.Exists(path)) found = path;
                    else why = $"{p.Name} ({path}) is not there";
                    break;

                case "file":
                    found = p.Search.Select(d => ExpandStatic(d, env)).Where(Directory.Exists)
                                    .SelectMany(d => SafeEntries(d, p.Name)).FirstOrDefault();
                    if (found is null) why = $"{p.Name} is not installed (looked in {string.Join(", ", p.Search.Select(s => ExpandStatic(s, env)))})";
                    break;

                case "program":
                    found = p.Search.SelectMany(d => ExpandStatic(d, env).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                                    .Select(d => ProgramIn(d, p.Name)).FirstOrDefault(c => c is not null);
                    if (found is null) why = $"{p.Name} is not installed (looked in {string.Join(", ", p.Search.Select(s => ExpandStatic(s, env)))})";
                    else if (p.MinVersion is { } min)
                    {
                        var run = RunQuick(found, p.Arguments.Count > 0 ? p.Arguments : ["--version"]);
                        var v   = Regex.Match(run.Output, @"\d+(?:\.\d+)+");
                        if (!v.Success || CompareVersions(v.Value, min) < 0)
                        {
                            why   = $"{p.Name} at {found} is {(v.Success ? "version " + v.Value : "of an unknown version")}; {min} or newer is needed";
                            found = null;
                        }
                    }
                    break;

                case "command":
                    string target = p.Path is { } exact ? ExpandStatic(exact, env)
                        : p.Search.Select(d => ProgramIn(ExpandStatic(d, env), p.Name)).FirstOrDefault(c => c is not null) ?? p.Name;
                    var r = File.Exists(target) ? RunQuick(target, p.Arguments) : new QuickRun(-1, "");
                    if (r.ExitCode == 0) found = target;
                    else why = File.Exists(target)
                        ? $"{p.Name} ({target}) did not pass its check (exit {r.ExitCode}{(FirstLine(r.Output) is { Length: > 0 } l ? ": " + l : "")})"
                        : $"{p.Name} ({target}) is not there";
                    break;
            }

            if (found is not null)
            {
                if (p.Variable is { Length: > 0 } v) variables[v] = found;
                continue;
            }
            missing.Add(why!);
            string remedy = RemedyFor(p);
            if (!commands.Contains(remedy, StringComparer.Ordinal)) commands.Add(remedy);
        }
        return new PrerequisiteCheck(missing, commands, variables);
    }

    /// <summary>The refusal a failed prerequisite check becomes.</summary>
    public string DescribePrerequisites(SolverRecipe recipe, PrerequisiteCheck check)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Discovery(recipe.Tool).Name} {recipe.Version} cannot be installed yet, and nothing was downloaded:");
        foreach (string m in check.Missing) sb.AppendLine($"  • {m}.");
        sb.AppendLine(check.Commands.Count == 1
            ? "Run this, then install again (circuitRF does not run it for you):"
            : "Run these, in order, then install again (circuitRF does not run them for you):");
        foreach (string c in check.Commands) sb.AppendLine($"    {c}");
        return sb.ToString().TrimEnd();
    }

    private string RemedyFor(RecipePrerequisite p)
    {
        foreach (string id in Distribution ?? ReadOsRelease())
            if (p.Remedy.TryGetValue(id, out var r)) return r;
        return p.Remedy.TryGetValue("default", out var d) ? d : p.Remedy.Values.First();
    }

    private static IReadOnlyList<string> ReadOsRelease()
    {
        if (!OperatingSystem.IsLinux()) return [];
        try
        {
            var ids = new List<string>();
            foreach (string line in File.ReadAllLines("/etc/os-release"))
            {
                if (line.StartsWith("ID=", StringComparison.Ordinal)) ids.Insert(0, line[3..].Trim('"', ' '));
                else if (line.StartsWith("ID_LIKE=", StringComparison.Ordinal))
                    ids.AddRange(line[8..].Trim('"', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
            return ids;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    // ── the install ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Installs <paramref name="recipe"/>: refuses on a held lock, an unbuildable path or a missing
    /// prerequisite; removes what an earlier attempt left; runs every step; checks the result with
    /// discovery (version and every capability probe); then publishes and writes the record. Blocking —
    /// run it off the UI thread. Progress and cancellation ride <paramref name="control"/>.
    /// </summary>
    public InstallOutcome Install(SolverRecipe recipe, RunControl? control = null)
    {
        control ??= new RunControl();
        var discovery = Discovery(recipe.Tool);
        string name   = discovery.Name;
        string home   = HomeFor(recipe);
        string toolDir = SolverHomes.ToolDirectory(Root, recipe.Tool);

        if (!Path.IsPathRooted(Root))
            return new(InstallStatus.Refused,
                       $"{name} was not installed: circuitRF could not work out an absolute per-user folder to install into " +
                       $"(it got '{Root}'). Set {UserStateDirectory.EnvironmentVariable} to a folder of your own and try again.", null, null);

        FileStream? gate;
        try
        {
            Directory.CreateDirectory(toolDir);
            gate = new FileStream(Path.Combine(toolDir, SolverHomes.InstallLockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                  FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return new(InstallStatus.Refused, $"Another install of {name} is already running. Wait for it to finish, or cancel it.", null, null);
        }
        catch (UnauthorizedAccessException e)
        {
            return new(InstallStatus.Refused, $"{name} cannot be installed into {toolDir}: {e.Message}", null, null);
        }

        using (gate)
        {
            if (InstallRecord.TryRead(Path.Combine(home, SolverHomes.RecordFile)) is { } existing)
                return new(InstallStatus.AlreadyInstalled,
                           $"{name} {recipe.Version} is already installed by circuitRF at {existing.Home} ({existing.Program}).",
                           existing, null);

            if (recipe.NeedsSpaceFreePath && SolverHomes.HasWhitespace(home))
                return new(InstallStatus.Refused,
                           $"{name} is built from source, and its build refuses a directory whose path contains a space " +
                           $"('configure: error: unsafe srcdir value'). This install would go into {home}. " +
                           KnownFailures.Explain(KnownFailures.All.First(k => k.Id == "autotools-path-has-space")), null, null);

            var prereqs = CheckPrerequisites(recipe);
            if (!prereqs.Ok) return new(InstallStatus.Refused, DescribePrerequisites(recipe, prereqs), null, null);

            // R-em3d24-2d — whatever an earlier attempt left goes now: a .partial, or a home with no record.
            string partial = SolverHomes.Partial(home);
            DeleteTree(partial);
            DeleteTree(home);

            string build = recipe.Relocatable ? partial : home;
            Directory.CreateDirectory(build);
            Directory.CreateDirectory(SolverHomes.LogDirectory(Root, recipe.Tool));
            string logPath = Path.Combine(SolverHomes.LogDirectory(Root, recipe.Tool),
                                          $"{recipe.Version}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var clock = Stopwatch.StartNew();

            using var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            log.WriteLine($"circuitRF solver install — {recipe.Id} — {DateTimeOffset.Now:O}");
            log.WriteLine($"home: {home}{(recipe.Relocatable ? $" (built in {partial})" : " (built in place)")}");

            var run = new Execution(this, recipe, build, prereqs.Variables, log, control);
            var sources = new List<InstallSource>();
            try
            {
                foreach (var step in recipe.Steps)
                {
                    control.ThrowIfCancellationRequested();
                    log.WriteLine();
                    log.WriteLine($"── {step.Name} ──");
                    if (run.Do(step, sources) is { } failure)
                        return Fail(recipe, name, step.Name, failure.Verbatim, failure.Summary, logPath);
                    control.Tick();
                }
                control.ThrowIfCancellationRequested();

                // R-em3d24-2e — not a success until discovery agrees: the version, and every capability probe.
                control.BeginStage("Checking the installed program");
                log.WriteLine();
                log.WriteLine("── Checking the installed program ──");
                string program = run.Expand(recipe.Program);
                if (!discovery.TryProbe(program, SolverHowFound.Installed, "installed by circuitRF", out var installed, out string? why))
                    return Fail(recipe, name, "Checking the installed program", [], $"the installed program at {program} did not answer: {why}.", logPath);
                log.WriteLine($"identified: {installed!.DescribeVersion()}");
                if (!installed.Validated)
                    return Fail(recipe, name, "Checking the installed program", [installed.Banner], discovery.DescribeUnvalidated(installed), logPath);
                foreach (var capability in CapabilitiesToCheck(recipe.Tool))
                {
                    var verdict = discovery.Probe(installed, capability);
                    log.WriteLine($"capability {capability}: {(verdict.Available ? "yes" : "no")} — {verdict.Detail}");
                    if (!verdict.Available)
                    {
                        string refusal = discovery.DescribeMissingCapability(installed, verdict);
                        return Fail(recipe, name, "Checking the installed program", [refusal], refusal, logPath);
                    }
                }
                control.Tick();

                // A downloaded archive has done its job once it is unpacked and its checksum is on the record;
                // keeping it would count its size twice in what the user is told the install costs.
                DeleteTree(Path.Combine(build, "downloads"));

                // Every source the recipe names is on the record, checked or not — a source a step fetched
                // (git, Spack) carries the recipe's own note on how it is pinned.
                foreach (var s in recipe.Sources)
                    if (!sources.Any(x => x.Url == s.Url))
                        sources.Add(new InstallSource(s.Url, s.FetchedBy, s.Sha256, Verified: false, s.ChecksumNote));

                // Publish. The record is written with the FINAL paths; for a relocatable home, the rename is
                // the publication, and for an in-place one the record's own atomic write is.
                var record = new InstallRecord
                {
                    Tool             = recipe.Tool,
                    Version          = recipe.Version,
                    Recipe           = recipe.Id,
                    Home             = home,
                    Program          = run.Expand(recipe.Program, home),
                    SpackInstallTree = recipe.SpackInstallTree is { } tree ? run.Expand(tree, home) : null,
                    Identified       = installed.DescribeVersion(),
                    InstalledAt      = DateTimeOffset.Now,
                    SizeBytes        = MeasureBytes(build),
                    ElapsedSeconds   = Math.Round(clock.Elapsed.TotalSeconds),
                    Sources          = sources,
                };
                record.Write(Path.Combine(build, SolverHomes.RecordFile));
                if (recipe.Relocatable) Directory.Move(partial, home);
                log.WriteLine($"published: {home} ({record.SizeBytes:N0} bytes, {clock.Elapsed:hh\\:mm\\:ss})");

                return new(InstallStatus.Installed, DescribeSuccess(recipe, record, discovery, clock.Elapsed), record, logPath);
            }
            catch (OperationCanceledException)
            {
                run.KillRunning();
                log.WriteLine("cancelled");
                return new(InstallStatus.Cancelled,
                           $"The install of {name} {recipe.Version} was cancelled. Nothing was published; what it left in " +
                           $"{build} is removed at the next attempt. The log is {logPath}.", null, logPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return Fail(recipe, name, run.CurrentStep ?? "Publishing the install", [e.Message], e.Message, logPath);
            }
        }
    }

    /// <summary>What an install of <paramref name="tool"/> must prove it can do — every capability a run
    /// could ask of it, brief 23's rows included, so a build that could not run a wave port is not called
    /// installed.</summary>
    public static IReadOnlyList<SolverCapability> CapabilitiesToCheck(SolverTool tool)
        => tool == SolverTool.Palace ? Enum.GetValues<SolverCapability>() : [];

    private string DescribeSuccess(SolverRecipe recipe, InstallRecord record, SolverDiscovery discovery, TimeSpan elapsed)
    {
        var sb = new StringBuilder();
        sb.Append($"Installed {discovery.Name} {record.Identified} at {record.Program}, in {Minutes(elapsed)} " +
                  $"({record.SizeBytes / 1e9:0.00} GB). It passed the version check");
        sb.Append(recipe.Tool == SolverTool.Palace ? " and every capability probe." : ".");
        var found = discovery.Find(out _);
        if (found is { HowFound: SolverHowFound.Installed } && PathsEqual(found.Path, record.Program))
            sb.Append(" A 3D run now finds it (installed by circuitRF).");
        else if (found is not null)
            sb.Append($" A 3D run still uses the {discovery.Name} {found.HowFoundText} ({found.Path}), which comes first; " +
                      "clear that in Settings ▸ 3D EM to use this one.");
        sb.Append(DescribeSuperseded(recipe));
        return sb.ToString();
    }

    /// <summary>
    /// brief-em3d-25 R-em3d25-3 — every older version of this tool circuitRF installed, with its size,
    /// offered for removal and never removed: a user may still want to compare old results against it.
    /// </summary>
    internal string DescribeSuperseded(SolverRecipe recipe)
    {
        var older = new SolverUninstaller([Root]) { Discovery = Discovery }.Superseded(recipe.Tool, recipe.Version);
        if (older.Count == 0) return "";
        string name = Discovery(recipe.Tool).Name;
        var sb = new StringBuilder();
        sb.Append($" circuitRF also installed {(older.Count == 1 ? "an older version" : "older versions")} of {name}, kept so earlier results can be compared: ");
        sb.Append(string.Join("; ", older.Select(o => $"{name} {o.Record.Version} ({SolverUninstaller.Size(o.Bytes)}, at {o.Record.Home})")));
        sb.Append(". Settings ▸ 3D EM offers to remove " + (older.Count == 1 ? "it" : "each") +
                  $", as does 'circuitrf solver remove {SolverHomes.ToolId(recipe.Tool)} --version <v>'.");
        return sb.ToString();
    }

    private static string Minutes(TimeSpan t) => t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0} min" : $"{t.TotalSeconds:0} s";

    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
                         OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ── R-em3d24-3: the failure report ───────────────────────────────────────────────────────────

    private InstallOutcome Fail(SolverRecipe recipe, string name, string step, IReadOnlyList<string> output, string summary, string logPath)
    {
        var verbatim = QuoteVerbatim(output);
        var known    = KnownFailures.Match(output.Append(summary));
        var sb = new StringBuilder();
        sb.AppendLine($"{name} {recipe.Version} did not install. The step that failed: {step}. {summary}");
        if (verbatim.Count > 0)
        {
            sb.AppendLine("Its own output:");
            foreach (string l in verbatim) sb.AppendLine("    " + l);
        }
        sb.AppendLine($"The full log is {logPath}.");
        sb.Append(KnownFailures.Explain(known));
        return new(InstallStatus.Failed, sb.ToString(), null, logPath, step, verbatim, known);
    }

    /// <summary>R-em3d24-3a: the last lines containing <c>Error</c>, or else the last 20 lines — verbatim.</summary>
    public static IReadOnlyList<string> QuoteVerbatim(IReadOnlyList<string> lines)
    {
        var errors = lines.Where(l => l.Contains("Error", StringComparison.Ordinal)).ToList();
        var pick = errors.Count > 0 ? errors : lines.Where(l => l.Trim().Length > 0).ToList();
        return pick.Skip(Math.Max(0, pick.Count - 20)).ToList();
    }

    // ── one attempt's steps ──────────────────────────────────────────────────────────────────────

    private sealed record StepFailure(string Summary, IReadOnlyList<string> Verbatim);

    /// <summary>The state of one attempt: its variables, its log, the running process.</summary>
    private sealed class Execution(SolverInstaller owner, SolverRecipe recipe, string build,
                                   IReadOnlyDictionary<string, string> prerequisites, StreamWriter log, RunControl control)
    {
        private readonly Dictionary<string, string> _vars = new(prerequisites, StringComparer.Ordinal)
        {
            ["home"]      = build,
            ["recipeId"]  = recipe.Id,
            ["downloads"] = Path.Combine(build, "downloads"),
            ["cores"]     = Math.Max(1, CircuitRF.Engine.Em3d.PhysicalCores.Count).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["jobs"]      = BuildJobs().ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        private readonly Lock _gate = new();
        private Process? _running;

        public string? CurrentStep { get; private set; }

        public string Expand(string text, string? homeOverride = null)
        {
            if (homeOverride is null) return owner.Expand(text, _vars);
            var vars = new Dictionary<string, string>(_vars, StringComparer.Ordinal)
            {
                ["home"] = homeOverride, ["downloads"] = Path.Combine(homeOverride, "downloads"),
            };
            return owner.Expand(text, vars);
        }

        public void KillRunning()
        {
            lock (_gate)
            {
                try { _running?.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }
        }

        public StepFailure? Do(RecipeStep step, List<InstallSource> sources)
        {
            CurrentStep = step.Name;
            switch (step.Kind)
            {
                case RecipeStepKind.Download: return Download(step, sources);
                case RecipeStepKind.Extract:  return Extract(step);
                case RecipeStepKind.Symlink:  return Symlink(step);
                case RecipeStepKind.Write:    return WriteFile(step);
                default:                      return RunStep(step, sources);
            }
        }

        private StepFailure? Download(RecipeStep step, List<InstallSource> sources)
        {
            var src  = recipe.Sources.First(s => s.Id == step.Source);
            string dir  = _vars["downloads"];
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, src.File!);
            control.BeginStage(step.Name);
            log.WriteLine($"GET {src.Url} -> {dest}");

            long? length = null;
            long lastShown = -1;
            void Show(long n)
            {
                if (n - lastShown < 256 * 1024 && (length is null || n < length)) return;
                lastShown = n;
                control.SetStageDetail(length is { } l ? $"{n / 1048576.0:0.0} MB of {l / 1048576.0:0.0} MB" : $"{n / 1048576.0:0.0} MB");
            }

            var uri = new Uri(src.Url);
            if (uri.IsFile)
            {
                // A local file (a test's fake upstream). Copied through the same byte counter.
                using var input  = File.OpenRead(uri.LocalPath);
                using var output = File.Create(dest);
                length = input.Length;
                var buffer = new byte[128 * 1024];
                long total = 0;
                int n;
                while ((n = input.Read(buffer)) > 0)
                {
                    control.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, n);
                    total += n;
                    Show(total);
                }
            }
            else
            {
                string partialFile = dest + SolverHomes.PartialSuffix;
                var http = owner.Http ?? CreateHttpClient();
                try
                {
                    var result = StreamingDownload.TransferAsync(
                        http, src.Url, partialFile, 0, owner.MaxDownloadBytes, owner.StallTimeout,
                        new SyncProgress(Show), keepGoing: null, control.Token, onLength: l => length = l)
                        .GetAwaiter().GetResult();
                    switch (result.End)
                    {
                        case TransferEnd.Completed: break;
                        case TransferEnd.Cancelled: control.Token.ThrowIfCancellationRequested(); break;
                        case TransferEnd.HttpError:
                            return new($"The server answered {result.StatusCode} for {src.Url}.", [$"HTTP {result.StatusCode} {src.Url}"]);
                        case TransferEnd.Stalled:
                            return new($"Nothing arrived from {src.Url} for {owner.StallTimeout.TotalSeconds:0} s.", []);
                        case TransferEnd.OverCap:
                            return new($"{src.Url} sent more than {owner.MaxDownloadBytes / 1048576} MB, which no archive here is.", []);
                        default:
                            return new($"The download from {src.Url} failed: {result.Error}", result.Error is { } e ? [e] : []);
                    }
                    if (length is { } expected && new FileInfo(partialFile).Length != expected)
                        return new($"The download from {src.Url} ended short: {new FileInfo(partialFile).Length:N0} of {expected:N0} bytes.", []);
                    File.Move(partialFile, dest, overwrite: true);
                }
                finally
                {
                    if (owner.Http is null) http.Dispose();
                }
            }

            string actual;
            using (var f = File.OpenRead(dest)) actual = Convert.ToHexStringLower(SHA256.HashData(f));
            log.WriteLine($"sha256 {actual}");
            if (src.Sha256 is { } published)
            {
                if (!string.Equals(published, actual, StringComparison.OrdinalIgnoreCase))
                    return new($"The file from {src.Url} does not match the checksum upstream publishes, so it was not used.",
                               [$"expected sha256 {published.ToLowerInvariant()}", $"received sha256 {actual}"]);
                sources.Add(new InstallSource(src.Url, src.FetchedBy, published, Verified: true, null));
            }
            else
            {
                sources.Add(new InstallSource(src.Url, src.FetchedBy, null, Verified: false,
                                              $"{src.ChecksumNote} (the file received had SHA-256 {actual})"));
            }
            return null;
        }

        private StepFailure? Extract(RecipeStep step)
        {
            var src = recipe.Sources.First(s => s.Id == step.Source);
            string archive = Path.Combine(_vars["downloads"], src.File!);
            string into = Expand(step.Into!);
            Directory.CreateDirectory(into);
            control.BeginStage(step.Name);
            log.WriteLine($"extract {archive} -> {into}");
            if (src.File!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ZipFile.ExtractToDirectory(archive, into, overwriteFiles: false);
            else
            {
                using var file = File.OpenRead(archive);
                using var gz   = new GZipStream(file, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gz, into, overwriteFiles: false);
            }
            control.ThrowIfCancellationRequested();
            return null;
        }

        private StepFailure? Symlink(RecipeStep step)
        {
            string link = Expand(step.Link!), target = Expand(step.Target!);
            control.BeginStage(step.Name);
            log.WriteLine($"symlink {link} -> {target}");
            if (File.Exists(link) || Directory.Exists(link) || new FileInfo(link).LinkTarget is not null) File.Delete(link);
            if (Directory.Exists(target)) Directory.CreateSymbolicLink(link, target);
            else File.CreateSymbolicLink(link, target);
            return null;
        }

        private StepFailure? WriteFile(RecipeStep step)
        {
            string path = Expand(step.Path!);
            control.BeginStage(step.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string text = string.Join("\n", step.Lines.Select(l => Expand(l))) + "\n";
            File.WriteAllText(path, text, new UTF8Encoding(false));
            log.WriteLine($"wrote {path}:");
            log.Write(text);
            return null;
        }

        private StepFailure? RunStep(RecipeStep step, List<InstallSource> sources)
        {
            var env = owner.StepEnvironment(recipe, _vars);
            string command = Expand(step.Command!);
            if (!Path.IsPathRooted(command))
                command = (env.TryGetValue("PATH", out var searchPath) ? searchPath : "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                              .Select(d => ProgramIn(d, command)).FirstOrDefault(c => c is not null) ?? command;
            var args = step.Arguments.Select(a => Expand(a)).ToList();
            string cwd = step.WorkingDirectory is { } w ? Expand(w) : build;
            Directory.CreateDirectory(cwd);

            var psi = new ProcessStartInfo(command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                RedirectStandardInput  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                WorkingDirectory       = cwd,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            psi.Environment.Clear();
            foreach (var (k, v) in env) psi.Environment[k] = v;

            log.WriteLine("$ " + string.Join(' ', new[] { command }.Concat(args).Select(Quote)));
            var lines    = new List<string>();
            var captured = new StringBuilder();
            var spack    = new SpackProgress(control, step, owner._spackTotal);
            var lastDetail = Stopwatch.StartNew();
            control.BeginStage(step.Name, step.Progress == RecipeProgress.SpackInstall ? owner._spackTotal : 0,
                               step.Progress == RecipeProgress.SpackInstall ? "package(s)" : "");

            void OnLine(string? line, bool stdout)
            {
                if (line is null) return;
                lock (lines)
                {
                    log.WriteLine(line);
                    lines.Add(line);
                    if (lines.Count > 4000) lines.RemoveRange(0, 1000);
                    if (stdout && step.CaptureAs is not null) captured.AppendLine(line);
                }
                if (step.Progress == RecipeProgress.Lines)
                {
                    if (lastDetail.ElapsedMilliseconds < 500) return;
                    lastDetail.Restart();
                    string t = line.Trim();
                    if (t.Length > 0) control.SetStageDetail(t.Length > 100 ? t[..100] + "…" : t);
                }
                else spack.OnLine(line);
            }

            Process? p;
            try { p = Em3dProcessLauncher.Start(psi, Em3dProcessKind.Installer); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                return new($"'{command}' could not be started: {e.Message}.", [e.Message]);
            }
            if (p is null) return new($"'{command}' could not be started.", []);

            using (p)
            {
                lock (_gate) _running = p;
                p.OutputDataReceived += (_, e) => OnLine(e.Data, stdout: true);
                p.ErrorDataReceived  += (_, e) => OnLine(e.Data, stdout: false);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.StandardInput.Close();   // nothing a step runs may stop to ask a question
                while (!p.WaitForExit(250))
                {
                    if (control.Token.IsCancellationRequested)
                    {
                        KillRunning();
                        p.WaitForExit();
                        control.ThrowIfCancellationRequested();
                    }
                }
                p.WaitForExit();
                lock (_gate) _running = null;

                List<string> snapshot;
                lock (lines) snapshot = [.. lines];
                if (p.ExitCode != 0)
                    return new($"'{Path.GetFileName(command)}' exited with code {p.ExitCode}.", snapshot);

                if (step.Progress == RecipeProgress.SpackConcretize)
                {
                    owner._spackTotal = spack.ToBuild;
                    log.WriteLine($"concretized: {spack.ToBuild} package(s) to build");
                }

                if (step.CaptureAs is { } name)
                {
                    string value = captured.ToString().Trim();
                    if (value.Contains('\n')) value = value.Split('\n').Last(l => l.Trim().Length > 0).Trim();
                    _vars[name] = value;
                    log.WriteLine($"{name} = {value}");
                    if (step.Expect is { } expected && !string.Equals(Expand(expected), value, StringComparison.Ordinal))
                        return new($"It printed '{value}', and this recipe was validated against '{expected}'.", snapshot);
                    if (step.Expect is not null)
                        foreach (var s in recipe.Sources.Where(s => s.FetchedBy != "circuitRF" && s.Id == name))
                            sources.Add(new InstallSource(s.Url, s.FetchedBy, null, Verified: true, $"{s.ChecksumNote} (checked: {value})"));
                }
            }
            return null;
        }

        private static string Quote(string a) => a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c is '"' or '\'') ? a : $"'{a}'";
    }

    /// <summary>The number of packages the last concretize said it would build (R-em3d24-2c).</summary>
    private long _spackTotal;

    /// <summary>
    /// Spack's own lines as progress. <c>spack concretize</c> marks each node it will build with <c> - </c>
    /// before its hash; <c>spack install</c> prints <c>[+] &lt;hash&gt; &lt;name&gt;@&lt;version&gt; &lt;prefix&gt;</c>
    /// when one finishes (Spack v1.2.2, measured 2026-09-25 — the brief's "[+] &lt;prefix&gt;" is an older
    /// format, so both are read) and <c>[ ] &lt;hash&gt; &lt;name&gt;@&lt;version&gt; &lt;phase&gt;</c> while one builds.
    /// </summary>
    private sealed class SpackProgress(RunControl control, RecipeStep step, long total)
    {
        private static readonly Regex ToBuildLine  = new(@"^\s*-\s+([a-z0-9]{7})\s+\^?\S", RegexOptions.Compiled);
        private static readonly Regex InstalledLine = new(@"^\[\+\]\s+(?:[a-z0-9]{7}\s+)?(\S+)", RegexOptions.Compiled);
        private static readonly Regex BuildingLine  = new(@"^\[ \]\s+[a-z0-9]{7}\s+(\S+?)@\S*\s+(\S+)", RegexOptions.Compiled);
        private readonly HashSet<string> _toBuild = [];
        private readonly HashSet<string> _done = [];

        public long ToBuild => _toBuild.Count;

        public void OnLine(string line)
        {
            if (step.Progress == RecipeProgress.SpackConcretize)
            {
                if (ToBuildLine.Match(line) is { Success: true } m) _toBuild.Add(m.Groups[1].Value);
                return;
            }
            if (InstalledLine.Match(line) is { Success: true } done)
            {
                string token = done.Groups[1].Value;
                string pkg = token.Contains('@') ? token[..token.IndexOf('@')]
                           : Regex.Replace(Path.GetFileName(token.TrimEnd('/')), @"-[a-z0-9]{32}$", "");
                if (!_done.Add(pkg)) return;
                string label = total > 0 ? $"{step.Name}: package {_done.Count} of {total} — {pkg}" : $"{step.Name}: {pkg} installed";
                control.TickStage(1, label);
                return;
            }
            if (BuildingLine.Match(line) is { Success: true } b)
                control.SetStageDetail($"building {b.Groups[1].Value} ({b.Groups[2].Value})");
        }
    }

    // ── the environment and ${…} ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The environment one step runs with: inherited, minus every variable the recipe clears (a trailing
    /// <c>*</c> clears a prefix — <c>SPACK_*</c> keeps the user's own Spack out of it), plus the recipe's own.
    /// Public so the gate can scan it (brief-em3d-24 gate 8).
    /// </summary>
    public IReadOnlyDictionary<string, string> StepEnvironment(SolverRecipe recipe, IReadOnlyDictionary<string, string> variables)
    {
        var inherited = InheritedEnvironment();
        var env = new Dictionary<string, string>(inherited, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string pattern in recipe.ClearEnvironment)
            foreach (string key in env.Keys.ToList())
                if (pattern.EndsWith('*') ? key.StartsWith(pattern[..^1], StringComparison.Ordinal) : key == pattern)
                    env.Remove(key);
        var vars = new Dictionary<string, string>(variables, StringComparer.Ordinal);
        foreach (var (k, v) in recipe.Environment) env[k] = Expand(v, vars, inherited);
        return env;
    }

    /// <summary>
    /// Every step of <paramref name="recipe"/> as it would run with <paramref name="home"/> as its home — the
    /// command, each argument, each file written, each link — with values only a step can produce left as
    /// <c>${name}</c>. Runs nothing. What gate 8 scans for a path outside the home.
    /// </summary>
    public IReadOnlyList<string> ResolvedPlan(SolverRecipe recipe, string home, IReadOnlyDictionary<string, string> prerequisites)
    {
        var vars = new Dictionary<string, string>(prerequisites, StringComparer.Ordinal)
        {
            ["home"] = home, ["recipeId"] = recipe.Id, ["downloads"] = Path.Combine(home, "downloads"), ["cores"] = "1", ["jobs"] = "1",
        };
        foreach (var s in recipe.Steps) if (s.CaptureAs is { } c) vars.TryAdd(c, "${" + c + "}");
        string X(string t) => Expand(t, vars);
        var plan = new List<string>();
        foreach (var s in recipe.Steps)
        {
            switch (s.Kind)
            {
                case RecipeStepKind.Run:
                    plan.Add(string.Join(' ', new[] { X(s.Command!) }.Concat(s.Arguments.Select(X))));
                    if (s.WorkingDirectory is { } w) plan.Add("cwd " + X(w));
                    break;
                case RecipeStepKind.Write:
                    plan.Add("write " + X(s.Path!));
                    plan.AddRange(s.Lines.Select(X));
                    break;
                case RecipeStepKind.Symlink:
                    plan.Add($"symlink {X(s.Link!)} -> {X(s.Target!)}");
                    break;
                case RecipeStepKind.Extract:
                    plan.Add("extract into " + X(s.Into!));
                    break;
            }
        }
        plan.Add("program " + X(recipe.Program));
        if (recipe.SpackInstallTree is { } tree) plan.Add("spack install tree " + X(tree));
        return plan;
    }

    private static readonly Regex Reference = new(@"\$\{(env:)?([A-Za-z0-9_]+)\}", RegexOptions.Compiled);

    private string Expand(string text, IReadOnlyDictionary<string, string> vars)
        => Expand(text, vars, InheritedEnvironment());

    /// <summary>Replaces <c>${name}</c> and <c>${env:NAME}</c>. An unknown name is left as written, which
    /// then fails visibly in the step that used it rather than silently becoming an empty argument.</summary>
    private static string Expand(string text, IReadOnlyDictionary<string, string> vars, IReadOnlyDictionary<string, string> env)
        => Reference.Replace(text, m => m.Groups[1].Success
            ? env.TryGetValue(m.Groups[2].Value, out var e) ? e : ""
            : vars.TryGetValue(m.Groups[2].Value, out var v) ? v : m.Value);

    private string ExpandStatic(string text, IReadOnlyDictionary<string, string> env)
        => Expand(text, new Dictionary<string, string>(StringComparer.Ordinal), env);

    private static IReadOnlyDictionary<string, string> ReadProcessEnvironment()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            if (e.Key is string k && e.Value is string v) map[k] = v;
        return map;
    }

    // ── small helpers ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>${jobs}</c>: parallel compile jobs for a source build — the physical cores, but no more than one
    /// per 2 GiB of the memory this process can see (a container's limit, not the host's). A C++ compile of
    /// MFEM or Palace takes of order a gigabyte, and a build that is OOM-killed half an hour in is the worst
    /// kind of failure to report. On F0's 16 GB M4 this gives 8, the -j F0 built with.
    /// </summary>
    public static int BuildJobs()
    {
        int cores = Math.Max(1, CircuitRF.Engine.Em3d.PhysicalCores.Count);
        long bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        int byMemory = bytes > 0 ? (int)Math.Max(1, bytes / (2L * 1024 * 1024 * 1024)) : cores;
        return Math.Min(cores, byMemory);
    }

    private sealed record QuickRun(int ExitCode, string Output);

    /// <summary>A prerequisite's own question — seconds at most, counted as a probe.</summary>
    private static QuickRun RunQuick(string program, IReadOnlyList<string> arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(program)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (string a in arguments) psi.ArgumentList.Add(a);
            using var p = Em3dProcessLauncher.Start(psi, Em3dProcessKind.Probe);
            if (p is null) return new(-1, "");
            p.StandardInput.Close();
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* gone */ }
                return new(-1, "it did not answer within 30 s");
            }
            p.WaitForExit();
            return new(p.ExitCode, o.GetAwaiter().GetResult() + e.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new(-1, ex.Message);
        }
    }

    private static IEnumerable<string> SafeEntries(string directory, string pattern)
    {
        try { return Directory.EnumerateFileSystemEntries(directory, pattern).Order(StringComparer.Ordinal).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    private static string? ProgramIn(string directory, string name)
    {
        foreach (string ext in OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : [""])
        {
            try
            {
                string c = Path.Combine(directory.Trim(), name + ext);
                if (File.Exists(c)) return c;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    private static int CompareVersions(string a, string b)
    {
        var x = a.Split('.').Select(s => int.TryParse(s, out int n) ? n : 0).ToArray();
        var y = b.Split('.').Select(s => int.TryParse(s, out int n) ? n : 0).ToArray();
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }

    private static string FirstLine(string s) => s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    internal static long MeasureBytes(string dir)
    {
        long total = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", options)) total += f.Length;
        return total;
    }

    /// <summary>Removes a directory tree an earlier attempt left, making read-only directories writable
    /// first — a build tree can hold them, and a delete that stops half way would leave the next attempt
    /// building on top of the remains.</summary>
    internal static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir) && new DirectoryInfo(dir).LinkTarget is null) return;
        try { Directory.Delete(dir, recursive: true); return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (!OperatingSystem.IsWindows())
            foreach (var d in new DirectoryInfo(dir).EnumerateDirectories("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }).Prepend(new DirectoryInfo(dir)))
                try { d.UnixFileMode |= UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute; } catch { }
        Directory.Delete(dir, recursive: true);
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        string version = typeof(SolverInstaller).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0";
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"circuitRF/{version.Split('+')[0]}");
        return http;
    }

    /// <summary>Delivers on the reporting thread; <see cref="Progress{T}"/> would post to the pool and race.</summary>
    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
