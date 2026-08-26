using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// The guard every tool panel that claims keyboard focus on activation has to apply first.
///
/// <para><b>Owner, 2026-08-26:</b> <i>"If I click in the Library palette's Search field, I
/// immediately lose focus of the textedit box and can't use it."</i> Activation focus exists because
/// clicking a panel's TAB leaves focus on Dock's chrome, outside the panel, so its key handlers are
/// never on the event's route. But <c>Tool.IsActive</c> turns true for a click ANYWHERE in the panel,
/// the search box included — and the focus grab is posted, so it lands after the click has already
/// given the box the caret and takes it straight back. The panel became unusable in the one place a
/// user types.</para>
///
/// <para>So the request is a FALLBACK, not an override: claim focus only when it is not already
/// somewhere inside this panel. A tab click still lands here (focus is on the chrome, which is not a
/// descendant); a click on the search field, the category picker, or a tile does not.</para>
///
/// <para>Deliberately narrower than "is anything focused at all" (<c>TechEditorView</c>'s
/// <c>onlyIfUnclaimed</c>): that would also decline when focus sits in a DIFFERENT panel, which is
/// exactly when a tab activation should pull it in.</para>
/// </summary>
public static class PanelActivationFocus
{
    /// <summary>
    /// True when keyboard focus is already on <paramref name="panel"/> or on something inside it.
    /// Call it from inside the posted action, never before — the click that triggered activation has
    /// not moved focus yet when the request is raised.
    /// </summary>
    public static bool AlreadyInside(Visual panel)
    {
        if (TopLevel.GetTopLevel(panel)?.FocusManager?.GetFocusedElement() is not Visual focused)
            return false;

        return ReferenceEquals(focused, panel) || focused.GetVisualAncestors().Contains(panel);
    }
}
