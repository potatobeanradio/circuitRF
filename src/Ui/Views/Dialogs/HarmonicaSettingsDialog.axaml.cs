using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.Harmonica;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// brief-harmonicarf-r6a §2.2 — the ONE Settings… dialog. Edit ▸ Preferences… (the colour/appearance
/// editor) and Display ▸ Advanced Settings… (loadline pts / FFT× / charge / M / §3's contour-kernel
/// controls) are now tabs here rather than two separate dialogs reachable from two separate menu
/// items — <see cref="HarmonicaMenuViewModel.SettingsHook"/> is the ONE hook that opens this, on all
/// three menu surfaces (§1.3's in-window Menu, torn-off NativeMenu, and the docked-injected
/// <c>harmonicaRF</c> menu).
/// </summary>
public partial class HarmonicaSettingsDialog : Window
{
    // Parameterless ctor satisfies the Avalonia XAML resource loader (AVLN3001).
    public HarmonicaSettingsDialog() : this(new HarmonicaViewModel()) { }

    public HarmonicaSettingsDialog(HarmonicaViewModel vm)
    {
        InitializeComponent();
        AppearanceTab.Attach(vm);
        // R8A §3 — the same HarmonicaColorEditor instance both tabs share; the Advanced tab gained the
        // fade sliders and the label toggle in this brief and writes through it exactly as Appearance
        // does. One editor, two tabs — never two editors.
        AdvancedTab.Attach(vm, vm.ColorEditor);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
