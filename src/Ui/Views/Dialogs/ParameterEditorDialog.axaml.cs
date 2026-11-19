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
}
