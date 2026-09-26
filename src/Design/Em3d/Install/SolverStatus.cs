namespace CircuitRF.Design.Em3d.Install;

/// <summary>
/// One tool's row, as Settings ▸ 3D EM shows it and <c>circuitrf solver list</c> prints it
/// (brief-em3d-24 R-em3d24-5a, R-em3d24-7): what discovery found, where and how, whether it is validated,
/// what its build can do, and whether the assistant offers to install it here. <b>Both call
/// <see cref="Of"/></b>, so the window and the terminal cannot disagree about a machine.
/// </summary>
/// <param name="Summary">The row's sentence — <see cref="SolverDiscovery.DescribeForSettings"/>.</param>
/// <param name="Capabilities">Each capability a run could ask, probed — empty unless the program is
/// validated (an unvalidated build is not probed, as a run does not probe it).</param>
/// <param name="Recipe">The recipe this machine would install, or null when it has none.</param>
/// <param name="OfferInstall">Whether <i>Install …</i> is offered — <see cref="SolverInstaller.OfferFor"/>'s rule.</param>
public sealed record SolverStatus(
    SolverTool Tool, string Name, SolverInstallation? Found, IReadOnlyList<string> Rejected, string Summary,
    IReadOnlyList<SolverCapabilityVerdict> Capabilities, SolverRecipe? Recipe, bool OfferInstall)
{
    /// <summary>True when the program found is one circuitRF installed — the only kind brief 25 may remove.</summary>
    public bool InstalledByCircuitRf => Found?.HowFound == SolverHowFound.Installed;

    /// <summary>Asks discovery about <paramref name="tool"/>. Starts short processes (a version question,
    /// and cached capability probes); call it off the UI thread.</summary>
    public static SolverStatus Of(SolverTool tool, SolverDiscovery? discovery = null)
    {
        var d = discovery ?? SolverDiscovery.For(tool);
        var found = d.Find(out var rejected);
        var capabilities = found is { Validated: true }
            ? SolverInstaller.CapabilitiesToCheck(tool).Select(c => d.Probe(found, c)).ToList()
            : [];
        var recipe = SolverRecipes.For(tool);
        bool offer = recipe is not null && SolverInstaller.WouldHelp(d, found);
        return new SolverStatus(tool, d.Name, found, rejected, d.DescribeForSettings(found, rejected), capabilities, recipe, offer);
    }
}
