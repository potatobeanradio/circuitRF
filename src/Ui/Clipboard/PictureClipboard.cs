// Pictures between the layout and the schematic editors. Each editor copies its own JSON, and each
// paste reads its own first; when the clipboard holds the OTHER editor's selection instead, the
// pictures in it — and only the pictures, the one kind both documents can hold — are converted and
// pasted. The copy references the SAME image file; everything else (size, position, opacity) is its
// own from then on, because the two are independent objects in different documents.
//
// The units are unrelated (DBU in a layout, schematic units in a schematic), so the group is sized by
// the DESTINATION's own placement rule — what dropping that image there would have given it — with
// the aspect of every picture and their arrangement relative to each other kept. A layout's Y runs
// up and a schematic's down, so the arrangement is flipped vertically on the way across.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input.Platform;

namespace CircuitRF.Ui.Clipboard;

public static class PictureClipboard
{
    /// <summary>A schematic picture's long edge when it is placed fresh — the drop rule's size.</summary>
    public const double SchematicPlacementLongEdge = 300.0;

    /// <summary>The fraction of the layout viewport's width a freshly placed picture's long edge spans.</summary>
    public const double LayoutPlacementViewportFraction = 0.25;

    /// <summary>The pictures in a layout fragment on the clipboard (a wBond mixed copy included), or
    /// empty when the clipboard holds none.</summary>
    public static async Task<List<BitmapShape>> LayoutPicturesAsync(IClipboard clipboard)
    {
        var payload = await LayoutClipboard.PasteAsync(clipboard);
        return payload is null ? [] : payload.Shapes.OfType<BitmapShape>().ToList();
    }

    /// <summary>The pictures in a schematic selection on the clipboard, or empty when it holds none.</summary>
    public static async Task<List<EditableBitmap>> SchematicPicturesAsync(IClipboard clipboard)
    {
        string? text = await ClipboardRead.TextAsync(clipboard);
        return SchematicPicturesIn(text);
    }

    public static List<EditableBitmap> SchematicPicturesIn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || LayoutFragment.TryDeserialize(text, out _)) return [];
        try { return SchematicPersistence.DeserializeSelection(text).CanvasObjs.OfType<EditableBitmap>().ToList(); }
        catch { return []; }
    }

    /// <summary>Layout pictures as schematic pictures, the group centred on (0,0) — the schematic's
    /// paste positions it from there.</summary>
    public static List<EditableBitmap> ToSchematic(IReadOnlyList<BitmapShape> shapes)
    {
        if (shapes.Count == 0) return [];
        double minX = shapes.Min(s => (double)s.X), maxX = shapes.Max(s => (double)s.X + s.W);
        double minY = shapes.Min(s => (double)s.Y), maxY = shapes.Max(s => (double)s.Y + s.H);
        double k  = SchematicPlacementLongEdge / Math.Max(Math.Max(maxX - minX, maxY - minY), 1.0);
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;

        return shapes.Select(s => new EditableBitmap
        {
            ImagePath    = s.ImagePathRef,
            Width        = s.W * k,
            Height       = s.H * k,
            X            =  (s.X + s.W / 2.0 - cx) * k,
            Y            = -(s.Y + s.H / 2.0 - cy) * k,
            Transparency = Math.Clamp(1 - s.Opacity, 0, 1),
        }).ToList();
    }

    /// <summary>Schematic pictures as layout pictures on <paramref name="layer"/>, the group centred on
    /// (<paramref name="centerX"/>, <paramref name="centerY"/>) in DBU.</summary>
    public static List<BitmapShape> ToLayout(IReadOnlyList<EditableBitmap> pictures, LayerKey layer,
                                             double centerX, double centerY, double viewportWidthDbu)
    {
        if (pictures.Count == 0) return [];
        double minX = pictures.Min(p => p.X - p.Width / 2),  maxX = pictures.Max(p => p.X + p.Width / 2);
        double minY = pictures.Min(p => p.Y - p.Height / 2), maxY = pictures.Max(p => p.Y + p.Height / 2);
        double longEdge = Math.Max(1.0, viewportWidthDbu * LayoutPlacementViewportFraction);
        double k  = longEdge / Math.Max(Math.Max(maxX - minX, maxY - minY), 1e-9);
        double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;

        return pictures.Select(p =>
        {
            long w = Math.Max(1, (long)Math.Round(p.Width * k));
            long h = Math.Max(1, (long)Math.Round(p.Height * k));
            double pcx = centerX + (p.X - cx) * k;
            double pcy = centerY - (p.Y - cy) * k;
            return new BitmapShape
            {
                Layer        = layer,
                ImagePathRef = p.ImagePath,
                X = (long)Math.Round(pcx - w / 2.0), Y = (long)Math.Round(pcy - h / 2.0), W = w, H = h,
                Opacity      = Math.Clamp(1 - p.Transparency, 0, 1),
            };
        }).ToList();
    }
}
