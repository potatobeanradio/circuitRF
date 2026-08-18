using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Views.WBond;

/// <summary>
/// The profile view's own context menu (wbond.md §6.4a) — group-scoped edits on whichever wire group
/// the pointer is over.
///
/// <para><b>Group-scoped, not selection-scoped, and that is the point.</b> The profile view draws one
/// curve per array, so the thing under the pointer there IS a group; "set the loop height" means
/// setting it for the group you are looking at, without first having to select it. The toolbar's own
/// transforms stay selection-scoped and are untouched.</para>
/// </summary>
public partial class WBondProfileView
{
    /// <summary>The group the current menu acts on, captured when the menu opens.</summary>
    private int _menuArray = -1;

    /// <summary>
    /// Where an <b>Add Vertex</b> would go — the one command on this menu that is CLICK-scoped rather
    /// than group-scoped, because a vertex is added at a place and not to a group.
    /// </summary>
    private (int Wire, int Segment, double T)? _menuInsertion;

    private async void OnProfileContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu || Editor is not { } editor) { e.Cancel = true; return; }

        _menuArray = ProfileCanvas.ConsumeContextMenuTargetArray();
        _menuInsertion = ProfileCanvas.ConsumeContextMenuInsertion();

        // Right-clicking empty space offers nothing rather than a menu of disabled items — there is
        // no group to act on, and an all-grey menu reads as the feature being broken.
        if (_menuArray < 0) { e.Cancel = true; return; }

        string? name = editor.ArrayNameAt(_menuArray);
        if (name is null) { e.Cancel = true; return; }

        // Paste's enabled state depends on what is actually on the clipboard right now, which is an
        // async read — so the menu opens with it disabled and it enables a moment later if the
        // clipboard turns out to hold a profile. Blocking the menu on a clipboard round trip would
        // make every right-click feel slow.
        var pasteItem = new MenuItem { Header = "Paste Coordinates", IsEnabled = false };

        menu.ItemsSource = BuildItems(editor, name, pasteItem);

        await EnablePasteIfClipboardHoldsAProfileAsync(editor, pasteItem);
    }

    private List<object> BuildItems(WBondViewModel editor, string groupName, MenuItem pasteItem)
    {
        var items = new List<object>
        {
            Item($"Set Loop Height… ({groupName})", () => SetGroupAsync(WBondGroupEdits.SetLoopHeightAsync)),
            Item("Set Span…",     () => SetGroupAsync(WBondGroupEdits.SetSpanAsync)),
            Item("Set Diameter…", () => SetGroupAsync(WBondGroupEdits.SetDiameterAsync)),
            Item("Set Material…", () => SetGroupAsync(WBondGroupEdits.SetMaterialAsync)),
            Item("Rotate…",       () => SetGroupAsync(WBondGroupEdits.RotateAsync)),
            Item("Reverse Wires", () => { Apply(editor.ReverseGroup(_menuArray)); return Task.CompletedTask; }),
            Item("Flip Wires",    () => { Apply(editor.FlipGroup(_menuArray)); return Task.CompletedTask; }),
            new Separator(),
        };

        // The ORDINARY copy (owner, 2026-08-16) — the same one ⌘C/Ctrl+C performs, on the SELECTION,
        // writing wires + geometry + the picture formats. It sits above Copy Coordinates because that
        // is the copy a user reaches for by default; Copy Coordinates is the specialised one (this
        // group's profile shape as text), and having only the specialised one on the menu made the
        // common gesture look unavailable here.
        //
        // Absent rather than disabled when the HOST has no clipboard story of its own, which is the
        // honest state for a docked profile view over a wirebond cell: Copy Coordinates below still
        // works, because that one is this view's own.
        if (CopyRequested is { } copy) items.Add(Item("Copy", copy));

        items.Add(Item("Copy Coordinates", () => CopyProfileCoordinatesAsync(editor)));

        pasteItem.Click += async (_, _) => await PasteProfileCoordinatesAsync(editor);
        items.Add(pasteItem);

        // Add Vertex — the same command the layout view's wire menu offers, on the same wire, with
        // its own separator above it (owner, 2026-08-17). Straighten Wire deliberately does NOT
        // appear here: it is a statement about the wire's path across the BOARD, and this view's
        // horizontal axis is position along that path — there is no XY plane here to straighten in.
        //
        // The new point is collinear with its neighbours and at their interpolated z, so it changes
        // nothing about the shape and only gives this view a handle to drag.
        items.Add(new Separator());
        items.Add(AddVertexItem(editor));

        items.Add(new Separator());
        items.Add(Item($"Delete Group \"{groupName}\"",
                       () => { Apply(editor.DeleteGroup(_menuArray)); return Task.CompletedTask; }));

        return items;
    }

    /// <summary>
    /// Add Vertex, on whatever wire the right-click landed on — disabled with its reason when it
    /// landed on none, never silently absent.
    /// </summary>
    private MenuItem AddVertexItem(WBondViewModel editor)
    {
        if (_menuInsertion is not { } insert)
        {
            var disabled = new MenuItem { Header = "Add Vertex", IsEnabled = false };
            ToolTip.SetTip(disabled, "Right-click a wire.");
            return disabled;
        }

        var item = new MenuItem { Header = "Add Vertex" };
        item.Click += (_, _) =>
        {
            if (editor.AddWirePoint(insert.Wire, insert.Segment, insert.T)) Repaint();
        };
        return item;
    }

    /// <summary>A fresh <see cref="MenuItem"/> per opening — never a reused instance with a re-subscribed
    /// <c>Click</c>, which would fire N times on the Nth opening.</summary>
    private static MenuItem Item(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    /// <summary>
    /// An edit that touched nothing is a no-op rather than an error — the group simply had nothing the
    /// command applied to.
    /// </summary>
    private void Apply(int touched)
    {
        if (touched > 0) Repaint();
    }

    private async Task SetGroupAsync(Func<Window?, WBondViewModel, int, Task<int>> edit)
    {
        if (Editor is not { } editor) return;
        Apply(await edit(TopLevel.GetTopLevel(this) as Window, editor, _menuArray));
    }

    // ---------------------------------------------------------------- coordinates

    private async Task CopyProfileCoordinatesAsync(WBondViewModel editor)
    {
        if (editor.ProfileForGroup(_menuArray) is not { } profile) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        await clipboard.SetTextAsync(ProfileCoordinateText.Write(profile, editor.DisplayUnit));
    }

    private async Task PasteProfileCoordinatesAsync(WBondViewModel editor)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        string? text = await clipboard.TryGetTextAsync();
        string name = editor.ArrayNameAt(_menuArray) ?? "Pasted";

        if (!ProfileCoordinateText.TryRead(text, editor.DisplayUnit, name, out var shape)) return;

        Apply(editor.ApplyProfileToGroup(_menuArray, shape));
    }

    /// <summary>
    /// Enables Paste only once the clipboard is known to hold something readable — the requirement
    /// that it be greyed out when the contents cannot be understood as a profile shape.
    /// </summary>
    private async Task EnablePasteIfClipboardHoldsAProfileAsync(WBondViewModel editor, MenuItem pasteItem)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        try
        {
            string? text = await clipboard.TryGetTextAsync();
            pasteItem.IsEnabled = ProfileCoordinateText.CanRead(text, editor.DisplayUnit);
        }
        catch
        {
            // A clipboard that cannot be read is indistinguishable from one holding nothing useful.
            pasteItem.IsEnabled = false;
        }
    }
}
