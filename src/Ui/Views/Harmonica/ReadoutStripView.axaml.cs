using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CircuitRF.Ui.Harmonica;
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
    /// §4 (R1C) — the density floor/ceiling, moved by R-h9r2-21's own +25%: below ~10 pt the strip is
    /// unreadable, above ~20 pt it stops being dense, which is §7.5's whole design constraint.
    ///
    /// <para><b>R-h9r2-21 — "increase text font size by +25%… we may have to tweak this for visual
    /// appeal, so make this text size a variable in the code."</b> The three constants already were
    /// variables (this file's own <see cref="FontSizeFor"/> is a pure function of them, unit-testable
    /// with no live control) — what changed is the NUMBERS: the fraction and both clamps all move by
    /// 1.25×, or the increase evaporates the moment the strip is small (floor) or large (ceiling)
    /// enough to be clamped, which at the default §7.1 layout it very nearly was at the top end.</para>
    /// </summary>
    public const double MinFontSize = 10.0;
    public const double MaxFontSize = 20.0;

    /// <summary>The fraction of the strip's shorter side its font tracks — the same panel-relative
    /// convention <c>HarmonicaPanelRenderer.TitleBandHeight</c> uses for the Smith panels' title
    /// rows, so the whole document scales by one consistent rule rather than several ad hoc ones.
    /// R-h9r2-21: 0.03 × 1.25 = 0.0375 (a ~910×342 px strip: 342 × 0.0375 ≈ 12.8, +25% on the old
    /// ≈10.3).</summary>
    public const double FontSizeFraction = 0.0375;

    /// <summary>
    /// §4 (R1C) — the font-size formula, factored out from <see cref="HarmonicaView"/> so it is
    /// unit-testable without a live control (this repo's <c>Ui.Tests</c> instantiate none). A fraction
    /// of the strip's own placed pixel size, the SHORTER side, clamped to
    /// [<see cref="MinFontSize"/>, <see cref="MaxFontSize"/>].
    /// </summary>
    public static double FontSizeFor(double width, double height)
        => Math.Clamp(Math.Min(width, height) * FontSizeFraction, MinFontSize, MaxFontSize);

    /// <summary>
    /// Replaces the strip's contents — §7.5's General run PLUS R-h9c-6's four columns. Rebuilding
    /// rather than diffing is deliberate: the strip is a few dozen short pairs, so a rebuild is
    /// cheaper than the bookkeeping a diff would need — and a diff that got it wrong would leave a
    /// stale number on screen, which is the one failure a readout must not have.
    ///
    /// <para><b>The four callbacks are all optional</b> so a caller with nothing to offer (a test, a
    /// read-only render) gets a strip with no interaction rather than a null-reference — the same
    /// shape <see cref="SetInputs"/>'s <c>apply</c> already has to be, since not every consumer wants
    /// to route through a live document.</para>
    /// </summary>
    /// <param name="formatFor">Resolves a row's CURRENT format from its <c>FormatKey</c> — read by
    /// every complex row to render itself and to check the right context-menu item.</param>
    /// <param name="onFormatChanged">R-h9c-7 — the user picked a format from a row's right-click
    /// menu. Display-only; never touches the model.</param>
    /// <param name="onCommitEdit">R-h9c-8's inline editor committed a new value for an editable row.
    /// Returns false to reject (the editor's own text stays, exactly as <see cref="SetInputs"/>'s
    /// <c>apply</c> already behaves) — though R-h9c-7's rows have nothing to reject the way a bad
    /// expression string can be; a bad Complex format simply fails to parse before this is even
    /// called.</param>
    /// <param name="onOpenSetDialog">R-h9c-7's "Set…" menu item on an editable row.</param>
    public void SetItems(IReadOnlyList<HarmonicaReadout> items, IBrush foreground, double fontSize = 10,
                         Func<string, ReadoutFormat>? formatFor = null,
                         Action<string, ReadoutFormat>? onFormatChanged = null,
                         Func<HarmonicaReadout, string, bool>? onCommitEdit = null,
                         Action<HarmonicaReadout>? onOpenSetDialog = null)
    {
        formatFor ??= _ => ReadoutFormat.RealImaginary;

        Items.Children.Clear();
        SourceColumn.Children.Clear();
        LoadColumn.Children.Clear();
        MxpColumn.Children.Clear();
        MxeColumn.Children.Clear();

        bool anyColumns = false;

        foreach (var item in items)
        {
            if (item.Column == ReadoutColumn.General)
            {
                Items.Children.Add(BuildGeneralRow(item.Label, item.Value, item.Tooltip, foreground, fontSize));
                continue;
            }

            anyColumns = true;
            var host = item.Column switch
            {
                ReadoutColumn.Source => SourceColumn,
                ReadoutColumn.Load   => LoadColumn,
                ReadoutColumn.Mxp    => MxpColumn,
                _                    => MxeColumn,
            };
            host.Children.Add(BuildColumnRow(item, foreground, fontSize, formatFor,
                                             onFormatChanged, onCommitEdit, onOpenSetDialog));
        }

        ColumnRule.IsVisible    = anyColumns;
        ColumnRule.Background   = foreground;
    }

    /// <summary>One General-column row — §7.5's original flat pair, unchanged shape.</summary>
    private static Control BuildGeneralRow(string label, string value, string tooltip,
                                           IBrush foreground, double fontSize)
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
            FontSize          = fontSize,
            Opacity           = 0.65,
            Foreground        = foreground,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // SelectableTextBlock, never TextBlock — §7.5: "All text is selectable so any readout can
        // be copied." A readout you cannot copy is one you retype by hand into a report.
        pair.Children.Add(new SelectableTextBlock
        {
            Text              = value,
            FontSize          = fontSize,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = foreground,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // Every element carries a tooltip — §7.5's own concession to newcomers, and the reason
        // the strip can afford to have no section titles.
        ToolTip.SetTip(pair, tooltip);

        return pair;
    }

    /// <summary>
    /// One Source/Load/MXP/MXE column row. A row with an empty <see cref="HarmonicaReadout.Value"/>
    /// and no tooltip is a HEADER (R-h9c-6's "MXP 1f0 Load" / a plain "Source"/"Load" column title) —
    /// label only, no value, no interaction. Everything else gets the label/value pair; a complex
    /// row (<see cref="HarmonicaReadout.IsComplex"/>) additionally gets R-h9c-7's right-click format
    /// menu, and an editable one (R-h9c-8) gets the double-click inline editor.
    /// </summary>
    private static Control BuildColumnRow(HarmonicaReadout item, IBrush foreground, double fontSize,
                                          Func<string, ReadoutFormat> formatFor,
                                          Action<string, ReadoutFormat>? onFormatChanged,
                                          Func<HarmonicaReadout, string, bool>? onCommitEdit,
                                          Action<HarmonicaReadout>? onOpenSetDialog)
    {
        var pair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

        bool isHeader = item.Value.Length == 0 && item.Tooltip.Length == 0;

        pair.Children.Add(new TextBlock
        {
            Text              = item.Label,
            FontSize          = fontSize,
            FontWeight        = isHeader ? FontWeight.Bold : FontWeight.Normal,
            Opacity           = isHeader ? 1.0 : 0.65,
            Foreground        = foreground,
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (isHeader) return pair;

        // R-h9r2-25 — rendered from RawValue at the CURRENT format, not from the solve-time Value, so
        // a right-click format change repaints immediately with no re-solve (see DisplayValue).
        string displayText = DisplayValue(item, formatFor);

        // R-h9r2-15 — an EDITABLE row is plain TextBlock, never SelectableTextBlock: the latter
        // consumes a double-tap as select-a-word before it ever reaches this pair's own
        // DoubleTapped handler below, which is why the inline editor never engaged (owner-reported).
        // Every other row (§7.5's "all text is selectable") keeps SelectableTextBlock — there is no
        // competing gesture on them, and an editable row's own value is still reachable for
        // selection one double-click away, inside the editor itself.
        bool editable = item.Editable && onCommitEdit is not null;
        Control valueBlock = editable
            ? new TextBlock
              {
                  Text              = displayText,
                  FontSize          = fontSize,
                  FontWeight        = FontWeight.SemiBold,
                  Foreground        = foreground,
                  VerticalAlignment = VerticalAlignment.Center,
              }
            : new SelectableTextBlock
              {
                  Text              = displayText,
                  FontSize          = fontSize,
                  FontWeight        = FontWeight.SemiBold,
                  Foreground        = foreground,
                  VerticalAlignment = VerticalAlignment.Center,
              };
        pair.Children.Add(valueBlock);

        if (item.Tooltip.Length > 0) ToolTip.SetTip(pair, item.Tooltip);

        if (item.IsComplex)
            pair.ContextMenu = BuildFormatMenu(item, formatFor, onFormatChanged, onOpenSetDialog);

        if (editable)
            pair.DoubleTapped += (_, _) =>
                BeginInlineEdit(pair, valueBlock, item, foreground, fontSize, onCommitEdit!,
                                DisplayValue(item, formatFor));

        return pair;
    }

    /// <summary>
    /// R-h9r2-25 — the text a row ACTUALLY shows: for an <see cref="HarmonicaReadout.IsComplex"/> row
    /// with a <see cref="HarmonicaReadout.RawValue"/>, reformatted through
    /// <see cref="HarmonicaReadoutFormatting"/> at whatever format <paramref name="formatFor"/> says is
    /// current RIGHT NOW — never the format that was current when <c>HarmonicaSolver.BuildReadouts</c>
    /// ran. <c>HarmonicaReadoutFormatting</c> stays the ONE formatter; only WHEN it runs moved. Falls
    /// back to <see cref="HarmonicaReadout.Value"/> for a row with no raw form.
    /// </summary>
    private static string DisplayValue(HarmonicaReadout item, Func<string, ReadoutFormat> formatFor)
    {
        if (item.IsComplex && item.RawValue is { } raw && item.FormatKey is { } key)
        {
            var format = formatFor(key);
            return item.IsGamma
                ? HarmonicaReadoutFormatting.FormatGamma(raw, format)
                : HarmonicaReadoutFormatting.FormatZ(raw, format);
        }
        return item.Value;
    }

    /// <summary>R-h9c-7's right-click flyout: real/imaginary ⇄ magnitude/angle, plus "Set…" on an
    /// editable row. MXP/MXE rows get the format choice but never "Set…" — the owner's own words,
    /// "MXP/MXE impedance … cannot be edited because those are a consequence of the simulation".</summary>
    private static ContextMenu BuildFormatMenu(HarmonicaReadout item,
                                               Func<string, ReadoutFormat> formatFor,
                                               Action<string, ReadoutFormat>? onFormatChanged,
                                               Action<HarmonicaReadout>? onOpenSetDialog)
    {
        var menu  = new ContextMenu();
        var items = new List<object>();

        if (item.FormatKey is { } key && onFormatChanged is not null)
        {
            var current = formatFor(key);

            var ri = new MenuItem
            {
                Header = "Real / Imaginary", ToggleType = MenuItemToggleType.Radio,
                IsChecked = current == ReadoutFormat.RealImaginary,
            };
            ri.Click += (_, _) => onFormatChanged(key, ReadoutFormat.RealImaginary);

            var ma = new MenuItem
            {
                Header = "Magnitude / Angle", ToggleType = MenuItemToggleType.Radio,
                IsChecked = current == ReadoutFormat.MagnitudeAngle,
            };
            ma.Click += (_, _) => onFormatChanged(key, ReadoutFormat.MagnitudeAngle);

            items.Add(ri);
            items.Add(ma);
        }

        if (item.Editable && onOpenSetDialog is not null)
        {
            if (items.Count > 0) items.Add(new Separator());
            var set = new MenuItem { Header = "Set…" };
            set.Click += (_, _) => onOpenSetDialog(item);
            items.Add(set);
        }

        menu.ItemsSource = items;
        return menu;
    }

    /// <summary>
    /// R-h9c-8 — double-click swaps the value cell for a <see cref="TextBox"/> in place, using the
    /// SAME position <see cref="HarmonicaPanelRenderer"/>… no — this is Avalonia CONTROLS, so "the
    /// same geometry the renderer uses" is trivially true: the anchor IS the control's own bounds,
    /// which is exactly R-h9c-8's own point (the schematic's inline editor has to derive a canvas
    /// position; a strip row does not). Commits on Return and LostFocus, reverts on Escape,
    /// <c>e.Handled = true</c> on Return so the hosting window's default button does not take it —
    /// the three-key contract <see cref="SetInputs"/>'s own editors already use.
    ///
    /// <para><b>R-h9r2-16</b> — the box is seeded with exactly what the row is CURRENTLY showing (its
    /// <paramref name="currentDisplayValue"/>, R-h9r2-25's own render-time text — never <c>item.
    /// Value</c> directly, which after a right-click format change would be the STALE solve-time
    /// string), unit included (<c>HarmonicaReadoutFormatting.FormatZ</c> always appends " Ω" to a
    /// termination row; a Γ row carries no unit at all — either way "what's in the box is what the
    /// row showed" holds without a special case here). Only the VALUE is pre-selected, mirroring the
    /// schematic editor's own rule (<c>InlineEditSelLength = param.Expression.Length</c> when a unit
    /// is present) — typing a fresh number replaces the number and leaves the unit in place, rather
    /// than <c>SelectAll</c> eating it. <see cref="HarmonicaReadoutFormatting.TryParse"/> already
    /// tolerates the trailing unit back (it strips a trailing 'Ω' before parsing), so committing needs
    /// no second strip-the-unit step here — and it parses in the row's OWN CURRENT format, which
    /// R-h9r2-25 makes unambiguous (<c>OnReadoutCommitEdit</c> reads it live, same as this seed does).</para>
    /// </summary>
    private static void BeginInlineEdit(StackPanel pair, Control valueControl, HarmonicaReadout item,
                                        IBrush foreground, double fontSize,
                                        Func<HarmonicaReadout, string, bool> onCommitEdit,
                                        string currentDisplayValue)
    {
        int index = pair.Children.IndexOf(valueControl);
        if (index < 0) return;

        string pristine = currentDisplayValue;
        var box = new TextBox
        {
            Text              = pristine,
            FontSize          = fontSize,
            Padding           = new Thickness(2, 0),
            MinHeight         = 0,
            MinWidth          = 70,
            Foreground        = foreground,
            VerticalAlignment = VerticalAlignment.Center,
        };

        pair.Children.RemoveAt(index);
        pair.Children.Insert(index, box);

        void EndEdit(bool commit)
        {
            if (commit && box.Text != pristine) onCommitEdit(item, box.Text ?? "");

            int i = pair.Children.IndexOf(box);
            if (i < 0) return;
            pair.Children.RemoveAt(i);
            pair.Children.Insert(i, valueControl);
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Return) { EndEdit(true); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndEdit(false); e.Handled = true; }
        };
        box.LostFocus += (_, _) => EndEdit(true);

        box.Focus();
        box.SelectionStart = 0;
        box.SelectionEnd   = ValueSelectionLength(pristine);
    }

    /// <summary>
    /// R-h9r2-16 — how much of a seeded editor's text is the VALUE, as opposed to a trailing unit
    /// token. Every editable row's <c>Value</c> carries at most one unit suffix, always a single space
    /// then the unit itself (<c>HarmonicaReadoutFormatting.FormatZ</c>'s " Ω" is the only one an
    /// editable row can show today) — neither real/imaginary nor magnitude/angle complex formatting
    /// ever puts a space anywhere else, so the LAST space is unambiguously the value/unit boundary. A
    /// Γ row (no unit) has no space at all, and the whole string is the value.
    /// </summary>
    private static int ValueSelectionLength(string text)
    {
        int space = text.LastIndexOf(' ');
        return space > 0 ? space : text.Length;
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
                          IBrush foreground, Func<string, string, bool> apply, double fontSize = 10)
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
                    UpdateInPlace(row, inputs[i], foreground, fontSize);
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
                FontSize          = fontSize,
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
                    FontSize          = fontSize,
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
                    FontSize          = fontSize,
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
                                      IBrush foreground, double fontSize)
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
                    box.FontSize   = fontSize;
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
                    check.FontSize = fontSize;
                    break;

                case TextBlock label:
                    label.Foreground = foreground;
                    label.FontSize   = fontSize;
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
    public void SetInputError(string? message, IBrush foreground, double fontSize = 10)
    {
        InputError.Text       = message ?? "";
        InputError.Foreground = foreground;
        InputError.FontSize   = fontSize;
        InputError.IsVisible  = message is { Length: > 0 };
    }
}
