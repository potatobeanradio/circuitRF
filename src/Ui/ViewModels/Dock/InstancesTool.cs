using System;
using Dock.Model.Mvvm.Controls;
using CircuitRF.Ui.Docking;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// Dock Tool for the Instances panel (brief-find-instance-panel.md) — the placed components of the
/// focused schematic or layout, found by name or type, double-click to zoom to one.
///
/// <para>Follows the focused document exactly as the DRC and LVS panels do — cleared first and set
/// again only for a <c>.csch</c> or a <c>.clay</c> — so it can never list one document beside another
/// (R-fi-2/R-fi-3). The list itself is <see cref="InstanceListViewModel"/>, which knows nothing of Dock.</para>
///
/// <para><b>Two focus requests, and they are different</b> (R-fi-14). Activating the panel by its tab
/// puts the caret in the search box unless focus is already somewhere inside it — the rule every
/// panel here follows. Design ▸ Find Instance… does more: it focuses the box AND selects its text so
/// typing replaces the last search, even when the caret was already there. Both carry a pending flag,
/// because the command can show the panel before its view exists — which is what makes the first
/// Ctrl/⌘+F of a session land in the box.</para>
/// </summary>
public sealed class InstancesTool : Tool, IActivatableTool
{
    private readonly ActivationFocusRelay _activationFocus = new();
    private readonly ActivationFocusRelay _searchFocus     = new();

    public InstancesTool()
    {
        Id    = DockPanelIds.Instances;
        Title = "Instances";
        _activationFocus.Follow(this);
    }

    public InstanceListViewModel ListVm { get; } = new();

    /// <summary>Called by <c>WorkspaceViewModel</c> whenever the focused document changes: a
    /// <c>SchematicViewModel</c> or a <c>LayoutEditorViewModel</c>, or null for anything else.</summary>
    public void SetActiveDocument(object? documentViewModel, string? displayName)
        => ListVm.SetActiveDocument(documentViewModel, displayName);

    // ── Activation focus (IActivatableTool) ──────────────────────────────────

    public event Action? ActivationFocusRequested
    {
        add    => _activationFocus.Requested += value;
        remove => _activationFocus.Requested -= value;
    }

    public void RequestActivationFocus() => _activationFocus.Request();

    public bool ConsumeActivationFocus() => _activationFocus.Consume();

    /// <summary>Dock's own "this tab was chosen" hook.</summary>
    public override void OnSelected()
    {
        base.OnSelected();
        RequestActivationFocus();
    }

    // ── Find Instance's own request ──────────────────────────────────────────

    /// <summary>Raised by <see cref="RequestSearchFocus"/> — the view focuses the search box and
    /// selects its text.</summary>
    public event Action? SearchFocusRequested
    {
        add    => _searchFocus.Requested += value;
        remove => _searchFocus.Requested -= value;
    }

    /// <summary>Design ▸ Find Instance…'s request: caret in the search box, text selected.</summary>
    public void RequestSearchFocus() => _searchFocus.Request();

    public bool ConsumeSearchFocus() => _searchFocus.Consume();
}
