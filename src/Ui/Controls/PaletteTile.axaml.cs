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
/// Drag source: pointer-move beyond threshold starts a DnD operation carrying <see cref="PaletteDragPayload"/>.
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
    private const double DragThreshold = 5.0;

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
            _pressArgs = e;
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

        Console.Error.WriteLine($"[DnD] drag threshold crossed for {vm.Item.Kind} portCount={vm.Item.PortCount}");

        var savedArgs = _pressArgs;
        _pressArgs = null;          // clear before await — prevents re-entry

        var transferItem = new DataTransferItem();
        transferItem.Set(PaletteDragPayload.Format,
                         new PaletteDragPayload(vm.Item.Kind, vm.Item.PortCount));
        var transfer = new DataTransfer();
        transfer.Add(transferItem);

        var effect = await DragDrop.DoDragDropAsync(savedArgs, transfer, DragDropEffects.Copy);
        Console.Error.WriteLine($"[DnD] DoDragDropAsync returned effect={effect}");
    }

    private void OnTilePointerReleased(object? sender, PointerReleasedEventArgs e)
        => _pressArgs = null;
}
