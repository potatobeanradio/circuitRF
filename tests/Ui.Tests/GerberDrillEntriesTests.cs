// Designer round 35 — a board with several drill files. Each file's stackup entry takes its own name, and the technology an
// import of the set mints takes the same set back: every default, as the Layer Mapping dialog offers it, imports.

using CircuitRF.Design.Layout.Interchange;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

public sealed class GerberDrillEntriesTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gerber-drills-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string Header = "%FSLAX46Y46*%\n%MOMM*%\n";

    private static string Copper(string function) =>
        Header + $"%TF.FileFunction,{function}*%\n%ADD10C,0.800*%\nD10*\nX1000000Y1000000D03*\nX3000000Y1000000D03*\nX5000000Y1000000D03*\nM02*\n";

    private static string Drill(double x) => $"M48\nMETRIC\nT1C0.300000\n%\nG90\nG05\nT1\nX{x:0.000000}Y1.000000\nM30\n";

    private static string Route() =>
        "M48\nMETRIC\nT1C2.000000\n%\nG90\nT1\nG00X0.000000Y0.000000\nM15\nG01X8.000000Y0.000000\nM16\nM30\n";

    private IReadOnlyList<string> FourLayerSet()
    {
        string dir = Directory.CreateDirectory(Path.Combine(_root, "set")).FullName;
        File.WriteAllText(Path.Combine(dir, "board-top.gbr"), Copper("Copper,L1,Top,Signal"));
        File.WriteAllText(Path.Combine(dir, "board-in1.gbr"), Copper("Copper,L2,Inr,Plane"));
        File.WriteAllText(Path.Combine(dir, "board-in2.gbr"), Copper("Copper,L3,Inr,Plane"));
        File.WriteAllText(Path.Combine(dir, "board-bot.gbr"), Copper("Copper,L4,Bot,Signal"));
        File.WriteAllText(Path.Combine(dir, "board-a.drl"), Drill(1));
        File.WriteAllText(Path.Combine(dir, "board-b.drl"), Drill(3));
        File.WriteAllText(Path.Combine(dir, "board.rou"), Route());
        return [.. Directory.EnumerateFiles(dir).OrderBy(p => p, StringComparer.Ordinal)];
    }

    private void Say(string title, GerberImport.ImportResult r)
    {
        output.WriteLine($"--- {title}: cancelled={r.Cancelled} refusal={r.Refusal} layers={r.Layers.Count} cell={r.CellDir}");
        foreach (var m in r.Messages.Where(m => !m.Contains("Digit format") && !m.Contains("Zero suppression") && !m.Contains("Units:") && !m.Contains("reconstructed"))) output.WriteLine("  " + m[..Math.Min(400, m.Length)]);
        if (r.Technology is { } t)
        {
            output.WriteLine("  vias: " + string.Join(", ", t.Stackup.Layers.Where(l => l.Kind == StackupKind.Via)
                .Select(l => $"{l.Name} -> {string.Join("/", l.DrawingLayers.Select(k => t.Layers.FirstOrDefault(d => d.Key == k)?.Name ?? k.ToString()))}")));
            foreach (var p in TechValidation.Validate(t)) output.WriteLine("  problem: " + p);
        }
    }

    [Fact]
    public void SeveralDrillFiles_EachTakeTheirOwnEntryName()
    {
        var r = GerberImport.Import(FourLayerSet(), Directory.CreateDirectory(Path.Combine(_root, "a")).FullName, "board", null, 1000);
        Say("new", r);
        var names = r.Technology!.Stackup.Layers.Where(l => l.Kind == StackupKind.Via).Select(l => l.Name).ToList();
        Assert.Equal(3, names.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(TechValidation.Validate(r.Technology), p => p.Contains("share the name", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTechnologyAnImportMinted_TakesTheSameSetBack()
    {
        var files = FourLayerSet();
        var first = GerberImport.Import(files, Directory.CreateDirectory(Path.Combine(_root, "a")).FullName, "board", null, 1000);
        Say("first", first);

        var again = GerberImport.Import(files, Directory.CreateDirectory(Path.Combine(_root, "b")).FullName, "board", null, 1000,
                                        target: ImportTechnologyTarget.Use(first.TechPath!));
        Say("again", again);
        Assert.Null(again.Refusal);
        Assert.False(again.Cancelled);
        Assert.NotNull(again.CellDir);
    }

    /// <summary>The workspace's technology donates its layer names to the technology the import mints.</summary>
    [Fact]
    public void AWorkspaceTechnologyAsDonor_StillGivesEachDrillFileItsOwnEntryName()
    {
        var r = GerberImport.Import(FourLayerSet(), Directory.CreateDirectory(Path.Combine(_root, "d")).FullName, "board",
                                    ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz"), 1000);
        Say("donor", r);
        var names = r.Technology!.Stackup.Layers.Where(l => l.Kind == StackupKind.Via).Select(l => l.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(TechValidation.Validate(r.Technology), p => p.Contains("share the name", StringComparison.Ordinal));
    }

    /// <summary>A donor layer owning the drill extension takes both files: one layer, so one via entry.</summary>
    [Fact]
    public void TwoFilesOnOneDonatedLayer_AreOneViaEntry()
    {
        var donor = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        var drill = donor.Layers.Single(l => l.Name == "Drill");
        drill.Interchange = new InterchangeMapping(null, null, null, "drl", null);
        var r = GerberImport.Import(FourLayerSet(), Directory.CreateDirectory(Path.Combine(_root, "e")).FullName, "board", donor, 1000);
        Say("suffix", r);
        var names = r.Technology!.Stackup.Layers.Where(l => l.Kind == StackupKind.Via).Select(l => l.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(TechValidation.Validate(r.Technology), p => p.Contains("share the name", StringComparison.Ordinal));
    }

    [Fact]
    public void AShippedFourLayerTechnology_TakesTheSet()
    {
        var files = FourLayerSet();
        string ws = Directory.CreateDirectory(Path.Combine(_root, "c")).FullName;
        string tech = CircuitRF.Design.Workspace.WorkspaceCreate.InstallTechnology(ws, "pcb-4layer_FR-4_62mil_1oz");
        var r = GerberImport.Import(files, ws, "board", null, 1000, target: ImportTechnologyTarget.Use(tech));
        Say("shipped", r);
        Assert.Null(r.Refusal);
        Assert.NotNull(r.CellDir);
    }
}
