using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Renderers;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// §7.7's trace picker over harmonicaRF's own <c>DataSet</c> (R-h7-5).
///
/// <para><b>The dialog validates by BUILDING the trace</b>, not by pattern-matching the text: it
/// calls the same <see cref="HarmonicaTracePicker.TryBuild"/> the canvas will, so a spec that this
/// dialog accepts is one the panel can actually draw. Anything else would let a spec through that
/// fails silently on the canvas afterwards.</para>
/// </summary>
public partial class HarmonicaTracePickerDialog : Window
{
    private readonly HarmonicaViewModel _vm;

    // Parameterless ctor satisfies the Avalonia XAML resource loader (AVLN3001).
    public HarmonicaTracePickerDialog() : this(new HarmonicaViewModel()) { }

    public HarmonicaTracePickerDialog(HarmonicaViewModel vm)
    {
        _vm = vm;
        InitializeComponent();

        var offers = HarmonicaTracePicker.Offers(vm.Frame.Published);
        OfferList.ItemsSource = offers;

        if (offers.Count > 0) OfferList.SelectedIndex = 0;
        else StatusLabel.Text = "Nothing has been solved yet — there is no DataSet to plot from.";
    }

    private void OnOfferSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (OfferList.SelectedItem is HarmonicaTracePicker.Offer offer)
        {
            SpecBox.Text = offer.Spec;
            Validate();
        }
    }

    private void OnSpecKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return) return;
        // Validate on Return WITHOUT letting it reach the default button: the user is checking a
        // spec, not committing one. Same reasoning as the colour editor's hex field.
        Validate();
        e.Handled = true;
    }

    private void OnSpecCommitted(object? sender, RoutedEventArgs e) => Validate();

    private bool Validate()
    {
        string spec = SpecBox.Text?.Trim() ?? "";
        if (spec.Length == 0)
        {
            StatusLabel.Text     = "";
            AddButton.IsEnabled  = false;
            return false;
        }

        var plot = HarmonicaTracePicker.TryBuild(
            new HarmonicaPickedTrace(spec, "preview"), _vm.Frame.Published,
            _vm.RenderTheme, out string? error);

        AddButton.IsEnabled = plot is not null;
        StatusLabel.Text    = plot is not null
            ? $"{plot.Traces.Sum(t => t.Points.Count)} points"
            : error ?? "";
        return plot is not null;
    }

    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (!Validate()) return;
        _vm.AddPickedTrace(SpecBox.Text!.Trim(),
                           string.IsNullOrWhiteSpace(LabelBox.Text) ? null : LabelBox.Text!.Trim());
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
