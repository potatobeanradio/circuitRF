using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Manages the per-session recovery directory for dirty scratch documents.
///
/// Layout: LocalApplicationData/circuitRF/recovery/&lt;session-id&gt;/
///   Each dirty scratch doc is serialized as &lt;sanitized-name&gt;.csch (atomic write).
///   The session dir is created lazily on the first AutoSave call.
///
/// Lifecycle:
///   AutoSave   — called periodically for each dirty scratch doc (atomic write).
///   ClearDoc   — called when a scratch doc is saved/materialized (removes one file).
///   ClearSession — called on clean exit (removes the whole session dir).
///
/// At next launch, FindPriorSessions returns session dirs left by ungraceful exits.
/// LoadSession deserializes their .csch files for restore-offer.
/// </summary>
public sealed class RecoveryManager
{
    // Under the one per-user state directory, so a tool can redirect it (see CircuitRF.Ui.AppDataRoot).
    private static string RecoveryRoot => AppDataRoot.SubDir("recovery");

    /// <summary>Absolute path of this session's recovery directory.</summary>
    public string SessionDir { get; }

    public RecoveryManager()
    {
        // 12-char hex session id — collision-safe for a local app.
        var sessionId = Guid.NewGuid().ToString("N")[..12];
        SessionDir = Path.Combine(RecoveryRoot, sessionId);
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Atomically serializes one dirty scratch document to the recovery dir.
    /// Non-critical: silently swallows I/O errors so a failing autosave never
    /// interrupts editing.
    /// </summary>
    public void AutoSave(SchematicDocument doc)
    {
        try
        {
            Directory.CreateDirectory(SessionDir);
            var file = Path.Combine(SessionDir, SafeFileName(doc.Id));
            var tmp  = file + ".tmp";
            SchematicPersistence.SaveToFile(tmp, doc.ViewModel.EditModel, doc.Id);
            File.Move(tmp, file, overwrite: true);
        }
        catch { /* autosave is non-critical — editing must never be interrupted */ }
    }

    // ── Clear ─────────────────────────────────────────────────────────────────

    /// <summary>Removes the recovery file for a document that was cleanly saved.</summary>
    public void ClearDoc(SchematicDocument doc)
    {
        try
        {
            var file = Path.Combine(SessionDir, SafeFileName(doc.Id));
            if (File.Exists(file)) File.Delete(file);
            PruneEmptySessionDir();
        }
        catch { }
    }

    /// <summary>Removes the entire session recovery directory on clean exit.</summary>
    public void ClearSession()
    {
        try
        {
            if (Directory.Exists(SessionDir))
                Directory.Delete(SessionDir, recursive: true);
        }
        catch { }
    }

    // ── Discovery ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns paths of prior-session recovery dirs (any session directory under the
    /// recovery root that is NOT this session's dir and contains at least one .csch file).
    /// Called at launch to find ungraceful-exit remnants.
    /// </summary>
    public static IReadOnlyList<string> FindPriorSessions(string currentSessionDir)
    {
        var result = new List<string>();
        if (!Directory.Exists(RecoveryRoot)) return result;

        try
        {
            foreach (var dir in Directory.GetDirectories(RecoveryRoot))
            {
                if (string.Equals(dir, currentSessionDir, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (Directory.GetFiles(dir, "*.csch").Length > 0)
                    result.Add(dir);
            }
        }
        catch { }

        return result;
    }

    /// <summary>
    /// Loads all .csch files from <paramref name="sessionDir"/> as (displayName, model) pairs.
    /// Skips corrupt files silently.
    /// </summary>
    public static IReadOnlyList<(string Name, SchematicEditModel Model)> LoadSession(
        string sessionDir)
    {
        var result = new List<(string, SchematicEditModel)>();
        if (!Directory.Exists(sessionDir)) return result;

        try
        {
            foreach (var file in Directory.GetFiles(sessionDir, "*.csch"))
            {
                try
                {
                    var (model, _, cellName) = SchematicPersistence.LoadFromFile(file);
                    var name = !string.IsNullOrWhiteSpace(cellName)
                        ? cellName
                        : Path.GetFileNameWithoutExtension(file);
                    result.Add((name, model));
                }
                catch { /* skip corrupt or unrecognised files */ }
            }
        }
        catch { }

        return result;
    }

    /// <summary>Deletes a prior-session directory after restore or discard.</summary>
    public static void DeletePriorSession(string sessionDir)
    {
        try { Directory.Delete(sessionDir, recursive: true); }
        catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Returns a safe filename for the doc (replaces invalid path characters with '_').
    private static string SafeFileName(string docId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(docId.Length + 5);
        foreach (var c in docId)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        if (sb.Length == 0) sb.Append("recovery");
        sb.Append(".csch");
        return sb.ToString();
    }

    // Removes the session dir if it is now empty (after the last doc was saved/discarded).
    private void PruneEmptySessionDir()
    {
        if (!Directory.Exists(SessionDir)) return;
        try
        {
            if (Directory.GetFileSystemEntries(SessionDir).Length == 0)
                Directory.Delete(SessionDir);
        }
        catch { }
    }
}
