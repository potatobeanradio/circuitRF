using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;

namespace CircuitRF.Cli.Serve;

/// <summary>
/// The protocol adapter: it advertises circuitRF's capabilities to an external client and invokes
/// them on request. It owns no logic (R-aut-1) — it translates a request into an argument vector,
/// hands that to <see cref="CliEntry.Run"/>, and hands the document back.
///
/// <para><b>It calls the verb; it does not re-implement it.</b> That is what makes R-aut-13 —
/// nothing reachable here that is not reachable from the command line, and vice versa — a property
/// of the code rather than a rule to remember. The parity gate compares two documents that came out
/// of one function.</para>
///
/// <para><b>One call at a time, but the reader never blocks.</b> The verbs use process-wide state
/// (<see cref="JsonRun"/>, <see cref="Console.Out"/>), so two capability calls cannot be in flight
/// together; they are queued onto one worker. The reader loop keeps reading regardless, which is the
/// whole reason for the split: <c>notifications/cancelled</c> and <c>ping</c> have to be answerable
/// while a full-wave run is going, and a run that cannot be cancelled is one a client times out on
/// and retries, doubling the cost of the run it gave up on.</para>
///
/// <para><b>Destructive operations are refusals, not confirmations</b> (R-aut5-8) — and they are
/// refusals by omission rather than by a filter: there is no tool that deletes, and no capability
/// below writes outside the paths it chooses itself. A client that wants a file gone deletes it
/// itself.</para>
/// </summary>
internal sealed class McpServer
{
    /// <summary>The protocol revisions this server knows how to speak. A client asking for one of
    /// them is answered in it; anything else is answered in the newest, which is what the protocol
    /// says to do rather than failing the handshake.</summary>
    private static readonly string[] KnownProtocolVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private readonly JsonRpc  _rpc;
    private readonly PathRoot _root;

    private readonly BlockingCollection<Action> _work = new(new ConcurrentQueue<Action>());
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new();

    /// <summary>
    /// RC-5's batch, which is the one tool that is not a command line. It holds SESSION state — a
    /// batch is opened, stays open while an agent works, and is closed — and a process that exits
    /// after one command cannot hold that, which is why <c>revision-control.md</c> §5.3d puts it
    /// here and leaves the other three history nouns as CLI verbs.
    /// </summary>
    private readonly HistoryBatch _batch;

    /// <summary>
    /// Whether the installation this server runs out of has changed under it (R-aut13-4). Checked
    /// before every call; see <see cref="InstallationGuard"/> for what was measured.
    /// </summary>
    private readonly InstallationGuard _installation;

    /// <summary>Set when the installation changed: the read loop stops after the current message.</summary>
    private bool _leaving;

    public McpServer(JsonRpc rpc, PathRoot root, InstallationGuard? installation = null)
    {
        _rpc          = rpc;
        _root         = root;
        _batch        = new HistoryBatch(root);
        _installation = installation ?? InstallationGuard.ForThisProcess();
    }

    /// <summary>Reads until the client disconnects. Returns the process exit code.</summary>
    public int Serve()
    {
        var worker = new Thread(WorkerLoop) { IsBackground = true, Name = "circuitrf-serve" };
        worker.Start();

        try
        {
            while (true)
            {
                var message = _rpc.Read(out string? malformed);
                if (message is null)
                {
                    // A line that is not JSON is the client's error and is reported as one; end of
                    // stream is the client disconnecting, which is not an error at all.
                    if (malformed is not null) _rpc.Error(null, JsonRpc.ParseError, "Not a JSON-RPC message.");
                    break;
                }
                Handle(message);
                if (_leaving) break;
            }
        }
        finally
        {
            // A client that disconnects mid-run: stop the run rather than leaving the process
            // holding a solve nobody is waiting for.
            foreach (var cts in _inFlight.Values) { try { cts.Cancel(); } catch { /* already done */ } }
            _work.CompleteAdding();
            worker.Join(TimeSpan.FromSeconds(30));
        }

        return _leaving ? 1 : 0;
    }

