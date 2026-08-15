using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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

        // R6C §5 — one Copy-flyout ContextMenu PER CHUNK, attached once here rather than per row (the
        // pattern this file already uses for the per-row format menu, BuildLiveFormatMenu). A row that
        // carries its OWN ContextMenu (an IsComplex row's format flyout, wired in BuildColumnRowShell)
        // wins on a right-click landing inside it — Avalonia resolves ContextRequested against the
        // nearest ancestor with a ContextMenu set, so a complex row's own menu is never shadowed by
        // this one. Everything else in a chunk (its header row, a plain scalar row, or the chunk's own
        // whitespace) falls through to this menu instead.
        // R-hui-4, owner-reported — "the readout text does not appear horizontally aligned... I want
        // the values to be as close to the metric text as possible and I want the units to be as
        // close to the values as possible... while aligning horizontally with the render above and
        // below." Every row inside ONE chunk is built as a small Label|Value Grid
        // (BuildColumnRowShell/BuildSettingsColumnRow/BuildGeneralRow), so the value sits as close to
        // the label as the chunk's widest label allows (never a generous fixed reserved budget, which
        // is what put daylight between the label and a short value), while still keeping every row's
        // label/value flush with the row above and below it. Scoped PER CHUNK, not globally, so the
        // Settings column's wide labels ("HB Order:") cannot push the MXP column's value out.
        //
        // R7C §1.5 — column alignment is NO LONGER `Grid.IsSharedSizeScope` + `SharedSizeGroup`. An
        // isolated repro (two rows of different label length inside a StackPanel with
        // IsSharedSizeScope=true) measured their value cells at two different X positions — confirmed
        // NOT working when the scope host is a StackPanel in this Avalonia build (see
        // src/Ui/RESOLVED.md). Each chunk's label column is instead pinned to a MEASURED width
        // (ChunkLabelWidth for BuildColumnRowShell/BuildSettingsColumnRow's chunks; SetItems itself
        // for BuildGeneralRow's), the same discipline ReservedValueWidth already uses for the value
        // column — so there is nothing left here to mark as a shared-size scope.
        //
        // R7C §1.1 — the UNIT is no longer a third column at all: it rides IN the label cell's own
        // text ("Pout (dBm):"), composed from the row's own Unit field rather than recovered by
        // parsing the rendered value — see LabelWithUnit and HarmonicaReadout.Unit's own remarks.
        foreach (var host in new[]
                 { SettingsColumn, OperatingPointColumn, TerminationsColumn,
                   MxpColumn, MxeColumn, IntrinsicVdsColumn, IntrinsicIdsColumn })
        {
            AttachChunkCopyMenu(host);
        }
    }

    /// <summary>Builds ONE chunk's Copy flyout — populated lazily on <see cref="ContextMenu.Opening"/>,
    /// same reason <see cref="BuildLiveFormatMenu"/> already does this: the single <c>MenuItem</c> is
    /// constructed on an actual right-click, never on a published frame nobody looked at the menu
    /// for.</summary>
    private void AttachChunkCopyMenu(StackPanel host)
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            var copy = new MenuItem { Header = "Copy" };
            copy.Click += (_, _) => _ = CopyChunkAsync(host);
            menu.ItemsSource = new List<object> { copy };
        };
        host.ContextMenu = menu;
    }

    /// <summary>R6C §5 — puts what the user is seeing in this chunk on the clipboard as tab-delimited
    /// text, one row per line (its header row included), so it pastes straight into Excel as two
    /// columns. Reads straight off the chunk's own built controls — the same text on screen, whatever
    /// built it (a <see cref="HarmonicaReadout"/>-backed column or the Settings column's plain
    /// label/value/unit rows), rather than a second, parallel data path that could disagree with the
    /// screen.</summary>
    private async System.Threading.Tasks.Task CopyChunkAsync(StackPanel host)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clip) return;
        var rows = host.Children.OfType<Grid>().Select(RowText);
        await clip.SetTextAsync(HarmonicaClipboard.RowsText(rows));
    }

    /// <summary>One row's (label, value) pair AS RENDERED — the label is the row's first child; the
    /// value is every TextBlock/SelectableTextBlock/CheckBox after it, joined with a space (a
    /// separate Unit cell reads as its own space-joined token, e.g. "3.5" + "V" reads as "3.5 V",
    /// matching what the row visually shows).</summary>
    private static (string Label, string Value) RowText(Grid row)
    {
        string label = row.Children.Count > 0 && row.Children[0] is TextBlock l ? l.Text ?? "" : "";

        var valueParts = new List<string>();
        for (int i = 1; i < row.Children.Count; i++)
        {
            string? t = row.Children[i] switch
            {
                // SelectableTextBlock derives from TextBlock, so it must be matched FIRST.
                SelectableTextBlock stb => stb.Text,
                TextBlock tb            => tb.Text,
                CheckBox cb             => cb.IsChecked == true ? "1" : "0",
                _                       => null,
            };
            if (!string.IsNullOrEmpty(t)) valueParts.Add(t);
        }

        return (label, string.Join(" ", valueParts));
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
    /// Replaces the strip's contents — §7.5's General run PLUS R-h9c-6's four columns.
    ///
    /// <para><b>brief-harmonicarf-r5 §2 — build-once/update-in-place for the four Source/Load/MXP/MXE
    /// (plus OperatingPoint) columns, the same pattern <see cref="SetInputs"/>'s Settings column
    /// already uses.</b> R3C's own note on this method used to read "rebuilding rather than diffing is
    /// deliberate... a rebuild is cheaper than the bookkeeping a diff would need" — true when the strip
    /// was measured in isolation, false once §1.4/§4.6 actually READ what an unconditional rebuild
    /// costs on a real published frame: ~70-110 real Avalonia controls constructed on the UI thread,
    /// EVERY frame, harmonicaRF publishes constantly. <see cref="UpdateReadoutColumn"/> rebuilds a
    /// column's rows only when its own SHAPE SIGNATURE changes (the marker set, or whether an MXP/MXE
    /// optimum is present/absent) and otherwise writes values into the existing rows — closing R3C's
    /// own follow-up for free (an open Source/Load inline editor no longer gets destroyed and reopened
    /// as a stale row every published frame; <see cref="SettingsRowMayBeOverwritten"/>, already built
    /// for the Settings column, guards it here too). <b>Per column, not whole-strip</b>: adding an L2
    /// marker rebuilds ONLY the Load (or Source) column's own shape — Source/MXP/MXE/OperatingPoint are
    /// each compared and rebuilt independently.</para>
    ///
    /// <para>The General column (<see cref="Items"/>) is UNCHANGED — still a full rebuild every call.
    /// It carries no editable rows (nothing there can be mid-edit) and today is typically zero or one
    /// row ("intrinsic: not located"), so it is not where the ~70-110-control cost lives; giving it the
    /// same signature machinery would add bookkeeping with no measurable payoff.</para>
    ///
    /// <para><b>The rendered output must not change</b> (guardrail 4) — this is a HOW, not a WHAT.</para>
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
    /// brief-harmonicarf-r3b §1.4 — how long the last <see cref="SetItems"/> call took, in
    /// milliseconds. Ui.Tests cannot instantiate this control (no live Avalonia Application), so this
    /// is the only way the strip-rebuild cost §1.4 asks about can be reported: read it after an
    /// interactive drag rather than from an automated benchmark. brief-harmonicarf-r5 §2's own
    /// prediction: in the STEADY STATE of a drag (no marker added or removed, no optimum
    /// appearing/vanishing) this should now read as the cost of writing ~37 strings, not constructing
    /// ~100 controls.
    /// </summary>
    public double LastSetItemsMs { get; private set; }

    public void SetItems(IReadOnlyList<HarmonicaReadout> items, IBrush foreground, double fontSize = 10,
                         Func<string, ReadoutFormat>? formatFor = null,
                         Action<string, ReadoutFormat>? onFormatChanged = null,
                         Func<HarmonicaReadout, string, bool>? onCommitEdit = null,
                         Action<HarmonicaReadout>? onOpenSetDialog = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        formatFor ??= HarmonicaReadoutFormatting.DefaultReadoutFormat;

        Items.Children.Clear();

        var operating = new List<HarmonicaReadout>();
        var source    = new List<HarmonicaReadout>();
        var load      = new List<HarmonicaReadout>();
        var mxp       = new List<HarmonicaReadout>();
        var mxe       = new List<HarmonicaReadout>();
        var intrVds   = new List<HarmonicaReadout>();
        var intrIds   = new List<HarmonicaReadout>();

        foreach (var item in items)
        {
            switch (item.Column)
            {
                case ReadoutColumn.General:
                    Items.Children.Add(BuildGeneralRow(item.Label, item.Unit, item.Value, item.Tooltip, foreground, fontSize));
                    break;
                case ReadoutColumn.OperatingPoint: operating.Add(item); break;
                case ReadoutColumn.Source:         source.Add(item);    break;
                case ReadoutColumn.Load:           load.Add(item);      break;
                case ReadoutColumn.Mxp:            mxp.Add(item);       break;
                case ReadoutColumn.Mxe:            mxe.Add(item);       break;
                case ReadoutColumn.IntrinsicVds:   intrVds.Add(item);   break;
                case ReadoutColumn.IntrinsicIds:   intrIds.Add(item);   break;
            }
        }

        // R7C §1.5 — same fallback as ChunkLabelWidth/UpdateSettingsColumn: SharedSizeGroup does not
        // align a StackPanel/WrapPanel-hosted Grid's columns in this Avalonia build, so General's
        // label column is pinned to a measured width too. Measured in a SECOND pass, after every
        // General row is already a child of Items — a freshly-constructed, not-yet-attached
        // TextBlock's FontFamily has not resolved its real (styled/inherited) value yet, so probing
        // one before attachment would measure the wrong typeface.
        if (Items.Children.Count > 0)
        {
            double generalLabelWidth = 0;
            foreach (var row in Items.Children.OfType<Grid>())
                if (row.Children.Count > 0 && row.Children[0] is TextBlock lbl)
                    generalLabelWidth = Math.Max(generalLabelWidth, ReservedValueWidth(lbl, fontSize, lbl.Text ?? ""));
            foreach (var row in Items.Children.OfType<Grid>())
                if (row.Children.Count > 0 && row.Children[0] is TextBlock lbl)
                    lbl.Width = generalLabelWidth;
        }

        UpdateReadoutColumn(OperatingPointColumn, ReadoutColumn.OperatingPoint, operating,
                            foreground, fontSize, formatFor, onFormatChanged, onCommitEdit, onOpenSetDialog);
        // R-hui-1 — Source and Load are merged into ONE chunk (Terminations, at grid (2,2)); Source
        // rows first, then Load, matching HarmonicaSolver.BuildReadouts' own production order. Each
        // item still carries its own ReadoutColumn.Source/.Load (FormatKey, Side/Band for the editor
        // callbacks) — only the RENDERED HOST is shared, not the data model.
        UpdateReadoutColumn(TerminationsColumn, ReadoutColumn.Source, [.. source, .. load],
                            foreground, fontSize, formatFor, onFormatChanged, onCommitEdit, onOpenSetDialog);
        UpdateReadoutColumn(MxpColumn, ReadoutColumn.Mxp, mxp,
                            foreground, fontSize, formatFor, onFormatChanged, onCommitEdit, onOpenSetDialog);
        UpdateReadoutColumn(MxeColumn, ReadoutColumn.Mxe, mxe,
                            foreground, fontSize, formatFor, onFormatChanged, onCommitEdit, onOpenSetDialog);
        UpdateReadoutColumn(IntrinsicVdsColumn, ReadoutColumn.IntrinsicVds, intrVds,
                            foreground, fontSize, formatFor, onFormatChanged, onCommitEdit, onOpenSetDialog);
        UpdateReadoutColumn(IntrinsicIdsColumn, ReadoutColumn.IntrinsicIds, intrIds,
                            foreground, fontSize, formatFor, onFormatChanged, onCommitEdit, onOpenSetDialog);

        ColumnRule.IsVisible    = operating.Count > 0 || source.Count > 0 || load.Count > 0 ||
                                  mxp.Count > 0 || mxe.Count > 0 || intrVds.Count > 0 || intrIds.Count > 0;
        ColumnRule.Background   = foreground;

        sw.Stop();
        LastSetItemsMs = sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>One General-column row — a 2-column (Label, Value) Grid. Its label column is pinned
    /// to a measured per-column width by <c>SetItems</c>'s own second pass (R7C §1.5 — not a
    /// <c>SharedSizeGroup</c>, which does not align columns hosted in a Panel like <c>Items</c> in
    /// this Avalonia build), so the value sits immediately after the label — never a fixed reserved
    /// gap — while still lining up with the row above/below it. R7C §1.1 — the unit (when the row has
    /// one) rides IN the label, "Label (Unit):", never in a third column; the same convention
    /// <c>BuildColumnRowShell</c>/<c>BuildSettingsColumnRow</c> use.</summary>
    private static Control BuildGeneralRow(string label, string unit, string value, string tooltip,
                                           IBrush foreground, double fontSize)
    {
        var pair = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            ColumnSpacing     = 4,
            Margin            = new Thickness(0, 0, 12, 2),
        };

        var labelBlock = new TextBlock
        {
            Text              = LabelWithUnit(label, unit),
            FontSize          = fontSize,
            Opacity           = 0.65,
            Foreground        = foreground,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(labelBlock, 0);
        pair.Children.Add(labelBlock);

        // SelectableTextBlock, never TextBlock — §7.5: "All text is selectable so any readout can
        // be copied." A readout you cannot copy is one you retype by hand into a report.
        var valueBlock = new SelectableTextBlock
        {
            Text              = value,
            FontSize          = fontSize,
            FontWeight        = FontWeight.SemiBold,
            Foreground        = foreground,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(valueBlock, 1);
        pair.Children.Add(valueBlock);

        // Every element carries a tooltip — §7.5's own concession to newcomers, and the reason
        // the strip can afford to have no section titles.
        ToolTip.SetTip(pair, tooltip);

        return pair;
    }

    /// <summary>
    /// What one Source/Load/MXP/MXE(/OperatingPoint) row remembers between refreshes — the same shape
    /// <see cref="SettingsRowState"/> plays for the Settings column, generalised to a row whose
    /// <see cref="HarmonicaReadout"/> identity can itself change (a right-click format toggle, a
    /// re-solved figure) without the row's STRUCTURE changing. Everything the row's live event
    /// handlers need is read from here AT CLICK/TAP TIME, never from a build-time closure — the row is
    /// built once per <see cref="ColumnShapeSignature"/> and refreshed every <see cref="SetItems"/>
    /// call after that, so a closure capturing the build-time <c>item</c>/<c>formatFor</c> would go
    /// stale the moment either changed.
    /// </summary>
    private sealed class ColumnRowState
    {
        public required HarmonicaReadout Item;
        public bool   IsEditing;
        public IBrush Foreground = Brushes.Black;
        public double FontSize   = 10;
        public Func<string, ReadoutFormat> FormatFor = _ => ReadoutFormat.RealImaginary;
        public Action<string, ReadoutFormat>?              OnFormatChanged;
        public Func<HarmonicaReadout, string, bool>?       OnCommitEdit;
        public Action<HarmonicaReadout>?                   OnOpenSetDialog;
    }

    /// <summary>One column's own last-built shape signature (brief-harmonicarf-r5 §2.2) — per column,
    /// so adding a marker to Load never rebuilds Source/MXP/MXE/OperatingPoint alongside it.</summary>
    private readonly Dictionary<ReadoutColumn, string> _columnSignatures = new();

    /// <summary>
    /// Build-once/update-in-place for ONE column (<paramref name="host"/>) — R3C's own Settings-column
    /// pattern (<see cref="UpdateSettingsColumn"/>), generalised from a fixed 7-key list to a
    /// variable-length, variable-shape one. <see cref="RowShapeKey"/> is what actually determines a
    /// row's STRUCTURE (its label, whether it is a header, whether it carries a format menu, whether
    /// it is editable) — never its current VALUE, which is written into the existing row every call
    /// regardless of whether a rebuild happened this time.
    /// </summary>
    private void UpdateReadoutColumn(StackPanel host, ReadoutColumn column, IReadOnlyList<HarmonicaReadout> items,
                                     IBrush foreground, double fontSize,
                                     Func<string, ReadoutFormat> formatFor,
                                     Action<string, ReadoutFormat>? onFormatChanged,
                                     Func<HarmonicaReadout, string, bool>? onCommitEdit,
                                     Action<HarmonicaReadout>? onOpenSetDialog)
    {
        // Whether a row's Editable flag actually yields an editable WIDGET depends on onCommitEdit
        // being non-null too (R-h9r2-15's own reasoning below) — folded into the signature so a caller
        // whose commit callback appears/disappears (never happens in production; a defensive case for
        // a test or a read-only render) still gets rebuilt rows rather than a stale widget choice.
        bool hasCommit  = onCommitEdit is not null;
        string signature = (hasCommit ? "C" : "c") + string.Join("|", items.Select(RowShapeKey));

        if (!_columnSignatures.TryGetValue(column, out var prev) || prev != signature ||
            host.Children.Count != items.Count)
        {
            _columnSignatures[column] = signature;
            host.Children.Clear();
            foreach (var item in items) host.Children.Add(BuildColumnRowShell(item, hasCommit));
        }

        // R7C §1.5 — Grid.IsSharedSizeScope set on a StackPanel host does NOT align columns in this
        // Avalonia build (confirmed empirically — a minimal isolated repro with two rows of different
        // label length inside a StackPanel with IsSharedSizeScope=true measured value cells at two
        // different X positions; see src/Ui/RESOLVED.md). So the label column's width is pinned
        // explicitly per chunk instead, exactly like ReservedValueWidth already pins the value
        // column: the max, measured against the LIVE typeface, over every non-header row's own label
        // text — computed ONCE per call, then applied to every row uniformly below.
        double labelWidth = ChunkLabelWidth(host, items, fontSize);

        for (int i = 0; i < host.Children.Count && i < items.Count; i++)
            if (host.Children[i] is Grid row)
                UpdateColumnRow(row, items[i], foreground, fontSize, labelWidth, formatFor,
                                onFormatChanged, onCommitEdit, onOpenSetDialog);
    }

    /// <summary>
    /// R7C §1.5 — the pinned LABEL column width for one chunk: the widest non-header label text
    /// <paramref name="items"/> carries, measured against a representative NON-HEADER row's own live
    /// TYPEFACE (a header renders Bold; measuring with it would overstate a Normal-weight label's
    /// width) but at THIS CALL's own <paramref name="fontSize"/> — never the probe control's current
    /// <c>FontSize</c> property, which still holds the PREVIOUS call's value at the point this method
    /// runs (<see cref="UpdateColumnRow"/> is what writes the new one, and it has not run yet this
    /// call). Reading the stale property was tried and measurably wrong: on a font-size change (a
    /// panel resize) it silently measured at the old size for one frame, then jumped once the row's
    /// own property finally caught up — pinned as <c>ChunkLabelWidth_UsesTheCurrentCallsFontSize</c>.
    /// <paramref name="host"/>'s children supply the probe control ONLY — <paramref name="items"/> is
    /// what is actually measured, so this reads the CURRENT frame's labels even before <see
    /// cref="UpdateColumnRow"/> has written them into the rows below. Recomputed every call — cheap (a
    /// handful of short strings) and necessary, since it is a genuine function of every row's CURRENT
    /// label/unit, not a fixed per-row-kind constant the way <see
    /// cref="ReservedValueWidth(TextBlock,double,HarmonicaReadout)"/> is. Returns 0 when there is no
    /// non-header row yet to probe a typeface from.
    /// </summary>
    private static double ChunkLabelWidth(StackPanel host, IReadOnlyList<HarmonicaReadout> items, double fontSize)
    {
        TextBlock? probe = null;
        for (int i = 0; i < host.Children.Count && i < items.Count; i++)
        {
            bool isHeader = items[i].Value.Length == 0 && items[i].Tooltip.Length == 0;
            if (isHeader) continue;
            if (host.Children[i] is Grid { Children: [TextBlock tb, ..] }) { probe = tb; break; }
        }
        if (probe is null) return 0;

        double max = 0;
        foreach (var item in items)
        {
            if (item.Value.Length == 0 && item.Tooltip.Length == 0) continue;   // header — spans both columns
            max = Math.Max(max, ReservedValueWidth(probe, fontSize, LabelWithUnit(item.Label, item.Unit)));
        }
        return max;
    }

    /// <summary>
    /// R7C §1.1 — the ONE place a row's label cell composes its unit, so all three row builders
    /// (<see cref="BuildGeneralRow"/>, <see cref="BuildColumnRowShell"/>, <see
    /// cref="BuildSettingsColumnRow"/>) render the identical convention rather than three that can
    /// drift apart. "Label (Unit):" when the row has a unit, "Label:" when it does not — the owner's
    /// own words, "Merge the units to be with the metric name." Strips a trailing colon
    /// <paramref name="label"/> may already carry (two of <c>HarmonicaInputs.Build</c>'s own labels,
    /// "Freq:"/"HB Order:", bake one in for reasons unrelated to this render-time convention) so this
    /// is the only place a colon is ever added, never a place one is doubled.
    /// </summary>
    private static string LabelWithUnit(string label, string unit)
    {
        string bare = label.EndsWith(':') ? label[..^1] : label;
        return unit.Length > 0 ? $"{bare} ({unit}):" : $"{bare}:";
    }

    /// <summary>What determines a row's STRUCTURE rather than its current display value — a header row
    /// (R-h9c-6's "MXP 1f0 Load" / a plain "Source"/"Load" title) has a different shape from a
    /// label/value pair, and a complex/editable row's widget type and interactivity are themselves
    /// structural, not something the per-frame update step may change.</summary>
    private static string RowShapeKey(HarmonicaReadout item)
    {
        bool isHeader = item.Value.Length == 0 && item.Tooltip.Length == 0;
        return $"{item.Label}{isHeader}{item.IsComplex}{item.Editable}";
    }

    /// <summary>
    /// Builds one row's SKELETON — R7C §1.1's 2-column (Label, Value) <see cref="Grid"/>. Every row's
    /// label column is pinned to the same measured width per chunk (<see cref="ChunkLabelWidth"/>,
    /// written in <see cref="UpdateColumnRow"/>) — <b>not</b> a <c>SharedSizeGroup</c>, which R7C §1.5
    /// found does NOT align columns hosted in a <c>StackPanel</c> in this Avalonia build (confirmed by
    /// an isolated repro; see <c>src/Ui/RESOLVED.md</c>) — so the value sits as close to the label as
    /// the chunk's widest label allows and every row lines up with its neighbours above/below.
    /// <b>The unit is no longer a third column</b> — R-hui-4/R-hui-7's own UNIT column is gone; the
    /// unit instead rides IN the label cell's own text (<see cref="LabelWithUnit"/>), composed from
    /// the row's <see cref="HarmonicaReadout.Unit"/> — never recovered by parsing the rendered value,
    /// which is what let the label column narrow the instant a value rendered "—" (§1.2). An optional
    /// live format menu and an optional double-click editor are wired here too, with NO content
    /// written yet (<see cref="UpdateColumnRow"/> fills it in immediately after, on this same call,
    /// and on every call after). Never rebuilt while <see cref="RowShapeKey"/> stays the same, so
    /// every event handler here reads <see cref="ColumnRowState"/> LIVE at invocation time rather
    /// than closing over this call's own <paramref name="item"/>.
    /// </summary>
    private Grid BuildColumnRowShell(HarmonicaReadout item, bool hasCommit)
    {
        var state = new ColumnRowState { Item = item };
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            ColumnSpacing     = 4,
            Tag               = state,
        };

        bool isHeader = item.Value.Length == 0 && item.Tooltip.Length == 0;

        // R8C §1.4 — a header is the one row a user could not select/copy, which now matters since
        // §1 puts a real impedance in it. SelectableTextBlock derives from TextBlock, so every other
        // match against `is TextBlock` below (UpdateColumnRow, the row-text extraction) still holds —
        // no second branch needed. The one hazard (R-h9r2-15: SelectableTextBlock eats a double-tap
        // as select-a-word before DoubleTapped fires) does not apply here — a header has no inline
        // editor and no DoubleTapped handler, so there is nothing to eat.
        Control label = isHeader
            ? new SelectableTextBlock { VerticalAlignment = VerticalAlignment.Center }
            : new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);
        if (isHeader)
        {
            // A header's own text ("MXP 1f0 Load") is typically far wider than any data row's label
            // in the same chunk — spanning both columns keeps it from forcing the shared Label column
            // (and so every data row's value) wider than the data actually needs.
            Grid.SetColumnSpan(label, 2);
            return row;
        }

        // R-h9r2-15 — an EDITABLE row is plain TextBlock, never SelectableTextBlock: the latter
        // consumes a double-tap as select-a-word before it ever reaches this row's own DoubleTapped
        // handler below, which is why the inline editor never engaged (owner-reported). Every other
        // row (§7.5's "all text is selectable") keeps SelectableTextBlock.
        bool editable = item.Editable && hasCommit;
        Control valueBlock = editable
            ? new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }
            : new SelectableTextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(valueBlock, 1);
        row.Children.Add(valueBlock);

        if (item.IsComplex) row.ContextMenu = BuildLiveFormatMenu(state);

        if (editable)
            row.DoubleTapped += (_, _) =>
            {
                if (state.OnCommitEdit is not { } commit || !SettingsRowMayBeOverwritten(state.IsEditing)) return;
                BeginInlineEdit(valueBlock, state.Foreground, state.FontSize,
                                text => commit(state.Item, text),
                                EditSeedValue(state.Item, state.FormatFor),
                                editing => state.IsEditing = editing);
            };

        return row;
    }

    /// <summary>Writes one row's CURRENT label/value/unit/tooltip/live foreground+font into an
    /// already-built row — skipping the value slot entirely while an editor is open on it, the same
    /// guard <see cref="UpdateSettingsColumnRow"/> already uses.</summary>
    private static void UpdateColumnRow(Grid row, HarmonicaReadout item, IBrush foreground, double fontSize,
                                        double labelWidth,
                                        Func<string, ReadoutFormat> formatFor,
                                        Action<string, ReadoutFormat>? onFormatChanged,
                                        Func<HarmonicaReadout, string, bool>? onCommitEdit,
                                        Action<HarmonicaReadout>? onOpenSetDialog)
    {
        if (row.Tag is not ColumnRowState state) return;
        state.Item            = item;
        state.Foreground      = foreground;
        state.FontSize        = fontSize;
        state.FormatFor       = formatFor;
        state.OnFormatChanged = onFormatChanged;
        state.OnCommitEdit    = onCommitEdit;
        state.OnOpenSetDialog = onOpenSetDialog;

        bool isHeader = item.Value.Length == 0 && item.Tooltip.Length == 0;

        if (row.Children.Count > 0 && row.Children[0] is TextBlock label)
        {
            // R7C §1.1 — the unit rides IN the label ("Pout (dBm):"), from item.Unit — a header row
            // keeps its own bare text (never composed; a header carries no Unit anyway).
            label.Text       = isHeader ? item.Label : LabelWithUnit(item.Label, item.Unit);
            label.FontWeight = isHeader ? FontWeight.Bold : FontWeight.Normal;
            label.Opacity    = isHeader ? 1.0 : 0.65;
            label.Foreground = foreground;
            label.FontSize   = fontSize;
            // R7C §1.5 — SharedSizeGroup does not align a StackPanel-hosted Grid's columns in this
            // Avalonia build; the label column is pinned to the chunk's own measured width instead.
            // A header spans both columns (below), so it is never pinned.
            if (!isHeader) label.Width = labelWidth;
        }

        if (isHeader) { ToolTip.SetTip(row, null); return; }

        if (row.Children.Count > 1 && row.Children[1] is TextBlock valueControl)
        {
            // R7C §1.3 — the row's reserved width is now an actual MEASUREMENT of the widest text
            // this row KIND can ever render (HarmonicaReadoutFormatting.WorstCaseValueTexts), taken
            // against the LIVE typeface — never a character count times an assumed 0.55
            // per-character advance, which was wrong by a different amount for every glyph this strip
            // renders and by a different amount again at every non-integer font size. Set
            // unconditionally, exactly like R-hui-5's own Width line: a pure function of the row's
            // KIND, never of its current value, so it cannot be what moves the shared column while a
            // dragged value's own digit count changes frame to frame.
            valueControl.Width = ReservedValueWidth(valueControl, fontSize, item);

            if (SettingsRowMayBeOverwritten(state.IsEditing))
            {
                // R-h9r2-25 — rendered from RawValue at the CURRENT format, not from the solve-time
                // Value, so a right-click format change repaints immediately with no re-solve. R7C
                // §1.2 — only the VALUE half is kept here; the unit (if any) is already in the label
                // above, so SplitUnit's own Unit half is discarded — SplitUnit is now "strip the
                // suffix", never "discover the unit".
                var (valueText, _) = HarmonicaReadoutFormatting.SplitUnit(DisplayValue(item, formatFor));
                valueControl.Text       = valueText;
                valueControl.Foreground = foreground;
                valueControl.FontSize   = fontSize;
            }
        }

        ToolTip.SetTip(row, item.Tooltip.Length > 0 ? item.Tooltip : null);
    }

    // ── R7C §1.3 — measured reserved widths, replacing the 0.55-per-character guess ─────────────────

    /// <summary>
    /// Cached pixel width of one worst-case STRING at one (family, size, weight) — measuring ~15 short
    /// strings once per font-size change is free; measuring per row per frame is not. Keyed on the
    /// FAMILY NAME rather than the <see cref="FontFamily"/> instance (which does not implement value
    /// equality) and on <see cref="FontWeight"/> (the value cells are SemiBold, the labels are not,
    /// and SemiBold digits measure wider).
    /// </summary>
    private static readonly Dictionary<(string Family, double Size, FontWeight Weight, string WorstCase), double>
        ReservedWidthCache = new();

    /// <summary>
    /// R7C §1.3 — the pixel width of the widest string this row KIND can ever render, measured with
    /// the typeface and size the row is actually drawn with. <paramref name="control"/> supplies the
    /// live typeface (its own FontFamily/FontWeight/FontStyle — SemiBold for a value cell) rather than
    /// assuming a default; <paramref name="item"/>'s row kind decides which worst-case string(s) apply
    /// (<see cref="HarmonicaReadoutFormatting.WorstCaseValueTexts"/> — a complex row's format can flip
    /// live, so both its rect and polar worst cases are measured and the wider one wins).
    /// </summary>
    private static double ReservedValueWidth(TextBlock control, double fontSize, HarmonicaReadout item)
    {
        double max = 0;
        foreach (var worstCase in HarmonicaReadoutFormatting.WorstCaseValueTexts(item))
            max = Math.Max(max, ReservedValueWidth(control, fontSize, worstCase));
        return max;
    }

    /// <summary>The single-worst-case-string overload — what <see cref="SettingsWorstCaseValueText"/>
    /// (the Settings column's own row kinds, which never flip format) uses directly.</summary>
    private static double ReservedValueWidth(TextBlock control, double fontSize, string worstCase)
    {
        var typeface = new Typeface(control.FontFamily, control.FontStyle, control.FontWeight);
        return MeasureWidth(typeface, fontSize, worstCase);
    }

    private static double MeasureWidth(Typeface typeface, double fontSize, string worstCase)
    {
        var key = (typeface.FontFamily.Name, fontSize, typeface.Weight, worstCase);
        if (ReservedWidthCache.TryGetValue(key, out double cached)) return cached;

        var formatted = new FormattedText(worstCase, System.Globalization.CultureInfo.InvariantCulture,
                                          FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black);
        double width = formatted.Width;
        ReservedWidthCache[key] = width;
        return width;
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
            if (item.IsGamma) return HarmonicaReadoutFormatting.FormatGamma(raw, format);
            // Owner: the intrinsic VDS/IDS chunks carry no per-row unit at all — their unit is stated
            // once, in the chunk's own header ("Intrinsic VDS (V)") — this used to fall through to
            // FormatZ unconditionally, which is where the wrong "Ω" on a Volts/Amps row came from.
            if (HarmonicaReadoutFormatting.IsIntrinsicVoltageOrCurrentKey(key))
                return HarmonicaReadoutFormatting.FormatComplex(raw, format);
            return HarmonicaReadoutFormatting.FormatZ(raw, format);
        }
        return item.Value;
    }

    /// <summary>
    /// R7C §1.5 — the VALUE half only, with any unit suffix <see cref="DisplayValue"/> may still carry
    /// stripped off (<see cref="HarmonicaReadoutFormatting.SplitUnit"/>): the unit now lives in the
    /// row's own LABEL cell (§1.1), so an editable termination row's seed no longer carries a trailing
    /// " Ω" the way it did before this brief — <see cref="BeginInlineEdit"/> now selects the WHOLE
    /// seeded text (there is no trailing unit token left to carve out of the selection).
    /// </summary>
    private static string EditSeedValue(HarmonicaReadout item, Func<string, ReadoutFormat> formatFor)
        => HarmonicaReadoutFormatting.SplitUnit(DisplayValue(item, formatFor)).Value;

    /// <summary>
    /// R-h9c-7's right-click flyout: real/imaginary ⇄ magnitude/angle, plus "Set…" on an editable row.
    /// MXP/MXE rows get the format choice but never "Set…" — the owner's own words, "MXP/MXE impedance
    /// … cannot be edited because those are a consequence of the simulation".
    ///
    /// <para><b>brief-harmonicarf-r5 §2 — built ONCE per row (unlike the pre-r5 version, rebuilt every
    /// <see cref="SetItems"/> call) and populated LAZILY on <see cref="ContextMenu.Opening"/></b>, which
    /// fires only when the user actually right-clicks — so the two/three <c>MenuItem</c>s this menu
    /// needs are constructed on a user gesture, never on a published frame the user never even looked
    /// at the menu for. Reads <paramref name="state"/> live, so a format changed elsewhere (the
    /// Settings-style "Set…" dialog, or another row) is reflected the next time this one opens.</para>
    /// </summary>
    private static ContextMenu BuildLiveFormatMenu(ColumnRowState state)
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            var item  = state.Item;
            var items = new List<object>();

            if (item.FormatKey is { } key && state.OnFormatChanged is { } onFormatChanged)
            {
                var current = state.FormatFor(key);

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

            if (item.Editable && state.OnOpenSetDialog is { } onOpenSetDialog)
            {
                if (items.Count > 0) items.Add(new Separator());
                var set = new MenuItem { Header = "Set…" };
                set.Click += (_, _) => onOpenSetDialog(item);
                items.Add(set);
            }

            menu.ItemsSource = items;
        };
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
    /// <para><b>R-h9r2-16, superseded by R7C §1.5</b> — the box is seeded with exactly what the row is
    /// CURRENTLY showing (its <paramref name="currentDisplayValue"/>, R-h9r2-25's own render-time
    /// text — never <c>item.Value</c> directly, which after a right-click format change would be the
    /// STALE solve-time string). <b>The seed no longer carries a unit</b> — R7C §1.1 moved the unit
    /// into the row's own LABEL cell, so <see cref="EditSeedValue"/> now strips it
    /// (<c>HarmonicaReadoutFormatting.SplitUnit</c>) before this is ever called, and the box
    /// <c>SelectAll</c>s: there is no trailing unit token left to carve the selection around, so the
    /// old value-only-not-unit selection rule (mirroring the schematic editor's own
    /// <c>InlineEditSelLength</c>) has nothing left to do. <see cref="HarmonicaReadoutFormatting.
    /// TryParse"/> still tolerates a trailing 'Ω' defensively (a user could still type one), but
    /// committing needs no strip-the-unit step on the SEED side any more — and it parses in the row's
    /// OWN CURRENT format, which R-h9r2-25 makes unambiguous (<c>OnReadoutCommitEdit</c> reads it
    /// live, same as this seed does).</para>
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
        // R7C §1.3 — the editor's own typeface, read off the row it is floating over (a TextBlock or
        // SelectableTextBlock in every production call site) rather than assumed, so its measured
        // width agrees with what the SAME text would measure as in the row itself.
        var editTypeface = valueControl is TextBlock vcTb
            ? new Typeface(vcTb.FontFamily, vcTb.FontStyle, vcTb.FontWeight)
            : new Typeface(FontFamily.Default);

        string pristine = currentDisplayValue;
        var box = new TextBox
        {
            Text              = pristine,
            FontSize          = fontSize,
            Padding           = new Thickness(2, 0),
            MinHeight         = 0,
            Width             = CalcInlineEditWidth(pristine, fontSize, editTypeface),
            Foreground        = foreground,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Owner-reported — a flat MinWidth (70, sized for the longest Z/Γ row) made a short row like
        // "-1.5" look oversized. Width now tracks the text itself — MEASURED against the live
        // typeface (R7C §1.3), never a character count times an assumed per-character advance — and
        // grows/shrinks live as the user types, since the box's LEFT edge is pinned by Canvas.Left
        // below and only its Width changes on TextChanged.
        box.TextChanged += (_, _) => box.Width = CalcInlineEditWidth(box.Text ?? "", fontSize, editTypeface);

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

        // R7C §1.5 — a plain SelectAll now that the seed carries no trailing unit token to carve the
        // selection around (EditSeedValue already stripped it; see this method's own R-h9r2-16 remark).
        box.Focus();
        box.SelectionStart = 0;
        box.SelectionEnd   = pristine.Length;
    }

    /// <summary>
    /// How wide an inline editor should be for <paramref name="text"/> at <paramref name="fontSize"/>
    /// — R7C §1.3: an actual MEASUREMENT of <paramref name="text"/> against <paramref
    /// name="typeface"/>, not an assumed per-character advance (this file's old formula, shared with
    /// <c>SchematicView.CalcInlineEditWidth</c>, is wrong by a different amount for every non-ASCII
    /// glyph this strip renders — see <c>HarmonicaReadoutFormatting</c>'s own §1.3 remark). The floor
    /// at two characters' worth of the typeface's own average advance keeps an empty box (Vgs blank,
    /// Idq-driven) clickable rather than a sliver; this one copy no longer shares a literal formula
    /// with the schematic editor's, which measures nothing and is out of this brief's scope.
    /// </summary>
    private static double CalcInlineEditWidth(string text, double fontSize, Typeface typeface)
    {
        string measured = text.Length > 0 ? text : "0";
        var formatted = new FormattedText(measured, System.Globalization.CultureInfo.InvariantCulture,
                                          FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black);
        double textWidth = text.Length > 0 ? formatted.Width : 0;
        return Math.Max(fontSize * 2.0, textWidth + fontSize * 0.8);
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

    /// <summary>
    /// brief-harmonicarf-r5 §1.1 -- "the SetInputs half timed the same way if it isn't already." It
    /// wasn't; this is the same self-timing convention <see cref="LastSetItemsMs"/> already uses,
    /// wrapping the whole call (both the Settings-column update and the shape-checked rest-of-strip
    /// path below, including its early-return branch) via try/finally.
    /// </summary>
    public double LastSetInputsMs { get; private set; }

    public void SetInputs(IReadOnlyList<CircuitRF.Ui.Harmonica.HarmonicaInput> inputs,
                          IBrush foreground, Func<string, string, bool> apply, double fontSize = 10,
                          CapacitanceRowActions? capacitanceActions = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { SetInputsCore(inputs, foreground, apply, fontSize, capacitanceActions); }
        finally { sw.Stop(); LastSetInputsMs = sw.Elapsed.TotalMilliseconds; }
    }

    private void SetInputsCore(IReadOnlyList<CircuitRF.Ui.Harmonica.HarmonicaInput> inputs,
                               IBrush foreground, Func<string, string, bool> apply, double fontSize,
                               CapacitanceRowActions? capacitanceActions)
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
        UpdateSettingsColumn(named, foreground, fontSize, apply, capacitanceActions);

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
    /// already emits them in, so this is a filter over that list rather than a second ordering.
    ///
    /// <para><b>R7D §3.1</b> appends a zero-content spacer (<see cref="CapacitanceSpacerKey"/>, never a
    /// real input key — see <see cref="CapacitanceSpacerKey"/>'s own remark) then Cgs/Cdg/Cds. Those
    /// four are only ever PRESENT in a call's own <c>named</c> dictionary for an SDD DUT (the same rule
    /// <see cref="CircuitRF.Ui.Harmonica.HarmonicaInputs.Build"/> follows), which is what
    /// <see cref="EffectiveSettingsColumnKeys"/> filters on — this array itself stays the fixed
    /// superset used for row CLASSIFICATION (which keys belong in this column at all, in
    /// <see cref="SetInputsCore"/>) regardless of which document is open.</para>
    /// </summary>
    private static readonly string[] SettingsColumnKeys =
    [
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyVgs,
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyIdq,
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyVds,
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyFrequency,
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyHarmonicCount,
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyCompression,
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyZ0,
        CapacitanceSpacerKey,
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyCgs,
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyCdg,
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyCds,
    ];

    /// <summary>R7D §3.1 — a sentinel, never a real <c>HarmonicaInput.Key</c>, so it is harmless
    /// inside <see cref="SettingsColumnKeys"/>'s own membership test in <see cref="SetInputsCore"/>
    /// (it can never match a real input) while still marking where the divider row goes.</summary>
    private const string CapacitanceSpacerKey = "__r7d.cap-spacer__";

    /// <summary>How many of <see cref="SettingsColumnKeys"/>' entries are the seven ALWAYS-present
    /// ones — everything from <see cref="CapacitanceSpacerKey"/> on is SDD-only.</summary>
    private static readonly int BaseSettingsColumnCount =
        Array.IndexOf(SettingsColumnKeys, CapacitanceSpacerKey);

    /// <summary>
    /// R7D §3.1 — the row set THIS document actually shows: the base seven, plus the spacer and
    /// Cgs/Cdg/Cds when (and only when) <paramref name="named"/> carries them — i.e. an SDD DUT. A
    /// non-SDD document, or a DUT change TO one, is exactly the shape change §1 says "happens on a DUT
    /// change, never per frame" — <see cref="UpdateSettingsColumn"/>'s own rebuild-on-count-mismatch
    /// check is what picks it up.
    /// </summary>
    private static string[] EffectiveSettingsColumnKeys(
        IReadOnlyDictionary<string, CircuitRF.Ui.Harmonica.HarmonicaInput> named)
        => named.ContainsKey(CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyCgs)
            ? SettingsColumnKeys
            : SettingsColumnKeys[..BaseSettingsColumnCount];

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

        /// <summary>R7D §3.4 — mirrors <c>HarmonicaInput.Locked</c>, refreshed every
        /// <see cref="UpdateSettingsColumnRow"/> call so the DoubleTapped guard below reads it live
        /// rather than closing over a build-time value (true for a NONLINEAR Cgs/Cdg/Cds row).</summary>
        public bool Locked;

        /// <summary>R7D §3.4 — the capacitance-row menu's own callbacks, refreshed every
        /// <see cref="UpdateSettingsColumnRow"/> call. Null for every non-capacitance row.</summary>
        public CapacitanceRowActions? CapActions;
    }

    /// <summary>
    /// R7D §3.4 — the three capacitance rows' own right-click menu and inline-edit guard, bound by
    /// KEY (<c>HarmonicaInputs.KeyCgs</c>/<c>KeyCdg</c>/<c>KeyCds</c>) rather than folded into
    /// <see cref="CircuitRF.Ui.Harmonica.HarmonicaInput"/> itself, which stays plain, UI-framework-free
    /// data — the dialog these open needs a owning <c>Window</c> and the schematic layer, neither of
    /// which that class may know about.
    /// </summary>
    /// <param name="IsNonlinear">Whether the named capacitor (by <c>HarmonicaInput.Key</c>) is
    /// currently nonlinear — decides the menu's own wording ("Use Nonlinear…" vs "Edit Nonlinear
    /// C(V)…") and whether "Use Linear" is offered at all.</param>
    /// <param name="OpenEditor">Opens <c>HarmonicaNonlinearCEditor</c> seeded from the capacitor's
    /// current coefficients (linear: just C0; nonlinear: its own C0…Cn) and writes the result back —
    /// the SAME operation for both menu wordings above, which is why there is only one callback.</param>
    /// <param name="UseLinear">Drops a nonlinear capacitor's coefficients back to its own C0 as the
    /// linear value.</param>
    public sealed record CapacitanceRowActions(
        Func<string, bool> IsNonlinear,
        Action<string> OpenEditor,
        Action<string> UseLinear);

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
                                      Func<string, string, bool> apply,
                                      CapacitanceRowActions? capacitanceActions)
    {
        // R7D §3.1 — variable shape now: the base seven, or the base seven plus the spacer and
        // Cgs/Cdg/Cds for an SDD DUT. A DUT change is exactly the "shape changed" case this rebuild
        // check already exists for — no separate signature needed, same as the "rest" WrapPanel below.
        string[] keys = EffectiveSettingsColumnKeys(named);

        if (SettingsColumn.Children.Count != keys.Length)
        {
            SettingsColumn.Children.Clear();
            foreach (string key in keys)
            {
                if (key == CapacitanceSpacerKey) { SettingsColumn.Children.Add(BuildCapacitanceSpacer()); continue; }
                if (named.TryGetValue(key, out var input))
                    SettingsColumn.Children.Add(BuildSettingsColumnRow(input, apply));
            }
        }

        // R7C §1.5 — same fallback as ChunkLabelWidth: SharedSizeGroup does not align a StackPanel-
        // hosted Grid's columns in this Avalonia build, so the label column is pinned to a measured
        // width instead. Every Settings row is non-header, so the whole set is measured — no
        // isHeader exclusion needed here, unlike ChunkLabelWidth's readout chunks. Measured at THIS
        // call's own fontSize parameter, never probe.FontSize — see ChunkLabelWidth's own remark on
        // why that property is stale at this point in the call. The spacer is skipped — it carries no
        // TextBlock to probe or measure.
        double labelWidth = 0;
        Grid? probeRow = null;
        foreach (var child in SettingsColumn.Children)
            if (child is Grid { Children: [TextBlock, ..] } g) { probeRow = g; break; }

        if (probeRow is { Children: [TextBlock probe, ..] })
        {
            double fs = fontSize;
            foreach (string key in keys)
                if (key != CapacitanceSpacerKey && named.TryGetValue(key, out var probeInput))
                    labelWidth = Math.Max(labelWidth, ReservedValueWidth(probe, fs, LabelWithUnit(probeInput.Label, probeInput.Unit)));
        }

        for (int i = 0; i < SettingsColumn.Children.Count && i < keys.Length; i++)
        {
            if (keys[i] == CapacitanceSpacerKey) continue;
            if (SettingsColumn.Children[i] is Grid row && named.TryGetValue(keys[i], out var input))
                UpdateSettingsColumnRow(row, input, foreground, fontSize, labelWidth, capacitanceActions);
        }
    }

    /// <summary>R7D §3.1 — the zero-content divider between Z0 and the capacitance rows: says "these
    /// are separate settings" without a section header, which this strip does not use anywhere. NOT a
    /// <see cref="Grid"/> — every per-row loop above pattern-matches on <c>Grid</c>, so a non-Grid
    /// control here is automatically skipped by measurement and update alike, exactly what a spacer
    /// needs (it must not participate in the label width measurement).</summary>
    private static Control BuildCapacitanceSpacer() => new Border { Height = 6 };

    /// <summary>Builds one Settings row's skeleton — R7C §1.1's 2-column (Label, Value) Grid, its
    /// label column pinned to a measured per-chunk width (<see cref="UpdateSettingsColumn"/>'s own
    /// <c>labelWidth</c>, written in <see cref="UpdateSettingsColumnRow"/>) rather than a
    /// <c>SharedSizeGroup</c> — R7C §1.5 found that does NOT align columns hosted in a
    /// <c>StackPanel</c> in this Avalonia build — so the value sits close to the label, lining up
    /// with the row above/below, with NO content written yet (<see cref="UpdateSettingsColumnRow"/>
    /// fills it in immediately after, on this same call, and on every call after). The unit is no
    /// longer a third column (R-hui-7's own UNIT column is gone); it rides in the label cell's own
    /// text instead (<see cref="LabelWithUnit"/>). The DoubleTapped handler is wired here, once, and
    /// reads <c>state</c>/<c>value.Text</c> LIVE at click time rather than closing over this call's
    /// own <paramref name="input"/> — this row is never rebuilt, so a closure over the build-time
    /// input would go stale the moment the value changes.</summary>
    private Grid BuildSettingsColumnRow(CircuitRF.Ui.Harmonica.HarmonicaInput input,
                                        Func<string, string, bool> apply)
    {
        var state = new SettingsRowState { Key = input.Key };
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            ColumnSpacing     = 4,
            Tag               = state,
        };

        var label = new TextBlock { Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center };
        var value = new TextBlock { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 0);
        Grid.SetColumn(value, 1);
        row.Children.Add(label);
        row.Children.Add(value);

        row.DoubleTapped += (_, _) =>
        {
            // R7D §3.4 — a NONLINEAR capacitance row is edited only through its own right-click menu;
            // the guard is state.Locked (mirrors HarmonicaInput.Locked), read live so a mode switch
            // takes effect on the very next double-tap without this row being rebuilt.
            if (!SettingsRowMayBeOverwritten(state.IsEditing) || state.Locked) return;
            string seed = state.IsPlaceholder ? "" : state.EditSeedText;
            BeginInlineEdit(value, state.Foreground, state.FontSize,
                            text => apply(state.Key, text), seed,
                            editing => state.IsEditing = editing);
        };

        if (IsCapacitanceKey(input.Key))
            row.ContextMenu = BuildCapacitanceRowMenu(state, row);

        return row;
    }

    private static bool IsCapacitanceKey(string key)
        => key is CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyCgs
               or CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyCdg
               or CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyCds;

    /// <summary>
    /// R7D §3.4 — a capacitance row's own right-click flyout. A row that carries its own
    /// <see cref="ContextMenu"/> wins over <see cref="AttachChunkCopyMenu"/>'s chunk-level Copy —
    /// Avalonia resolves <c>ContextRequested</c> against the nearest ancestor that has one, the same
    /// mechanism this file's format flyout already documents (constructor remark). Built lazily on
    /// <see cref="ContextMenu.Opening"/>, the pattern every other menu in this file uses, and reads
    /// <paramref name="state"/> LIVE so a mode switch elsewhere is reflected next time this one opens.
    /// </summary>
    private ContextMenu BuildCapacitanceRowMenu(SettingsRowState state, Grid row)
    {
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            var items = new List<object>();
            bool nonlinear = state.CapActions?.IsNonlinear(state.Key) ?? false;

            var openEditor = new MenuItem { Header = nonlinear ? "Edit Nonlinear C(V)…" : "Use Nonlinear…" };
            openEditor.Click += (_, _) => state.CapActions?.OpenEditor(state.Key);
            items.Add(openEditor);

            if (nonlinear)
            {
                var useLinear = new MenuItem { Header = "Use Linear" };
                useLinear.Click += (_, _) => state.CapActions?.UseLinear(state.Key);
                items.Add(useLinear);
            }

            items.Add(new Separator());
            var copy = new MenuItem { Header = "Copy" };
            copy.Click += (_, _) => _ = CopyRowAsync(row);
            items.Add(copy);

            menu.ItemsSource = items;
        };
        return menu;
    }

    /// <summary>R7D §3.4's own "Copy" — the same single-row shape <see cref="CopyChunkAsync"/> uses
    /// for a whole chunk, scoped to just this row (label + value, as rendered).</summary>
    private async System.Threading.Tasks.Task CopyRowAsync(Grid row)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clip) return;
        await clip.SetTextAsync(HarmonicaClipboard.RowsText([RowText(row)]));
    }

    /// <summary>R7C §1.3 — the worst-case VALUE text for one Settings row, keyed by WHICH setting it
    /// is rather than measured from its current text (the whole point: it must not change when the
    /// text does, or the shared column would reflow every time the text does). Generous rather than
    /// exact — the reserved box may show a little blank space, which costs nothing; a box too narrow
    /// for a legitimate value would. <see cref="ReservedValueWidth(TextBlock,double,string)"/> measures
    /// it against the live typeface, same as <see cref="HarmonicaReadoutFormatting.WorstCaseValueTexts"/>
    /// does for the readout columns.</summary>
    private static string SettingsWorstCaseValueText(string key) => key switch
    {
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyVgs           => "-00.000",
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyIdq           => "-0000.0",
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyVds           => "-00.000",
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyFrequency     => "000.0000000",
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyHarmonicCount => "00",
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyCompression   => "-000.000",
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyZ0            => "00000.000",
        // R7D §3.2 — 2 decimals, always, plus the longest possible suffix.
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyCgs or
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyCdg or
        CircuitRF.Ui.Harmonica.HarmonicaInputs.KeyCds           => "00000.00 (linearized)",
        _                                                        => "0000000000",
    };

    /// <summary>Writes one Settings row's CURRENT value — label (with the structural marker), value
    /// (or its placeholder), unit, tooltip and live foreground/font — skipping the value slot entirely
    /// while an editor is open on it (§1.2 trap 1; see <see cref="SettingsRowMayBeOverwritten"/>).</summary>
    private static void UpdateSettingsColumnRow(Grid row, CircuitRF.Ui.Harmonica.HarmonicaInput input,
                                                IBrush foreground, double fontSize, double labelWidth,
                                                CapacitanceRowActions? capacitanceActions)
    {
        if (row.Tag is not SettingsRowState state) return;
        state.Foreground = foreground;
        state.FontSize   = fontSize;
        state.Locked     = input.Locked;
        if (IsCapacitanceKey(input.Key)) state.CapActions = capacitanceActions;

        if (row.Children.Count > 0 && row.Children[0] is TextBlock label)
        {
            // Owner: no "*" marker on the Settings-column label — Freq/Harmonic Order's structural
            // note stays in the tooltip only (below), which is where every other row's own detail
            // already lives. R7C §1.1 — the unit rides IN the label, same as every readout column.
            label.Text       = LabelWithUnit(input.Label, input.Unit);
            // R7C §1.5 — pinned to the chunk's measured width, not a (non-functional) SharedSizeGroup.
            label.Width      = labelWidth;
            label.Foreground = foreground;
            label.FontSize   = fontSize;
        }

        if (row.Children.Count > 1 && row.Children[1] is TextBlock value)
        {
            // R7C §1.3 — the SAME measured-width discipline as UpdateColumnRow's own Width line: a
            // pure function of WHICH setting this row is, never of its current text, so it cannot be
            // the thing that jitters the shared column. Set unconditionally, unlike the TEXT below.
            value.Width = ReservedValueWidth(value, fontSize, SettingsWorstCaseValueText(input.Key));

            if (SettingsRowMayBeOverwritten(state.IsEditing))
            {
                state.IsPlaceholder = input.Text.Length == 0 && input.Placeholder.Length > 0;
                state.EditSeedText  = input.EditValue;
                value.Text       = state.IsPlaceholder ? input.Placeholder : input.Text;
                value.Opacity    = state.IsPlaceholder ? 0.55 : 1.0;
                value.Foreground = foreground;
                value.FontSize   = fontSize;
            }
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
