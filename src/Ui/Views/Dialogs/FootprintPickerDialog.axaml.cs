using System.Linq;
using Avalonia.Controls;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// "Place a footprint by hand" — brief-footprint-3 R-fp3-6b, and the designer's own words: <i>if you
/// don't have a netlist, you can always place the parts by hand that way, on top of the same pads at
/// that layer.</i>
///
/// <para><b>Not the same gesture as re-pointing a selected instance</b> (R-fp3-6a, which is the
/// Footprint combobox in the Properties Inspector): there is nothing selected to re-point. This arms
/// the ordinary instance-placement ghost, so what lands is an ordinary instance carrying no
/// <c>SchematicId</c> — which is correct, because it corresponds to no schematic component.</para>
///
/// <para><b>It lists the built-ins and nothing else, on purpose.</b> Brief 4 decides what the one
/// picker offers — workspace cells, imported parts, Custom — and this brief only needs the gesture to
/// exist. A second catalogue written here would be the thing that brief then has to unpick.</para>
/// </summary>
public partial class FootprintPickerDialog : Window
{
    public FootprintPickerDialog()
    {
        InitializeComponent();

        // Every row reads its metric twin and its millimetres (the series overview's §1e): 0201
        // imperial is 0.60 x 0.30 mm and 0201 METRIC is 0.25 x 0.125 mm — a 2.4x error with nothing
        // to notice it by, so the twin is part of the row, not a tooltip.
        CaseList.ItemsSource = LayoutEditorViewModel.FootprintCases.Select(c => c.Display).ToList();
        CaseList.SelectedIndex = IndexOfDefaultCase();

        DensityCombo.ItemsSource = ViewModels.ParameterEditorViewModel.FootprintDensityOptions;
        DensityCombo.SelectedIndex = 0;

        CancelButton.Click += (_, _) => Close(null);
        OkButton.Click += (_, _) => Accept();
        CaseList.DoubleTapped += (_, _) => Accept();
    }

    private static int IndexOfDefaultCase()
    {
        var cases = LayoutEditorViewModel.FootprintCases;
        for (int i = 0; i < cases.Count; i++)
            if (cases[i].Code == FootprintDefaults.DefaultCaseCode) return i;
        return 0;
    }

    private void Accept()
    {
        int index = CaseList.SelectedIndex;
        if ((uint)index >= (uint)LayoutEditorViewModel.FootprintCases.Count) return;

        Close(FootprintRef.For(
            LayoutEditorViewModel.FootprintCases[index],
            DensityCombo.SelectedIndex switch { 1 => DensityLevel.Most, 2 => DensityLevel.Least, _ => DensityLevel.Nominal }));
    }
}
