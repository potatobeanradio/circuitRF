using System.Text.Json;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em.ParametricEm;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// brief-sdl-8 §3 — a schematic-driven cell simulated by EM through a setup embedded in its 3D view. Nothing here solves:
/// each test reads the geometry an evaluation builds, its key, its connectivity signature, or the solver input a held
/// setup writes.
/// </summary>
public sealed class EmThreeDCellTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-sdl8-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    private const string Design = "C1-through-line";

    /// <summary>C1 with a VAR block: w50 drives every line's width, Lx the last line's length, and spare drives nothing.</summary>
    private EmCellFixture Fixture(string tag, params EditableParameter[] moreVars) => EmCellFixture.Create(Design, Path.Combine(_scratch, tag), AnalysisModel.Em, edit: m =>
    {
        var vars = new EditableComponent
        {
            InstanceName = "VAR1", Symbol = SymbolKind.Var,
            Parameters =
            {
                new EditableParameter { Name = "w50", Expression = "1.1", Unit = "mm" },
                new EditableParameter { Name = "Lx", Expression = "7", Unit = "mm" },
                new EditableParameter { Name = "spare", Expression = "2", Unit = "mm" },
            },
        };
        vars.Parameters.AddRange(moreVars);
        m.Components.Add(vars);
        foreach (var tl in m.Components.Where(c => c.InstanceName.StartsWith("TL", StringComparison.Ordinal)))
        {
            var w = tl.Parameters.Single(p => p.Name == "W");
            (w.Expression, w.Unit) = ("w50", "");
        }
        var l = m.Components.Single(c => c.InstanceName == "TL3").Parameters.Single(p => p.Name == "L");
        (l.Expression, l.Unit) = ("Lx", "");
    });

    private static EmSetup Driven(string name, Em3dSolverId solver) => new()
    {
        Name = name, Solver3D = Em3dSolverChoice.One(solver),
        Frequency = new FrequencySpec("1", "3", 3, SweepKind.Linear, "GHz", "GHz"),
    };

    /// <summary>New 3D View from Layout on the fixture's cell, with driven setups embedded, saved as its 3D view.</summary>
    private static (string Path, C3dFromLayout Made) ThreeDView(EmCellFixture fx, Action<C3dDocument, string>? edit = null,
                                                                params EmSetup[] setups)
    {
        string dir = CellFolder.SubFolderPath(fx.P.CellDir, ViewType.ThreeD);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Design + C3dPersistence.Extension);
        var made = C3dHierarchy.NewFromLayout(fx.P.CellDir, path, fx.Cws);
        Assert.Null(made.Refusal);
        var doc = made.Document!;
        foreach (var s in setups.Length == 0 ? [Driven("Driven1", Em3dSolverId.Palace)] : setups)
            doc.Setups.Add(EmSetupPersistence.ToEmbedded(s));
        edit?.Invoke(doc, path);
        C3dPersistence.SaveToFile(path, doc);
        return (path, made);
    }

    private static Dictionary<string, Value> At(params (string Name, double Si)[] values) =>
        values.ToDictionary(v => v.Name, v => new Value(v.Si), StringComparer.Ordinal);

    private static EmCellGeometry Geometry(EmCellTarget t, Dictionary<string, Value> values)
    {
        var g = EmEvaluation.Geometry(t, values, out var refusal);
        Assert.True(g is not null, refusal);
        return g!;
    }

    /// <summary>D4 and D1: the new view links one VAR per schematic name and takes the layout's port labels; its driven setup
    /// is an EM view of the cell, named <c>#Driven1</c>, and with the planar .cem beside it the choice must be named.</summary>
    [Fact]
    public void NewFromLayout_LinksTheSchematicsNames_AndItsSetupIsAnEmView()
    {
        var fx = Fixture("new");
        var (path, made) = ThreeDView(fx);
        Assert.Equal(["Lx", "spare", "w50"], made.LinkedVariables.Order(StringComparer.Ordinal));
        var doc = made.Document!;
        Assert.Equal("1.1", doc.Variables.Single(v => v.Name == "w50").Expression);
        Assert.Equal(2, doc.Ports.Count);
        Assert.All(doc.Ports, p => Assert.Equal("U1", p.FromLayout?.Instance));

        Assert.Null(EmCapability.Resolve(fx.P.CellDir, null, null, out var several));
        Assert.Contains("#Driven1", several);
        var t = EmCapability.Resolve(fx.P.CellDir, "#Driven1", null, out var why);
        Assert.True(t is not null, why);
        Assert.True(t!.Is3D);
        Assert.Equal(path, t.C3dPath);
        Assert.Equal(Design + "#Driven1", t.SetupName);
        string listed = Assert.Single(EmCapability.SetupsFor(fx.P.CellDir), s => s.EndsWith(".c3d#Driven1", StringComparison.Ordinal));
        Assert.Equal(Design + "#Driven1", EmCapability.SetupLabel(listed));
        Assert.True(EmCapability.Resolve(fx.P.CellDir, EmCapability.SetupLabel(listed), null, out _)!.Is3D);   // the picker's spelling

        File.Delete(fx.Cem);
        Assert.True(EmCapability.Resolve(fx.P.CellDir, null, null, out _)!.Is3D);
    }

    /// <summary>
    /// D2 / D5 (§3 first bullet): at two values of Lx the 3D view elaborates two different geometries, and the copper it
    /// places is the planar path's regenerated layout exactly; the user's files are unchanged; a change of a VAR that moves
    /// nothing keeps the key.
    /// </summary>
    [Fact]
    public void TwoValues_TwoGeometries_WhoseCopperIsThePlanarPathsLayout()
    {
        var fx = Fixture("live");
        var (path, _) = ThreeDView(fx);
        var before = (fx.UserFileBytes(), File.ReadAllText(path));
        var planar = EmCapability.Resolve(fx.P.CellDir, Path.GetFileName(fx.Cem), null, out var pw)!;
        Assert.NotNull(planar);
        var three = EmCapability.Resolve(fx.P.CellDir, "#Driven1", null, out var tw);
        Assert.True(three is not null, tw);

        var spans = new List<double>();
        var keys = new List<string>();
        foreach (double lx in new[] { 6e-3, 8e-3 })
        {
            var gp = Geometry(planar, At(("Lx", lx)));
            var g3 = Geometry(three!, At(("Lx", lx)));
            Assert.Equal(LayoutPersistence.Serialize(gp.Source.View), LayoutPersistence.Serialize(g3.Source.View));
            var e = C3dProblemAssembly.Assemble(g3.ThreeD!.Setup, g3.ThreeD.Document, path, fx.Cws, g3.ThreeD.Elaborator);
            Assert.True(e.Ok, e.Refusal);
            var copper = e.Problem!.Solids.Where(s => s.Role == Em3dRole.Conductor && s.Name.StartsWith("U1/", StringComparison.Ordinal))
                                          .Select(s => Em3dProblem.Bounds(s.Primitive)).ToList();
            spans.Add(copper.Max(b => b.X1) - copper.Min(b => b.X0));
            keys.Add(g3.Key);
        }
        Assert.NotEqual(keys[0], keys[1]);
        Assert.Equal(2e-3, spans[1] - spans[0], 9);

        // spare drives nothing, in the schematic or the 3D view: the same geometry, the same key.
        var a = Geometry(three!, At(("Lx", 6e-3)));
        var b = Geometry(three!, At(("Lx", 6e-3), ("spare", 3e-3)));
        Assert.Equal(a.Key, b.Key);
        Assert.Equal(before, (fx.UserFileBytes(), File.ReadAllText(path)));
    }

    /// <summary>
    /// D2 / D3 (§3 second bullet): a flattened layout is reported as a copy; a VAR linked to the schematic drives a 3D-only
    /// body's dimension; the names that reach nothing are said; a VAR the schematic does not define is listed as not
    /// tunable from it.
    /// </summary>
    [Fact]
    public void Flattened_IsReported_ALinkedVarDrivesADimension_AndAnUnlinkedOneIsNotTunable()
    {
        var fx = Fixture("flat");
        File.Delete(fx.Cem);
        var (path, _) = ThreeDView(fx, (doc, p) =>
        {
            var e = C3dElaborator.ElaborateOnce(doc, p, fx.Cws);
            var (tech, _) = TechnologyResolver.ResolveForDocument(doc.TechRef, p, fx.Cws, new TechnologyCache());
            var flat = C3dHierarchy.FlattenOne(doc, p, 0, e, tech.Tech, null, fx.Cws);
            Assert.True(flat.Refusal is null, flat.Refusal);
            doc.Instances.Clear();
            doc.Objects.AddRange(flat.Objects);
            doc.Variables.Add(new C3dVariable { Name = "lidGap", Expression = "3", Unit = "Mm" });
            var spacer = new C3dBox { Name = "Spacer", Material = "Laminate", Min = new C3dPoint3(0, 0, 2_000_000), Size = new C3dPoint3(1_000_000, 1_000_000, 0) };
            spacer.Exprs = new() { ["Size"] = [null, null, new C3dExpr("w50", "Mm")] };
            doc.Objects.Add(spacer);
        });
        var t = EmCapability.Resolve(fx.P.CellDir, null, null, out var why);
        Assert.True(t is not null, why);

        var g = Geometry(t!, At(("w50", 1.5e-3)));
        var box = (C3dBox)g.ThreeD!.Document.Objects.Single(o => o.Name == "Spacer");
        Assert.Equal(1.5e-3 * 1e6 * g.ThreeD.Document.DbuPerMicron, box.Size.Z, 0);
        Assert.Contains("Group U1 was flattened from the layout; it follows w50 only where its dimensions use them.", g.Notes);
        Assert.Contains(g.Unreached, u => u.StartsWith("Lx does not reach", StringComparison.Ordinal));

        var (report, exit) = CircuitRF.Cli.ExplainTunables.Collect(fx.Csch, CircuitRF.Cli.DocumentKind.Schematic);
        Assert.Equal(0, exit);
        Assert.Contains(report!.ThreeDOnly!, l => l.StartsWith("VAR 'lidGap'", StringComparison.Ordinal) && l.Contains("add a VAR named lidGap"));
        Assert.DoesNotContain(report.ThreeDOnly!, l => l.Contains("'w50'"));
    }

    /// <summary>D6 (§3 third bullet): a conductor of the 3D view's own that sits on the line's end at the start leaves it when
    /// Lx shortens the line, and the signature says so — the difference that makes the point infeasible with no solve.</summary>
    [Fact]
    public void ABodyThatLeavesItsPad_ChangesTheConnectivitySignature()
    {
        var fx = Fixture("pin");
        File.Delete(fx.Cem);
        var (path, _) = ThreeDView(fx);
        var t = EmCapability.Resolve(fx.P.CellDir, null, null, out var why)!;
        Assert.True(t is not null, why);

        // Where the line ends at the start, from the start's own problem.
        var g0 = Geometry(t, At());
        var e0 = C3dProblemAssembly.Assemble(g0.ThreeD!.Setup, g0.ThreeD.Document, path, fx.Cws, g0.ThreeD.Elaborator);
        Assert.True(e0.Ok, e0.Refusal);
        var top = e0.Problem!.Solids.Where(s => s.Role == Em3dRole.Conductor && s.Name.StartsWith("U1/", StringComparison.Ordinal))
                                    .Select(s => Em3dProblem.Bounds(s.Primitive)).OrderByDescending(b => b.Z1).ThenByDescending(b => b.X1).First();
        int dbu = g0.ThreeD.Document.DbuPerMicron;
        long D(double m) => (long)Math.Round(m * 1e6 * dbu);
        var doc = C3dPersistence.LoadFromFile(path);
        doc.Objects.Add(new C3dBox
        {
            Name = "Pin", Material = "Copper",
            Min = new C3dPoint3(D(top.X1 - 0.4e-3), D((top.Y0 + top.Y1) / 2 - 0.2e-3), D(top.Z1)),
            Size = new C3dPoint3(D(0.35e-3), D(0.4e-3), D(0.5e-3)),
        });
        C3dPersistence.SaveToFile(path, doc);

        var start = Geometry(t, At()).Connectivity!;
        Assert.Contains(start.Bodies!, b => b.Split('+').Contains("Pin") && b.Contains("U1/"));
        var shorter = Geometry(t, At(("Lx", 6e-3))).Connectivity!;
        Assert.False(shorter.SameAs(start));
        Assert.Contains("'Pin' no longer touches", shorter.DifferenceFrom(start));
        Assert.True(Geometry(t, At(("Lx", 7.2e-3))).Connectivity!.SameAs(start));
    }

    /// <summary>D7 (§3 fourth bullet): the mesh settings each solver's input carries are the same at two points — Palace's
    /// refinement block, EMerge's settings block (every pass made), openEMS's merge distance — and the run says so.</summary>
    [Fact]
    public void TheMeshSettingsEachSolverIsGiven_AreTheSameAtTwoPoints()
    {
        var fx = Fixture("mesh");
        File.Delete(fx.Cem);
        var palace = Driven("P", Em3dSolverId.Palace);
        var emerge = Driven("E", Em3dSolverId.Emerge);
        emerge.Emerge = new CemEmerge { AdaptiveMesh = 2 };
        var openems = Driven("O", Em3dSolverId.OpenEms);
        var (path, _) = ThreeDView(fx, null, palace, emerge, openems);

        string Inputs(string setup, double lx)
        {
            var t = EmCapability.Resolve(fx.P.CellDir, "#" + setup, null, out var why);
            Assert.True(t is not null, why);
            var g = Geometry(t!, At(("Lx", lx)));
            Assert.Contains(g.ThreeD!.Notes, n => n.StartsWith("The mesh is held", StringComparison.Ordinal));
            var held = g.ThreeD.Setup;
            var e = C3dProblemAssembly.Assemble(held, g.ThreeD.Document, path, fx.Cws, g.ThreeD.Elaborator);
            Assert.True(e.Ok, e.Refusal);
            var problem = e.Problem!;
            switch (setup)
            {
                case "P":
                {
                    var settings = PalaceSettings.Resolve(held.Palace);
                    var low = GmshGeoWriter.Write(problem, settings);
                    Assert.True(low.Ok, low.Refusal);
                    var cfg = PalaceConfigWriter.Write(problem, low.Groups, settings);
                    Assert.True(cfg.Ok, cfg.Refusal);
                    using var json = JsonDocument.Parse(cfg.Json!);
                    var refinement = json.RootElement.GetProperty("Model").GetProperty("Refinement");
                    Assert.Equal(EmMeshHold.HeldPalaceTolerance, refinement.GetProperty("Tol").GetDouble());
                    return refinement.GetRawText();
                }
                case "E":
                {
                    var lowered = EmergeScriptWriter.Write(problem, EmergeSettings.Resolve(held.Emerge));
                    Assert.True(lowered.ProblemJson is not null, lowered.Refusal);
                    using var json = JsonDocument.Parse(lowered.ProblemJson!);
                    var settings = json.RootElement.GetProperty("settings");
                    Assert.True(settings.GetProperty("holdMesh").GetBoolean());
                    return settings.GetRawText();
                }
                default:
                {
                    Assert.NotNull(held.OpenEms?.MinCellUm);
                    var grid = FdtdGrid.Build(problem, CemOpenEms.ResolveGrid(held.OpenEms));
                    return grid.MinCellM.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                }
            }
        }

        foreach (var s in new[] { "P", "E", "O" })
            Assert.Equal(Inputs(s, 6e-3), Inputs(s, 8e-3));
    }

    /// <summary>D1: a '#' in a folder of the workspace's path, or in a .cem's own name, is part of the path — the picker's
    /// label of the planar setup resolves back to it — and a 3D view with two driven setups, named without one, is asked for
    /// one rather than said to have none.</summary>
    [Fact]
    public void ASetupSpelling_KeepsAHashInItsPath_AndAViewWithTwoSetupsAsksForOne()
    {
        Assert.Equal("amp#2", EmCapability.SetupLabel("/w/C#x/amp/em/amp#2.cem"));
        Assert.Equal("board#Driven1", EmCapability.SetupLabel("/w/C#x/amp/3d/board.c3d#Driven1"));

        var fx = Fixture("C#ws");
        var (path, _) = ThreeDView(fx, null, Driven("Driven1", Em3dSolverId.Palace), Driven("Driven2", Em3dSolverId.Palace));
        string cem = Assert.Single(EmCapability.SetupsFor(fx.P.CellDir), s => s.EndsWith(EmSetupPersistence.Extension, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("C#ws", cem, StringComparison.Ordinal);
        Assert.Equal(Path.GetFileNameWithoutExtension(fx.Cem), EmCapability.SetupLabel(cem));
        var planar = EmCapability.Resolve(fx.P.CellDir, EmCapability.SetupLabel(cem), null, out var why);
        Assert.True(planar is { Is3D: false }, why);
        var three = EmCapability.Resolve(fx.P.CellDir, Design + "#Driven2", null, out why);
        Assert.True(three is { Is3D: true, C3dSetup: "Driven2" }, why);

        Assert.Null(EmCapability.Resolve(fx.P.CellDir, path, null, out var ask));
        Assert.Contains("has 2 driven setups", ask);
        Assert.Contains("#Driven1", ask);
    }

    /// <summary>D5: a file the elaboration reads that the top document only references — here a placed part's own 3D
    /// view — is in the key by its content: editing it misses, though the cell's own files are unchanged.</summary>
    [Fact]
    public void TheKey_MovesWhenAPlacedPartsOwnFileChanges()
    {
        var fx = Fixture("child");
        File.Delete(fx.Cem);
        string lidDir = CellFolder.CreateCellFolder(fx.Workspace, "Lid");
        string? lid = null;
        ThreeDView(fx, (doc, p) =>
        {
            var (tech, _) = TechnologyResolver.ResolveForDocument(doc.TechRef, p, fx.Cws, new TechnologyCache());
            var part = new C3dDocument
            {
                TechRef = Path.GetRelativePath(CellFolder.SubFolderPath(lidDir, ViewType.ThreeD), tech.ResolvedPath!).Replace('\\', '/'),
                Objects = [new C3dBox { Name = "Lid", Material = "Laminate", Min = new C3dPoint3(0, 0, 2_000_000), Size = new C3dPoint3(1_000_000, 1_000_000, 1_000_000) }],
            };
            lid = CellCreate.WriteThreeDView(lidDir, "Lid", part);
            doc.Instances.Add(new C3dInstance
            {
                Name = "L1", View = C3dInstanceView.ThreeD,
                CellRef = Path.GetRelativePath(Path.GetDirectoryName(p)!, lidDir).Replace('\\', '/'),
            });
        });
        var t = EmCapability.Resolve(fx.P.CellDir, null, null, out var why);
        Assert.True(t is not null, why);

        string before = Geometry(t!, At()).Key;
        Assert.Equal(before, Geometry(t!, At()).Key);
        var lidDoc = C3dPersistence.LoadFromFile(lid!);
        ((C3dBox)lidDoc.Objects.Single()).Size = new C3dPoint3(1_000_000, 1_000_000, 10_000_000);
        C3dPersistence.SaveToFile(lid!, lidDoc);
        Assert.NotEqual(before, Geometry(t!, At()).Key);
    }

    /// <summary>D3/D4: a schematic VAR that states no unit but whose value is a length (w50/4) is a length in the 3D view —
    /// written in the view's unit by New 3D View from Layout, so the view alone resolves it as one, and bound as base SI at
    /// an evaluation's point.</summary>
    [Fact]
    public void AUnitlessVarWhoseValueIsALength_IsALengthInTheView_AndAtAPoint()
    {
        var fx = Fixture("gap", new EditableParameter { Name = "gap", Expression = "w50/4", Unit = "" });
        File.Delete(fx.Cem);
        var (path, made) = ThreeDView(fx, (doc, _) =>
        {
            var spacer = new C3dBox { Name = "Spacer", Material = "Laminate", Min = new C3dPoint3(0, 0, 2_000_000), Size = new C3dPoint3(1_000_000, 1_000_000, 0) };
            spacer.Exprs = new() { ["Size"] = [null, null, new C3dExpr("gap", "Mm")] };
            doc.Objects.Add(spacer);
        });
        Assert.Equal(C3dUnits.Stored(made.Document!.DisplayUnit), made.Document.Variables.Single(v => v.Name == "gap").Unit);
        static long Z(C3dDocument d) => ((C3dBox)d.Objects.Single(o => o.Name == "Spacer")).Size.Z;

        var alone = C3dPersistence.LoadFromFile(path);
        var resolution = C3dResolver.Resolve(alone, C3dCell.Of(path));
        Assert.True(resolution.Errors.Count == 0, string.Join(" ", resolution.Errors));
        Assert.Equal(1.1e-3 / 4 * 1e6 * alone.DbuPerMicron, Z(alone), 0);

        var t = EmCapability.Resolve(fx.P.CellDir, null, null, out var why);
        Assert.True(t is not null, why);
        var g = Geometry(t!, At(("w50", 2e-3)));
        Assert.Equal(2e-3 / 4 * 1e6 * g.ThreeD!.Document.DbuPerMicron, Z(g.ThreeD.Document), 0);
    }

    /// <summary>D4: a port linked to a layout label but rotated in the 3D view stays where it is drawn, so a point that
    /// moves its label is refused rather than solved with the port off the line; at the design's own values it is not.</summary>
    [Fact]
    public void ARotatedPortWhoseLabelMoves_RefusesThePoint()
    {
        var fx = Fixture("rot");
        File.Delete(fx.Cem);
        ThreeDView(fx, (doc, _) => { foreach (var p in doc.Ports) p.Angle = 10; });
        var t = EmCapability.Resolve(fx.P.CellDir, null, null, out var why);
        Assert.True(t is not null, why);

        EmEvaluation.Geometry(t!, At(), out var atSaved);
        Assert.DoesNotContain("cannot follow", atSaved ?? "", StringComparison.Ordinal);
        Assert.Null(EmEvaluation.Geometry(t!, At(("Lx", 8e-3)), out var refusal));
        Assert.Contains("cannot follow its layout's ports", refusal);
        Assert.Contains("is rotated in the 3D view", refusal);
    }
}
