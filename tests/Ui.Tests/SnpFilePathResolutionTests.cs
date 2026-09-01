using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// One base for a stored SnP path, and it is the WORKSPACE ROOT.
///
/// <para><b>The bug this pins.</b> <see cref="SnpPathPolicy.ToStored"/> writes a picked path
/// relative to the workspace root — that is what makes a design portable — and
/// <c>Elaborator.ResolveSnpFilePath</c> resolves it against the same root at Run
/// (<c>WorkspaceViewModel</c> hands it <c>CurrentWorkspaceRoot</c>). <see cref="SetSnpFileCommand"/>
/// resolved it against the SCHEMATIC's own directory instead. The two agree only when the schematic
/// sits at the workspace root, which is the usual layout and is why nothing reported it; for a
/// schematic in a sub-folder the port-count sniff silently missed the file and left
/// <c>NumPorts</c> at its previous value — so the symbol drew the wrong number of pins and the
/// netlist bound the wrong number of nets, for a file that was perfectly readable.</para>
///
/// <para>It was already known: <c>EmBackAnnotation</c>'s <c>SetSnpReferenceCommand</c> documents it
/// as the reason it does not reuse this command. That workaround stands on its own merits (the EM
/// kernel knows the port count exactly and has no reason to sniff), but it is not the fix.</para>
/// </summary>
public class SnpFilePathResolutionTests : IDisposable
{
    private readonly string _root;

    public SnpFilePathResolutionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-snppath-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "ws.cws"), "{}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>A three-port Touchstone with one frequency point — enough for the sniff.</summary>
    private string WriteS3P(string relative)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "# GHZ S RI R 50\n" +
            "1.0 " + string.Join(" ", Enumerable.Repeat("0.1 0.0", 9)) + "\n");
        return path;
    }

    /// <summary>A schematic two folders down, which is where a cell's schematic actually lives.</summary>
    private string DeepSchematicDir()
    {
        string dir = Path.Combine(_root, "cells", "Amp", "schematic");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static EditableComponent Snp(string file, int numPorts)
    {
        // PortCount is derived from the NumPorts parameter, which is exactly what this command sets.
        var c = new EditableComponent { InstanceName = "S1", Symbol = SymbolKind.Snp };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Snp, numPorts))
            c.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression });
        c.Parameters.First(p => p.Name == "File").Expression = file;
        return c;
    }

    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AWorkspaceRelativePath_SniffsThePortCount_FromASchematicInASubFolder()
    {
        WriteS3P("data/dut.s3p");

        var model = new SchematicEditModel { SchematicDirectory = DeepSchematicDir() };
        var comp = Snp("", numPorts: 2);
        model.Components.Add(comp);

        new SetSnpFileCommand(model, comp, "data/dut.s3p", _root).Execute();

        Assert.Equal("data/dut.s3p", comp.Parameters.First(p => p.Name == "File").Expression);
        Assert.Equal("3", comp.Parameters.First(p => p.Name == "NumPorts").Expression);
    }

    [Fact]
    public void ThePolicyRoundTrips_StoreThenResolve_ForASchematicInASubFolder()
    {
        string absolute = WriteS3P("data/dut.s3p");

        // The two halves of one contract: what ToStored writes must be what Resolve reads back.
        string stored = SnpPathPolicy.ToStored(absolute, _root);
        Assert.Equal("data/dut.s3p", stored);

        Assert.Equal(Path.GetFullPath(absolute),
                     SnpPathPolicy.Resolve(stored, _root, DeepSchematicDir()));
    }

    [Fact]
    public void AnAbsolutePath_IsUnchangedByEitherBase()
    {
        string absolute = WriteS3P("data/dut.s3p");
        Assert.Equal(Path.GetFullPath(absolute), SnpPathPolicy.Resolve(absolute, _root, DeepSchematicDir()));
        Assert.Equal(Path.GetFullPath(absolute), SnpPathPolicy.Resolve(absolute, null, null));
    }

    [Fact]
    public void AWindowsAuthoredSeparator_ResolvesOnEveryPlatform()
    {
        WriteS3P("data/dut.s3p");

        // The elaborator tolerates a '\' in a stored relative path so a design ports across
        // operating systems; the editor has to read the same string the same way or it reports a
        // missing file for one that is there.
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "data", "dut.s3p")),
                     SnpPathPolicy.Resolve(@"data\dut.s3p", _root, null));
    }

    [Fact]
    public void WithNoWorkspaceRoot_TheSchematicsOwnDirectoryIsTheBase()
    {
        // A loose schematic has no workspace to be relative to. ToStored never produces a relative
        // path in that case (it keeps the absolute one), so this only arises for a hand-typed value
        // — and beside the file being edited is the only thing such a value can reasonably mean.
        string dir = DeepSchematicDir();
        File.WriteAllText(Path.Combine(dir, "local.s2p"), "! placeholder\n");

        Assert.Equal(Path.GetFullPath(Path.Combine(dir, "local.s2p")),
                     SnpPathPolicy.Resolve("local.s2p", null, dir));
    }

    [Fact]
    public void WithNeitherBase_ARelativePathDoesNotResolve_RatherThanResolvingAgainstTheProcess()
    {
        // Never the current working directory: that is wherever circuitRF happened to be launched
        // from, which is not a place any design meant.
        Assert.Null(SnpPathPolicy.Resolve("data/dut.s3p", null, null));
        Assert.Null(SnpPathPolicy.Resolve("   ", _root, null));
    }

    [Fact]
    public void AMissingFile_LeavesTheStoredPathAlone_AndSaysNothingAboutPortCount()
    {
        var model = new SchematicEditModel { SchematicDirectory = DeepSchematicDir() };
        var comp = Snp("data/dut.s3p", numPorts: 2);   // the file was never written
        model.Components.Add(comp);

        new SetSnpFileCommand(model, comp, "data/gone.s3p", _root).Execute();

        Assert.Equal("data/gone.s3p", comp.Parameters.First(p => p.Name == "File").Expression);
        Assert.Equal("2", comp.Parameters.First(p => p.Name == "NumPorts").Expression);
    }

    [Fact]
    public void Undo_PutsBackBothTheFileAndThePortCount()
    {
        WriteS3P("data/dut.s3p");

        var model = new SchematicEditModel { SchematicDirectory = DeepSchematicDir() };
        var comp = Snp("old.s2p", numPorts: 2);
        model.Components.Add(comp);

        var cmd = new SetSnpFileCommand(model, comp, "data/dut.s3p", _root);
        cmd.Execute();
        Assert.Equal("3", comp.Parameters.First(p => p.Name == "NumPorts").Expression);

        cmd.Undo();
        Assert.Equal("old.s2p", comp.Parameters.First(p => p.Name == "File").Expression);
        Assert.Equal("2", comp.Parameters.First(p => p.Name == "NumPorts").Expression);
    }
}
