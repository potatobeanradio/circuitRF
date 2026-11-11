using System.Diagnostics;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.PCells.Wire;

namespace CircuitRF.Design.Plugins;

/// <summary>The Python a plugin runs on, once its environment is prepared.</summary>
/// <param name="Kind"><see cref="PluginActiveRecord.Ready"/> (no requirements: the shared Python itself),
/// <see cref="PluginActiveRecord.Shared"/> (the one plugin environment) or <see cref="PluginActiveRecord.Own"/> (its own,
/// because its requirements conflict with another plugin's). Null with a <paramref name="Refusal"/>.</param>
public sealed record PluginEnvironmentOutcome(string? Kind, string? Refusal = null);

/// <summary>
/// Where a Python plugin's packages live (brief-plg-6 §3, <c>plugins.md</c> §9.3). <b>There is one Python</b> — the
/// interpreter <see cref="PythonInterpreterDiscovery"/> resolves, the one PDK kits run on — and nothing here installs
/// another. A plugin with no <c>requirements.txt</c> runs on it directly. A plugin with requirements installs them into
/// ONE shared plugin environment, <c>&lt;plugins&gt;/.python</c>, so two plugins that need the same package share one
/// copy; only when its requirements cannot be resolved together with every plugin already there does it get an
/// environment of its own (<c>&lt;id&gt;/&lt;version&gt;/.venv</c>), and the log says why. PDK kits keep their own
/// environments: a plugin's install must never be able to change a kit's packages.
///
/// <para><b>The shared environment is never changed on a guess.</b> The new plugin's requirements are resolved together
/// with every sharing plugin's by a <c>pip install --dry-run</c> first; only if that succeeds are they installed (again
/// together, so nothing another plugin needs is moved out of its range), and <c>pip check</c> confirms it. Installing is
/// the only step that reaches the network. A requirement that fails even in an environment of its own is a refusal quoting
/// pip's last lines and naming the requirement; there is no retry with other flags.</para>
/// </summary>
public static partial class PluginPythonEnvironment
{
    public const string RequirementsFile = "requirements.txt";
    /// <summary>The shared plugin environment's folder under the plugins root. A dot folder, so the catalogue skips it.</summary>
    public const string SharedFolder = ".python";

    /// <summary>The shared plugin environment of the plugins root above <paramref name="versionDir"/>.</summary>
    public static string SharedEnvironment(string pluginsRoot) => Path.Combine(pluginsRoot, SharedFolder);

    /// <summary>A plugin's OWN environment's interpreter, whether or not it exists.</summary>
    public static string Interpreter(string versionDir) => EnvInterpreter(Path.Combine(versionDir, PluginInstaller.VenvFolder));

    /// <summary>An environment folder's interpreter.</summary>
    public static string EnvInterpreter(string envDir)
        => OperatingSystem.IsWindows() ? Path.Combine(envDir, "Scripts", "python.exe") : Path.Combine(envDir, "bin", "python");

    /// <summary>The interpreter a prepared installed plugin runs with: its own environment's, the shared one's, or null for
    /// the shared Python itself (found as PDK kits find it).</summary>
    public static string? InterpreterFor(string versionDir, string? kind)
        => kind switch
        {
            PluginActiveRecord.Own    => Interpreter(versionDir),
            PluginActiveRecord.Shared => EnvInterpreter(SharedEnvironment(PluginsRootOf(versionDir))),
            _                         => null,
        };

    /// <summary>Whether the environment a record names is still there — a deleted shared environment is remade on allow.</summary>
    public static bool IsPresent(string versionDir, string? kind)
        => InterpreterFor(versionDir, kind) is not { } interpreter || File.Exists(interpreter);

    /// <summary>Whether preparing will reach the network: there are requirements to install.</summary>
    public static bool NeedsNetwork(string versionDir) => File.Exists(Path.Combine(versionDir, RequirementsFile));

