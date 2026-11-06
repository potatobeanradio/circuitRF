// brief-emb-1 R-emb1-8 — `explain x.c3d --component-ports ["C1,C2"|"*"]`: a 3D view's component ports, built as the run builds
// them and verified, without meshing. It owns no construction: every line comes out of C3dProblemAssembly.Assemble, the
// function the run calls, so what this reports is what a run would solve or refuse.

using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using RfCore.Export;

namespace CircuitRF.Cli;

internal static class ExplainComponentPorts
{
    private const string How =
        "built on the part's own pads from the board projection netlist --placement reads: the gap between its two pads (a " +
        "signal pad and its ground pad on a part with more), its faces at the closest copper scanned across the width, a " +
        "landing rectangle where that copper is not one straight face, the sheet at mid-copper height; verified before " +
        "meshing (no net bridged, end edges on their two conductors alone, side edges on none, no copper in the sheet, two nets)";

    /// <param name="list">The entries to build instead of the setup's own, as <c>em --component-ports</c> takes them; null
    /// builds the setup's.</param>
    public static int Walk(string path, string? setupName, string? list, List<ResolutionStepJson> walks)
    {
        string full = Path.GetFullPath(path);
        C3dDocument doc;
        try { doc = C3dPersistence.LoadFromFile(full); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.ExplainUnreadable(path, ex.Message)); }
        var (embedded, why) = C3dSetups.Select(doc, setupName);
        if (embedded is null) return JsonRun.Fail(CliDiagnostics.EmThreeDSetup(full, why!));
        var setup = C3dSetups.ForRun(embedded, full);
        if (list is not null) setup.ComponentPorts = C3dComponentPorts.ParseList(list);
        if (setup.ComponentPorts.Count == 0)
        {
            walks.Add(new ResolutionStepJson("component ports", full, null,
                $"setup '{embedded.Name}' names no ComponentPorts; give them in the setup, or here as --component-ports \"C1,C2\" or \"*\""));
            return 0;
        }
        var g = C3dProblemAssembly.Assemble(setup, doc, full, DocumentKinds.AncestorCws(full));
        if (g.ComponentPorts is not { } build)
        {
            walks.Add(new ResolutionStepJson("component ports", full, $"not built: {g.Refusal}", How));
            return 1;
        }
        if (build.Refusal is { } refused)
            walks.Add(new ResolutionStepJson("component ports", full, $"refused: {refused}", How));
        foreach (var p in build.Ports)
            walks.Add(new ResolutionStepJson("component port", p.ClayPath, C3dComponentPorts.Describe(p), How));
        foreach (var b in build.Bridges)
            walks.Add(new ResolutionStepJson($"connector shell {b.Name}", null,
                $"a copper block {C3dComponentPorts.Um(b.Box.X1 - b.Box.X0)} × {C3dComponentPorts.Um(b.Box.Y1 - b.Box.Y0)} µm joining '{b.Top}' " +
                $"and '{b.Bottom}' through the substrate",
                "a part whose ground pads on two copper layers lie over each other is a coaxial launch; its shell clamps the two grounds"));
        foreach (string note in build.Notes) walks.Add(new ResolutionStepJson("component ports: note", null, note, "the run notes the same"));
        foreach (string warning in build.Warnings)
        {
            walks.Add(new ResolutionStepJson("component ports: warning", null, warning, "check warns the same"));
            Console.Error.WriteLine("warning: " + warning);
        }
        if (build.Ok)
            walks.Add(new ResolutionStepJson("co-simulation netlist", null, EmCoSimNetlist.FileNameFor(setup),
                "a finished driven run writes it in the workspace: the board's N-port, each part from its port's node to ground"));
        if (build.Ok && setup.CreateSnpSchematic)
            walks.Add(new ResolutionStepJson("co-simulation cell", null, EmCoSimNetlist.CellNameFor(setup),
                "a finished driven run draws the same circuit as this cell, in place of a _sim cell; an existing cell is left as it is"));
        return build.Ok ? 0 : 1;
    }
}
