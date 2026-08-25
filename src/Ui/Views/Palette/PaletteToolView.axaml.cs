using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.Views.Palette;

public partial class PaletteToolView : UserControl
{
    public PaletteToolView()
    {
        InitializeComponent();

        // Page Up / Page Down / Home / End over the tile grid — the same rule the Project Tree and
        // the .ctech editor use, from the one place that owns it. Tunnelled so nothing inside the
        // grid can claim a key first.
        AddHandler(KeyDownEvent, OnScrollKeyDown, RoutingStrategies.Tunnel);

        // A click anywhere in the tile area gives that area keyboard focus, which is what makes the
        // keys above arrive at all. Bubbling and never Handled, so a tile's own press (arm toggle /
        // drag start) runs exactly as before.
        TileScroll.AddHandler(PointerPressedEvent, OnTilePressed, handledEventsToo: true);

        // Activating the panel (clicking its tab) must also put focus in here — see below.
        DataContextChanged += OnDataContextChangedForActivation;
    }

    private void OnTilePressed(object? sender, PointerPressedEventArgs e) => TileScroll.Focus();

    // ── Activation focus (owner, 2026-08-25) ──────────────────────────────────
    //
    //  Clicking this panel's TAB leaves focus on the tab — Dock chrome, outside this control — so a
    //  key event's route never passes through here and OnScrollKeyDown is never called. The user had
    //  to click a tile first. Same fix, and the same mechanism, as the Project Tree beside it.

    private IActivatableTool? _activationTool;

    private void OnDataContextChangedForActivation(object? sender, EventArgs e)
    {
        if (_activationTool is not null) _activationTool.ActivationFocusRequested -= OnActivationFocusRequested;
        _activationTool = DataContext as IActivatableTool;
        if (_activationTool is null) return;

        _activationTool.ActivationFocusRequested += OnActivationFocusRequested;

        // The panel can be activated before this view exists (first layout, restored arrangement).
        if (_activationTool.ConsumeActivationFocus()) OnActivationFocusRequested();
    }

    private void OnActivationFocusRequested() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => TileScroll.Focus(), Avalonia.Threading.DispatcherPriority.Input);

    private void OnScrollKeyDown(object? sender, KeyEventArgs e)
    {
        // An open dropdown (the category picker) owns all four keys — it is navigating its own items,
        // and the grid behind it is not what the user is looking at.
        if (e.Source is ComboBox { IsDropDownOpen: true }) return;

        var action = PanelScrollKeys.ActionFor(e.Key, e.Source is TextBox);
        if (action is null) return;

        PanelScrollKeys.Apply(action.Value, TileScroll);
        e.Handled = true;
    }
}
