using CircuitRF.Design.Em3d.Install;

namespace CircuitRF.Design.Em3d.Wsl;

/// <summary>
/// brief-em3d-26 R-em3d26-3a — the Windows-side MIRROR of an install record whose home is inside a Linux
/// subsystem distribution: <c>&lt;state&gt;/solvers/wsl/&lt;distro&gt;/&lt;tool&gt;/&lt;version&gt;/install.json</c>.
/// Settings and the uninstaller read it without starting the subsystem. It is never a native discovery
/// location — native discovery reads <c>&lt;root&gt;/&lt;tool&gt;/</c>, and this is under <c>&lt;root&gt;/wsl/</c> — and
/// the install's lock and logs live beside it, on this machine's side.
/// </summary>
public static class WslSolverHomes
{
    /// <summary>The directory under a solver root that holds every distribution's mirrors.</summary>
    public const string MirrorDirectoryName = "wsl";

    /// <summary>One distribution's mirror root — itself laid out as a solver root.</summary>
    public static string MirrorRoot(string localRoot, string distribution) => Path.Combine(localRoot, MirrorDirectoryName, distribution);

    /// <summary>Where <paramref name="record"/>'s mirror lives under <paramref name="localRoot"/>.</summary>
    public static string MirrorDirectory(string localRoot, InstallRecord record)
        => SolverHomes.Home(MirrorRoot(localRoot, record.Distribution!), record.Tool, record.Version);

    /// <summary>Writes the mirror of a record just published inside a distribution.</summary>
    public static void WriteMirror(string localRoot, InstallRecord record)
    {
        string dir = MirrorDirectory(localRoot, record);
        Directory.CreateDirectory(dir);
        record.Write(Path.Combine(dir, SolverHomes.RecordFile));
    }

    /// <summary>Every mirrored record of <paramref name="tool"/> under <paramref name="localRoots"/>, newest first.</summary>
    public static IReadOnlyList<InstallRecord> Mirrors(IEnumerable<string> localRoots, SolverTool tool)
    {
        var found = new List<InstallRecord>();
        foreach (string root in localRoots.Distinct(StringComparer.Ordinal))
        {
            string wsl = Path.Combine(root, MirrorDirectoryName);
            IEnumerable<string> distros;
            try { distros = Directory.Exists(wsl) ? Directory.EnumerateDirectories(wsl).ToList() : []; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (string mirror in distros)
                found.AddRange(SolverHomes.Published(tool, [mirror])
                    .Where(r => string.Equals(r.Distribution, Path.GetFileName(mirror), StringComparison.OrdinalIgnoreCase)));
        }
        return found.OrderByDescending(r => r.InstalledAt).ToList();
    }

    /// <summary>
    /// Holds the mirror of the circuitRF-installed home <paramref name="program"/> (inside a distribution)
    /// lives in, for as long as a run uses it — brief 25's in-use rule, kept on this machine's side because a
    /// lock inside the distribution could not be seen without starting it. Null for a program circuitRF did
    /// not install there.
    /// </summary>
    public static IDisposable? Hold(IEnumerable<string> localRoots, SolverInstallation program, string holder)
    {
        if (program.Distribution is null) return null;
        var roots = localRoots.ToList();
        var record = Mirrors(roots, program.Tool).FirstOrDefault(r =>
            string.Equals(r.Distribution, program.Distribution, StringComparison.OrdinalIgnoreCase)
            && program.Path.StartsWith(r.Home.TrimEnd('/') + "/", StringComparison.Ordinal));
        if (record is null) return null;
        string? mirror = roots.Select(root => MirrorDirectory(root, record)).FirstOrDefault(Directory.Exists);
        return mirror is null ? null : SolverInUse.HoldHome(mirror, holder);
    }

    /// <summary>
    /// Whether a mirrored record's home has VANISHED — shown as <i>missing</i> and offered for clean-up.
    /// Answered without starting anything: the distribution is gone from <c>wsl -l</c> (unregistered, and its
    /// filesystem with it), or it is RUNNING and its share shows no home. A stopped distribution is not
    /// started to find out; its home is taken to be there.
    /// </summary>
    public static bool Missing(IWsl? wsl, InstallRecord record)
    {
        if (record.Distribution is not { } name || wsl is null || !wsl.Available) return false;
        var state = WslDistributions.Read(wsl);
        if (!state.Ready) return state.Condition == WslCondition.NoDistribution;
        var d = state.Distributions.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (d is null) return true;
        return d.Running && !new WslSession(wsl, d.Name).Exists(record.Home);
    }
}
