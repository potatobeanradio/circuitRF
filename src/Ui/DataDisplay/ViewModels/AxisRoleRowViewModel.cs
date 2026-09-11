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

    /// <summary>Display label: "name", or "name (unit)" when unit is non-empty. Spectral axes
    /// (single-tone "harmonic", two-tone "mixIndex") show the bare name — they are spectral-line
    /// identifiers, not unit-bearing quantities (the per-line frequency lives in the values/marker).
    /// <para>An ANGLE axis is shown as its symbol (&#952;, &#966;) rather than as the ASCII
    /// identifier the cube stores — <see cref="AxisSymbols"/> owns the mapping, and owns it for the
    /// Y-axis labels too so the card and the plot cannot spell one axis two ways.</para></summary>
    public string AxisLabel => AxisName is "harmonic" or "mixIndex" || string.IsNullOrEmpty(Unit)
        ? AxisSymbols.Display(AxisName)
        : $"{AxisSymbols.Display(AxisName)} ({Unit})";

    /// <summary>
    /// <b>Whether this row is worth the height it takes.</b> A <c>port</c> axis with ONE value is
    /// not: it is a combo box with a single entry, on every trace card of every single-port antenna
    /// (owner, 2026-09-11). The same rule drops "port=1" from the trace's label
    /// (<c>TraceResolve.ApplyPinnedAxisDisplay</c>) — one decision, said in both places.
    ///
    /// <para>The row is HIDDEN rather than never built, so the axis keeps its entry in the slice
    /// <c>FlushSliceAndRebuild</c> writes back. A row dropped from <c>AxisRoles</c> would be a
    /// dimension dropped from the slice the next time any other row moved.</para>
    ///
    /// <para>Never hidden while it is the X axis. A one-value axis is a poor X and nothing promotes
    /// one on purpose, but a cube whose ONLY axis is <c>port</c> would otherwise present an empty
    /// card with no way to see what it is bound to.</para>
    ///
    /// <para><b>The second rule is ANT-10's:</b> a 3D surface's own two angle axes are off the card
    /// entirely (owner, 2026-09-11). The row had nothing left on it once the role buttons went — no
    /// buttons, no picker, just the axis name — and a row that only says "this axis exists" is height
    /// spent on nothing. The row still EXISTS in <see cref="TraceRowViewModel.AxisRoles"/> for the
    /// same reason the <c>port</c> rule keeps it: the slice write-back is built from these rows, and
    /// a row dropped from the list is a dimension dropped from the slice.</para>
    /// </summary>
    public bool IsRowVisible => !(AxisName == "port" && PinOptions.Count <= 1 && !IsX)
                             && !(SurfaceMode && IsSurfaceAngleAxis);

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

    // ---- ANT-10: what a role MEANS on a 3D surface -----------------------------------------
    //
    //  A surface has no X axis and no family. SurfaceResolve opens BOTH angle axes whole and pins
    //  every other axis at this row's own index, whatever role the slice records — so on a
    //  Surface3D plot X / Fam / Fix were three buttons that changed the trace's stored slice and
    //  redrew exactly the same picture (owner, 2026-09-11: "I change them and select different
    //  values, but the plot rendering appears to be the same no matter what I use"). They are not
    //  shown there — and since that leaves an angle row with no control on it at all, the ROW goes
    //  too (see IsRowVisible). The Fix PICKER stays for every axis that is NOT one of the two
    //  angles, because that one does change the picture: it chooses the frequency, the port, the
    //  sweep point the surface is drawn at.
    //
    //  A polar CUT is a different question and keeps all three: it draws one angle against a
    //  value, so which axis is X and which is a family is exactly what the buttons are for.

    /// <summary>True when the parent plot is the 3D pattern surface.</summary>
    public bool SurfaceMode { get; private set; }

    /// <summary>True when this row is one of the two axes the surface opens whole — its polar angle
    /// or its azimuth, found by <c>SurfaceResolve.TryFindAngleAxes</c>, the same test the resolve
    /// itself uses so the card cannot name a different pair from the picture.</summary>
    public bool IsSurfaceAngleAxis { get; private set; }

    /// <summary>X / Fam / Fix. Hidden on a surface — see the note above.</summary>
    public bool ShowRoleButtons => !SurfaceMode;

    internal void ApplySurfaceMode(bool surfaceMode, bool isAngleAxis)
    {
        if (SurfaceMode == surfaceMode && IsSurfaceAngleAxis == isAngleAxis) return;
        SurfaceMode        = surfaceMode;
        IsSurfaceAngleAxis = isAngleAxis;
        OnPropertyChanged(nameof(SurfaceMode));
        OnPropertyChanged(nameof(IsSurfaceAngleAxis));
        OnPropertyChanged(nameof(ShowRoleButtons));
        OnPropertyChanged(nameof(ShowPinPicker));
        OnPropertyChanged(nameof(IsRowVisible));
    }

    // ---- Role state -------------------------------------------------------

    // Suppresses FlushSliceAndRebuild calls during batch auto-flip.
    private bool _suppress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPinned))]
    [NotifyPropertyChangedFor(nameof(ShowPinPicker))]
    [NotifyPropertyChangedFor(nameof(IsRowVisible))]
    private bool _isX;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPinned))]
    [NotifyPropertyChangedFor(nameof(ShowPinPicker))]
    private bool _isFamily;

    [ObservableProperty]
    private int _pinIndex;

    public bool IsPinned      => !IsX && !IsFamily;

    /// <summary>Whether the value picker is on the row. Off for the X and family axes of an ordinary
    /// plot; on a SURFACE it follows <see cref="IsSurfaceAngleAxis"/> instead, because the surface
    /// pins every non-angle axis regardless of the role the slice happens to carry — a freq axis
    /// left as X from a rect plot is still pinned by the surface, and hiding its picker would leave
    /// a multi-frequency pattern with no way to choose the frequency.</summary>
    public bool ShowPinPicker => SurfaceMode ? !IsSurfaceAngleAxis : (!IsX && !IsFamily);

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

    // These three rewrite the trace's SLICE — the `slice=[freq:KeepAsX, i:PinToIndex, j:PinToIndex]`
    // field every failing resolve note prints. A trail that shows the slice but not the click that
    // set it cannot say whether it was authored, restored from a .cdd, or carried over a re-run.
    [RelayCommand]
    private void SetX()
    {
        Gesture.Note("axis.role", $"{AxisName} -> X");
        IsX = true;
    }

    [RelayCommand]
    private void SetPinned()
    {
        Gesture.Note("axis.role", $"{AxisName} -> pinned[{PinIndex}]");
        IsX      = false;
        IsFamily = false;
    }

    [RelayCommand]
    private void SetFamily()
    {
        Gesture.Note("axis.role", $"{AxisName} -> family");
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
        if (_suppress) return;
        // On a surface the picker is shown for a non-angle axis whatever its recorded role, so the
        // X/family early return would swallow the one change the picker there actually makes.
        if (!SurfaceMode && (_isX || _isFamily)) return;
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
