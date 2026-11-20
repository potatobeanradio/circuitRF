using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Cli;
using CircuitRF.Core;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Workspace;
using Xunit;

namespace CircuitRF.Ui.Tests.Cli;

/// <summary>
/// A headless run loads a referenced kit the first time something asks for one of its parts or devices,
/// and only then (brief-gdf-6 R-gdf6-1) — it used to load every kit the workspace referenced before every
/// run. Counted through <see cref="WorkspaceKits.KitLoaded"/>, never timed.
///
/// <para>One workspace referencing two synthetic kits, plus a third whose folder is gone.</para>
/// </summary>
[Collection(UserStateDirectoryCollection.Name)]
public sealed class KitsLoadedOnDemandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-kitlazy-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly string _ws;
    private readonly List<string> _loads = [];

    public KitsLoadedOnDemandTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "state"));
        CircuitRF.Ui.AppDataRoot.RedirectTo(Path.Combine(_root, "state"));
        PdkKitRegistry.ResetAllForTests();

        WorkspaceCreate.Create(_root, "ws", technologyId: null);
        _ws = Path.GetFullPath(Path.Combine(_root, "ws"));
        foreach (string kit in new[] { "kita", "kitb", "kitc" })
            Assert.Equal(0, Run("import", "pdk", Kit(kit), "--into", _ws).Exit);
        Directory.Delete(Path.Combine(_root, "kitc"), recursive: true);   // referenced, and gone

        PdkKitRegistry.ResetAllForTests();
        WorkspaceKits.Forget(_ws);
        WorkspaceKits.KitLoaded = (root, kit) => { if (root == _ws) lock (_loads) _loads.Add(kit); };
    }

    public void Dispose()
    {
        WorkspaceKits.KitLoaded = null;
        WorkspaceKits.Forget(_ws);
        PdkKitRegistry.ResetAllForTests();
        CircuitRF.Ui.AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>A kit of one part, <c>widget_&lt;kit&gt;</c>: a record-format symbol and its subcircuit.</summary>
    private string Kit(string name)
    {
        string kit = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(kit, "libs.sym"));
        Directory.CreateDirectory(Path.Combine(kit, "libs.spice"));
        File.WriteAllText(Path.Combine(kit, "libs.sym", $"widget_{name}.sym"), $$"""
            v {version=1.2}
            K {type=res
            template="name=X1 model=widget_{{name}} w=1u"}
            L 4 -20 -20 20 20 {}
            B 5 -2.5 -2.5 2.5 2.5 {name=a dir=inout}
            B 5 17.5 -2.5 22.5 2.5 {name=b dir=inout}
            """);
        File.WriteAllText(Path.Combine(kit, "libs.spice", "devices.lib"), $$"""
            .subckt widget_{{name}} a b w=1u
            R1 a b {w*1e6}
            .ends widget_{{name}}
            """);
        return kit;
    }

    private static SchematicEditModel Sheet(string dir, params EditableComponent[] parts)
    {
        var model = new SchematicEditModel { SchematicDirectory = dir };
        foreach (var p in parts) model.Components.Add(p);
        return model;
    }

    private static EditableComponent KitPart(string kit) => new()
    {
        InstanceName = "X1", Symbol = SymbolKind.Generic, CellRef = PdkKitRegistry.RefFor(kit, $"widget_{kit}"),
    };

    private static EditableComponent Resistor() => new() { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 0, Y = 200 };

    private string Save(string name, SchematicEditModel model)
    {
        string path = Path.Combine(model.SchematicDirectory!, name + ".csch");
        SchematicPersistence.SaveToFile(path, model, name);
        return path;
    }

    [Fact]
    public void ADesignUsingNoKit_LoadsNone_AndSaysNothingAboutKits()
    {
        var (exit, err) = Run("netlist", Save("plain", Sheet(_ws, Resistor())), "-o", Path.Combine(_ws, "plain.cnl"));

        Assert.True(exit == 0, err);
        Assert.Empty(_loads);
        Assert.DoesNotContain("kitc", err, StringComparison.Ordinal);
    }

    [Fact]
    public void ADesignWhoseChildCellPlacesAKitPart_LoadsExactlyThatKit()
    {
        var child = CellCreate.Create(_ws, "sub", CellViews.Schematic);
        string childDir = Path.GetDirectoryName(child.SchematicPath!)!;
        Save("sub", Sheet(childDir, KitPart("kita")));

        var top = Sheet(_ws, new EditableComponent
        {
            InstanceName = "XS", Symbol = SymbolKind.Generic,
            CellRef = RefPath.ToStored(ExternalCellRef.MakeCellRef(_ws, child.CellDir)),
        });
        string cnl = Path.Combine(_ws, "top.cnl");
        var (exit, err) = Run("netlist", Save("top", top), "-o", cnl);

        Assert.True(exit == 0, err);
        Assert.Equal(["kita"], _loads);
        Assert.Contains("widget_kita", File.ReadAllText(cnl), StringComparison.Ordinal);   // the kit's part, reached
    }

    [Fact]
    public void ANetlistNamingAKitDevice_LoadsExactlyThatKit()
    {
        string cnl = Path.Combine(_ws, "b.cnl");
        File.WriteAllText(cnl, """
            ExtDevice:X1 n1 0 Provider=kitb Type=widget_kitb
            R:R1 n1 0 R=50 Ohm
            analysis DC1 type=dc
            """);

        Run("dc", cnl);   // whether it solves is not the question; which kit it had to read is

        Assert.Equal(["kitb"], _loads);
    }

    [Fact]
    public void AMissingKitFolder_IsReportedOnlyByARunThatNeedsIt()
    {
        var (_, quiet) = Run("netlist", Save("a", Sheet(_ws, KitPart("kita"))), "-o", Path.Combine(_ws, "a.cnl"));
        Assert.DoesNotContain("kitc", quiet, StringComparison.Ordinal);

        var (_, said) = Run("netlist", Save("c", Sheet(_ws, KitPart("kitc"))), "-o", Path.Combine(_ws, "c.cnl"));
        Assert.Matches(@"(?m)^warning: .*'kitc'.*does not exist", said);
    }

    /// <summary>
    /// <c>lvs</c> reads a kit part on the schematic side, loading its kit as <c>netlist</c> does. The verb installed no
    /// loader, so headlessly the part was read as a cell instance with no schematic, skipped, and the schematic side had no
    /// devices at all ("schematic 0 device(s)").
    /// </summary>
    [Fact]
    public void Lvs_ReadsAKitPart_LoadingItsKit()
    {
        var cell = CellCreate.Create(_ws, "amp", CellViews.Schematic | CellViews.Layout);
        string dir = Path.GetDirectoryName(cell.SchematicPath!)!;
        SchematicPersistence.SaveToFile(cell.SchematicPath!, Sheet(dir, KitPart("kita")), "amp");
        string report = Path.Combine(_root, "amp-lvs.txt");

        var (exit, err) = Run("lvs", cell.CellDir, "-o", report);

        Assert.True(exit is 0 or 1, err);   // the layout is empty, so the part is unmatched: what matters is that it was read
        Assert.Equal(["kita"], _loads);
        Assert.DoesNotContain("has no primary schematic", err + File.ReadAllText(report), StringComparison.Ordinal);
        Assert.Contains("schematic 1 device(s)", File.ReadAllText(report), StringComparison.Ordinal);
    }

    private static (int Exit, string Err) Run(params string[] args)
    {
        var (realOut, realErr) = (Console.Out, Console.Error);
        var err = new StringWriter();
        try
        {
            Console.SetOut(new StringWriter());
            Console.SetError(err);
            JsonRun.Reset();
            return (CliEntry.Run(args), err.ToString());
        }
        finally { Console.SetOut(realOut); Console.SetError(realErr); }
    }
}
