using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CircuitRF.Ui.WBond;
using CircuitRF.Ui.Views.Dialogs;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Views.WBond;

/// <summary>
/// The four group-wide "set this for every wire in the array" prompts, in one place.
///
/// <h3>Why they are not on a view</h3>
/// <para>There are now THREE ways in: the profile view's own context menu (wbond.md §6.4a), a
/// double-click on the inductance panel's Loop height / Span / Diameter / Material row (owner,
/// 2026-08-16), and — since WB39a/M3 — the same panel again as a dock tool over a wirebond cell, with
/// no wBond editor anywhere in sight. A second copy would be a second chance to get the undo grouping,
/// the seed value or the refusal wrong.</para>
///
/// <para>The seed is always the MEDIAN the panel is showing for that array, converted back to
/// nanometres. Seeding from the group's bound profile reads 0 on a group of free wires — a prompt that
/// opens on zero when the panel beside it says 18.5 mil. Seeding from the FIRST wire is a lie on
/// exactly the non-uniform arrays the <c>*</c> exists to flag.</para>
/// </summary>
internal static class WBondGroupEdits
{
    /// <returns>How many wires changed — 0 when the user cancelled or the edit applied to nothing.</returns>
    public static async Task<int> SetLoopHeightAsync(Window? owner, WBondViewModel editor, int arrayIndex)
    {
        if (owner is null) return 0;

        long? nm = await WBondValuePromptDialog.PromptLengthAsync(
            owner, "Set Loop Height", "Peak height above the chord, for every wire in this group.",
            SeedNm(editor, arrayIndex, r => r.LoopHeightMm.Value), editor.DisplayUnit);

        return nm is { } v ? editor.SetGroupLoopHeight(arrayIndex, v) : 0;
    }

    public static async Task<int> SetSpanAsync(Window? owner, WBondViewModel editor, int arrayIndex)
    {
        if (owner is null) return 0;

        long? nm = await WBondValuePromptDialog.PromptLengthAsync(
            owner, "Set Span", "Foot-to-foot span. The output foot moves; the input foot stays put.",
            SeedNm(editor, arrayIndex, r => r.SpanMm.Value), editor.DisplayUnit);

        return nm is { } v ? editor.SetGroupSpan(arrayIndex, v) : 0;
    }

    public static async Task<int> SetDiameterAsync(Window? owner, WBondViewModel editor, int arrayIndex)
    {
        if (owner is null) return 0;

        long? nm = await WBondValuePromptDialog.PromptLengthAsync(
            owner, "Set Diameter", "Wire diameter, for every wire in this group.",
            SeedNm(editor, arrayIndex, r => r.DiameterMm.Value), editor.DisplayUnit);

        return nm is { } v ? editor.SetGroupDiameter(arrayIndex, v) : 0;
    }

    public static async Task<int> SetMaterialAsync(Window? owner, WBondViewModel editor, int arrayIndex)
    {
        if (owner is null) return 0;

        var choices = editor.Design.Materials.Select(m => m.Name).ToList();
        if (choices.Count == 0) choices = WireMaterials.All.Select(m => m.Name).ToList();

        string? current = Row(editor, arrayIndex)?.Material.Value;

        string? picked = await WBondValuePromptDialog.PromptChoiceAsync(
            owner, "Set Material", "Conductor material, for every wire in this group.",
            choices, string.IsNullOrEmpty(current) ? null : current);

        return picked is { Length: > 0 } ? editor.SetGroupMaterial(arrayIndex, picked) : 0;
    }

    /// <summary>Rotates the whole group rigidly about its own centre, in the layout plane.</summary>
    public static async Task<int> RotateAsync(Window? owner, WBondViewModel editor, int arrayIndex)
    {
        if (owner is null) return 0;

        double? deg = await WBondValuePromptDialog.PromptAngleAsync(
            owner, "Rotate Group", "Rotates the whole group rigidly about its own centre, in the layout plane.");

        return deg is { } d ? editor.RotateGroup(arrayIndex, d * Math.PI / 180.0) : 0;
    }

    /// <summary>The median the panel is showing for that array, in nanometres. See the class remarks.</summary>
    private static long SeedNm(WBondViewModel editor, int arrayIndex, Func<PanelReadout.ArrayRow, double> millimetres) =>
        Row(editor, arrayIndex) is { } row ? (long)Math.Round(millimetres(row) * 1e6) : 0;

    private static PanelReadout.ArrayRow? Row(WBondViewModel editor, int arrayIndex)
    {
        var rows = editor.Readout.Rows;
        return rows is not null && arrayIndex >= 0 && arrayIndex < rows.Count ? rows[arrayIndex] : null;
    }
}
