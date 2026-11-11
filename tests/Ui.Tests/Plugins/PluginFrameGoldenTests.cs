using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Design.Frames;
using CircuitRF.Design.Layout.PCells.Wire;
using CircuitRF.Ui.Tests.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests.Plugins;

/// <summary>
/// brief-plg-7 gate 4: the frame does not drift under a compiled plugin. <c>tools/plugin-examples/csharp-window/golden/</c>
/// holds the C# example's own encoding of a fixed set of messages (no payload, a payload with both int64 extremes,
/// non-ASCII JSON) and <c>expected.json</c> saying what each holds. circuitRF's readers and the Python helper's must read
/// each one back exactly, and circuitRF must write the same bytes — so a codec change that would break a plugin already
/// compiled against the frame fails here, without publishing one.
/// </summary>
public class PluginFrameGoldenTests
{
    private static string Golden => Path.Combine(PluginTestKit.ExamplesRoot, "csharp-window", "golden");
    private static string PythonHelper => Path.Combine(Path.GetDirectoryName(PluginTestKit.ExamplesRoot)!, "plugin-python");

    private static (string File, string Json, long[] Payload)[] Expected()
        => JsonNode.Parse(File.ReadAllText(Path.Combine(Golden, "expected.json")))!.AsArray()
            .Select(n => ((string)n!["file"]!, (string)n["json"]!, n["payload"]!.AsArray().Select(v => long.Parse((string)v!)).ToArray()))
            .ToArray();

    [Fact]
    public void EachGoldenFrame_ReadsBackThroughCircuitRFsReaders_AndCircuitRFWritesTheSameBytes()
    {
        var expected = Expected();
        Assert.Equal(3, expected.Length);
        Assert.Contains(expected, e => e.Payload.Length == 0);
        Assert.Contains(expected, e => e.Payload.Contains(long.MinValue) && e.Payload.Contains(long.MaxValue));
        Assert.Contains(expected, e => e.Json.Any(c => c > 127));

        foreach (var (file, json, payload) in expected)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(Golden, file));

            var plugin = WireFrames.Read(new MemoryStream(bytes), "the golden frame");    // what a plugin session reads with
            Assert.Equal(json, plugin.Json);
            Assert.Equal(payload, plugin.Payload.ToArray());

            var pcell = PCellWireProtocol.ReadFrame(new MemoryStream(bytes));             // the PCell wire's reader, the same frame
            Assert.Equal(json, pcell.Json);
            Assert.Equal(payload, pcell.Payload.ToArray());

            var written = new MemoryStream();
            WireFrames.Write(written, new WireFrame(json, payload));
            Assert.Equal(bytes, written.ToArray());
        }
    }

    [PythonFact]
    public void EachGoldenFrame_ReadsBackThroughThePythonHelper()
    {
        string script = """
            import json, sys
            sys.path.insert(0, sys.argv[1])
            from circuitrf_plugin.frame import read_frame
            out = []
            for name in sys.argv[3:]:
                with open(sys.argv[2] + "/" + name, "rb") as f:
                    text, payload = read_frame(f)
                out.append({"json": text, "payload": [str(v) for v in payload]})
            sys.stdout.buffer.write(json.dumps(out, ensure_ascii=False).encode("utf-8"))
            """;
        var expected = Expected();
        var (exit, stdout, stderr) = Python(["-I", "-c", script, PythonHelper, Golden, .. expected.Select(e => e.File)], null);
        Assert.True(exit == 0, stderr);
        var read = JsonNode.Parse(stdout)!.AsArray();
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Json, (string)read[i]!["json"]!);
            Assert.Equal(expected[i].Payload, read[i]!["payload"]!.AsArray().Select(v => long.Parse((string)v!)).ToArray());
        }
    }

    /// <summary>brief-plg-7 gate 1: the helper's own tests (frame round trip, partial reads, cancel, a stray print, float
    /// coordinates refused), run in the routine gate rather than only by hand.</summary>
    [PythonFact]
    public void ThePythonHelpersOwnTests_Pass()
    {
        var (exit, stdout, stderr) = Python(["-I", "-m", "unittest", "discover", "-s", "tests"], PythonHelper);
        Assert.True(exit == 0, stdout + stderr);
        Assert.Contains("OK", stderr);
    }

    private static (int Exit, string StdOut, string StdErr) Python(string[] args, string? workingDirectory)
    {
        var psi = new ProcessStartInfo(PythonRunner.Interpreter!)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "python did not finish within a minute");
        return (p.ExitCode, stdout.Result, stderr.Result);
    }
}
