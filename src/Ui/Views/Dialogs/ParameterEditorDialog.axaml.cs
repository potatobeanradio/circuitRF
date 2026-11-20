using Avalonia.Controls;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// The Parameter dialog. It sizes to its rows up to a cap, and past that its rows scroll while the header (name, labels) and the
/// footer (+, Help, Close) stay put — the view already holds the rows in a ScrollViewer, which only engages once the window's
/// height is bounded. Unbounded, a component carrying many parameters (a Propagation Channel given a few dozen through its "+")
/// grew the dialog past the bottom of the screen and took its buttons with it.
/// </summary>
public partial class ParameterEditorDialog : Window
{
    /// <summary>The share of the screen's working height the dialog may take; the XAML MaxHeight caps it on a large screen.</summary>
    internal const double ScreenShare = 0.85;

    public ParameterEditorDialog()
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            // WorkingArea is device pixels and MaxHeight logical: divide by the screen's scaling (as CrfHostWindow does).
            if (Screens?.ScreenFromWindow(this) is { } screen)
                MaxHeight = System.Math.Min(MaxHeight, ScreenShare * screen.WorkingArea.Height / (screen.Scaling > 0 ? screen.Scaling : 1.0));
        };
    }

    /// <summary>
    /// The cap bounds the dialog's OWN sizing, never the user's (GitHub #78). The window manager is not told about it: Avalonia
    /// sends no maximum-size hint while the width is unbounded. A drag past it was resized back by the layout pass, the window
    /// manager answered with the dragged size, and the two alternated on every pointer event. On one desktop the
    /// dialog ran out to the screen edge. A resize from outside that goes past the cap lifts it, before the layout pass sees it.
    /// </summary>
    protected override void OnResized(WindowResizedEventArgs e)
    {
        if (e.Reason is WindowResizeReason.User or WindowResizeReason.Unspecified && e.ClientSize.Height > MaxHeight)
            MaxHeight = double.PositiveInfinity;
        base.OnResized(e);
    }
}
