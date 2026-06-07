using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Material.Icons;
using CircuitRF.Ui.ViewModels.ProjectTree;

namespace CircuitRF.Ui.Converters;

/// <summary>Maps ProjectTreeItemKind to a Material Icon kind for the tree view.</summary>
public class TreeItemIconConverter : IValueConverter
{
    public static readonly TreeItemIconConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is ProjectTreeItemKind kind
            ? kind switch
            {
                ProjectTreeItemKind.Library     => MaterialIconKind.BookOpenPageVariant,
                ProjectTreeItemKind.Cell        => MaterialIconKind.IntegratedCircuitChip,
                ProjectTreeItemKind.TestBench   => MaterialIconKind.TestTube,
                ProjectTreeItemKind.DataDisplay => MaterialIconKind.ChartLine,
                _                               => MaterialIconKind.File,
            }
            : MaterialIconKind.FileOutline;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
