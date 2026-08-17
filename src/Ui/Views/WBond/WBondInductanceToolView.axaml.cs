using Avalonia.Controls;

namespace CircuitRF.Ui.Views.WBond;

/// <summary>
/// The Array Inductance panel as a dock tool (wbond.md §10.1, WB39a/M3) — a host, not a second
/// implementation.
/// </summary>
public partial class WBondInductanceToolView : UserControl
{
    public WBondInductanceToolView()
    {
        InitializeComponent();

        // This tab already says "Array Inductance" — the panel must not say it again (owner, 2026-08-17).
        Panel.ShowHeading = false;

        // Nothing else to wire: the panel reads the editor its gestures act on from the FORMATTER it is
        // already bound to (WBondPanelViewModel.Editor). Pushing it from here is what went stale.
    }
}
