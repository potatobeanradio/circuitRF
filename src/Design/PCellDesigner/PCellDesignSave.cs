// Owner: PCD-6 (brief-pcd-6 R-pcd6-9). Framework-free.
//
// The designer's Save, for every caller: the window (PCellDesignerViewModel.Save) and a caller with no window
// (PCellFilter's Make and Polish, brief-pcd-12 D5). One implementation, so they cannot write a cell differently.

using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Symbol;

namespace CircuitRF.Design.PCellDesigner;

/// <summary>Where a designed cell's views are, its technology, and how it is saved.</summary>
public static class PCellDesignSave
{
    /// <summary>The cell's primary schematic, or where it goes.</summary>
    public static string SchematicPath(string cellDir) => Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Schematic),
        CellFolder.ResolvePrimary(cellDir, ViewType.Schematic).ResolvedName ?? NameOf(cellDir) + CellFolder.ViewExtension(ViewType.Schematic));

    /// <summary>The cell's own layout, or where it goes.</summary>
    public static string ClayPath(string cellDir) => Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout),
        CellFolder.ResolvePrimary(cellDir, ViewType.Layout).ResolvedName ?? NameOf(cellDir) + CellFolder.ViewExtension(ViewType.Layout));

    /// <summary>The cell's technology as the designer resolves it: its layout's reference, else the workspace's.</summary>
    public static (Technology? Tech, string? Identity) Technology(string cellDir, string workspaceRoot)
    {
        string clay = ClayPath(cellDir);
        string? techRef = File.Exists(clay) ? LayoutPersistence.LoadFromFile(clay).TechRef : null;
        var (tech, _) = TechnologyResolver.ResolveForDocument(techRef, clay, Path.Combine(workspaceRoot, ".cws"), new TechnologyCache());
        return (tech.Tech, tech.ResolvedPath);
    }

    private static string NameOf(string cellDir) => Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cellDir)));

    /// <summary>
    /// R-pcd6-9: <paramref name="cell"/> (its parameters as the caller set them) marked designed with its terminals and a
    /// symbol from its Ports when it has none that fits, the <paramref name="schematic"/>, and the cell's own layout
    /// planned at the parameters' defaults (S1), never at any other value. Null when all three were written; else what
    /// was not, and why. <paramref name="technology"/> null resolves the cell's.
    /// </summary>
    public static string? Save(string cellDir, string workspaceRoot, SchematicEditModel schematic, CcellFile cell,
                               Technology? technology = null, string? techIdentity = null)
    {
        string name = NameOf(cellDir);
        string schematicPath = SchematicPath(cellDir), clayPath = ClayPath(cellDir);
        cell.PCellDesign ??= new CcellPCellDesign();
        cell.LayoutGenerator = PCellDesign.GeneratorIdFor(name);
        cell.EmCopper = true;
        cell.Terminals = PCellDesign.TerminalsOf(schematic);   // LVS pairs each Port with the generator's pin (R-pcd5-4)
        cell.NumPorts = cell.Terminals.Count;
        cell.PrimarySchematic ??= Path.GetFileName(schematicPath);
        cell.PrimaryLayout ??= Path.GetFileName(clayPath);
        string symbolDir = CellFolder.SubFolderPath(cellDir, ViewType.Symbol);
        string symbolPath = Path.Combine(symbolDir,
            CellFolder.ResolvePrimary(cellDir, ViewType.Symbol).ResolvedName ?? name + CellFolder.ViewExtension(ViewType.Symbol));
        cell.PrimarySymbol ??= Path.GetFileName(symbolPath);
        try
        {
            // A bench places the cell by its symbol: one generated from its Ports (the generator Generate Symbols uses)
            // whenever the cell has none, or one whose pins no longer match the Ports. A drawn symbol that matches stays.
            if (!File.Exists(symbolPath) || SymbolPersistence.LoadFromFile(symbolPath).Pins.Count != cell.Terminals.Count)
            {
                Directory.CreateDirectory(symbolDir);
                SymbolPersistence.SaveToFile(symbolPath, AutoSymbolGenerator.Generate(name, cell.Terminals.Count));
            }
            CellPersistence.SaveToFile(Path.Combine(cellDir, CellFolder.CcellFileName), cell);
            Directory.CreateDirectory(Path.GetDirectoryName(schematicPath)!);
            SchematicPersistence.SaveToFile(schematicPath, schematic, name);
            if (technology is null) (technology, techIdentity) = Technology(cellDir, workspaceRoot);
            if (technology is null) return $"Saved {name}'s schematic and parameters; the layout resolves no technology, so it was not written.";
            var at = DesignedCellLayout.At(cellDir, new Dictionary<string, Value>(), workspaceRoot, technology, techIdentity, schematic);
            if (at.Layout is null) return $"Saved {name}'s schematic and parameters; the layout was not written: {at.Refusal}";
            Directory.CreateDirectory(Path.GetDirectoryName(clayPath)!);
            LayoutPersistence.SaveToFile(clayPath, at.Layout);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{name} could not be saved: {e.Message}";
        }
    }
}
