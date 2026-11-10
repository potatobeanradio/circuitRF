using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using CircuitRF.Design.Plugins;

namespace CircuitRF.Ui.Plugins.Views;

/// <summary>
/// A <c>table</c> view (brief-plg-3 §2): the grid a <c>readout</c> with <c>"as": "table"</c> draws
/// (<see cref="PluginUi.Table"/>), every number already in its column's unit with an SI prefix, and <b>Copy</b>, which puts
/// the table on the clipboard as tab-separated text — headers first, cells as shown.
/// </summary>
public sealed class PluginTableViewControl : DockPanel, IPluginViewControl
{
    public const string CopyText = "Copy";

    private readonly ScrollViewer _scroll;
    private PluginReadoutValue? _table;

    public PluginTableViewControl()
    {
        var copy = new Button { Content = CopyText, FontSize = 11, Padding = new Thickness(8, 2), HorizontalAlignment = HorizontalAlignment.Right };
        copy.Click += async (_, _) =>
        {
            if (_table is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            await clipboard.SetTextAsync(TabSeparated(_table));
        };
        var bar = new Border { Padding = new Thickness(6, 4, 6, 0), Child = copy };
        SetDock(bar, Avalonia.Controls.Dock.Top);
        Children.Add(bar);
        _scroll = new ScrollViewer
        {
            Padding = new Thickness(10, 6), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Children.Add(_scroll);
    }

    public string? Show(PluginView view)
    {
        _table = ((PluginTableView)view).Table;
        _scroll.Content = PluginUi.Table(_table);
        return null;
    }

    /// <summary>The table as the clipboard gets it.</summary>
    public static string TabSeparated(PluginReadoutValue table)
        => string.Join("\n", new[] { table.Columns ?? [] }.Concat(table.Rows ?? []).Select(r => string.Join("\t", r))) + "\n";
}
