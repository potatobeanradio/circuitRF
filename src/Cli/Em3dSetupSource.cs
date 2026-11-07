using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Cli;

/// <summary>
/// A <c>.cem</c> read, resolved and — for a 3D setup — generated, the way every verb that looks at a
/// 3D setup reads one (brief-em3d-5). <c>render</c> and <c>explain</c> both come through here, so
/// the picture and the report are of the same problem: the same two walk-ups
/// (<see cref="EmSetupResolver"/>), the same generator, the same stem-paired <c>.wBond</c>.
/// </summary>
internal sealed record Em3dSetupSource(
    string                 Path,
    EmSetup                Setup,
    EmSetupResolution      Resolution,
    Em3dGenerationResult?  Generated,
    string?                Refusal)
{
    /// <summary>brief-em3d-42 — the elaboration a 3D view's problem was assembled from (a <c>.c3d</c>, or a
    /// <c>.cem</c> naming one); null for a layout's.</summary>
    public C3dElaboration? Elaboration { get; init; }

    /// <summary>Reads <paramref name="path"/>. Throws what the <c>.cem</c> reader throws on a file
    /// that will not parse; every other failure is <see cref="Refusal"/>.</summary>
    /// <param name="edit">brief-em3d-92 — a change made to a <c>.c3d</c>'s document after it is read and before it is
    /// elaborated (<c>render --transparency</c>): the document is this call's own copy, read from disk, so nothing else sees it
    /// and the file is never written.</param>
    public static Em3dSetupSource Load(string path, Action<C3dDocument>? edit = null)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (DocumentKinds.Classify(full) == DocumentKind.ThreeD) return ForThreeDView(full, null, edit);
        var setup = EmSetupPersistence.LoadFromFile(full);
        string? cws = DocumentKinds.AncestorCws(full);
        // brief-em3d-42 R-em3d42-5b — one field, two kinds of geometry: a LayoutRef naming a .c3d elaborates it.
        if (C3dSetups.IsThreeDView(EmSetupResolver.ResolveLayoutPath(full, setup.LayoutRef, cws)))
            return FromCemOnThreeDView(full, setup, cws);
        var resolution = EmSetupResolver.Resolve(full, setup.LayoutRef, cws, new TechnologyCache());
        return From(full, setup, resolution);
    }

    /// <summary>
    /// brief-em3d-42 — a <c>.c3d</c> drawn or explained: through its embedded setup when it has exactly one (or
    /// <paramref name="setupName"/> names one), so the picture is the problem a run would receive; otherwise
    /// its elaboration alone, in an air box at its own extent, because there is no setup to pad by.
    /// </summary>
    public static Em3dSetupSource ForThreeDView(string full, string? setupName, Action<C3dDocument>? edit = null)
    {
        var doc = C3dPersistence.LoadFromFile(full);
        edit?.Invoke(doc);
        string? cws = DocumentKinds.AncestorCws(full);
        var embedded = C3dSetups.Read(doc);
        var elaborator = new C3dElaborator();
        // brief-em3d-73 — a lone THERMAL setup makes no EM problem to draw or explain; the elaboration is what there is.
        bool one = setupName is not null || embedded.Count(s => s.Setup is { IsThermal: false }) == 1 && embedded.Count == 1;
        if (one)
        {
            var (chosen, why) = C3dSetups.Select(doc, setupName);
            if (chosen is null)
                return new Em3dSetupSource(full, new EmSetup { Name = setupName ?? "" }, NoResolution(full), null, why);
            var setup = C3dSetups.ForRun(chosen, full);
            // brief-em3d-88 R-em3d88-2 — a thermal setup has no EM problem to assemble: its picture is the view's own
            // elaboration, which is what the 3D editor draws whatever the active setup's kind (C3dProblemAssembly.ViewProblem).
            if (setup.IsThermal)
                return Drawing(full, setup, embedded.Count, elaborator.Elaborate(doc, full, cws,
                    new C3dElaborationOptions(null, setup.OperatingTempC ?? EmSetup.DefaultOperatingTempC)), thermal: true);
            Stage("lowering the 3D view to its problem");
            var generated = C3dProblemAssembly.Assemble(setup, doc, full, cws, elaborator);
            Stage("elaborating the 3D view at the sweep's top");
            var e = elaborator.Elaborate(doc, full, cws, new C3dElaborationOptions(generated.Problem?.Frequency.StopHz,
                                                                                   setup.OperatingTempC ?? EmSetup.DefaultOperatingTempC)
                                                         { RobustMeshing = setup.RobustMeshing, SolidVias = !setup.IsThermal });
            return new Em3dSetupSource(full, setup, ResolutionOf(full, e), generated,
                generated.Problem is null ? generated.Refusal ?? "the 3D problem could not be built." : null) { Elaboration = e };
        }

        var drawing = new EmSetup { Name = System.IO.Path.GetFileNameWithoutExtension(full), Solver3D = CircuitRF.Design.Em3d.Em3dSolvers.Default };
        return Drawing(full, drawing, embedded.Count, elaborator.Elaborate(doc, full, cws), thermal: false);
    }

    /// <summary>A view drawn from its elaboration alone, in a box at its own extent: a view with no single EM setup to pad
    /// one, or — <paramref name="thermal"/> — a thermal setup, which solves no air.</summary>
    private static Em3dSetupSource Drawing(string full, EmSetup setup, int embedded, C3dElaboration elaboration, bool thermal, bool partial = false)
    {
        if ((!elaboration.Ok && !partial) || elaboration.Extent() is not { } x)
            return new Em3dSetupSource(full, setup, ResolutionOf(full, elaboration), null,
                elaboration.Ok ? C3dProblemAssembly.NothingToSolve(elaboration) : string.Join(" ", elaboration.Refusals)) { Elaboration = elaboration };
        var problem = C3dProblemAssembly.ViewProblem(elaboration.Solids, elaboration.Sheets, elaboration.Materials, [],
                                                     C3dProblemAssembly.ExtentBox(x), elaboration.CylinderSheets);
        var notes = elaboration.Notes.Append(partial
            ? "Drawn from what resolved, in a box at its own extent: the 3D view does not build as a whole, so nothing can run from it."
            : thermal
            ? $"Drawn from the 3D view's elaboration: '{setup.Name}' is a thermal setup, which solves the solids alone, in no air box."
            : embedded == 0
            ? "Drawn from the 3D view's elaboration in a box at its own extent: it embeds no setup, so there is no air box to show."
            : $"Drawn from the 3D view's elaboration in a box at its own extent: it embeds {embedded} setups; name one with --setup to see its air box.").ToList();
        return new Em3dSetupSource(full, setup, ResolutionOf(full, elaboration),
            new Em3dGenerationResult(problem, null, notes)
            {
                Warnings = elaboration.Warnings, Origins = elaboration.Origins, MaterialSources = elaboration.MaterialSources,
                Wires = elaboration.Wires,
            }, null) { Elaboration = elaboration };
    }

    /// <summary>
    /// brief-em3d-135 follow-up — the owner's rule for every picture: a best attempt at drawing what works. A 3D view that is
    /// refused as a whole (a name that does not resolve, a wire off its pad) still elaborated everything that did, and this is
    /// that, drawn in a box at its own extent. Null when nothing was built, or <paramref name="refused"/> was not refused.
    /// A run never comes through here: it keeps the refusal.
    /// </summary>
    public static Em3dSetupSource? WhatResolved(Em3dSetupSource refused)
    {
        if (refused.Refusal is null || refused.Elaboration is not { Ok: false } e || e.Extent() is null) return null;
        return Drawing(refused.Path, refused.Setup, 0, e, thermal: false, partial: true);
    }

    /// <summary>brief-em3d-42 R-em3d42-5b — a <c>.cem</c> whose LayoutRef names a <c>.c3d</c>: that document,
    /// elaborated and assembled with this setup; its own embedded setups are not consulted.</summary>
    public static Em3dSetupSource FromCemOnThreeDView(string full, EmSetup setup, string? cws)
    {
        string c3d = EmSetupResolver.ResolveLayoutPath(full, setup.LayoutRef, cws)!;
        if (!File.Exists(c3d))
            return new Em3dSetupSource(full, setup, new EmSetupResolution(null, c3d, null, [$"3D view not found: {c3d}"]), null,
                                       $"its LayoutRef names the 3D view {c3d}, which does not exist.");
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(c3d); }
        catch (Exception e)
        {
            return new Em3dSetupSource(full, setup, new EmSetupResolution(null, c3d, null, [e.Message]), null,
                                       $"its 3D view {c3d} cannot be read: {e.Message}");
        }
        if (!setup.Is3D)
            return new Em3dSetupSource(full, setup, new EmSetupResolution(null, c3d, null, []), null,
                                       $"this setup is a planar analysis, and {C3dSetups.PlanarRefusal}.");
        var elaborator = new C3dElaborator();
        Stage("lowering the 3D view to its problem");
        var generated = C3dProblemAssembly.Assemble(setup, doc, c3d, DocumentKinds.AncestorCws(c3d), elaborator, fromCem: true);
        Stage("elaborating the 3D view at the sweep's top");
        var e2 = elaborator.Elaborate(doc, c3d, DocumentKinds.AncestorCws(c3d),
                                      new C3dElaborationOptions(generated.Problem?.Frequency.StopHz, setup.OperatingTempC ?? EmSetup.DefaultOperatingTempC)
                                      { RobustMeshing = setup.RobustMeshing, SolidVias = !setup.IsThermal });
        return new Em3dSetupSource(full, setup, ResolutionOf(c3d, e2), generated,
            generated.Problem is null ? generated.Refusal ?? "the 3D problem could not be built." : null) { Elaboration = e2 };
    }

    /// <summary>
    /// EMB-5 R-emb5-3/4 — a stage boundary of the lowering, for whoever hosts the call: named, so a client's progress says
    /// what is slow, and a point where a cancelled call stops. Lowering a board's 3D view is seconds to minutes and nothing
    /// below this takes a token, so these are its boundaries. Nothing on a command line, where no host is installed.
    /// </summary>
    private static void Stage(string name)
    {
        if (RunHost.Control is not { } control) return;
        control.ThrowIfCancellationRequested();
        control.BeginStage(name);
    }

    private static EmSetupResolution NoResolution(string c3d) => new(null, c3d, null, []);

    private static EmSetupResolution ResolutionOf(string c3d, C3dElaboration e) => new(null, c3d, e.TechnologyPath, []);

    /// <summary>A setup already read and resolved — <c>explain</c>'s case, which has done both for its
    /// walks — generated exactly as <see cref="Load"/> generates it.</summary>
    public static Em3dSetupSource From(string full, EmSetup setup, EmSetupResolution resolution)
    {
        if (!setup.Is3D) return new Em3dSetupSource(full, setup, resolution, null, null);

        if (resolution.Source is not { } source)
            return new Em3dSetupSource(full, setup, resolution, null,
                resolution.Diagnostics.Count > 0 ? string.Join(" ", resolution.Diagnostics)
                                                 : "its layout reference resolves to nothing.");
        if (source.Technology is not { } tech)
            return new Em3dSetupSource(full, setup, resolution, null,
                EmDiagnostics.NoTechnology(setup.LayoutRef).Render());

        Stage("generating the 3D problem from the layout");
        var generated = Em3dSetupProblem.Of(setup, source, tech, workspaceCws: DocumentKinds.AncestorCws(full));
        Stage("generated the 3D problem");
        return new Em3dSetupSource(full, setup, resolution, generated,
            generated.Problem is null ? generated.Refusal ?? "the 3D problem could not be built." : null);
    }
}
