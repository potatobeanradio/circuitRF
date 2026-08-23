using Avalonia.Input;

namespace CircuitRF.Ui.Layout;

/// <summary>What a Page/Home/End keystroke should do to the .ctech editor's row list.</summary>
public enum TechScrollAction { PageUp, PageDown, Home, End }

/// <summary>
/// The keyboard-scrolling rule for the .ctech editor's four row lists, kept out of the code-behind
/// so it can be tested without a rendered window (the same reason every other framework-free decision
/// in <c>src/Ui/Layout</c> lives beside its view rather than in it).
///
/// <para><b>Home/End are conditional and Page Up/Down are not</b>, and that asymmetry is the whole
/// content of this type. Every row in these lists is built out of editable <c>TextBox</c>es, where
/// Home/End mean "caret to start/end of this field" — hijacking them to scroll the list would break
/// text editing everywhere in the editor to add a shortcut nobody asked for there. Page Up/Down mean
/// nothing to a single-line TextBox, so they are free to take.</para>
/// </summary>
public static class TechEditorScrollKeys
{
    /// <param name="sourceIsTextInput">Whether the keystroke came from a control that owns a caret
    /// (a <c>TextBox</c>) — the filter box included, which is itself a text field.</param>
    /// <returns>Null when the key is not one this editor scrolls with, or when the source's own
    /// meaning for it wins.</returns>
    public static TechScrollAction? ActionFor(Key key, bool sourceIsTextInput) => key switch
    {
        Key.PageUp                       => TechScrollAction.PageUp,
        Key.PageDown                     => TechScrollAction.PageDown,
        Key.Home when !sourceIsTextInput => TechScrollAction.Home,
        Key.End  when !sourceIsTextInput => TechScrollAction.End,
        _                                => null,
    };
}
