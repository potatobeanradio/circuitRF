using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.WBond;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// WB40 — finding the <c>.wBond</c> that belongs to a layout, and attaching it to that layout's
/// editing session.
///
/// <h3>Where a wirebond cell's wires live</h3>
/// <code>
/// &lt;workspace&gt;/&lt;cell&gt;/
/// ├── layout/amp_v1.clay       artwork — pads, traces, die outline
/// ├── layout/amp_v1.wBond      the wires for THAT artwork  ← stem-paired
/// ├── layout/amp_v2.clay
/// └── schematic/…              unchanged
/// </code>
///
/// <para>A <c>.wBond</c> is an <b>attachment</b> (<c>workspace-and-project-tree.md</c> §1.2.1): a file
/// always used TOGETHER with one view file rather than instead of it. So it lives in that view's
/// sub-folder sharing that view file's stem, has no primacy, and is never named in <c>.ccell</c>.</para>
///
/// <h3>Why the stem, and not the cell root (revised 2026-08-17)</h3>
/// <para>Until 2026-08-17 the file sat at <c>&lt;cell&gt;/&lt;cell&gt;.wBond</c> and was found by looking
/// one level UP from the <c>.clay</c>. That placement assumed one <c>.wBond</c> per cell, which the model
/// never guaranteed — a cell may hold several <c>.clay</c> files, and wires are drawn over SPECIFIC pads
/// at specific coordinates, so "the cell's wires" stops being well formed the moment there are two
/// layouts. The old resolution could only guess (prefer the cell-named file, else the sole one, else give
/// up). Stem pairing makes the association defined instead of assumed.</para>
///
/// <h3>Every outcome here is non-fatal, and two of them must SPEAK</h3>
/// <list type="bullet">
///   <item>A layout with no wires is the ordinary case and says nothing.</item>
///   <item>A <c>.wBond</c> that cannot be READ is reported and the layout still opens — WB35's "never
///     fails, never substitutes".</item>
///   <item>A <b>legacy</b> cell-root <c>.wBond</c> is still read, and the move is named. Pre-2026-08-17
///     workspaces exist and silently dropping their wires is the one unacceptable outcome.</item>
///   <item>An <b>orphan</b> — wires in <c>layout/</c> pairing with no <c>.clay</c> — is reported. This is
///     not a nicety: it is the price of stem pairing. A <c>.clay</c> renamed in Finder detaches its wires,
///     and unlike every other Finder-edit failure mode (a "Not Found" glyph, a warning row) that one
///     would otherwise remove wires from a simulation the user believes includes them.</item>
/// </list>
/// </summary>
public static class WBondCell
{
    /// <summary>The wires belonging to one layout, and anything the user needs told about how they were
    /// (or were not) found.</summary>
    /// <param name="Path">The resolved <c>.wBond</c>, or null when this layout has none.</param>
    /// <param name="Note">
    /// A line to report, or null when there is nothing to say. Present on the legacy and orphan branches
    /// — both of which are cases where what is on disk does not match where wires now belong, and where
    /// saying nothing is how a user loses wires without noticing.
    /// </param>
    public readonly record struct Resolution(string? Path, string? Note);

    /// <summary>
    /// Resolves the <c>.wBond</c> attached to <paramref name="absClayPath"/>, in one pass, with the
    /// reason attached when there is one.
    ///
    /// <para>Order: the stem-paired file beside the <c>.clay</c>; then the legacy cell-root file (with a
    /// note naming the move); then nothing — but if <c>layout/</c> holds wires that pair with no
    /// <c>.clay</c> at all, that orphan is named rather than passed over in silence.</para>
    /// </summary>
    public static Resolution Resolve(string? absClayPath)
    {
        if (absClayPath is not { Length: > 0 }) return default;
        if (Path.GetDirectoryName(absClayPath) is not { } layoutDir) return default;

        string stem = Path.GetFileNameWithoutExtension(absClayPath);

        // 1. The attachment: same folder, same stem.
        string attached = Path.Combine(layoutDir, stem + ".wBond");
        if (File.Exists(attached)) return new Resolution(attached, null);

        // 2. Legacy: the pre-2026-08-17 cell-root sidecar. Still read, and the move named.
        if (LegacyRootPath(layoutDir) is { } legacy)
            return new Resolution(legacy,
                $"Wirebonds for this cell are at the cell root ('{Path.GetFileName(legacy)}'). They now " +
                $"belong in layout/ beside the .clay they are drawn over — move it to " +
                $"'layout/{stem}.wBond' so it stays attached if the cell gains a second layout.");

        // 3. Nothing attached. If there are wires in here that pair with nothing, say so — a renamed
        //    .clay detaches its wires, and that must not be silent.
        if (OrphanNote(layoutDir, stem) is { } orphan) return new Resolution(null, orphan);

        return default;
    }

