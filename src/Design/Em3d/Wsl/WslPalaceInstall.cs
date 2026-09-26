using CircuitRF.Design.Em3d.Install;

namespace CircuitRF.Design.Em3d.Wsl;

/// <summary>
/// brief-em3d-26 R-em3d26-3 — <i>Install Palace…</i> on Windows: brief 24's Linux recipe, run inside the
/// chosen distribution, into a circuitRF-owned home in ITS Linux filesystem
/// (<c>~/.circuitrf/solvers/palace/&lt;version&gt;/</c>). Every precondition is refused with the one step that
/// fixes it, before anything runs; circuitRF performs none of them.
/// </summary>
public static class WslPalaceInstall
{
    /// <summary>
    /// The install for this computer, or null with <paramref name="refusal"/>: the subsystem is not enabled,
    /// virtualization is off, there is no distribution, the chosen one is WSL 1, it does not start, or no recipe
    /// fits its processor. The distribution is the location setting's, or else the default one (or the first
    /// WSL 2 one when the default is WSL 1).
    /// </summary>
    public static SolverInstallPlan? Plan(IWsl wsl, string? version, PalaceLocation? location, string localRoot,
                                          out string? refusal)
    {
        refusal = null;
        var state = WslDistributions.Read(wsl);
        if (!state.Ready) { refusal = state.Refusal; return null; }

        WslDistribution? chosen;
        if (location is { Kind: PalaceLocationKind.Subsystem, Distribution: { } named })
        {
            chosen = state.Distributions.FirstOrDefault(d => string.Equals(d.Name, named, StringComparison.OrdinalIgnoreCase));
            if (chosen is null)
            {
                refusal = $"The Linux subsystem distribution '{named}' chosen in Settings ▸ 3D EM is not installed. Installed: " +
                          $"{string.Join(", ", state.Distributions.Select(d => d.Name))}. Choose one of them, or Automatic.";
                return null;
            }
        }
        else
        {
            var @default = state.Distributions.FirstOrDefault(d => d.IsDefault) ?? state.Distributions[0];
            chosen = @default.Version >= 2 ? @default : state.Version2.FirstOrDefault() ?? @default;
        }
        if (chosen.Version < 2) { refusal = WslDistributions.Wsl1Refusal(chosen.Name); return null; }

        var session = new WslSession(wsl, chosen.Name);
        if (session.Home(out string? why) is not { } home)
        {
            refusal = $"Palace cannot be installed: {why}.";
            return null;
        }
        if (session.Architecture() is not { } arch)
        {
            refusal = $"Palace cannot be installed in '{chosen.Name}': circuitRF has install recipes for Linux on " +
                      "x64 and arm64 processors, and this distribution reports neither.";
            return null;
        }
        if (SolverRecipes.For(SolverTool.Palace, version, RecipePlatform.Linux, arch, SolverRecipes.All) is not { } recipe)
        {
            refusal = $"circuitRF has no recipe for Palace{(version is null ? "" : " " + version)} on Linux {SolverRecipes.Describe(arch)}.";
            return null;
        }

        var installer = new SolverInstaller(WslPaths.Combine(home, SolverDiscovery.SubsystemSolverRoot))
        {
            Target      = new WslInstallTarget(session),
            LocalRoot   = WslSolverHomes.MirrorRoot(localRoot, chosen.Name),
            Published   = record => WslSolverHomes.WriteMirror(localRoot, record),
            ConsentNote = ConsentNote(chosen.Name),
        };
        return new SolverInstallPlan(recipe, installer, chosen.Name);
    }

    /// <summary>R-em3d26-3c — where the build takes place, and whose the distribution stays.</summary>
    public static string ConsentNote(string distribution) =>
        $"The build takes place inside your Linux subsystem distribution '{distribution}', in its own Linux filesystem " +
        "(not on a Windows drive), with your Linux user's rights. That distribution, and any packages you install on " +
        "circuitRF's advice (the build tools a check below may name), are yours: circuitRF never removes them, " +
        "including when it uninstalls Palace. A record of the install is also kept on this computer, so Settings can " +
        "show it without starting the subsystem.";
}
