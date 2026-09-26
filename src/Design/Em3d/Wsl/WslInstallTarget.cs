using System.Diagnostics;
using System.Globalization;
using System.Text;
using CircuitRF.Design.Em3d.Install;

namespace CircuitRF.Design.Em3d.Wsl;

/// <summary>
/// brief-em3d-26 R-em3d26-3a — brief 24's Linux recipe, acting INSIDE a distribution. Every operation is a
/// program with an argument list through <c>wsl.exe --exec</c> (<c>mkdir</c>, <c>ln</c>, <c>mv</c>, <c>rm</c>,
/// <c>du</c>, <c>test</c>, <c>find</c>), except file contents, which cross through the <c>\\wsl.localhost\</c>
/// share. Each step runs as the leader of its own process group, so cancelling an install stops Spack and
/// every compiler it started, not just <c>wsl.exe</c>.
/// </summary>
internal sealed class WslInstallTarget(WslSession session) : InstallTarget
{
    private IReadOnlyDictionary<string, string>? _environment;

    public override string? Distribution => session.Distribution;
    public override char PathSeparator => ':';
    public override string Combine(string first, string second) => WslPaths.Combine(first, second);
    public override string? Parent(string path) => WslPaths.Parent(path);
    public override bool IsRooted(string path) => path.StartsWith('/');

    public override bool FileExists(string path) => IsRooted(path) && session.Exec(["test", "-f", path]).Ok;
    public override bool Exists(string path) => IsRooted(path) && session.Exec(["test", "-e", path]).Ok;

    public override IReadOnlyList<string> Entries(string directory, string pattern)
    {
        if (!IsRooted(directory)) return [];
        var run = session.Exec(["find", directory, "-mindepth", "1", "-maxdepth", "1", "-name", pattern]);
        return run.Ok ? run.Text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('/')).Order(StringComparer.Ordinal).ToList() : [];
    }

    public override string? ProgramIn(string directory, string name)
    {
        if (!IsRooted(directory.Trim())) return null;
        string candidate = WslPaths.Combine(directory.Trim(), name);
        return session.Exec(["test", "-x", candidate]).Ok && !session.Exec(["test", "-d", candidate]).Ok ? candidate : null;
    }

    public override void CreateDirectory(string path) => Check(session.Exec(["mkdir", "-p", "--", path]), $"mkdir {path}");

    public override void WriteText(string path, string text)
    {
        CreateDirectory(WslPaths.Parent(path));
        File.WriteAllText(session.ToWindows(path), text, new UTF8Encoding(false));
    }

    public override void Symlink(string link, string target) => Check(session.Exec(["ln", "-sfn", "--", target, link]), $"ln -sfn {target} {link}");

    public override void DeleteTree(string path)
    {
        if (!Exists(path)) return;
        Check(session.RemoveTree(path), $"rm -rf {path}");
    }

    public override void Move(string from, string to) => Check(session.Exec(["mv", "-T", "--", from, to]), $"mv {from} {to}");

    public override long MeasureBytes(string path)
    {
        var run = session.Exec(["du", "-sb", "--", path]);
        string first = run.Text.Split('\t', ' ', '\n').FirstOrDefault() ?? "";
        return run.Ok && long.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out long b) ? b : 0;
    }

    public override InstallRecord? ReadRecord(string path) => InstallRecord.TryRead(session.ToWindows(path));

    public override void WriteRecord(InstallRecord record, string path) => record.Write(session.ToWindows(path));

    /// <summary>The distribution's own environment for its default user (<c>printenv -0</c>), read once —
    /// the Linux-side equivalent of what a native step inherits.</summary>
    public override IReadOnlyDictionary<string, string> Environment(SolverInstaller installer)
    {
        if (_environment is not null) return _environment;
        var run = session.Exec(["printenv", "-0"]);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (run.Ok)
            foreach (string entry in run.Text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                if (entry.IndexOf('=') is > 0 and var eq) map[entry[..eq]] = entry[(eq + 1)..];
        return _environment = map;
    }

    public override IReadOnlyList<string> OsRelease()
    {
        var run = session.Exec(["cat", "/etc/os-release"]);
        return run.Ok ? SolverInstaller.ParseOsRelease(run.Text) : [];
    }

    /// <summary>
    /// The step as <c>setsid -w sh -c '&lt;wrapper&gt;' &lt;pid file&gt; env -i K=V… &lt;command&gt; &lt;args&gt;</c>:
    /// <c>env -i</c> gives it EXACTLY the environment the installer computed (inherited, cleared, the recipe's),
    /// as <see cref="ProcessStartInfo.Environment"/> does natively.
    /// </summary>
    public override ProcessStartInfo StartInfo(string command, IReadOnlyList<string> arguments, string cwd,
                                               IReadOnlyDictionary<string, string> environment, out Action? stop)
    {
        string pidFile = "/tmp/circuitrf-install-" + Guid.NewGuid().ToString("N")[..12] + ".pid";
        var argv = new List<string> { "env", "-i" };
        argv.AddRange(environment.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        argv.Add(command);
        argv.AddRange(arguments);
        stop = () => WslProcessGroup.Stop(session, pidFile);
        return session.Wsl.StartInfo(session.Arguments(WslProcessGroup.Wrap(pidFile, argv), cwd));
    }

    public override (int ExitCode, string Output) Quick(string program, IReadOnlyList<string> arguments)
    {
        var run = session.Exec([program, .. arguments], timeout: TimeSpan.FromSeconds(30) + WslSession.ExecTimeout);
        return (run.Failure is null ? run.ExitCode : -1,
                run.Failure ?? Encoding.UTF8.GetString(run.Stdout) + Encoding.UTF8.GetString(run.Stderr));
    }

    /// <summary>Physical cores, at most one per 2 GiB of the SUBSYSTEM's memory (<see cref="SolverInstaller.BuildJobs"/>'s
    /// rule, applied to the VM the compilers actually run in).</summary>
    public override int Jobs()
    {
        int cores = Cores();
        long bytes = session.MemoryBytes() ?? 0;
        int byMemory = bytes > 0 ? (int)Math.Max(1, bytes / (2L * 1024 * 1024 * 1024)) : cores;
        return Math.Min(cores, byMemory);
    }

    public override int Cores() => Math.Max(1, session.Cores().Count);

    public override bool TryProbe(SolverDiscovery discovery, string program, out SolverInstallation? installation, out string? why)
        => discovery.TryProbeInSubsystem(session, program, SolverHowFound.Installed,
                                         $"installed by circuitRF in the Linux subsystem distribution '{session.Distribution}'",
                                         out installation, out why);

    /// <summary>A failed operation becomes an <see cref="IOException"/> the installer reports as the step's
    /// failure: the command, then wsl.exe's or the program's own words, verbatim (R-em3d24-3a).</summary>
    private static void Check(WslOutput run, string what)
    {
        if (!run.Ok) throw new IOException(what + ": " + (run.Failure ?? run.Message));
    }
}
