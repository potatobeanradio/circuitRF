using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CircuitRF.Ui.Messages;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Writes a run's analysis results as a single grouped .npy to a stable, collision-safe path.
/// Framework-free — no Avalonia, no Skia. Testable headless.
///
/// Path convention: &lt;baseDir&gt;/results/&lt;schematicKey&gt;/run.npy — **one** file per run, containing
/// every analysis as a group (plus a <c>measurements</c> group). See docs/design/data-display.md §3,
/// and §7.0 which explicitly records that a per-analysis <c>&lt;analysisName&gt;.npy</c> scheme was an
/// earlier plan and is NOT what ships — do not reintroduce it.
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
    /// Resolves the directory that holds per-run results subfolders — the READ-side companion to
    /// <see cref="WriteRun"/>'s <c>baseDir/results</c>. It MUST mirror where runs are actually written:
    /// the workspace root when a workspace is open, otherwise the scratch recovery-session dir (so a
    /// scratch simulation's results are discoverable in the Data Display without saving anything).
    /// </summary>
    public static string ResolveResultsRoot(string? workspaceCwsPath, string scratchSessionDir)
    {
        if (workspaceCwsPath is not null)
        {
            var wsDir = Path.GetDirectoryName(workspaceCwsPath);
            if (wsDir is not null)
                return Path.Combine(wsDir, "results");
        }
        return Path.Combine(scratchSessionDir, "results");
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

            // Record the owner RELATIVE to the workspace root (baseDir) when it lives inside it, so moving
            // the whole workspace — which relocates baseDir, results/, and the cells together — keeps the
            // identity stable and never looks like a collision.
            var ownerNorm = NormalizeOwnerIdentity(ownerIdentity, baseDir);

            // ── Collision check ───────────────────────────────────────────────
            if (Directory.Exists(dir) && File.Exists(source))
            {
                var existing = File.ReadAllText(source, Encoding.UTF8).Trim();
                if (!SameOwner(existing, ownerNorm))
                {
                    messages?.Warning(
                        $"results/{schematicKey}/ belongs to a different cell — " +
                        "rename one cell to avoid a results collision",
                        dir);
                    return [];
                }
            }

            Directory.CreateDirectory(dir);
            File.WriteAllText(source, ownerNorm, Encoding.UTF8);

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

    /// <summary>
    /// Normalizes an owner identity for storage/comparison. An owner that lives INSIDE the workspace
    /// (<paramref name="baseDir"/>) is recorded as a path RELATIVE to baseDir, so a whole-workspace move
    /// keeps it stable. Owners outside baseDir (e.g. a loose file elsewhere) and the "scratch:" sentinel
    /// are kept verbatim.
    /// </summary>
    internal static string NormalizeOwnerIdentity(string identity, string baseDir)
    {
        if (!Path.IsPathRooted(identity)) return identity;   // "scratch:…" or already relative
        string rel;
        try { rel = Path.GetRelativePath(Path.GetFullPath(baseDir), Path.GetFullPath(identity)); }
        catch { return identity; }
        // Inside the workspace (no "../", not re-rooted) → use the relative form.
        return !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel)
            ? rel
            : identity;
    }

    /// <summary>
    /// True when an owner whose normalized identity is <paramref name="normalizedNew"/> may write to a
    /// results dir currently marked <paramref name="stored"/>.
    /// </summary>
    internal static bool SameOwner(string stored, string normalizedNew)
    {
        if (string.Equals(stored, normalizedNew, StringComparison.OrdinalIgnoreCase))
            return true;

        // Migration: a results dir written before identities were workspace-relative carries a legacy
        // ABSOLUTE marker. After a workspace move that marker points at the OLD location and cannot be
        // compared. If the cell being run lives INSIDE this workspace (normalizedNew is a relative path),
        // it legitimately owns these results here — adopt them (a moved workspace, not a collision). A
        // genuinely different owner from OUTSIDE the workspace keeps an absolute identity and still warns.
        bool storedIsLegacyAbsolute = Path.IsPathRooted(stored);
        bool newIsInsideWorkspace   = !Path.IsPathRooted(normalizedNew)
                                      && !normalizedNew.StartsWith("scratch:", StringComparison.Ordinal);
        return storedIsLegacyAbsolute && newIsInsideWorkspace;
    }

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
