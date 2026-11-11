// The entry point, and the plugin's headless form.
//
// circuitRF starts this program with pipes on stdin and stdout and sends hello. hello's mode says which form to be:
//   interactive  open the window (MainWindow.cs) and talk to circuitRF whenever its user asks;
//   headless     `circuitRF plugin run`: no window — answer update (readouts) and commit (placed) from the manifest's
//                params, the way a panel plugin does, then exit on bye.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace LineSectionPlugin;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // stdout is the pipe to circuitRF and carries frames ONLY. Keep it, then point Console.Out at stderr (circuitRF's
        // log of this plugin) before anything else runs — one stray Console.WriteLine would otherwise corrupt a frame.
        var wire = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);

        if (args is ["--write-golden-frames", var dir]) return GoldenFrames.Write(dir);

        var protocol = new Protocol(Console.OpenStandardInput(), wire);
        HelloMessage? hello;
        try { hello = protocol.ReadHello(); }
        catch (Exception e) when (e is FrameException or IOException or JsonException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        if (hello is null) return 0;   // circuitRF closed the pipe before saying hello
        return hello.Mode == "headless" ? Headless.Run(protocol) : MainWindow.Run(protocol, args);
    }
}

/// <summary><c>circuitRF plugin run</c>: requests answered one at a time on this thread, while the reader thread
/// delivers circuitRF's hostReply to each host call they make.</summary>
public static class Headless
{
    public static int Run(Protocol protocol)
    {
        var requests = new BlockingCollection<RequestMessage>();
        protocol.Request += requests.Add;
        protocol.Ended += () => { try { requests.CompleteAdding(); } catch (ObjectDisposedException) { } };
        protocol.Listen();
        foreach (var request in requests.GetConsumingEnumerable())
            Answer(protocol, request).GetAwaiter().GetResult();
        return 0;
    }

    private static async Task Answer(Protocol protocol, RequestMessage request)
    {
        try
        {
            var (spec, technology) = Read(request.Params);
            var problem = LineSection.Problem(spec);
            if (problem is not null) throw new HostException(problem);

            // Host calls made while answering a request carry that request's id.
            var tech = Technology.From(await protocol.ResolveAsync(request.Id, "technology", technology));
            string substrate = $"{spec.Conductor} : {tech.Path}";
            var design = await LineSection.DesignAsync(spec, tech,
                w => protocol.LineModelAsync(request.Id, substrate, w, spec.FrequencyHz));

            if (request.Type == "commit")
            {
                var workspaceDefault = Technology.From(await protocol.ResolveAsync(request.Id, "technology"));
                var staged = LineSection.Documents(spec, tech, design, TechRef(protocol.Hello.Workspace, tech, workspaceDefault));
                var cell = LineSection.Stage(staged, protocol.Hello.Staging);
                protocol.Send(new PlacedMessage { Id = request.Id, Cells = [cell] }, PluginJson.Wire.PlacedMessage);
                return;
            }
            var readouts = new Dictionary<string, double>
            {
                ["width"] = design.Width, ["length"] = design.Length, ["eeff"] = design.Eeff, ["modelZ0"] = design.ModelZ0,
            };
            protocol.Send(new ReplyMessage { Id = request.Id, Readouts = readouts }, PluginJson.Wire.ReplyMessage);
        }
        catch (Exception e) when (e is HostException or InvalidOperationException or KeyNotFoundException or IOException)
        {
            var note = new Note { Severity = "error", Text = e.Message };
            if (request.Type == "commit")
                protocol.Send(new PlacedMessage { Id = request.Id, Messages = [note] }, PluginJson.Wire.PlacedMessage);
            else
                protocol.Send(new ReplyMessage { Id = request.Id, Messages = [note] }, PluginJson.Wire.ReplyMessage);
        }
    }

    /// <summary>The manifest's params as circuitRF delivers them: quantities in base SI, the substrate resolved.</summary>
    private static (LineSpec Spec, string Technology) Read(JsonElement p)
    {
        double Quantity(string name) => p.GetProperty(name).GetProperty("value").GetDouble();
        var substrate = p.GetProperty("substrate");
        var spec = new LineSpec(Quantity("f0"), Quantity("z0"), p.GetProperty("degrees").GetDouble(),
                                substrate.GetProperty("signal").GetString() ?? "");
        return (spec, substrate.GetProperty("technology").GetString()!);
    }

    /// <summary>The technology as the placed layout names it: nothing when it is the workspace's default, otherwise its
    /// path from <c>&lt;workspace&gt;/&lt;cell&gt;/layout/</c> — the same from any cell, since every cell is one level down.</summary>
    public static string? TechRef(string? workspace, Technology tech, Technology workspaceDefault)
    {
        if (workspace is null || string.Equals(Path.GetFullPath(tech.Path), Path.GetFullPath(workspaceDefault.Path), StringComparison.OrdinalIgnoreCase))
            return null;
        return Path.GetRelativePath(Path.Combine(workspace, LineSection.CellName, "layout"), tech.Path).Replace('\\', '/');
    }
}

/// <summary>
/// Writes this program's encoding of a fixed set of messages, one <c>.bin</c> each, plus <c>expected.json</c> saying what
/// each holds. They are committed beside this file; circuitRF's tests decode them with its own frame reader and with the
/// Python helper's, so a change on either side that would break a compiled plugin fails there without building one.
/// </summary>
public static class GoldenFrames
{
    public static int Write(string dir)
    {
        Directory.CreateDirectory(dir);
        var frames = new (string Name, Frame Frame)[]
        {
            ("hello", new Frame(JsonSerializer.Serialize(new HelloAnswer(), PluginJson.Wire.HelloAnswer))),
            ("payload", new Frame(JsonSerializer.Serialize(
                new ReplyMessage { Id = 7, Readouts = new() { ["width"] = 0.001668 } }, PluginJson.Wire.ReplyMessage),
                [0, -834, 16_217_594, 834, long.MinValue, long.MaxValue, -1])),
            ("non-ascii", new Frame(JsonSerializer.Serialize(
                new ReplyMessage { Id = 8, Messages = [new Note { Text = "Z0 = 50 Ω, εeff ≈ 2.85, 1.668 µm — ±0.1 °" }] },
                PluginJson.Wire.ReplyMessage))),
        };
        using var expected = new MemoryStream();
        using (var w = new Utf8JsonWriter(expected, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartArray();
            foreach (var (name, frame) in frames)
            {
                using (var file = File.Create(Path.Combine(dir, name + ".bin"))) Frames.Write(file, frame);
                w.WriteStartObject();
                w.WriteString("file", name + ".bin");
                w.WriteString("json", frame.Json);
                w.WriteStartArray("payload");
                foreach (long v in frame.Payload) w.WriteStringValue(v.ToString(CultureInfo.InvariantCulture));
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        File.WriteAllBytes(Path.Combine(dir, "expected.json"), [.. expected.ToArray(), (byte)'\n']);
        return 0;
    }
}
