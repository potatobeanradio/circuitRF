using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>One row of the Change Technology picker: what it is called, and which file it is.</summary>
public sealed record TechChoice(string Label, string AbsolutePath);

/// <summary>
/// Finds the technologies a layout can be retargeted onto, for
/// <see cref="ChangeTechnologyDialog"/>. Separated from the dialog so the RULE — which files are
/// offered, in what order, and how two same-named ones are told apart — can be driven by a test
/// without standing up a window.
/// </summary>
public static class WorkspaceTechnologyChoices
{
    /// <summary>
    /// Every <c>.ctech</c> under the workspace root, <c>tech/</c> first (it is the conventional home,
    /// so it stays at the top of the list) and the rest after, each alphabetical.
    ///
    /// <para>Labelled by the technology's own <c>Name</c>, falling back to the filename stem. Two
    /// technologies can legitimately carry the same name — a copy taken into a cell folder is the
    /// obvious case — so <b>any label that is not unique gains its workspace-relative folder</b>. A
    /// picker with two identical rows is worse than a long one: it makes the choice unmakeable rather
    /// than merely wordy, and it is exactly what widening the search from one folder to the whole tree
    /// makes possible.</para>
    ///
    /// <para><paramref name="rootDir"/> null (a loose file with no ancestor workspace) yields nothing,
    /// leaving "(Workspace default)" and Browse… — unchanged from before.</para>
    /// </summary>
    public static IReadOnlyList<TechChoice> Enumerate(string? rootDir, string? techDir)
    {
        var choices = new List<TechChoice>();
        if (rootDir is not { Length: > 0 } || !Directory.Exists(rootDir)) return choices;

        string[] paths;
        try
        {
            // IgnoreInaccessible: one unreadable subfolder somewhere in a workspace must not empty the
            // whole picker — it would look exactly like the bug this method exists to fix.
            paths = Directory.GetFiles(rootDir, "*.ctech", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible    = true,
                MatchCasing           = MatchCasing.CaseInsensitive,
            });
        }
        catch (IOException)              { return choices; }
        catch (UnauthorizedAccessException) { return choices; }

        bool InTechFolder(string p) =>
            techDir is { Length: > 0 } &&
            string.Equals(Path.GetDirectoryName(p), techDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                          StringComparison.OrdinalIgnoreCase);

        var ordered = paths
            .OrderByDescending(InTechFolder)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var names = ordered.ToDictionary(
            p => p,
            p => TryReadTechName(p) ?? Path.GetFileNameWithoutExtension(p),
            StringComparer.OrdinalIgnoreCase);

        var duplicated = names.Values
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in ordered)
        {
            string name = names[path];
            string label = duplicated.Contains(name) ? $"{name}  —  {RelativeFolder(rootDir, path)}" : name;
            choices.Add(new TechChoice(label, path));
        }
        return choices;
    }

    /// <summary>The file's folder relative to the workspace root, or "." for the root itself — the
    /// short, meaningful half of the path, since the root is the same for every row.</summary>
    private static string RelativeFolder(string rootDir, string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (dir is null) return ".";
            var rel = Path.GetRelativePath(rootDir, dir);
            return rel is "" or "." ? "." : rel;
        }
        catch { return "."; }
    }


    /// <summary>The technology's own <c>Name</c>, or null when the file cannot be read at all.</summary>
    private static string? TryReadTechName(string path)
    {
        try
        {
            var tech = TechPersistence.LoadFromFile(path);
            return tech.Name is { Length: > 0 } n ? n : null;
        }
        catch
        {
            return null; // corrupt/unreadable — the caller falls back to the filename stem, never throws
        }
    }
}
