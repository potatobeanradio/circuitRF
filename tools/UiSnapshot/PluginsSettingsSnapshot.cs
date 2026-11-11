using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using CircuitRF.Design.Plugins;
using CircuitRF.Ui.Plugins;

/// <summary>
/// brief-plg-6 gate 8 — Settings ▸ Plugins and its dialogs, on a scratch state directory built here from
/// <c>tools/plugin-examples</c> (nothing is fetched: the vetted list is written in memory and the HTTP seam counts, and
/// the count is printed so a snapshot also shows that opening the tab made no request).
/// <c>--settings-plugins empty|full|outside|policy|dialog</c>, <c>--plugin-address-dialog</c>,
/// <c>--plugin-install-progress</c>.
/// </summary>
static class PluginsSettingsSnapshot
{
    public static int Tab(string scenario, string output, double width, double height)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "crf-plg6-snap-" + Guid.NewGuid().ToString("N")[..8]);
        string state = Path.Combine(scratch, "state"), root = Path.Combine(state, "plugins");
        Directory.CreateDirectory(root);
        CircuitRF.Ui.AppDataRoot.RedirectTo(state);
        CircuitRF.Ui.Theming.AppPreferencesIo.InvalidateCache();

        if (scenario == "dialog")
        {
            // The whole Settings window on its Plugins tab, so the eighth header is seen to fit.
            var settings = new CircuitRF.Ui.Views.Dialogs.SettingsView(null);
            settings.SelectTab("Plugins");
            return Capture(settings, output, null);
        }

        string examples = Examples();
        var linked = new List<string>();
        var trust = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        string prefs = Path.Combine(state, "preferences.json");
        PluginInstallContext Ctx(PluginInstallOrigin o) => new(root, prefs, o);

        if (scenario is "full" or "outside" or "policy")
        {
            var hello = PluginInstaller.InstallFolderAsync(Ctx(PluginInstallOrigin.Settings), Path.Combine(examples, "hello")).GetAwaiter().GetResult();
            trust[PluginTrustStore.Key(hello.Directory!)] = true;
            var calc = PluginInstaller.InstallFolderAsync(Ctx(scenario == "outside" ? PluginInstallOrigin.Cli : PluginInstallOrigin.Settings),
                Path.Combine(examples, "calculator")).GetAwaiter().GetResult();
            if (scenario != "outside") trust[PluginTrustStore.Key(calc.Directory!)] = false;
            linked.Add(Path.Combine(examples, "stub"));
            // Installed from the list, so the list's newer version shows as Update available.
            RecordListSource(Path.Combine(root, "example.hello"));
        }
        var trustJson = new System.Text.Json.Nodes.JsonObject();
        foreach (var (k, v) in trust) trustJson[k] = v;
        File.WriteAllText(prefs, new System.Text.Json.Nodes.JsonObject { [PluginTrustStore.PreferenceKey] = trustJson }.ToJsonString());

        var index = scenario == "empty" ? new PluginIndex([]) : PluginIndex.Read(Encoding.UTF8.GetBytes(ListJson)).Index!;
        var http = new CountingHttp();
        var policy = scenario == "policy"
            ? PluginPolicy.Resolve(null, null, n => n == PluginPolicy.EnvironmentVariable ? "1" : null)
            : PluginPolicy.Resolve(null, null, _ => null);
        var view = new PluginsSettingsView(new PluginsSettingsContext(
            () => new PluginCatalogSources(root, linked), () => PluginTrustStore.FromRecordedPreferences(prefs), () => index, () => policy, () => http, null));
        var window = new Window
        {
            Width = width, Height = height, Title = "circuitRF Settings — Plugins",
            Content = new ScrollViewer { Padding = new Thickness(14), Content = view, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("SystemControlBackgroundAltHighBrush"));
        return Capture(window, output, () => $"requests made by opening the tab: {http.Requests}");
    }

    public static int AddressDialog(string output)
    {
        // The dialog reads the versions itself, as it does in the application; this seam answers from memory.
        var http = new CountingHttp { Body = Encoding.UTF8.GetBytes("""
            [ { "tag_name": "v0.3.0", "name": "Wider bandwidth", "prerelease": false,
                "assets": [ { "name": "plugin-0.3.0-any.zip", "browser_download_url": "https://github.com/x/plugin-0.3.0-any.zip" } ] },
              { "tag_name": "v0.3.0-rc1", "prerelease": true, "assets": [] },
              { "tag_name": "v0.2.1", "prerelease": false,
                "assets": [ { "name": "plugin-0.2.1-any.zip", "browser_download_url": "https://github.com/x/plugin-0.2.1-any.zip" } ] } ]
            """) };
        var dialog = new PluginAddressDialog("https://github.com/example/coupled-line-filter", http);
        dialog.Show();
        for (int k = 0; k < 40 && http.Requests == 0; k++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(25); }
        for (int k = 0; k < 20; k++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        return Capture(dialog, output, () => $"requests: {http.Requests}");
    }

    public static int InstallProgress(string output)
    {
        var dialog = new PluginInstallDialog("Installing from https://gitlab.com/example/spiral-inductor (v1.4.0)");
        foreach (string line in new[]
                 {
                     "Using the source archive of v1.4.0.",
                     "Downloading https://gitlab.com/example/spiral-inductor/-/archive/v1.4.0/spiral-inductor-v1.4.0.zip",
                     "Unpacking Spiral Inductor 1.4.0.",
                     "Checking requirements.txt against the 2 other plugin(s) in the shared environment — this reaches the network.",
                     "Installing requirements.txt into the shared plugin environment.",
                     "Collecting numpy>=1.26 (from -r requirements.txt (line 1))",
                     "  Using cached numpy-2.1.3-cp312-cp312-macosx_14_0_arm64.whl.metadata (62 kB)",
                 })
            dialog.Append(line);
        return Capture(dialog, output, null);
    }

    private const string ListJson = """
        { "schema": 1, "plugins": [
          { "id": "example.hello", "name": "Hello Divider", "summary": "The smallest plugin: a two-resistor cell.",
            "repo": "https://github.com/example/hello",
            "versions": [ { "version": "1.1.0", "apiVersion": 1, "assets": { "any": { "url": "https://github.com/example/hello/releases/download/v1.1.0/hello.zip", "sha256": "1111111111111111111111111111111111111111111111111111111111111111" } } } ] },
          { "id": "example.coupled-line-filter", "name": "Coupled-Line Filter", "summary": "Edge-coupled bandpass filters on a microstrip substrate.",
            "repo": "https://github.com/example/crf-coupled-line-filter",
            "versions": [ { "version": "0.3.0", "apiVersion": 1, "assets": { "any": { "url": "https://github.com/example/crf-coupled-line-filter/releases/download/v0.3.0/plugin.zip", "sha256": "2222222222222222222222222222222222222222222222222222222222222222" } } } ] },
          { "id": "example.balun", "name": "Marchand Balun", "summary": "Planar baluns from a centre frequency and impedances.",
            "repo": "https://github.com/example/crf-balun",
            "versions": [ { "version": "2.0.0", "minCircuitRF": "9.0.0", "apiVersion": 1, "assets": { "win-x64": { "url": "https://github.com/example/crf-balun/releases/download/v2/balun-win-x64.zip", "sha256": "3333333333333333333333333333333333333333333333333333333333333333" }, "linux-x64": { "url": "https://github.com/example/crf-balun/releases/download/v2/balun-linux-x64.zip", "sha256": "4444444444444444444444444444444444444444444444444444444444444444" } } } ] }
        ] }
        """;

    private static void RecordListSource(string idDir)
    {
        string file = Path.Combine(idDir, PluginInstaller.ActiveFile);
        var o = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        o["source"] = "list";
        File.WriteAllText(file, o.ToJsonString());
    }

    private static string Examples()
    {
        for (string? dir = Directory.GetCurrentDirectory(); dir is not null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, "tools", "plugin-examples"))) return Path.Combine(dir, "tools", "plugin-examples");
        throw new DirectoryNotFoundException("tools/plugin-examples not found above the working directory.");
    }

    private static int Capture(Window window, string output, Func<string>? note)
    {
        window.Show();
        for (int k = 0; k < 8; k++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); }
        window.CaptureRenderedFrame()!.Save(output);
        Console.WriteLine($"wrote {output}" + (note is null ? "" : $"; {note()}"));
        return 0;
    }

    private sealed class CountingHttp : IPluginHttp
    {
        public int Requests;
        public byte[]? Body;
        public Task<PluginHttpResult> GetBytesAsync(string url, CancellationToken ct) { Requests++; return Task.FromResult(new PluginHttpResult(Body)); }
        public Task<string?> DownloadAsync(string url, string file, CancellationToken ct) { Requests++; return Task.FromResult<string?>("offline"); }
    }
}
