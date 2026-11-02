// ================================================================
//  CliSmoke — does the command line in THIS publish tree answer?
//
//      dotnet run --project tools/CliSmoke -c Release -- <executable> <expected-version>
//                                                        [--kernel <occt-version> | --no-kernel <why>]
//                                                        [--gdstk <gdstk-version> | --no-gdstk <why>]
//
//  <executable> is the application as it will be installed: Contents/MacOS/circuitRF inside the
//  .app, publish/linux-*/circuitRF, or — on Windows — the per-user launcher stub in front of the
//  publish tree, so the pipe route an MCP client takes is the route that is checked
//  (brief-automation-13-installed-cli.md R-aut13-2, route 1). A Linux .AppImage FILE is checked as
//  itself (brief APPIMG R-appimg-3): checks 1-3 run the image, through its AppRun, with
//  APPIMAGE_EXTRACT_AND_RUN=1 so no FUSE is needed; checks 4-5 look for the workers in the image's
//  own files, unpacked by its runtime's --appimage-extract.
//
//  Six checks, all required; exit 0 only when all six pass:
//    1. `--version` prints exactly <expected-version> (the VERSION file) and exits 0.
//    2. `reference --json` exits 0 and its stdout parses as JSON.
//    3. `serve --root <tmp>` answers `initialize` with a result carrying serverInfo, answers
//       `tools/list` with EXACTLY the tools ToolCatalog defines (plus the batch tool), and exits 0
//       when its stdin closes.
//    4. THE GEOMETRY KERNEL (brief-em3d-62 R-em3d62-5e). With --kernel <occt-version>:
//       geometry-kernel/geometry-worker[.exe], found relative to <executable>, answers `--version`
//       with that OCCT version (the recipe's), answers `{"op":"selftest"}` over its framed protocol
//       (brief-em3d-63) with "ok":true and "valid":true, and exits 0 on `quit`. With --no-kernel <why> -- a RID the
//       release does not ship it on, or a package built without it on purpose -- the check reports
//       <why> and passes; whether that is allowed is the packaging script's decision, not this one's.
//       With neither, the check is skipped and says so.
//    5. THE GDSTK WORKER (brief-oasis-gdstk.md §4e), the same way. With --gdstk <gdstk-version>:
//       gdstk-kernel/gdstk-worker[.exe] answers `--version` with that gdstk version (the recipe's),
//       answers `{"op":"selftest"}` with "ok":true and "oas_valid":true, and exits 0 on `shutdown`.
//       --no-gdstk <why> reports and passes; neither skips and says so.
//    6. NO PYTHON SHIPS (brief-gdf-5 R-gdf5-3, gdsfactory series D10): the tree around <executable>
//       -- the whole .app on macOS -- holds no Python environment, installed package, compiled extension
//       module or interpreter. circuitRF runs the user's own interpreter; BundledPython.cs is the rule.
//       `CliSmoke --no-python <executable>` runs this check alone, for a tree this machine cannot execute.
// ================================================================

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Cli.Serve;

// `CliSmoke --no-python <executable>`: check 6 ALONE. It reads files and runs nothing, so it holds for a tree
// built for an architecture this machine cannot execute — which the full smoke run has to skip. Every packaging
// script runs it on exactly those trees.
if (args.Length == 2 && args[0] == "--no-python")
    return NoBundledPython(Path.GetFullPath(args[1])) ? 0 : 1;

string? expectedOcct = null, noKernelWhy = null, expectedGdstk = null, noGdstkWhy = null;
bool usage = args.Length < 2 || args.Length % 2 != 0;
for (int i = 2; !usage && i + 1 < args.Length; i += 2)
{
    string v = args[i + 1].Trim();
    switch (args[i])
    {
        case "--kernel":    expectedOcct = v; break;
        case "--no-kernel": noKernelWhy = v; break;
        case "--gdstk":     expectedGdstk = v; break;
        case "--no-gdstk":  noGdstkWhy = v; break;
        default: usage = true; break;
    }
}
if (usage || (expectedOcct is not null && noKernelWhy is not null) || (expectedGdstk is not null && noGdstkWhy is not null))
{
    Console.Error.WriteLine("usage: CliSmoke <executable> <expected-version> [--kernel <occt-version> | --no-kernel <why>] " +
                            "[--gdstk <gdstk-version> | --no-gdstk <why>]");
    return 2;
}

string exe = Path.GetFullPath(args[0]);
string expectedVersion = args[1].Trim();
var timeout = TimeSpan.FromSeconds(120);

if (!File.Exists(exe))
{
    Console.Error.WriteLine($"FAIL  no executable at {exe}");
    return 1;
}

