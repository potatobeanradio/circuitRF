// ================================================================
//  CliSmoke — does the command line in THIS publish tree answer?
//
//      dotnet run --project tools/CliSmoke -c Release -- <executable> <expected-version>
//
//  <executable> is the application as it will be installed: Contents/MacOS/circuitRF inside the
//  .app, publish/linux-*/circuitRF, or — on Windows — the per-user launcher stub in front of the
//  publish tree, so the pipe route an MCP client takes is the route that is checked
//  (brief-automation-13-installed-cli.md R-aut13-2, route 1).
//
//  Three checks, all required; exit 0 only when all three pass:
//    1. `--version` prints exactly <expected-version> (the VERSION file) and exits 0.
//    2. `reference --json` exits 0 and its stdout parses as JSON.
//    3. `serve --root <tmp>` answers `initialize` with a result carrying serverInfo, answers
//       `tools/list` with EXACTLY the tools ToolCatalog defines (plus the batch tool), and exits 0
//       when its stdin closes.
// ================================================================

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Cli.Serve;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: CliSmoke <executable> <expected-version>");
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

Console.WriteLine(failures == 0
    ? "PASS  the command line in this publish tree answers."
    : $"FAIL  {failures} of 3 checks failed; this tree must not be packaged.");
return failures == 0 ? 0 : 1;

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

        // THE CATALOGUE, not a count: what ToolCatalog defines plus the batch tool, which is the one
        // tool that is not a command line (McpServer's tools/list adds it the same way).
        var expected = ToolCatalog.Tools.Select(t => t.Name).Append(HistoryBatch.ToolName)
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

static Result RunToEnd(string exe, string[] argv, TimeSpan timeout)
{
    using var p = Process.Start(Start(exe, argv))!;
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
