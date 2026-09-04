using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// R-L4h-6: shown ONLY when L4f's inference actually had to guess
/// (<see cref="DrillFormatInference.RequiredAGuess"/>) — a file that declared its own units and digit
/// format settles the question itself and must not be interrupted for.
///
/// <para>Pre-filled with the inference <b>and the evidence behind it</b>: which sources were available,
/// what the tool diameters imply, and how the hits compare against the artwork's own bounding box —
/// the last being the strongest single piece of evidence, and free, because the orchestrator is the
/// only place that holds both readers' output. Units and zero suppression are two separate unknowns and
/// get two separate controls; the digit counts get a third, and all three collapse to read-only when
/// every coordinate in the file carries a literal decimal point, because then there is nothing left to
/// answer.</para>
///
/// <para>The precedent is <see cref="DxfUnitsPromptDialog"/>, built for the same reason: a drawing read
/// at the wrong scale is the worst possible silent failure, and a drill file read at the wrong scale
/// is that failure with holes in it.</para>
///
/// <para>Returns a <see cref="GerberImport.DrillFormatChoice"/> via
/// <c>ShowDialog&lt;GerberImport.DrillFormatChoice?&gt;</c> — carrying an override when the user changed
/// something, a null <c>Override</c> when they accepted the inference as it stands, and null itself on
/// Cancel, which aborts the WHOLE import and leaves nothing behind.</para>
/// </summary>
public partial class GerberDrillFormatPromptDialog : Window
{
    private DrillFormatInference? _inferred;

    public GerberDrillFormatPromptDialog() => InitializeComponent();

    /// <param name="remainingFiles">How many further drill files this import would otherwise ask the
    /// same question about. The "apply to all" box appears only when there IS something else to apply
    /// it to — offering it for a lone drill file is a control that does nothing.</param>
    public GerberDrillFormatPromptDialog(
        string fileName, DrillFormatInference inferred, DrillExtentsCheck crossCheck,
        int remainingFiles = 0) : this()
    {
        _inferred = inferred;
        ApplyToAllBox.IsVisible = remainingFiles > 0;
        if (remainingFiles > 0)
            ApplyToAllBox.Content =
                $"Apply this answer to the other {remainingFiles} drill file(s) in this set";

        MessageText.Text =
            $"\"{fileName}\" does not state its coordinate format, so circuitRF inferred one: " +
            $"{inferred}. Confirm it, or correct it — a drill file read at the wrong scale puts every " +
            "hole in the wrong place, and nothing downstream will say so.";

        EvidenceText.Text = string.Join("\n", inferred.Evidence.Append(crossCheck.Report));

        MmRadio.IsChecked = inferred.Unit == GerberUnit.Millimetres;
        InchRadio.IsChecked = inferred.Unit == GerberUnit.Inches;
        LeadingRadio.IsChecked = inferred.ZeroOmission == GerberZeroOmission.Leading;
        TrailingRadio.IsChecked = inferred.ZeroOmission == GerberZeroOmission.Trailing;
        IntegerDigitsBox.Value = inferred.IntegerDigits;
        DecimalDigitsBox.Value = inferred.DecimalDigits;

        // A file whose coordinates all carry a decimal point has no digit count and no suppression
        // convention to get wrong (R-L4f-2's third form). Leaving the controls live would invite an
        // answer to a question that does not exist.
        if (inferred.DecimalCoordinates)
        {
            LeadingRadio.IsEnabled = TrailingRadio.IsEnabled = false;
            DigitsPanel.IsEnabled = false;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnImportClick(object? sender, RoutedEventArgs e)
    {
        bool applyToAll = ApplyToAllBox.IsChecked == true;
        if (_inferred is not { } inferred) { Close(new GerberImport.DrillFormatChoice(null, applyToAll)); return; }

        var unit = InchRadio.IsChecked == true ? GerberUnit.Inches : GerberUnit.Millimetres;
        var zeros = TrailingRadio.IsChecked == true ? GerberZeroOmission.Trailing : GerberZeroOmission.Leading;
        int integerDigits = (int)(IntegerDigitsBox.Value ?? inferred.IntegerDigits);
        int decimalDigits = (int)(DecimalDigitsBox.Value ?? inferred.DecimalDigits);

        // Only what the user actually CHANGED becomes an override. Sending the inference back as an
        // override would re-label every part of it DrillFormatEvidence.Override, and the import's own
        // message would then claim the user settled something the file did.
        var overrides = new DrillFormatOverride(
            unit == inferred.Unit ? null : unit,
            integerDigits == inferred.IntegerDigits ? null : integerDigits,
            decimalDigits == inferred.DecimalDigits ? null : decimalDigits,
            zeros == inferred.ZeroOmission ? null : zeros);

        bool changed = overrides != new DrillFormatOverride();
        Close(new GerberImport.DrillFormatChoice(changed ? overrides : null, applyToAll));
    }
}
