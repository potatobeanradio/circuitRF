using System.Diagnostics;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ThreeD;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// HZN-9 — a design of a Hertzon project placed as an instance in a <c>.c3d</c>. The fixtures are the small hand-written
/// projects in <c>tests/Ui.Tests/Hzn/Fixtures</c>; the parent's technology is the translation's own in-memory technology
/// written to a <c>.ctech</c>, so "identical materials" is true by construction until a test changes one.
/// </summary>
public sealed class HznInstanceTests : IDisposable
{
    private const long Mm = 1_000_000;                         // DBU per mm at the 1000 DBU/µm a translated document uses
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-hzn9-" + Guid.NewGuid().ToString("N")[..10]);

    public HznInstanceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string Fixture(string name) => Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), name);

    /// <summary>A workspace holding <c>parts/&lt;file&gt;</c> (a copy of the fixture) and a technology that states, exactly,
    /// the materials the fixture's design uses.</summary>
    private (string Ws, string Hzn, string Standalone) Workspace(string fixture = "instance.hzn", string design = "Pad")
    {
        string ws = Path.Combine(_root, "ws" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(ws, "parts"));
        string hzn = Path.Combine(ws, "parts", fixture);
        File.Copy(Fixture(fixture), hzn);

        var read = HznFile.Read(hzn);
        Assert.True(read.Ok, read.Refusal?.Render());
        var t = HznToC3d.Translate(read.File!.Project, design);
        string tech = Path.Combine(ws, "tech.ctech");
        if (t.Ok)
        {
            t.Technology!.Name = "tech";
            TechPersistence.SaveToFile(tech, t.Technology);
        }
        WorkspacePersistence.SaveToFile(Path.Combine(ws, ".cws"), new CwsFile { DefaultTechRef = "tech.ctech" });

        // The same design, translated and elaborated on its own: what the placed one must equal, moved.
        string standalone = "";
        if (t.Ok)
        {
            t.Document!.TechRef = "../../tech.ctech";
            standalone = WriteC3d(ws, "Alone", t.Document);
        }
        return (ws, hzn, standalone);
    }

    private static string WriteC3d(string ws, string cell, C3dDocument doc)
    {
        string dir = Path.Combine(ws, cell, "3d");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, cell + ".c3d");
        C3dPersistence.SaveToFile(path, doc);
        return path;
    }

    private static C3dInstance Place(string design = "Pad", long x = 5 * Mm, Dictionary<string, C3dExpr>? overrides = null) => new()
    {
        Name = "U1", CellRef = "../../parts/instance.hzn", View = C3dInstanceView.Hertzon, Design = design,
        Placement = new C3dPlacement { Origin = new(x, 0, 0) }, Params = overrides,
    };

    private static string Parent(string ws, params C3dInstance[] instances) =>
        WriteC3d(ws, "Top", new C3dDocument { DbuPerMicron = 1000, Instances = [.. instances] });

    private static C3dElaboration Elaborate(string path, C3dElaborator? e = null) =>
        (e ?? new C3dElaborator()).Elaborate(C3dPersistence.LoadFromFile(path), path, null);

    private static (double X0, double Y0, double Z0, double X1, double Y1, double Z1) Bounds(C3dElaboration e, string name) =>
        Em3dProblem.Bounds(e.Solids.Single(s => s.Name == name).Primitive);

    [Fact]
    public void APlacedDesign_ElaboratesToTheSolidsOfItsOwnTranslation_MovedByThePlacement()
    {
        var (ws, _, standalone) = Workspace();
        var alone = Elaborate(standalone);
        Assert.True(alone.Ok, string.Join(" ", alone.Refusals));
        var placed = Elaborate(Parent(ws, Place()));
        Assert.True(placed.Ok, string.Join(" ", placed.Refusals));

        Assert.Equal(alone.Solids.Count, placed.Solids.Count);
        Assert.NotEmpty(alone.Solids);
        foreach (var s in alone.Solids)
        {
            var (a, b) = (Bounds(alone, s.Name), Bounds(placed, "U1/" + s.Name));
            Assert.Equal(a.X0 + 5e-3, b.X0, 1e-12);
            Assert.Equal(a.X1 + 5e-3, b.X1, 1e-12);
            Assert.Equal((a.Y0, a.Z0, a.Y1, a.Z1), (b.Y0, b.Z0, b.Y1, b.Z1));
            Assert.Equal(alone.Solids.Single(x => x.Name == s.Name).Material, placed.Solids.Single(x => x.Name == "U1/" + s.Name).Material);
        }
    }

    [Fact]
    public void AnOverrideOfADesignVariableAndOfAProjectVariable_TakeEffect_EvaluatedInTheParentsScope()
    {
        var (ws, _, _) = Workspace();
        string path = WriteC3d(ws, "Top", new C3dDocument
        {
            DbuPerMicron = 1000,
            Variables = [new C3dVariable { Name = "double_w", Expression = "6mm" }],
            Instances = [Place(overrides: new() { ["w"] = new C3dExpr("double_w * 2/3 + 1mm"), ["tt"] = new C3dExpr("0.1mm") })],
        });
        var e = Elaborate(path);
        Assert.True(e.Ok, string.Join(" ", e.Refusals));
        var sub = Bounds(e, "U1/Sub");
        Assert.Equal(5e-3, sub.X1 - sub.X0, 1e-12);                       // w = 6mm * 2/3 + 1mm
        var trace = Bounds(e, "U1/Trace");
        Assert.Equal(0.1e-3, trace.Z1 - trace.Z0, 1e-12);                 // the project variable tt
        Assert.Equal(2.5e-3, trace.X1 - trace.X0, 1e-12);                 // w/2 follows the override
    }

    [Fact]
    public void AnOverrideNamingAVariableTheDesignDoesNotHave_IsRefusedListingTheOnesItHas()
    {
        var (ws, _, _) = Workspace();
        var e = Elaborate(Parent(ws, Place(overrides: new() { ["nope"] = new C3dExpr("1mm") })));
        Assert.False(e.Ok);
        string refusal = Assert.Single(e.Refusals);
        Assert.Contains("Instance 'U1' overrides 'nope'", refusal);
        Assert.Contains("L, h, tt, w", refusal);
        Assert.Empty(e.Solids);
    }

    [Fact]
    public void AnOverrideOfAQuantityThatStatesNoUnit_IsRefused_NotReadAsMetres()
    {
        var (ws, _, _) = Workspace();
        var e = Elaborate(Parent(ws, Place(overrides: new() { ["w"] = new C3dExpr("4") })));
        Assert.False(e.Ok);
        Assert.Contains("overrides 'w' with '4', which states no unit", Assert.Single(e.Refusals));
    }

    [Fact]
    public void ADesignThatWouldBeRefusedWhenOpened_IsRefusedAsAnInstance_WithTheWholeList_NamingTheInstance()
    {
        var (ws, _, _) = Workspace("unsupported.hzn", "Bad");
        string path = WriteC3d(ws, "Top", new C3dDocument
        {
            DbuPerMicron = 1000,
            Instances = [new C3dInstance { Name = "U1", CellRef = "../../parts/unsupported.hzn", View = C3dInstanceView.Hertzon, Design = "Bad" }],
        });
        var e = Elaborate(path);
        Assert.False(e.Ok);
        string refusal = Assert.Single(e.Refusals);
        Assert.StartsWith("Instance 'U1' places design 'Bad'", refusal);
        Assert.Contains("CreateTorus", refusal);
        Assert.Contains("Fillet", refusal);
        Assert.Contains("Incident Plane Wave", refusal);
        Assert.Empty(e.Solids);
        Assert.Contains(e.Unresolved, u => u.Item1 == "U1");
    }

    [Fact]
    public void AChangedHzn_IsPickedUpOnTheNextElaboration_AndAnUnchangedOneIsTranslatedOnce()
    {
        var (ws, hzn, _) = Workspace();
        string path = Parent(ws, Place());
        var elaborator = new C3dElaborator();
        Assert.Equal(10e-3, Length(Elaborate(path, elaborator)), 1e-12);
        Elaborate(path, elaborator);
        Assert.Equal(1, elaborator.Hertzon.Translations);

        // Same length, newer time: the stamp moves, the hash settles it.
        File.WriteAllText(hzn, File.ReadAllText(hzn).Replace("\"L\": \"10mm\"", "\"L\": \"12mm\""));
        File.SetLastWriteTimeUtc(hzn, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(12e-3, Length(Elaborate(path, elaborator)), 1e-12);
        Assert.Equal(2, elaborator.Hertzon.Translations);

        // Touched with no change of content: no second translation.
        File.SetLastWriteTimeUtc(hzn, DateTime.UtcNow.AddSeconds(10));
        Elaborate(path, elaborator);
        Assert.Equal(2, elaborator.Hertzon.Translations);

        static double Length(C3dElaboration e) { var b = Bounds(e, "U1/Sub"); return b.Y1 - b.Y0; }
    }

    [Theory]
    [InlineData("conflict")]
    [InlineData("missing")]
    public void AMaterialTheParentsTechnologyStatesDifferentlyOrNotAtAll_IsRefusedListingIt_AndTheTechnologyIsNotWritten(string kind)
    {
        var (ws, _, _) = Workspace();
        string techPath = Path.Combine(ws, "tech.ctech");
        var tech = TechPersistence.LoadFromFile(techPath);
        if (kind == "conflict") tech.Materials.Single(m => m.Name == "lam").Epsr = 3.9;
        else tech.Materials.RemoveAll(m => m.Name == "lam");
        TechPersistence.SaveToFile(techPath, tech);
        byte[] before = File.ReadAllBytes(techPath);

        var e = Elaborate(Parent(ws, Place()));
        Assert.False(e.Ok);
        string refusal = Assert.Single(e.Refusals);
        Assert.Contains("'lam'", refusal);
        Assert.Contains(kind == "conflict" ? "εr 3.55" : "is not defined", refusal);
        if (kind == "conflict") Assert.Contains("εr 3.9", refusal);
        Assert.Empty(e.Solids);
        Assert.Equal(before, File.ReadAllBytes(techPath));
    }

    [Theory]
    [InlineData("differs")]
    [InlineData("missing")]
    public void AConductorTheParentsTechnologyStatesDifferentlyOrNotAtAll_StillPlaces_AndIsOnlyNoted(string kind)
    {
        var (ws, _, _) = Workspace();
        string techPath = Path.Combine(ws, "tech.ctech");
        var tech = TechPersistence.LoadFromFile(techPath);
        var pec = tech.Materials.Single(m => m.Name == "pec");
        if (kind == "differs") pec.Sigma20 = 1.0e7;
        else tech.Materials.Remove(pec);
        TechPersistence.SaveToFile(techPath, tech);

        var e = Elaborate(Parent(ws, Place()));
        Assert.True(e.Ok, string.Join("; ", e.Refusals));
        Assert.NotEmpty(e.Solids);
        Assert.Contains(e.Notes, n => n.Contains("'pec'") && n.Contains("keeps its own"));
    }

    [Fact]
    public void Check_FollowsTheReference_AndReportsAMissingFileAsABrokenReference()
    {
        var (ws, hzn, _) = Workspace();
        string path = Parent(ws, Place());
        var ok = RunCli("check", path);
        Assert.True(ok.ExitCode == 0, ok.StdErr + ok.StdOut);

        File.Delete(hzn);
        var broken = RunCli("check", path);
        Assert.Equal(1, broken.ExitCode);
        Assert.Contains("Instance 'U1' places design 'Pad' of '../../parts/instance.hzn', which resolves to nothing", broken.StdErr);

        File.Copy(Fixture("instance.hzn"), hzn);
        var noDesign = RunCli("check", Parent(ws, Place(design: "Elsewhere")));
        Assert.Equal(1, noDesign.ExitCode);
        Assert.Contains("design 'Elsewhere'", noDesign.StdErr);
        Assert.Contains("Pad", noDesign.StdErr);
    }

    [Fact]
    public void ExplainRef_ResolvesAHertzonReference_AndSaysWhenItLeavesTheWorkspace()
    {
        var (ws, _, _) = Workspace();
        Parent(ws, Place());
        var run = RunCli("explain", Path.Combine(ws, "Top", "3d"), "--ref", "../../parts/instance.hzn", "--json");
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        Assert.Contains("\"state\": \"resolved\"", run.StdOut.Replace("\":\"", "\": \""));
        var missing = RunCli("explain", Path.Combine(ws, "Top", "3d"), "--ref", "../../parts/gone.hzn");
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("does not exist", missing.StdErr);
    }

    [Fact]
    public void ADroppedHertzonDesign_PlansAnInstanceWithTheRelativeReference_AndAMissingFileIsRefused()
    {
        var (ws, hzn, _) = Workspace();
        string top = Parent(ws, Place());
        var plan = C3dTreeDrop.Resolve(new HznDesignDragPayload(hzn, "Pad").Serialize(), top)!;
        Assert.True(plan.Ok, plan.Refusal);
        Assert.Equal((C3dInstanceView.Hertzon, "Pad", "../../parts/instance.hzn"), (plan.View, plan.Design, plan.CellRef));
        Assert.Equal(hzn, plan.CellDir);

        File.Delete(hzn);
        Assert.Contains("does not exist", C3dTreeDrop.Resolve(new HznDesignDragPayload(hzn, "Pad").Serialize(), top)!.Refusal);
    }

    [Fact]
    public void MovingTheC3d_RespellsAHertzonReference_AsItDoesAnyCellReference()
    {
        var (ws, hzn, _) = Workspace();
        string top = Parent(ws, Place());
        var site = MoveRefRegistry.Sites.Single(x => x.Id == "c3d/CellRef");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(top))!;
        var slot = Assert.Single(site.Locate(node));
        string baseDir = site.BaseDirOf(top)!;
        Assert.Equal(hzn, site.Resolve(slot.Stored, baseDir));
        // The .c3d moves one folder deeper: the reference is written again from there, to the same file.
        string deeper = Path.Combine(ws, "Top", "3d", "sub");
        Assert.Equal("../../../parts/instance.hzn", site.Store(hzn, deeper, slot.Stored));
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(HznInstanceTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        psi.ArgumentList.Add(Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")));
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