    /// <summary>
    /// The <c>.wBond</c> attached to <paramref name="absClayPath"/>, or null.
    /// <para>The path half of <see cref="Resolve"/>, for callers with nowhere to report to.</para>
    /// </summary>
    public static string? FindFor(string? absClayPath) => Resolve(absClayPath).Path;

    /// <summary>
    /// The pre-2026-08-17 cell-root sidecar for the cell owning <paramref name="layoutDir"/>, or null.
    ///
    /// <para>Preserves the old resolution exactly — prefer <c>&lt;cell&gt;.wBond</c>, else the single
    /// <c>*.wBond</c> under any other name (a hand-named bond list from an assembly house), and treat two
    /// or more as ambiguous rather than picking whichever sorts first.</para>
    /// </summary>
    internal static string? LegacyRootPath(string layoutDir)
    {
        if (Path.GetDirectoryName(layoutDir) is not { } cellDir) return null;
        if (!Directory.Exists(cellDir)) return null;

        string preferred = Path.Combine(cellDir, Path.GetFileName(cellDir) + ".wBond");
        if (File.Exists(preferred)) return preferred;

        var found = Directory.GetFiles(cellDir, "*.wBond", SearchOption.TopDirectoryOnly);
        return found.Length == 1 ? found[0] : null;
    }

    /// <summary>
    /// A line naming the wires in <paramref name="layoutDir"/> that pair with no <c>.clay</c>, or null
    /// when there are none.
    /// </summary>
    private static string? OrphanNote(string layoutDir, string stem)
    {
        if (!Directory.Exists(layoutDir)) return null;

        var orphans = Directory
            .GetFiles(layoutDir, "*.wBond", SearchOption.TopDirectoryOnly)
            .Where(w => !File.Exists(Path.ChangeExtension(w, ".clay")))
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (orphans.Count == 0) return null;

        return $"{string.Join(", ", orphans)} {(orphans.Count == 1 ? "has" : "have")} no matching .clay, " +
               $"so {(orphans.Count == 1 ? "its wires are" : "their wires are")} not attached to any " +
               $"layout — including this one ('{stem}.clay'). Rename to '{stem}.wBond' to attach.";
    }

    /// <summary>
    /// Reads the <c>.wBond</c> attached to this layout, if there is one, and attaches it to
    /// <paramref name="vm"/>.
    /// </summary>
    /// <param name="report">
    /// Called with a human-readable line when there is something to say: a file that was found but could
    /// not be read, a legacy cell-root sidecar, or an orphan. The layout still opens in every case — a
    /// bond list that will not parse is not a reason to withhold the artwork.
    /// </param>
    /// <returns>True when wires were attached.</returns>
    public static bool TryAttach(LayoutEditorViewModel vm, string? absClayPath, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(vm);

        var (path, note) = Resolve(absClayPath);
        if (note is not null) report?.Invoke(note);
        if (path is null) return false;

        WBondDesign design;
        try
        {
            design = WBondIo.ReadFile(path);
        }
        catch (Exception ex)
        {
            report?.Invoke($"Wirebonds in '{Path.GetFileName(path)}' could not be read: {ex.Message}");
            return false;
        }

        vm.AttachWireDesign(design, path);
        return true;
    }
}
