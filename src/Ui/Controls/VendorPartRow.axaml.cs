using System;
using Avalonia.Controls;
using Avalonia.Input;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// One row of a vendor library's part list (brief vendorlib-2 R-vl2-2b). DataContext =
/// <see cref="VendorPartRowVm"/>. It arms placement on click and drags onto a canvas exactly as a
/// <see cref="PaletteTile"/> does: the same press/move/release ownership, the same payload type.
/// </summary>
public partial class VendorPartRow : UserControl
{
    private PointerPressedEventArgs? _pressArgs;
    private bool                     _dragOccurred;
    private const double             DragThreshold = 5.0;

    /// <summary>A part with no build for this platform is drawn greyed (brief vendorlib-15 D3), never hidden.</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter PlaceableOpacity =
        new Avalonia.Data.Converters.FuncValueConverter<bool, double>(placeable => placeable ? 1.0 : 0.45);

    public VendorPartRow()
    {
        InitializeComponent();
        // handledEventsToo: the ListBoxItem around the row claims the press for its own selection.
        AddHandler(PointerPressedEvent,  OnRowPointerPressed,  handledEventsToo: true);
        AddHandler(PointerMovedEvent,    OnRowPointerMoved,    handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnRowPointerReleased, handledEventsToo: true);
    }

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressArgs    = e;
            _dragOccurred = false;
        }
    }

    private async void OnRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressArgs is null || DataContext is not VendorPartRowVm vm) return;
        if (!vm.IsPlaceable) { _pressArgs = null; return; }   // no build here: nothing to drop
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

        var payload = new PaletteDragPayload(SymbolKind.SpiceModel, 2, Vendor: vm.Ref);
        var transferItem = new DataTransferItem();
        transferItem.Set(DataFormat.Text, payload.Serialize());
        var transfer = new DataTransfer();
        transfer.Add(transferItem);

        await DragDrop.DoDragDropAsync(savedArgs, transfer, DragDropEffects.Copy);
    }

    private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        bool wasPress = _pressArgs is not null && !_dragOccurred;
        _pressArgs    = null;
        _dragOccurred = false;

        if (wasPress && DataContext is VendorPartRowVm vm)
            vm.ArmCommand.Execute(null);
    }
}
