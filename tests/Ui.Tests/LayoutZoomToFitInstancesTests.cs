using System.IO;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner report (2026-07-29): "Layout Editor 'Zoom to Fit' does not account for pCell extents."
/// Root cause: <c>LayoutCanvas.ZoomToFitInternal</c> unioned <see cref="LayoutGeometry.BboxOf"/> over
/// <c>Model.Shapes</c> only — it never looked at <c>Model.Instances</c> at all, PCell or otherwise. A
/// layout consisting solely of placed instances (which is exactly what a placed PCell is — see
/// <c>src/Ui/CLAUDE.md</c>'s own note: "a placed PCell is therefore an ORDINARY LayoutInstance pointing
/// at a generated cell folder") zoomed to an empty/undersized extent.
///
/// <c>LayoutCanvas</c> is a <c>Control</c> subclass and this project's tests must not call any Avalonia
/// runtime API (matching every prior Layout Editor phase's note on this), so the fix itself cannot be
/// exercised directly. Correctness rests on two things, mirroring the established pattern for this
/// class of untestable-control bug (see <c>PCellDoubleClickDispatchTests.cs</c>): (1) a structural
/// source-scan proving <c>ZoomToFitInternal</c>'s body actually unions <c>CellHierarchy.InstanceBbox</c>
/// over <c>Model.Instances</c>, not just shapes; and (2) a direct test of that same primitive
/// (<see cref="CellHierarchy.InstanceBbox"/>) against a REAL PCell instance, proving it returns the
/// generated cell's actual extent rather than an empty or placeholder bbox — the exact value the fixed
/// method now unions in.
/// </summary>
public sealed class LayoutZoomToFitInstancesTests : IDisposable
{
    private readonly string _root;

    public LayoutZoomToFitInstancesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-zoomfit-pcell-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ── CellHierarchy.InstanceBbox — the primitive the fix unions in ────────────────────────────

    [Fact]
    public void InstanceBbox_ForAPlacedPCellInstance_ReturnsTheGeneratedCellsRealExtent_NotEmpty()
    {
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", defaults, null, null, PCellLayerSelection.Default);

        var docBaseDir = Path.Combine(_root, "Doc", "layout");
        Directory.CreateDirectory(docBaseDir);
        var inst = new LayoutInstance { CellRef = Path.GetRelativePath(docBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0 };

        var bb = CellHierarchy.InstanceBbox(inst, docBaseDir);

        Assert.False(bb.IsEmpty);
        // A generated MLIN's own geometry (a real trace) is far larger than the small, fixed
        // placeholder box a BROKEN/unresolved reference would produce — proves this is the PCell's
        // real generated extent, not CellHierarchy.PlaceholderBbox silently standing in for it.
        Assert.True(bb.MaxX - bb.MinX > CellHierarchy.PlaceholderHalfExtentDbu * 2
                     || bb.MaxY - bb.MinY > CellHierarchy.PlaceholderHalfExtentDbu * 2,
            $"expected the real generated-cell extent, got a placeholder-sized bbox: {bb}");
    }

    [Fact]
    public void InstanceBbox_ForAPCellInstance_TranslatesWithPosition()
    {
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", defaults, null, null, PCellLayerSelection.Default);

        var docBaseDir = Path.Combine(_root, "Doc", "layout");
        Directory.CreateDirectory(docBaseDir);
        string cellRef = Path.GetRelativePath(docBaseDir, cellDir);

        var atOrigin = CellHierarchy.InstanceBbox(new LayoutInstance { CellRef = cellRef, X = 0, Y = 0, Mag = 1.0 }, docBaseDir);
        long dx = 50_000_000, dy = -20_000_000;
        var moved = CellHierarchy.InstanceBbox(new LayoutInstance { CellRef = cellRef, X = dx, Y = dy, Mag = 1.0 }, docBaseDir);

        Assert.Equal(atOrigin.MinX + dx, moved.MinX);
        Assert.Equal(atOrigin.MinY + dy, moved.MinY);
    }

    // ── Structural proof that ZoomToFitInternal actually unions instance bboxes now ──────────────

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    [Fact]
    public void ZoomToFitInternal_UnionsInstanceBboxes_NotJustShapeBboxes()
    {
        string src = ReadRepoFile(Path.Combine("src", "Ui", "Controls", "LayoutCanvas.cs"));

        int methodStart = src.IndexOf("private void ZoomToFitInternal(", System.StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "ZoomToFitInternal not found");
        int methodEnd = src.IndexOf("\n    }", methodStart, System.StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart, "could not find the end of ZoomToFitInternal");
        string body = src[methodStart..methodEnd];

        Assert.Contains("model.Shapes", body);
        Assert.Contains("model.Instances", body);
        Assert.Contains("CellHierarchy.InstanceBbox(", body);

        // The instance union must happen unconditionally alongside the shape union, not behind some
        // separate/optional path — both loops union into the SAME `bb` the viewport is computed from.
        int shapesUnionAt = body.IndexOf("model.Shapes", System.StringComparison.Ordinal);
        int instancesUnionAt = body.IndexOf("model.Instances", System.StringComparison.Ordinal);
        Assert.True(shapesUnionAt >= 0 && instancesUnionAt >= 0 && instancesUnionAt > shapesUnionAt);
    }
}
