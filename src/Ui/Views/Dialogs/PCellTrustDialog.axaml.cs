using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout.PCells.Wire;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>
/// B6: third-party code execution needs consent. Names exactly what would run — the kit and the entry
/// script — before anything is launched.
///
/// <para>Returns <c>true</c> (Allow), <c>false</c> (Don't Allow), or <c>null</c> when the window is
/// dismissed without answering. <b>Null records nothing</b>, so a dismissed prompt asks again next
/// time rather than being read as either answer — the safe direction for a question about running
/// somebody else's code.</para>
///
/// <para><b>Allow is deliberately not the default button.</b> Enter does nothing here; a consent
/// prompt that can be cleared with a reflexive keystroke has not obtained consent. Escape maps to
/// Don't Allow, which is the safe side.</para>
/// </summary>
public partial class PCellTrustDialog : Window
{
    public PCellTrustDialog() => InitializeComponent();

    public static async Task<bool?> ShowAsync(Window? owner, IReadOnlyList<PCellKit> kits)
    {
        if (kits.Count == 0) return null;

        var dlg = new PCellTrustDialog();
        dlg.KitList.ItemsSource = kits;
        if (kits.Count > 1)
            dlg.HeadingText.Text = $"Allow these {kits.Count} kits to draw their own artwork?";

        // $parent[Window] resolves to null on macOS for menu- and key-binding-sourced calls, and this
        // one is raised from a workspace open rather than from a control at all — find the shell.
        owner ??= (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                  ?.Windows.FirstOrDefault(w => w.IsActive)
                  ?? (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                  ?.Windows.FirstOrDefault();

        // No window to own the prompt means nobody can be asked. Record nothing and ask again later —
        // never fall through to running the scripts on the strength of a question that was not put.
        return owner is null ? null : await dlg.ShowDialog<bool?>(owner);
    }

    private void OnAllowClick(object? sender, RoutedEventArgs e) => Close(true);

    private void OnDontAllowClick(object? sender, RoutedEventArgs e) => Close(false);
}
