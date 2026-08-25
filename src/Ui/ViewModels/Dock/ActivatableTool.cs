using System;
using Dock.Model.Mvvm.Controls;

namespace CircuitRF.Ui.ViewModels.Dock;

/// <summary>
/// A Dock TOOL panel whose view should claim keyboard focus when the panel is activated — so
/// Page Up / Page Down / Home / End (and anything else the panel binds) work immediately, without a
/// preliminary click inside it.
///
/// <para><b>Owner, 2026-08-25:</b> <i>"if I click on the title bar of a window (like Project, or
/// Library) or when it generally gets focus, I cannot use &lt;page up&gt;, &lt;page down&gt; etc.
/// keystrokes. I am forced to click somewhere inside the window before the keystrokes will
/// register."</i> Clicking a tool's tab leaves keyboard focus on the TAB, which is Dock's chrome and
/// sits OUTSIDE the panel's own view — so the view's key handler is not on the event's route at all
/// and never sees the key.</para>
///
/// <para>This is the exact problem <c>IActivatableDocument</c> already solved for document tabs
/// ("without a preliminary click on the canvas"); tools were simply never given the same treatment.
/// The shape is copied deliberately, including the pending-flag half — a panel can be activated
/// before its view is built, and the view then consumes the request when it binds.</para>
/// </summary>
public interface IActivatableTool
{
    /// <summary>Raised when this panel becomes the active one.</summary>
    event Action? ActivationFocusRequested;

    /// <summary>Marks a focus request as pending and raises <see cref="ActivationFocusRequested"/>.</summary>
    void RequestActivationFocus();

    /// <summary>Returns whether a focus request is pending and clears it. Called by the view when it binds.</summary>
    bool ConsumeActivationFocus();
}

/// <summary>
/// The one implementation of <see cref="IActivatableTool"/>'s mechanics, mixed into a tool by
/// composition so the Project Tree and the Library palette cannot drift apart.
///
/// <para><b>Two signals, and they are genuinely different events</b> rather than belt-and-braces:
/// <c>OnSelected</c> is "this tab was chosen", and <c>IsActive</c> is "this dockable is now the
/// active one" — a panel can become active without any tab changing (focus moving into a pinned or
/// floating panel), and a tab can be re-selected in a dock that was already active.</para>
/// </summary>
public sealed class ActivationFocusRelay
{
    private bool _pending;

    public event Action? Requested;

    public void Request()
    {
        _pending = true;
        Requested?.Invoke();
    }

    public bool Consume()
    {
        var p = _pending;
        _pending = false;
        return p;
    }

    /// <summary>
    /// Follows the tool's own <c>IsActive</c> — the second of the two signals. A panel can become the
    /// active one without any tab changing (focus moving into a pinned or floating panel), which
    /// <c>OnSelected</c> alone would miss; and a tab can be re-selected in a dock that was already
    /// active, which <c>IsActive</c> alone would miss. Requesting twice is harmless — the view just
    /// focuses something that is already focused.
    /// </summary>
    public void Follow(Tool tool) =>
        tool.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Tool.IsActive) && tool.IsActive) Request();
        };
}
