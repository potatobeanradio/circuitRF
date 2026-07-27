using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Commands.Layout;
using CircuitRF.Ui.Renderers;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Layout bitmaps (docs/sonnet-briefs/brief-layout-bitmaps-and-insert-button.md) — placement (drag-drop
/// and the toolbar Insert Bitmap button share <see cref="PlaceBitmap"/>), the right-click Resolve
/// Path…/Refresh Cache actions, and the R-bmp Locked exclusion from move/scale. Selection/hit-test/
/// scale-handle-visibility/booleans-exclusion for <see cref="BitmapShape"/> live in the main VM file
/// and <c>LayoutEditorViewModel.Booleans.cs</c>/<c>.Scale.cs</c> respectively — this file is placement
/// + the two context-menu actions + the Locked filter those other files' move/scale paths call into.
/// </summary>
public sealed partial class LayoutEditorViewModel
{
    // ── R-bmp-4: viewport-relative sizing — never a fixed DBU constant, since DBU are nanometres ──

    /// <summary>A newly-placed bitmap's long edge spans ~25% of the CURRENT viewport width, preserving
    /// the source image's pixel aspect ratio. Falls back to a 4:3 box (still 25%-of-viewport sized) and
    /// a Messages note when the file can't be decoded — insertion never silently fails.</summary>
    private (long W, long H) ComputeBitmapPlacementSize(string path, double viewportWidthDbu)
    {
        double longEdge = Math.Max(1.0, viewportWidthDbu * 0.25);

        if (BitmapCache.TryGetPixelSize(path) is { } px && px.Width > 0 && px.Height > 0)
        {
            double w, h;
            if (px.Width >= px.Height) { w = longEdge; h = longEdge * px.Height / px.Width; }
            else                       { h = longEdge; w = longEdge * px.Width / px.Height; }
            return ((long)Math.Round(w), Math.Max(1, (long)Math.Round(h)));
        }

        _messageSink?.Warning($"Couldn't read image dimensions for '{Path.GetFileName(path)}' — placed as a 4:3 box.");
        return (Math.Max(1, (long)Math.Round(longEdge)), Math.Max(1, (long)Math.Round(longEdge * 3.0 / 4.0)));
    }

    /// <summary>The one place a <see cref="BitmapShape"/> is constructed and inserted — shared by
    /// canvas drag-drop and the Insert Bitmap toolbar button (R-bmp-5) so there is no second placement
    /// path to keep in sync. <paramref name="originX"/>/<paramref name="originY"/> is the shape's
    /// top-left (min) corner, already whatever the caller decided (drop point, or centre-minus-half-size).</summary>
    private void PlaceBitmap(string path, double originX, double originY, double viewportWidthDbu)
    {
        if (string.IsNullOrEmpty(path)) return;
        var (w, h) = ComputeBitmapPlacementSize(path, viewportWidthDbu);
        var (sx, sy) = LayoutSnapping.SnapPoint(originX, originY, Model.SnapDbu, false);
        var shape = new BitmapShape { Layer = CurrentLayerKey, ImagePathRef = path, X = sx, Y = sy, W = w, H = h, Opacity = 1.0 };
        Execute(new AddShapeCommand(Model, shape));
        SetSelection([Model.Shapes.Count - 1]);
        RebuildOverlay();
    }

    /// <summary>Canvas drag-drop of an image file — top-left corner lands at the drop point (matches
    /// the symbol editor's <c>DropBitmap</c> convention exactly; only the SIZE rule differs, since
    /// symbol-editor local units aren't viewport-relative).</summary>
    public void DropBitmap(string path, double worldX, double worldY, double viewportWidthDbu)
        => PlaceBitmap(path, worldX, worldY, viewportWidthDbu);

    /// <summary>The Insert Bitmap toolbar button (R-bmp-5) — centres the placed rect on the current
    /// viewport centre, unlike drag-drop's drop-point-as-top-left.</summary>
    public void InsertBitmapAtViewportCenter(string path, double centerX, double centerY, double viewportWidthDbu)
    {
        if (string.IsNullOrEmpty(path)) return;
        var (w, h) = ComputeBitmapPlacementSize(path, viewportWidthDbu);
        PlaceBitmap(path, centerX - w / 2.0, centerY - h / 2.0, viewportWidthDbu);
    }

    // ── Right-click: Resolve Path… / Refresh Cache ────────────────────────────────────────────────

    /// <summary>Topmost <see cref="BitmapShape"/> under the click, within tolerance — mirrors the
    /// symbol editor's <c>OnPointerRightPressed</c> hit-test (any bitmap under the click, not just an
    /// already-selected one).</summary>
    public (int ShapeIndex, string Path, bool IsBroken)? FindBitmapForContextMenu(double wx, double wy, long tolDbu)
    {
        long px = (long)Math.Round(wx), py = (long)Math.Round(wy);
        foreach (var idx in LayoutHitTest.HitStack(Model, Technology, px, py, tolDbu))
        {
            if (Model.Shapes[idx] is not BitmapShape bmp) continue;
            bool isBroken = string.IsNullOrEmpty(bmp.ImagePathRef) || !File.Exists(bmp.ImagePathRef);
            return (idx, bmp.ImagePathRef, isBroken);
        }
        return null;
    }

    public void ResolveBitmapPath(int shapeIndex, string newPath)
    {
        if (shapeIndex < 0 || shapeIndex >= Model.Shapes.Count) return;
        if (Model.Shapes[shapeIndex] is not BitmapShape bmp) return;
        BitmapCache.Invalidate(bmp.ImagePathRef);
        Execute(new SetShapeFieldCommand<string>(Model, "Resolve Bitmap Path", bmp.ImagePathRef, newPath, v => bmp.ImagePathRef = v));
    }

    public void RefreshBitmapCache(int shapeIndex)
    {
        if (shapeIndex < 0 || shapeIndex >= Model.Shapes.Count) return;
        if (Model.Shapes[shapeIndex] is not BitmapShape bmp) return;
        BitmapCache.Invalidate(bmp.ImagePathRef);
        Model.NotifyChanged();
    }

    // ── R-bmp: Locked blocks move and scale, never selection ─────────────────────────────────────

    private static bool IsLockedBitmap(LayoutShape s) => s is BitmapShape { Locked: true };

    /// <summary>The subset of <paramref name="indices"/> a move or scale operation should actually
    /// act on — a locked bitmap stays selected (and its outline still renders) but is excluded here,
    /// so it simply doesn't move/scale while the rest of a multi-selection does.</summary>
    private IReadOnlyList<int> MovableSelectedIndices(IEnumerable<int> indices) =>
        indices.Where(i => i >= 0 && i < Model.Shapes.Count && !IsLockedBitmap(Model.Shapes[i])).ToList();
}
