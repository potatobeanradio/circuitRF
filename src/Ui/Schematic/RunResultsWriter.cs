using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CircuitRF.Ui.Messages;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Writes per-run analysis results as .npy files to a stable, collision-safe path.
/// Framework-free — no Avalonia, no Skia. Testable headless.
///
/// Path convention: &lt;baseDir&gt;/results/&lt;schematicKey&gt;/&lt;analysisName&gt;.npy
/// See docs/design/data-display.md §3 / 7.0.
/// </summary>
public static class RunResultsWriter
{
    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the schematic key derived from the document's file path.
    /// For cell-homed schematics:  cell name, or "cell.view" when the view stem differs.
    /// For loose schematics:       file stem.
    /// For scratch (filePath null): Sanitized scratchId.
    /// </summary>
    public static string SchematicKey(string? filePath, string scratchId)
    {
        if (filePath is null)
            return Sanitize(scratchId);

        var parentDir = Path.GetDirectoryName(filePath);
        if (parentDir is not null &&
            string.Equals(Path.GetFileName(parentDir), "schematic",
                StringComparison.OrdinalIgnoreCase))
        {
            var cellDir = Path.GetDirectoryName(parentDir);
            if (cellDir is not null)
            {
                var cell = Path.GetFileName(cellDir)!;
                var view = Path.GetFileNameWithoutExtension(filePath);
                return string.Equals(view, cell, StringComparison.OrdinalIgnoreCase)
                    ? cell
                    : $"{cell}.{view}";
            }
        }

        return Path.GetFileNameWithoutExtension(filePath);
    }

    /// <summary>
    /// Returns the stable owner identity used for collision detection.
    /// Cell-homed:  absolute path of the cell folder (above schematic/).
    /// Loose:       absolute path of the .csch file.
    /// Scratch:     "scratch:" + scratchId.
    /// </summary>
    public static string OwnerIdentity(string? filePath, string scratchId)
    {
        if (filePath is null)
            return $"scratch:{scratchId}";

        var parentDir = Path.GetDirectoryName(filePath);
        if (parentDir is not null &&
            string.Equals(Path.GetFileName(parentDir), "schematic",
                StringComparison.OrdinalIgnoreCase))
        {
            var cellDir = Path.GetDirectoryName(parentDir);
            if (cellDir is not null)
                return Path.GetFullPath(cellDir);
        }

        return Path.GetFullPath(filePath);
    }

    /// <summary>
    /// Writes one run.npy for the whole run under &lt;baseDir&gt;/results/&lt;schematicKey&gt;/.
    /// Collision-check: if the directory already has a .source from a different owner,
    /// posts a warning and returns without writing.  Clears stale .npy files each run.
    /// I/O failures are caught and posted as warnings — never thrown.
    /// Returns the absolute path of run.npy written; empty list on any early-out.
    /// </summary>
    public static IReadOnlyList<string> WriteRun(
        string        baseDir,
        string        schematicKey,
        string        ownerIdentity,
        DataSet?      grouped,
        IMessageSink? messages)
    {
        if (grouped is null || grouped.Groups.Count == 0) return [];

        try
        {
            var dir    = Path.Combine(baseDir, "results", schematicKey);
            var source = Path.Combine(dir, ".source");

            // ── Collision check ───────────────────────────────────────────────
            if (Directory.Exists(dir) && File.Exists(source))
            {
                var existing = File.ReadAllText(source, Encoding.UTF8).Trim();
                if (!string.Equals(existing, ownerIdentity, StringComparison.OrdinalIgnoreCase))
                {
                    messages?.Warning(
                        $"results/{schematicKey}/ belongs to a different cell — " +
                        "rename one cell to avoid a results collision",
                        dir);
                    return [];
                }
            }

            Directory.CreateDirectory(dir);
            File.WriteAllText(source, ownerIdentity, Encoding.UTF8);

            // ── Clear stale outputs ───────────────────────────────────────────
            foreach (var stale in Directory.GetFiles(dir, "*.npy"))
                File.Delete(stale);

            // ── Write single grouped file ─────────────────────────────────────
            var runNpy = Path.Combine(dir, "run.npy");
            DataSetExporter.Export(grouped, runNpy, ExportFormat.Npy);

            messages?.Success(
                $"Results written: {schematicKey} ({grouped.Groups.Count} group(s))",
                dir);

            return [Path.GetFullPath(runNpy)];
        }
        catch (Exception ex)
        {
            messages?.Warning($"Results write failed: {ex.Message}");
            return [];
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Replaces invalid filename characters with '_'. Mirrors RecoveryManager.SafeFileName
    // but without the .csch suffix.
    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.Length == 0 ? "analysis" : sb.ToString();
    }
}
