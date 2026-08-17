using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.WBond;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// WB40 — finding the <c>.wBond</c> that belongs to a cell, and attaching it to that cell's layout
/// session.
///
/// <h3>The layout of a wirebond cell</h3>
/// <code>
/// &lt;workspace&gt;/&lt;cell&gt;/
/// ├── layout/&lt;cell&gt;.clay      artwork — pads, traces, die outline
/// ├── &lt;cell&gt;.wBond            the wires
/// └── schematic/…               unchanged
/// </code>
///
/// <para>The sidecar sits in the CELL folder, not in <c>layout/</c>, because it is not a view of the
/// cell in the way a schematic or a layout is — it is a second file the layout view draws over. That
/// is also why it is found by looking one level up from the <c>.clay</c> rather than beside it.</para>
///
/// <h3>Every outcome here is non-fatal</h3>
/// <para>A cell with no <c>.wBond</c> is the ordinary case and says nothing. A <c>.wBond</c> that
/// cannot be read is REPORTED and the layout still opens — WB35's "never fails, never substitutes",
/// applied at the one new place a wBond file is now read from.</para>
/// </summary>
public static class WBondCell
{
    /// <summary>
    /// The <c>.wBond</c> belonging to the cell that owns <paramref name="absClayPath"/>, or null.
    ///
    /// <para>Prefers <c>&lt;cell&gt;/&lt;cell&gt;.wBond</c>. Falls back to the single <c>*.wBond</c>
    /// in the cell folder when there is exactly one under a different name — a hand-named bond list
    /// dropped in beside the artwork is a normal thing for an assembly house to send. Two or more is
    /// ambiguous and resolves to none rather than to whichever sorts first.</para>
    /// </summary>
    public static string? FindFor(string? absClayPath)
    {
        if (absClayPath is not { Length: > 0 }) return null;

        // <cell>/layout/<cell>.clay → <cell>
        if (Path.GetDirectoryName(absClayPath) is not { } layoutDir) return null;
        if (Path.GetDirectoryName(layoutDir) is not { } cellDir) return null;
        if (!Directory.Exists(cellDir)) return null;

        string preferred = Path.Combine(cellDir, Path.GetFileName(cellDir) + ".wBond");
        if (File.Exists(preferred)) return preferred;

        var found = Directory.GetFiles(cellDir, "*.wBond", SearchOption.TopDirectoryOnly);
        return found.Length == 1 ? found[0] : null;
    }

    /// <summary>
    /// Reads the cell's <c>.wBond</c>, if it has one, and attaches it to <paramref name="vm"/>.
    /// </summary>
    /// <param name="report">
    /// Called with a human-readable reason when a file was found but could not be read. The layout
    /// still opens without wires — a bond list that will not parse is not a reason to withhold the
    /// artwork.
    /// </param>
    /// <returns>True when wires were attached.</returns>
    public static bool TryAttach(LayoutEditorViewModel vm, string? absClayPath, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(vm);

        if (FindFor(absClayPath) is not { } path) return false;

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
