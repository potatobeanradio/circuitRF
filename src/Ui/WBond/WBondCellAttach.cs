using System;
using System.IO;
using CircuitRF.Ui.Layout;
using CircuitRF.WBond;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// The half of <see cref="WBondCell"/> that could not cross the UI firewall — attaching a resolved
/// <c>.wBond</c> to a layout EDITING SESSION.
///
/// <para>Finding the file is a document rule and lives below the wall with the rest of them
/// (<c>src/Design/Layout/WBondCell.cs</c>, and its header says why). What is here is the one step
/// that needs a view model, which is what "draws, docks or observes a canvas" means.</para>
/// </summary>
public static class WBondCellAttach
{
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

        var (path, note) = WBondCell.Resolve(absClayPath);
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
