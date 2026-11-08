// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-lcp-5 R-lcp5-4 — the one answer to "the 3D problem this setup makes of its layout". A setup naming component ports is
// built through an in-memory 3D view of the layout (C3dHierarchy.ViewForSetup, then C3dProblemAssembly.Assemble with the .cem
// as its setup), so a .cem and a .c3d of one layout build their ports by one construction: landings, verification, connector
// shell bridges and the co-simulation data are the .c3d's. Every other setup is the generator's, unchanged.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.ThreeD;

public static class Em3dSetupProblem
{
    /// <summary>The note a problem built through the view carries first.</summary>
    public const string ThroughViewNote =
        "This setup names component ports, so its problem is built in a 3D view of the layout, made in memory as New 3D View " +
        "from Layout makes one; nothing is written.";

    private const string PlacedCellNotePrefix = "A placed cell contributes geometry only";

    /// <summary>R-lcp5-3 — a solve region beside component ports. A placed cell takes the whole layout, so the region could only
    /// be dropped, and that would solve a different problem from the one on screen.</summary>
    public static string RegionRefusal(EmSetup setup)
        => $"{(setup.Name is { Length: > 0 } n ? $"Setup '{n}'" : "This setup")} names a SolveRegion and ComponentPorts. A 3D run " +
           "builds component ports in a 3D view of the whole layout, which has no solve region: remove the SolveRegion, or the " +
           "component ports.";

    /// <summary>Whether <paramref name="setup"/>'s problem is built through a 3D view of its layout rather than the generator. A list
    /// the setup cannot use (a thermal or electrostatic setup that kept one from before) is not a reason to go through the view:
    /// such a setup is the generator's, as it would be with no list.</summary>
    public static bool ThroughView(EmSetup setup) => setup.ComponentPorts.Count > 0 && EmComponentPorts.Inapplicable(setup) is null;

    /// <summary>
    /// The 3D problem <paramref name="setup"/> makes of <paramref name="source"/>. A setup with no component ports is
    /// <see cref="Em3dGenerator.Generate"/>'s, byte for byte.
    /// </summary>
    /// <param name="portsOptional">For a picture, never for a solve: on the generator, as <see cref="Em3dGenerator.Generate"/>
    /// takes it; through the view, a refusal draws the layout without component ports, the refusal in
    /// <see cref="Em3dGenerationResult.PortRefusal"/>.</param>
    /// <param name="workspaceCws">The technology walk's fallback; null walks up from the layout.</param>
    public static Em3dGenerationResult Of(EmSetup setup, EmLayoutSource source, Technology tech, bool portsOptional = false,
                                          string? workspaceCws = null, TechnologyCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tech);
        if (!ThroughView(setup))
        {
            var plain = setup;
            if (plain.ComponentPorts.Count > 0) { plain = setup.Clone(); plain.ComponentPorts = []; }
            return Em3dGenerator.Generate(plain, source, tech, portsOptional: portsOptional);
        }
        var built = ThroughThreeDView(setup, source, tech, workspaceCws, cache);
        if (built.Problem is not null || !portsOptional) return built;
        var drawn = setup.Clone();
        drawn.ComponentPorts = [];
        drawn.SolveRegion = null;
        var picture = Em3dGenerator.Generate(drawn, source, tech, portsOptional: true);
        return picture with { PortRefusal = built.Refusal };
    }

    private static Em3dGenerationResult ThroughThreeDView(EmSetup setup, EmLayoutSource source, Technology tech, string? workspaceCws,
                                                         TechnologyCache? cache)
    {
        if (setup.SolveRegion is not null) return new(null, RegionRefusal(setup), []);
        string clay = Path.GetFullPath(source.AbsolutePath);
        workspaceCws ??= WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(clay));
        cache ??= new TechnologyCache();
        var view = C3dHierarchy.ViewForSetup(setup, source, tech, workspaceCws, cache);
        if (view.Document is not { } document) return new(null, view.Refusal, view.Notes);
        // The layout the caller holds is the one solved — the open editor's, with its unsaved edits — not the file.
        var elaborator = new C3dElaborator(cache) { LayoutsInMemory = new Dictionary<string, LayoutView> { [clay] = source.View } };
        var g = C3dProblemAssembly.Assemble(setup, document, view.Path, workspaceCws, elaborator, fromCem: true);
        // The elaborator's note that a placed cell brings no ports is about a .c3d's instance; here the setup's drawn ports were
        // carried into the view, and its solve region refused beside component ports, so the note would say the opposite.
        return g with
        {
            Notes = [ThroughViewNote, .. view.Notes, .. g.Notes.Where(n => !n.StartsWith(PlacedCellNotePrefix, StringComparison.Ordinal))],
        };
    }
}
