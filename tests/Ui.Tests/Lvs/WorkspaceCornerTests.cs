// ================================================================
//  WorkspaceCornerTests.cs
//
//  A part whose value is a kit corner variable: LVS reads the schematic as Simulate does, with the
//  workspace's corner axes and the design's own selections bound, so the value resolves and the
//  part pairs. Place Site Parts reads the same way. A synthetic kit with one corner axis, on a copy
//  of the shipped LVS Attenuator.
// ================================================================

using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Diagnostics;

namespace CircuitRF.Ui.Tests.Lvs;

public sealed class WorkspaceCornerTests : IDisposable
{
    private readonly string _ws = Path.Combine(Path.GetTempPath(), "crf-lvs-corner-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly string _cell;
    private readonly string _csch;

    public WorkspaceCornerTests()
    {
        string example = Path.Combine(RepoRoot(), "examples", "LVS");
        foreach (string file in Directory.EnumerateFiles(example, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(_ws, Path.GetRelativePath(example, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }

        // One corner axis; its nominal section (listed first) binds the value the board is built with.
        Directory.CreateDirectory(Path.Combine(_ws, "kit", "models"));
        File.WriteAllText(Path.Combine(_ws, "kit", "models", "rCorners.lib"), """
            .LIB r_typ
            .param rser = 294
            .ENDL r_typ

            .LIB r_hi
            .param rser = 330
            .ENDL r_hi
            """);
        string cws = Path.Combine(_ws, ".cws");
        var ws = WorkspacePersistence.LoadFromFile(cws);
        ws.PdkRefs = [new CwsPdkRef
        {
            Path = "kit", Provider = "SynthKit",
            Corners = [new CwsCornerAxis { AxisId = "models/rCorners.lib", DisplayName = "rCorners", Options = ["r_typ", "r_hi"] }],
        }];
        WorkspacePersistence.SaveToFile(cws, ws);

        // R1 and R3 (294 Ω) take their value from the corner variable instead.
        _cell = Path.Combine(_ws, "Attenuator");
        _csch = Path.Combine(_cell, "schematic", "Attenuator.csch");
        string text = File.ReadAllText(_csch);
        Assert.Equal(2, text.Split("\"Expression\": \"294\"").Length - 1);
        File.WriteAllText(_csch, text.Replace("\"Expression\": \"294\"", "\"Expression\": \"rser\""));
    }

    public void Dispose() { try { Directory.Delete(_ws, true); } catch { /* best effort */ } }

    /// <summary>The value elaborates through the workspace's nominal corner, and every part pairs with nothing above
    /// info — the shipped board's own result. Without the corners bound it was <c>lvs.schematic.elaboration-failed</c>
    /// and no schematic devices at all.</summary>
    [Fact]
    public void APartValuedByACornerVariable_ElaboratesAndPairs()
    {
        var result = LvsRun.Run(_cell);

        Assert.True(result.IsClean, string.Join("; ",
            result.Diagnostics.Where(d => d.Severity > DiagnosticSeverity.Info).Select(d => d.Render())));
        Assert.Equal(4, result.Comparison.Devices.Count);
        Assert.All(result.Comparison.Devices, p => Assert.Equal(p.SchematicName, p.LayoutName));
        Assert.Equal(294.0, (double)result.Schematic.Devices.Single(d => d.Path == "R1").Parameters["R"]!, 9);
    }

    /// <summary>The design's own corner selection is what binds, as in Simulate — not only the nominal.</summary>
    [Fact]
    public void TheDesignsCornerSelectionIsTheOneBound()
    {
        var (model, _, _) = SchematicPersistence.LoadFromFile(_csch);
        model.CornerSelections[Assert.Single(WorkspaceCorners.ForDocument(_csch)).Key] = "r_hi";

        var netlist = SchematicRead.Read(model, _csch);

        Assert.DoesNotContain(netlist.Notes, n => n.Severity >= DiagnosticSeverity.Warning);
        Assert.Equal(330.0, (double)netlist.Devices.Single(d => d.Path == "R1").Parameters["R"]!, 9);
    }

    /// <summary>Place Site Parts reads the open schematic's sites through the same extraction, so a part valued by a
    /// corner variable elsewhere in it no longer refuses the read.</summary>
    [Fact]
    public void PassiveSitesRead_BindsTheWorkspaceCorners()
    {
        var model = new SchematicEditModel { SchematicDirectory = Path.GetDirectoryName(_csch) };
        var divider = new EditableComponent { InstanceName = "WPD1", Symbol = SymbolKind.Mwpd };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mwpd, 5))
            divider.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, Dimension = dp.Dimension });
        divider.Parameters.Single(q => q.Name == "Realization").Expression = "Lumped";
        model.Components.Add(divider);
        var r = new EditableComponent { InstanceName = "R9", Symbol = SymbolKind.Resistor, X = 5000 };
        r.Parameters.Add(new EditableParameter { Name = "R", Expression = "rser" });
        model.Components.Add(r);

        var passives = PassiveSites.Read(model, out string? refusal);

        Assert.True(refusal is null, refusal);
        Assert.Same(divider, Assert.Single(passives).Component);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
