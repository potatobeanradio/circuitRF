// HZN-8 R-hzn8-1 — finding hertzon-cli, and asking it which version it is.
//
// The walk is SolverDiscovery's, for the same reasons (src/Design/Em3d/SolverDiscovery.cs): a path named in Settings,
// then the environment, then PATH, then the places the platform's installer puts it — because a Finder-launched
// application's PATH holds only /usr/bin:/bin:/usr/sbin:/sbin. A NAMED program that does not work is reported, never
// silently replaced by another one found elsewhere. Hertzon is its own small class rather than a fifth SolverTool: it is
// a command line that solves a project file, not a program circuitRF assembles a run around.
//
// `version --json` is asked once per program per session (the answer is kept against the file's size and time). Its
// `cli_schema` is the contract this feature is written against: any other value disables Run in Hertzon with a sentence
// naming both.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CircuitRF.Design.Layout.Em3d;

namespace CircuitRF.Design.Interchange.Hzn;

/// <summary>How a hertzon-cli was found, in the order the walk tries them.</summary>
public enum HertzonHowFound { Settings, Environment, Home, Path, DefaultDirectory }

/// <summary>A hertzon-cli that started and said who it is.</summary>
/// <param name="Path">Absolute; the program is started from here.</param>
/// <param name="Version">What <c>version --json</c> reports as <c>hertzon</c> (<c>0.3.19</c>).</param>
/// <param name="Schema">What it reports as <c>cli_schema</c>.</param>
public sealed record HertzonInstallation(string Path, string Version, int Schema, HertzonHowFound HowFound, string HowFoundText)
{
    /// <summary>The label a result carries.</summary>
    public string Label => "Hertzon " + Version;
}

/// <summary>What the walk found and whether a run may use it.</summary>
/// <param name="Installation">The program, when one started and identified itself (even one whose schema is not supported).</param>
/// <param name="Refusal">Null when Run in Hertzon may proceed; otherwise the sentence saying why not.</param>
/// <param name="Rejected">Every candidate that was tried and did not work, with why.</param>
public sealed record HertzonReadiness(HertzonInstallation? Installation, string? Refusal, IReadOnlyList<string> Rejected)
{
    public bool Ready => Refusal is null && Installation is not null;
}

public sealed class HertzonDiscovery
{
    /// <summary>The <c>cli_schema</c> the run code is written against.</summary>
    public const int SupportedSchema = 1;

    /// <summary>Names the program for one process (the CLI, a test, a batch job).</summary>
    public const string EnvironmentVariable = "CIRCUITRF_HERTZON";

    /// <summary>Names the folder Hertzon is installed in (the folder holding its <c>.venv</c>), for one process.</summary>
    public const string HomeVariable = "CIRCUITRF_HERTZON_HOME";

    /// <summary>The shared instance; a test makes its own with <c>new</c>.</summary>
    public static HertzonDiscovery Shared { get; } = new();

    /// <summary>The path named in Settings. The preference lives above the firewall and arrives here (see Em3dSolverPathInstaller).</summary>
    public Func<string?>? PreferredCommand { get; set; }

    /// <summary>Reads an environment variable; settable so a test need not touch the process's.</summary>
    public Func<string, string?> ReadEnvironment { get; set; } = Environment.GetEnvironmentVariable;

    /// <summary>The bare names looked for on PATH and in <see cref="SearchDirectories"/>. Empty disables both routes.</summary>
    public IReadOnlyList<string> CandidateCommands { get; set; } = OperatingSystem.IsWindows() ? ["hertzon-cli.exe"] : ["hertzon-cli"];

    /// <summary>Where the candidates are looked for once PATH has failed.</summary>
    public IReadOnlyList<string> SearchDirectories { get; set; } = DefaultSearchDirectories();

    /// <summary>How long the version question may take before the program is called not working.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, (string Stamp, HertzonInstallation? Found, string? Why)> _probed = new(StringComparer.Ordinal);

    /// <summary>How many times a program was started to ask its version, over the life of this instance.</summary>
    public int ProbesStarted { get; private set; }

    /// <summary>Forgets every answer, so the next check asks again.</summary>
    public void Forget() { lock (_gate) _probed.Clear(); }

