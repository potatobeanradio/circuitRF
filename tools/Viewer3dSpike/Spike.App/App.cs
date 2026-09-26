using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace Viewer3dSpike.App;

sealed class App : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Viewer3dSpike/")) { Source = new Uri("avares://Dock.Avalonia.Themes.Fluent/DockFluentTheme.axaml") });
        DataTemplates.Add(new FuncDataTemplate<ViewportTool>((t, _) => t is null ? null : new ViewportHost(t.Session)));
        DataTemplates.Add(new FuncDataTemplate<NotesTool>((_, _) => new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6),
            Text = "Type here while the pane orbits (tick Auto-orbit). The typing must stay smooth: that is\n" +
                   "the one-compositor property of em-3d.md §8.3.\n\n",
        }));
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
        {
            var session = new Session(Program.Route, Program.Msh, Program.Tris);
            d.MainWindow = new MainWindow(session);
            d.ShutdownRequested += (_, _) => { if (session.FirstPrint()) MainWindow.PrintSummary(session); };
            d.Exit += (_, _) => { if (session.FirstPrint()) MainWindow.PrintSummary(session); };
            if (Program.ExitAfter > 0)
                DispatcherTimer.RunOnce(() => d.Shutdown(), TimeSpan.FromSeconds(Program.ExitAfter));
        }
        base.OnFrameworkInitializationCompleted();
    }
}

sealed class ViewportTool : Tool
{
    public Session Session { get; }
    public ViewportTool(Session s) { Session = s; Id = "Viewport"; Title = $"3D view — route {s.Route}"; CanClose = false; }
}

sealed class NotesTool : Tool
{
    public NotesTool() { Id = "Notes"; Title = "Notes (type here)"; CanClose = false; }
}

/// <summary>Two tool panes side by side; either can be floated (drag its tab out) and re-docked.
/// Floating the 3D view re-parents it into a new window — where NativeControlHost fails (§8.3).</summary>
sealed class SpikeDockFactory(ViewportTool vp, NotesTool notes) : Factory
{
    public override IRootDock CreateLayout()
    {
        var left = new ToolDock { Id = "ViewDock", VisibleDockables = CreateList<IDockable>(vp), ActiveDockable = vp, Proportion = 0.72 };
        var right = new ToolDock { Id = "NotesDock", VisibleDockables = CreateList<IDockable>(notes), ActiveDockable = notes, Proportion = 0.28 };
        var main = new ProportionalDock { Id = "Main", Orientation = Dock.Model.Core.Orientation.Horizontal, VisibleDockables = CreateList<IDockable>(left, new ProportionalDockSplitter(), right) };
        var root = CreateRootDock();
        root.Id = "Root";
        root.VisibleDockables = CreateList<IDockable>(main);
        root.ActiveDockable = main;
        root.DefaultDockable = main;
        return root;
    }

    public override void InitLayout(IDockable layout)
    {
        DefaultHostWindowLocator = () => new HostWindow();
        base.InitLayout(layout);
    }
}

sealed class MainWindow : Window
{
    readonly Session _s;
    readonly TextBlock _status = new() { FontFamily = new FontFamily("Menlo, Consolas, monospace"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4) };
    readonly DockControl _dock;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    long _lastUiFrames;
    double _lastT;

