// ================================================================
//  AxisRoleRowViewModel.cs  —  Phase 7.3a: per-axis role row VM
// ================================================================

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

/// <summary>
/// One row in the axis-role editor for a cube-bound trace (Phase 7.3a).
/// Each axis of the DataCube gets a role — KeepAsX (exactly one) or PinToIndex
/// (single index, value picker).
/// </summary>
public sealed partial class AxisRoleRowViewModel : ViewModelBase
{
    private readonly TraceRowViewModel _owner;

    // ---- Immutable axis metadata ----------------------------------------

    public string          AxisName   { get; }
    public string?         Unit       { get; }

    /// <summary>Display label: "name" or "name (unit)" when unit is non-empty.</summary>
    public string AxisLabel => string.IsNullOrEmpty(Unit) ? AxisName : $"{AxisName} ({Unit})";

    /// <summary>Selectable index labels (Axis.Labels[k] ?? Values[k].ToString("G3")).</summary>
    public IReadOnlyList<string> PinOptions { get; }

    /// <summary>True when this is the only axis (rank-1 cube) — role toggle disabled.</summary>
    public bool IsRoleToggleable => _owner.AxisRoles.Count > 1;

    // ---- Role state -------------------------------------------------------

    // Suppresses FlushSliceAndRebuild calls during batch auto-flip.
    private bool _suppress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPinned))]
    [NotifyPropertyChangedFor(nameof(ShowPinPicker))]
    private bool _isX;

    [ObservableProperty]
    private int _pinIndex;

    public bool IsPinned    => !IsX;
    public bool ShowPinPicker => !IsX;

    // ---- Construction -----------------------------------------------------

    internal AxisRoleRowViewModel(TraceRowViewModel owner,
                                   string axisName, string? unit,
                                   IReadOnlyList<string> pinOptions,
                                   bool isX, int pinIndex)
    {
        _owner     = owner;
        AxisName   = axisName;
        Unit       = unit;
        PinOptions = pinOptions;
        _isX       = isX;
        _pinIndex  = Math.Clamp(pinIndex, 0, Math.Max(0, pinOptions.Count - 1));
    }

    // ---- Commands ---------------------------------------------------------

    [RelayCommand]
    private void SetX()
    {
        IsX = true;
    }

    [RelayCommand]
    private void SetPinned()
    {
        IsX = false;
    }

    // ---- Observable callbacks --------------------------------------------

    partial void OnIsXChanged(bool value)
    {
        if (_suppress) return;
        if (value) _owner.OnAxisSetToX(this);  // auto-flip previous X to Pinned
        _owner.FlushSliceAndRebuild();
    }

    partial void OnPinIndexChanged(int value)
    {
        if (_suppress || _isX) return;
        _owner.FlushSliceAndRebuild();
    }

    // ---- Internal helpers ------------------------------------------------

    /// <summary>Sets IsX without triggering FlushSliceAndRebuild (used during auto-flip).</summary>
    internal void SetIsXSilent(bool value)
    {
        _suppress = true;
        IsX       = value;
        _suppress = false;
    }
}