    private static string PluginsRootOf(string versionDir)
        => Path.GetDirectoryName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(versionDir))!)!;

    /// <summary>Prepares <paramref name="versionDir"/> to run — see the class. Every line of output goes to <paramref name="log"/>.</summary>
    /// <param name="pluginsRoot">The plugins root whose shared environment it may join.</param>
    public static async Task<PluginEnvironmentOutcome> PrepareAsync(string versionDir, string pluginsRoot, PluginManifest manifest,
        PythonInterpreter? interpreter, Action<string> log, CancellationToken ct)
    {
        var how = manifest.ResolveRun().Entry;
        if (how is not { IsPython: true }) return new(null);

        if (interpreter is null)
        {
            interpreter = PythonInterpreterDiscovery.Find(null, null, out var rejected);
            if (interpreter is null)
                return new(null, $"{manifest.Name} needs Python, and none was found: {PythonInterpreterDiscovery.DescribeFailure(rejected)}");
        }
        if (how.RequiresPython is { } spec && !Satisfies(interpreter.Version, spec))
            return new(null, $"{manifest.Name} needs Python {spec}, and the interpreter found is {interpreter.Version} ({interpreter.Command}).");

        string requirements = Path.Combine(versionDir, RequirementsFile);
        if (!File.Exists(requirements))
        {
            log($"{manifest.Name} has no requirements, so it runs on Python {interpreter.Version} ({interpreter.Command}) as it is.");
            return new(PluginActiveRecord.Ready);
        }

        if (await JoinSharedAsync(pluginsRoot, manifest, requirements, interpreter, log, ct) is { } whyNot)
        {
            log($"{manifest.Name}'s requirements cannot share the plugin environment ({whyNot}), so it gets an environment of its own.");
            return await OwnAsync(versionDir, manifest, requirements, interpreter, log, ct);
        }
        return new(PluginActiveRecord.Shared);
    }

    /// <summary>Installs into the shared environment, or returns why this plugin cannot share it — with the environment
    /// as it was whenever the answer is no.</summary>
    private static async Task<string?> JoinSharedAsync(string pluginsRoot, PluginManifest manifest, string requirements,
        PythonInterpreter interpreter, Action<string> log, CancellationToken ct)
    {
        string env = SharedEnvironment(pluginsRoot);
        using var held = await LockAsync(pluginsRoot, ct);

        string python = EnvInterpreter(env);
        if (!File.Exists(python))
        {
            log($"Creating the shared plugin environment in {env} with Python {interpreter.Version}.");
            var made = await RunAsync(interpreter.Command, [.. interpreter.Arguments, "-m", "venv", env], pluginsRoot, log, ct);
            if (made.Exit != 0) return $"it could not be made: {Tail(made.Lines, 3)}";
        }
        if (manifest.ResolveRun().Entry?.RequiresPython is { } spec && await VersionOf(python, ct) is { } shared && !Satisfies(shared, spec))
            return $"it runs Python {shared}, and {manifest.Name} needs {spec}";

        // Every plugin already sharing it constrains this install: resolved together, nothing they need moves.
        var together = new List<string> { "-r", requirements };
        foreach (string other in SharedRequirements(pluginsRoot, manifest.Id)) together.AddRange(["-r", other]);

        log($"Checking {RequirementsFile} against the {together.Count / 2 - 1} other plugin(s) in the shared environment — this reaches the network.");
        var dry = await RunAsync(python, ["-m", "pip", "install", "--dry-run", "--no-input", "--disable-pip-version-check", .. together], pluginsRoot, log, ct);
        if (dry.Exit != 0) return Tail(dry.Lines, 2);

        log($"Installing {RequirementsFile} into the shared plugin environment.");
        var pip = await RunAsync(python, ["-m", "pip", "install", "--no-input", "--disable-pip-version-check", .. together], pluginsRoot, log, ct);
        if (pip.Exit != 0) return Tail(pip.Lines, 2);
        var check = await RunAsync(python, ["-m", "pip", "check"], pluginsRoot, log, ct);
        return check.Exit == 0 ? null : $"pip check: {Tail(check.Lines, 2)}";
    }

    private static async Task<PluginEnvironmentOutcome> OwnAsync(string versionDir, PluginManifest manifest, string requirements,
        PythonInterpreter interpreter, Action<string> log, CancellationToken ct)
    {
        string venv = Path.Combine(versionDir, PluginInstaller.VenvFolder);
        log($"Creating a Python {interpreter.Version} environment in {venv}.");
        var made = await RunAsync(interpreter.Command, [.. interpreter.Arguments, "-m", "venv", venv], versionDir, log, ct);
        if (made.Exit != 0)
            return new(null, $"The Python environment for {manifest.Name} could not be made: {Tail(made.Lines, 3)}");

        log($"Installing {RequirementsFile} — this reaches the network.");
        var pip = await RunAsync(Interpreter(versionDir), ["-m", "pip", "install", "--no-input", "--disable-pip-version-check", "-r", requirements],
            versionDir, log, ct);
        if (pip.Exit == 0) return new(PluginActiveRecord.Own);
        string? failed = FailedRequirement(pip.Lines);
        return new(null, $"{manifest.Name}'s requirement {(failed is null ? "" : $"'{failed}' ")}could not be installed. pip said: {Tail(pip.Lines, 5)}");
    }

    /// <summary>The <c>requirements.txt</c> of every installed plugin, other than <paramref name="exceptId"/>, whose active
    /// version runs in the shared environment.</summary>
    public static IReadOnlyList<string> SharedRequirements(string pluginsRoot, string exceptId)
    {
        var files = new List<string>();
        try
        {
            foreach (string idDir in Directory.GetDirectories(pluginsRoot))
            {
                if (Path.GetFileName(idDir).StartsWith('.') || Path.GetFileName(idDir) == exceptId) continue;
                if (PluginInstaller.ReadActive(idDir) is { Environment: PluginActiveRecord.Shared } r
                    && Path.Combine(idDir, r.Active, RequirementsFile) is var f && File.Exists(f)) files.Add(f);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return files;
    }

    /// <summary>One change to the shared environment at a time — another window, the command line, a second install.</summary>
    private static async Task<IDisposable> LockAsync(string pluginsRoot, CancellationToken ct)
    {
        Directory.CreateDirectory(pluginsRoot);
        string file = Path.Combine(pluginsRoot, SharedFolder + ".lock");
        while (true)
        {
            try { return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) { await Task.Delay(250, ct); }
        }
    }

    private static async Task<string?> VersionOf(string python, CancellationToken ct)
    {
        var r = await RunAsync(python, ["-c", "import platform; print(platform.python_version())"], Path.GetDirectoryName(python)!, _ => { }, ct);
        return r.Exit == 0 ? r.Lines.LastOrDefault(l => l.Length > 0)?.Trim() : null;
    }

    /// <summary>The requirement pip names in its failure, or null.</summary>
    public static string? FailedRequirement(IReadOnlyList<string> lines)
    {
        foreach (string line in lines.Reverse())
        {
            var m = FailurePattern().Match(line);
            if (m.Success) return m.Groups["req"].Value.Trim().TrimEnd('.', ',');
        }
        return null;
    }

    [GeneratedRegex(@"(?:No matching distribution found for|Failed to build|Could not build wheels for|satisfies the requirement|Failed building wheel for)\s+(?<req>[A-Za-z0-9_.\-\[\]<>=!~ ,]+?)(?:\s*\(|$|\s+which)")]
    private static partial Regex FailurePattern();

    /// <summary>Whether <paramref name="version"/> meets a specifier like <c>&gt;=3.10</c> or <c>&gt;=3.9,&lt;3.13</c>.
    /// An operator it does not know is not held against the interpreter.</summary>
    public static bool Satisfies(string version, string spec)
    {
        foreach (string raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Regex.Match(raw, @"^(>=|<=|==|!=|~=|>|<)?\s*([0-9][0-9.]*)");
            if (!m.Success) continue;
            string op = m.Groups[1].Value.Length == 0 ? "==" : m.Groups[1].Value, want = m.Groups[2].Value.TrimEnd('.');
            int c = PluginVersions.Compare(Truncate(version, want), want);
            bool ok = op switch
            {
                ">=" or "~=" => c >= 0, ">" => c > 0, "<=" => c <= 0, "<" => c < 0, "!=" => c != 0, _ => c == 0,
            };
            if (!ok) return false;
        }
        return true;

        // "3.12.1" against "3.12" compares as "3.12": a specifier names as many parts as it means.
        static string Truncate(string v, string like)
        {
            var parts = v.Split('.');
            int n = like.Split('.').Length;
            return string.Join('.', parts.Take(Math.Max(n, 1)));
        }
    }

    private static string Tail(IReadOnlyList<string> lines, int n)
    {
        var last = lines.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(n).Select(l => l.Trim()).ToList();
        return last.Count == 0 ? "it said nothing." : string.Join(" / ", last);
    }

    private static async Task<(int Exit, IReadOnlyList<string> Lines)> RunAsync(string command, IReadOnlyList<string> args, string cwd,
        Action<string> log, CancellationToken ct)
    {
        var lines = new List<string>();
        var info = new ProcessStartInfo(command)
        {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (string a in args) info.ArgumentList.Add(a);
        // Anything that would ask is a refusal: pip never prompts, git (a VCS requirement) never asks for a credential.
        info.Environment["PIP_NO_INPUT"] = "1";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["PYTHONIOENCODING"] = "utf-8";

        using var p = new Process { StartInfo = info };
        void Line(string? l)
        {
            if (l is null) return;
            lock (lines) lines.Add(l);
            try { log(l); } catch { }
        }
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        try { p.Start(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return (-1, [$"{command} would not start: {e.Message}"]); }
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        p.WaitForExit();
        lock (lines) return (p.ExitCode, [.. lines]);
    }
}
