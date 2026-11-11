// The conversation with circuitRF, on top of Frame.cs: hello and its mode, commit -> placed, host -> hostReply, open, bye.
//
// Every message is one JSON object whose "type" names it. JSON goes through System.Text.Json SOURCE GENERATION
// (PluginJson below), never reflection: a NativeAOT publish trims reflection-based serialisation away, and the failure
// shows only at run time, as an exception on the first message.
//
// Threads: all pipe I/O happens off the UI thread. One background thread reads; Send takes a lock, so the UI's tasks and
// the reader can both write. An answer circuitRF sends to a message of ours (hostReply, placedReply, openReply) completes
// the task that message's sender is awaiting, matched by id.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace LineSectionPlugin;

// ── messages ───────────────────────────────────────────────────────────────────

/// <summary>Every message's first two fields: enough to route it.</summary>
public sealed class Envelope
{
    public string? Type { get; set; }
    public long? Id { get; set; }
}

/// <summary>circuitRF's first message. <c>Mode</c> is <c>interactive</c> (open a window) or <c>headless</c>
/// (<c>circuitRF plugin run</c>: answer <c>update</c> and <c>commit</c>, open nothing).</summary>
public sealed class HelloMessage
{
    public string Type { get; set; } = "";
    public long? Id { get; set; }
    public int ApiVersion { get; set; }
    public string? CircuitRF { get; set; }
    public string? Platform { get; set; }
    public string Staging { get; set; } = "";
    public string? Workspace { get; set; }
    public string? Mode { get; set; }
    public JsonElement? Context { get; set; }
}

/// <summary><c>update</c>, <c>action</c> or <c>commit</c> — the headless form's requests.</summary>
public sealed class RequestMessage
{
    public string Type { get; set; } = "";
    public long Id { get; set; }
    public string? Name { get; set; }
    public JsonElement Params { get; set; }
}

public sealed class Note
{
    public string Severity { get; set; } = "info";
    public string Text { get; set; } = "";
}

public sealed class HelloAnswer
{
    public string Type { get; set; } = "hello";
    public int ApiVersion { get; set; } = 1;
}

public sealed class ReplyMessage
{
    public string Type { get; set; } = "reply";
    public long Id { get; set; }
    public Dictionary<string, double>? Readouts { get; set; }
    public List<Note> Messages { get; set; } = [];
}

public sealed class PlacedCell
{
    public string Name { get; set; } = "";
    public List<string> Files { get; set; } = [];
}

public sealed class PlacedMessage
{
    public string Type { get; set; } = "placed";
    public long Id { get; set; }
    public List<PlacedCell> Cells { get; set; } = [];
    public List<Note>? Messages { get; set; }
}

public sealed class OpenMessage
{
    public string Type { get; set; } = "open";
    public long Id { get; set; }
    public string Path { get; set; } = "";
}

public sealed class ResolveCall
{
    public string Type { get; set; } = "host";
    public long Id { get; set; }
    public string Call { get; set; } = "resolve";
    public string What { get; set; } = "";
    public string? Name { get; set; }
}

public sealed class LineModelCall
{
    public string Type { get; set; } = "host";
    public long Id { get; set; }
    public string Call { get; set; } = "lineModel";
    public string Line { get; set; } = "microstrip";
    public string Substrate { get; set; } = "";
    public double Width { get; set; }
    public double[] Freq { get; set; } = [];
}

/// <summary>circuitRF's answer to one of our messages: <c>hostReply</c>, <c>placedReply</c> or <c>openReply</c>. Each
/// carries <c>error</c> (a sentence) when it failed; the fields a call returns otherwise sit beside it.</summary>
public sealed class Answer
{
    public string? Type { get; set; }
    public long Id { get; set; }
    public string? Error { get; set; }
    public JsonElement? Resolved { get; set; }      // resolve
    public double[]? Z0 { get; set; }               // lineModel
    public double[]? Eeff { get; set; }
    public List<string>? Cells { get; set; }        // placedReply: the names the cells were placed under
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(RequestMessage))]
[JsonSerializable(typeof(HelloAnswer))]
[JsonSerializable(typeof(ReplyMessage))]
[JsonSerializable(typeof(PlacedMessage))]
[JsonSerializable(typeof(OpenMessage))]
[JsonSerializable(typeof(ResolveCall))]
[JsonSerializable(typeof(LineModelCall))]
[JsonSerializable(typeof(Answer))]
public sealed partial class PluginJson : JsonSerializerContext
{
    /// <summary>The context every message is read and written through: camelCase as circuitRF spells it, nulls left out,
    /// and text such as "Ω" written as UTF-8 rather than escaped.</summary>
    public static PluginJson Wire { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}

/// <summary>circuitRF answered a host call with an error; the message is its own sentence.</summary>
public sealed class HostException(string message) : Exception(message);

// ── the conversation ───────────────────────────────────────────────────────────

public sealed class Protocol(Stream input, Stream output)
{
    private readonly object _write = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<Answer>> _awaiting = new();
    private long _ownIds;

