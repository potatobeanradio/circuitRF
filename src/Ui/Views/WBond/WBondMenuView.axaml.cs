using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using CircuitRF.Ui.WBond;

namespace CircuitRF.Ui.Views.WBond;

/// <summary>
/// wBond's own menu bar, on both surfaces (wbond.md §11 / brief-wbond-wbe M2, R-wbe-4).
///
/// <para><b>The macOS <c>NativeMenu</c> is attached PER WINDOW, and that is the whole point.</b>
/// <c>NativeMenu.Menu</c> is a per-<c>AvaloniaObject</c> attached property and Avalonia does not fall
/// back to an application-scope menu for a key window that has none — so a second shell window would
/// show a bare app menu unless its own view attaches one. Several <c>.wBond</c> files open as several
/// windows (R-wbe-4), so this runs once per window, on attach.</para>
///
/// <para><b>The guard is a type-NAME comparison, deliberately.</b> A wBond document docked as a tab
/// inside circuitRF must not replace the workspace's application menu bar; comparing by name rather
/// than by type keeps this view free of a dependency on the shell — and is pinned by a test, because
/// a rename would otherwise silently stop the menu bar appearing with nothing failing to compile.</para>
/// </summary>
public partial class WBondMenuView : UserControl
{
    public WBondMenuView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => AttachNativeMenuIfOwnWindow();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// The window a wBond document must NOT steal the menu bar from. Resolved by type NAME so this
    /// view takes no dependency on the workspace shell — wBond ships standalone, where nothing ever
    /// constructs that type.
    /// </summary>
    private const string WorkspaceWindowTypeName = "WorkspaceWindow";

    private void AttachNativeMenuIfOwnWindow()
    {
        if (!OperatingSystem.IsMacOS()) return;

        if (TopLevel.GetTopLevel(this) is not Window window) return;
        if (window.GetType().Name == WorkspaceWindowTypeName) return;   // docked — leave the app bar alone

        if (NativeMenu.GetMenu(this) is { } menu) NativeMenu.SetMenu(window, menu);
    }

    /// <summary>The menu commands this bar drives. Set by the shell once, on construction.</summary>
    public WBondMenuViewModel? Menus => DataContext as WBondMenuViewModel;
}
