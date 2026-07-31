using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// Square tile showing a SymbolKind's glyph + DisplayName caption.
/// DataContext = <see cref="PaletteTileVm"/>.
/// IsArmed: driven by the palette VM while placement is armed.
/// Single pointer owner: PointerReleased-without-drag = arm toggle; PointerMoved-past-threshold = DnD.
/// No nested Button — the Button-eats-drag gotcha is avoided by using a plain Border.
/// </summary>
public partial class PaletteTile : UserControl
{
    // ── IsArmed ───────────────────────────────────────────────────────────────

    public static readonly StyledProperty<bool> IsArmedProperty =
        AvaloniaProperty.Register<PaletteTile, bool>(nameof(IsArmed));

    public bool IsArmed
    {
        get => GetValue(IsArmedProperty);
        set => SetValue(IsArmedProperty, value);
    }

    // ── Drag source state ─────────────────────────────────────────────────────

    private PointerPressedEventArgs? _pressArgs;
    private bool                     _dragOccurred;
    private const double             DragThreshold = 5.0;

    public PaletteTile()
    {
        InitializeComponent();
        PointerPressed  += OnTilePointerPressed;
        PointerMoved    += OnTilePointerMoved;
        PointerReleased += OnTilePointerReleased;
    }

    private void OnTilePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressArgs    = e;
            _dragOccurred = false;
        }
    }

    private async void OnTilePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressArgs is null || DataContext is not PaletteTileVm vm) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressArgs = null;
            return;
        }

        var delta = e.GetPosition(this) - _pressArgs.GetPosition(this);
        if (Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) < DragThreshold) return;

        var savedArgs = _pressArgs;
        _pressArgs    = null;   // clear before await — prevents re-entry
        _dragOccurred = true;

        var payload = new PaletteDragPayload(vm.Item.Kind, vm.Item.PortCount, vm.Item.Pdk?.CellDir);
        var transferItem = new DataTransferItem();
        transferItem.Set(DataFormat.Text, payload.Serialize());
        var transfer = new DataTransfer();
        transfer.Add(transferItem);

        await DragDrop.DoDragDropAsync(savedArgs, transfer, DragDropEffects.Copy);
    }

    private void OnTilePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // PointerMoved clears _pressArgs when a drag starts, so wasPress=false after a drag.
        bool wasPress = _pressArgs is not null && !_dragOccurred;
        _pressArgs    = null;
        _dragOccurred = false;

        if (wasPress && DataContext is PaletteTileVm vm)
            vm.ArmCommand.Execute(null);
    }
}