    public MainWindow(Session s)
    {
        _s = s;
        Title = $"circuitRF F2 spike — route {s.Route} ({RouteName(s.Route)}) — Avalonia compositor: {(Program.Compositor == "default" ? "platform default" : Program.Compositor)}";
        Width = 1400; Height = 900;
        var factory = new SpikeDockFactory(new ViewportTool(s), new NotesTool());
        var layout = factory.CreateLayout();
        factory.InitLayout(layout);
        _dock = new DockControl { Factory = factory, Layout = layout };

        var orbit = new CheckBox { Content = "Auto-orbit", IsChecked = false };
        orbit.IsCheckedChanged += (_, _) => { s.Input.AutoOrbit = orbit.IsChecked == true; Kick(); };
        var cont = new CheckBox { Content = "Continuous redraw", IsChecked = true };
        cont.IsCheckedChanged += (_, _) => { s.Continuous = cont.IsChecked == true; Kick(); };
        var reset = new Button { Content = "Reset counters" };
        reset.Click += (_, _) => { PrintSummary(s); s.Ui.Reset(); s.Render.Reset(); };
        var bar = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 16, Margin = new Thickness(8, 4) };
        bar.Children.Add(orbit); bar.Children.Add(cont); bar.Children.Add(reset);
        bar.Children.Add(new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, Text = "drag: orbit · right/Shift-drag: pan · wheel/pinch: zoom · click: select · drag a tab out to float it" });

        var root = new DockPanel();
        DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top); root.Children.Add(bar);
        DockPanel.SetDock(_status, Avalonia.Controls.Dock.Bottom); root.Children.Add(_status);
        root.Children.Add(_dock);
        Content = root;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => UpdateStatus();
        timer.Start();
    }

    static string RouteName(string r) => r switch { "gl" => "C: OpenGlControlBase", "metal" => "A: composition interop + Metal", "wgpu" => "B: composition interop + WebGPU/wgpu",
        "d3d11" => "A: composition interop + D3D11", "vulkan" => "A: composition interop + Vulkan", _ => r };

    void Kick()
    {
        foreach (var w in (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows ?? [])
            foreach (var h in w.GetVisualDescendantsOfType<ViewportHost>()) ((IPane?)h.Children[0])?.RequestFrame();
    }

    void UpdateStatus()
    {
        foreach (var w in (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows ?? [])
            foreach (var h in w.GetVisualDescendantsOfType<ViewportHost>()) h.ShowError();
        double t = _clock.Elapsed.TotalSeconds;
        long frames = _s.Ui.FrameIndex;
        double fps = (frames - _lastUiFrames) / Math.Max(1e-3, t - _lastT);
        _lastUiFrames = frames; _lastT = t;
        var u = _s.Ui.Last; var r = _s.Render.Last;
        int uiThread = Environment.CurrentManagedThreadId;
        string where = _s.RenderThreadId == uiThread ? "UI thread" : _s.RenderThreadId > 0 ? $"own thread #{_s.RenderThreadId}" : "?";
        long uploaded = Math.Max(_s.Ui.UploadBytesTotal, _s.Render.UploadBytesTotal);
        _status.Text =
            $"{_s.DeviceInfo}\n{_s.HostInfo}\nscene: {_s.SceneSource} — {_s.Scene.Describe()}\n" +
            $"{fps,5:F1} UI frames/s | GPU work runs on: {where} | GPU (re)initialisations: {_s.Inits}, bytes uploaded in total: {uploaded:N0} | release timeouts: {_s.ReleaseTimeouts}\n" +
            $"last UI frame: upload {u.Upload} B, alloc {u.Alloc} B, {u.Ms:F3} ms" +
            (_s.Route == "gl" ? "" : $" | last render-thread frame: upload {r.Upload} B, alloc {r.Alloc} B, {r.Ms:F3} ms") +
            $" | pick latency {(_s.Route == "gl" ? _s.Ui : _s.Render).LastPickLatency} frame(s) | hovered: {_s.ObjectName(_s.Hovered)}";
    }

    public static void PrintSummary(Session s)
    {
        Console.WriteLine($"=== route {s.Route}, {(System.Diagnostics.Debugger.IsAttached ? "debugger" : "")}{Config} ===");
        Console.WriteLine(s.DeviceInfo);
        Console.WriteLine(s.HostInfo);
        Console.WriteLine($"GPU (re)initialisations: {s.Inits}; release timeouts: {s.ReleaseTimeouts}; OnOpenGlRender calls: {s.GlRenderCalls}; " +
            $"GPU work ran on: {(s.RenderThreadId == s.UiThreadId ? "the UI thread" : s.RenderThreadId > 0 ? $"its own thread (#{s.RenderThreadId})" : "?")}");
        Console.WriteLine(s.Ui.Summarize());
        if (s.Route != "gl") Console.WriteLine(s.Render.Summarize());
    }

#if DEBUG
    const string Config = "Debug";
#else
    const string Config = "Release";
#endif
}

static class VisualExtensions
{
    public static IEnumerable<T> GetVisualDescendantsOfType<T>(this Avalonia.Visual v) where T : Avalonia.Visual =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(v).OfType<T>();
}
