// What kind of file is this, decided from its BYTES (docs/sonnet-briefs/
// brief-PL1-component-library-import.md R-PL1-28).
//
// The first bytes decide; the extension only breaks a tie the content left open. Never the containing
// folder's name. The extension alone is not enough to dispatch on — `.lib` names several unrelated
// formats and `.txt` names several more — and a folder name is not evidence about a file at all.
//
// GerberFileClassifier follows the same pattern on the board side.

using System.Text;

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>
/// What a file handed to the component import is.
///
/// <para>Each member names the SHAPE of the format — an S-expression symbol library, a line-record
/// symbol library, an XML library — which is the same naming the rest of this folder uses.</para>
/// </summary>
public enum ComponentFileKind
{
    /// <summary>Nothing recognisable.</summary>
    Unknown,

    /// <summary>An S-expression symbol library — <c>.kicad_sym</c>.</summary>
    SymbolSexpr,

    /// <summary>A standalone S-expression footprint — <c>.kicad_mod</c>, either epoch.</summary>
    FootprintSexpr,

    /// <summary>The older one-record-per-line symbol library — <c>.lib</c>.</summary>
    SymbolLegacyText,

    /// <summary>The XML library holding packages, symbols and device sets — <c>.lbr</c>.</summary>
    LibraryXml,

    /// <summary>A whole BOARD, which is a different import (File ▸ Import ▸ Board…).</summary>
    Board,

    /// <summary>A three-dimensional model — out of scope for this phase and reported as skipped.</summary>
    Model3D,

    /// <summary>A dimensioned drawing (DXF, Gerber). Reported as skipped rather than offered as a
    /// component — see <see cref="ComponentFolderScan.Summarize"/> and R-PL1-30.</summary>
    Drawing,

    /// <summary>Text circuitRF has no reader for.</summary>
    UnreadableText,

    /// <summary>Not text at all.</summary>
    Binary,
}

/// <summary>Classifies one file by its content.</summary>
public static class ComponentClassifier
{
    /// <summary>How much of a file's head is enough to recognise it. Every marker below is in the
    /// first record of its format; reading more would only slow a scan of a folder holding hundreds
    /// of files.</summary>
    internal const int SniffBytes = 8192;

    /// <summary>The extensions this phase reads. A refusal lists them (R-PL1-29) so the message names
    /// what would work rather than only what did not.</summary>
    public static readonly IReadOnlyList<string> ReadableExtensions =
        [".kicad_sym", ".kicad_mod", ".lib", ".lbr"];

    public static ComponentFileKind Classify(string path)
    {
        byte[] head;
        try
        {
            using var stream = File.OpenRead(path);
            head = new byte[Math.Min(SniffBytes, (int)Math.Min(stream.Length, SniffBytes))];
            int read = stream.Read(head, 0, head.Length);
            if (read < head.Length) Array.Resize(ref head, read);
        }
        catch (IOException) { return ComponentFileKind.Unknown; }
        catch (UnauthorizedAccessException) { return ComponentFileKind.Unknown; }

        return ClassifyContent(head, Path.GetExtension(path));
    }

    /// <summary>The classification itself, over bytes — so a test states the CONTENT it is testing
    /// rather than writing a file first.</summary>
    public static ComponentFileKind ClassifyContent(ReadOnlySpan<byte> head, string extension = "")
    {
        if (head.Length == 0) return ComponentFileKind.Unknown;
        if (LooksBinary(head)) return Model3DByContent(head) ?? ComponentFileKind.Binary;

        string text = Decode(head);
        string trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');

        // ── The three text markers that decide it outright ──────────────────────────────────────
        if (trimmed.StartsWith("EESchema-LIBRARY", StringComparison.OrdinalIgnoreCase))
            return ComponentFileKind.SymbolLegacyText;

        if (trimmed.StartsWith('('))
        {
            string root = RootTagOf(trimmed);
            return root switch
            {
                "kicad_symbol_lib" => ComponentFileKind.SymbolSexpr,
                "footprint" or "module" => ComponentFileKind.FootprintSexpr,
                "kicad_pcb" => ComponentFileKind.Board,
                // A lone (symbol …) is a fragment of a library rather than a library. Accepted:
                // ComponentSymbolSexprReader reads it directly.
                "symbol" => ComponentFileKind.SymbolSexpr,
                _ => ComponentFileKind.UnreadableText,
            };
        }

        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith('<'))
            return ClassifyXml(text);

