using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui;

/// <summary>
/// Opens the bundled User Documentation (the <c>docs/user/</c> HTML set, copied into the app output) in
/// the OS default browser. Help buttons deep-link to the Reference Guide via the stable anchor scheme
/// documented in <c>docs/user/reference/index.html</c>:
///   component help → reference/components.html#&lt;symbolkind-lowercase&gt;
///   analysis help  → reference/simulations.html#&lt;analysis&gt;
///   plot help      → reference/plot-types.html#&lt;type&gt;
///   C–V editor     → reference/nonlinear-capacitor.html
///
/// The docs are served over a loopback HTTP server (<c>http://127.0.0.1:&lt;port&gt;/</c>), NOT via a
/// <c>file://</c> URL: Safari (the macOS default browser) blocks a <c>file://</c> page from loading its
/// sibling <c>file://</c> CSS/images, so styles and symbols would silently fail to load. Over HTTP every
/// browser loads them normally. The server binds to loopback only, serves files strictly under the docs
/// root, and starts on first use for the app's lifetime. Failures are swallowed — Help must never crash
/// the app.
/// </summary>
public static class DocLauncher
{
    private static readonly object _gate = new();
    private static HttpListener? _listener;
    private static string? _baseUrl;   // e.g. http://127.0.0.1:51763/
    private static string? _docsRoot;

    /// <summary>
    /// Open a documentation page (relative to <c>docs/user/</c>, default the landing page), optionally
    /// scrolled to an anchor (without the leading '#').
    /// </summary>
    public static void Open(string page = "index.html", string? anchor = null)
    {
        try
        {
            string? root = ResolveDocsRoot();
            if (root is null) return;

            string? baseUrl = EnsureServer(root);
            if (baseUrl is null) return;

            string url = baseUrl + page.Replace('\\', '/').TrimStart('/');
            if (!string.IsNullOrEmpty(anchor)) url += "#" + anchor;

            OpenUrl(url);
        }
        catch { /* Help must never throw. */ }
    }

    /// <summary>Open the Reference entry for a component (Parameter Editor "Help").</summary>
    public static void OpenComponent(SymbolKind kind)
    {
        string anchor = kind switch
        {
            // The three tuner variants share one Reference section.
            SymbolKind.Tuner or SymbolKind.SourceTuner or SymbolKind.LoadTuner => "tuner",
            SymbolKind.Generic => "",                       // no dedicated section → page top
            _ => kind.ToString().ToLowerInvariant(),        // matches components.html ids (resistor, sdd, …)
        };
        Open("reference/components.html", anchor.Length == 0 ? null : anchor);
    }

    /// <summary>Open the Reference entry for an analysis (Analyses "Help").</summary>
    public static void OpenAnalysis(string? anchor = null)
        => Open("reference/simulations.html", anchor);

    /// <summary>Open the Reference entry for a plot type (Plot inspector "Help").</summary>
    public static void OpenPlotType(string? anchor = null)
        => Open("reference/plot-types.html", anchor);

    // ── Docs-root resolution ────────────────────────────────────────────────
    // Bundled layout: <appOutput>/docs/user/index.html. For an in-repo `dotnet run`,
    // the copy lands in bin/.../docs/user too; the parent-walk is a dev fallback.
    private static string? ResolveDocsRoot()
    {
        string baseDir = AppContext.BaseDirectory;

        string bundled = Path.Combine(baseDir, "docs", "user");
        if (File.Exists(Path.Combine(bundled, "index.html"))) return Path.GetFullPath(bundled);

        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "docs", "user");
            if (File.Exists(Path.Combine(candidate, "index.html"))) return Path.GetFullPath(candidate);
        }
        return null;
    }

    // ── Loopback static-file server ─────────────────────────────────────────
    private static string? EnsureServer(string root)
    {
        lock (_gate)
        {
            if (_listener is { IsListening: true } && _baseUrl is not null
                && string.Equals(_docsRoot, root, StringComparison.Ordinal))
                return _baseUrl;

            try { _listener?.Close(); } catch { /* ignore */ }

            int port = FreeLoopbackPort();
            string baseUrl = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(baseUrl);
            listener.Start();

            _listener = listener;
            _baseUrl  = baseUrl;
            _docsRoot = root;
            _ = Task.Run(() => ServeLoop(listener, root));
            return baseUrl;
        }
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task ServeLoop(HttpListener listener, string root)
    {
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; }   // listener stopped/disposed
            try { ServeOne(ctx, rootFull); }
            catch { try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { /* ignore */ } }
        }
    }

    private static void ServeOne(HttpListenerContext ctx, string rootFull)
    {
        var resp = ctx.Response;
        string rel = Uri.UnescapeDataString(ctx.Request.Url?.AbsolutePath ?? "/").TrimStart('/');
        if (rel.Length == 0) rel = "index.html";

        string full = Path.GetFullPath(Path.Combine(rootFull, rel.Replace('/', Path.DirectorySeparatorChar)));
        // Path-traversal guard: the resolved file must stay within the docs root, and exist.
        if (!full.StartsWith(rootFull, StringComparison.Ordinal) || !File.Exists(full))
        {
            resp.StatusCode = 404; resp.Close(); return;
        }

        byte[] bytes = File.ReadAllBytes(full);
        resp.ContentType = ContentType(Path.GetExtension(full));
        resp.ContentLength64 = bytes.Length;
        resp.OutputStream.Write(bytes, 0, bytes.Length);
        resp.Close();
    }

    private static string ContentType(string ext) => ext.ToLowerInvariant() switch
    {
        ".html" or ".htm"  => "text/html; charset=utf-8",
        ".css"             => "text/css; charset=utf-8",
        ".svg"             => "image/svg+xml",
        ".png"             => "image/png",
        ".jpg" or ".jpeg"  => "image/jpeg",
        ".gif"             => "image/gif",
        ".js"              => "text/javascript; charset=utf-8",
        ".ico"             => "image/x-icon",
        ".woff2"           => "font/woff2",
        ".woff"            => "font/woff",
        ".ttf"             => "font/ttf",
        _                  => "application/octet-stream",
    };

    // ── Cross-platform open (mirrors WorkspaceViewModel.Reveal) ──────────────
    private static void OpenUrl(string url)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
    }
}
