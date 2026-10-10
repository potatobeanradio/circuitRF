// The picture the user gave us, whichever way they gave it — brief-img-2-image-source-and-kind.md R-im2-1 (D3, D4).
//
// Three origins, one record: a FILE (the bytes read once), BYTES (a clipboard picture the GUI encoded as PNG, with a
// suggested name), or a PLACED BITMAP — a layout's BitmapShape or a schematic's EditableBitmap, whose path resolves
// exactly as the primitive resolves its own: relative to the document, else absolute. An unresolved placed path is the
// refusal the primitive's Resolve Path… answers, and the sentence says so.
//
// The record keeps the ORIGINAL bytes as well as the decoded raster: ImageKeep copies the bytes the user gave, not a
// re-encoding, and the SHA-256 is of those bytes, so a re-run on the same file compares equal.

using System.Security.Cryptography;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Imaging;

/// <summary>Where a picture came from.</summary>
public enum ImageOrigin { File, Bytes, LayoutBitmap, SchematicBitmap }

/// <summary>Where a placed bitmap sits, when the picture is one.</summary>
/// <param name="Document">The <c>.clay</c> or <c>.csch</c> it is placed in, absolute.</param>
public abstract record ImagePlacement(string Document);

/// <summary>A <see cref="BitmapShape"/>'s placement: its rect in the layout's DBU and its layer.</summary>
public sealed record LayoutBitmapPlacement(string Document, long X, long Y, long W, long H, LayerKey Layer)
    : ImagePlacement(Document);

/// <summary>An <see cref="EditableBitmap"/>'s placement: its rect in schematic units and its rotation.</summary>
public sealed record SchematicBitmapPlacement(string Document, double X, double Y, double Width, double Height, double RotationDeg)
    : ImagePlacement(Document);

