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
    /// obvious case — so <b>any label that is not unique gains its workspace-relative FILE path</b>. A
    /// picker with two identical rows is worse than a long one: it makes the choice unmakeable rather
    /// than merely wordy, and it is exactly what widening the search from one folder to the whole tree
    /// makes possible.</para>
    ///
    /// <para><b>The folder alone was not enough</b> (owner report, 2026-09-09). A Save As from the
    /// technology editor writes a second <c>.ctech</c> carrying the same internal <c>Name</c>, and the
    /// obvious place to put it is beside the original — so both rows disambiguated to the same folder
    /// and the picker was back to two identical labels. The file name is the half that always differs,
    /// because two files cannot share a path. Every row also carries its full path as a tooltip in the
    /// dialog itself, which is the answer for the case no label can carry.</para>
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
            string label = duplicated.Contains(name) ? $"{name}  —  {RelativeFilePath(rootDir, path)}" : name;
            choices.Add(new TechChoice(label, path));
        }
        return choices;
    }

    /// <summary>
    /// Which offered row stands for the technology the layout is using RIGHT NOW — an index into
    /// <paramref name="choices"/>, or -1 meaning "(Workspace default)".
    ///
    /// <para><b>The picker used to open on "(Workspace default)" for every layout</b>, whatever the
    /// layout's own <c>TechRef</c> said. That is the wrong answer to the only question the dialog
    /// exists to answer, and it is wrong most visibly for an imported board: a Gerber import writes
    /// its own <c>.ctech</c> beside the cell and points the new <c>.clay</c> at it, so opening this
    /// dialog to check WHICH technology a board uses reported the workspace's instead. Confirming
    /// that reading then made it true — <c>TechRef</c> is cleared and the layout follows the
    /// workspace default from then on, which in a workspace holding several imports is another
    /// board's technology.</para>
    ///
    /// <para>A null (or empty) <paramref name="layoutTechRef"/> IS the workspace default (L0c's
    /// convention, §5A.2), so it answers -1 without looking at anything else.
    /// <paramref name="resolvedTechPath"/> is what the layout's ref actually resolved to — compared
    /// as a full path, case-insensitively, exactly as <see cref="TechnologyCache"/> keys it. A ref
    /// that resolved to a file OUTSIDE the workspace matches no row and answers -1; the dialog adds
    /// a row for it rather than pre-selecting a technology the layout is not using.</para>
    /// </summary>
    public static int IndexOfCurrent(
        IReadOnlyList<TechChoice> choices, string? layoutTechRef, string? resolvedTechPath)
    {
        if (layoutTechRef is not { Length: > 0 })    return -1;
        if (resolvedTechPath is not { Length: > 0 }) return -1;

        string target;
        try   { target = Path.GetFullPath(resolvedTechPath); }
        catch { return -1; }

        for (int i = 0; i < choices.Count; i++)
        {
            string candidate;
            try   { candidate = Path.GetFullPath(choices[i].AbsolutePath); }
            catch { continue; }

            if (string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    /// <summary>The file relative to the workspace root — the short, meaningful half of the path,
    /// since the root is the same for every row, and the only half that is guaranteed to differ
    /// between two rows. Falls back to the bare file name if the path cannot be made relative.</summary>
    private static string RelativeFilePath(string rootDir, string filePath)
    {
        try
        {
            var rel = Path.GetRelativePath(rootDir, filePath);
            return rel is "" or "." ? Path.GetFileName(filePath) : rel;
        }
        catch { return Path.GetFileName(filePath); }
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
