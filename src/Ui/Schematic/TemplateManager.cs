using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// An in-memory representation of one <c>.canl</c> template loaded from disk.
/// Immutable after load.
/// </summary>
public sealed record AnalysisTemplate(
    string                      Name,
    string?                     Description,
    string                      FilePath,
    IReadOnlyList<Analysis>     Analyses,
    IReadOnlyList<Measurement>  Measurements);

/// <summary>
/// Loads, saves, lists, and deletes <c>.canl</c> multi-analysis template bundles.
/// Resolution chain: workspace/templates → user templates (→ bundled, none in v1).
/// Mirrors the <c>.ccolor</c> resolution chain.  Framework-free.
/// </summary>
public static class TemplateManager
{
    // ── Directories ───────────────────────────────────────────────────────────

    public static string UserTemplatesDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "circuitRF", "templates");

    /// <summary>Returns the workspace-scoped templates dir, or null if no workspace is open.</summary>
    public static string? WorkspaceTemplatesDir(string? workspaceDir) =>
        workspaceDir is null ? null : Path.Combine(workspaceDir, "templates");

    // ── Load all (resolution chain) ───────────────────────────────────────────

    /// <summary>
    /// Loads all readable <c>.canl</c> files from the resolution chain.
    /// Workspace templates shadow user templates with the same name.
    /// </summary>
    public static IReadOnlyList<AnalysisTemplate> LoadAll(string? workspaceDir)
    {
        var result = new List<AnalysisTemplate>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var wsDir = WorkspaceTemplatesDir(workspaceDir);
        if (wsDir is not null) AddFromDir(wsDir, result, seen);
        AddFromDir(UserTemplatesDir, result, seen);

        return result;
    }

    private static void AddFromDir(string dir, List<AnalysisTemplate> list, HashSet<string> seen)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var path in Directory.GetFiles(dir, "*.canl")
                                      .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
                var (name, desc, analyses, measurements) = AnalysisSerialization.DeserializeCanl(json);
                if (string.IsNullOrWhiteSpace(name))
                    name = Path.GetFileNameWithoutExtension(path);
                if (seen.Add(name.ToUpperInvariant()))
                    list.Add(new AnalysisTemplate(name, desc, path, analyses, measurements));
            }
            catch { /* corrupt or unreadable — skip gracefully */ }
        }
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes <paramref name="name"/>.canl to <paramref name="targetDir"/> atomically
    /// (temp + rename).  Creates the directory if it does not exist.
    /// Returns the absolute path written.
    /// </summary>
    public static string SaveTemplate(
        string                     targetDir,
        string                     name,
        string?                    description,
        IReadOnlyList<Analysis>    analyses,
        IReadOnlyList<Measurement> measurements)
    {
        Directory.CreateDirectory(targetDir);
        var fileName   = ToSafeFileName(name) + ".canl";
        var targetPath = Path.Combine(targetDir, fileName);
        var tmpPath    = targetPath + ".tmp";
        var json = AnalysisSerialization.SerializeCanl(name, description, analyses, measurements);
        File.WriteAllText(tmpPath, json, System.Text.Encoding.UTF8);
        File.Move(tmpPath, targetPath, overwrite: true);
        return targetPath;
    }

    /// <summary>Returns true if a <c>.canl</c> file for <paramref name="name"/> already exists in <paramref name="targetDir"/>.</summary>
    public static bool TemplateExists(string targetDir, string name)
        => File.Exists(Path.Combine(targetDir, ToSafeFileName(name) + ".canl"));

    // ── Delete ────────────────────────────────────────────────────────────────

    public static void DeleteTemplate(string filePath) => File.Delete(filePath);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ToSafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray());
    }
}
