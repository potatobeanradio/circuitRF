// Owner: PCD-6 (brief-pcd-6 §4).

using CircuitRF.Ui.PCellDesigner;
using CircuitRF.Ui.Tests.SchematicSync;

namespace CircuitRF.Ui.Tests.PCellDesigner;

/// <summary>A schematic-to-layout design made a designed cell in a scratch workspace, and the designer open on it.</summary>
internal static class Pcd6Fixture
{
    /// <summary><paramref name="design"/> marked designed; on C1, TL1's length is the parameter <c>L1</c> (5 mm, 1 to 10 mm).
    /// <paramref name="edit"/> changes the schematic before it is saved.</summary>
    public static (PCellDesignerViewModel Vm, SdlChecker.Prepared P) Open(string scratch, string design = "C1-through-line",
                                                                          Action<SchematicEditModel>? edit = null)
    {
        var p = SdlChecker.Prepare(design, scratch);
        var parameters = new List<CcellParameter>();
        if (p.Model.Components.FirstOrDefault(c => c.InstanceName == "TL1") is { } tl1 && design == "C1-through-line")
        {
            tl1.Parameters.Single(x => x.Name == "L").Expression = "L1";
            parameters.Add(new CcellParameter { Name = "L1", DefaultExpression = "5", Unit = "mm", Dimension = UnitDimension.Length,
                                                Minimum = 1e-3, Maximum = 1e-2 });
        }
        edit?.Invoke(p.Model);
        SchematicPersistence.SaveToFile(p.SchematicPath, p.Model, design);
        CellPersistence.SaveToFile(Path.Combine(p.CellDir, CellFolder.CcellFileName), new CcellFile
        {
            PCellDesign = new CcellPCellDesign(), LayoutGenerator = "cpcell:" + design, EmCopper = true,
            PrimarySchematic = Path.GetFileName(p.SchematicPath), Parameters = parameters,
        });
        return (new PCellDesignerViewModel(p.CellDir, p.Workspace), p);
    }

    /// <summary>The placed instance's box, DBU.</summary>
    public static Bbox Box(LayoutView layout, string layoutDir, string id) =>
        CellHierarchy.InstanceBbox(layout.Instances.Single(i => i.SchematicId == id), layoutDir);

    public static long Length(Bbox b) => Math.Max(b.MaxX - b.MinX, b.MaxY - b.MinY);
}
