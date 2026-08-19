// ================================================================
//  XAxisPinRowViewModel.cs  —  one Fix row on the vs-X picker.
//
//  The vs-X area shows a row ONLY for an axis the X quantity has and
//  the Y side does not — picking HB1.V as X still needs a node and a
//  harmonic. Those are the X side's own choices, and pinning is all
//  they can be: the swept axis and the family must be the SAME axis on
//  both sides or the two halves could not be paired sample-for-sample.
//
//  Shared axes deliberately have no row here. They are already edited
//  by the trace's own axis rows a few lines above, and duplicating them
//  put two controls on one piece of state — which is what made this
//  area confusing (owner, 2026-08-19: "does it make sense to have Pin
//  as X, Fam and Fix as well? I am confused with the vs X area").
//  What the user needs about a shared axis is a STATEMENT, not a second
//  control: TraceRowViewModel.XRoleSummary says it in words.
// ================================================================

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public sealed partial class XAxisPinRowViewModel : ViewModelBase
{
    private readonly TraceRowViewModel _owner;
    private readonly bool _optionsAreLabels;

    public string AxisName  { get; }
    public string AxisLabel { get; }
    public IReadOnlyList<string> PinOptions { get; }

    public string RoleTooltip =>
        $"'{AxisName}' is not on the Y side, so the X side fixes it to one value";

    [ObservableProperty] private int _pinIndex;

    /// <summary>The token this row contributes to the X spec: a quoted label, or an index.</summary>
    public string Token
    {
        get
        {
            int i = Math.Clamp(PinIndex, 0, Math.Max(0, PinOptions.Count - 1));
            return _optionsAreLabels && PinOptions.Count > 0 ? $"\"{PinOptions[i]}\"" : i.ToString();
        }
    }

    internal XAxisPinRowViewModel(TraceRowViewModel owner, string axisName, string? unit,
                                  IReadOnlyList<string> pinOptions, int pinIndex, bool optionsAreLabels)
    {
        _owner            = owner;
        AxisName          = axisName;
        AxisLabel         = string.IsNullOrEmpty(unit) ? axisName : $"{axisName} ({unit})";
        PinOptions        = pinOptions;
        _optionsAreLabels = optionsAreLabels;
        _pinIndex         = Math.Clamp(pinIndex, 0, Math.Max(0, pinOptions.Count - 1));
    }

    partial void OnPinIndexChanged(int value) => _owner.OnXPinChanged();
}