Console.WriteLine($"Smoke-testing the command line in {exe}");

// An AppImage runs from a FUSE mount, which a container or a CI runner may not have. Extract-and-run
// is the runtime's own answer, and every process started below inherits it.
bool isAppImage = exe.EndsWith(".AppImage", StringComparison.Ordinal);
if (isAppImage) Environment.SetEnvironmentVariable("APPIMAGE_EXTRACT_AND_RUN", "1");

// Where the workers are looked for: beside the executable, or for an AppImage beside the executable
// INSIDE it - the runtime unpacks the image (no FUSE either), and the tree is usr/lib/circuitrf/.
string? unpacked = null;
string workerBase = exe;
if (isAppImage && (expectedOcct is not null || expectedGdstk is not null))
{
    unpacked = Path.Combine(Path.GetTempPath(), "circuitrf-smoke-appimage-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(unpacked);
    var psi = Start(exe, ["--appimage-extract"]);
    psi.WorkingDirectory = unpacked;
    var x = RunPsiToEnd(psi, timeout);
    workerBase = Path.Combine(unpacked, "squashfs-root", "usr", "lib", "circuitrf", "circuitRF");
    if (x.ExitCode != 0) Console.WriteLine($"note  --appimage-extract exited {x.ExitCode}; the worker checks below will say what is missing");
}

// A binary this machine cannot execute at all (another architecture, no Rosetta, no emulation) is
// a refusal about the MACHINE, not a verdict about the build. The packaging scripts decide up front
// which architectures they can run and say loudly which they could not; this is the backstop.
try { using var probe = Process.Start(Start(exe, ["--version"]))!; probe.WaitForExit(); }
catch (System.ComponentModel.Win32Exception ex)
{
    Console.Error.WriteLine($"FAIL  this machine cannot execute {exe}: {ex.Message}");
    return 1;
}

int failures = 0;

// ── 1. --version ─────────────────────────────────────────────────────────────
{
    var r = RunToEnd(exe, ["--version"], timeout);
    string got = r.Stdout.TrimEnd('\r', '\n');
    if (r.ExitCode == 0 && got == expectedVersion && !got.Contains('\n'))
        Console.WriteLine($"ok    --version  -> {got}");
    else
        failures += Fail("--version", $"exit {r.ExitCode}, printed '{Escape(r.Stdout)}', expected exactly '{expectedVersion}'", r);
}

// ── 2. reference --json ──────────────────────────────────────────────────────
{
    var r = RunToEnd(exe, ["reference", "--json"], timeout);
    string? parseError = null;
    try { using var _ = JsonDocument.Parse(r.Stdout); }
    catch (JsonException ex) { parseError = ex.Message; }

    if (r.ExitCode == 0 && parseError is null)
        Console.WriteLine($"ok    reference --json  -> {r.Stdout.Length:N0} bytes of JSON");
    else
        failures += Fail("reference --json",
            parseError is null ? $"exit {r.ExitCode}" : $"exit {r.ExitCode}, stdout is not JSON: {parseError}", r);
}

// ── 3. serve ─────────────────────────────────────────────────────────────────
{
    string root = Path.Combine(Path.GetTempPath(), "circuitrf-smoke-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(root);
    try   { failures += Serve(exe, root, timeout); }
    finally { try { Directory.Delete(root, recursive: true); } catch { /* best effort */ } }
}

// ── 4. the geometry kernel ───────────────────────────────────────────────────
if (expectedOcct is not null)
    failures += Kernel(workerBase, expectedOcct, timeout);
else if (noKernelWhy is not null)
    Console.WriteLine($"ok    geometry kernel  -> not shipped here: {noKernelWhy}");
else
    Console.WriteLine("skip  geometry kernel  -> not asked (no --kernel or --no-kernel)");

// ── 5. the gdstk worker ──────────────────────────────────────────────────────
if (expectedGdstk is not null)
    failures += Gdstk(workerBase, expectedGdstk, timeout);
else if (noGdstkWhy is not null)
    Console.WriteLine($"ok    gdstk worker  -> not shipped here: {noGdstkWhy}");
else
    Console.WriteLine("skip  gdstk worker  -> not asked (no --gdstk or --no-gdstk)");

// ── 6. no Python environment ships (gdsfactory series D10) ───────────────────
if (isAppImage)
    Console.WriteLine("skip  bundled Python  -> an AppImage is built from a publish tree this check already passed");
else
{
    if (!NoBundledPython(exe)) failures++;
}

if (unpacked is not null) try { Directory.Delete(unpacked, recursive: true); } catch { /* best effort */ }

Console.WriteLine(failures == 0
    ? "PASS  the command line in this publish tree answers."
    : $"FAIL  {failures} of 6 checks failed; this tree must not be packaged.");
return failures == 0 ? 0 : 1;

// Check 6 (BundledPython.cs is the rule): true when the tree around the executable carries no Python.
static bool NoBundledPython(string exe)
{
    string tree = CliSmoke.BundledPython.TreeOf(exe);
    var bundled = CliSmoke.BundledPython.Find(tree);
    if (bundled.Count == 0)
    {
        Console.WriteLine("ok    bundled Python  -> no Python environment, installed package or extension module in the tree");
        return true;
    }
    Console.Error.WriteLine($"FAIL  bundled Python: circuitRF runs the user's own interpreter and ships none, but {tree} holds:");
    foreach (string p in bundled) Console.Error.WriteLine($"        {p}");
    return false;
}

// ─────────────────────────────────────────────────────────────────────────────

// The worker is found relative to the executable CliSmoke was given: beside it (the .app's
// Contents/MacOS, a Linux publish tree), or -- on Windows, where the smoke target is the per-user
// launcher stub -- inside the version directory the stub's `current` file names.
static string? FindWorker(string exe, string folder = "geometry-kernel", string program = "geometry-worker")
{
    string name = OperatingSystem.IsWindows() ? program + ".exe" : program;
    string dir = Path.GetDirectoryName(exe)!;
    var candidates = new List<string> { Path.Combine(dir, folder, name) };
    string current = Path.Combine(dir, "current");
    if (File.Exists(current))
        candidates.Add(Path.Combine(dir, File.ReadAllText(current).Trim(), folder, name));
    return candidates.FirstOrDefault(File.Exists);
}

// The gdstk worker (tools/gdstk-worker): one statically linked program, the geometry kernel's frames.
static int Gdstk(string exe, string expectedGdstk, TimeSpan timeout)
{
    string? worker = FindWorker(exe, "gdstk-kernel", "gdstk-worker");
    if (worker is null)
    {
        Console.WriteLine($"FAIL  gdstk worker: no gdstk-kernel/gdstk-worker beside {exe}");
        return 1;
    }

    var v = RunToEnd(worker, ["--version"], timeout);
    string? gdstk = v.Stdout.Split('\n').Select(l => l.Trim())
                     .FirstOrDefault(l => l.StartsWith("gdstk ", StringComparison.Ordinal))?["gdstk ".Length..];
    if (v.ExitCode != 0 || gdstk != expectedGdstk)
        return Fail("gdstk worker --version", $"exit {v.ExitCode}, gdstk '{gdstk}', expected '{expectedGdstk}' (the recipe's)", v);
    Console.WriteLine($"ok    gdstk worker --version  -> {Escape(v.Stdout.TrimEnd())}");

    var psi = Start(worker, []);
    psi.RedirectStandardInput = true;
    using var p = Process.Start(psi)!;
    var stderr = p.StandardError.ReadToEndAsync();
    Stream toWorker = p.StandardInput.BaseStream, fromWorker = p.StandardOutput.BaseStream;
    try
    {
        SendFrame(toWorker, "{\"op\":\"selftest\"}");
        var read = Task.Run(() => ReadFrameJson(fromWorker));
        if (!read.Wait(timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            Console.WriteLine($"FAIL  gdstk worker selftest: no answer within {timeout.TotalSeconds:0} s");
            return 1;
        }
        string answer = read.Result ?? "";
        JsonObject? json = null;
        try { json = JsonNode.Parse(answer) as JsonObject; } catch (JsonException) { }
        static bool True(JsonNode? n) => n is JsonValue jv && jv.TryGetValue<bool>(out bool b) && b;
        bool ok = True(json?["ok"]) && True(json?["oas_valid"]);

        SendFrame(toWorker, "{\"op\":\"shutdown\"}");
        p.StandardInput.Close();
        bool exited = p.WaitForExit((int)timeout.TotalMilliseconds);
        if (!ok)
        {
            Console.WriteLine($"FAIL  gdstk worker selftest: answered '{answer}'");
            if (stderr.IsCompleted && stderr.Result.Length > 0) Console.WriteLine("      stderr:\n" + Indent(stderr.Result));
            return 1;
        }
        if (!exited || p.ExitCode != 0)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            Console.WriteLine("FAIL  gdstk worker: did not exit 0 on shutdown");
            return 1;
        }
        Console.WriteLine($"ok    gdstk worker selftest  -> {answer}");
        return 0;
    }
    catch (IOException ex)
    {
        Console.WriteLine($"FAIL  gdstk worker: the pipe broke: {ex.Message}");
        return 1;
    }
}

static int Kernel(string exe, string expectedOcct, TimeSpan timeout)
{
    string? worker = FindWorker(exe);
    if (worker is null)
    {
        Console.WriteLine($"FAIL  geometry kernel: no geometry-kernel/geometry-worker beside {exe}");
        return 1;
    }

    var v = RunToEnd(worker, ["--version"], timeout);
    string? occt = v.Stdout.Split('\n').Select(l => l.Trim())
                    .FirstOrDefault(l => l.StartsWith("occt ", StringComparison.Ordinal))?["occt ".Length..];
    if (v.ExitCode != 0 || occt != expectedOcct)
        return Fail("geometry kernel --version", $"exit {v.ExitCode}, OCCT '{occt}', expected '{expectedOcct}' (the recipe's)", v);
    Console.WriteLine($"ok    geometry kernel --version  -> {Escape(v.Stdout.TrimEnd())}");

    var psi = Start(worker, []);
    psi.RedirectStandardInput = true;
    using var p = Process.Start(psi)!;
    var stderr = p.StandardError.ReadToEndAsync();
    Stream toWorker = p.StandardInput.BaseStream, fromWorker = p.StandardOutput.BaseStream;
    try
    {
        // The protocol is framed (brief-em3d-63 R-em3d63-4a): [uint32 jsonLen][uint32 binLen][json][bin],
        // little-endian. Written out here rather than referenced: this program depends on no project.
        SendFrame(toWorker, "{\"op\":\"selftest\"}");
        var read = Task.Run(() => ReadFrameJson(fromWorker));
        if (!read.Wait(timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            Console.WriteLine($"FAIL  geometry kernel selftest: no answer within {timeout.TotalSeconds:0} s");
            return 1;
        }
        string answer = read.Result ?? "";
        JsonObject? json = null;
        try { json = JsonNode.Parse(answer) as JsonObject; } catch (JsonException) { }
        static bool True(JsonNode? n) => n is JsonValue jv && jv.TryGetValue<bool>(out bool b) && b;
        bool ok = True(json?["ok"]) && True(json?["valid"]);

        SendFrame(toWorker, "{\"op\":\"quit\"}");
        p.StandardInput.Close();
        bool exited = p.WaitForExit((int)timeout.TotalMilliseconds);
        if (!ok)
        {
            Console.WriteLine($"FAIL  geometry kernel selftest: answered '{answer}'");
            if (stderr.IsCompleted && stderr.Result.Length > 0) Console.WriteLine("      stderr:\n" + Indent(stderr.Result));
            return 1;
        }
        if (!exited || p.ExitCode != 0)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            Console.WriteLine($"FAIL  geometry kernel: did not exit 0 on quit");
            return 1;
        }
        Console.WriteLine($"ok    geometry kernel selftest  -> {answer}");
        return 0;
    }
    catch (IOException ex)
    {
        Console.WriteLine($"FAIL  geometry kernel: the pipe broke: {ex.Message}");
        return 1;
    }
}

static void SendFrame(Stream s, string json)
{
    byte[] body = Encoding.UTF8.GetBytes(json);
    var head = new byte[8];
    BitConverter.TryWriteBytes(head.AsSpan(0), (uint)body.Length);   // little-endian on every RID shipped
    s.Write(head);
    s.Write(body);
    s.Flush();
}

// A reply's JSON, its binary part read and dropped; null when the pipe closed first.
static string? ReadFrameJson(Stream s)
{
    var head = new byte[8];
    if (!Fill(s, head)) return null;
    var body = new byte[BitConverter.ToUInt32(head, 0)];
    var bin = new byte[BitConverter.ToUInt32(head, 4)];
    return Fill(s, body) && Fill(s, bin) ? Encoding.UTF8.GetString(body) : null;

    static bool Fill(Stream s, byte[] b)
    {
        for (int at = 0, n; at < b.Length; at += n)
            if ((n = s.Read(b, at, b.Length - at)) == 0) return false;
        return true;
    }
}

// ─────────────────────────────────────────────────────────────────────────────

static int Serve(string exe, string root, TimeSpan timeout)
{
    var psi = Start(exe, ["serve", "--root", root]);
    psi.RedirectStandardInput = true;
    using var p = Process.Start(psi)!;

    var lines  = new BlockingCollection<string>();
    var stderr = new StringBuilder();
    p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
    p.BeginErrorReadLine();
    var reader = new Thread(() =>
    {
        try { while (p.StandardOutput.ReadLine() is { } l) lines.Add(l); }
        catch { /* process gone */ }
        finally { lines.CompleteAdding(); }
    }) { IsBackground = true };
    reader.Start();

    var input = p.StandardInput;
    input.AutoFlush = true;

    string Err() { lock (stderr) return stderr.ToString(); }

    // The response to `id`, skipping any notification the server sends first.
    JsonObject? Await(int id)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!lines.TryTake(out var line, TimeSpan.FromMilliseconds(250)))
            {
                if (lines.IsCompleted) return null;
                continue;
            }
            JsonObject? msg;
            try { msg = JsonNode.Parse(line) as JsonObject; } catch (JsonException) { continue; }
            if (msg?["id"] is JsonValue v && v.TryGetValue<int>(out int got) && got == id) return msg;
        }
        return null;
    }

    try
    {
        input.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"circuitrf-package-smoke","version":"1"}}}""");
        var init = Await(1);
        if (init?["result"]?["serverInfo"] is not JsonObject info)
            return FailServe(p, $"initialize answered {(init is null ? "nothing" : init.ToJsonString())}", Err());
        Console.WriteLine($"ok    serve initialize  -> serverInfo {info.ToJsonString()}");

        input.WriteLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        input.WriteLine("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var list = Await(2);
        if (list?["result"]?["tools"] is not JsonArray tools)
            return FailServe(p, $"tools/list answered {(list is null ? "nothing" : list.ToJsonString())}", Err());

        // THE CATALOGUE, not a count: what ToolCatalog defines plus the batch and job tools, the two
        // that are not command lines (McpServer's tools/list adds them the same way).
        var expected = ToolCatalog.Tools.Select(t => t.Name).Append(HistoryBatch.ToolName).Append(ServeJobs.ToolName)
                                  .ToHashSet(StringComparer.Ordinal);
        var advertised = tools.Select(t => t?["name"]?.GetValue<string>() ?? "").ToHashSet(StringComparer.Ordinal);
        var missing = expected.Except(advertised).OrderBy(n => n).ToList();
        var extra   = advertised.Except(expected).OrderBy(n => n).ToList();
        if (missing.Count > 0 || extra.Count > 0)
            return FailServe(p, $"tools/list does not match the catalogue — missing [{string.Join(", ", missing)}], "
                              + $"unexpected [{string.Join(", ", extra)}]", Err());
        Console.WriteLine($"ok    serve tools/list  -> all {expected.Count} catalogue tools: {string.Join(", ", expected.OrderBy(n => n))}");

        // A client disconnecting is end of stream, which serve treats as a clean exit.
        input.Close();
        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            return FailServe(p, "did not exit after stdin closed", Err());
        p.WaitForExit();
        if (p.ExitCode != 0)
            return FailServe(p, $"exited {p.ExitCode} after stdin closed", Err());
        Console.WriteLine("ok    serve exits 0 when stdin closes");
        return 0;
    }
    catch (IOException ex)
    {
        return FailServe(p, $"the pipe broke: {ex.Message}", Err());
    }
}

static int FailServe(Process p, string why, string stderr)
{
    try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
    Console.WriteLine($"FAIL  serve: {why}");
    if (stderr.Length > 0) Console.WriteLine("      stderr:\n" + Indent(stderr));
    return 1;
}

static int Fail(string check, string why, Result r)
{
    Console.WriteLine($"FAIL  {check}: {why}");
    if (r.Stderr.Length > 0) Console.WriteLine("      stderr:\n" + Indent(r.Stderr));
    return 1;
}

static ProcessStartInfo Start(string exe, string[] argv)
{
    var psi = new ProcessStartInfo(exe)
    {
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        UseShellExecute        = false,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding  = Encoding.UTF8,
        WorkingDirectory       = Path.GetTempPath(),
    };
    foreach (var a in argv) psi.ArgumentList.Add(a);
    return psi;
}

static Result RunToEnd(string exe, string[] argv, TimeSpan timeout) => RunPsiToEnd(Start(exe, argv), timeout);

static Result RunPsiToEnd(ProcessStartInfo psi, TimeSpan timeout)
{
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    if (!p.WaitForExit((int)timeout.TotalMilliseconds))
    {
        try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
        return new Result(-1, "", $"(timed out after {timeout.TotalSeconds:0} s — a GUI launch instead of the CLI looks exactly like this)");
    }
    p.WaitForExit();
    return new Result(p.ExitCode, stdout.Result, stderr.Result);
}

static string Escape(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");
static string Indent(string s) => string.Join('\n', s.TrimEnd().Split('\n').Select(l => "        " + l));

record Result(int ExitCode, string Stdout, string Stderr);
