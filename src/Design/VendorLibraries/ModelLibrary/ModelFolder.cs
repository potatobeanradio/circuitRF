using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CircuitRF.Design.VendorLibraries.ModelLibrary;

/// <summary>
/// The model-library folder format's names, and the two questions every later brief asks of a path
/// (brief vendorlib-6 §3, R-vl6-1c): is this a model folder, and which model folder owns it.
///
/// <para><b>A model folder is also a cell folder.</b> It is what Export to Library packages a cell
/// into, so every verb that reads a cell reads one; <c>part.cpart</c> is the only thing that makes it
/// a model folder as well.</para>
/// </summary>
public static partial class ModelFolder
{
    /// <summary>The model manifest. Its presence is what makes a folder a model folder.</summary>
    public const string PartFileName    = "part.cpart";
    /// <summary>The library manifest at a library's root. Optional.</summary>
    public const string LibraryFileName = "library.clib";
    /// <summary>The model's own files: <c>.va</c> and what it includes, <c>.cmodel</c>, <c>.sNp</c>, SPICE files.</summary>
    public const string ModelsFolder    = "models";
    /// <summary>Sub-cell folders the model's schematic places.</summary>
    public const string CellsFolder     = "cells";
    /// <summary>Optional documents the palette's ? button opens.</summary>
    public const string DocsFolder      = "docs";
    public const string NetlistExtension = ".cnl";

    /// <summary>Does <paramref name="dir"/> hold a <c>part.cpart</c>?</summary>
    public static bool Is(string dir) => File.Exists(Path.Combine(dir, PartFileName));

    /// <summary>The model's netlist: <c>&lt;PartNumber&gt;.cnl</c> beside its <c>part.cpart</c>.</summary>
    public static string NetlistPath(string modelDir, string partNumber)
        => Path.Combine(modelDir, partNumber + NetlistExtension);

    /// <summary>
    /// The nearest folder at or above <paramref name="path"/> that is a model folder, or null. A file
    /// starts the walk at its own folder. Brief 8 resolves a model's relative references against it.
    /// </summary>
    public static string? OwningModelFolder(string path)
    {
        string full = Path.GetFullPath(path);
        string? dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        for (; dir is not null; dir = Path.GetDirectoryName(dir))
            if (Is(dir)) return Path.TrimEndingDirectorySeparator(dir);
        return null;
    }

    /// <summary>
    /// Every model folder under <paramref name="root"/> (the root itself included), in ordinal path
    /// order. A model folder is not looked inside — its <c>cells/</c> are sub-cells, not parts — and a
    /// folder whose name starts with <c>.</c> is never walked.
    /// </summary>
    public static IReadOnlyList<string> Under(string root)
    {
        var found = new List<string>();
        Walk(Path.GetFullPath(root), found);
        found.Sort(StringComparer.Ordinal);
        return found;

        static void Walk(string dir, List<string> found)
        {
            if (Is(dir)) { found.Add(dir); return; }
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            foreach (string sub in subs)
                if (!Path.GetFileName(sub).StartsWith('.')) Walk(sub, found);
        }
    }
}

/// <summary>
/// <c>PublishedHash</c> (brief vendorlib-6 §3b, R-vl6-1b) — the one function Export (10), Edit (12) and
/// Validate (9) all call, so "edited since exported" means one thing everywhere.
///
/// <para><b>The rule, as the format page publishes it.</b> Every file under the model folder except
/// <c>part.cpart</c> at its root, and except operating-system litter (<c>.DS_Store</c>,
/// <c>Thumbs.db</c>, <c>desktop.ini</c>) and anything under a <c>.git</c> folder. For each, in ordinal
/// order of its path relative to the folder with <c>/</c> separators, one line
/// <c>&lt;sha256 hex of its content&gt;  &lt;relative path&gt;\n</c> — two spaces, lower-case hex.
/// The hash is <c>sha256:</c> and the lower-case hex SHA-256 of those lines as UTF-8. A text file's
/// content has every CR LF replaced by LF first, so a checkout that changes line endings is not an
/// edit.</para>
///
/// <para><b>Why a list of per-file hashes rather than one stream.</b> A concatenation would hash
/// <c>ab</c>+<c>c</c> and <c>a</c>+<c>bc</c> alike; a line per file with its path cannot, a renamed
/// file changes the hash, and the list is short enough that a script reproduces it in a few
    /// lines.</para>
/// </summary>
public static partial class ModelPartHash
{
    public const string Prefix = "sha256:";

    /// <summary>Extensions whose content is CR LF-normalised before hashing; <c>.sNp</c> matches any N.</summary>
    public static readonly IReadOnlyList<string> TextExtensions =
        [".cnl", ".csch", ".csym", ".ccell", ".va", ".vams", ".lib", ".cir", ".sub", ".txt", ".md"];

    private static readonly HashSet<string> Litter = new(StringComparer.OrdinalIgnoreCase)
        { ".DS_Store", "Thumbs.db", "desktop.ini" };

    [GeneratedRegex(@"^\.s\d+p$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TouchstoneExtension();

    public static bool IsText(string path)
    {
        string ext = Path.GetExtension(path);
        // `.ccell` is a file NAMED .ccell — GetExtension gives the whole name, which is what is wanted.
        return TextExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase) || TouchstoneExtension().IsMatch(ext);
    }

    /// <summary>The files the hash covers, as relative <c>/</c>-separated paths in ordinal order.</summary>
    public static IReadOnlyList<string> Files(string modelDir)
    {
        string root = Path.GetFullPath(modelDir);
        var files = new List<string>();
        foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (rel == ModelFolder.PartFileName) continue;
            if (Litter.Contains(Path.GetFileName(rel))) continue;
            if (rel.Split('/').SkipLast(1).Any(seg => seg.Equals(".git", StringComparison.OrdinalIgnoreCase))) continue;
            files.Add(rel);
        }
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <summary><c>sha256:&lt;hex&gt;</c> over the model folder at <paramref name="modelDir"/>.</summary>
    public static string Compute(string modelDir)
    {
        string root = Path.GetFullPath(modelDir);
        var list = new StringBuilder();
        foreach (string rel in Files(root))
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(root, rel));
            if (IsText(rel)) bytes = NormaliseLineEndings(bytes);
            list.Append(Convert.ToHexStringLower(SHA256.HashData(bytes))).Append("  ").Append(rel).Append('\n');
        }
        return Prefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(list.ToString())));
    }

    /// <summary>Every CR LF pair becomes LF; a lone CR is content and stays.</summary>
    public static byte[] NormaliseLineEndings(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)'\r') < 0) return bytes;
        var outBytes = new List<byte>(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == '\r' && i + 1 < bytes.Length && bytes[i + 1] == '\n') continue;
            outBytes.Add(bytes[i]);
        }
        return [.. outBytes];
    }
}
