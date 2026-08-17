using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using CircuitRF.Ui.WBond;
using CircuitRF.Ui.Views.Dialogs;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Views.WBond;

/// <summary>
/// The Array Inductance panel (wbond.md §6.8) — one card per wire array, with the four settable rows
/// the owner asked for.
///
/// <h3>One control, two hosts</h3>
/// <para>WB39a/M3: the wBond editor docks this left at full window height (§6.1), and the workspace
/// offers the same control as a dock tool that follows the active layout (§10.1), so a wirebond cell
/// pushed into in the ordinary Layout Editor reads the same numbers. The <c>DataContext</c> is the
/// <see cref="WBondPanelViewModel"/> that formats them; <see cref="Editor"/> is what the double-click
/// gestures act on, and is separate because a panel with no editor behind it is a legitimate state
/// (the dock tool before any wirebond cell has been opened).</para>
/// </summary>
public partial class WBondInductancePanelView : UserControl
{
    public WBondInductancePanelView() => InitializeComponent();

    /// <summary>
    /// Whether the panel prints its own "Array Inductance" heading.
    ///
    /// <para>False when a DOCK TAB already carries that name (owner, 2026-08-17) — the tab and the first
    /// row would otherwise say the same word twice with nothing between them. True inline in the wBond
    /// editor, which has no tab of its own and where the heading is the only label there is.</para>
    /// </summary>
    public bool ShowHeading
    {
        get => HeadingText.IsVisible;
        set => HeadingText.IsVisible = value;
    }

    /// <summary>
    /// The wire editor the panel's gestures act on — selecting an array, and the four group-wide
    /// "set this for every wire" prompts. Null leaves the panel a pure readout.
    ///
    /// <para>Read from the panel's OWN DataContext rather than pushed in by each host, and that is a
    /// correction: the docked host pushed it once, on its <c>DataContextChanged</c>, which fires when the
    /// TOOL is bound and never again — while the editor the tool points at changes with every document
    /// activation. It was therefore null for the life of the panel and every gesture here silently did
    /// nothing (owner, 2026-08-17). See <see cref="WBondPanelViewModel.Editor"/>.</para>
    /// </summary>
    private WBondViewModel? Editor => (DataContext as WBondPanelViewModel)?.Editor;

    /// <summary>
    /// Double-clicking an array's name selects that array's wires (owner, 2026-08-16).
    ///
    /// <para>The row carries its own array INDEX, so membership is read from the mesh rather than
    /// matched by name — two arrays may not share a name today, but resolving a selection by string
    /// would make that a silent selection bug the day one does.</para>
    /// </summary>
    private void OnArrayNameDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Editor is null || sender is not Control { DataContext: WBondArrayRowViewModel row }) return;

        Editor.SelectArray(row.ArrayIndex);
        e.Handled = true;
    }

    /// <summary>
    /// Double-clicking a card's Loop height / Span / Diameter / Material opens the SAME prompt the
    /// profile view's context menu opens, on the same array (owner, 2026-08-16), and applies to every
    /// wire in it.
    ///
    /// <para>The panel is where a user reads those four numbers, so it is where they reach for them.
    /// Both routes land on <see cref="WBondGroupEdits"/> — one implementation, one undo entry, one
    /// refusal path.</para>
    /// </summary>
    private void OnArrayLoopHeightDoubleTapped(object? sender, TappedEventArgs e) =>
        Prompt(sender, e, (owner, editor, index) => WBondGroupEdits.SetLoopHeightAsync(owner, editor, index));

    private void OnArraySpanDoubleTapped(object? sender, TappedEventArgs e) =>
        Prompt(sender, e, (owner, editor, index) => WBondGroupEdits.SetSpanAsync(owner, editor, index));

    private void OnArrayDiameterDoubleTapped(object? sender, TappedEventArgs e) =>
        Prompt(sender, e, (owner, editor, index) => WBondGroupEdits.SetDiameterAsync(owner, editor, index));

    private void OnArrayMaterialDoubleTapped(object? sender, TappedEventArgs e) =>
        Prompt(sender, e, (owner, editor, index) => WBondGroupEdits.SetMaterialAsync(owner, editor, index));

    private void Prompt(object? sender, TappedEventArgs e, Func<Window?, WBondViewModel, int, Task> prompt)
    {
        if (Editor is not { } editor || sender is not Control { DataContext: WBondArrayRowViewModel row }) return;

        e.Handled = true;
        _ = prompt(TopLevel.GetTopLevel(this) as Window, editor, row.ArrayIndex);
    }
}
