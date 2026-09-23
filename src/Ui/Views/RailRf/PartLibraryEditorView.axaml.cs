using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace CircuitRF.Ui.Views.RailRf;

/// <summary>
/// The <c>.crlib</c> part library editor's view
/// (docs/sonnet-briefs/brief-railrf-24-part-library-editor.md R-rail24-1b/-1c).
///
/// <para><b>It holds no library logic, and that is the whole point of R-rail24-1a.</b> Every command,
/// every refusal sentence and every derived number is
/// <see cref="Ui.RailRf.PartLibraryEditorViewModel"/>'s, which is framework-free and drivable from a
/// test with no host; the Save picker is the workspace's. A second copy of any of it here would be
/// the copy that drifts. What DOES live here is the one thing a view model cannot answer — what a
/// pointer landing on a control means for the row it is in.</para>
/// </summary>
public partial class PartLibraryEditorView : UserControl
{
    // InitializeComponent(), NEVER AvaloniaXamlLoader.Load(this) directly — the generated method
    // loads the XAML *and* assigns every x:Name field. Declaring one of our own HIDES it, so the
    // named controls stay null and the constructor throws on the first one it touches: a
    // NullReferenceException with nothing but the constructor in its stack, raised during a layout
    // pass when the DataTemplate first builds the view. See src/Ui/CLAUDE.md, and the same note on
    // TechnologyExportDialog.
    public PartLibraryEditorView()
    {
        InitializeComponent();

        // Owner report, 2026-09-20: "it's hard to select a row because there are so many text edit
        // boxes."
        //
        // A ListBoxItem selects on a pointer press it actually RECEIVES. Both of these rows are
        // editable cells edge to edge, and a TextBox handles its own press — so the press never
        // bubbled as an unhandled one and the item never selected. The user was left hunting for the
        // few pixels of gutter between two boxes, on a list whose selection is not decoration: the
        // bias-curve editor below acts on the selected row, and the point sub-editor's Remove acts on
        // the selected point.
        //
        // FOCUS is the right signal rather than the press, and it is why this is not a
        // PointerPressed handler: it covers tabbing into a cell and a programmatic focus as well as a
        // click, so the row the caret is in and the row the panel below is about can never be two
        // different rows. GotFocus bubbles, so one handler on the list serves every cell in it —
        // AddHandler with handledEventsToo, because the TextBox marks the event handled on its way
        // out.
        foreach (var list in new[] { PartsList, BiasPointList })
            list.AddHandler(InputElement.GotFocusEvent, OnCellGotFocus,
                            RoutingStrategies.Bubble, handledEventsToo: true);

        // …and Escape puts the row down again (owner request, 2026-09-20), which closes the
        // bias-curve panel with it — HasSelectedRow is what makes it visible at all.
        //
        // TUNNELLING, and handledEventsToo, because the gesture has to arrive whatever has focus: a
        // cell being edited is the ordinary case, and several controls treat Escape as their own. It
        // is NOT marked handled here, so a control that does want it (a popup, an inline editor
        // opened over a cell) still gets it afterwards.
        AddHandler(InputElement.KeyDownEvent, OnKeyDown,
                   RoutingStrategies.Tunnel, handledEventsToo: true);

        // A table pasted into a bias-curve cell is a CURVE, not a cell value (owner request,
        // 2026-09-23): pasting 200 rows of "bias<TAB>capacitance" into one text box would otherwise
        // put all of it in that box. Only multi-line text is taken — a single value still pastes into
        // the cell as it always has.
        BiasPointList.AddHandler(TextBox.PastingFromClipboardEvent, OnBiasCellPasting,
                                 RoutingStrategies.Bubble);

        // The curve cells commit on LostFocus (a bias commit re-sorts the curve), so Enter has to
        // end the edit too: it moves focus to the list, which commits the cell.
        BiasPointList.AddHandler(InputElement.KeyDownEvent, OnBiasCellKeyDown, RoutingStrategies.Tunnel);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is Ui.RailRf.PartLibraryDocument doc)
                doc.ViewModel.BiasCurvePasteRequested = () => _ = PasteBiasCurveAsync(doc.ViewModel);
        };
    }

    private void OnBiasCellKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.Source is not TextBox) return;
        e.Handled = true;
        BiasPointList.Focus();
    }

    /// <summary>The clipboard's text, or null. The clipboard needs a top level, which is why this
    /// is the view's and not the view model's.</summary>
    private async Task<string?> ClipboardTextAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return null;
        try { return await clipboard.TryGetTextAsync(); }
        catch (Exception) { return null; }
    }

    private async Task PasteBiasCurveAsync(Ui.RailRf.PartLibraryEditorViewModel vm)
    {
        if (await ClipboardTextAsync() is { Length: > 0 } text) vm.PasteBiasCurve(text);
    }

    /// <summary>
    /// A paste into a curve cell. The paste is always taken over — whether the clipboard holds a
    /// table is only known after an asynchronous read — and a single line is put back into the cell
    /// as the text box would have.
    /// </summary>
    private async void OnBiasCellPasting(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not TextBox box) return;
        if (DataContext is not Ui.RailRf.PartLibraryDocument doc) return;
        e.Handled = true;

        if (await ClipboardTextAsync() is not { Length: > 0 } text) return;
        if (text.Trim().Contains('\n'))
            doc.ViewModel.PasteBiasCurve(text);
        else
            box.SelectedText = text;
    }

    /// <summary>
    /// Writes a chosen footprint row onto the part library row the combo box is in (R-rail24-4a).
    /// </summary>
    /// <remarks>
    /// <b>Not left to the text binding alone.</b> An editable <c>ComboBox</c> is two-way bound to the
    /// row’s <c>Footprint</c>, and Avalonia does put a selected item’s string into its text box — but
    /// which string that is depends on the control’s own text-search rules, and the value landing in
    /// the file is not a thing to leave to them. The view model’s
    /// <c>SelectFootprint</c> is the one write path; it ignores a selection that changes nothing, so
    /// the pair cannot record two undo entries for one gesture and binding-time selection cannot
    /// open the document dirty.
    ///
    /// <para>This is the whole of the view’s part in it: no list, no token, no refusal sentence
    /// — those are all <c>PartLibraryRowViewModel</c>’s, which is framework-free and driven from the
    /// gate with no host.</para>
    /// </remarks>
    private static void OnFootprintSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { DataContext: Ui.RailRf.PartLibraryRowViewModel row } combo) return;
        if (combo.SelectedItem is not Ui.RailRf.FootprintOption option) return;
        row.SelectFootprint(option);
    }

    /// <summary>Selects the row a newly-focused cell belongs to. A no-op when the row is already
    /// selected, so re-focusing a cell in the current row costs nothing.</summary>
    private static void OnCellGotFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Visual source) return;

        var item = source as ListBoxItem ?? source.FindAncestorOfType<ListBoxItem>();
        if (item is null || item.IsSelected) return;

        // Only for THIS list: a focus event from a nested list would otherwise reach the outer one's
        // handler and select a row of it by ancestry.
        if (sender is ListBox owner && item.FindAncestorOfType<ListBox>() != owner) return;

        item.IsSelected = true;
    }

    /// <summary>
    /// Escape clears the row selection, which is what dismisses the bias-curve editor.
    /// </summary>
    /// <remarks>
    /// <b>Focus is moved off the cell, and that is not tidiness.</b> Leaving the caret in a text box
    /// of the row just unselected means the next focus event this view sees — a Tab, or a click back
    /// into the same cell — re-selects it through <see cref="OnCellGotFocus"/>, so the panel would
    /// flicker back for a gesture the user reads as "put it away". It goes to the LIST rather than
    /// nowhere, so arrow-key navigation still works — and it is moved AFTER the selection is cleared,
    /// because focusing a list that still has a selected item can forward focus straight back into it.
    /// </remarks>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl/Cmd+V over the bias-curve panel with no cell focused — the empty curve, which has no
        // cell to paste into, is the commonest case. A focused text box has its own paste, which
        // OnBiasCellPasting takes over.
        if (e.Key == Key.V && (e.KeyModifiers == KeyModifiers.Control || e.KeyModifiers == KeyModifiers.Meta)
            && e.Source is Visual pasteSource && e.Source is not TextBox
            && (ReferenceEquals(pasteSource, BiasCurvePanel)
                || pasteSource.GetVisualAncestors().Contains(BiasCurvePanel))
            && DataContext is Ui.RailRf.PartLibraryDocument pasteDoc
            && pasteDoc.ViewModel.RequestBiasCurvePasteCommand.CanExecute(null))
        {
            e.Handled = true;
            pasteDoc.ViewModel.RequestBiasCurvePasteCommand.Execute(null);
            return;
        }

        if (e.Key != Key.Escape) return;
        if (DataContext is not Ui.RailRf.PartLibraryDocument doc) return;
        if (doc.ViewModel.SelectedRow is null) return;

        doc.ViewModel.SelectedRow = null;
        PartsList.Focus();
    }
}
