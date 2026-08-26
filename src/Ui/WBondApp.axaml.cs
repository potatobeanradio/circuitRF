using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using CircuitRF.Core.Devices.External;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.Views.WBond;

namespace CircuitRF.Ui;

/// <summary>
/// The standalone wBond application (wbond.md §11, WB38).
///
/// <para><b>What it deliberately does NOT do (R-wbe-1's sibling, R-h8-7 applied again).</b> No
/// <c>WorkspaceWindow</c>, no <c>WorkspaceViewModel</c>, no launch action, no
/// <c>ProcessTechnologyRecognizers</c>, no <c>.crfw</c>. There is no workspace here and standing one
/// up to reach anything would be building the thing this binary exists to be smaller than.</para>
///
/// <para><b>What it still MUST do.</b> Both <c>ProcessExit</c> cleanups: a wBond's reference geometry
/// can hold PCells, so <c>PCellRegistry.ClearResolvers</c> matters here in a way it does not for
/// harmonicaRF — a kit-backed PCell resolver holds a live interpreter process. And
/// <c>ExternalDeviceRegistry.ResetResolved</c> for the same reason the full application has it: a
/// leaked device worker on macOS holds a VM slot indefinitely and the NEXT run dies with a broken
/// pipe and no output at all. Also the theme: without <c>ThemeResolver.SetBuiltInProvider</c> every
/// <c>.ccolor</c> the user has resolves to nothing.</para>
/// </summary>
public partial class WBondApp : Application
{
    /// <summary>Paths handed to this process on the command line — the double-click route.</summary>
    internal static string[] StartupFiles { get; set; } = [];

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Built-in .ccolor assets, so a saved theme name resolves to something. Same provider the
        // full application installs; without it ThemeResolver answers null for every built-in.
        ThemeResolver.SetBuiltInProvider(name =>
        {
            try
            {
                var uri = new Uri($"avares://CircuitRF.Ui/Assets/Color/{name}.ccolor");
                using var stream = AssetLoader.Open(uri);
                using var reader = new StreamReader(stream);
                return ColorThemeIo.Load(reader.ReadToEnd());
            }
            catch { return null; }
        });

        var prefs = AppPreferencesIo.Load();

        // No recorded preference means the SHIPPED DEFAULT, not "leave whatever is loaded" — see
        // ThemeResolver.DefaultThemeName for why a wBond palette can be that default without
        // recolouring anything outside the wirebond editor.
        ThemeService.Active = ThemeResolver.Resolve(prefs.ActiveThemeName ?? ThemeResolver.DefaultThemeName);

        UpdateCrfWarningBrush();
        ThemeService.ThemeChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateCrfWarningBrush);
        ActualThemeVariantChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateCrfWarningBrush);

        // NOT optional, and the reason is a real failure this codebase has already met: a leaked
        // worker on macOS holds a VM slot for ever and the next run is killed by the system before it
        // can say why. Hooked on ProcessExit rather than on a quit path because quit is not the only
        // way out. PCellRegistry is the wBond-specific half — reference geometry can hold PCells,
        // whose kit resolvers own an interpreter process each.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ExternalDeviceRegistry.ResetResolved();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Layout.PCells.PCellRegistry.ClearResolvers();

        // An exception that reaches the dispatcher unhandled takes the process down, so it is a
        // crash and gets a report. This app installs no dispatcher backstop of its own; the
        // e.Handled check inside still makes the order-independent thing the right one.
        Diagnostics.CrashReporter.WireDispatcherLogging();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = new WBondShellWindow();
            desktop.MainWindow = shell;
            shell.Show();

            // Automatic updates. The sink is null here on purpose: MessagesTool is a docking tool of
            // circuitRF's workspace and this shell has none, so a staged update is silent in this
            // application. The check, the staging and the launch-time swap are identical; only the
            // one Message Panel line has nowhere to go. Recorded in src/Ui/RESOLVED.md.
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => Updates.UpdateStartup.AfterFirstWindow(null),
                Avalonia.Threading.DispatcherPriority.ApplicationIdle);

            // The double-click route: a .wBond named on the command line (Windows/Linux argv) or
            // delivered by the Finder (macOS Apple Event). Both land in one place — and several
            // files open as several WINDOWS (R-wbe-4), the first reusing the shell that just opened
            // rather than leaving a blank one behind it.
            bool usedShell = false;
            foreach (string path in StartupFiles.Where(File.Exists))
            {
                if (!usedShell) { shell.OpenWBond(path); usedShell = true; }
                else WBondShellWindow.OpenInNewWindow(path);
            }

            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                activatable.Activated += (_, e) =>
                {
                    if (e is not FileActivatedEventArgs fileArgs) return;
                    foreach (var f in fileArgs.Files)
                        if (f.Path?.LocalPath is { Length: > 0 } path)
                            WBondShellWindow.OpenInNewWindow(path);
                };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Keeps <c>CrfWarningBrush</c> following the active colour theme, exactly as the full
    /// application does — the brush is in the shared dictionary, so the update rule is shared too.</summary>
    private void UpdateCrfWarningBrush()
    {
        var variant = ActualThemeVariant == ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light;
        ThemeService.CurrentVariant = variant;

        var rgba  = ThemeService.Active.Resolve(ColorRole.SystemWarning, variant);
        var color = Color.FromArgb(rgba.A, rgba.R, rgba.G, rgba.B);
        if (Resources.TryGetResource("CrfWarningBrush", null, out var res) && res is SolidColorBrush brush)
            brush.Color = color;
    }
}
