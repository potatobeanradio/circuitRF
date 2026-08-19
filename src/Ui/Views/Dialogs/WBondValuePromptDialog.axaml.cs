using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// The one prompt behind every "…" entry in the profile view's context menu (wbond.md §6.4a) — a
/// length in any unit, an angle in degrees, or a choice from a closed set.
///
/// <para><b>Lengths are free text, deliberately, not a numeric stepper.</b> "In any units" is the
/// requirement, and a numeric control cannot accept <c>"2 mil"</c>. Parsing goes through
/// <see cref="WBondUnits.TryParseLength"/> — the single parser — so a bare number always means the
/// document's own display unit and a stated suffix always wins, in every one of these prompts.</para>
///
/// <para>Validation is live: OK is disabled while the text does not parse, and the reason is shown
/// under the field rather than raised as a second dialog after the fact.</para>
/// </summary>
public partial class WBondValuePromptDialog : Window
{
    private WBondUnit _unit = WBondUnit.Mil;
    private Mode _mode = Mode.Length;

    private enum Mode { Length, Angle, Choice, FrequencyGHz }

    // Parameterless ctor satisfies the Avalonia XAML resource loader.
    public WBondValuePromptDialog() => InitializeComponent();

    // ---------------------------------------------------------------- length

    /// <summary>
    /// Prompts for a length. Returns the value in nanometres, or null on cancel.
    /// </summary>
    /// <param name="currentNm">Pre-filled and pre-selected, so retyping one digit is one keystroke.</param>
    public static async Task<long?> PromptLengthAsync(
        Window owner, string title, string prompt, long currentNm, WBondUnit unit)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var dlg = new WBondValuePromptDialog
        {
            Title = title,
            _unit = unit,
            _mode = Mode.Length,
        };

        dlg.PromptText.Text = prompt;
        dlg.SubText.Text = $"A bare number is read as {WBondUnits.Suffix(unit)}; " +
                           "any of nm, um, mm, mil or in may be typed with the value.";
        dlg.ValueBox.Text = Format(WBondUnits.FromNm(currentNm, unit));
        dlg.ValueBox.PlaceholderText = $"e.g. 2 mil, 50um, or {Format(WBondUnits.FromNm(currentNm, unit))}";

        dlg.Opened += (_, _) => { dlg.ValueBox.Focus(); dlg.ValueBox.SelectAll(); };
        dlg.Validate();

        string? text = await dlg.ShowDialog<string?>(owner);
        if (text is null) return null;

        return WBondUnits.TryParseLength(text, unit, out long nm) ? nm : null;
    }

    // ---------------------------------------------------------------- angle

    /// <summary>Prompts for an angle in degrees. Returns degrees, or null on cancel.</summary>
    public static async Task<double?> PromptAngleAsync(Window owner, string title, string prompt)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var dlg = new WBondValuePromptDialog { Title = title, _mode = Mode.Angle };

        dlg.PromptText.Text = prompt;
        dlg.SubText.Text = "Degrees. Positive is counter-clockwise in the layout plane.";
        dlg.ValueBox.Text = "90";
        dlg.ValueBox.PlaceholderText = "e.g. 90, -45, 180";

        dlg.Opened += (_, _) => { dlg.ValueBox.Focus(); dlg.ValueBox.SelectAll(); };
        dlg.Validate();

        string? text = await dlg.ShowDialog<string?>(owner);
        if (text is null) return null;

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double deg)
               && double.IsFinite(deg)
            ? deg
            : null;
    }

    // ---------------------------------------------------------------- frequency

    /// <summary>
    /// Prompts for a frequency in GHz. Returns GHz, or null on cancel.
    ///
    /// <para><b>GHz always, with no auto-ranging</b>, for the same reason the panel fixes picohenries
    /// (<c>PanelReadout</c>'s own note): the row exists to be compared against itself a moment ago,
    /// and a unit that switched under the reader would destroy that. A bond wire's useful range —
    /// hundreds of MHz to tens of GHz — fits in one unit at one decimal.</para>
    /// </summary>
    public static async Task<double?> PromptFrequencyGHzAsync(Window owner, string title, string prompt,
                                                             double currentGHz)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var dlg = new WBondValuePromptDialog { Title = title, _mode = Mode.FrequencyGHz };

        dlg.PromptText.Text = prompt;
        dlg.SubText.Text = "In GHz. The frequency the array inductances are extracted at.";
        dlg.ValueBox.Text = Format(currentGHz);
        dlg.ValueBox.PlaceholderText = "e.g. 10, 2.4, 40";

        dlg.Opened += (_, _) => { dlg.ValueBox.Focus(); dlg.ValueBox.SelectAll(); };
        dlg.Validate();

        string? text = await dlg.ShowDialog<string?>(owner);
        if (text is null) return null;

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double ghz)
               && double.IsFinite(ghz) && ghz > 0.0
            ? ghz
            : null;
    }

    // ---------------------------------------------------------------- choice

    /// <summary>
    /// Prompts for one of a closed set of names — used for Material, where free text would only
    /// invite a typo the model would then have to reject.
    /// </summary>
    public static async Task<string?> PromptChoiceAsync(
        Window owner, string title, string prompt, IReadOnlyList<string> choices, string? current)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(choices);

        var dlg = new WBondValuePromptDialog { Title = title, _mode = Mode.Choice };

        dlg.PromptText.Text = prompt;
        dlg.SubText.Text = "Sets the conductivity used for R and the internal-impedance term.";
        dlg.ValueBox.IsVisible = false;
        dlg.ChoiceBox.IsVisible = true;
        dlg.ChoiceBox.ItemsSource = choices;
        dlg.ChoiceBox.SelectedItem =
            choices.FirstOrDefault(c => string.Equals(c, current, StringComparison.OrdinalIgnoreCase))
            ?? choices.FirstOrDefault();

        return await dlg.ShowDialog<string?>(owner);
    }

    // ---------------------------------------------------------------- plumbing

    private void OnValueChanged(object? sender, TextChangedEventArgs e) => Validate();

    private void OnValueKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (!OkButton.IsEnabled) { e.Handled = true; return; }
        OnOk(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    /// <summary>Live validation: OK reflects whether the current text can actually be used.</summary>
    private void Validate()
    {
        if (_mode == Mode.Choice) { OkButton.IsEnabled = true; return; }

        string text = ValueBox.Text ?? "";
        bool ok;
        string reason;

        if (_mode == Mode.Length)
        {
            ok = WBondUnits.TryParseLength(text, _unit, out long nm) && nm > 0;
            reason = string.IsNullOrWhiteSpace(text)
                ? "Enter a value."
                : "Not a positive length. Try a number, optionally with nm, um, mm, mil or in.";
        }
        else if (_mode == Mode.FrequencyGHz)
        {
            ok = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double f)
                 && double.IsFinite(f) && f > 0.0;
            reason = string.IsNullOrWhiteSpace(text) ? "Enter a frequency." : "Not a positive number of GHz.";
        }
        else
        {
            ok = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                 && double.IsFinite(d);
            reason = string.IsNullOrWhiteSpace(text) ? "Enter an angle." : "Not a number.";
        }

        OkButton.IsEnabled = ok;
        ErrorText.IsVisible = !ok;
        ErrorText.Text = ok ? "" : reason;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOk(object? sender, RoutedEventArgs e) =>
        Close(_mode == Mode.Choice ? ChoiceBox.SelectedItem as string : ValueBox.Text);

    private static string Format(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}
