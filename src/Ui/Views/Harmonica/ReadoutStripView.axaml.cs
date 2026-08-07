using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Ui.Renderers;

namespace CircuitRF.Ui.Views.Harmonica;

/// <summary>
/// §7.5's readout strip. Built in code-behind rather than by an <c>ItemsControl</c> template for one
/// reason worth stating: the strip's contents are a flat list of <c>(label, value, tooltip)</c> and
/// its whole design constraint is DENSITY — a template with an implicit container per item brings
/// padding and a border that §7.5 explicitly does not want ("no section titles, no decoration").
/// </summary>
public partial class ReadoutStripView : UserControl
{
    public ReadoutStripView() => InitializeComponent();

    /// <summary>
    /// Replaces the strip's contents. Rebuilding rather than diffing is deliberate: the strip is a
    /// few dozen short pairs, so a rebuild is cheaper than the bookkeeping a diff would need — and a
    /// diff that got it wrong would leave a stale number on screen, which is the one failure a
    /// readout must not have.
    /// </summary>
    public void SetItems(IReadOnlyList<(string Label, string Value, string Tooltip)> items,
                         IBrush foreground)
    {
        Items.Children.Clear();

        foreach (var (label, value, tooltip) in items)
        {
            var pair = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 3,
                Margin      = new Thickness(0, 0, 12, 2),
            };

            pair.Children.Add(new TextBlock
            {
                Text              = label,
                FontSize          = 10,
                Opacity           = 0.65,
                Foreground        = foreground,
                VerticalAlignment = VerticalAlignment.Center,
            });

            // SelectableTextBlock, never TextBlock — §7.5: "All text is selectable so any readout can
            // be copied." A readout you cannot copy is one you retype by hand into a report.
            pair.Children.Add(new SelectableTextBlock
            {
                Text              = value,
                FontSize          = 10,
                FontWeight        = FontWeight.SemiBold,
                Foreground        = foreground,
                VerticalAlignment = VerticalAlignment.Center,
            });

            // Every element carries a tooltip — §7.5's own concession to newcomers, and the reason
            // the strip can afford to have no section titles.
            ToolTip.SetTip(pair, tooltip);

            Items.Children.Add(pair);
        }
    }

    /// <summary>Projects the theme's own readout role to a brush. §7.9.2: Harmonica.ReadoutText
    /// covers ALL text in this strip, so there is exactly one colour decision here.</summary>
    public static IBrush BrushFor(HarmonicaRenderTheme theme)
    {
        var c = theme.ReadoutText;
        return new SolidColorBrush(Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue));
    }

    // ── §7.5's INPUT half (H7 / R-h7-3, R-h7-4) ──────────────────────────────

    /// <summary>
    /// Replaces the strip's input row.
    ///
    /// <para><b>Every editor commits on Return and on LostFocus, and reverts on Escape</b> — the same
    /// three-key contract <c>SettingsView</c>'s hex field already carries, and for the same reason:
    /// Return that does not set <c>e.Handled</c> reaches the hosting window's default button, and an
    /// edit that only commits on Return is one the user loses by clicking away.</para>
    ///
    /// <para><b>A STRUCTURAL input is marked</b>, because changing one rebuilds the context and resets
    /// the frame ladder — visibly slower than a bias tweak, and the strip should say which is which
    /// rather than leave the user to discover it.</para>
    /// </summary>
    /// <param name="apply">
    /// Writes one input; returns false when the text was rejected. The editor then keeps focus and
    /// the caller's own error text is what explains it.
    /// </param>
    private string _inputSignature = "";

    public void SetInputs(IReadOnlyList<CircuitRF.Ui.Harmonica.HarmonicaInput> inputs,
                          IBrush foreground, Func<string, string, bool> apply)
    {
        // The strip is refreshed on EVERY published frame, and harmonicaRF publishes constantly. A
        // rebuild would destroy the TextBox the user is typing in — the caret vanishing mid-number is
        // the single most disruptive thing this panel could do. So the row is rebuilt only when its
        // SHAPE changes (a different model declares different parameters), and otherwise the values
        // are written in place, skipping whichever editor currently has focus.
        string signature = string.Join("", inputs.Select(i => i.Key + "" + i.Entry));
        if (signature == _inputSignature && Inputs.Children.Count == inputs.Count)
        {
            for (int i = 0; i < inputs.Count; i++)
                if (Inputs.Children[i] is StackPanel row)
                    UpdateInPlace(row, inputs[i], foreground);
            return;
        }
        _inputSignature = signature;

        Inputs.Children.Clear();
        InputRule.IsVisible       = inputs.Count > 0;
        InputRule.Background      = foreground;

        foreach (var input in inputs)
        {
            var pair = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 3,
                Margin      = new Thickness(0, 0, 12, 2),
            };

            pair.Children.Add(new TextBlock
            {
                // A structural input carries a marker rather than a colour: §7.9.2 reserves red, and
                // the strip has exactly one text role.
                Text              = input.Structural ? input.Label + "*" : input.Label,
                FontSize          = 10,
                Opacity           = 0.65,
                Foreground        = foreground,
                VerticalAlignment = VerticalAlignment.Center,
            });

            if (input.Entry == CircuitRF.Ui.Harmonica.HarmonicaInputEntry.Boolean)
            {
                var check = new CheckBox
                {
                    IsChecked         = input.Text is "1" or "true" or "True",
                    Foreground        = foreground,
                    MinWidth          = 0,
                    Padding           = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var state = new EditorState(input.Key, input.Text);
                check.Tag = state;
                check.IsCheckedChanged += (_, _) =>
                {
                    // A programmatic refresh must not read as a click. Same guard SettingsView uses
                    // around its sliders, for the same reason.
                    if (state.Suppress) return;
                    string next = check.IsChecked == true ? "1" : "0";
                    state.Pristine = next;
                    apply(state.Key, next);
                };
                pair.Children.Add(check);
            }
            else
            {
                var box = new TextBox
                {
                    Text              = input.Text,
                    FontSize          = 10,
                    Padding           = new Thickness(3, 1),
                    MinHeight         = 0,
                    MinWidth          = input.Entry == CircuitRF.Ui.Harmonica.HarmonicaInputEntry.Text ? 160 : 56,
                    Foreground        = foreground,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var state = new EditorState(input.Key, input.Text);
                box.Tag = state;

                void Commit()
                {
                    if (box.Text == state.Pristine) return;   // nothing typed — do not churn the model
                    if (!apply(state.Key, box.Text ?? "")) return;
                    state.Pristine = box.Text ?? "";
                }

                box.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Return)
                    {
                        Commit();
                        // Inherited from SettingsView's hex field: without this the hosting window's
                        // default button takes the Return and the dialog closes instead of applying.
                        e.Handled = true;
                    }
                    else if (e.Key == Key.Escape)
                    {
                        box.Text  = state.Pristine;
                        e.Handled = true;
                    }
                };
                box.LostFocus += (_, _) => Commit();

                pair.Children.Add(box);
            }

            if (input.Unit.Length > 0)
                pair.Children.Add(new TextBlock
                {
                    Text              = input.Unit,
                    FontSize          = 10,
                    Opacity           = 0.5,
                    Foreground        = foreground,
                    VerticalAlignment = VerticalAlignment.Center,
                });

            ToolTip.SetTip(pair, input.Structural
                ? input.Tooltip + "  (structural — changing it rebuilds the context and resets the frame ladder)"
                : input.Tooltip);

            Inputs.Children.Add(pair);
        }
    }

    /// <summary>
    /// Writes one already-built input row's value, leaving a FOCUSED editor alone.
    ///
    /// <para>Skipping the focused one is the whole point: a solve that lands while the user is
    /// halfway through typing "2.4" must not replace it with "2".</para>
    /// </summary>
    private static void UpdateInPlace(StackPanel row, CircuitRF.Ui.Harmonica.HarmonicaInput input,
                                      IBrush foreground)
    {
        foreach (var child in row.Children)
        {
            switch (child)
            {
                case TextBox box when box.Tag is EditorState s:
                    if (!box.IsFocused && box.Text != input.Text)
                    {
                        box.Text    = input.Text;
                        s.Pristine  = input.Text;   // or LostFocus would re-apply a value nobody typed
                    }
                    box.Foreground = foreground;
                    break;

                case CheckBox check when check.Tag is EditorState cs:
                    bool wanted = input.Text is "1" or "true" or "True";
                    if (!check.IsFocused && check.IsChecked != wanted)
                    {
                        cs.Suppress = true;
                        try { check.IsChecked = wanted; }
                        finally { cs.Suppress = false; }
                        cs.Pristine = input.Text;
                    }
                    break;

                case TextBlock label:
                    label.Foreground = foreground;
                    break;
            }
        }
    }

    /// <summary>What one editor needs to remember between refreshes: which input it writes, what it
    /// last agreed with the model about, and whether the current change is a programmatic refresh
    /// rather than the user.</summary>
    private sealed class EditorState(string key, string pristine)
    {
        public string Key      { get; } = key;
        public string Pristine { get; set; } = pristine;
        public bool   Suppress { get; set; }
    }

    /// <summary>Shows or clears the input-rejection message. Beside the strip, never thrown.</summary>
    public void SetInputError(string? message, IBrush foreground)
    {
        InputError.Text       = message ?? "";
        InputError.Foreground = foreground;
        InputError.IsVisible  = message is { Length: > 0 };
    }
}
