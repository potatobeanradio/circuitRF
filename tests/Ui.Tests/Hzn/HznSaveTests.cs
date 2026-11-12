using CircuitRF.Design.Interchange.Hzn;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.ThreeD.Hzn;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// HZN-5 R-hzn5-2 / R-hzn5-3 / R-hzn5-4 — a save rewrites only what was edited. Each test opens the fixture design through the
/// editor's document source, edits the in-memory document as the editor would, saves, and reads the file: the bytes that were not
/// edited are the file's own, and a save that cannot be traced back to the file is refused and writes nothing.
/// </summary>
public sealed class HznSaveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf_hznsave_" + Guid.NewGuid().ToString("N")[..8]);

    public HznSaveTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed record Opened(string Path, HznDesignSource Source, C3dDocument Doc, byte[] Original)
    {
        public string Text => File.ReadAllText(Path);
        public string[] Lines => Text.Split('\n');
        public string[] OriginalLines => System.Text.Encoding.UTF8.GetString(Original).Split('\n');

        public T Get<T>(string name) where T : C3dObject => (T)Doc.Objects.First(o => o.Name == name);

        /// <summary>The lines that differ, when the file has as many lines as it had.</summary>
        public List<(string Was, string Now)> ChangedLines()
        {
            Assert.Equal(OriginalLines.Length, Lines.Length);
            return [.. OriginalLines.Zip(Lines).Where(p => p.First != p.Second)];
        }
    }

    private Opened Open(string fixture = "save-design.hzn", string design = "Save")
    {
        string path = Path.Combine(_root, fixture);
        File.Copy(Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), fixture), path, overwrite: true);
        var source = new HznDesignSource(path, design, new TechnologyCache());
        return new Opened(path, source, source.Load(), File.ReadAllBytes(path));
    }

    private static void SetExpression(IC3dBindable owner, string property, int k, string expr, double value)
    {
        var spec = C3dBindings.SpecOf(owner.GetType(), property)!;
        C3dBindings.SetExpr(owner, spec, k, new C3dExpr(expr, "mm"));
        C3dBindings.SetNumber(owner, spec, k, value);
    }

    private static void Resolve(C3dDocument doc) => Assert.True(C3dResolver.Resolve(doc, C3dCell.None).Ok);

    [Fact]
    public void ASaveWithNoEdit_IsByteIdentical()
    {
        var o = Open();
        Assert.Null(o.Source.Save(o.Doc));
        Assert.True(File.ReadAllBytes(o.Path).AsSpan().SequenceEqual(o.Original));
    }

    [Fact]
    public void AnEditedBoxSize_ChangesExactlyOnePropertyString()
    {
        var o = Open();
        var slab = o.Get<C3dBox>("Slab");
        slab.Size = slab.Size with { X = 2_000_000 };

        Assert.Null(o.Source.Save(o.Doc));

        var changed = Assert.Single(o.ChangedLines());
        Assert.Equal("\"XSize\": \"1mm\",", changed.Was.Trim());
        Assert.Equal("\"XSize\": \"2mm\",", changed.Now.Trim());
        // …and it reads back as the edited document's box.
        var back = HznToC3d.Translate(HznFile.Read(o.Path).File!.Project, "Save").Document!;
        Assert.Equal(2_000_000, ((C3dBox)back.Objects.First(x => x.Name == "Slab")).Size.X);
    }

    [Fact]
    public void AnEditedExpression_StaysAnExpression()
    {
        var o = Open();
        var blank = (C3dBox)o.Get<C3dBoolean>("Sub").Blank!;
        SetExpression(blank, nameof(C3dBox.Size), 0, "3*w", 9_000_000);

        Assert.Null(o.Source.Save(o.Doc));

        var changed = Assert.Single(o.ChangedLines());
        Assert.Equal("\"XSize\": \"2*w\",", changed.Was.Trim());
        Assert.Equal("\"XSize\": \"3*w\",", changed.Now.Trim());
    }

    [Fact]
    public void AnAddedObject_AppendsOneObject()
    {
        var o = Open();
        o.Doc.Objects.Add(new C3dBox { Name = "Extra", Material = "lam", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(1_000_000, 1_000_000, 1_000_000) });

        Assert.Null(o.Source.Save(o.Doc));

        var before = HznFile.Read(Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), "save-design.hzn")).File!.Project.Designs[0].Objects;
        var after = HznFile.Read(o.Path).File!.Project.Designs[0].Objects;
        Assert.Equal(before.Count + 1, after.Count);
        Assert.Equal("Extra", after[^1].Name);
        // Everything that was in the file is still in it, in order, byte for byte.
        var now = o.Lines;
        int at = 0;
        foreach (string line in o.OriginalLines)
        {
            while (at < now.Length && now[at] != line) at++;
            Assert.True(at < now.Length, $"the line '{line}' is gone");
            at++;
        }
    }

    [Fact]
    public void ABooleanAdded_ExtendsTheHistory_AndAppendsItsTool()
    {
        var o = Open();
        var ball = o.Get<C3dSphere>("Ball");
        ball.Name = "";
        int at = o.Doc.Objects.FindIndex(x => ReferenceEquals(x, ball));
        o.Doc.Objects[at] = new C3dBoolean
        {
            Name = "Ball", Op = C3dBooleanOp.Subtract, Blank = ball,
            Tools = [new C3dBox { Name = "Cut", Material = "lam", Min = new C3dPoint3(0, 0, 9_000_000), Size = new C3dPoint3(1_000_000, 1_000_000, 1_000_000) }],
        };

        Assert.Null(o.Source.Save(o.Doc));

        var design = HznFile.Read(o.Path).File!.Project.Designs[0];
        var history = design.Objects.First(x => x.Name == "Ball").History;
        Assert.Equal(["CreateSphere", "Subtract"], history.Select(h => h.Kind));
        Assert.Equal("Cut", history[1].Props["Tools"]);
        Assert.Equal("Ball", design.Objects.First(x => x.Name == "Cut").ConsumedBy);
        Assert.Contains("\"Radius\": \"2mm\"", o.Text);                // the sphere's own step is as it was

        // …and taking the tool away again cuts the history back.
        var edited = o.Get<C3dBoolean>("Ball");
        o.Doc.Objects[o.Doc.Objects.IndexOf(edited)] = edited.Blank!;
        edited.Blank!.Name = "Ball";
        Assert.Null(o.Source.Save(o.Doc));
        var after = HznFile.Read(o.Path).File!.Project.Designs[0];
        Assert.Equal(["CreateSphere"], after.Objects.First(x => x.Name == "Ball").History.Select(h => h.Kind));
        Assert.DoesNotContain(after.Objects, x => x.Name == "Cut");
        Assert.True(File.ReadAllBytes(o.Path).AsSpan().SequenceEqual(o.Original));      // back to the file's own bytes
    }

    [Fact]
    public void ADeletedObject_RemovesItsBoundary_AndListsWhatNamesIt()
    {
        var o = Open();
        o.Doc.Objects.Remove(o.Get<C3dSheet>("Pad"));

        Assert.Null(o.Source.Save(o.Doc));

        var design = HznFile.Read(o.Path).File!.Project.Designs[0];
        Assert.DoesNotContain(design.Objects, x => x.Name == "Pad");
        Assert.DoesNotContain(design.Boundaries, x => x.Name == "GND");      // it named only Pad
        Assert.Contains(o.Source.LastSaveNotes, n => n.Contains("GND") && n.Contains("removed"));
        // The field plot that names it is NOT edited, and is listed.
        Assert.Contains("Jsurf_Pad", o.Text);
        Assert.Contains(o.Source.LastSaveNotes, n => n.Contains("field plot 'Jsurf_Pad'") && n.Contains("not edited"));
    }

    [Fact]
    public void ADeletedBoolean_TakesItsToolsAlong_AndListsTheFieldPlotThatNamesIt()
    {
        var o = Open();
        o.Doc.Objects.Remove(o.Get<C3dBoolean>("Sub"));

        Assert.Null(o.Source.Save(o.Doc));

        var design = HznFile.Read(o.Path).File!.Project.Designs[0];
        Assert.DoesNotContain(design.Objects, x => x.Name is "Sub" or "Pin");
        Assert.Contains(o.Source.LastSaveNotes, n => n.Contains("field plot 'Jsurf_Sub'") && n.Contains("'Sub'"));
    }

    [Fact]
    public void ADeletedPort_RemovesItsExcitationAndSheet()
    {
        var o = Open();
        o.Doc.Ports.Clear();

        Assert.Null(o.Source.Save(o.Doc));

        var design = HznFile.Read(o.Path).File!.Project.Designs[0];
        Assert.Empty(design.Excitations);
        Assert.DoesNotContain(design.Objects, x => x.Name == "Port1");
        Assert.Contains(o.Source.LastSaveNotes, n => n.Contains("P1"));
    }

    [Fact]
    public void AMovedPort_RewritesItsSheetsCreationStep()
    {
        var o = Open();
        var port = o.Doc.Ports.Single();
        SetExpression(port.Rect, nameof(C3dRect.Size), 0, "w+0.5mm", 3_500_000);

        Assert.Null(o.Source.Save(o.Doc));

        var changed = Assert.Single(o.ChangedLines());
        Assert.Equal("\"XSize\": \"w\",", changed.Was.Trim());
        Assert.StartsWith("\"XSize\": \"w+0.5", changed.Now.Trim());
    }

    [Fact]
    public void ASuppressedStep_SurvivesAnEditToItsObject()
    {
        var o = Open();
        var blocked = o.Get<C3dBox>("Blocked");
        blocked.Size = blocked.Size with { Z = 2_000_000 };

        Assert.Null(o.Source.Save(o.Doc));

        var changed = Assert.Single(o.ChangedLines());
        Assert.Contains("ZSize", changed.Now);
        Assert.Contains("9mm,9mm,9mm", o.Text);      // the suppressed Move, as it was
        var steps = HznFile.Read(o.Path).File!.Project.Designs[0].Objects.First(x => x.Name == "Blocked").History;
        Assert.Equal(true, steps[1].Suppressed);
    }

    [Fact]
    public void ProjectAndDesignVariables_GoBackUnderTheirOwnNames()
    {
        var o = Open();
        o.Doc.Variables.First(v => v.Name == "hznp_f0").Expression = "2.5GHz";
        o.Doc.Variables.First(v => v.Name == "L").Expression = "25mm";
        Resolve(o.Doc);

        Assert.Null(o.Source.Save(o.Doc));

        var project = HznFile.Read(o.Path).File!.Project;
        Assert.Equal("2.5GHz", project.Variables["f0"]);
        Assert.Equal("25mm", project.Designs[0].Variables["L"]);
        Assert.False(project.Designs[0].Variables.Contains("hznp_f0"));
        Assert.Equal("3.55", project.Variables["eps_r"]);
    }

    [Fact]
    public void AFaceBoundary_FollowsItsObject_WithANewSignature()
    {
        var o = Open();
        var cube = o.Get<C3dBox>("Cube");
        cube.Min = cube.Min with { X = 21_000_000 };

        Assert.Null(o.Source.Save(o.Doc));

        var face = HznFile.Read(o.Path).File!.Project.Designs[0].Boundaries.First(b => b.Name == "PerfE_Face").Faces.Single();
        Assert.Equal(0.021, face.Signature[0], 9);
        Assert.Equal(0, face.Face);            // the index still names that face, so it is kept
        Assert.Contains("Position", o.Text);
    }

    [Fact]
    public void AnEditedDesign_ReadsBackToTheEditedDocument()
    {
        var o = Open();
        o.Get<C3dBox>("Slab").Size = new C3dPoint3(2_000_000, 2_000_000, 3_000_000);
        o.Get<C3dBox>("Blocked").Name = "Blocked2";
        o.Doc.Objects.Add(new C3dBox { Name = "Extra", Material = "lam", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(1_000_000, 1_000_000, 1_000_000) });
        // A rename is a delete and an add: the old object goes and the new one is appended.

        Assert.Null(o.Source.Save(o.Doc));

        var back = HznToC3d.Translate(HznFile.Read(o.Path).File!.Project, "Save");
        Assert.True(back.Ok, string.Join("\n", back.Refusals.Select(r => r.What)));
        Assert.Equal(o.Doc.Objects.Select(x => x.Name).Order(), back.Document!.Objects.Select(x => x.Name).Order());
        foreach (var mine in o.Doc.Objects.OfType<C3dBox>())
            Assert.Equal(mine.Size, ((C3dBox)back.Document.Objects.First(x => x.Name == mine.Name)).Size);
    }

    [HertzonHomeFact]
    public void Hertzon_Describes_AnEditedSave_WithTheExpectedDesignPortsAndVariables()
    {
        var o = Open();
        o.Get<C3dBox>("Slab").Size = new C3dPoint3(2_000_000, 2_000_000, 3_000_000);
        o.Doc.Objects.Add(new C3dBox { Name = "Extra", Material = "lam", Min = new C3dPoint3(0, 0, 0), Size = new C3dPoint3(1_000_000, 1_000_000, 1_000_000) });
        o.Doc.Variables.First(v => v.Name == "hznp_f0").Expression = "2.5GHz";
        Resolve(o.Doc);
        Assert.Null(o.Source.Save(o.Doc));

        string cli = new[] { ".venv/bin/hertzon-cli", ".venv/Scripts/hertzon-cli.exe", "bin/hertzon-cli" }
            .Select(r => Path.Combine(HertzonHomeFactAttribute.Home!, r)).FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("CIRCUITRF_HERTZON_HOME holds no hertzon-cli.");
        var psi = new System.Diagnostics.ProcessStartInfo(cli) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("describe");
        psi.ArgumentList.Add(o.Path);
        using var run = System.Diagnostics.Process.Start(psi)!;
        var err = run.StandardError.ReadToEndAsync();
        string json = run.StandardOutput.ReadToEnd();
        run.WaitForExit();
        Assert.True(run.ExitCode == 0, err.GetAwaiter().GetResult() + json);

        using var described = System.Text.Json.JsonDocument.Parse(json);
        var design = described.RootElement.GetProperty("designs").EnumerateArray().Single();
        Assert.Equal("Save", design.GetProperty("name").GetString());
        Assert.Equal(1, design.GetProperty("ports").GetArrayLength());
        var f0 = design.GetProperty("variables").EnumerateArray().Single(v => v.GetProperty("name").GetString() == "f0");
        Assert.Equal(2.5e9, f0.GetProperty("value").GetDouble(), 3);
    }

    [Fact]
    public void AFileChangedOnDisk_IsRefused_AndNothingIsWritten()
    {
        var o = Open();
        File.AppendAllText(o.Path, "\n");                       // another program saved it
        byte[] theirs = File.ReadAllBytes(o.Path);
        var slab = o.Get<C3dBox>("Slab");
        slab.Size = slab.Size with { X = 2_000_000 };

        string? why = o.Source.Save(o.Doc);

        Assert.NotNull(why);
        Assert.True(o.Source.LastSaveChangedOnDisk);
        Assert.True(File.ReadAllBytes(o.Path).AsSpan().SequenceEqual(theirs));
        Assert.Contains("Objects changed: Slab", o.Source.EditSummary(o.Doc));

        // Reload reads what the other program saved; the next edit then saves against that file.
        var again = o.Source.Load();
        var reloaded = (C3dBox)again.Objects.First(x => x.Name == "Slab");
        Assert.Equal(1_000_000, reloaded.Size.X);
        reloaded.Size = reloaded.Size with { X = 3_000_000 };
        Assert.Null(o.Source.Save(again));
        Assert.Contains("\"XSize\": \"3mm\"", o.Text);
    }

    [Fact]
    public void AnEditThatCannotBeTracedBack_RefusesTheSave_AndTheDocumentSurvives()
    {
        var o = Open();
        o.Doc.AirBoxMaterial = "lam";                            // a setting a Hertzon design has no key for here
        o.Doc.Objects.Add(new C3dEllipsoid { Name = "Egg", Material = "lam", Centre = new C3dPoint3(0, 0, 0), SemiAxes = new C3dPoint3(1_000_000, 2_000_000, 3_000_000) });

        string? why = o.Source.Save(o.Doc);

        Assert.NotNull(why);
        Assert.False(o.Source.LastSaveChangedOnDisk);
        Assert.Contains("AirBoxMaterial", why);
        Assert.Contains("ellipsoid", why);
        Assert.True(File.ReadAllBytes(o.Path).AsSpan().SequenceEqual(o.Original));      // the file is as it was
        Assert.Contains(o.Doc.Objects, x => x.Name == "Egg");                           // and the edits are still in memory
    }
}
