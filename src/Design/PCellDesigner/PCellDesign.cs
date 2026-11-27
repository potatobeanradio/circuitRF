// Owner: PCD-5 (brief-pcd-0 §5, seam 4; written by PCD-1's seams commit). Framework-free.

using System.Globalization;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.PCellDesigner;

/// <summary>brief-pcd-0 D-cell: what makes a cell a designed cell, and how one is made.</summary>
public static class PCellDesign
{
    public const string GeneratorPrefix      = "cpcell:";
    public const string PackageExtension     = ".cpcell";
    public const string ManifestFileName     = "manifest.json";
    public const string PackageFormat        = "circuitrf.pcell";
    public const int    PackageFormatVersion = 1;

    public static string GeneratorIdFor(string cellName) => GeneratorPrefix + cellName;

    public static bool IsDesigned(string? cellDir) => CellFolder.ReadCcell(cellDir)?.PCellDesign is not null;

    /// <summary>
    /// A new designed cell under parentDir: CellCreate.Create with schematic and symbol views, then the .ccell's
    /// PCellDesign, LayoutGenerator = GeneratorIdFor(cellName) and EmCopper = true. What Tools ▸ PCell Designer… and
    /// <c>pcell new</c> both call.
    /// </summary>
    /// <exception cref="ArgumentException">The name fails <see cref="NameValidator"/>, as <see cref="CellCreate.Create"/>.</exception>
    /// <exception cref="IOException">A folder of that name is already there: a cell is never created over another.</exception>
    public static CellCreateResult Create(string parentDir, string cellName)
    {
        if (Directory.Exists(Path.Combine(parentDir, cellName)))
            throw new IOException($"A folder named {cellName} already exists in {parentDir}; choose another name.");
        var created = CellCreate.Create(parentDir, cellName, CellViews.Schematic | CellViews.Symbol);
        string ccellPath = Path.Combine(created.CellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PCellDesign = new CcellPCellDesign();
        ccell.LayoutGenerator = GeneratorIdFor(cellName);
        ccell.EmCopper = true;
        CellPersistence.SaveToFile(ccellPath, ccell);

        // brief-pcd-11 R-pcd11-1: not empty — Port 1 and its ground, so the first part dropped has a pin to join.
        if (created.SchematicPath is { } csch)
        {
            var (model, _, _) = SchematicPersistence.LoadFromFile(csch);
            model.SchematicDirectory = Path.GetDirectoryName(csch);
            if (CellNetlistMerge.Merge(model, "Port:P1 in 0 Num=1 Z=50 Ohm\n").Schematic is { } seeded)
                SchematicPersistence.SaveToFile(csch, seeded, cellName);
        }
        return created;
    }

    /// <summary>
    /// brief-pcd-5 R-pcd5-4: the <c>.ccell</c> terminal map of a designed cell — one terminal per Port, its <c>Num</c> the
    /// port and the generator's pin of that number (<see cref="DesignedPCellResolver"/> names its pins so), its
    /// designator the name. What Export writes into the package and the designer's Save writes beside the schematic.
    /// </summary>
    public static List<CcellTerminal> TerminalsOf(SchematicEditModel schematic) =>
        [.. PortsOf(schematic).Select(p => new CcellTerminal
        {
            Port = p.Num, Name = p.Port.InstanceName, LayoutPin = [p.Num.ToString(CultureInfo.InvariantCulture)],
        })];

    /// <summary>The schematic's Ports with a whole <c>Num</c>, in Num order: the designed cell's interface.</summary>
    internal static IReadOnlyList<(EditableComponent Port, int Num)> PortsOf(SchematicEditModel schematic) =>
        [.. schematic.Components
            .Where(c => PortPlacement.IsPort(c) && c.Disable is not (DisableState.Open or DisableState.Short))
            .Select(c => (Port: c, Ok: int.TryParse(PortPlacement.NumberText(c), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n), Num: n))
            .Where(t => t.Ok && t.Num > 0)
            .OrderBy(t => t.Num)
            .Select(t => (t.Port, t.Num))];
}
