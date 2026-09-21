// ================================================================
//  FootprintCliTests.cs — brief-footprint-4-picker-and-import.md §5 gates 9 and 10.
//
//  `check` and `explain` are read-only and neither runs an analysis. Both gain one thing each, and
//  neither writes a rule of its own: every finding comes from FootprintCatalog, which is the same
//  resolution SchematicToLayoutGenerator performs. A rule living only in the CLI is a rule the
//  application does not enforce — a design would pass headlessly and be refused when somebody
//  opened it.
//
//  Run as a PROCESS, like every other CLI gate here, because the exit code is half of what is being
//  asserted: an unresolvable footprint is a WARNING, and a warning is always reported and still
//  exits 0. A check that hid it to keep the exit code clean makes the exit code useless; one that
//  failed on it would stop a build machine over artwork nothing in the run touches.
// ================================================================

using System.Diagnostics;
using System.Text.Json;
using CircuitRF.Design.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Cli;

public sealed class FootprintCliTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-fp4cli-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ══ 9. `check` warns and exits 0 ════════════════════════════════════════

    /// <summary>
    /// R-fp4-4a. A design with an unresolvable footprint AND one whose pad count disagrees with its
    /// component's port count: both named, both warnings, exit code 0.
    /// </summary>
    [Fact]
    public void Check_WarnsOnAnUnresolvableFootprintAndAPadCountMismatchAndStillExitsZero()
    {
        string csch = WriteSchematic(
            ("C1",   SymbolKind.Capacitor, "smt:0402@N", 0),   // fine
            ("C2",   SymbolKind.Capacitor, "smt:9999",   0),   // claims a built-in and is not one
            ("S4P1", SymbolKind.Snp,       "smt:0402@N", 4));  // two pads under four ports

        var run = RunCli("check", csch);

        // The exit code is the assertion, not an incidental: warnings are reported and exit 0.
        Assert.Equal(0, run.ExitCode);

        string all = run.StdOut + run.StdErr;
        Assert.Contains("C2", all, StringComparison.Ordinal);
        Assert.Contains("smt:9999", all, StringComparison.Ordinal);
        Assert.Contains("S4P1", all, StringComparison.Ordinal);
        Assert.Contains("2 pad", all, StringComparison.Ordinal);
        Assert.Contains("4 port", all, StringComparison.Ordinal);

        // And the one that resolves is not reported at all.
        Assert.DoesNotContain("'C1'", all, StringComparison.Ordinal);
    }

    // ══ 10. `explain --footprints` reports the walk ═════════════════════════

    /// <summary>
    /// R-fp4-4b. Per component: the stored value, what it resolved to, the pads against the ports,
    /// the technology a built-in would be generated against — and the WALK, which is the half a
    /// caller cannot otherwise see.
    /// </summary>
    [Fact]
    public void Explain_Footprints_NamesTheStoredValueTheResolvedArtworkAndTheTechnology()
    {
        string csch = WriteSchematic(
            ("C1",   SymbolKind.Capacitor, "smt:0402@N", 0),
            ("S4P1", SymbolKind.Snp,       "smt:0402@N", 4));
        var tech = ShippedTechnologies.Load("pcb-2layer_FR-4_70mil_1oz");

        var run = RunCli("explain", csch, "--footprints", "--json");
        Assert.Equal(0, run.ExitCode);

        using var doc = JsonDocument.Parse(run.StdOut);
        var rows = doc.RootElement.GetProperty("result").GetProperty("explain").GetProperty("footprints");
        Assert.Equal(2, rows.GetArrayLength());

        var c1 = rows[0];
        Assert.Equal("C1",         c1.GetProperty("component").GetString());
        Assert.Equal("smt:0402@N", c1.GetProperty("stated").GetString());
        Assert.Equal("builtin",    c1.GetProperty("state").GetString());
        Assert.Equal(2,            c1.GetProperty("pads").GetInt32());
        Assert.Equal(2,            c1.GetProperty("ports").GetInt32());

        // The WALK, not only the answer (R-aut4-7's format): resolution here is chosen by the first
        // four characters of the stored value, and which branch fired is what a caller is asking.
        Assert.Contains("smt:", c1.GetProperty("walk").GetString()!, StringComparison.Ordinal);

        // The metric twin and the millimetres, on every mention — the overview's §1e spelling.
        Assert.Contains("metric 1005", c1.GetProperty("resolvedTo").GetString()!, StringComparison.Ordinal);

        // The technology a built-in would be GENERATED against. Reported for a built-in and only
        // for a built-in: the shipped technologies disagree about every layer key, so which one is
        // in force is part of what the artwork will be.
        Assert.Equal(tech.Name, c1.GetProperty("technology").GetString());

        // The mismatch is visible in the report rather than only at Update Layout.
        Assert.Equal(2, rows[1].GetProperty("pads").GetInt32());
        Assert.Equal(4, rows[1].GetProperty("ports").GetInt32());
    }

    // ── fixtures ────────────────────────────────────────────────────────────

    /// <param name="parts">Instance name, symbol kind, the stored <c>Footprint</c>, and — for an
    /// <c>SnP</c> — its port count.</param>
    private string WriteSchematic(params (string Name, SymbolKind Kind, string Footprint, int Ports)[] parts)
    {
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        var tech = ShippedTechnologies.Load("pcb-2layer_FR-4_70mil_1oz");
        TechPersistence.SaveToFile(Path.Combine(_root, "tech", "t.ctech"), tech);
        WorkspacePersistence.SaveToFile(
            Path.Combine(_root, ".cws"), new CwsFile { DefaultTechRef = "tech/t.ctech" });

        string cellDir = CellFolder.CreateCellFolder(_root, "Board");
        string schematicDir = CellFolder.SubFolderPath(cellDir, ViewType.Schematic);

        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        foreach (var (name, kind, footprint, ports) in parts)
        {
            var c = new EditableComponent { InstanceName = name, Symbol = kind };
            if (kind == SymbolKind.Snp)
                c.Parameters.Add(new EditableParameter { Name = "NumPorts", Expression = ports.ToString() });
            c.Parameters.Add(new EditableParameter { Name = "Footprint", Expression = footprint });
            model.Components.Add(c);
        }

        string csch = Path.Combine(schematicDir, "Board.csch");
        SchematicPersistence.SaveToFile(csch, model, "Board");
        return csch;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(FootprintCliTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}