    // ── dispatch ─────────────────────────────────────────────────────────────

    private void Handle(JsonObject message)
    {
        string? method = message["method"]?.GetValue<JsonElement>().ValueKind == JsonValueKind.String
            ? message["method"]!.GetValue<string>()
            : null;
        JsonNode? id = message["id"];

        if (method is null)
        {
            // A response, not a request. This server issues no requests of its own, so there is
            // nothing it can be an answer to; ignoring it is what the protocol asks for.
            return;
        }

        // R-aut13-4: an installation replaced under this process can no longer load code it has not
        // already loaded, so a call is answered with the reason and the server leaves — never a
        // server that answers some calls and fails others with a missing-file message.
        if (method is "tools/call" or "resources/read" && _installation.Changed(out string? why))
        {
            string sentence = InstallationGuard.Sentence(why);
            _rpc.Error(id, JsonRpc.InstallationChanged, sentence);
            Console.Error.WriteLine($"[circuitRF] serve: {sentence}");
            _leaving = true;
            return;
        }

        switch (method)
        {
            case "initialize":
                _rpc.Result(id, Initialize(message["params"] as JsonObject));
                return;

            case "notifications/initialized" or "notifications/roots/list_changed":
                return;

            case "ping":
                _rpc.Result(id, new JsonObject());
                return;

            case "tools/list":
            {
                var tools = ToolCatalog.Advertise();
                tools.Add(HistoryBatch.Advertise());
                _rpc.Result(id, new JsonObject { ["tools"] = tools });
            }
                return;

            case "tools/call":
                Enqueue(id, message["params"] as JsonObject);
                return;

            case "resources/list":
                _rpc.Result(id, new JsonObject { ["resources"] = ReferenceResources.Advertise() });
                return;

            case "resources/templates/list":
                // None. Every reference resource is a fixed URI and there is nothing to template
                // over; answering with an empty list is what stops a client asking again.
                _rpc.Result(id, new JsonObject { ["resourceTemplates"] = new JsonArray() });
                return;

            case "resources/read":
                EnqueueResource(id, message["params"] as JsonObject);
                return;

            case "notifications/cancelled":
                Cancel(message["params"] as JsonObject);
                return;

            default:
                if (id is not null) _rpc.Error(id, JsonRpc.MethodNotFound, $"No method '{method}'.");
                return;
        }
    }

