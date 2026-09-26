// brief-em3d-26 R-em3d26-1b/1c — discovery INSIDE a Linux subsystem distribution.
//
// The same walk §7.1 gives a Linux machine, run inside each WSL 2 distribution through wsl.exe, in the
// brief's order: circuitRF's own Linux home, Spack trees, conda, then PATH from a login shell. Files are
// read through the \\wsl.localhost\ share; programs are started with `wsl.exe -d <name> --exec`, an
// argument list. The version and capability probes are the native ones, asked inside.

using System.Text;
using CircuitRF.Design.Em3d.Install;
using CircuitRF.Design.Em3d.Wsl;

namespace CircuitRF.Design.Em3d;

public sealed partial class SolverDiscovery
{
    /// <summary>circuitRF's own install root inside a distribution, under its home (brief-em3d-26 §3).</summary>
    public const string SubsystemSolverRoot = ".circuitrf/solvers";

    /// <summary>
    /// The subsystem half of <see cref="Find"/>. <paramref name="named"/> is the distribution the location
    /// setting names, or null for every WSL 2 distribution in the order <c>wsl -l</c> lists them.
    /// <b>The first distribution with a VALIDATED program wins</b>; with none validated, the first found is
    /// returned, so its refusal can say which version it is.
    /// </summary>
    private SolverInstallation? FindInSubsystem(string? named, List<string> notes)
    {
        var wsl = Subsystem!;
        var state = WslDistributions.Read(wsl);
        if (!state.Ready)
        {
            notes.Add("the Linux subsystem: " + state.Refusal);
            return null;
        }

        var candidates = named is null
            ? state.Distributions
            : state.Distributions.Where(d => string.Equals(d.Name, named, StringComparison.OrdinalIgnoreCase)).ToList();
        if (named is not null && candidates.Count == 0)
        {
            notes.Add($"the Linux subsystem distribution '{named}' chosen in Settings ▸ 3D EM is not installed " +
                      $"(installed: {string.Join(", ", state.Distributions.Select(d => d.Name))})");
            return null;
        }

        SolverInstallation? first = null;
        foreach (var d in candidates)
        {
            if (d.Version < 2)
            {
                notes.Add(WslDistributions.Wsl1Refusal(d.Name));
                continue;
            }
            var found = FindInDistribution(new WslSession(wsl, d.Name), notes);
            if (found is { Validated: true }) return found;
            first ??= found;
        }
        return first;
    }

    /// <summary>One distribution's walk, in the brief's order.</summary>
    private SolverInstallation? FindInDistribution(WslSession session, List<string> notes)
    {
        string where = $"in the Linux subsystem distribution '{session.Distribution}'";
        if (session.Home(out string? why) is not { } home)
        {
            notes.Add($"{where}: {why}");
            return null;
        }
        var names = BareCandidates();
        if (names.Count == 0) return null;

        SolverInstallation? Try(string candidate, SolverHowFound how, string text)
        {
            if (TryProbeInSubsystem(session, candidate, how, text, out var chosen, out string? reason)) return chosen;
            notes.Add($"'{candidate}' {where}: {reason}");
            return null;
        }

        // 1. circuitRF's own Linux home — a home counts only once its record exists, as natively.
        foreach (var record in SubsystemRecords(session, home, Tool))
            if (names.Contains(WslPaths.Name(record.Program), StringComparer.Ordinal)
                && Try(record.Program, SolverHowFound.Installed, $"installed by circuitRF {where} at {record.Home}") is { } installed)
                return installed;

        // 2. Spack trees: the recipe's ~/opt/spack, a clone's ~/spack/opt/spack, the system /opt/spack.
        if (SpackPackage is { Length: > 0 } package)
        {
            string[] roots = [WslPaths.Combine(home, "spack", "opt", "spack"), WslPaths.Combine(home, "opt", "spack"), "/opt/spack"];
            foreach (var install in SpackInstalls.Find(package, roots, session.ToWindows))
                foreach (string command in names)
                {
                    string candidate = WslPaths.Combine(install.Prefix, "bin", command);
                    if (session.Exists(candidate)
                        && Try(candidate, SolverHowFound.Spack, $"found in a Spack installation {where} at {candidate}") is { } spack)
                        return spack;
                }
        }

        // 3. conda environments, under the installers' default folders. Nothing is activated.
        foreach (string conda in new[] { "miniforge3", "mambaforge", "miniconda3", "anaconda3" }
                     .Select(c => WslPaths.Combine(home, c)).Append("/opt/conda"))
            foreach (string env in SubsystemDirectories(session, WslPaths.Combine(conda, "envs")))
                foreach (string command in names)
                {
                    string candidate = WslPaths.Combine(env, "bin", command);
                    if (session.Exists(candidate)
                        && Try(candidate, SolverHowFound.Conda, $"found in a conda environment {where} at {candidate}") is { } fromConda)
                        return fromConda;
                }

        // 4. PATH, as a login shell sees it — the one place a shell runs, with a constant script.
        foreach (string command in names)
        {
            var run = session.Exec(["bash", "-lc", WslSession.LoginShellCommandV, "bash", command]);
            string path = run.Text.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
            if (run.Ok && path.StartsWith('/')
                && Try(path, SolverHowFound.Path, $"found on PATH {where}") is { } onPath)
                return onPath;
        }
        return null;
    }

