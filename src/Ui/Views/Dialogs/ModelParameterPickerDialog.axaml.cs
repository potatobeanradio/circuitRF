using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Picks one parameter from the set the chosen compiled model actually declares, so a parameter is
/// added by name from the model rather than typed from memory of it.
///
/// <para><b>A picker, not a list of rows.</b> A compact model declares hundreds of parameters —
/// materialising them all would be unusable, would bloat every <c>.csch</c>, and, worst, would turn
/// every one into an explicit override, freezing the model's own defaults at the moment of placement
/// so that recompiling the model with a changed default would silently not take effect. A parameter
/// the component does not carry is simply not forwarded, which already means "use the model's own
/// default" — that is the property this design preserves.</para>
///
/// <para>Returns the chosen parameter via <c>ShowDialog</c>, or null on cancel — the same
/// return-or-null contract <c>InputNameDialog</c> uses.</para>
/// </summary>
public partial class ModelParameterPickerDialog : Window
{
    private IReadOnlyList<VerilogAParameterInfo> _all = [];

    public ModelParameterPickerDialog() => InitializeComponent();

    public ModelParameterPickerDialog(
        string modelName,
        IReadOnlyList<VerilogAParameterInfo> parameters,
        IReadOnlyCollection<string> alreadyPresent) : this()
    {
        // Already-present parameters are excluded rather than shown disabled: adding one twice is
        // not a thing a user can do, and a list of hundreds should not carry entries that do nothing.
        _all = [.. parameters.Where(p => !alreadyPresent.Contains(p.Name))];

        HeaderText.Text = _all.Count > 0
            ? $"{modelName} declares {parameters.Count} parameter(s). Choose one to set explicitly."
            : $"{modelName} declares {parameters.Count} parameter(s), and this component already carries every one.";

        EmptyText.Text = parameters.Count == 0
            ? "This model declares no settable parameters."
            : "Every parameter this model declares is already on the component.";

        ApplyFilter("");

        SearchBox.TextChanged += (_, _) => ApplyFilter(SearchBox.Text ?? "");
        ChoiceList.DoubleTapped += (_, _) => Commit();
        OkButton.Click     += (_, _) => Commit();
        CancelButton.Click += (_, _) => Close(null);

        Opened += (_, _) => SearchBox.Focus();
    }

    private void ApplyFilter(string query)
    {
        // Matches the name AND the description, because a user hunting for "threshold" is more
        // likely to know what it does than how the model's author abbreviated it.
        var shown = query.Trim().Length == 0
            ? _all
            : [.. _all.Where(p =>
                  p.Name.Contains(query.Trim(), System.StringComparison.OrdinalIgnoreCase) ||
                  p.Description.Contains(query.Trim(), System.StringComparison.OrdinalIgnoreCase))];

        ChoiceList.ItemsSource = shown;
        ChoiceList.SelectedIndex = shown.Count > 0 ? 0 : -1;

        bool anything = _all.Count > 0;
        ChoiceList.IsVisible = anything;
        EmptyText.IsVisible  = !anything;
        OkButton.IsEnabled   = shown.Count > 0;
    }

    private void Commit()
    {
        if (ChoiceList.SelectedItem is VerilogAParameterInfo picked) Close(picked);
    }
}