/// <summary>One picture, decoded, with what identifies it.</summary>
public sealed class ImageSource
{
    private ImageSource(ImageOrigin origin, byte[] bytes, RasterImage raster, string format, string originalName,
                        string? path, ImagePlacement? placement)
    {
        Origin = origin;
        OriginalBytes = bytes;
        Raster = raster;
        Format = format;
        OriginalName = originalName;
        Path = path;
        Placement = placement;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public ImageOrigin Origin { get; }

    /// <summary>The bytes as given — what <see cref="ImageKeep"/> copies.</summary>
    public byte[] OriginalBytes { get; }

    /// <summary>The decoded picture (IM-1): upright, sRGB, flattened onto white, reduced to 50 MP when larger.</summary>
    public RasterImage Raster { get; }

    /// <summary>The format the bytes announce — <c>png</c>, <c>jpg</c>, <c>bmp</c>, <c>gif</c> or <c>webp</c> — which is
    /// the extension a kept copy is written with, whatever the original file was called.</summary>
    public string Format { get; }

    /// <summary>The SHA-256 of <see cref="OriginalBytes"/>, lower-case hex.</summary>
    public string Sha256 { get; }

    /// <summary>The original file NAME — never a path (no personal paths in a workspace, D4).</summary>
    public string OriginalName { get; }

    /// <summary>The file read, absolute; null for clipboard bytes. Never written anywhere.</summary>
    public string? Path { get; }

    /// <summary>The placed bitmap's placement; null unless the picture is one.</summary>
    public ImagePlacement? Placement { get; }

    /// <summary>The name a clipboard picture is given when the caller suggests none.</summary>
    public const string PastedName = "pasted_image.png";

    /// <summary>Reads a picture file.</summary>
    public static ImageSourceResult FromFile(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        byte[] bytes;
        try { bytes = File.ReadAllBytes(full); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ImageSourceResult.Refused($"The picture '{System.IO.Path.GetFileName(full)}' could not be opened: {e.Message}");
        }
        return Decode(ImageOrigin.File, bytes, System.IO.Path.GetFileName(full), full, null);
    }

    /// <summary>A clipboard picture: the PNG bytes the GUI encoded from the clipboard bitmap, and the name to suggest
    /// for it (a path is cut to its file name).</summary>
    public static ImageSourceResult FromBytes(byte[] bytes, string? suggestedName = null)
    {
        string name = string.IsNullOrWhiteSpace(suggestedName) ? PastedName : System.IO.Path.GetFileName(suggestedName);
        return Decode(ImageOrigin.Bytes, bytes, name, null, null);
    }

    /// <summary>The picture a layout's <see cref="BitmapShape"/> shows, placed in <paramref name="clayPath"/>.</summary>
    public static ImageSourceResult FromLayoutBitmap(string clayPath, BitmapShape bitmap)
    {
        string doc = System.IO.Path.GetFullPath(clayPath);
        var placement = new LayoutBitmapPlacement(doc, bitmap.X, bitmap.Y, bitmap.W, bitmap.H, bitmap.Layer);
        return FromPlaced(ImageOrigin.LayoutBitmap, doc, bitmap.ImagePathRef, placement);
    }

    /// <summary>The picture a schematic's <see cref="EditableBitmap"/> shows, placed in <paramref name="cschPath"/>.</summary>
    public static ImageSourceResult FromSchematicBitmap(string cschPath, EditableBitmap bitmap)
    {
        string doc = System.IO.Path.GetFullPath(cschPath);
        var placement = new SchematicBitmapPlacement(doc, bitmap.X, bitmap.Y, bitmap.Width, bitmap.Height, bitmap.RotationDeg);
        return FromPlaced(ImageOrigin.SchematicBitmap, doc, bitmap.ImagePath, placement);
    }

    /// <summary>The refusal for a placed bitmap whose picture is not where it says — the case its own Resolve Path…
    /// answers.</summary>
    public static string UnresolvedRefusal(string reference, string documentDir) =>
        string.IsNullOrWhiteSpace(reference)
            ? "This bitmap names no picture file. Right-click it and use Resolve Path… to point it at the file."
            : $"The picture this bitmap shows is not at '{reference}' (resolved against {documentDir}). " +
              "Right-click it and use Resolve Path… to point it at the file.";

    private static ImageSourceResult FromPlaced(ImageOrigin origin, string document, string reference, ImagePlacement placement)
    {
        string dir = System.IO.Path.GetDirectoryName(document)!;
        if (string.IsNullOrWhiteSpace(reference)) return ImageSourceResult.Refused(UnresolvedRefusal(reference, dir));
        // The primitive's own rule: a rooted reference wins, a relative one is relative to the document.
        string resolved = CircuitRF.Core.RefPath.Resolve(dir, reference);
        if (!File.Exists(resolved)) return ImageSourceResult.Refused(UnresolvedRefusal(reference, dir));

        byte[] bytes;
        try { bytes = File.ReadAllBytes(resolved); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ImageSourceResult.Refused($"The picture '{System.IO.Path.GetFileName(resolved)}' could not be opened: {e.Message}");
        }
        return Decode(origin, bytes, System.IO.Path.GetFileName(resolved), resolved, placement);
    }

    private static ImageSourceResult Decode(ImageOrigin origin, byte[] bytes, string name, string? path, ImagePlacement? placement)
    {
        var decoded = RasterImage.Decode(bytes);
        if (!decoded.Ok) return ImageSourceResult.Refused(decoded.Refusal!);
        // Decode accepted it, so it carries one of the five signatures.
        string format = RasterImage.DetectFormat(bytes) ?? "png";
        return ImageSourceResult.Read(new ImageSource(origin, bytes, decoded.Image!, format, name, path, placement));
    }
}

/// <summary>A picture, or the sentence saying why there is none.</summary>
public sealed class ImageSourceResult
{
    private ImageSourceResult(ImageSource? source, string? refusal) { Source = source; Refusal = refusal; }

    public ImageSource? Source { get; }
    public string? Refusal { get; }
    public bool Ok => Source is not null;

    public static ImageSourceResult Read(ImageSource source) => new(source, null);
    public static ImageSourceResult Refused(string refusal) => new(null, refusal);
}