    private JsonObject Initialize(JsonObject? parameters)
    {
        string? asked = parameters?["protocolVersion"]?.GetValue<JsonElement>().ValueKind == JsonValueKind.String
            ? parameters["protocolVersion"]!.GetValue<string>()
            : null;

        return new JsonObject
        {
            ["protocolVersion"] = asked is not null && KnownProtocolVersions.Contains(asked)
                ? asked
                : KnownProtocolVersions[0],
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
                // The reference surface, on its cheaper channel: a resource costs a URI and a title
                // until it is read, where a tool description is a standing per-session cost
                // (R-aut-9, R-aut6-4). The `reference` TOOL offers the same bytes for the clients
                // that do not surface resources to the model at all.
                ["resources"] = new JsonObject { ["listChanged"] = false, ["subscribe"] = false },
            },
            ["serverInfo"]   = new JsonObject { ["name"] = "circuitrf", ["version"] = Version() },
            // Terse, English, invariant (R-aut5-7). It says the three things a client cannot learn
            // from a tool schema — and then, since AUT-10 R-aut10-5, SHOWS them once. Most of what
            // the exercise behind this series learned by trial and error is in that sequence, and a
            // worked example is the one form of documentation a client does not have to go and ask
            // for. It costs ~1 kB per session against tools/list's 20 kB.
            ["instructions"] =
                "circuitRF is driven by writing its documents and then running, checking or " +
                "explaining them; the file formats are the interface and there are no per-primitive " +
                "edit tools. What may be written is in the reference resources, and in the " +
                "'reference' tool for the same bytes. NO TOOL HERE WRITES A FILE OF YOUR TEXT — you " +
                "supply your own file writing; what these tools write is what they produce " +
                "(a created document, a result, a netlist, a picture). Every path resolves under " +
                "this server's root, and a path outside it is refused. Nothing here deletes or " +
                "overwrites an existing workspace; 'create' does make missing parent directories.\n" +
                "\n" +
                "'find' says what is already here. 'netlist' extracts the .cnl a schematic runs as, " +
                "which is also the reference to check your own authoring against.\n" +
                "\n" +
                "End to end. Nothing here writes a document — use your own file tools for step 3.\n" +
                "  1. create   what=workspace path=<root> name=demo      -> <root>/demo/.cws\n" +
                "  2. reference topic=analyses type=sparam               -> every legal key, with defaults\n" +
                "  3. write <root>/demo/pad.cnl:\n" +
                "       Port:P1 in  0 Num=1 Z=50 Ohm\n" +
                "       Port:P2 out 0 Num=2 Z=50 Ohm\n" +
                "       R:R1 in  mid R=96 Ohm\n" +
                "       R:R2 mid 0   R=71.2 Ohm\n" +
                "       R:R3 mid out R=96 Ohm\n" +
                "       analysis SP1 type=sparam start=1 stop=6 npts=51 Unit=GHz\n" +
                "  4. check    path=<root>/demo/pad.cnl                  -> 0 errors, 0 warnings\n" +
                "  5. run      analysis=sparam path=<root>/demo/pad.cnl output=<root>/demo/pad.s2p\n" +
                "  6. read     path=<root>/demo/pad.s2p                  -> the cubes; narrow with only/at/range\n" +
                "On an instance line the NETS come first and every 'Key=value' after them; how many " +
                "nets each type takes is the 'nets' field of reference components, which is not the " +
                "same number as its symbol's pin count. render draws a .csch, .csym, .clay or .cdd " +
                "— never a .cnl; 'plot' draws a result file. An unknown analysis key or type= token " +
                "is refused, not ignored.",
        };
    }

    // ── tool calls ───────────────────────────────────────────────────────────

    private void Enqueue(JsonNode? id, JsonObject? parameters)
    {
        string? tool = parameters?["name"]?.GetValue<JsonElement>().ValueKind == JsonValueKind.String
            ? parameters["name"]!.GetValue<string>()
            : null;

        if (tool is null)
        {
            _rpc.Error(id, JsonRpc.InvalidParams, "tools/call needs a tool name.");
            return;
        }

        var    arguments     = parameters?["arguments"] as JsonObject;
        var    progressToken = parameters?["_meta"]?["progressToken"];
        string key           = KeyOf(id);
        var    cts           = new CancellationTokenSource();
        _inFlight[key] = cts;

        _work.Add(() =>
        {
            try     { Invoke(id, tool, arguments, progressToken, cts.Token); }
            finally { _inFlight.TryRemove(key, out _); cts.Dispose(); }
        });
    }

    /// <summary>
    /// <c>resources/read</c>. Queued onto the same worker as a tool call, because it reaches the same
    /// verb through the same process-wide state.
    ///
    /// <para><b>It returns the bytes the tool returns</b> (R-aut-13, gate 2): both translate to
    /// <c>reference &lt;topic&gt; --json</c> and hand back what came out, so a resource and a tool
    /// call for one topic cannot disagree — they are one code path with two envelopes.</para>
    /// </summary>
    private void EnqueueResource(JsonNode? id, JsonObject? parameters)
    {
        string? uri = parameters?["uri"]?.GetValue<JsonElement>().ValueKind == JsonValueKind.String
            ? parameters["uri"]!.GetValue<string>()
            : null;

        if (uri is null)
        {
            _rpc.Error(id, JsonRpc.InvalidParams, "resources/read needs a uri.");
            return;
        }

        if (ReferenceResources.TopicOf(uri) is not { } topic)
        {
            // Not an error frame: an unknown resource is answered the way an unknown topic is
            // answered on the command line — a document naming the ones that exist (R-aut-7).
            _work.Add(() => _rpc.Result(id, ResourceEnvelope(
                uri, RefusalDocument("reference", CliDiagnostics.ReferenceUnknownResource(
                    uri, string.Join(", ", ReferenceResources.Uris()))))));
            return;
        }

        string key = KeyOf(id);
        var    cts = new CancellationTokenSource();
        _inFlight[key] = cts;

        _work.Add(() =>
        {
            try
            {
                // `--json` here for the reason ToArgv appends it there: a document is what a read
                // returns, always (R-aut5-5), and it is what makes these bytes the tool's bytes.
                _rpc.Result(id, ResourceEnvelope(uri, RunVerb(["reference", topic, "--json"], null,
                                                             cts.Token, out _, "reference")));
            }
            finally { _inFlight.TryRemove(key, out _); cts.Dispose(); }
        });
    }

    /// <summary>The protocol's own envelope for a resource, around the document unchanged.</summary>
    private static JsonObject ResourceEnvelope(string uri, string document) => new()
    {
        ["contents"] = new JsonArray(new JsonObject
        {
            ["uri"]      = uri,
            ["mimeType"] = "application/json",
            ["text"]     = document,
        }),
    };

    private void Invoke(JsonNode? id, string tool, JsonObject? arguments, JsonNode? progressToken,
                        CancellationToken ct)
    {
        int    refusedCode;
        string document;

        if (tool == HistoryBatch.ToolName)
        {
            // Not a command line, and deliberately not pretended to be one (R-aut-1: the adapter
            // makes no decisions). The batch is session state this process holds; there is no argv
            // that could carry it.
            document = _batch.Invoke(arguments, out refusedCode);
        }
        else
        {
            var argv = ToolCatalog.ToArgv(tool, arguments, _root, out var refusal);

            document = argv is null
                ? RefusalDocument(tool, refusal!, out refusedCode)
                : RunVerb(argv, progressToken, ct, out refusedCode, tool);
        }

        // The document, unchanged (R-aut5-5). The text block is the protocol's own envelope, not a
        // reshaping of the payload: these bytes are the CLI's `--json` bytes.
        var content = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = document,
        });

        if (ToolCatalog.AttachmentAsked(arguments)) Attach(content, document);

        var result = new JsonObject
        {
            ["content"] = content,
            // The CLI's own 0-or-not split (cli.md §7), forwarded. The document carries the truth —
            // including the deliberate difference between "did not converge" and "could not run".
            ["isError"] = refusedCode != 0,
        };

        // AUT-9 R-aut9-12. The document is a JSON document, and putting it inside a JSON string made
        // every client parse twice and every quote and em-dash arrive escaped. Where the protocol
        // has a place for structured content it goes there as well — the text block STAYS, both
        // because the protocol asks for it as the fallback and because the parity gate's whole
        // premise is that `content[0].text` is the CLI's own bytes, unchanged.
        //
        // Parsed rather than re-serialized, for the same reason: nothing here reshapes the payload,
        // and a document this cannot parse simply does not get a structured twin.
        try
        {
            if (JsonNode.Parse(document) is JsonObject structured)
                result["structuredContent"] = structured;
        }
        catch (JsonException) { /* a document that is not an object; the text block still carries it */ }

        _rpc.Result(id, result);
    }

    // ── the picture, back through the protocol ───────────────────────────────

    /// <summary>
    /// Appends the files the run wrote to the result, as protocol content (R-rnd5-4).
    ///
    /// <para><b>This is the point of the whole render series for an agent.</b> A client that receives
    /// only a path has to be able to READ that path, and many cannot — an agent that cannot SEE the
    /// picture it asked for has gained nothing over <c>--json</c>.</para>
    ///
    /// <para><b>The same bytes the verb wrote, read back. Never a second render.</b> R-aut-1: the
    /// adapter calls the verb and does not re-implement it, and that applies to drawing most of all.
    /// What is attached is the file <c>outputs</c> names, opened and base64'd; nothing here decides a
    /// viewport, a page size or a format, and a PDF is never transcoded to a PNG to make it
    /// attachable — that would be the adapter making a rendering decision, which is the one thing
    /// §11.1 says it never does.</para>
    ///
    /// <para><b>The document itself is untouched, always.</b> Everything this method produces is an
    /// ADDITIONAL content block, so the first block stays byte-identical to what
    /// <c>circuitrf render --json</c> writes and the parity gate keeps meaning what it means. A file
    /// too large to attach is reported here, in a block of its own, rather than as a diagnostic
    /// inside a document the CLI would not have written it into.</para>
    /// </summary>
    private static void Attach(JsonArray content, string document)
    {
        JsonNode? parsed;
        try   { parsed = JsonNode.Parse(document); }
        catch { return; }        // a refusal document with no outputs; nothing to attach

        if (parsed?["outputs"] is not JsonArray outputs) return;

        foreach (var entry in outputs)
        {
            if (entry?["path"]?.GetValue<JsonElement>().ValueKind != JsonValueKind.String) continue;
            string path = entry["path"]!.GetValue<string>();
            if (MimeOf(path) is not { } mime) continue;

            long size;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                size = info.Length;
            }
            catch (Exception ex) { Note(content, CliDiagnostics.ServeImageUnreadable(path, ex.Message)); continue; }

            // Opt-in was not enough on its own: an image is expensive in a way a JSON document is
            // not, and base64 adds a third on top. Over the cap the PATH is the answer, with a
            // sentence saying how large it came to and what would narrow it — never truncated, and
            // never dropped in silence, because a client that asked for a picture and got nothing
            // with no explanation simply asks again.
            if (size > ToolCatalog.AttachmentCapBytes)
            {
                Note(content, CliDiagnostics.ServeImageTooLarge(path, size, ToolCatalog.AttachmentCapBytes));
                continue;
            }

            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex) { Note(content, CliDiagnostics.ServeImageUnreadable(path, ex.Message)); continue; }

            string data = Convert.ToBase64String(bytes);

            // An `image` block for an image MIME type, an embedded `resource` for anything else. A
            // PDF is not an image and saying it is would be a lie a client acts on; the alternative —
            // rasterizing it — is the rendering decision this adapter does not make.
            content.Add(mime.StartsWith("image/", StringComparison.Ordinal)
                ? new JsonObject { ["type"] = "image", ["data"] = data, ["mimeType"] = mime }
                : new JsonObject
                {
                    ["type"] = "resource",
                    ["resource"] = new JsonObject
                    {
                        ["uri"]      = new Uri(path).AbsoluteUri,
                        ["mimeType"] = mime,
                        ["blob"]     = data,
                    },
                });
        }
    }

    /// <summary>
    /// The MIME type of a file this adapter will attach, or null for one it will not.
    ///
    /// <para>Deliberately a SHORT list rather than a general extension map: the three formats
    /// <c>render</c> writes and nothing else. A Touchstone or an <c>.npy</c> from an <c>em</c> run
    /// stays a path — those are read by a program, not looked at, and a client that wants one calls
    /// <c>read</c>.</para>
    /// </summary>
    private static string? MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".svg" => "image/svg+xml",
        ".pdf" => "application/pdf",
        _      => null,
    };

    /// <summary>A sentence about the attachment, as its own text block. Its <c>id</c> travels in it,
    /// because the id is the contract and the message is not (cli.md §3.2).</summary>
    private static void Note(JsonArray content, Diagnostic d) => content.Add(new JsonObject
    {
        ["type"] = "text",
        ["text"] = d.Id + ": " + d.Render(),
    });

    /// <summary>
    /// Runs one argument vector and returns the document it wrote.
    ///
    /// <para><b>One runner for both channels.</b> A tool call and a <c>resources/read</c> reach the
    /// same verb through the same process-wide state, so they share this rather than each setting
    /// that state up for itself — which is also what makes the parity gate a property of the code:
    /// the bytes a resource returns came out of the same function a tool call's did (R-aut-13).</para>
    /// </summary>
    private string RunVerb(string[] argv, JsonNode? progressToken, CancellationToken ct,
                           out int exitCode, string tool)
    {
        // Every collector, cleared. Without this a document would carry the previous call's
        // diagnostics and outputs — a stale success a caller cannot tell from a real one.
        JsonRun.Reset();

        var document = new StringWriter();
        JsonRun.Sink = document;

        try
        {
            using var _ = RunHost.Install(ct, progressToken is null ? null : p => Report(progressToken, p));
            exitCode = CliEntry.Run(argv);
        }
        catch (OperationCanceledException)
        {
            // 130, which is the code `em` already returns for a run stopped at a work boundary
            // (cli.md §7). The adapter picks no new number.
            exitCode = Refuse(tool, CliDiagnostics.ServeCancelled(tool), 130);
        }
        catch (Exception ex)
        {
            // A server survives a verb that throws and reports it. The alternative is a connection
            // that simply ends, which tells the client nothing at all.
            exitCode = Refuse(tool, CliDiagnostics.ServeToolFailed(tool, ex.Message));
        }
        finally
        {
            JsonRun.Sink = null;
            JsonRun.Reset();
        }

        return document.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// The adapter's own refusal, as a document. Same shape as everyone else's (R-aut-7): the
    /// sentence on stderr, the diagnostic in the document.
    /// </summary>
    private static string RefusalDocument(string tool, Diagnostic d, out int exitCode)
    {
        JsonRun.Reset();
        var document = new StringWriter();
        JsonRun.Sink = document;
        try     { exitCode = Refuse(tool, d); }
        finally { JsonRun.Sink = null; JsonRun.Reset(); }
        return document.ToString().TrimEnd('\n');
    }

    private static string RefusalDocument(string tool, Diagnostic d)
        => RefusalDocument(tool, d, out _);

    /// <summary>Emits a refusal the way a verb would, into whatever sink is installed.</summary>
    private static int Refuse(string tool, Diagnostic d, int exitCode = 1)
    {
        JsonRun.Verb = tool;
        JsonRun.TakeFlags(["--json"]);
        JsonRun.Report(d);
        return JsonRun.Finish(exitCode);
    }

    private void Report(JsonNode progressToken, RunProgress p)
    {
        var parameters = new JsonObject
        {
            ["progressToken"] = progressToken.DeepClone(),
            ["progress"]      = p.Completed,
            ["message"]       = p.Stage,
        };
        // Total 0 means INDETERMINATE, which RunProgress states explicitly. Sending a zero would
        // read as a finished run rather than an unknown one, so the field is left out instead.
        if (p.Total > 0) parameters["total"] = p.Total;

        _rpc.Notify("notifications/progress", parameters);
    }

    private void Cancel(JsonObject? parameters)
    {
        if (parameters?["requestId"] is not { } requestId) return;
        if (_inFlight.TryGetValue(KeyOf(requestId), out var cts))
            try { cts.Cancel(); } catch { /* already finished */ }
    }

    /// <summary>A JSON-RPC id is a string OR a number, and the two spellings must not collide — the
    /// id <c>1</c> and the id <c>"1"</c> are different requests.</summary>
    private static string KeyOf(JsonNode? id) =>
        id is null ? "" : id.GetValue<JsonElement>().ValueKind + ":" + id.ToJsonString();

    private void WorkerLoop()
    {
        foreach (var item in _work.GetConsumingEnumerable())
        {
            try { item(); }
            catch (Exception ex)
            {
                // Nothing here should throw — Invoke catches — so this is the last resort, and it
                // reports rather than taking the server down with it.
                Console.Error.WriteLine($"serve: {ex.Message}");
            }
        }
    }

    private static string Version()
    {
        var asm = typeof(McpServer).Assembly;
        string? v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? asm.GetName().Version?.ToString();
        if (string.IsNullOrWhiteSpace(v)) return "unknown";
        int plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }
}
