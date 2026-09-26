using CircuitRF.Design.Em3d.Install;

namespace CircuitRF.Design.Em3d.Wsl;

/// <summary>
/// brief-em3d-26 — what a run needs to know about a Palace found in the Linux subsystem before it starts:
/// the distribution's home, the MPI launcher beside that Palace, and the memory the subsystem's VM has.
/// </summary>
public static class WslPalace
{
    /// <summary>
    /// The runner for <paramref name="palace"/>, which discovery found inside a distribution, or null with
    /// <paramref name="refusal"/> saying why the distribution cannot take the run.
    /// </summary>
    internal static WslPalaceRunner? Runner(IWsl wsl, SolverInstallation palace, out string? refusal)
    {
        refusal = null;
        var session = new WslSession(wsl, palace.Distribution!);
        if (session.Home(out string? why) is not { } home)
        {
            refusal = $"Palace is in the Linux subsystem distribution '{palace.Distribution}', and {why}.";
            return null;
        }
        var (mpirun, how) = FindMpiLauncher(session, home, palace.Path);
        return new WslPalaceRunner(session, home, mpirun, how);
    }

    /// <summary>
    /// R-em3d26-2b — the MPI Palace runs under inside the distribution, found the way series 1's Spack route
    /// finds it natively: an <c>mpirun</c> beside Palace; then the MPI Palace was LINKED against, from the
    /// Spack tree circuitRF installed it in (its record names the tree), then from the user's own Spack
    /// trees; then the distribution's login-shell <c>PATH</c>. A named launcher in Settings is a WINDOWS
    /// path and cannot run inside, so it is not consulted here.
    /// </summary>
    public static (string? Path, string How) FindMpiLauncher(WslSession session, string home, string palace)
    {
        string beside = WslPaths.Combine(WslPaths.Parent(palace), "mpirun");
        if (session.Exists(beside)) return (beside, "found beside Palace");

        var trees = SolverDiscovery.SubsystemRecords(session, home, SolverTool.Palace)
            .Where(r => r.SpackInstallTree is not null && palace.StartsWith(r.Home.TrimEnd('/') + "/", StringComparison.Ordinal))
            .Select(r => r.SpackInstallTree!).ToList();
        if (trees.Count > 0 && SpackInstalls.MpiLauncherFor(palace, trees, session.ToWindows) is { } own)
            return (own, "the MPI Palace was built with, from the Spack tree circuitRF installed it in");

        string[] roots = [WslPaths.Combine(home, "spack", "opt", "spack"), WslPaths.Combine(home, "opt", "spack"), "/opt/spack"];
        if (SpackInstalls.MpiLauncherFor(palace, roots, session.ToWindows) is { } linked)
            return (linked, "the MPI Palace was built with, from its Spack installation");

        var run = session.Exec(["bash", "-lc", WslSession.LoginShellCommandV, "bash", "mpirun"]);
        string path = run.Text.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
        if (run.Ok && path.StartsWith('/')) return (path, "found on the distribution's PATH");
        return (null, $"no MPI launcher (mpirun) was found beside Palace, in its Spack installation or on the PATH of " +
                      $"the Linux subsystem distribution '{session.Distribution}'");
    }

    /// <summary>
    /// R-em3d26-2c — the subsystem's memory as the run's memory check sees it: <c>free -b</c> inside the
    /// distribution, and the sentence naming <c>.wslconfig</c>'s <c>memory=</c>, with the file's path under
    /// the user's profile. Null when the figure cannot be read (the check then has nothing to compare with).
    /// </summary>
    public static Em3dMemoryScope? MemoryScope(WslSession session, string? userProfile = null)
        => session.MemoryBytes() is { } bytes and > 0
            ? new Em3dMemoryScope(bytes, $"the Linux subsystem's (distribution '{session.Distribution}')", WslConfigRemedy(userProfile))
            : null;

    /// <summary>The setting that raises the subsystem's memory, and where it lives.</summary>
    public static string WslConfigRemedy(string? userProfile = null)
    {
        string profile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string file = profile.Length == 0 ? @"%UserProfile%\.wslconfig" : profile.TrimEnd('\\', '/') + @"\.wslconfig";
        return "The Linux subsystem's virtual machine gets only part of this computer's memory by default; " +
               $"'memory=' under '[wsl2]' in {file} raises it (for example memory=24GB). Run 'wsl --shutdown' after " +
               "changing it, so the subsystem restarts with the new size.";
    }
}
