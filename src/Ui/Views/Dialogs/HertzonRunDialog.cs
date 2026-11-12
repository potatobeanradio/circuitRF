using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Design.Interchange.Hzn;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>What <see cref="HertzonRunDialog"/> was answered with. A null setup or sweep is Hertzon's own default (the first enabled).</summary>
public sealed record HertzonRunAnswer(string? Setup, string? Sweep, IReadOnlyList<KeyValuePair<string, string>> Sets);

/// <summary>
/// HZN-8 R-hzn8-2 — Run in Hertzon: the setup and sweep to solve and the variable values to run with, for one design.
/// Variables come from <c>hertzon-cli describe</c>; a box left empty keeps the project's value, so only what the person typed is
/// handed to <c>--set</c>. The dialog collects answers and starts nothing.
///
/// <para>The line under the list is always present, so the buttons never move as a problem appears or clears.</para>
/// </summary>
public static class HertzonRunDialog
{
    /// <summary>The sweep choice that leaves the choice to Hertzon.</summary>
    public const string DefaultSweep = "Hertzon's default";

    public static async Task<HertzonRunAnswer?> AskAsync(Window owner, string project, HertzonDesign design, string version)
    {
        HertzonRunAnswer? answer = null;
        var dialog = Build(project, design, version, a => answer = a);
        await dialog.ShowDialog(owner);
        return answer;
    }

    /// <summary>Why <paramref name="text"/> cannot be a value for <paramref name="name"/>, or null when it can. Hertzon reads the
    /// unit and the expression, and says when it cannot; only the one mistake that is the box's own is caught here.</summary>
    public static string? Problem(string name, string text)
        => text.Contains('=') ? $"{name}: type the value only, such as 1.2mm, without the name." : null;

    public static Window Build(string project, HertzonDesign design, string version, Action<HertzonRunAnswer?> answered)
    {
        HertzonRunAnswer? result = null;
        var dialog = new Window
        {
            Title                 = "Run in Hertzon",
            Width                 = 560,
            SizeToContent         = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize             = false,
            ShowInTaskbar         = false,
        };
        dialog.Closed += (_, _) => answered(result);

        var setups = design.Setups.Where(s => s.Enabled).ToList();
        var setupBox = new ComboBox { ItemsSource = setups.Count > 0 ? setups.Select(s => s.Name).ToList() : ["(none)"], SelectedIndex = 0,
                                      FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        var sweepBox = new ComboBox { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        void FillSweeps()
        {
            var sel = setups.ElementAtOrDefault(Math.Max(0, setupBox.SelectedIndex));
            var names = new List<string> { DefaultSweep };
            if (sel is not null) names.AddRange(sel.Sweeps.Where(w => w.Enabled).Select(w => w.Name));
            names.Add("LastAdaptive");
            sweepBox.ItemsSource = names;
            sweepBox.SelectedIndex = 0;
        }
        setupBox.SelectionChanged += (_, _) => FillSweeps();
        FillSweeps();

        var boxes = new List<(HertzonVariable Var, TextBox Box)>();
        var rows = new StackPanel { Spacing = 4 };
        foreach (var v in design.Variables)
        {
            var box = new TextBox { FontSize = 12, MinWidth = 140, PlaceholderText = "unchanged" };
            ToolTip.SetTip(box, v.Scope == "project" ? "A project variable" : "A design variable");
            boxes.Add((v, box));
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*,Auto") };
            var name = new TextBlock { Text = v.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
            var current = new TextBlock { Text = v.Expression, FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center,
                                          TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(current, 1);
            Grid.SetColumn(box, 2);
            line.Children.Add(name);
            line.Children.Add(current);
            line.Children.Add(box);
            rows.Children.Add(line);
        }
        if (design.Variables.Count == 0)
            rows.Children.Add(new TextBlock { Text = "This design has no variables.", FontSize = 12, Opacity = 0.7 });

        var problem = new TextBlock { FontSize = 11, Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, MinHeight = 32 };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var run    = new Button { Content = "Run", IsDefault = true, IsEnabled = setups.Count > 0 };
        if (setups.Count == 0) problem.Text = "The design has no enabled setup to solve.";

        void Check()
        {
            string? why = boxes.Select(b => Problem(b.Var.Name, b.Box.Text ?? "")).FirstOrDefault(p => p is not null);
            problem.Text = why ?? (setups.Count == 0 ? "The design has no enabled setup to solve." : "");
            run.IsEnabled = why is null && setups.Count > 0;
        }
        foreach (var (_, box) in boxes) box.TextChanged += (_, _) => Check();

        cancel.Click += (_, _) => dialog.Close();
        run.Click += (_, _) =>
        {
            string? sweep = sweepBox.SelectedItem as string;
            result = new HertzonRunAnswer(
                setups.Count > 0 ? setups[Math.Max(0, setupBox.SelectedIndex)].Name : null,
                sweep is null or DefaultSweep ? null : sweep,
                [.. boxes.Where(b => (b.Box.Text ?? "").Trim().Length > 0).Select(b => new KeyValuePair<string, string>(b.Var.Name, b.Box.Text!.Trim()))]);
            dialog.Close();
        };

        var scroll = new ScrollViewer
        {
            Height = Math.Min(240, 34 * Math.Max(3, design.Variables.Count)),
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = rows,
        };

        dialog.Content = new StackPanel
        {
            Margin  = new Thickness(20),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = $"Design {design.Name}", FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"{Path.GetFileName(project)}, solved by Hertzon {version}", FontSize = 11, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = "Setup", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) },
                setupBox,
                new TextBlock { Text = "Sweep", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) },
                sweepBox,
                new TextBlock { Text = "Variables", FontSize = 12, Margin = new Thickness(0, 6, 0, 0) },
                scroll,
                problem,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                    Children = { cancel, run },
                },
            },
        };
        return dialog;
    }
}
