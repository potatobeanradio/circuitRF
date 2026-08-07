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
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.Views.Harmonica;

namespace CircuitRF.Ui;

/// <summary>
/// The standalone harmonicaRF application (harmonicarf.md §3.1).
///
/// <para><b>What it deliberately does NOT do (R-h8-7).</b> No <c>WorkspaceWindow</c>, no
/// <c>WorkspaceViewModel</c>, no launch action, no <c>ProcessTechnologyRecognizers</c>, no
/// <c>.crfw</c>. There is no workspace here (§1.2) and standing one up to reach anything would be
/// building the thing this binary exists to be smaller than.</para>
///
/// <para><b>What it still MUST do, and the first one is not optional.</b> The two
/// <c>ProcessExit</c> cleanups: harmonicaRF can hold an external DUT, so it can leak a device worker
/// — and on macOS a leaked worker holds a VM slot indefinitely, so the NEXT run dies with a broken
/// pipe and no worker output at all. The reason is written out in <c>App.OnFrameworkInitializationCompleted</c>
/// and applies here identically. Also the theme: without <c>ThemeResolver.SetBuiltInProvider</c>
/// every <c>.ccolor</c> the user has resolves to nothing.</para>
/// </summary>
public partial class HarmonicaApp : Application
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
        if (prefs.ActiveThemeName is { } savedTheme)
            ThemeService.Active = ThemeResolver.Resolve(savedTheme);

        UpdateCrfWarningBrush();
        ThemeService.ThemeChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateCrfWarningBrush);
        ActualThemeVariantChanged += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateCrfWarningBrush);

        // R-h8-7 — NOT optional, and the reason is a real failure this codebase has already met: a
        // leaked worker on macOS holds a VM slot for ever and the next run is killed by the system
        // before it can say why. Hooked on ProcessExit rather than on a quit path because quit is
        // not the only way out.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ExternalDeviceRegistry.ResetResolved();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Layout.PCells.PCellRegistry.ClearResolvers();

        // A kit's devices are reachable with no workspace at all — the folder-list resolver is the
        // whole mechanism (R-h8-4). Installing it starts nothing.
        HarmonicaDutCatalog.RegisterKitResolver();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = new HarmonicaShellWindow();
            desktop.MainWindow = shell;
            shell.Show();

            // The double-click route: a .charm named on the command line (Windows/Linux argv) or
            // delivered by the Finder (macOS Apple Event). Both land in one place.
            string? first = StartupFiles.FirstOrDefault(File.Exists);
            if (first is not null) shell.OpenCharm(first);

            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                activatable.Activated += (_, e) =>
                {
                    if (e is not FileActivatedEventArgs fileArgs) return;
                    foreach (var f in fileArgs.Files)
                        if (f.Path?.LocalPath is { Length: > 0 } path) shell.OpenCharm(path);
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