        // ── Everything else, by the shape of its first record ───────────────────────────────────
        if (trimmed.StartsWith("#VRML", StringComparison.OrdinalIgnoreCase)) return ComponentFileKind.Model3D;
        if (trimmed.StartsWith("ISO-10303-21", StringComparison.OrdinalIgnoreCase)) return ComponentFileKind.Model3D;
        if (trimmed.StartsWith("solid ", StringComparison.OrdinalIgnoreCase)) return ComponentFileKind.Model3D;

        // A DXF opens with a group code 0 introducing SECTION; a Gerber with an %FS / %MO parameter or
        // a G04 comment. Both are dimensioned DRAWINGS as far as a component import is concerned.
        if (trimmed.StartsWith("999") || DxfHead(trimmed)) return ComponentFileKind.Drawing;
        if (trimmed.StartsWith("%FS", StringComparison.Ordinal) || trimmed.StartsWith("%MO", StringComparison.Ordinal)
            || trimmed.StartsWith("G04", StringComparison.Ordinal))
            return ComponentFileKind.Drawing;

        // The extension is the LAST word, never the first — and only for a file whose content said
        // nothing at all.
        return extension.ToLowerInvariant() switch
        {
            ".step" or ".stp" or ".wrl" or ".iges" or ".igs" => ComponentFileKind.Model3D,
            ".dxf" or ".gbr" or ".gtl" or ".gbl" => ComponentFileKind.Drawing,
            _ => ComponentFileKind.UnreadableText,
        };
    }

    /// <summary>
    /// The XML library, recognised by its STRUCTURE rather than by its root element's name: a
    /// <c>&lt;library&gt;</c> holding <c>&lt;packages&gt;</c>, <c>&lt;symbols&gt;</c>,
    /// <c>&lt;devicesets&gt;</c> or <c>&lt;drawing&gt;</c>. A file whose root element is named
    /// anything at all still classifies, which is what <see cref="ComponentLibraryXmlReader"/> then
    /// reads.
    /// </summary>
    private static ComponentFileKind ClassifyXml(string head)
    {
        bool library = head.Contains("<library", StringComparison.OrdinalIgnoreCase);
        bool content = head.Contains("<packages", StringComparison.OrdinalIgnoreCase)
                    || head.Contains("<symbols", StringComparison.OrdinalIgnoreCase)
                    || head.Contains("<devicesets", StringComparison.OrdinalIgnoreCase)
                    || head.Contains("<drawing", StringComparison.OrdinalIgnoreCase);
        return library && content ? ComponentFileKind.LibraryXml : ComponentFileKind.UnreadableText;
    }

    private static bool DxfHead(string trimmed)
    {
        // "  0\nSECTION" with any leading padding the format allows on a group code line.
        int nl = trimmed.IndexOf('\n');
        if (nl < 0 || nl > 8) return false;
        return trimmed[..nl].Trim() == "0"
            && trimmed[(nl + 1)..].TrimStart().StartsWith("SECTION", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Binary 3D containers, so the skipped summary can say "three-dimensional model" rather
    /// than the less specific "binary format".</summary>
    private static ComponentFileKind? Model3DByContent(ReadOnlySpan<byte> head)
        => head.Length >= 4 && head[0] == 0x67 && head[1] == 0x6C && head[2] == 0x54 && head[3] == 0x46
            ? ComponentFileKind.Model3D            // "glTF"
            : null;

    private static bool LooksBinary(ReadOnlySpan<byte> head)
    {
        // A NUL in the first block is the classic tell; so is a high proportion of control bytes.
        int control = 0;
        foreach (byte b in head)
        {
            if (b == 0) return true;
            if (b < 0x09 || (b > 0x0D && b < 0x20)) control++;
        }
        return control * 100 > head.Length * 5;
    }

    private static string Decode(ReadOnlySpan<byte> head)
    {
        try { return Encoding.UTF8.GetString(head); }
        catch (ArgumentException) { return Encoding.Latin1.GetString(head); }
    }

    private static string RootTagOf(string trimmed)
    {
        int i = 1;
        while (i < trimmed.Length && char.IsWhiteSpace(trimmed[i])) i++;
        int start = i;
        while (i < trimmed.Length && !char.IsWhiteSpace(trimmed[i]) && trimmed[i] != '(' && trimmed[i] != ')') i++;
        return trimmed[start..i];
    }
}
