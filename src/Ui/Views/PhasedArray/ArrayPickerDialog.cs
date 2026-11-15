using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;

namespace CircuitRF.Ui.Views.PhasedArray;

/// <summary>brief-paa-9 R-paa9-1 — which Phased Antenna Array the tool opens on, when the schematic holds several and none is selected. A
/// picker, since the valid answers are the instances the schematic holds.</summary>
public sealed class ArrayPickerDialog : Window
{
    public ArrayPickerDialog(IReadOnlyList<string> instances)
    {
        Title = "Phased Array";
        Width = 320;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var combo = new ComboBox { ItemsSource = instances, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new(0, 6, 0, 12) };
        var ok = new Button { Content = "Open", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new(8, 0, 0, 0) };
        ok.Click += (_, _) => Close(combo.SelectedItem as string);
        cancel.Click += (_, _) => Close(null);
        Content = new StackPanel
        {
            Margin = new(16),
            Children =
            {
                new TextBlock { Text = "This schematic holds several arrays. Open the tool on:" },
                combo,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
    }
}
