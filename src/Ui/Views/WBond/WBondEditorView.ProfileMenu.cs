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
            Item($"Set Loop Height… ({groupName})", () => SetGroupLoopHeightAsync()),
            Item("Set Span…",     () => SetGroupSpanAsync()),
            Item("Set Diameter…", () => SetGroupDiameterAsync()),
            Item("Set Material…", () => SetGroupMaterialAsync()),
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

    private async Task SetGroupLoopHeightAsync()
    {
        if (Owner() is not { } owner || _bound is null) return;

        long current = _bound.Editor.ProfileForGroup(_profileMenuArray)?.LoopHeightNm ?? 0;

        long? nm = await WBondValuePromptDialog.PromptLengthAsync(
            owner, "Set Loop Height", "Peak height above the chord, for every wire in this group.",
            current, _bound.Editor.DisplayUnit);

        if (nm is { } v) Apply(() => _bound.Editor.SetGroupLoopHeight(_profileMenuArray, v));
    }

    private async Task SetGroupSpanAsync()
    {
        if (Owner() is not { } owner || _bound is null) return;

        long current = FirstWireOfGroup() is { } w
            ? (long)Math.Round(w.ChordLengthMetres() * WBondUnits.NmPerMetre)
            : 0;

        long? nm = await WBondValuePromptDialog.PromptLengthAsync(
            owner, "Set Span", "Foot-to-foot span. The output foot moves; the input foot stays put.",
            current, _bound.Editor.DisplayUnit);

        if (nm is { } v) Apply(() => _bound.Editor.SetGroupSpan(_profileMenuArray, v));
    }

    private async Task SetGroupDiameterAsync()
    {
        if (Owner() is not { } owner || _bound is null) return;

        long current = FirstWireOfGroup()?.DiameterNm ?? 0;

        long? nm = await WBondValuePromptDialog.PromptLengthAsync(
            owner, "Set Diameter", "Wire diameter, for every wire in this group.",
            current, _bound.Editor.DisplayUnit);

        if (nm is { } v) Apply(() => _bound.Editor.SetGroupDiameter(_profileMenuArray, v));
    }

    private async Task SetGroupMaterialAsync()
    {
        if (Owner() is not { } owner || _bound is null) return;

        var choices = _bound.Editor.Design.Materials.Select(m => m.Name).ToList();
        if (choices.Count == 0) choices = WireMaterials.All.Select(m => m.Name).ToList();

        string? picked = await WBondValuePromptDialog.PromptChoiceAsync(
            owner, "Set Material", "Conductor material, for every wire in this group.",
            choices, FirstWireOfGroup()?.Material);

        if (picked is { Length: > 0 }) Apply(() => _bound.Editor.SetGroupMaterial(_profileMenuArray, picked));
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

    private Wire? FirstWireOfGroup() =>
        _bound is not null
        && _profileMenuArray >= 0
        && _profileMenuArray < _bound.Editor.Arrays.Count
            ? _bound.Editor.Arrays[_profileMenuArray].Wires.FirstOrDefault()
            : null;

    private Window? Owner() => TopLevel.GetTopLevel(this) as Window;
}
