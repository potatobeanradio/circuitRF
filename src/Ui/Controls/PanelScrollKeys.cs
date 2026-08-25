using Avalonia.Controls;
using Avalonia.Input;

namespace CircuitRF.Ui.Controls;

/// <summary>What a Page/Home/End keystroke should do to a scrollable panel's list.</summary>
public enum PanelScrollAction { PageUp, PageDown, Home, End }

/// <summary>
/// The keyboard-scrolling rule shared by every long list panel in the app — the .ctech editor's four
/// row lists, the Project Tree, and the Library palette's tile grid. Kept out of the code-behinds so
/// it can be tested without a rendered window (the same reason every other framework-free decision in
/// <c>src/Ui/Layout</c> lives beside its view rather than in it).
///
/// <para><b>Home/End are conditional and Page Up/Down are not</b>, and that asymmetry is the whole
/// content of this type. A row in the .ctech editor is built out of editable <c>TextBox</c>es and the
/// palette is headed by a search field, where Home/End mean "caret to start/end of this field" —
/// hijacking them to scroll the list would break text editing to add a shortcut nobody asked for
/// there. Page Up/Down mean nothing to a single-line TextBox, so they are free to take, which is what
/// lets the palette user type a search and then page through its results without leaving the box.</para>
/// </summary>
public static class PanelScrollKeys
{
    /// <param name="sourceIsTextInput">Whether the keystroke came from a control that owns a caret
    /// (a <c>TextBox</c>) — a filter/search box included, which is itself a text field.</param>
    /// <returns>Null when the key is not one these panels scroll with, or when the source's own
    /// meaning for it wins.</returns>
    public static PanelScrollAction? ActionFor(Key key, bool sourceIsTextInput) => key switch
    {
        Key.PageUp                       => PanelScrollAction.PageUp,
        Key.PageDown                     => PanelScrollAction.PageDown,
        Key.Home when !sourceIsTextInput => PanelScrollAction.Home,
        Key.End  when !sourceIsTextInput => PanelScrollAction.End,
        _                                => null,
    };

    /// <summary>Runs the action on <paramref name="scroll"/>. One copy, because three panels
    /// (.ctech editor, Project Tree, Library palette) would otherwise each spell out the same
    /// four-case switch and drift.</summary>
    public static void Apply(PanelScrollAction action, ScrollViewer scroll)
    {
        switch (action)
        {
            case PanelScrollAction.PageUp:   scroll.PageUp();       break;
            case PanelScrollAction.PageDown: scroll.PageDown();     break;
            case PanelScrollAction.Home:     scroll.ScrollToHome(); break;
            case PanelScrollAction.End:      scroll.ScrollToEnd();  break;
        }
    }
}
