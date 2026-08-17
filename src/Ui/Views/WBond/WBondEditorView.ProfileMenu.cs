using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using CircuitRF.Ui.Views.Dialogs;
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
public partial class WBondEditorView
{
    /// <summary>The group the current menu acts on, captured when the menu opens.</summary>
    private int _profileMenuArray = -1;

    private async void OnProfileContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu || _bound is null) { e.Cancel = true; return; }

        _profileMenuArray = ProfileCanvas.ConsumeContextMenuTargetArray();

        // Right-clicking empty space offers nothing rather than a menu of disabled items — there is
        // no group to act on, and an all-grey menu reads as the feature being broken.
        if (_profileMenuArray < 0) { e.Cancel = true; return; }

        string? name = _bound.Editor.ArrayNameAt(_profileMenuArray);
        if (name is null) { e.Cancel = true; return; }

        // Paste's enabled state depends on what is actually on the clipboard right now, which is an
        // async read — so the menu opens with it disabled and it enables a moment later if the
        // clipboard turns out to hold a profile. Blocking the menu on a clipboard round trip would
        // make every right-click feel slow.
        var pasteItem = new MenuItem { Header = "Paste Coordinates", IsEnabled = false };

        menu.ItemsSource = BuildItems(name, pasteItem);

        await EnablePasteIfClipboardHoldsAProfileAsync(pasteItem);
    }

    private List<object> BuildItems(string groupName, MenuItem pasteItem)
    {
        var items = new List<object>
        {
            Item($"Set Loop Height… ({groupName})", () => SetGroupLoopHeightAsync(_profileMenuArray)),
            Item("Set Span…",     () => SetGroupSpanAsync(_profileMenuArray)),
            Item("Set Diameter…", () => SetGroupDiameterAsync(_profileMenuArray)),
            Item("Set Material…", () => SetGroupMaterialAsync(_profileMenuArray)),
            Item("Rotate…",       () => RotateGroupAsync()),
            Item("Reverse Wires", () => { Apply(() => _bound!.Editor.ReverseGroup(_profileMenuArray)); return Task.CompletedTask; }),
            Item("Flip Wires",    () => { Apply(() => _bound!.Editor.FlipGroup(_profileMenuArray)); return Task.CompletedTask; }),
            new Separator(),
            Item("Copy Coordinates", CopyProfileCoordinatesAsync),
        };

        pasteItem.Click += async (_, _) => await PasteProfileCoordinatesAsync();
        items.Add(pasteItem);

        items.Add(new Separator());
        items.Add(Item($"Delete Group \"{groupName}\"",
                       () => { Apply(() => _bound!.Editor.DeleteGroup(_profileMenuArray)); return Task.CompletedTask; }));

        return items;
    }

    /// <summary>A fresh <see cref="MenuItem"/> per opening — never a reused instance with a re-subscribed
    /// <c>Click</c>, which would fire N times on the Nth opening.</summary>
    private static MenuItem Item(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action();
        return item;
    }

    // ---------------------------------------------------------------- the "…" prompts
    //
    // Each takes the array it acts on rather than reading _profileMenuArray, because there are now
    // TWO ways in: this menu, and a double-click on the inductance panel's own Loop height / Span /
    // Diameter / Material row (owner, 2026-08-16). One implementation for both — a second copy would
    // be a second chance to get the undo grouping, the seed value or the refusal wrong.

    internal async Task SetGroupLoopHeightAsync(int arrayIndex)
    {
        if (Owner() is not { } owner || _bound is null) return;

        long? nm = await WBondValuePromptDialog.PromptLengthAsync(
            owner, "Set Loop Height", "Peak height above the chord, for every wire in this group.",
            SeedNm(arrayIndex, r => r.LoopHeightMm.Value), _bound.Editor.DisplayUnit);

        if (nm is { } v) Apply(() => _bound.Editor.SetGroupLoopHeight(arrayIndex, v));
    }

    internal async Task SetGroupSpanAsync(int arrayIndex)
    {
        if (Owner() is not { } owner || _bound is null) return;

        long? nm = await WBondValuePromptDialog.PromptLengthAsync(
            owner, "Set Span", "Foot-to-foot span. The output foot moves; the input foot stays put.",
            SeedNm(arrayIndex, r => r.SpanMm.Value), _bound.Editor.DisplayUnit);

        if (nm is { } v) Apply(() => _bound.Editor.SetGroupSpan(arrayIndex, v));
    }

    internal async Task SetGroupDiameterAsync(int arrayIndex)
    {
        if (Owner() is not { } owner || _bound is null) return;

        long? nm = await WBondValuePromptDialog.PromptLengthAsync(
            owner, "Set Diameter", "Wire diameter, for every wire in this group.",
            SeedNm(arrayIndex, r => r.DiameterMm.Value), _bound.Editor.DisplayUnit);

        if (nm is { } v) Apply(() => _bound.Editor.SetGroupDiameter(arrayIndex, v));
    }

    internal async Task SetGroupMaterialAsync(int arrayIndex)
    {
        if (Owner() is not { } owner || _bound is null) return;

        var choices = _bound.Editor.Design.Materials.Select(m => m.Name).ToList();
        if (choices.Count == 0) choices = WireMaterials.All.Select(m => m.Name).ToList();

        string? current = Row(arrayIndex)?.Material.Value;

        string? picked = await WBondValuePromptDialog.PromptChoiceAsync(
            owner, "Set Material", "Conductor material, for every wire in this group.",
            choices, string.IsNullOrEmpty(current) ? null : current);

        if (picked is { Length: > 0 }) Apply(() => _bound.Editor.SetGroupMaterial(arrayIndex, picked));
    }

    /// <summary>
    /// What the prompt opens on: the SAME median the inductance panel is showing for that array,
    /// converted back to nanometres.
    ///
    /// <para>Seeding from the group's bound profile (as Loop Height used to) reads 0 on a group of
    /// free wires — a prompt that opens on zero when the panel beside it says 18.5 mil. Seeding from
    /// the FIRST wire (as the other three used to) is a lie on exactly the non-uniform arrays the
    /// <c>*</c> exists to flag. The median is the number on screen, which is the number the user
    /// believes they are editing.</para>
    /// </summary>
    private long SeedNm(int arrayIndex, Func<PanelReadout.ArrayRow, double> millimetres) =>
        Row(arrayIndex) is { } row ? (long)Math.Round(millimetres(row) * 1e6) : 0;

    private PanelReadout.ArrayRow? Row(int arrayIndex)
    {
        var rows = _bound?.Editor.Readout.Rows;
        return rows is not null && arrayIndex >= 0 && arrayIndex < rows.Count ? rows[arrayIndex] : null;
    }

    private async Task RotateGroupAsync()
    {
        if (Owner() is not { } owner || _bound is null) return;

        double? deg = await WBondValuePromptDialog.PromptAngleAsync(
            owner, "Rotate Group", "Rotates the whole group rigidly about its own centre, in the layout plane.");

        if (deg is { } d) Apply(() => _bound.Editor.RotateGroup(_profileMenuArray, d * Math.PI / 180.0));
    }

    // ---------------------------------------------------------------- coordinates

    private async Task CopyProfileCoordinatesAsync()
    {
        if (_bound is null) return;
        if (_bound.Editor.ProfileForGroup(_profileMenuArray) is not { } profile) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        await clipboard.SetTextAsync(ProfileCoordinateText.Write(profile, _bound.Editor.DisplayUnit));
    }

    private async Task PasteProfileCoordinatesAsync()
    {
        if (_bound is null) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        string? text = await clipboard.TryGetTextAsync();
        string name = _bound.Editor.ArrayNameAt(_profileMenuArray) ?? "Pasted";

        if (!ProfileCoordinateText.TryRead(text, _bound.Editor.DisplayUnit, name, out var shape)) return;

        Apply(() => _bound.Editor.ApplyProfileToGroup(_profileMenuArray, shape));
    }

    /// <summary>
    /// Enables Paste only once the clipboard is known to hold something readable — the requirement
    /// that it be greyed out when the contents cannot be understood as a profile shape.
    /// </summary>
    private async Task EnablePasteIfClipboardHoldsAProfileAsync(MenuItem pasteItem)
    {
        if (_bound is null) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        try
        {
            string? text = await clipboard.TryGetTextAsync();
            pasteItem.IsEnabled = ProfileCoordinateText.CanRead(text, _bound.Editor.DisplayUnit);
        }
        catch
        {
            // A clipboard that cannot be read is indistinguishable from one holding nothing useful.
            pasteItem.IsEnabled = false;
        }
    }

    // ---------------------------------------------------------------- helpers

    private Window? Owner() => TopLevel.GetTopLevel(this) as Window;
}
