namespace CircuitRF.Design.Em3d.Install;

/// <summary>
/// The install this computer would run for a tool: the recipe and the installer that runs it, and — for
/// Palace on Windows — the Linux subsystem distribution it runs in (brief-em3d-26). <b>The one entry point</b>
/// the Settings row, a refusal's <i>Install…</i> and <c>circuitrf solver install</c> all take, so none of them
/// knows which platform installs where.
/// </summary>
public sealed record SolverInstallPlan(SolverRecipe Recipe, SolverInstaller Installer, string? Distribution)
{
    /// <summary>
    /// The plan for <paramref name="tool"/> (the named <paramref name="version"/>, or the newest validated),
    /// or null. On null, <paramref name="refusal"/> is the reason when there is one to give (a Linux subsystem
    /// precondition); null there means this machine simply has no recipe, which the caller words.
    /// </summary>
    public static SolverInstallPlan? For(SolverTool tool, string? version, out string? refusal)
    {
        refusal = null;
        if (InSubsystem(tool) is { } wsl)
            return Wsl.WslPalaceInstall.Plan(wsl, version, SolverDiscovery.For(tool).PreferredLocation?.Invoke(),
                                             SolverHomes.DefaultRoot, out refusal);
        return SolverRecipes.For(tool, version) is { } recipe ? new SolverInstallPlan(recipe, new SolverInstaller(), null) : null;
    }

    /// <summary>
    /// Whether this computer has an install route for <paramref name="tool"/> at all — a recipe here, or, for
    /// Palace on Windows, the subsystem's <c>wsl.exe</c> (whose preconditions the install itself then checks,
    /// each refused with its one step). Starts nothing.
    /// </summary>
    public static bool HasRoute(SolverTool tool)
        => InSubsystem(tool) is { } wsl ? wsl.Available : SolverRecipes.For(tool) is not null;

    /// <summary>The subsystem a tool installs into here, or null when it installs natively.</summary>
    private static Wsl.IWsl? InSubsystem(SolverTool tool)
    {
        var d = SolverDiscovery.For(tool);
        return d.Subsystem is { } wsl && d.PreferredLocation?.Invoke() is not { Kind: PalaceLocationKind.Native } ? wsl : null;
    }
}
