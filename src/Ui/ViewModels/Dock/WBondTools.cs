using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.WBond;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// The two wBond panels, as dock tools that follow the active layout (wbond.md §10.1, WB39a/M3).
///
/// <h3>What this milestone actually is</h3>
/// <para>It is what turns "two editors" into "one editor with two extra panels". Once a wirebond cell
/// carries its own wires (WB40), pushing into it in the ordinary Layout Editor shows them — and these
/// two panels are the rest of what the wBond editor had that the Layout Editor did not: the profile
/// view and the Array Inductance readout. Both host the SAME controls the wBond editor hosts inline,
/// so there is one copy of each.</para>
///
/// <para>Following the active LAYOUT is the same contract <c>PropertiesTool</c> and <c>DrcTool</c>
/// already have, and it is not tidiness: a wire shape shown beside a different layout's artwork would
/// be worse than showing none.</para>
/// </summary>
public abstract partial class WBondToolBase : Tool
{
    /// <summary>The wire editor the panel is showing, or null when the active layout has no wires.</summary>
    [ObservableProperty] private WBondViewModel? _editor;

    /// <summary>True when there is something to show — otherwise the panel says why it is empty.</summary>
    [ObservableProperty] private bool _hasWires;

    partial void OnEditorChanged(WBondViewModel? value) => OnEditorAssigned(value);

    /// <summary>A derived tool's chance to re-point whatever it derives from the editor.</summary>
    protected virtual void OnEditorAssigned(WBondViewModel? value) { }

    /// <summary>
    /// Points the panel at <paramref name="vm"/>'s wires, if it has any. A layout with none — which is
    /// nearly every layout — leaves the panel empty and saying so, rather than showing the previous
    /// document's wires beside this one's artwork.
    /// </summary>
    public virtual void SetActiveLayout(LayoutEditorViewModel? vm)
    {
        Editor = vm?.WireEditor;
        HasWires = Editor is not null;
        Subject = HasWires ? NameOf(vm?.CurrentLayoutPath) : null;
    }

    /// <summary>
    /// Points the panel at a wBond DOCUMENT's own editor — the second surface of §10.1's table, where
    /// the wires are the document rather than a property of the artwork.
    /// </summary>
    public void SetActiveWBond(WBondViewModel? editor, string? subject = null)
    {
        Editor = editor;
        HasWires = editor is not null;
        Subject = HasWires ? subject : null;
    }

    /// <summary>
    /// <b>Whose wires the panel is showing</b> — appended to the tab title (owner, 2026-08-17).
    ///
    /// <para>A workspace can hold several cells with a wBond in each, and these two panels follow
    /// whichever layout is active — so a tab reading only "Wire Profile" says nothing about which cell's
    /// wires are on screen, and the answer changes under the user as they switch tabs. The wBond app does
    /// not have the problem (one document, one layout) and does not use these panels.</para>
    /// </summary>
    [ObservableProperty] private string? _subject;

    partial void OnSubjectChanged(string? value) => Title = value is { Length: > 0 } s ? $"{BaseTitle} — {s}" : BaseTitle;

    /// <summary>The tab's name with nothing to qualify it — what it reads when no wires are in view.</summary>
    protected abstract string BaseTitle { get; }

    /// <summary>A layout's file name without its extension, or null — the cell as the user names it.</summary>
    private static string? NameOf(string? path) =>
        path is { Length: > 0 } ? System.IO.Path.GetFileNameWithoutExtension(path) : null;
}

/// <summary>
/// The profile view (§6.2) as a dock tool.
///
/// <para>It carries the GRID PITCH as well as the editor, because the profile canvas draws its own grid
/// from a pitch the host pushes in — the wBond editor pushes its reference layout's snap, and this tool
/// pushes the active layout's. Without it the docked view drew no grid at all (owner, 2026-08-17), and
/// following the layout's <c>SnapDbu</c> live is what makes the one Snap box in that editor govern this
/// panel too.</para>
/// </summary>
public sealed partial class WBondProfileTool : WBondToolBase
{
    public WBondProfileTool()
    {
        Id    = DockPanelIds.WBondProfile;
        Title = BaseTitle;
    }

    protected override string BaseTitle => "Wire Profile";

    /// <summary>The grid pitch in nanometres, or 0 for no grid — the active layout's own snap.</summary>
    [ObservableProperty] private long _gridPitchNm;

    private LayoutEditorViewModel? _followedLayout;

    public override void SetActiveLayout(LayoutEditorViewModel? vm)
    {
        if (_followedLayout is not null) _followedLayout.WireGridPitchChanged -= PullGridPitch;

        _followedLayout = vm?.WireEditor is null ? null : vm;
        if (_followedLayout is not null) _followedLayout.WireGridPitchChanged += PullGridPitch;

        base.SetActiveLayout(vm);
        PullGridPitch();
    }

    private void PullGridPitch() => GridPitchNm = _followedLayout?.WireGridPitchNm ?? 0;
}

/// <summary>
/// The Array Inductance panel (§6.8) as a dock tool.
///
/// <para>It owns its own <see cref="WBondPanelViewModel"/> — the formatter — because the panel is a
/// readout of whatever editor is current and the editor itself has no opinion about formatting. The
/// wBond document builds its own for exactly the same reason.</para>
/// </summary>
public sealed partial class WBondInductanceTool : WBondToolBase
{
    public WBondInductanceTool()
    {
        Id    = DockPanelIds.WBondInductance;
        Title = BaseTitle;
    }

    protected override string BaseTitle => "Array Inductance";

    /// <summary>The formatted rows the panel binds to.</summary>
    public WBondPanelViewModel Panel { get; } = new();

    private WBondViewModel? _subscribed;

    protected override void OnEditorAssigned(WBondViewModel? value)
    {
        if (_subscribed is not null) _subscribed.ReadoutChanged -= Refresh;

        _subscribed = value;
        if (_subscribed is not null) _subscribed.ReadoutChanged += Refresh;

        Refresh();
    }

    private void Refresh()
    {
        // Assigned even when there is no editor, so the panel's gestures cannot act on a document that
        // is no longer showing.
        Panel.Editor = _subscribed;

        if (_subscribed is not { } editor) return;   // the panel keeps its last rows; the view hides them

        Panel.Unit = editor.DisplayUnit;
        Panel.Update(editor.Readout);
    }
}
