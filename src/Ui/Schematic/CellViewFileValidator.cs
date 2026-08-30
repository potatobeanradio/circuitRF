using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Answers "would this file survive being adopted as a cell's schematic / symbol / layout view?"
/// before anything is created on disk.
///
/// <para>The gate exists because a Known File is an arbitrary path the user bookmarked — nothing has
/// ever read it. "Copy to Workspace as Cell…" would otherwise build a cell folder around a file the
/// editor then refuses to open, leaving the user with a broken cell to delete by hand and no
/// explanation. So the check runs FIRST and its failure text is what the user is told.</para>
///
/// <para>Two steps, and the first is the one an extension alone cannot do. Every circuitRF view file
/// is JSON with no cross-format type discriminator, and <c>System.Text.Json</c> ignores unknown
/// members by default — so a <c>.clay</c> renamed to <c>.csch</c> deserializes CLEANLY into an empty
/// schematic. Requiring a key that only that format writes is what separates the formats; running
/// the format's own reader afterwards is what catches malformed JSON, a truncated file and a
/// format_version from a newer build.</para>
/// </summary>
public static class CellViewFileValidator
{
    /// <summary>
    /// The cell view a file's extension claims to be, or null when the extension is not one of the
    /// three a cell folder has a home for. Lexical only — nothing is read.
    /// </summary>
    public static ViewType? ViewTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".csch" => ViewType.Schematic,
            ".csym" => ViewType.Symbol,
            ".clay" => ViewType.Layout,
            _       => null,
        };

    /// <summary>
    /// A JSON property every file of that format writes and the other two never do. Non-nullable
    /// collections on the file DTOs, so they are present even in a brand-new empty view — which is
    /// what makes their ABSENCE evidence rather than a false alarm on a sparse file.
    /// </summary>
    private static string RequiredKey(ViewType type) => type switch
    {
        ViewType.Schematic => "Components",
        ViewType.Symbol    => "Primitives",
        ViewType.Layout    => "Shapes",
        _                  => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>
    /// Returns null when <paramref name="path"/> reads back as a well-formed
    /// <paramref name="viewType"/> view, otherwise a sentence saying what is wrong with it — shown
    /// to the user verbatim, so it names the defect and not the call that raised it.
    /// </summary>
    public static string? DescribeDefect(string path, ViewType viewType)
    {
        if (!File.Exists(path))
            return "the file is no longer there.";

        string text;
        try
        {
            // Gzip-sniffing read: .clay is the format that may be compressed, and one reader for all
            // three costs nothing while a plain ReadAllText would report a gzipped layout as garbage.
            text = GzipTextFile.ReadAllTextAutoGzip(path);
        }
        catch (Exception ex)
        {
            return $"it could not be read ({ex.Message}).";
        }

        if (string.IsNullOrWhiteSpace(text))
            return "the file is empty.";

        JsonNode? root;
        try   { root = JsonNode.Parse(text); }
        catch (JsonException ex) { return $"it is not valid JSON ({ex.Message})."; }

        if (root is not JsonObject obj)
            return "it is not a circuitRF view file (its contents are not a JSON object).";

        string key = RequiredKey(viewType);
        bool hasKey = false;
        foreach (var kv in obj)
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) { hasKey = true; break; }

        if (!hasKey)
            return $"it does not look like a {CellFolder.ViewExtension(viewType)} view "
                 + $"(no '{key}' section) — check the file is really a {DisplayName(viewType)}.";

        // The format's own reader is the authority on everything past the shape check: format_version
        // ceilings, enum converters, and any per-format validation those readers grow later.
        try
        {
            switch (viewType)
            {
                case ViewType.Schematic: SchematicPersistence.LoadFromFile(path); break;
                case ViewType.Symbol:    SymbolPersistence.LoadFromFile(path);    break;
                case ViewType.Layout:    LayoutPersistence.LoadFromFile(path);    break;
            }
        }
        catch (Exception ex)
        {
            return $"it could not be loaded as a {DisplayName(viewType)} ({ex.Message}).";
        }

        return null;
    }

    private static string DisplayName(ViewType type) => type switch
    {
        ViewType.Schematic => "schematic",
        ViewType.Symbol    => "symbol",
        ViewType.Layout    => "layout",
        _                  => "view",
    };
}
