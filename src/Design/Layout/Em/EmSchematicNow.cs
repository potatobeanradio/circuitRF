// Framework-free. No Avalonia, no SkiaSharp.
//
// brief-isc-1 — Create SnP schematic NOW: the cell a finished run's Create SnP schematic would make, made from the panel without a run.
// It reads the setup's existing Touchstone when there is one; when there is none the cell is still made, its SnP block naming the
// predictable path the run will write (EmRunService.ResolveSnpPath), and the result warns. The ports come from the run's own port
// discovery with nothing solved (EmRunService.PlannedPorts, C3dProblemAssembly.Assemble), so the cell is wired as the run would wire it.
// An existing cell is a question for the caller: Replace writes over its schematic, New name makes another cell.

using CircuitRF.Design.Cells;
using CircuitRF.Design.ThreeD;

namespace CircuitRF.Design.Layout.Em;

public static class EmSchematicNow
{
    /// <summary>The cell Create now would make for <paramref name="setup"/>: <c>_sch</c> with Include pre-existing schematic components
    /// on and a Primary schematic in <paramref name="emCellDir"/>, <c>_cosim</c> for a setup naming component ports, else <c>_sim</c>.</summary>
    public static string TargetCellName(EmSetup setup, string? emCellDir)
        => setup.IncludeSchematicComponents && EmCoSimNetlist.PrimarySchematic(emCellDir) is not null ? EmSchematicWithParts.CellNameFor(setup)
         : setup.ComponentPorts.Count > 0 && EmComponentPorts.Inapplicable(setup) is null ? EmCoSimNetlist.CellNameFor(setup)
         : EmSnpSchematic.CellNameFor(setup);

    /// <summary>The cell folder the setup's results belong to: the results root's parent, as a run's cell.</summary>
    public static string WorkspaceOf(string resultsRoot) => Path.GetDirectoryName(Path.GetFullPath(resultsRoot))!;

    /// <summary>A setup on a layout (a <c>.cem</c>, Planar or 3D).</summary>
    /// <param name="replace">Write over an existing cell's schematic.</param>
    /// <param name="cellName">Another name for the cell; null takes <see cref="TargetCellName"/>.</param>
    public static EmSnpSchematicResult ForLayout(EmSetup setup, EmLayoutSource? source, string resultsRoot, bool replace = false, string? cellName = null)
    {
        var ports = EmRunService.PlannedPorts(setup, source);
        string? cellDir = source is null ? null : CellFolder.SiblingView(source.AbsolutePath, ViewType.Layout, ViewType.Schematic).CellDir;
        return Make(setup, resultsRoot, cellDir, ports, () => source is null ? new Dictionary<int, EmPortFace>() : EmSnpSchematic.FacesOf(source, setup),
                    replace, cellName);
    }

    /// <summary>A setup run on a <c>.c3d</c>: embedded, or a <c>.cem</c> naming the 3D view (<paramref name="fromCem"/>).</summary>
    public static EmSnpSchematicResult ForThreeD(EmSetup setup, C3dDocument document, string documentPath, string? workspaceCws, string resultsRoot,
                                                 bool fromCem = false, bool replace = false, string? cellName = null)
    {
        EmResultPorts? ports = null;
        try
        {
            var g = C3dProblemAssembly.Assemble(setup, document, documentPath, workspaceCws, new C3dElaborator(), fromCem);
            if (g.ResultPorts is { } p) ports = new EmResultPorts(p, g.CoSimPorts is not null);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException) { }
        return Make(setup, resultsRoot, CellFolder.SiblingView(documentPath, ViewType.ThreeD, ViewType.Schematic).CellDir, ports,
                    () => EmSnpSchematic.FacesOf(document), replace, cellName);
    }

    /// <summary>
    /// The setup's Touchstone: the file for its port count when the ports are known and it exists, else the newest file of its
    /// results stem, else the path the run will write (which needs the port count).
    /// </summary>
    public static string? TouchstoneFor(EmSetup setup, string resultsRoot, int? portCount)
    {
        if (portCount is int n && EmRunService.ResolveSnpPath(resultsRoot, setup, n) is var exact && File.Exists(exact)) return exact;
        string stem = EmRunService.ResolveSnpBasePath(resultsRoot, setup);
        string? dir = Path.GetDirectoryName(stem);
        if (dir is not null && Directory.Exists(dir))
        {
            string name = Path.GetFileName(stem);
            var found = Directory.EnumerateFiles(dir, name + ".s*p")
                .Where(f => Path.GetExtension(f) is var x && x.Length > 3 && x[2..^1].All(char.IsDigit))
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (found is not null) return found;
        }
        return portCount is int m ? EmRunService.ResolveSnpPath(resultsRoot, setup, m) : null;
    }

    private static EmSnpSchematicResult Make(EmSetup setup, string resultsRoot, string? cellDir, EmResultPorts? ports,
                                             Func<IReadOnlyDictionary<int, EmPortFace>> faces, bool replace, string? cellName)
    {
        string workspace = WorkspaceOf(resultsRoot);
        string target = cellName ?? TargetCellName(setup, cellDir);
        if (TouchstoneFor(setup, resultsRoot, ports?.Ports.Count) is not { } snp)
            return new EmSnpSchematicResult(EmSnpSchematicOutcome.NotCreated, target, null,
                "No schematic was created: this setup has no Touchstone yet, and its ports could not be found to count them. Check the setup.");
        // A file of another port count than the setup now has is the setup's last result; its pins are what the block reads.
        if (ports is not null && File.Exists(snp) && RfCore.TouchstoneIO.ReadFile(snp, readComments: false).Ports != ports.Ports.Count) ports = null;

        IReadOnlyDictionary<int, EmPortFace> faceMap;
        try { faceMap = faces(); }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { faceMap = new Dictionary<int, EmPortFace>(); }

        string? fallback = null;
        if (setup.IncludeSchematicComponents
            && EmSchematicWithParts.Create(workspace, setup, cellDir, snp, ports?.Ports, faceMap, out fallback, replace: replace, cellName: cellName) is { } made)
            return made;

        EmSnpSchematicResult result;
        if (ports is { ComponentPorts: true })
        {
            string stored = Path.GetRelativePath(workspace, Path.GetFullPath(snp)).Replace('\\', '/');
            var models = EmCoSimNetlist.ModelsFor(ports.Ports.Select(p => p.Component?.ClayPath).FirstOrDefault(c => c is not null));
            string text = EmCoSimNetlist.Text(setup, stored, ports.Ports, models);
            result = EmCoSimNetlist.CreateCell(workspace, setup, text, snp, null, replace, cellName);
            if (!File.Exists(snp) && result.Outcome == EmSnpSchematicOutcome.Created)
                result = result with { Warnings = [$"No Touchstone exists yet at {stored}: the SnP block names it, and reads it once the setup has been run."] };
        }
        else result = EmSnpSchematic.Create(workspace, setup, snp, faceMap, ports?.Ports, replace, cellName);
        return fallback is null ? result : result with { Sentence = fallback + " " + result.Sentence };
    }
}
