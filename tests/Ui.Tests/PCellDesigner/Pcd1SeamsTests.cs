// Owner: PCD-1 (brief-pcd-0 §5, seam 13).

using System.Runtime.CompilerServices;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Ui.Tests.PCellDesigner;

/// <summary>brief-pcd-0 §4 and S4: every new field round-trips, and existing files re-serialise byte for byte.</summary>
public sealed class Pcd1SeamsTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    [Fact]
    public void EveryNewFieldRoundTripsAndExistingFilesAreUnchanged()
    {
        // .ccell: the designed marker, EmCopper and a parameter's range.
        var cell = new CcellFile
        {
            LayoutGenerator = "cpcell:PadTee",
            PCellDesign     = new CcellPCellDesign { Sweep = "start=1 stop=20 npts=201 Unit=GHz" },
            EmCopper        = true,
            Parameters      = [new CcellParameter { Name = "Lpad", DefaultExpression = "200", Unit = "um",
                                                    Dimension = UnitDimension.Length, Minimum = 1e-4, Maximum = 2e-3 }],
        };
        string ccellJson = CellPersistence.Serialize(cell);
        var back = CellPersistence.Deserialize(ccellJson);
        Assert.Equal("start=1 stop=20 npts=201 Unit=GHz", back.PCellDesign?.Sweep);
        Assert.True(back.EmCopper);
        Assert.Equal((1e-4, 2e-3), (back.Parameters[0].Minimum, back.Parameters[0].Maximum));
        Assert.Equal((1e-4, 2e-3), (back.Parameters[0].Clone().Minimum, back.Parameters[0].Clone().Maximum));
        Assert.Equal(ccellJson, CellPersistence.Serialize(back));

        // .csch: an offset placement and a stretch binding, through the model and its clone.
        var m = new SchematicEditModel();
        var offset = new OffsetPlacement { Anchor = "TL1.2", Pin = 1, Dx = "g", Dy = "0", Unit = "um", Angle = "90" };
        var stretch = new StretchBinding { Parameter = "Lpad", Anchor = 1, Moving = [2, 3], AxisDeg = 0 };
        m.Components.Add(new EditableComponent { InstanceName = "SH1", Symbol = SymbolKind.Mlin, Offset = offset, Stretch = [stretch] });
        string csch = SchematicPersistence.Serialize(m, "PadTee");
        var c = SchematicPersistence.Deserialize(csch).model.Components.Single().Clone();
        Assert.Equal(offset, c.Offset);
        var s = Assert.Single(c.Stretch!);
        Assert.Equal(("Lpad", 1, 0.0), (s.Parameter, s.Anchor, s.AxisDeg));
        Assert.Equal([2, 3], s.Moving);
        Assert.Equal(csch, SchematicPersistence.Serialize(SchematicPersistence.Deserialize(csch).model, "PadTee"));

        // Existing fixtures, byte for byte.
        string ccellPath = Path.Combine(RepoRoot(), "examples", "Hierarchy", "Pad", ".ccell");
        string onDiskCell = File.ReadAllText(ccellPath);
        Assert.Equal(onDiskCell, CellPersistence.Serialize(CellPersistence.Deserialize(onDiskCell)));

        string cschPath = Path.Combine(RepoRoot(), "testdata", "schematic-to-layout", "C4-bends", "schematic", "C4-bends.csch");
        string onDisk = File.ReadAllText(cschPath);
        var (model, view, name) = SchematicPersistence.Deserialize(onDisk, Path.GetDirectoryName(cschPath));
        Assert.Equal(onDisk, SchematicPersistence.Serialize(model, name, view.PanX, view.PanY, view.Zoom));
    }
}
