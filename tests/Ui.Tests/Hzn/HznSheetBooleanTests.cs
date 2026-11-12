using System.Text.Json.Nodes;
using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Ui.ThreeD.Hzn;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// brief-hzn-10 R-hzn10-4 — Unite and Subtract on Hertzon sheets: translated into a .c3d boolean of sheets, dressed by the Blank's
/// boundary, saved back one property at a time, and written as Hertzon writes one. The fixture (<c>sheet-boolean.hzn</c>) is a
/// notched patch with an inset feed written for these tests: the Blank is a rectangle, a Subtract takes the notch and a Unite the feed.
/// </summary>
public sealed class HznSheetBooleanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf_hzn10_" + Guid.NewGuid().ToString("N")[..8]);

    public HznSheetBooleanTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Fixture => Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), "sheet-boolean.hzn");

    private static C3dTranslation Translate(string path)
    {
        var read = HznFile.Read(path);
        Assert.True(read.Ok, read.Refusal?.Render());
        return HznToC3d.Translate(read.File!.Project, "Notched");
    }

    /// <summary>The fixture with one change made to its JSON, written beside the test.</summary>
    private string Mutated(Action<JsonObject> change)
    {
        var json = JsonNode.Parse(File.ReadAllText(Fixture))!.AsObject();
        change(json["designs"]![0]!.AsObject());
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N")[..6] + ".hzn");
        File.WriteAllText(path, json.ToJsonString());
        return path;
    }

    private static JsonObject ObjectNamed(JsonObject design, string name)
        => design["objects"]!.AsArray().Select(o => o!.AsObject()).First(o => (string?)o["name"] == name);

    [Fact]
    public void UniteAndSubtractOnASheet_TranslateToABooleanOfSheets_DressedByTheBlanksBoundary()
    {
        var t = Translate(Fixture);
        Assert.True(t.Ok, t.Refusal?.Render());
        var patch = Assert.IsType<C3dBoolean>(t.Document!.Objects.Single(o => o.Name == "Patch"));
        Assert.Equal(C3dBooleanOp.Unite, patch.Op);
        Assert.Equal(["Feed"], patch.Tools.Select(x => x.Name));
        var notched = Assert.IsType<C3dBoolean>(patch.Blank);
        Assert.Equal((C3dBooleanOp.Subtract, "Notch"), (notched.Op, notched.Tools.Single().Name));
        Assert.True(C3dSheetBooleans.IsSheetBoolean(patch));
        // The patch's Perfect E is on its Blank, so the result conducts as a perfect sheet.
        var carrier = C3dSheetBooleans.Carrier(patch)!;
        Assert.Equal(Em3dRole.Conductor, carrier.Role);
        Assert.Equal("pec", carrier.Material);
        // Each operand keeps its expressions: the notch's depth is still `inset`.
        var notch = (C3dSheet)notched.Tools[0];
        Assert.Equal("inset", C3dBindings.GetExpr(notch.Rect!, nameof(C3dRect.Size), 1)!.Expr);
    }

    [Fact]
    public void AnAssignmentOnAConsumedTool_IsRefusedByName()
    {
        var t = Translate(Mutated(d => d["boundaries"]![1]!["objects"]!.AsArray().Add("Notch")));
        Assert.False(t.Ok);
        Assert.Contains(t.Refusals, r => r.What == "Perfect E on 'Notch'" && r.Why.Contains("consumed by 'Patch'"));
    }

    [Fact]
    public void ToolsOutOfOnePlane_KeepTheItem_WithTheReason()
    {
        var t = Translate(Mutated(d => ObjectNamed(d, "Feed")["history"]![0]!["props"]!["Position"] = "-wf/2,-Ws/2,0"));
        Assert.False(t.Ok);
        Assert.Contains(t.Refusals, r => r.Kind == HznRefusalKind.Operation && r.What == "Unite on a sheet" && r.Why.Contains("offset"));
    }

    [Fact]
    public void AnEditedOperand_RewritesOnlyItsCreationStepsProperty()
    {
        string path = Path.Combine(_root, "edit.hzn");
        File.Copy(Fixture, path);
        string[] before = File.ReadAllText(path).Split('\n');
        var source = new HznDesignSource(path, "Notched", new TechnologyCache());
        var doc = source.Load();
        var feed = (C3dSheet)((C3dBoolean)doc.Objects.Single(o => o.Name == "Patch")).Tools[0];
        var size = C3dBindings.SpecOf(typeof(C3dRect), nameof(C3dRect.Size))!;
        C3dBindings.SetExpr(feed.Rect!, size, 0, new C3dExpr("2*wf", "mm"));
        C3dBindings.SetNumber(feed.Rect!, size, 0, 6);
        Assert.True(C3dResolver.Resolve(doc, C3dCell.None).Ok);

        Assert.Null(source.Save(doc));

        string[] after = File.ReadAllText(path).Split('\n');
        Assert.Equal(before.Length, after.Length);
        var changed = Assert.Single(before.Zip(after), p => p.First != p.Second);
        Assert.Equal(("\"XSize\": \"wf\",", "\"XSize\": \"2*wf\","), (changed.First.Trim(), changed.Second.Trim()));
    }

    /// <summary>A Boolean of sheets is written one step per tool, so an operand added is a step inside the history, not at its end:
    /// refused with the reason, and the file is not touched.</summary>
    [Fact]
    public void AnOperandAdded_IsRefusedWithTheReason_AndNothingIsWritten()
    {
        string path = Path.Combine(_root, "add.hzn");
        File.Copy(Fixture, path);
        byte[] original = File.ReadAllBytes(path);
        var source = new HznDesignSource(path, "Notched", new TechnologyCache());
        var doc = source.Load();
        var notched = (C3dBoolean)((C3dBoolean)doc.Objects.Single(o => o.Name == "Patch")).Blank!;
        var second = (C3dSheet)C3dBooleans.Copy(notched.Tools[0]);
        second.Name = "Notch2";
        notched.Tools.Add(second);

        Assert.Contains("an operand added to or removed from a boolean of sheets is not saved", source.Save(doc));
        Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(original));
    }

    [Fact]
    public void ANativeNotchedPatch_IsWrittenAsHertzonWritesOne_AndReadsBack()
    {
        var (written, path) = WriteNative();
        var design = HznFile.Read(path).File!.Project.Designs[0];
        var patch = design.Objects.First(o => o.Name == "patch");
        Assert.Equal(["CreateRectangle", "Subtract", "Unite"], patch.History.Select(h => h.Kind));
        Assert.Equal(["patch", "patch"], design.Objects.Where(o => o.Name is "notch" or "feed").Select(o => o.ConsumedBy));
        // The result is metal by one boundary on the result, not on its tools.
        var metal = Assert.Single(design.Boundaries, b => b.Kind == "Finite Conductivity");
        Assert.Contains("patch", metal.Objects);
        Assert.DoesNotContain("notch", metal.Objects);
        Assert.DoesNotContain("feed", metal.Objects);
        var back = HznToC3d.Translate(HznFile.Read(path).File!.Project, "Native");
        Assert.True(back.Ok, back.Refusal?.Render());
        Assert.True(C3dSheetBooleans.IsSheetBoolean(back.Document!.Objects.Single(o => o.Name == "patch")));
        Assert.Empty(written.Refusals);
    }

    /// <summary>Gate 2 — Hertzon's own patch, edited (<c>inset</c> changed) and saved, and the native notched patch exported, both read
    /// by <c>hertzon-cli describe</c>. Skipped without an install.</summary>
    [HertzonHomeFact]
    public void Hertzon_Describes_AnEditedPatch_AndAnExportedNotchedPatch()
    {
        string path = Path.Combine(_root, "PatchAntenna.hzn");
        File.Copy(Path.Combine(HertzonHomeFactAttribute.Home!, "examples", "PatchAntenna.hzn"), path);
        var source = new HznDesignSource(path, "Patch245", new TechnologyCache());
        var doc = source.Load();
        doc.Variables.Single(v => v.Name == "inset").Expression = "9mm";
        Assert.True(C3dResolver.Resolve(doc, C3dCell.None).Ok);
        Assert.Null(source.Save(doc));
        Assert.Contains("\"inset\": \"9mm\"", File.ReadAllText(path));
        Assert.Equal(9e-3, Describe(path).GetProperty("variables").EnumerateArray()
                                            .Single(v => v.GetProperty("name").GetString() == "inset").GetProperty("value").GetDouble(), 9);

        var (_, native) = WriteNative();
        var described = Describe(native);
        Assert.Equal("Native", described.GetProperty("name").GetString());
        Assert.Equal(1, described.GetProperty("ports").GetArrayLength());
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A native notched patch with an inset feed and a lumped port, written as a new Hertzon project.</summary>
    private (C3dToHznResult Written, string Path) WriteNative()
    {
        const long Mm = 1_000_000;
        C3dSheet Rect(string name, long u, long v, long su, long sv, long z) => new()
        {
            Name = name, Plane = C3dPlane.XY, Offset = z, Rect = new C3dRect { Min = new C3dPoint2(u, v), Size = new C3dPoint2(su, sv) },
        };
        var blank = Rect("", -15 * Mm, -12 * Mm, 30 * Mm, 24 * Mm, 1_600_000);
        blank.Material = "Copper";
        var notched = new C3dBoolean { Op = C3dBooleanOp.Subtract, Blank = blank, Tools = [Rect("notch", -2_500_000, -12 * Mm, 5 * Mm, 7 * Mm, 1_600_000)] };
        var patch = new C3dBoolean { Name = "patch", Op = C3dBooleanOp.Unite, Blank = notched, Tools = [Rect("feed", -1_500_000, -25 * Mm, 3 * Mm, 20 * Mm, 1_600_000)] };
        var ground = Rect("ground", -25 * Mm, -25 * Mm, 50 * Mm, 50 * Mm, 0);
        ground.Material = "Copper";
        var doc = new C3dDocument
        {
            Objects =
            [
                new C3dBox { Name = "sub", Material = "FR", Min = new C3dPoint3(-25 * Mm, -25 * Mm, 0), Size = new C3dPoint3(50 * Mm, 50 * Mm, 1_600_000) },
                ground, patch,
            ],
            Ports = [new C3dPort { Number = 1, Name = "P1", Kind = Em3dPortKind.Lumped, Plane = C3dPlane.XZ, Offset = -25 * Mm, Z0 = "50",
                                   Rect = new C3dRect { Min = new C3dPoint2(-1_500_000, 0), Size = new C3dPoint2(3 * Mm, 1_600_000) } }],
        };
        var tech = new Technology
        {
            Name = "t",
            Materials = [new TechMaterial { Name = "Copper", Sigma20 = 5.8e7 }, new TechMaterial { Name = "FR", Epsr = 4.4 }],
        };
        var written = C3dToHzn.Write(doc, new HznExportOptions { DesignName = "Native", Technology = tech });
        Assert.True(written.Ok, HznExportDiagnostics.Refused("Native", written.Refusals).Render());
        string path = Path.Combine(_root, "native.hzn");
        var outcome = HznExport.ToNewFile(written, path);
        Assert.True(outcome.Ok, outcome.Error?.Render());
        return (written, path);
    }

    internal static System.Text.Json.JsonElement Describe(string path)
    {
        string cli = new[] { ".venv/bin/hertzon-cli", ".venv/Scripts/hertzon-cli.exe", "bin/hertzon-cli" }
            .Select(r => Path.Combine(HertzonHomeFactAttribute.Home!, r)).FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("CIRCUITRF_HERTZON_HOME holds no hertzon-cli.");
        var psi = new System.Diagnostics.ProcessStartInfo(cli) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("describe");
        psi.ArgumentList.Add(path);
        using var run = System.Diagnostics.Process.Start(psi)!;
        var err = run.StandardError.ReadToEndAsync();
        string json = run.StandardOutput.ReadToEnd();
        run.WaitForExit();
        Assert.True(run.ExitCode == 0, err.GetAwaiter().GetResult() + json);
        using var described = System.Text.Json.JsonDocument.Parse(json);
        return described.RootElement.GetProperty("designs").EnumerateArray().Single().Clone();
    }
}
