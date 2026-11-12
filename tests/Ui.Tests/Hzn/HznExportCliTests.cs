using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// HZN-4 R-hzn4-4 — <c>convert x.c3d -o y.hzn</c> and <c>--into</c>, run as a PROCESS and compared byte for byte with the in-process call
/// they forward to; and the gate against an installed Hertzon (series gate 2), Skipped without one.
/// </summary>
public sealed class HznExportCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-hzn4cli-" + Guid.NewGuid().ToString("N")[..10]);

    public HznExportCliTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A workspace with a technology and a cell "Pkg" whose 3D view is a dielectric, a conductor and a lumped port between two sheets.</summary>
    private (string Cws, string C3d) Package()
    {
        string ws = Path.Combine(_root, "ws");
        Directory.CreateDirectory(ws);
        var tech = new Technology { Name = "t" };
        tech.Materials.Add(new TechMaterial { Name = "FR4", Epsr = 4.4, TanD = 0.02, Mur = 1 });
        tech.Materials.Add(new TechMaterial { Name = "pec", Epsr = 1, TanD = 0, Mur = 1 });
        TechPersistence.SaveToFile(Path.Combine(ws, "tech.ctech"), tech);
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });
        string dir = CellFolder.CreateCellFolder(ws, "Pkg");
        string c3d = CellCreate.WriteThreeDView(dir, "Pkg", new C3dDocument
        {
            DisplayUnit = LayoutUnit.Mm,
            Variables = [new C3dVariable { Name = "h", Expression = "1.5", Unit = "Mm" }],
            Objects =
            [
                new C3dBox { Name = "sub", Material = "FR4", Size = new(10000000, 10000000, 1500000) },
                new C3dSheet { Name = "gnd", Material = "pec", Role = Em3dRole.Conductor, Rect = new C3dRect { Min = new(0, 0), Size = new(10000000, 10000000) } },
            ],
            Ports = [new C3dPort { Number = 1, Name = "P1", Plane = C3dPlane.XZ, Offset = 0, Z0 = "50", Rect = new C3dRect { Min = new(4000000, 0), Size = new(2000000, 1500000) } }],
        });
        return (Path.Combine(ws, ".cws"), c3d);
    }

    [Fact]
    public void Convert_AsAProcess_WritesTheBytesTheInProcessCallWrites_AndAdds_AndRefusesAName_NamingTheFlag()
    {
        var (cws, c3d) = Package();
        string a = Path.Combine(_root, "a", "out.hzn"), b = Path.Combine(_root, "b", "out.hzn");

        var run = RunCli("convert", c3d, "-o", a);
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        Assert.True(HznExport.ToNewFile(HznExport.FromFile(c3d, null, cws), b).Ok);
        Assert.True(File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b)), "the verb must write what the call writes");
        var project = HznFile.Read(a).File!.Project;
        Assert.Equal("Pkg", Assert.Single(project.Designs).Name);       // named after the cell, as the call names it
        Assert.Equal("out", project.Name);                                // the project is named for its file

        // --into adds a design, and a second one of the same name is refused naming --design-name.
        string into1 = Path.Combine(_root, "i1.hzn"), into2 = Path.Combine(_root, "i2.hzn");
        File.Copy(a, into1);
        File.Copy(a, into2);
        var taken = RunCli("convert", c3d, "--into", into1);
        Assert.Equal(1, taken.ExitCode);
        Assert.Contains("--design-name", taken.StdErr);
        var added = RunCli("convert", c3d, "--into", into1, "--design-name", "Extra");
        Assert.True(added.ExitCode == 0, added.StdErr + added.StdOut);
        Assert.True(HznExport.IntoExisting(HznExport.FromFile(c3d, "Extra", cws), into2).Ok);
        Assert.True(File.ReadAllBytes(into1).AsSpan().SequenceEqual(File.ReadAllBytes(into2)));
        Assert.Equal(["Pkg", "Extra"], HznFile.Read(into1).File!.Project.Designs.Select(d => d.Name));
    }

    [Fact]
    public void TheVerb_HoldsNoExportLogic()
    {
        string code = Regex.Replace(Regex.Replace(File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cli", "HznExportVerb.cs")), @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
        Assert.Contains("HznExport.FromFile", code);
        foreach (string forbidden in new[] { "HznJson", "CreateBox", "Subtract", "C3dToHzn", "consumed_by", "\"materials\"" })
            Assert.DoesNotContain(forbidden, code);
    }

    // ── series gate 2: Hertzon reads what we write ───────────────────────────────────────────────

    /// <summary>The installed <c>hertzon-cli</c> under <c>CIRCUITRF_HERTZON_HOME</c>, or null.</summary>
    private static string? HertzonCli()
    {
        string? home = HertzonHomeFactAttribute.Home;
        if (home is null) return null;
        foreach (string rel in new[] { ".venv/bin/hertzon-cli", ".venv/Scripts/hertzon-cli.exe", "bin/hertzon-cli" })
            if (File.Exists(Path.Combine(home, rel))) return Path.Combine(home, rel);
        return null;
    }

    [HertzonHomeFact]
    public void Hertzon_Describes_WhatWeWrite_WithTheExpectedDesignsPortsAndVariables()
    {
        string cli = HertzonCli() ?? throw new InvalidOperationException("CIRCUITRF_HERTZON_HOME holds no hertzon-cli (.venv/bin/hertzon-cli).");
        var (cws, c3d) = Package();
        string path = Path.Combine(_root, "described.hzn");
        Assert.True(RunCli("convert", c3d, "-o", path).ExitCode == 0);

        var psi = new ProcessStartInfo(cli) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("describe");
        psi.ArgumentList.Add(path);
        using var proc = Process.Start(psi)!;
        var err = proc.StandardError.ReadToEndAsync();
        string json = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        Assert.True(proc.ExitCode == 0, err.GetAwaiter().GetResult() + json);

        using var doc = JsonDocument.Parse(json);
        var design = Assert.Single(doc.RootElement.GetProperty("designs").EnumerateArray());
        Assert.Equal("Pkg", design.GetProperty("name").GetString());
        Assert.Equal("3d", design.GetProperty("kind").GetString());
        var port = Assert.Single(design.GetProperty("ports").EnumerateArray());
        Assert.Equal(1, port.GetProperty("number").GetInt32());
        Assert.Equal("Lumped Port", port.GetProperty("kind").GetString());
        Assert.Equal(50.0, port.GetProperty("z0_ohm").GetDouble());
        var h = Assert.Single(design.GetProperty("variables").EnumerateArray());
        Assert.Equal("h", h.GetProperty("name").GetString());
        Assert.Equal(0.0015, h.GetProperty("value").GetDouble(), 12);
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = AppContext.BaseDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
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
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(HznExportCliTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }
}