    internal static IReadOnlyList<string> DefaultSearchDirectories()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (local.Length > 0) list.Add(Path.Combine(local, "Programs", "Hertzon", ".venv", "Scripts"));
        }
        else
        {
            list.Add(Path.Combine(home, ".local", "share", "hertzon", ".venv", "bin"));
            list.Add(Path.Combine(home, ".local", "bin"));
            if (OperatingSystem.IsMacOS()) list.Add("/opt/homebrew/bin");
            list.Add("/usr/local/bin");
        }
        return list;
    }

    /// <summary>Where hertzon-cli lives inside an install folder (the one that holds <c>.venv</c>).</summary>
    internal IEnumerable<string> InsideHome(string folder)
    {
        string name = CandidateCommands.Count > 0 ? CandidateCommands[0] : (OperatingSystem.IsWindows() ? "hertzon-cli.exe" : "hertzon-cli");
        yield return Path.Combine(folder, ".venv", OperatingSystem.IsWindows() ? "Scripts" : "bin", name);
        yield return Path.Combine(folder, name);
        yield return Path.Combine(folder, "bin", name);
    }

    private IEnumerable<(string Path, HertzonHowFound How, string Text)> Candidates()
    {
        if (PreferredCommand?.Invoke()?.Trim() is { Length: > 0 } preferred)
        {
            foreach (string p in Resolve(preferred)) yield return (p, HertzonHowFound.Settings, "set in Settings");
            yield break;                       // a named program that does not work is not replaced
        }
        if (ReadEnvironment(EnvironmentVariable)?.Trim() is { Length: > 0 } named)
        {
            foreach (string p in Resolve(named)) yield return (p, HertzonHowFound.Environment, $"named by {EnvironmentVariable}");
            yield break;
        }
        if (ReadEnvironment(HomeVariable)?.Trim() is { Length: > 0 } home)
        {
            foreach (string p in InsideHome(home)) if (File.Exists(p)) yield return (Path.GetFullPath(p), HertzonHowFound.Home, $"in the folder {HomeVariable} names");
        }
        foreach (string name in CandidateCommands)
            foreach (string dir in (ReadEnvironment("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate;
                try { candidate = Path.Combine(dir.Trim().Trim('"'), name); } catch (ArgumentException) { continue; }
                if (File.Exists(candidate)) yield return (Path.GetFullPath(candidate), HertzonHowFound.Path, "on PATH");
            }
        foreach (string dir in SearchDirectories)
            foreach (string name in CandidateCommands)
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) yield return (Path.GetFullPath(candidate), HertzonHowFound.DefaultDirectory, $"found in {dir}");
            }
    }

    private static string FullPathOrSame(string path)
    {
        try { return Path.GetFullPath(path); } catch (Exception) { return path; }
    }

    /// <summary>A named path: the program itself, or an install folder holding it.</summary>
    private IEnumerable<string> Resolve(string named)
    {
        string full = FullPathOrSame(named);
        if (Directory.Exists(full))
        {
            bool any = false;
            foreach (string p in InsideHome(full)) if (File.Exists(p)) { any = true; yield return Path.GetFullPath(p); }
            if (!any) yield return full;       // reported as not a program
        }
        else yield return full;
    }

    /// <summary>The last answer <see cref="Check"/> gave, or null before the first. A menu reads this to say why an item is off
    /// without starting a process on the UI thread.</summary>
    public HertzonReadiness? Last { get; private set; }

    /// <summary>Walks, asks the first program that starts, and decides whether a run may use it.</summary>
    public HertzonReadiness Check() => Last = Walk();

    private HertzonReadiness Walk()
    {
        var rejected = new List<string>();
        foreach (var (path, how, text) in Candidates())
        {
            var (found, why) = Probe(path, how, text);
            if (found is null) { rejected.Add($"{path}: {why}"); continue; }
            return new HertzonReadiness(found, SchemaRefusal(found), rejected);
        }
        string reason = rejected.Count > 0
            ? "Hertzon's command line did not work: " + string.Join("; ", rejected) + "."
            : NotFoundSentence();
        return new HertzonReadiness(null, reason, rejected);
    }

    /// <summary>The sentence for a program that works but speaks another schema.</summary>
    public static string? SchemaRefusal(HertzonInstallation found)
        => found.Schema == SupportedSchema ? null
         : $"Hertzon {found.Version} speaks command-line schema {found.Schema}, and this circuitRF is written for schema {SupportedSchema}, so Run in Hertzon is off.";

    private string NotFoundSentence()
        => $"hertzon-cli was not found. Name it in Settings > Solvers, set {EnvironmentVariable}, or put it on PATH.";

    private (HertzonInstallation? Found, string? Why) Probe(string path, HertzonHowFound how, string text)
    {
        string stamp;
        try
        {
            var f = new FileInfo(path);
            if (!f.Exists) return (null, "no such file");
            stamp = $"{f.Length}:{f.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) { return (null, ex.Message); }

        lock (_gate)
        {
            if (_probed.TryGetValue(path, out var kept) && kept.Stamp == stamp)
                return (kept.Found is { } k ? k with { HowFound = how, HowFoundText = text } : null, kept.Why);
        }

        var (installation, why) = Ask(path, how, text);
        lock (_gate) _probed[path] = (stamp, installation, why);
        return (installation, why);
    }

    private (HertzonInstallation? Found, string? Why) Ask(string path, HertzonHowFound how, string text)
    {
        ProbesStarted++;
        var info = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        info.ArgumentList.Add("version");
        info.ArgumentList.Add("--json");
        try
        {
            using var p = Em3dProcessLauncher.Start(info, Em3dProcessKind.Probe);
            if (p is null) return (null, "it did not start");
            var err = p.StandardError.ReadToEndAsync();
            var outTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(ProbeTimeout))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                return (null, $"it did not answer `version --json` in {ProbeTimeout.TotalSeconds:0} s");
            }
            p.WaitForExit();
            string stdout = outTask.GetAwaiter().GetResult();
            if (p.ExitCode != 0) return (null, $"`version --json` ended with exit code {p.ExitCode}");
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (!root.TryGetProperty("hertzon", out var v) || v.ValueKind != JsonValueKind.String)
                return (null, "`version --json` did not report a Hertzon version");
            if (!root.TryGetProperty("cli_schema", out var s) || !s.TryGetInt32(out int schema))
                return (null, "`version --json` did not report a cli_schema");
            return (new HertzonInstallation(path, v.GetString()!.Trim(), schema, how, text), null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or JsonException or InvalidOperationException)
        {
            return (null, ex.Message);
        }
    }

    // ── the window ───────────────────────────────────────────────────────────────────────────

    /// <summary>The Hertzon application beside <paramref name="cli"/>, or null when it is not there. On Windows the install
    /// folder's own <c>Hertzon.exe</c> is preferred (it has no console window); elsewhere the <c>hertzon</c> script beside the CLI.</summary>
    public static string? ApplicationFor(HertzonInstallation cli)
    {
        string dir = Path.GetDirectoryName(cli.Path) ?? "";
        if (OperatingSystem.IsWindows())
        {
            string? root = Directory.GetParent(dir)?.Parent?.FullName;     // ...\.venv\Scripts -> the install folder
            if (root is not null && File.Exists(Path.Combine(root, "Hertzon.exe"))) return Path.Combine(root, "Hertzon.exe");
            string exe = Path.Combine(dir, "hertzon.exe");
            return File.Exists(exe) ? exe : null;
        }
        string script = Path.Combine(dir, "hertzon");
        return File.Exists(script) ? script : null;
    }

    /// <summary>Starts Hertzon on <paramref name="project"/> and leaves it running. Null on success, else why not.</summary>
    public static string? Open(HertzonInstallation cli, string project)
    {
        string? app = ApplicationFor(cli);
        if (app is null) return "Hertzon's application was not found beside hertzon-cli.";
        try
        {
            var info = new ProcessStartInfo(app) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(Path.GetFullPath(project));
            using var p = Process.Start(info);
            return p is null ? "Hertzon did not start." : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return "Hertzon could not be started: " + ex.Message;
        }
    }

    /// <summary>What each Settings and <c>solver list</c> row says: found or not, the version, the schema, the route.</summary>
    public string Summary(HertzonReadiness ready)
    {
        if (ready.Installation is not { } i) return ready.Refusal ?? NotFoundSentence();
        string line = string.Create(CultureInfo.InvariantCulture, $"Hertzon {i.Version}, command-line schema {i.Schema}; {i.HowFoundText}: {i.Path}");
        return ready.Refusal is null ? line : line + ". " + ready.Refusal;
    }
}
