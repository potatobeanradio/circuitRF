// Where the picture is kept — brief-img-2-image-source-and-kind.md R-im2-4 (D4).
//
// Documents reference images by path (the bitmap primitives' rule), so the source picture is COPIED into the target
// cell folder as `<cell>.source.<ext>` — the original bytes, under the format they announce (a pasted picture is PNG).
// A file of that name holding the same bytes is reused; one holding different bytes is never overwritten, the copy
// going to `<cell>.source-2.<ext>`, `-3`, and so on, each checked the same way.
//
// Called only from the user's Create: a cancelled dialog leaves nothing behind, because nothing before this call writes.

using System.Security.Cryptography;

namespace CircuitRF.Design.Imaging;

/// <summary>The kept copy of a picture.</summary>
/// <param name="FullPath">Where it is, absolute.</param>
/// <param name="Ref">Its path relative to the cell folder, forward slashes — its file name.</param>
/// <param name="Reused">A file with the same bytes was already there and nothing was written.</param>
public sealed record KeptPicture(string FullPath, string Ref, bool Reused)
{
    /// <summary>The stored reference to the kept copy from a document in <paramref name="documentDir"/> — what an
    /// underlay's image path and the provenance carry.</summary>
    public string RefFrom(string documentDir) =>
        CircuitRF.Core.RefPath.ToStored(System.IO.Path.GetRelativePath(documentDir, FullPath));
}

public static class ImageKeep
{
    /// <summary>The kept copy's file name for the <paramref name="n"/>th distinct picture (1 is unsuffixed).</summary>
    public static string FileName(string cellName, string format, int n = 1) =>
        n <= 1 ? $"{cellName}.source.{format}" : $"{cellName}.source-{n}.{format}";

    /// <summary>Copies <paramref name="source"/>'s original bytes into <paramref name="cellDir"/>, or finds them there
    /// already.</summary>
    public static KeptPicture Into(string cellDir, string cellName, ImageSource source)
    {
        Directory.CreateDirectory(cellDir);
        for (int n = 1; ; n++)
        {
            string name = FileName(cellName, source.Format, n);
            string path = Path.Combine(cellDir, name);
            if (File.Exists(path))
            {
                if (Sha256Of(path) == source.Sha256) return new KeptPicture(Path.GetFullPath(path), name, Reused: true);
                continue;
            }
            WriteAtomically(path, source.OriginalBytes);
            return new KeptPicture(Path.GetFullPath(path), name, Reused: false);
        }
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>A sibling temporary renamed over the target, as <c>AtomicFile</c> writes text: a crash mid-write
    /// leaves no half picture under the name a document will reference.</summary>
    private static void WriteAtomically(string path, byte[] bytes)
    {
        string tmp = path + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: false);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }
}
