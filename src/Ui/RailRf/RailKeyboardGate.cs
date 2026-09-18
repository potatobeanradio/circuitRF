// R-rail8-4 — the one thing railRF ADDS to the layout canvas's own behaviour, and it is a gate
// rather than a gesture (docs/sonnet-briefs/brief-railrf-8-board-view.md §1, §2; railrf.md §11.6
// trap 1).

using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// Answers whether the keyboard currently belongs to a text field, so
/// <c>LayoutCanvas.NavigationKeysSuppressed</c> can refuse a navigation key.
/// </summary>
/// <remarks>
/// <b>Why railRF needs this and the layout editor does not.</b> §11.6 trap 1: <c>F</c> is an ordinary
/// letter in a net name and <c>Z</c> is one in a part number, and the layout canvas already
/// suppresses both while a label is being typed. But "railRF's window has far more text fields than
/// the layout editor does — its whole left column is editable rows. The gate is therefore wider here,
/// not narrower: no navigation key fires while focus is in any text-entry control."
///
/// <para><b>One rule, and it covers <see cref="Controls.InlineEditText"/> without naming it.</b> That
/// control is a <see cref="Panel"/> that swaps a real <see cref="TextBox"/> in when it opens, and the
/// box is what takes focus — so "the focused element is, or sits inside, a TextBox" answers for the
/// whole left column, for the plain boxes in the import dialog, and for anything added later, without
/// a list of control types that would go stale the first time somebody used a different one. A list
/// is exactly the shape that fails silently here: the gate would simply stop covering a field, and
/// the symptom is a board that jumps to fit while a value is being typed.</para>
///
/// <para>Framework-thin on purpose — one visual walk and a type test — so the window's wiring is a
/// single line and this is the only place that knows what "a text field" means to railRF.</para>
/// </remarks>
public static class RailKeyboardGate
{
    /// <summary>
    /// True when <paramref name="focused"/> is a text-entry control, or sits inside one.
    /// </summary>
    /// <param name="focused">Whatever the focus manager currently reports — null when nothing is
    /// focused, which is not a text field and so not a suppression.</param>
    public static bool IsTextEntry(object? focused)
    {
        if (focused is not Visual v) return false;
        if (v is TextBox) return true;

        // Inside one: a TextBox's own template parts (the presenter that actually carries the caret)
        // are what a focus manager sometimes reports, and InlineEditText's editor is a real TextBox
        // with a real template under it.
        foreach (var ancestor in v.GetVisualAncestors())
            if (ancestor is TextBox) return true;

        return false;
    }

    /// <summary>
    /// The predicate a window hands to <c>LayoutCanvas.NavigationKeysSuppressed</c>.
    /// </summary>
    /// <param name="scope">The window (or any control in it) whose focus manager to ask.</param>
    public static Func<bool> For(Visual scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // Resolved on every call rather than captured: a control is attached to its top level after
        // it is constructed, so a TopLevel captured at wiring time would be null for the life of the
        // window — and the gate would then never engage, silently, which is the whole failure mode.
        return () => IsTextEntry(TopLevel.GetTopLevel(scope)?.FocusManager?.GetFocusedElement());
    }
}
