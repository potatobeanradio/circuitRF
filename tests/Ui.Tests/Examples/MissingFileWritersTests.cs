// ================================================================
//  MissingFileWritersTests.cs
//
//  Simulating a Dividers and Couplers bench before its EM setups had run stopped at "Touchstone file not found", and
//  nothing said that the file is one an EM setup in the same workspace writes. A failed run now names the writer, found by
//  asking each setup where its own run writes rather than by matching names, and the Messages row offers to open it. A
//  Touchstone file nothing in the workspace writes is said to be one, and is attributed to nothing.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Tests.Em3d;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class MissingFileWritersTests : IDisposable
{
    private readonly ITestOutputHelper output;
    private static string Workspace => Path.Combine(PalaceBackendTests.RepoRoot(), "examples", "Dividers and Couplers");
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-writers-" + Guid.NewGuid().ToString("N")[..8]);

    public MissingFileWritersTests(ITestOutputHelper output)
    {
        this.output = output;
        foreach (var file in Directory.GetFiles(Workspace, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(Workspace, file);
            if (rel.StartsWith("pictures", StringComparison.Ordinal) || rel.StartsWith("results", StringComparison.Ordinal)) continue;
            string to = Path.Combine(_ws, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_ws, recursive: true); } catch (IOException) { }
    }

    private string Results(string name) => Path.Combine(_ws, "results", name);

    [Fact]
    public void ABenchRunBeforeItsEmSetup_NamesTheSetup_AndItsRowOpensIt()
    {
        var (model, _, _) = SchematicPersistence.LoadFromFile(Path.Combine(_ws, "Bagley Bench", "schematic", "Bagley Bench.csch"));
        string netlist = Path.Combine(_ws, "netlist.cnl");
        File.WriteAllText(netlist, SchematicCircuit.CnlTextOf(model, "Bagley Bench"));
        var run = SchematicRunService.RunNetlist(netlist, _ws);
        output.WriteLine(run.StatusMessage);

        string cem = Path.Combine(_ws, "Bagley", "em", "Bagley Planar.cem");
        Assert.Contains("Bagley Planar.s4p is written by Bagley/em/Bagley Planar.cem: run that first.", run.StatusMessage);
        var writer = Assert.Single(run.FileWriters);
        Assert.Equal(cem, writer.DocumentPath);

        // Every analysis failed, so the errors are on the run's own row: the writer gets a row of its own, with Open.
        var sink = new ActionSink();
        string? opened = null;
        RunMessages.Post(sink, run, () => null, path => { opened = path; return true; });
        var row = Assert.Single(sink.Actions);
        Assert.Equal("Open", row.Label);
        Assert.Equal(cem, row.FilePath);
        row.Action().GetAwaiter().GetResult();
        Assert.Equal(cem, opened);
    }

    [Fact]
    public void A3dViewsSetup_IsNamedWithItsSolver()
    {
        var writer = MissingFileWriters.Explain(Results("Wilkinson EMerge.emerge.s4p"), _ws);
        output.WriteLine(writer?.Sentence);
        Assert.Equal("Wilkinson EMerge.emerge.s4p is written by the EMerge run of setup 'EMerge' in Wilkinson/3d/Wilkinson.c3d: " +
                     "run that first.", writer!.Sentence);
        Assert.Equal(Path.Combine(_ws, "Wilkinson", "3d", "Wilkinson.c3d"), writer.DocumentPath);
    }

    [Fact]
    public void AResultWithAnotherPortCount_IsNamed_RatherThanAskingForTheSameRunAgain()
    {
        Directory.CreateDirectory(Path.Combine(_ws, "results"));
        File.WriteAllText(Results("Bagley Planar.s3p"), "# GHz S MA R 50\n1 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");

        var writer = MissingFileWriters.Explain(Results("Bagley Planar.s4p"), _ws);
        Assert.Equal("Bagley/em/Bagley Planar.cem wrote Bagley Planar.s3p, a 3-port result, and this block reads " +
                     "Bagley Planar.s4p, a 4-port one.", writer!.Sentence);
        Assert.Equal(Path.Combine(_ws, "Bagley", "em", "Bagley Planar.cem"), writer.DocumentPath);
    }

    [Fact]
    public void AFileNothingWrites_IsSaidToBeOne_EvenWhenItsNameLooksLikeAResult()
    {
        foreach (string missing in new[] { Path.Combine(_ws, "parts", "vendor part.s2p"), Results("Bagley Planar copy.s4p") })
        {
            var writer = MissingFileWriters.Explain(missing, _ws);
            Assert.Equal(MissingFileWriters.NoWriter, writer!.Sentence);
            Assert.Null(writer.DocumentPath);
        }
        // Only a Touchstone file is said to have no writer: the scan knows nothing else that writes files.
        Assert.Null(MissingFileWriters.Explain(Path.Combine(_ws, "models", "device.cmodel"), _ws));
    }

    private sealed class ActionSink : CircuitRF.Ui.Messages.IMessageSink
    {
        public List<(string Label, Func<Task> Action, string? FilePath)> Actions { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) { }
        public void PostAction(MessageLevel level, string text, string actionLabel, Func<Task> action, string? filePath = null)
            => Actions.Add((actionLabel, action, filePath));
        public void Clear() { }
    }
}
