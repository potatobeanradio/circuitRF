using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d.Wsl;

/// <summary>
/// One distribution, as circuitRF asks things of it: every call is <c>wsl.exe -d &lt;name&gt; [--cd dir]
/// --exec &lt;program&gt; &lt;argument&gt;…</c> — an argument list, <b>never a shell string</b> (R-em3d26-1b).
/// The two exceptions are constants: the login-shell <c>PATH</c> question
/// (<see cref="LoginShellCommandV"/>) and the process-group wrapper (<see cref="WslProcessGroup"/>).
///
/// <para>What a distribution says about itself — its home, its processors, its memory, a translated path —
/// is asked once per session and kept, so a run asks <c>wslpath</c> once however many times it is used
/// (R-em3d26-4).</para>
/// </summary>
public sealed class WslSession(IWsl wsl, string distribution)
{
    /// <summary>How long one short question may take — the first can start the distribution's VM.</summary>
    public static readonly TimeSpan ExecTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// R-em3d26-1b step 4 — the one place a shell runs, with a CONSTANT script: the candidate name arrives
    /// as <c>$1</c>, never spliced into the string. A login shell, because that is the only one that reads
    /// the profile files a user's own <c>PATH</c> additions live in.
    /// </summary>
    public const string LoginShellCommandV = "command -v -- \"$1\"";

    private readonly Dictionary<string, string> _toLinux = new(StringComparer.Ordinal);
    private string? _home;
    private int? _cores;
    private long? _memory;

    public IWsl Wsl => wsl;

    public string Distribution => distribution;

    /// <summary>The wsl.exe arguments that run <paramref name="argv"/> inside the distribution.</summary>
    public IReadOnlyList<string> Arguments(IReadOnlyList<string> argv, string? cwd = null)
    {
        var args = new List<string> { "-d", distribution };
        if (cwd is not null) args.AddRange(["--cd", cwd]);
        args.Add("--exec");
        args.AddRange(argv);
        return args;
    }

    /// <summary>Runs <paramref name="argv"/> inside the distribution and waits for it.</summary>
    public WslOutput Exec(IReadOnlyList<string> argv, string? cwd = null, TimeSpan? timeout = null)
        => wsl.Run(Arguments(argv, cwd), timeout ?? ExecTimeout);

    /// <summary>The Windows path of a Linux one — the <c>\\wsl.localhost\</c> share.</summary>
    public string ToWindows(string linuxPath) => wsl.WindowsPath(distribution, linuxPath);

    /// <summary>
    /// R-em3d26-4 — a Windows path as the distribution sees it, through <c>wslpath -u</c> inside it, once
    /// per path per session. Null with <paramref name="why"/> when wslpath refuses it.
    /// </summary>
    public string? ToLinux(string windowsPath, out string? why)
    {
        why = null;
        lock (_toLinux)
            if (_toLinux.TryGetValue(windowsPath, out var hit)) return hit;
        var run = Exec(["wslpath", "-u", windowsPath]);
        string linux = run.Text.Trim();
        if (!run.Ok || !linux.StartsWith('/'))
        {
            why = run.Failure ?? (run.Message.Length > 0 ? run.Message : $"wslpath exited {run.ExitCode}");
            return null;
        }
        lock (_toLinux) _toLinux[windowsPath] = linux;
        return linux;
    }

    /// <summary>The distribution's <c>$HOME</c> for its default user, or null when it cannot be read —
    /// which is also how "this distribution does not start" first shows.</summary>
    public string? Home(out string? why)
    {
        why = null;
        if (_home is not null) return _home;
        var run = Exec(["printenv", "HOME"]);
        string home = run.Text.Trim();
        if (!run.Ok || !home.StartsWith('/'))
        {
            why = StartFailure(run);
            return null;
        }
        return _home = home;
    }

    /// <summary>Whether the distribution starts at all: the refusal text when it does not.</summary>
    public string? CannotStart()
    {
        var run = Exec(["true"]);
        return run.Ok ? null : StartFailure(run);
    }

    private string StartFailure(WslOutput run)
    {
        if (run.Failure is { } f) return $"the Linux subsystem distribution '{distribution}' did not answer ({f})";
        var classified = WslDistributions.Classify(run.Message);
        return classified.Condition is WslCondition.VirtualizationOff or WslCondition.FeatureMissing
            ? classified.Refusal!
            : $"the Linux subsystem distribution '{distribution}' could not be started" +
              (run.Message.Length > 0 ? $" (wsl.exe said: “{run.Message.Split('\n')[0].Trim()}”)" : $" (exit {run.ExitCode})");
    }

    /// <summary>
    /// R-em3d26-2b — the SUBSYSTEM's physical cores (brief 21 applies inside): the unique core ids of its
    /// <c>/proc/cpuinfo</c>, read by the same parser a native Linux run uses; <c>nproc</c> halved when those
    /// lines are absent, as <see cref="PhysicalCores.Fallback"/> does natively.
    /// </summary>
    public PhysicalCoreReading Cores()
    {
        if (_cores is { } known) return new(known, true, $"/proc/cpuinfo in '{distribution}'");
        var info = Exec(["cat", "/proc/cpuinfo"]);
        if (info.Ok && PhysicalCores.ParseProcCpuinfo(info.Text) is { } n)
        {
            _cores = n;
            return new(n, true, $"/proc/cpuinfo in '{distribution}'");
        }
        var nproc = Exec(["nproc"]);
        int logical = int.TryParse(nproc.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int l) ? l : Environment.ProcessorCount;
        return new(PhysicalCores.Fallback(logical), false,
                   $"'{distribution}' does not list its physical cores, so half its {logical} logical processors were assumed");
    }

    /// <summary>R-em3d26-2c — the subsystem VM's memory, from <c>free -b</c> inside it; null when unread.</summary>
    public long? MemoryBytes()
    {
        if (_memory is not null) return _memory;
        var run = Exec(["free", "-b"]);
        return _memory = run.Ok ? ParseFree(run.Text) : null;
    }

    /// <summary>The total on <c>free -b</c>'s <c>Mem:</c> line.</summary>
    public static long? ParseFree(string text)
    {
        var m = Regex.Match(text, @"^\s*Mem:\s+(\d+)", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        return m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long b) ? b : null;
    }

    /// <summary><c>uname -m</c>, as a recipe architecture; null for one no recipe names.</summary>
    public Install.RecipeArchitecture? Architecture()
    {
        var run = Exec(["uname", "-m"]);
        return run.Text.Trim() switch
        {
            "x86_64"            => Install.RecipeArchitecture.X64,
            "aarch64" or "arm64" => Install.RecipeArchitecture.Arm64,
            _                   => null,
        };
    }

    /// <summary>Whether a Linux path exists (file or directory), through the share.</summary>
    public bool Exists(string linuxPath)
    {
        try
        {
            string w = ToWindows(linuxPath);
            return File.Exists(w) || Directory.Exists(w);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>Removes a Linux directory tree inside the distribution (<c>rm -rf</c>, one argument).</summary>
    public WslOutput RemoveTree(string linuxPath)
    {
        if (!linuxPath.StartsWith('/') || linuxPath.TrimEnd('/').Count(c => c == '/') < 2)
            throw new ArgumentException($"refusing to remove '{linuxPath}': not a path circuitRF made.", nameof(linuxPath));
        return Exec(["rm", "-rf", "--", linuxPath]);
    }
}
