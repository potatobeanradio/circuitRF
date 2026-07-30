using System;
using System.IO;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-followups-3.md §2 (R-L5h-3/4): the connectivity ratsnest between pins
/// was being emitted as real, persisted <c>PathShape</c> geometry on a reserved layer (0, 900) —
/// selectable, movable, deletable, swept into booleans/flatten/clipboard/the spatial index, and one
/// <c>.ctech</c> mapping away from a fabrication file. This is the identical error already fixed once
/// for pins (R-L5g-13/14): a connectivity guide is an overlay concern, never artwork. This brief does
/// NOT build an overlay replacement — it removes the geometry emission entirely and cleans up any
/// already-persisted pollution on load.
/// </summary>
public sealed class RatsnestGeometryRemovalTests : IDisposable
{
    private readonly string _root;

    public RatsnestGeometryRemovalTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-ratsnest-removal-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private (string SchematicDir, string LayoutDir, Technology Tech) MakeCell(string cellName)
    {
        var tech = StarterTechnologies.MmicGaAs();
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        TechPersistence.SaveToFile(Path.Combine(_root, "tech", "t.ctech"), tech);
        WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "tech/t.ctech" });

        string cellDir = CellFolder.CreateCellFolder(_root, cellName);
        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        return (schematicDir, layoutDir, tech);
    }

    private static EditableComponent MakeMlin(string instanceName) =>
        new() { InstanceName = instanceName, Symbol = SymbolKind.Mlin, X = 0, Y = 0 };

    // ── R-L5h-3: the generator itself never emits ratsnest geometry ─────────────────────────────

    [Fact]
    public void Run_NeverAddsAnyShapesToTheTargetView_ConnectivityIsNoLongerGeometry()
    {
        var (schematicDir, layoutDir, tech) = MakeCell("Ratsnest1");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(MakeMlin("ML1"));
        model.Components.Add(MakeMlin("ML2"));

        var target = new LayoutView();
        var result = SchematicToLayoutGenerator.Run(
            model, target, schematicDir, _root, layoutDir, tech, "tech/t.ctech", cellResolver: null);
        Assert.NotNull(result.Command);
        result.Command!.Execute();

        Assert.Equal(2, target.Instances.Count);
        Assert.Empty(target.Shapes);
    }

    [Fact]
    public void Run_ProducesNoShapesOnTheReservedRatsnestLayer_EvenAcrossARerun()
    {
        var (schematicDir, layoutDir, tech) = MakeCell("Ratsnest2");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(MakeMlin("ML1"));

        var target = new LayoutView();
        SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, "tech/t.ctech", null)
            .Command!.Execute();

        model.Components.Add(MakeMlin("ML2"));
        var r2 = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, tech, "tech/t.ctech", null);
        r2.Command?.Execute();

        Assert.DoesNotContain(target.Shapes, s => s.Layer == SchematicToLayoutGenerator.RatsnestLayer);
        Assert.Empty(target.Shapes);
    }

    // ── R-L5h-4: RemoveRatsnestShapes cleans up already-persisted pollution ─────────────────────

    [Fact]
    public void RemoveRatsnestShapes_RemovesOnlyShapesOnTheReservedLayer_ReturnsTheCount()
    {
        var view = new LayoutView();
        view.Shapes.Add(new PathShape { Layer = SchematicToLayoutGenerator.RatsnestLayer, Xy = [0, 0, 1_000_000, 0], Width = 0, End = PathEndStyle.Flush });
        view.Shapes.Add(new PathShape { Layer = SchematicToLayoutGenerator.RatsnestLayer, Xy = [0, 0, 0, 1_000_000], Width = 0, End = PathEndStyle.Flush });
        var realShape = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 };
        view.Shapes.Add(realShape);

        int removed = SchematicToLayoutGenerator.RemoveRatsnestShapes(view);

        Assert.Equal(2, removed);
        Assert.Single(view.Shapes);
        Assert.Same(realShape, view.Shapes[0]);
    }

    [Fact]
    public void RemoveRatsnestShapes_NoneOnTheReservedLayer_ReturnsZero_LeavesShapesUntouched()
    {
        var view = new LayoutView();
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1, Y2 = 1 });

        int removed = SchematicToLayoutGenerator.RemoveRatsnestShapes(view);

        Assert.Equal(0, removed);
        Assert.Single(view.Shapes);
    }

    // ── Structural proof: GetOrCreateLayoutSession wires the cleanup at the ONE load funnel ─────

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    [Fact]
    public void GetOrCreateLayoutSession_SweepsRatsnestOnFreshLoad_MarksDirtyAndReportsWhenNonZero()
    {
        string src = ReadRepoFile(Path.Combine("src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));

        int methodStart = src.IndexOf("internal LayoutEditorViewModel GetOrCreateLayoutSession(", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "GetOrCreateLayoutSession not found");
        int methodEnd = src.IndexOf("\n    }", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart, "could not find the end of GetOrCreateLayoutSession");
        string body = src[methodStart..methodEnd];

        int loadAt = body.IndexOf("LayoutPersistence.LoadFromFile(", StringComparison.Ordinal);
        int sweepAt = body.IndexOf("SchematicToLayoutGenerator.RemoveRatsnestShapes(", StringComparison.Ordinal);
        Assert.True(loadAt >= 0, "no longer loads via LayoutPersistence.LoadFromFile");
        Assert.True(sweepAt >= 0, "no longer sweeps ratsnest shapes via RemoveRatsnestShapes");
        Assert.True(loadAt < sweepAt, "the ratsnest sweep must run AFTER the fresh load, not before");

        Assert.Contains("vm.IsDirty = true", body);
        Assert.Contains("Messages.Warning", body);
    }
}
