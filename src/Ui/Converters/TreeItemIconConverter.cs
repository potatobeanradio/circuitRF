using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Material.Icons;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Converters;

/// <summary>
/// Maps NodeKind to a Material Icon kind for the project tree view.
/// For TestBench cells, pass the full ProjectTreeNodeViewModel to pick TestTube instead.
/// (The view now binds directly to ProjectTreeNodeViewModel.IconKind, so this converter
/// is retained as a utility but is no longer used in the tree AXAML.)
/// </summary>
public class TreeItemIconConverter : IValueConverter
{
    public static readonly TreeItemIconConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is NodeKind kind)
        {
            return kind switch
            {
                NodeKind.Workspace       => MaterialIconKind.Folder,
                NodeKind.Cell            => MaterialIconKind.IntegratedCircuitChip,
                NodeKind.Library         => MaterialIconKind.BookOpenPageVariant,
                NodeKind.LibrariesGroup  => MaterialIconKind.BookOpenPageVariant,
                NodeKind.CellViewFolder  => MaterialIconKind.FolderOutline,
                NodeKind.ViewFile        => MaterialIconKind.FileOutline,
                NodeKind.DataDisplayFile => MaterialIconKind.ChartLine,
                NodeKind.ColorThemeFile  => MaterialIconKind.Palette,
                NodeKind.KnownFile       => MaterialIconKind.FileOutline,
                NodeKind.KnownFilesGroup => MaterialIconKind.FolderOutline,
                NodeKind.UserFolder      => MaterialIconKind.Folder,
                NodeKind.OtherFile       => MaterialIconKind.FileOutline,
                _                        => MaterialIconKind.FileOutline,
            };
        }
        return MaterialIconKind.FileOutline;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
