using System;

namespace CircuitRF.Ui.Commands;

/// <summary>
/// A Dock document whose editor view should claim keyboard focus when the document's tab is
/// activated — so keyboard shortcuts (Select All, nudges, …) work immediately, without a preliminary
/// click on the canvas.
///
/// The workspace calls <see cref="RequestActivationFocus"/> from its tab-activation hook
/// (<c>OnDocumentDockPropertyChanged</c>). The editor view focuses its canvas when:
///   • it is already bound — via the <see cref="ActivationFocusRequested"/> event; or
///   • it binds AFTER the request (first open, when the view is built during the next layout pass) —
///     by calling <see cref="ConsumeActivationFocus"/> on DataContext change and focusing if it returns true.
/// </summary>
public interface IActivatableDocument
{
    /// <summary>Raised when this document becomes the active tab.</summary>
    event Action? ActivationFocusRequested;

    /// <summary>Marks a focus request as pending and raises <see cref="ActivationFocusRequested"/>.</summary>
    void RequestActivationFocus();

    /// <summary>Returns whether a focus request is pending and clears it. Called by the view when it binds.</summary>
    bool ConsumeActivationFocus();
}
