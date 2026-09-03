// C1's real gate, and it is a join rather than a feature: a technology imported from a process (C0)
// has to absorb THAT PROCESS'S OWN artwork with nothing left to reconcile.
//
// C1 needed no new import code — GdsiiImport already turns every structure into a real cell with a
// layout view. What was never checked is whether the two halves meet: if C0's layer table were keyed
// wrongly, every device in the library would arrive needing a layer decision from the user, one row
// per layer, on an import of dozens of cells. That failure is loud but only at the moment someone
// tries it, which is exactly the kind of thing a gate is for.
//
// The fixture is synthetic on both sides — the repository commits no third-party process data or
// artwork — but the SHAPE is the real one: artwork drawn on the same stream numbers the process file
// declares.

using CircuitRF.Ui.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Layout.TechImport;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.TechImport;

public class ImportedTechnologyAbsorbsItsOwnArtworkTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-c1-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private static Technology ImportedTechnology() =>
        ProcessTechnologyBuilder.Build(
            ProcessStackReader.Read(ProcessStackReaderTests.Stack),
            LayerPropertiesReader.Read(LayerPropertiesReaderTests.Table),
            "unused").Technology;

    /// <summary>
    /// A device library in the shape a real one has: several primitives, each its own top, plus a
    /// shared via array that only they reference. Drawn on the stream numbers the process's own layer
    /// table declares (10/0, 10/2, 5/0, 7/0).
    /// </summary>
    private static MemoryStream BuildDeviceLibrary()
    {
        var viaArray = new InterchangeStructure(
            "VIAARRAY",
            [new RectShape { Layer = new LayerKey(7, 0), X1 = 0, Y1 = 0, X2 = 200, Y2 = 200 }],
            []);

        InterchangeStructure Device(string name, int y) => new(
            name,
            [
                new RectShape { Layer = new LayerKey(10, 0), X1 = 0,   Y1 = y, X2 = 1000, Y2 = y + 400 },
                new RectShape { Layer = new LayerKey(5,  0), X1 = 100, Y1 = y, X2 = 900,  Y2 = y + 100 },
                new RectShape { Layer = new LayerKey(10, 2), X1 = 0,   Y1 = y, X2 = 50,   Y2 = y + 50  },
            ],
            [new LayoutInstance { CellRef = "VIAARRAY", X = 300, Y = y, Mag = 1.0 }]);

        var ms = new MemoryStream();
        GdsiiWriter.Write(
            ms,
            [viaArray, Device("devA", 0), Device("devB", 2000), Device("devC", 4000)],
            new GdsiiUnits(1e-6, 1e-9),
            null);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void EveryLayerTheArtworkUsesIsAlreadyDefined_SoNothingNeedsReconciling()
    {
        // THE gate. Measured: 77 distinct layers used by 56 device structures, all 77
        // already defined by the technology imported from that same process — zero to add, zero
        // decisions. If this goes red, C0's layer table and the kit's own artwork disagree, and every
        // device import turns into a wall of layer questions.
        var tech = ImportedTechnology();

        using var gds = BuildDeviceLibrary();
        var reader  = GdsiiReader.Open(gds);
        var shapes  = reader.ReadStructures().SelectMany(s => s.Shapes).ToList();
        var used    = shapes.Select(s => s.Layer).Distinct().ToList();

        Assert.NotEmpty(used);
        Assert.All(used, k => Assert.Contains(tech.Layers, l => l.Key == k));

        var sourceLayers = GdsiiLayerReconciliation.BuildSourceLayers(shapes, tech);
        var rows         = LayoutLayerMapping.Propose(shapes, sourceLayers, tech);

        Assert.False(LayoutLayerMapping.RequiresConfirmation(rows),
                     "artwork drawn on a process's own layers must not ask the user anything");
        Assert.All(rows, r => Assert.Equal(LayerMatchKind.SameKeySameName, r.Match));
    }

    [Fact]
    public void TheLibraryImportsAsPlaceableCells_AgainstThatTechnology()
    {
        var tech = ImportedTechnology();

        using var gds = BuildDeviceLibrary();
        var result = GdsiiImport.Import(gds, _dir, tech, destDbuPerMicron: 1000,
                                        preferSourceResolution: true);

        Assert.False(result.Cancelled);
        Assert.Equal(4, result.CreatedCellDirs.Count);
        Assert.Empty(result.LayersToAdd);           // nothing had to be invented

        // Each device is its own top; the shared via array is referenced and so is not.
        Assert.Equal(["devA", "devB", "devC"],
                     result.TopLevelCellDirs.Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));

        // And a device really is a placeable cell: a resolvable primary layout carrying its geometry.
        string devA    = Path.Combine(_dir, "devA");
        var    primary = CellFolder.ResolvePrimary(devA, ViewType.Layout);
        Assert.Equal(PrimaryState.SoleFile, primary.State);

        var view = LayoutPersistence.LoadFromFile(Path.Combine(
            CellFolder.SubFolderPath(devA, ViewType.Layout), primary.ResolvedName!));
        Assert.Equal(3, view.Shapes.Count);
        Assert.Single(view.Instances);
        Assert.Equal(1000, view.DbuPerMicron);
    }

    [Fact]
    public void TheProcessResolutionSurvivesTheImport_WithNoCoordinateRounding()
    {
        // A process draws on a 1 nm grid and C0 builds its stackup on the same assumption. Importing
        // at the source's own resolution is exact by construction; a mismatch here would round every
        // coordinate in the library, silently and only near the fine features.
        var tech = ImportedTechnology();

        using var gds = BuildDeviceLibrary();
        var result = GdsiiImport.Import(gds, _dir, tech, destDbuPerMicron: 1000,
                                        preferSourceResolution: true);

        Assert.DoesNotContain(result.Messages, m => m.Contains("round", StringComparison.OrdinalIgnoreCase));

        var view = LayoutPersistence.LoadFromFile(Path.Combine(
            CellFolder.SubFolderPath(Path.Combine(_dir, "devA"), ViewType.Layout), "devA.clay"));

        // Asserted on the GEOMETRY, not the shape type: the format has no rectangle record, so a
        // rectangle is written as a boundary and legitimately reads back as a polygon.
        var metal = Assert.Single(view.Shapes, s => s.Layer == new LayerKey(10, 0));
        var bbox  = LayoutGeometry.BboxOf(metal);
        Assert.Equal(0,    bbox.MinX);
        Assert.Equal(1000, bbox.MaxX);
        Assert.Equal(400,  bbox.MaxY - bbox.MinY);
    }
}
