using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CircuitRF.Design.Results;
using CircuitRF.Ui.Messages;
using RfCore.Data;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Writes a run's analysis results as a single grouped .npy to a flat, shared results directory.
/// Framework-free — no Avalonia, no Skia. Testable headless.
///
/// <para><b>The path convention itself lives in <see cref="ResultsWriter"/>, in CircuitRF.Design.</b>
/// This type is the Messages-posting façade over it: it derives the schematic key, delegates the
/// write, and posts the success/warning line. See ResultsWriter's own header for why that split runs
/// where it does — headless callers (<c>circuitrf em</c>) must write the SAME file this does, and a
/// second copy of the convention is how the two would silently drift apart.</para>
///
/// Path convention: &lt;baseDir&gt;/results/&lt;schematicKey&gt;.npy — **one** file per run, containing
/// every analysis as a group (plus a <c>measurements</c> group). See docs/design/data-display.md §3,
/// and §7.0/results-dataset-layout.md, which record that the earlier per-schematic-SUBDIRECTORY layout
/// (&lt;schematicKey&gt;/run.npy) was flattened — a run writes directly into the shared results/ folder,
/// alongside every other schematic's results and any user-named baseline. Do not reintroduce the
/// subdirectory form.
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
    /// Resolves the directory that holds every schematic's flat results file — the READ-side companion
    /// to <see cref="WriteRun"/>'s <c>baseDir/results</c>. It MUST mirror where runs are actually written:
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
    /// Sanitizes a user- or key-derived results file name COMPONENT (never a path): strips path
    /// separators (on every platform, regardless of what <see cref="Path.GetInvalidFileNameChars"/>
    /// reports locally) and every other character the local filesystem disallows in a plain file name,
    /// replacing each with '_'. Leading/trailing whitespace is trimmed. Returns "" for a
    /// null/blank/all-invalid input — callers treat "" as "no override, use the default".
    /// </summary>
    public static string SanitizeFileNameComponent(string? raw)
        => ResultsWriter.SanitizeFileNameComponent(raw);

    /// <summary>
    /// Resolves the file name (no directory) a run writes to: the sanitized, ".npy"-suffixed
    /// <paramref name="fileNameOverride"/> when set, else "&lt;schematicKey&gt;.npy".
    /// </summary>
    public static string ResolveFileName(string? fileNameOverride, string schematicKey)
        => ResultsWriter.ResolveFileName(fileNameOverride, schematicKey);

    /// <summary>
    /// Writes one grouped .npy for the whole run directly under &lt;baseDir&gt;/results/ — a flat,
    /// shared directory holding every schematic's results plus any user-named baseline.
    ///
    /// Deletes ONLY the specific file about to be written, never every .npy in results/: the earlier
    /// per-schematic-subdirectory layout could safely wildcard-clear its own private directory, but a
    /// wildcard clear here would wipe every other schematic's results and every user-named baseline
    /// sitting in the same shared folder — the single most damaging regression a naive flattening could
    /// introduce.
    ///
    /// The prior per-cell ".source" collision marker has been dropped, not rehomed (R-res-0a):
    /// <see cref="SchematicKey"/> already disambiguates "cell" from "cell.view", and two cells cannot
    /// share a folder name, so the collision it guarded against (two different cells resolving to the
    /// same results file) is no longer reachable through normal use — a user who deliberately types the
    /// same override name for two different schematics is making a well-understood choice (silent
    /// overwrite is documented behavior for the default file too, R-res-3), not hitting a bug.
    ///
    /// I/O failures are caught and posted as warnings — never thrown.
    /// Returns the absolute path written; empty list on any early-out.
    /// </summary>
    public static IReadOnlyList<string> WriteRun(
        string        baseDir,
        string        schematicKey,
        DataSet?      grouped,
        IMessageSink? messages,
        string?       fileNameOverride = null)
    {
        var outcome = ResultsWriter.WriteRun(baseDir, schematicKey, grouped, fileNameOverride);

        if (outcome.Error is { } error)
        {
            messages?.Warning($"Results write failed: {error}");
            return [];
        }

        if (outcome.Written.Count == 0) return [];

        // The posted path is the FILE itself (not its containing directory) so "Reveal in
        // Finder/File Explorer" on this message selects the actual .npy, not just its folder.
        var absRunNpy = outcome.Written[0];
        messages?.Success($"Results written: {Path.GetFileNameWithoutExtension(absRunNpy)}", absRunNpy);

        return outcome.Written;
    }

    /// <summary>
    /// R-res-11 — migrates a workspace's results directory from the earlier per-schematic-
    /// SUBDIRECTORY layout (<c>results/&lt;schematicKey&gt;/run.npy</c>) to the current flat one
    /// (<c>results/&lt;schematicKey&gt;.npy</c>), on workspace open. Also removes the now-defunct
    /// <c>.source</c> collision marker (dropped, not rehomed — R-res-0a) wherever one is found, so no
    /// orphaned marker survives migration. Every touched subdirectory is removed once empty.
    ///
    /// Never overwrites an existing flat file — if <c>results/&lt;key&gt;.npy</c> already exists (e.g.
    /// the schematic was already re-run once under the new layout), the old subdirectory's copy is
    /// left in place and reported, rather than silently discarding either one.
    ///
    /// Safe to call on every workspace open: a workspace already on the flat layout has no
    /// subdirectories under results/ and this is a no-op.
    /// </summary>
    public static IReadOnlyList<string> MigrateOldLayout(string resultsRoot, IMessageSink? messages)
    {
        if (!Directory.Exists(resultsRoot)) return [];

        var migrated = new List<string>();

        foreach (var dir in Directory.GetDirectories(resultsRoot))
        {
            var key       = Path.GetFileName(dir);
            var oldNpy    = Path.Combine(dir, "run.npy");
            var marker    = Path.Combine(dir, ".source");
            var newNpy    = Path.Combine(resultsRoot, key + ".npy");

            if (File.Exists(oldNpy))
            {
                if (File.Exists(newNpy))
                {
                    messages?.Warning(
                        $"Migration: results/{key}/run.npy left in place — results/{key}.npy already exists",
                        dir);
                }
                else
                {
                    try
                    {
                        File.Move(oldNpy, newNpy);
                        migrated.Add(key);
                    }
                    catch (Exception ex)
                    {
                        messages?.Warning($"Migration: could not move {key}/run.npy: {ex.Message}", dir);
                    }
                }
            }

            if (File.Exists(marker))
                try { File.Delete(marker); } catch { /* best-effort cleanup */ }

            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch { /* leave it — not worth failing migration over a stubborn empty directory */ }
        }

        if (migrated.Count > 0)
            messages?.Success(
                $"Migrated {migrated.Count} results file(s) to the flat layout: {string.Join(", ", migrated)}",
                resultsRoot);

        return migrated;
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
