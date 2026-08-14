using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
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
    public ReadoutStripView()
    {
        InitializeComponent();

        // R3C follow-up — an inline editor (R-h9c-8's Source/Load boxes, R3C §1's Settings boxes)
        // must close on Escape and on a click OUTSIDE it, and neither happened reliably:
        //
        // Escape: a docked document sits inside WorkspaceWindow, whose OWN <KeyBinding Gesture=
        // "Escape" Command="{Binding DisarmPlacementCommand}"/> marks the KeyDown event Handled
        // BEFORE routing reaches this control at all — SchematicView.OnViewKeyDownTunnel hit the
        // identical problem for its own inline editor and its own comment names the mechanism.
        // handledEventsToo:true is what lets a handler still see and act on an already-Handled key.
        //
        // Click-away: most of this strip is plain, non-focusable TextBlocks/StackPanels — clicking
        // one does not move keyboard focus away from an open editor's TextBox (Avalonia only moves
        // focus to a FOCUSABLE target), so the box's own LostFocus commit never fires. A Tunnel
        // PointerPressed here — closing any open editor the press did not land inside — does not
        // depend on focus actually moving at all.
        AddHandler(KeyDownEvent, OnStripKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnStripPointerPressedTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>Every currently-open inline editor (there can be more than one — nothing stops a
    /// double-click on a second row while a first is still open), its OWN end-edit callback
    /// (<c>commit</c>: true to commit-if-changed, false to revert), and its box. Populated by
    /// <see cref="BeginInlineEdit"/>, drained by that same call's own <c>EndEdit</c>.</summary>
    private readonly List<(TextBox Box, Action<bool> EndEdit)> _openEditors = [];

    private void OnStripKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _openEditors.Count == 0) return;

        // Only the box that actually has keyboard focus is what Escape means to cancel — a second
        // open editor elsewhere in the strip is untouched.
        var focused = _openEditors.FirstOrDefault(x => x.Box.IsFocused);
        if (focused.EndEdit is null) return;

        focused.EndEdit(false);
        e.Handled = true;
    }

    private void OnStripPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (_openEditors.Count == 0 || e.Source is not Visual source) return;

        // Snapshot first — EndEdit removes its own entry from _openEditors, so iterating the live
        // list while calling it would skip whichever entry follows the one just removed.
        foreach (var (box, endEdit) in _openEditors.ToArray())
            if (!ReferenceEquals(source, box) && !box.IsVisualAncestorOf(source))
                endEdit(true);   // outside the box — commit it, exactly what LostFocus would have done
    }

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
    /// <summary>
    /// brief-harmonicarf-r3b §1.4 — how long the last <see cref="SetItems"/> call took to rebuild
    /// every column's controls, in milliseconds. Ui.Tests cannot instantiate this control (no live
    /// Avalonia Application), so this is the only way the strip-rebuild cost §1.4 asks about can be
    /// reported: read it after an interactive drag rather than from an automated benchmark.
    /// </summary>
    public double LastSetItemsMs { get; private set; }

    public void SetItems(IReadOnlyList<HarmonicaReadout> items, IBrush foreground, double fontSize = 10,
                         Func<string, ReadoutFormat>? formatFor = null,
                         Action<string, ReadoutFormat>? onFormatChanged = null,
                         Func<HarmonicaReadout, string, bool>? onCommitEdit = null,
                         Action<HarmonicaReadout>? onOpenSetDialog = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        formatFor ??= _ => ReadoutFormat.RealImaginary;

        Items.Children.Clear();
        OperatingPointColumn.Children.Clear();
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
                ReadoutColumn.OperatingPoint => OperatingPointColumn,
                ReadoutColumn.Source         => SourceColumn,
                ReadoutColumn.Load           => LoadColumn,
                ReadoutColumn.Mxp            => MxpColumn,
                _                            => MxeColumn,
            };
            host.Children.Add(BuildColumnRow(item, foreground, fontSize, formatFor,
                                             onFormatChanged, onCommitEdit, onOpenSetDialog));
        }

        ColumnRule.IsVisible    = anyColumns;
        ColumnRule.Background   = foreground;

        sw.Stop();
        LastSetItemsMs = sw.Elapsed.TotalMilliseconds;
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
    private Control BuildColumnRow(HarmonicaReadout item, IBrush foreground, double fontSize,
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
                BeginInlineEdit(valueBlock, foreground, fontSize,
                                text => onCommitEdit!(item, text),
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
    /// R-h9c-8 — double-click opens a floating <see cref="TextBox"/> over the value cell. Commits on
    /// Return and LostFocus, reverts on Escape, <c>e.Handled = true</c> on Return so the hosting
    /// window's default button does not take it — the three-key contract <see cref="SetInputs"/>'s
    /// own editors already use.
    ///
    /// <para><b>R3C follow-up — floats in <c>EditorOverlay</c>, does NOT splice into the row.</b> The
    /// original scheme removed <paramref name="valueControl"/> from its own row and inserted the
    /// <see cref="TextBox"/> in its place — so the box's own <c>MinWidth</c> (70) widened whichever
    /// row it opened on, and since a <c>StackPanel</c> column sizes to its widest row, that shoved
    /// every column to ITS right sideways the instant an edit opened (owner-reported). The box now
    /// renders in <c>EditorOverlay</c> — a transparent <c>Canvas</c> layered on top of the whole
    /// strip's content, sharing its coordinate space (see the AXAML's own remark) — positioned over
    /// <paramref name="valueControl"/> via <c>TranslatePoint</c>, while <paramref name="valueControl"/>
    /// itself merely goes <c>Opacity = 0</c> (which reserves its layout slot; removing it would not).
    /// No row, column, or anything to its right ever moves, and the editor genuinely paints over
    /// everything, per the owner's own words — it is the TOPMOST sibling in the shared <c>Panel</c>.</para>
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
    ///
    /// <para><b>R3C §1 — generalised to a bare <c>Func&lt;string, bool&gt;</c> commit callback</b>
    /// (was <c>Func&lt;HarmonicaReadout, string, bool&gt;</c>) so the Settings column's rows
    /// (<see cref="BuildSettingsColumnRow"/>, which have no <see cref="HarmonicaReadout"/> at all —
    /// their identity is a plain input KEY) can share this one editor rather than a second copy of it.
    /// The Source/Load/MXP/MXE call site (<see cref="BuildColumnRow"/>) adapts by closing over its own
    /// <c>item</c>. <paramref name="onEditingChanged"/> is optional and lets a caller track "is a
    /// box open right now" in its own per-row state — the Settings column needs this because, unlike
    /// this readout half (rebuilt every frame by <see cref="SetItems"/>, so an editor can never survive
    /// long enough to collide with a refresh), the Settings column is built ONCE and updated in place
    /// forever after (see <see cref="SettingsRowMayBeOverwritten"/>'s own note).</para>
    /// </summary>
    private void BeginInlineEdit(Control valueControl, IBrush foreground, double fontSize,
                                 Func<string, bool> onCommitEdit,
                                 string currentDisplayValue,
                                 Action<bool>? onEditingChanged = null)
    {
        string pristine = currentDisplayValue;
        var box = new TextBox
        {
            Text              = pristine,
            FontSize          = fontSize,
            Padding           = new Thickness(2, 0),
            MinHeight         = 0,
            Width             = CalcInlineEditWidth(pristine, fontSize),
            Foreground        = foreground,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Owner-reported — a flat MinWidth (70, sized for the longest Z/Γ row) made a short row like
        // "-1.5" look oversized. Width now tracks the text itself, same formula and shape as the
        // schematic editor's own CalcInlineEditWidth: grows/shrinks live as the user types, since the
        // box's LEFT edge is pinned by Canvas.Left below and only its Width changes on TextChanged.
        box.TextChanged += (_, _) => box.Width = CalcInlineEditWidth(box.Text ?? "", fontSize);

        var origin = valueControl.TranslatePoint(new Point(0, 0), EditorOverlay) ?? default;
        Canvas.SetLeft(box, origin.X);
        Canvas.SetTop(box, origin.Y);
        EditorOverlay.Children.Add(box);
        valueControl.Opacity = 0;
        onEditingChanged?.Invoke(true);

        void EndEdit(bool commit)
        {
            if (commit && box.Text != pristine) onCommitEdit(box.Text ?? "");

            _openEditors.RemoveAll(x => x.Box == box);
            EditorOverlay.Children.Remove(box);
            valueControl.Opacity = 1;
            onEditingChanged?.Invoke(false);
        }

        // Registered so the strip-level tunnel handlers (constructor) can close THIS editor from
        // Escape or a click outside it — the two follow-up bugs this box's own KeyDown/LostFocus below
        // could not reliably catch on their own. See the constructor's own remark for why.
        _openEditors.Add((box, EndEdit));

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
    /// How wide an inline editor should be for <paramref name="text"/> at <paramref name="fontSize"/>
    /// — the SAME formula (and same reasoning) as <c>SchematicView.CalcInlineEditWidth</c>: an average
    /// per-character width for the IBM Plex Sans proportional font, floored at two characters' worth
    /// so an empty box (Vgs blank, Idq-driven) is still clickable rather than a sliver.
    /// </summary>
    private static double CalcInlineEditWidth(string text, double fontSize)
    {
        double charWidth = fontSize * 0.55;
        return Math.Max(fontSize * 2.0, text.Length * charWidth + fontSize * 0.8);
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
        // R3C §1 — the seven named inputs move to SettingsColumn (double-click text, R-h9c-8's own
        // editor, reused via BeginInlineEdit); everything else stays exactly what it was, in the
        // ORIGINAL always-live-TextBox WrapPanel below. Splitting here, once, keeps both halves'
        // existing logic — the signature/update-in-place discipline below, and SettingsColumn's own —
        // from having to know about each other's rows.
        var named = new Dictionary<string, CircuitRF.Ui.Harmonica.HarmonicaInput>(StringComparer.Ordinal);
        var rest  = new List<CircuitRF.Ui.Harmonica.HarmonicaInput>(inputs.Count);
        foreach (var i in inputs)
        {
            if (Array.IndexOf(SettingsColumnKeys, i.Key) >= 0) named[i.Key] = i;
            else if (Array.IndexOf(HiddenFromStripKeys, i.Key) >= 0) { /* owner: moved to the Settings dialog */ }
            else rest.Add(i);
        }
        UpdateSettingsColumn(named, foreground, fontSize, apply);

        // The strip is refreshed on EVERY published frame, and harmonicaRF publishes constantly. A
        // rebuild would destroy the TextBox the user is typing in — the caret vanishing mid-number is
        // the single most disruptive thing this panel could do. So the row is rebuilt only when its
        // SHAPE changes (a different model declares different parameters), and otherwise the values
        // are written in place, skipping whichever editor currently has focus.
        string signature = string.Join("", rest.Select(i => i.Key + "" + i.Entry));
        if (signature == _inputSignature && Inputs.Children.Count == rest.Count)
        {
            for (int i = 0; i < rest.Count; i++)
                if (Inputs.Children[i] is StackPanel row)
                    UpdateInPlace(row, rest[i], foreground, fontSize);
            return;
        }
        _inputSignature = signature;

        Inputs.Children.Clear();
        InputRule.IsVisible       = rest.Count > 0;
        InputRule.Background      = foreground;

        foreach (var input in rest)
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

    // ── R3C §1 — the Settings column (Vgs, Idq, Vds, f₀, K, compr, Z0) ──────

    /// <summary>The seven inputs R3C §1 moves into their own column, IN THE OWNER'S OWN ORDER — the
    /// same order the brief lists them and <see cref="CircuitRF.Ui.Harmonica.HarmonicaInputs.Build"/>
    /// already emits them in, so this is a filter over that list rather than a second ordering.</summary>
    private static readonly string[] SettingsColumnKeys =
    [
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyVgs,
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyIdq,
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyVds,
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyFrequency,
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyHarmonicCount,
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyCompression,
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyZ0,
    ];

    /// <summary>
    /// Owner request — "Remove the loadline pts, FFTx, charge and M display settings (and the
    /// horizontal bar below them) from the display. These are to be set via a menu item AND a
    /// settings in a separate dialog." These four still come back from
    /// <see cref="CircuitRF.Ui.Harmonica.HarmonicaInputs.Build"/> (it stays the one general-purpose
    /// input list — the Settings dialog reads/writes through the SAME <c>HarmonicaInputs.Apply</c>
    /// path this strip uses, no second write-back), they are just never RENDERED here. Dropped in
    /// <see cref="SetInputs"/> before the shape signature is computed, so removing/adding a model
    /// parameter around them still only rebuilds when it actually needs to.
    /// </summary>
    private static readonly string[] HiddenFromStripKeys =
    [
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyLoadlineSamples,
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyFftOverSample,
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyComputeCharge,
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyMultiplicity,
    ];

    /// <summary>
    /// What one Settings row remembers between refreshes: which input it writes, the live
    /// foreground/font an editor opened THIS INSTANT should use (so a theme/size change between
    /// builds cannot leave a freshly-opened editor showing stale colours — the row is built once and
    /// never rebuilt, unlike <see cref="BuildColumnRow"/>'s rows, which <see cref="SetItems"/> rebuilds
    /// every frame and so never go stale), whether it is mid-edit, and whether its steady-state text is
    /// a PLACEHOLDER (<see cref="CircuitRF.Ui.Harmonica.HarmonicaInput.Placeholder"/>) rather than a
    /// real value — so opening the editor seeds empty, not the placeholder string.
    /// </summary>
    private sealed class SettingsRowState
    {
        public required string Key;
        public bool   IsEditing;
        public bool   IsPlaceholder;
        public IBrush Foreground = Brushes.Black;
        public double FontSize   = 10;

        /// <summary>Owner follow-up — what an editor opened on THIS row right now should seed from.
        /// Usually equal to the displayed text, but Vgs/Idq round their DISPLAY (3 / 1 decimal places)
        /// while the editor still seeds from the full value (<c>HarmonicaInput.EditValue</c>) — kept
        /// on the row's own state, refreshed every <see cref="UpdateSettingsColumnRow"/> call, so the
        /// DoubleTapped handler below reads it LIVE rather than closing over a build-time value.</summary>
        public string EditSeedText = "";
    }

    /// <summary>
    /// R3C §1.2 trap 1 — "SetItems clears and rebuilds all four columns on every call, so a refresh
    /// while an editor is open destroys it mid-edit." <see cref="SetInputs"/> runs on every published
    /// frame too, exactly like <see cref="SetItems"/> — so the Settings column cannot follow
    /// <see cref="BuildColumnRow"/>'s shape (rebuild-from-scratch every call) once its rows are
    /// double-click editable. The chosen discipline: SettingsColumn is built ONCE (its shape never
    /// changes — <see cref="SettingsColumnKeys"/> is a fixed 7-key list
    /// <see cref="CircuitRF.Ui.Harmonica.HarmonicaInputs.Build"/> always emits in full) and every later
    /// call only WRITES VALUES into the existing rows — and this is the one decision that guards a
    /// row's value slot from being overwritten mid-edit. <b>Pure</b> so it is testable without a live
    /// Avalonia control (<c>Ui.Tests</c> cannot instantiate one): the production call site is
    /// <see cref="UpdateSettingsColumnRow"/>, which reads <c>state.IsEditing</c> exactly as this
    /// predicate expects.
    /// </summary>
    internal static bool SettingsRowMayBeOverwritten(bool isEditing) => !isEditing;

    /// <summary>Builds SettingsColumn once (if its shape is not already right) then writes every
    /// row's current value — the same "build lazily, always update" shape <see cref="SetInputs"/>
    /// itself already uses for <c>Inputs</c>, just split into two steps because a Settings row's build
    /// step wires a closure (the DoubleTapped handler) the update step must not repeat.</summary>
    private void UpdateSettingsColumn(IReadOnlyDictionary<string, CircuitRF.Ui.Harmonica.HarmonicaInput> named,
                                      IBrush foreground, double fontSize,
                                      Func<string, string, bool> apply)
    {
        if (SettingsColumn.Children.Count != SettingsColumnKeys.Length)
        {
            SettingsColumn.Children.Clear();
            foreach (string key in SettingsColumnKeys)
                if (named.TryGetValue(key, out var input))
                    SettingsColumn.Children.Add(BuildSettingsColumnRow(input, apply));
        }

        for (int i = 0; i < SettingsColumn.Children.Count && i < SettingsColumnKeys.Length; i++)
            if (SettingsColumn.Children[i] is StackPanel row &&
                named.TryGetValue(SettingsColumnKeys[i], out var input))
                UpdateSettingsColumnRow(row, input, foreground, fontSize);
    }

    /// <summary>Builds one Settings row's skeleton — label, value, optional unit — with NO content yet
    /// (<see cref="UpdateSettingsColumnRow"/> fills it in immediately after, on this same call, and on
    /// every call after). The DoubleTapped handler is wired here, once, and reads <c>state</c>/<c>value
    /// .Text</c> LIVE at click time rather than closing over this call's own <paramref name="input"/> —
    /// this row is never rebuilt, so a closure over the build-time input would go stale the moment the
    /// value changes.</summary>
    private StackPanel BuildSettingsColumnRow(CircuitRF.Ui.Harmonica.HarmonicaInput input,
                                                      Func<string, string, bool> apply)
    {
        var state = new SettingsRowState { Key = input.Key };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Tag = state };

        var label = new TextBlock { Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center };
        var value = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(label);
        row.Children.Add(value);

        if (input.Unit.Length > 0)
            row.Children.Add(new TextBlock { Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center });

        row.DoubleTapped += (_, _) =>
        {
            if (!SettingsRowMayBeOverwritten(state.IsEditing)) return;   // already open — ignore
            string seed = state.IsPlaceholder ? "" : state.EditSeedText;
            BeginInlineEdit(value, state.Foreground, state.FontSize,
                            text => apply(state.Key, text), seed,
                            editing => state.IsEditing = editing);
        };

        return row;
    }

    /// <summary>Writes one Settings row's CURRENT value — label (with the structural marker), value
    /// (or its placeholder), unit, tooltip and live foreground/font — skipping the value slot entirely
    /// while an editor is open on it (§1.2 trap 1; see <see cref="SettingsRowMayBeOverwritten"/>).</summary>
    private static void UpdateSettingsColumnRow(StackPanel row, CircuitRF.Ui.Harmonica.HarmonicaInput input,
                                                IBrush foreground, double fontSize)
    {
        if (row.Tag is not SettingsRowState state) return;
        state.Foreground = foreground;
        state.FontSize   = fontSize;

        if (row.Children.Count > 0 && row.Children[0] is TextBlock label)
        {
            // A structural input carries a marker rather than a colour: §7.9.2 reserves red, and the
            // strip has exactly one text role.
            label.Text       = input.Structural ? input.Label + "*" : input.Label;
            label.Foreground = foreground;
            label.FontSize   = fontSize;
        }

        if (SettingsRowMayBeOverwritten(state.IsEditing) &&
            row.Children.Count > 1 && row.Children[1] is TextBlock value)
        {
            state.IsPlaceholder = input.Text.Length == 0 && input.Placeholder.Length > 0;
            state.EditSeedText  = input.EditValue;
            value.Text       = state.IsPlaceholder ? input.Placeholder : input.Text;
            value.Opacity    = state.IsPlaceholder ? 0.55 : 1.0;
            value.Foreground = foreground;
            value.FontSize   = fontSize;
        }

        if (row.Children.Count > 2 && row.Children[2] is TextBlock unit)
        {
            unit.Text       = input.Unit;
            unit.Foreground = foreground;
            unit.FontSize   = fontSize;
        }

        ToolTip.SetTip(row, input.Structural
            ? input.Tooltip + "  (structural — changing it rebuilds the context and resets the frame ladder)"
            : input.Tooltip);
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
