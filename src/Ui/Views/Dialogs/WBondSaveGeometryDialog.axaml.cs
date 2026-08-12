using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.WBond;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// Asks whether the layout geometry travels inside the `.wBond`, and states what embedding costs
/// (wbond.md §9.1 / WB33).
///
/// <para><b>Named before the save, never reported after it.</b> A file that quietly lost
/// parametricity on a PDK cell is discovered by whoever receives it, which is the worst possible
/// moment — so the cells are listed here, while the user can still choose to reference instead.</para>
/// </summary>
public partial class WBondSaveGeometryDialog : Window
{
    public enum Choice { Cancel, Reference, Embed }

    // Parameterless ctor satisfies the Avalonia XAML resource loader.
    public WBondSaveGeometryDialog() : this(default) { }

    public WBondSaveGeometryDialog(WBondGeometryEmbedding.EmbedPlan plan)
    {
        InitializeComponent();

        if (plan.PdkFlattened is { Count: > 0 } flattened)
        {
            FlattenPanel.IsVisible = true;
            FlattenHeader.Text = flattened.Count == 1
                ? "Embedding flattens 1 vendor cell:"
                : $"Embedding flattens {flattened.Count} vendor cells:";
            FlattenList.Text = string.Join(", ", flattened.Select(Path.GetFileName));
        }

        if (plan.Unresolved is { Count: > 0 } unresolved)
        {
            UnresolvedPanel.IsVisible = true;
            UnresolvedHeader.Text = unresolved.Count == 1
                ? "1 cell reference could not be resolved and will not be included:"
                : $"{unresolved.Count} cell references could not be resolved and will not be included:";
            UnresolvedList.Text = string.Join(", ", unresolved);
        }

        if (plan.NativeKept is { Count: > 0 } kept)
        {
            NativeNote.IsVisible = true;
            NativeNote.Text = kept.Count == 1
                ? "1 circuitRF cell stays parametric."
                : $"{kept.Count} circuitRF cells stay parametric.";
        }
    }

    public static async Task<Choice> ShowAsync(Window owner, WBondGeometryEmbedding.EmbedPlan plan)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return await new WBondSaveGeometryDialog(plan).ShowDialog<Choice>(owner);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(Choice.Cancel);

    private void OnSave(object? sender, RoutedEventArgs e) =>
        Close(EmbedRadio.IsChecked == true ? Choice.Embed : Choice.Reference);
}
