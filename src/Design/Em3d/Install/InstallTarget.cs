using System.Diagnostics;
using System.Text;

namespace CircuitRF.Design.Em3d.Install;

/// <summary>
/// brief-em3d-26 R-em3d26-3a — WHERE an install's steps act: this machine, or a Linux subsystem
/// distribution. <see cref="SolverInstaller"/> runs one recipe the same way in both — the same prerequisite
/// check, the same <c>.partial</c> publish, the same install record, the same known-failures matcher —
/// and every file-system operation and every process it starts goes through here. Paths handed in and out
/// are the TARGET's: native paths natively, Linux paths inside a distribution.
/// </summary>
internal abstract class InstallTarget
{
    /// <summary>The Linux subsystem distribution the install acts in, or null for this machine.</summary>
    public virtual string? Distribution => null;

    /// <summary>The separator between <c>PATH</c> entries on the target.</summary>
    public abstract char PathSeparator { get; }

    public abstract string Combine(string first, string second);
    public abstract string? Parent(string path);
    public abstract bool IsRooted(string path);
    public abstract bool FileExists(string path);
    public abstract bool Exists(string path);

    /// <summary>Entries of <paramref name="directory"/> matching <paramref name="pattern"/> (may hold <c>*</c>), in name order.</summary>
    public abstract IReadOnlyList<string> Entries(string directory, string pattern);

    /// <summary><paramref name="name"/> as a program in <paramref name="directory"/>, or null.</summary>
    public abstract string? ProgramIn(string directory, string name);

    public abstract void CreateDirectory(string path);
    public abstract void WriteText(string path, string text);
    public abstract void Symlink(string link, string target);
    public abstract void DeleteTree(string path);
    public abstract void Move(string from, string to);
    public abstract long MeasureBytes(string path);
    public abstract InstallRecord? ReadRecord(string path);
    public abstract void WriteRecord(InstallRecord record, string path);

    /// <summary>The environment a step inherits before the recipe's changes (the native one is the
    /// installer's own seam, <see cref="SolverInstaller.InheritedEnvironment"/>).</summary>
    public abstract IReadOnlyDictionary<string, string> Environment(SolverInstaller installer);

    /// <summary>The <c>ID</c> and <c>ID_LIKE</c> words of the target's <c>/etc/os-release</c>.</summary>
    public abstract IReadOnlyList<string> OsRelease();

    /// <summary>A step's process: <paramref name="command"/> with <paramref name="arguments"/> in
    /// <paramref name="cwd"/>, with exactly <paramref name="environment"/>. <paramref name="stop"/> is what
    /// cancelling it must do beyond killing the returned process's tree.</summary>
    public abstract ProcessStartInfo StartInfo(string command, IReadOnlyList<string> arguments, string cwd,
                                               IReadOnlyDictionary<string, string> environment, out Action? stop);

    /// <summary>A prerequisite's own short question.</summary>
    public abstract (int ExitCode, string Output) Quick(string program, IReadOnlyList<string> arguments);

    /// <summary>Compile jobs for a source build on the target: physical cores, at most one per 2 GiB.</summary>
    public abstract int Jobs();

    /// <summary>Physical cores on the target.</summary>
    public abstract int Cores();

    /// <summary>Asks discovery what the installed program at <paramref name="program"/> is.</summary>
    public abstract bool TryProbe(SolverDiscovery discovery, string program, out SolverInstallation? installation, out string? why);

    /// <summary>This machine.</summary>
    public static InstallTarget Native { get; } = new NativeInstallTarget();
}

/// <summary>This machine — brief-em3d-24's behaviour, moved here unchanged.</summary>
internal sealed class NativeInstallTarget : InstallTarget
{
    public override char PathSeparator => Path.PathSeparator;
    public override string Combine(string first, string second) => Path.Combine(first, second);
    public override string? Parent(string path) => Path.GetDirectoryName(path);
    public override bool IsRooted(string path) => Path.IsPathRooted(path);
    public override bool FileExists(string path) => File.Exists(path);
    public override bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public override IReadOnlyList<string> Entries(string directory, string pattern)
    {
        try { return Directory.Exists(directory) ? Directory.EnumerateFileSystemEntries(directory, pattern).Order(StringComparer.Ordinal).ToList() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    public override string? ProgramIn(string directory, string name)
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

    public override void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public override void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    public override void Symlink(string link, string target)
    {
        if (File.Exists(link) || Directory.Exists(link) || new FileInfo(link).LinkTarget is not null) File.Delete(link);
        if (Directory.Exists(target)) Directory.CreateSymbolicLink(link, target);
        else File.CreateSymbolicLink(link, target);
    }

    public override void DeleteTree(string path) => SolverInstaller.DeleteTree(path);
    public override void Move(string from, string to) => Directory.Move(from, to);
    public override long MeasureBytes(string path) => SolverInstaller.MeasureBytes(path);
    public override InstallRecord? ReadRecord(string path) => InstallRecord.TryRead(path);
    public override void WriteRecord(InstallRecord record, string path) => record.Write(path);
    public override IReadOnlyDictionary<string, string> Environment(SolverInstaller installer) => installer.InheritedEnvironment();

    public override IReadOnlyList<string> OsRelease()
    {
        if (!OperatingSystem.IsLinux()) return [];
        try { return SolverInstaller.ParseOsRelease(File.ReadAllText("/etc/os-release")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    public override ProcessStartInfo StartInfo(string command, IReadOnlyList<string> arguments, string cwd,
                                               IReadOnlyDictionary<string, string> environment, out Action? stop)
    {
        stop = null;
        var psi = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = cwd,
        };
        foreach (string a in arguments) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var (k, v) in environment) psi.Environment[k] = v;
        return psi;
    }

    public override (int ExitCode, string Output) Quick(string program, IReadOnlyList<string> arguments)
        => SolverInstaller.RunQuick(program, arguments);

    public override int Jobs() => SolverInstaller.BuildJobs();
    public override int Cores() => Math.Max(1, Engine.Em3d.PhysicalCores.Count);

    public override bool TryProbe(SolverDiscovery discovery, string program, out SolverInstallation? installation, out string? why)
        => discovery.TryProbe(program, SolverHowFound.Installed, "installed by circuitRF", out installation, out why);
}
