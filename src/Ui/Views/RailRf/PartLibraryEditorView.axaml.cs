using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

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
        if (e.Key != Key.Escape) return;
        if (DataContext is not Ui.RailRf.PartLibraryDocument doc) return;
        if (doc.ViewModel.SelectedRow is null) return;

        doc.ViewModel.SelectedRow = null;
        PartsList.Focus();
    }
}