    /// <summary>circuitRF's hello, once <see cref="ReadHello"/> has read it.</summary>
    public HelloMessage Hello { get; private set; } = new();

    /// <summary>Raised on the reader thread when circuitRF says <c>bye</c> or closes the pipe: time to exit (within 2 s,
    /// or circuitRF kills the process).</summary>
    public event Action? Ended;

    /// <summary>Raised on the reader thread for <c>update</c>, <c>action</c> and <c>commit</c> — the headless form's
    /// requests. A window session never receives one.</summary>
    public event Action<RequestMessage>? Request;

    /// <summary>Reads circuitRF's hello and answers it with the version this plugin speaks. Null if the pipe closed first.</summary>
    public HelloMessage? ReadHello()
    {
        if (Frames.Read(input) is not { } frame) return null;
        var hello = JsonSerializer.Deserialize(frame.Json, PluginJson.Wire.HelloMessage);
        if (hello is null || hello.Type != "hello") throw new FrameException("circuitRF's first message was not hello.");
        Hello = hello;
        Send(new HelloAnswer(), PluginJson.Wire.HelloAnswer);
        return hello;
    }

    /// <summary>Starts the reader thread. Background, so it never keeps the process alive on its own.</summary>
    public void Listen() => new Thread(ReadLoop) { IsBackground = true, Name = "circuitRF pipe" }.Start();

    public void Send<T>(T message, JsonTypeInfo<T> type) => Send(new Frame(JsonSerializer.Serialize(message, type)));

    public void Send(in Frame frame)
    {
        lock (_write) Frames.Write(output, frame);
    }

    /// <summary>A fresh id for a message this plugin starts (a window plugin's <c>placed</c>, <c>open</c> or <c>host</c>).
    /// circuitRF answers with the same id.</summary>
    public long NextId() => Interlocked.Increment(ref _ownIds);

    /// <summary>Sends a message carrying <paramref name="id"/> and completes with circuitRF's answer to it.</summary>
    public Task<Answer> AskAsync<T>(long id, T message, JsonTypeInfo<T> type)
    {
        var done = new TaskCompletionSource<Answer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _awaiting[id] = done;
        try { Send(message, type); }
        catch (Exception e) { _awaiting.TryRemove(id, out _); done.SetException(e); }
        return done.Task;
    }

    // ── host calls ──────────────────────────────────────────────────────────────

    /// <summary><c>resolve</c>: a technology, substrate or cell as a parameter of that kind delivers it. A technology with
    /// no name is the workspace's default.</summary>
    public async Task<JsonElement> ResolveAsync(long id, string what, string? name = null)
    {
        var answer = await AskAsync(id, new ResolveCall { Id = id, What = what, Name = name }, PluginJson.Wire.ResolveCall);
        if (answer.Error is { } error) throw new HostException(error);
        return answer.Resolved ?? throw new HostException("circuitRF resolved nothing.");
    }

    /// <summary><c>lineModel</c>: circuitRF's microstrip of <paramref name="width"/> metres on <paramref name="substrate"/>
    /// ("conductor : technology"), at one frequency.</summary>
    public async Task<(double Z0, double Eeff)> LineModelAsync(long id, string substrate, double width, double frequency)
    {
        var call = new LineModelCall { Id = id, Substrate = substrate, Width = width, Freq = [frequency] };
        var answer = await AskAsync(id, call, PluginJson.Wire.LineModelCall);
        if (answer.Error is { } error) throw new HostException(error);
        if (answer.Z0 is not [var z0, ..] || answer.Eeff is not [var eeff, ..]) throw new HostException("circuitRF's line model returned no values.");
        return (z0, eeff);
    }

    private void ReadLoop()
    {
        try
        {
            while (Frames.Read(input) is { } frame)
            {
                var envelope = JsonSerializer.Deserialize(frame.Json, PluginJson.Wire.Envelope);
                switch (envelope?.Type)
                {
                    case "bye":
                        Ended?.Invoke();
                        return;
                    case "hostReply" or "placedReply" or "openReply":
                        var answer = JsonSerializer.Deserialize(frame.Json, PluginJson.Wire.Answer)!;
                        if (_awaiting.TryRemove(answer.Id, out var waiting)) waiting.SetResult(answer);
                        break;
                    case "update" or "action" or "commit":
                        Request?.Invoke(JsonSerializer.Deserialize(frame.Json, PluginJson.Wire.RequestMessage)!);
                        break;
                    case "cancel":
                        break;   // every answer here is quick; a plugin may ignore cancel and still be correct
                    default:
                        Console.Error.WriteLine($"Ignoring a {envelope?.Type} message.");
                        break;
                }
            }
        }
        catch (Exception e) when (e is FrameException or IOException or JsonException)
        {
            Console.Error.WriteLine($"The pipe from circuitRF broke: {e.Message}");
        }
        foreach (var waiting in _awaiting.Values) waiting.TrySetException(new IOException("circuitRF closed the pipe."));
        Ended?.Invoke();
    }
}
