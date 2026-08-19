using System;
using CircuitRF.Ui.Schematic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// VM for ONE excitation tone of a multi-tone HB analysis (harmonic-balance.md §3.2, §6.5):
/// an observable coefficient + unit selector with a live "≈" preview, exactly the shape
/// <see cref="FrequencySpecViewModel"/> uses for a sweep segment — the unit ComboBox rescales the
/// coefficient so the Hz value stays constant, and the parent sets
/// <see cref="CanRemoveSelf"/>.
///
/// <para>The coefficient is stored as a RAW EXPRESSION, never a baked Hz number, so a tone
/// written as <c>RFfreq - ToneSpacing/2</c> survives a dialog round trip as that expression
/// rather than collapsing to the number it happened to evaluate to.</para>
/// </summary>
public sealed partial class HbToneRowViewModel : ObservableObject
{
    private readonly SchematicEditModel _model;
    private Action<HbToneRowViewModel>? _removeCallback;

    /// <summary>Frequency unit list exposed for AXAML x:Static binding.</summary>
    public static readonly string[] FreqUnits = FreqUnitHelper.Units;

    [ObservableProperty] private string _coeff = "1";
    [ObservableProperty] private string _unit  = "GHz";
    private string _prevUnit = "GHz";

    [ObservableProperty] private string _preview = "";

    /// <summary>1-based position, shown as the row's "Tone n" caption.</summary>
    [ObservableProperty] private int _index = 1;

    public string Caption => $"Tone {Index}";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private bool _canRemoveSelf = true;

    public HbToneRowViewModel(SchematicEditModel model, string? coeff = null, string? unit = null)
    {
        _model = model;

        string c = coeff ?? "1";
        string u = string.IsNullOrEmpty(unit) ? "Hz" : unit!;

        // Legacy nicety, matching FrequencySpecViewModel: a plain number stored in Hz displays
        // more readably split into coefficient + unit ("2e9" → 2 GHz). Never split an expression.
        if (u == "Hz")
        {
            var (sc, su) = FreqUnitHelper.Split(c);
            c = sc; u = su;
        }

        _coeff    = c;
        _unit     = u;
        _prevUnit = u;
        _preview  = AnalysisPreviewHelper.ComputeFreqPreview(c, u, model);
    }

    partial void OnIndexChanged(int value) => OnPropertyChanged(nameof(Caption));

    partial void OnCoeffChanged(string value)
        => Preview = AnalysisPreviewHelper.ComputeFreqPreview(value, Unit, _model);

    partial void OnUnitChanged(string value)
    {
        // Rescale so the Hz value is unchanged by picking a different unit.
        Coeff     = FreqUnitHelper.Rescale(Coeff, _prevUnit, value);
        _prevUnit = value;
        Preview   = AnalysisPreviewHelper.ComputeFreqPreview(Coeff, value, _model);
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelf))]
    private void Remove() => _removeCallback?.Invoke(this);

    internal void SetRemoveCallback(Action<HbToneRowViewModel> cb) => _removeCallback = cb;
}
