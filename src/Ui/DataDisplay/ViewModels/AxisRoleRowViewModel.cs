// ================================================================
//  AxisRoleRowViewModel.cs  —  Phase 7.3a/7.3b: per-axis role row VM
// ================================================================

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

/// <summary>
/// One row in the axis-role editor for a cube-bound trace.
/// Each axis of the DataCube gets a role — KeepAsX (exactly one), FamilyIterate (at most one),
/// or PinToIndex (single index, value picker).
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

    /// <summary>
    /// Maps display position → true cube-axis index. Non-null only when options are filtered
    /// (node axis, labeled-only mode). Null = 1:1 mapping (PinIndex IS the cube index).
    /// </summary>
    public IReadOnlyList<int>? PinOptionIndices { get; }

    /// <summary>True cube-axis index for the selected option.</summary>
    public int TruePinIndex => PinOptionIndices is not null && PinOptionIndices.Count > 0
        ? PinOptionIndices[Math.Clamp(PinIndex, 0, PinOptionIndices.Count - 1)]
        : PinIndex;

    /// <summary>True when PinOptions were built from axis.Labels (i.e. they are net names, not
    /// formatted numeric values). When true, the selected option string is used as the label in
    /// AxisSlice so BuildPickerExpression can emit a quoted net-name token.</summary>
    public bool OptionsAreLabels { get; }

    /// <summary>True for the axis that is filtered by label (node or branch axis).</summary>
    public bool IsFilterableLabelAxis { get; }

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
    [NotifyPropertyChangedFor(nameof(IsPinned))]
    [NotifyPropertyChangedFor(nameof(ShowPinPicker))]
    private bool _isFamily;

    [ObservableProperty]
    private int _pinIndex;

    public bool IsPinned      => !IsX && !IsFamily;
    public bool ShowPinPicker => !IsX && !IsFamily;

    // ---- Construction -----------------------------------------------------

    internal AxisRoleRowViewModel(TraceRowViewModel owner,
                                   string axisName, string? unit,
                                   IReadOnlyList<string> pinOptions,
                                   bool isX, int pinIndex,
                                   IReadOnlyList<int>? pinOptionIndices = null,
                                   bool optionsAreLabels = false,
                                   bool isFamily = false,
                                   bool isFilterableLabelAxis = false)
    {
        _owner                = owner;
        AxisName              = axisName;
        Unit                  = unit;
        PinOptions            = pinOptions;
        PinOptionIndices      = pinOptionIndices;
        OptionsAreLabels      = optionsAreLabels;
        IsFilterableLabelAxis = isFilterableLabelAxis;
        _isX                  = isX;
        _isFamily             = isFamily;
        _pinIndex             = Math.Clamp(pinIndex, 0, Math.Max(0, pinOptions.Count - 1));
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
        IsX      = false;
        IsFamily = false;
    }

    [RelayCommand]
    private void SetFamily()
    {
        IsFamily = true;
    }

    // ---- Observable callbacks --------------------------------------------

    partial void OnIsXChanged(bool value)
    {
        if (_suppress) return;
        if (value)
        {
            // Mutually exclusive with IsFamily on this row
            _suppress = true;
            IsFamily  = false;
            _suppress = false;
            _owner.OnAxisSetToX(this);  // auto-flip previous X to Pinned
        }
        _owner.FlushSliceAndRebuild();
    }

    partial void OnIsFamilyChanged(bool value)
    {
        if (_suppress) return;
        if (value)
        {
            // Mutually exclusive with IsX on this row
            _suppress = true;
            IsX       = false;
            _suppress = false;
            _owner.OnAxisSetToFamily(this);  // auto-demote any other Family row
        }
        _owner.FlushSliceAndRebuild();
    }

    partial void OnPinIndexChanged(int value)
    {
        if (_suppress || _isX || _isFamily) return;
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

    /// <summary>Clears IsFamily without triggering FlushSliceAndRebuild (used during auto-demote).</summary>
    internal void SetIsFamilySilent(bool value)
    {
        _suppress = true;
        IsFamily  = value;
        _suppress = false;
    }
}
