// brief-artsch-9-acceptance-docs-example.md R-as9-3 — the field boards as a theory, one case per board present under
// the git-ignored testdata/artwork-boards/ (FieldBoards): each recognises with no refusal, its .cnl passes `check`
// with no error, and its counts fall in its expected.json's ranges. The schema is in testdata/artwork-boards/README.md;
// a part or port count is exact, a line type's [min, max]. On a fresh clone the theory skips with the reason.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Cli;
using CircuitRF.Design.Layout.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Recognition;

[Collection(CircuitRF.Ui.Tests.Lvs.LvsCliConsoleCollection.Name)]
public sealed class ArtworkFieldBoardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-as9-field-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    public static TheoryData<string> Boards()
    {
        var data = new TheoryData<string>();
        foreach (string dir in FieldBoards.Dirs()) data.Add(Path.GetFileName(dir));
        return data;
    }

    [FixtureTheory(FieldBoards.Gate, FieldBoards.Reason)]
    [MemberData(nameof(Boards))]
    public void TheBoardIsRecognised_ChecksClean_AndCountsWithinItsRanges(string board)
    {
        string dir = Path.Combine(FixturePaths.Require(FieldBoards.Folder), board);
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "expected.json")));
        var root = expected.RootElement;
        string clay = Path.Combine(dir, root.GetProperty("clay").GetString()!);

        var (result, circuit) = ArtworkRecognition.Circuit(RecognitionInput.FromFile(clay), new RecognitionEmitOptions());
        Assert.True(result.Ok && circuit is not null, $"{board}: {result.Refusal}");
        RecognitionBoards.CopperAgrees(result, board);

        // The .cnl is written outside the board's folder, which is the user's: its references are relative to where it lands.
        Directory.CreateDirectory(_root);
        string cnl = Path.Combine(_root, board + ".cnl");
        File.WriteAllText(cnl, circuit!.CnlText(_root));
        JsonRun.Reset();
        Assert.True(CliEntry.Run(["check", cnl]) == 0, $"{board}: check reported an error in {cnl}");

        var twoPad = result.Parts.Rows.Where(r => r.PadCount == 2).ToList();
        InRange(root, "ports", result.Board!.Ports.Count, board);
        InRange(root, "parts", twoPad.Count, board);
        InRange(root, "series", twoPad.Count(r => r.Connection == PartConnection.Series), board);
        InRange(root, "shunt", twoPad.Count(r => r.Connection == PartConnection.Shunt), board);
        if (root.TryGetProperty("lines", out var lines))
            foreach (var type in lines.EnumerateObject())
                InRange(lines, type.Name, result.Lines.Elements.Count(e => e.Type.ToString() == type.Name), board);

        // Parts the copper keeps apart — a line runs between them — share no signal node (designer report, round 15:
        // two beads on one supply rail came out on one node, drawn side by side).
        if (root.TryGetProperty("apart", out var apart))
            foreach (var pair in apart.EnumerateArray())
            {
                string a = pair[0].GetString()!, b = pair[1].GetString()!;
                string[] Nets(string name) => [.. circuit.TestBench.Instances.Single(i => i.InstanceName == name).NetBindings.Where(n => n != "0")];
                Assert.True(!Nets(a).Intersect(Nets(b)).Any(), $"{board}: {a} and {b} share a node");
            }
    }

    /// <summary>A count stated as a number is exact; as [min, max], a range. A key the file does not state is not checked.</summary>
    private static void InRange(JsonElement root, string key, int count, string board)
    {
        if (!root.TryGetProperty(key, out var stated)) return;
        var (lo, hi) = stated.ValueKind == JsonValueKind.Array
            ? (stated[0].GetInt32(), stated[1].GetInt32())
            : (stated.GetInt32(), stated.GetInt32());
        Assert.True(count >= lo && count <= hi, $"{board}: {key} {count}, expected {(lo == hi ? $"{lo}" : $"{lo}–{hi}")}");
    }
}
