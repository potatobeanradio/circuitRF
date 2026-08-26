// The results-folder CONVENTION — where a run's grouped `.npy` lands, and what it is called.
//
// ── WHICH HALF MOVED, AND WHY (brief-cli-em-verb.md R-emcli-3, answered here because the next
//    person will ask) ────────────────────────────────────────────────────────────────────────────
//
// THE CONVENTION MOVED. It was not left in src/Ui with the EM caller taking a folder as a parameter.
//
// R-emcli-7 requires that `circuitrf em` and the Simulate button write the SAME file — R-em-19's
// predictable path is what keeps a schematic's SnP reference valid across re-runs. If
// `<baseDir>/results/`, the filename sanitiser and the scoped-delete rule stayed on the UI side, the
// CLI would need its own copy of all three, and the day someone changed one of them the two halves
// would write to different places without anything failing. That is exactly the duplication
// R-emcli-1 forbids, on the one path whose stability the requirement is about.
//
// WHAT DID NOT MOVE: the MESSAGE POSTING. CircuitRF.Ui.Schematic.RunResultsWriter is still the entry
// point every schematic run calls; it delegates the file work here and keeps its IMessageSink
// success/warning lines, its SchematicKey derivation, its ResolveResultsRoot, and its old-layout
// migration. A headless run has no Messages region to post to.

using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Design.Results;

/// <summary>The outcome of one <see cref="ResultsWriter.WriteRun"/>: the absolute paths written
/// (empty on any early-out or failure) and, when nothing was written because the write FAILED, the
/// reason. <c>Error</c> is null for a clean early-out — a <c>DataSet</c> with no groups is not an
/// error, it is nothing to write.</summary>
public readonly record struct ResultsWriteOutcome(IReadOnlyList<string> Written, string? Error);

/// <summary>
/// Writes a run's analysis results as a single grouped <c>.npy</c> to a flat, shared results
/// directory. Framework-free by construction — this project references no UI framework at all.
///
/// <para>Path convention: <c>&lt;baseDir&gt;/results/&lt;key&gt;.npy</c> — <b>one</b> file per run,
/// containing every analysis as a group (plus a <c>measurements</c> group). See
/// docs/design/data-display.md §3 and §7.0/results-dataset-layout.md, which record that the earlier
/// per-schematic-SUBDIRECTORY layout (<c>&lt;key&gt;/run.npy</c>) was flattened. Do not reintroduce
/// the subdirectory form.</para>
/// </summary>
public static class ResultsWriter
{
    /// <summary>The flat, shared directory a run writes into.</summary>
    public static string ResultsDirectory(string baseDir) => Path.Combine(baseDir, "results");

    /// <summary>
    /// Sanitizes a user- or key-derived results file name COMPONENT (never a path): strips path
    /// separators (on every platform, regardless of what <see cref="Path.GetInvalidFileNameChars"/>
    /// reports locally) and every other character the local filesystem disallows in a plain file name,
    /// replacing each with '_'. Leading/trailing whitespace is trimmed. Returns "" for a
    /// null/blank/all-invalid input — callers treat "" as "no override, use the default".
    /// </summary>
    public static string SanitizeFileNameComponent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
            sb.Append(c is '/' or '\\' || Array.IndexOf(invalid, c) >= 0 ? '_' : c);

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Resolves the file name (no directory) a run writes to: the sanitized, ".npy"-suffixed
    /// <paramref name="fileNameOverride"/> when set, else "&lt;key&gt;.npy".
    /// </summary>
    public static string ResolveFileName(string? fileNameOverride, string key)
    {
        var sanitized = SanitizeFileNameComponent(fileNameOverride);
        var baseName  = sanitized.Length == 0 ? key : sanitized;
        return baseName.EndsWith(".npy", StringComparison.OrdinalIgnoreCase)
            ? baseName
            : baseName + ".npy";
    }

    /// <summary>
    /// Writes one grouped <c>.npy</c> for the whole run directly under
    /// <c>&lt;baseDir&gt;/results/</c> — a flat, shared directory holding every schematic's results
    /// plus any user-named baseline.
    ///
    /// <para>Deletes ONLY the specific file about to be written, never every <c>.npy</c> in
    /// <c>results/</c>: the earlier per-schematic-subdirectory layout could safely wildcard-clear its
    /// own private directory, but a wildcard clear here would wipe every other schematic's results
    /// and every user-named baseline sitting in the same shared folder — the single most damaging
    /// regression a naive flattening could introduce.</para>
    ///
    /// <para>The prior per-cell <c>.source</c> collision marker has been dropped, not rehomed
    /// (R-res-0a): the caller's key already disambiguates "cell" from "cell.view", and two cells
    /// cannot share a folder name, so the collision it guarded against is no longer reachable
    /// through normal use — a user who deliberately types the same override name for two different
    /// schematics is making a well-understood choice (silent overwrite is documented behavior for
    /// the default file too, R-res-3), not hitting a bug.</para>
    ///
    /// <para><b>Never throws.</b> An I/O failure comes back as
    /// <see cref="ResultsWriteOutcome.Error"/> so the caller decides how to report it — the UI posts
    /// a warning to the Messages region, the CLI prints to stderr.</para>
    /// </summary>
    public static ResultsWriteOutcome WriteRun(
        string   baseDir,
        string   key,
        DataSet? grouped,
        string?  fileNameOverride = null)
    {
        if (grouped is null || grouped.Groups.Count == 0) return new([], null);

        try
        {
            var dir = ResultsDirectory(baseDir);
            Directory.CreateDirectory(dir);

            var fileName = ResolveFileName(fileNameOverride, key);
            var runNpy   = Path.Combine(dir, fileName);

            // R-res-0: scoped delete — only the file we are about to overwrite, never a wildcard scan
            // of the shared results/ directory.
            if (File.Exists(runNpy))
                File.Delete(runNpy);

            DataSetExporter.Export(grouped, runNpy, ExportFormat.Npy);

            return new([Path.GetFullPath(runNpy)], null);
        }
        catch (Exception ex)
        {
            return new([], ex.Message);
        }
    }
}