    /// <summary>
    /// The version probe, asked inside the distribution (R-em3d26-1c): the same arguments and the same
    /// parser as <see cref="TryProbe"/>; the installation carries the distribution, and its path is Linux.
    /// </summary>
    internal bool TryProbeInSubsystem(WslSession session, string linuxPath, SolverHowFound howFound, string howFoundText,
                                      out SolverInstallation? installation, out string? why)
    {
        installation = null;
        why = null;
        if (!linuxPath.StartsWith('/')) { why = "that is not an absolute Linux path"; return false; }
        if (!session.Exists(linuxPath)) { why = "there is no file there"; return false; }

        Interlocked.Increment(ref _versionProbes);
        var run = session.Exec([linuxPath, .. _versionArguments], timeout: VersionTimeout + WslSession.ExecTimeout);
        if (run.Failure is { } failure) { why = failure; return false; }
        string output = Encoding.UTF8.GetString(run.Stdout) + "\n" + Encoding.UTF8.GetString(run.Stderr);
        if (ParseVersion(Tool, output) is not { } parsed)
        {
            string first = FirstLine(output);
            why = first.Length == 0 ? "it started but identified itself with nothing" : $"it identified itself as '{first}', which is not {Name}";
            return false;
        }
        string? release = ValidatedVersions
            .FirstOrDefault(v => v.Identity == parsed.Version && (v.Companion is null || v.Companion == parsed.Companion))
            ?.Release;
        installation = new SolverInstallation(Tool, linuxPath, parsed.Version, parsed.Companion, parsed.Banner,
                                              howFound, howFoundText, release, session.Distribution);
        return true;
    }

    /// <summary>The capability probe, inside the distribution: staged in <c>~/.circuitrf/probes/</c> through
    /// the share, run there with <c>--cd</c>, removed afterwards.</summary>
    private (SolverCapabilityVerdict Verdict, bool Definitive) RunPalaceProbeInSubsystem(WslSession session, string palace,
                                                                                        SolverCapability capability)
    {
        if (session.Home(out string? why) is not { } home)
            return (new SolverCapabilityVerdict(capability, false, $"the probe could not be staged ({why}).", false), false);
        string dir = WslPaths.Combine(home, ".circuitrf", "probes", Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var mkdir = session.Exec(["mkdir", "-p", dir]);
            if (!mkdir.Ok)
                return (new SolverCapabilityVerdict(capability, false, $"the probe could not be staged ({mkdir.Failure ?? mkdir.Message}).", false), false);
            return PalaceProbe(capability,
                write: (name, text) => File.WriteAllText(session.ToWindows(WslPaths.Combine(dir, name)), text),
                run: args =>
                {
                    var r = session.Exec([palace, .. args], cwd: dir, timeout: CapabilityTimeout + WslSession.ExecTimeout);
                    return new ProbeRun(r.ExitCode, Encoding.UTF8.GetString(r.Stdout) + "\n" + Encoding.UTF8.GetString(r.Stderr), r.Failure);
                },
                exists: relative => session.Exists(WslPaths.Combine(dir, relative)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (new SolverCapabilityVerdict(capability, false, $"the probe could not be staged ({e.Message}).", false), false);
        }
        finally
        {
            try { session.RemoveTree(dir); } catch (ArgumentException) { }
        }
    }

    /// <summary>The capability cache's stamp for a program inside a distribution: the share's view of the
    /// file and of every <c>palace-*.bin</c> beside it, as <see cref="Stamp"/> reads a native one.</summary>
    private string? SubsystemStamp(WslSession session, string linuxPath)
    {
        try { return Stamp(session.ToWindows(linuxPath)); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>The install records of circuitRF's own homes inside a distribution (published only: a
    /// <c>.partial</c> or <c>.removing</c> directory is never read), newest first.</summary>
    internal static IReadOnlyList<InstallRecord> SubsystemRecords(WslSession session, string home, SolverTool tool)
    {
        string toolDir = WslPaths.Combine(home, SubsystemSolverRoot, SolverHomes.ToolId(tool));
        var found = new List<InstallRecord>();
        foreach (string dir in SubsystemDirectories(session, toolDir))
        {
            string name = WslPaths.Name(dir);
            if (name.EndsWith(SolverHomes.PartialSuffix, StringComparison.Ordinal) || name.EndsWith(SolverHomes.RemovingSuffix, StringComparison.Ordinal))
                continue;
            if (InstallRecord.TryRead(session.ToWindows(WslPaths.Combine(dir, SolverHomes.RecordFile))) is { } record && record.Tool == tool)
                found.Add(record);
        }
        return found.OrderByDescending(r => r.InstalledAt).ToList();
    }

    /// <summary>A Linux directory's subdirectories, as Linux paths in name order; none when it is absent.</summary>
    private static IEnumerable<string> SubsystemDirectories(WslSession session, string linuxDir)
    {
        try
        {
            string local = session.ToWindows(linuxDir);
            if (!Directory.Exists(local)) return [];
            return Directory.EnumerateDirectories(local).Select(Path.GetFileName).OfType<string>()
                            .Order(StringComparer.Ordinal).Select(n => WslPaths.Combine(linuxDir, n)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }
}
