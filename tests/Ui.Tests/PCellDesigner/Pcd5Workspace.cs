// Owner: PCD-5 (brief-pcd-5 §3).

using System.Runtime.CompilerServices;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.PCellDesigner;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Ui.Tests.PCellDesigner;

/// <summary>
/// A scratch workspace on the <c>microstrip-0508</c> board holding designed cells made by <see cref="PCellDesign.Create"/>:
/// the C1 through line (Port – TL1 – TL2 – TL3 – Port, 5 + 3 + 7 mm) with TL2's length the cell parameter <c>Lmid</c>
/// (3 mm, 1 to 6 mm). Each test names its own cells: the PCell registry is process-wide.
/// </summary>
internal sealed class Pcd5Workspace : IDisposable
{
    public string Root { get; }
    public string TechPath { get; }
    public Technology Tech { get; }

    public Pcd5Workspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "pcd5-" + Guid.NewGuid().ToString("N")[..10]);
        TechPath = Path.Combine(Root, "tech", "microstrip-0508.ctech");
        Directory.CreateDirectory(Path.GetDirectoryName(TechPath)!);
        File.Copy(Path.Combine(TestData, "schematic-to-layout", "microstrip-0508.ctech"), TechPath);
        WorkspacePersistence.SaveToFile(Path.Combine(Root, ".cws"), new CwsFile { DefaultTechRef = "tech/microstrip-0508.ctech" });
        Tech = TechPersistence.LoadFromFile(TechPath);
    }

    public static string TestData { get; } = TestDataRoot();
    private static string TestDataRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "testdata"));

    /// <summary>A designed through line named <paramref name="name"/> under <paramref name="parent"/> (the root when null).</summary>
    public string Designed(string name, string? parent = null, Action<SchematicEditModel>? edit = null)
    {
        var created = PCellDesign.Create(parent ?? Root, name);
        var model = SchematicPersistence.LoadFromFile(
            Path.Combine(TestData, "schematic-to-layout", "C1-through-line", "schematic", "C1-through-line.csch")).model;
        var l = model.Components.Single(c => c.InstanceName == "TL2").Parameters.Single(p => p.Name == "L");
        l.Expression = "Lmid";
        edit?.Invoke(model);
        SchematicPersistence.SaveToFile(created.SchematicPath!, model, cellName: name);

        string ccellPath = Path.Combine(created.CellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.Parameters = [new CcellParameter { Name = "Lmid", DefaultExpression = "3", Unit = "mm", Dimension = UnitDimension.Length,
                                                 Minimum = 1e-3, Maximum = 6e-3, Description = "middle line length" }];
        CellPersistence.SaveToFile(ccellPath, ccell);
        return created.CellDir;
    }

    /// <summary>A bench cell whose schematic places one instance <c>X1</c> of <paramref name="cellDir"/>, Lmid as given.</summary>
    public string Bench(string name, string cellDir, string lmidMm)
    {
        var created = CellCreate.Create(Root, name, CellViews.Schematic);
        var model = new SchematicEditModel();
        var x1 = new EditableComponent
        {
            InstanceName = "X1", Symbol = SymbolKind.Generic,
            CellRef = CircuitRF.Core.RefPath.ToStored(Path.GetRelativePath(Path.GetDirectoryName(created.SchematicPath!)!, cellDir)),
        };
        x1.Parameters.Add(new EditableParameter { Name = "Lmid", Expression = lmidMm, Unit = "mm", Dimension = UnitDimension.Length });
        model.Components.Add(x1);
        SchematicPersistence.SaveToFile(created.SchematicPath!, model, cellName: name);
        return created.SchematicPath!;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
