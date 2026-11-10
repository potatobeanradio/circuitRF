using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Plugins;

namespace CircuitRF.Ui.Plugins;

/// <summary>
/// One parameter's control, as both the action dialog and the panel draw it — built in one place
/// (<see cref="PluginParamControls.Build"/>) so the two cannot differ.
/// </summary>
public sealed class PluginParamControl(PluginParam param, Control editor, Control? trailing, int? chars, Func<string> read, Action<string> write)
{
    private bool _writing;

    public PluginParam Param { get; } = param;
    /// <summary>The input: a box, a picker, a check box — or, for a readout, the value's text.</summary>
    public Control Editor { get; } = editor;
    /// <summary>What sits beside it: the unit, or the file picker's <c>…</c>.</summary>
    public Control? Trailing { get; } = trailing;
    /// <summary>The size hint in force, already clamped; null takes the row's width.</summary>
    public int? Chars { get; } = chars;

    /// <summary>The control's text, as <see cref="PluginParams"/> reads it.</summary>
    public string Read() => read();

    /// <summary>Puts text into the control without it counting as the user's edit.</summary>
    public void Write(string text)
    {
        _writing = true;
        try { write(text); } finally { _writing = false; }
    }

    /// <summary>The user changed the value (never raised by <see cref="Write"/>).</summary>
    public event Action<PluginParamControl>? Changed;
    internal void RaiseChanged() { if (!_writing) Changed?.Invoke(this); }

    public void SetEnabled(bool on)
    {
        Editor.IsEnabled = on;
        if (Trailing is not null) Trailing.IsEnabled = on;
    }

    /// <summary>Marks the control as holding text that cannot be sent — a class the panel styles, and the reason as a
    /// tooltip. Never anything that takes space, so nothing beside it moves.</summary>
    public void SetError(string? error)
    {
        Editor.Classes.Set(PluginParamControls.InvalidClass, error is not null);
        ToolTip.SetTip(Editor, error);
    }

    /// <summary>
    /// Lays the control into <paramref name="grid"/>'s three columns (label, control, trailing) at
    /// <paramref name="row"/>. A size hint makes a box about that many characters wide with its unit or picker right
    /// beside it; without one the box takes the row's width and widens with the window.
    /// </summary>
    public void PlaceIn(Grid grid, int row)
    {
        if (Param.IsTyped && Chars is { } chars && Editor is TextBox sized)
        {
            sized.Width = PluginUi.TypedWidth(sized, chars);
            sized.HorizontalAlignment = HorizontalAlignment.Left;
            var together = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            together.Children.Add(sized);
            if (Trailing is not null) together.Children.Add(Trailing);
            Grid.SetRow(together, row); Grid.SetColumn(together, 1); Grid.SetColumnSpan(together, 2);
            grid.Children.Add(together);
            return;
        }
        Grid.SetRow(Editor, row); Grid.SetColumn(Editor, 1);
        grid.Children.Add(Editor);
        if (Trailing is not null) { Grid.SetRow(Trailing, row); Grid.SetColumn(Trailing, 2); grid.Children.Add(Trailing); }
    }
}

/// <summary>Builds a parameter's control from its declaration (<c>plugins.md</c> §3.1): a quantity is typed with an
/// SI prefix or an expression, a choice, substrate, technology, cell or result is a picker, a file is a box with a
/// <c>…</c> picker, a bool a check box, and a readout the plugin's value.</summary>
public static class PluginParamControls
{
    /// <summary>The class a control carries while its text cannot be sent.</summary>
    public const string InvalidClass = "plg-invalid";

    /// <param name="chars">The size hint in force here — a panel leaf's own, else the declaration's — or null.</param>
    /// <param name="host">The window a file picker opens from.</param>
    public static PluginParamControl Build(PluginParam p, string? workspaceDir, string initial, int? chars, Func<TopLevel?> host)
    {
        PluginParamControl? made = null;
        void Changed() => made?.RaiseChanged();

        Control editor;
        Control? trailing = null;
        Func<string> read;
        Action<string> write;

        switch (p.Kind)
        {
            case PluginParamKind.Bool:
            {
                var box = new CheckBox { IsChecked = initial == "true" };
                box.IsCheckedChanged += (_, _) => Changed();
                read = () => box.IsChecked == true ? "true" : "false";
                write = t => box.IsChecked = t.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";
                editor = box;
                break;
            }
            case PluginParamKind.Choice:
            case PluginParamKind.Substrate:
            case PluginParamKind.Technology:
            case PluginParamKind.Cell:
            case PluginParamKind.Result:
            {
                IReadOnlyList<string> items = PluginParams.ChoicesFor(p, workspaceDir)!;
                var combo = new ComboBox { ItemsSource = items, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
                combo.SelectedItem = items.Contains(initial) ? initial : items.FirstOrDefault();
                if (items.Count == 0) combo.PlaceholderText = workspaceDir is null ? "needs an open workspace" : "none in this workspace";
                combo.SelectionChanged += (_, _) => Changed();
                read = () => combo.SelectedItem as string ?? "";
                write = t => { if (items.Contains(t)) combo.SelectedItem = t; };
                editor = combo;
                break;
            }
            case PluginParamKind.File:
            {
                var box = new TextBox { Text = initial, FontSize = 12 };
                var pick = new Button { Content = "…", FontSize = 12, Padding = new Thickness(8, 2) };
                pick.Click += async (_, _) =>
                {
                    if (host()?.StorageProvider is not { } storage) return;
                    var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                    {
                        AllowMultiple = false,
                        FileTypeFilter = p.Extensions.Count == 0 ? null :
                            [new FilePickerFileType(string.Join(", ", p.Extensions)) { Patterns = [.. p.Extensions.Select(e => "*" + (e.StartsWith('.') ? e : "." + e))] }],
                    });
                    if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) box.Text = path;
                };
                box.TextChanged += (_, _) => Changed();
                read = () => box.Text ?? "";
                write = t => box.Text = t;
                editor = box;
                trailing = pick;
                break;
            }
            case PluginParamKind.Readout:
            {
                var value = new SelectableTextBlock { Text = "—", FontSize = 12, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
                read = () => value.Text ?? "";
                write = t => value.Text = t;
                editor = value;
                break;
            }
            default:
            {
                var box = new TextBox { Text = initial, FontSize = 12 };
                box.TextChanged += (_, _) => Changed();
                read = () => box.Text ?? "";
                write = t => box.Text = t;
                editor = box;
                if (p.Kind == PluginParamKind.Quantity)
                    trailing = new TextBlock { Text = PluginValueFormat.Glyph(p.Unit!), FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, MinWidth = 28 };
                break;
            }
        }

        made = new PluginParamControl(p, editor, trailing,
            p.IsTyped && chars is { } c ? Math.Clamp(c, PluginParam.CharsMin, PluginParam.CharsMax) : null, read, write);
        return made;
    }
}
