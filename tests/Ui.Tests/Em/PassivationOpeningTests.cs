// ================================================================
//  PassivationOpeningTests.cs — PSV-2: a dielectric that exists everywhere except where a layer is drawn
//
//  StackupLayer.OpenWithLayer names the mask a passivation's pad openings are drawn on. Every 3D route
//  cuts each opening out of the slab (one solid with hole rings, never an air solid ordered above it);
//  the planar medium is laterally infinite, so a planar run keeps the slab and says how much is open.
// ================================================================

using CircuitRF.Core.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Diagnostics;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using CircuitRF.Render;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests.Em;

public sealed class PassivationOpeningTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Metal = new(1, 0);
    private static readonly LayerKey PadOpen = new(2, 0);

    private static long Um(double v) => (long)Math.Round(v * Dbu);

    /// <summary>1 µm εᵣ 7 passivation / 3 µm gold / 100 µm GaAs / ground, the passivation opened by
    /// <paramref name="opening"/>.</summary>
    private static Technology Mmic(string? opening = "PadOpen") => new()
    {
        Name = "psv2", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = Um(1), DefaultFlattenTolDbu = Um(1),
        Layers =
        [
            new LayerDef { Key = Metal, Name = "Metal", ZOrder = 1, Purpose = "drawing" },
            new LayerDef { Key = PadOpen, Name = "PadOpen", ZOrder = 2, Purpose = "drawing" },
        ],
        Stackup = new Stackup
        {
            Top = BoundaryCondition.Open, Bottom = BoundaryCondition.Ground,
            Layers =
            [
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Passivation", ThicknessDbu = Um(1), Epsr = 7, Mur = 1,
                                   OpenWithLayer = opening },
                new StackupLayer { Kind = StackupKind.Conductor, Name = "TopMetal", ThicknessDbu = Um(3), SigmaSm = 4.1e7,
                                   DrawingLayers = [Metal] },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "GaAs", ThicknessDbu = Um(100), Epsr = 12.9, Mur = 1 },
                new StackupLayer { Kind = StackupKind.Conductor, Name = "Backside", ThicknessDbu = Um(3), SigmaSm = 4.1e7,
                                   IsGroundReference = true },
            ],
        },
    };

    private static RectShape Rect(LayerKey layer, double x1, double y1, double x2, double y2)
        => new() { Layer = layer, X1 = Um(x1), Y1 = Um(y1), X2 = Um(x2), Y2 = Um(y2) };

    /// <summary>The brief's fixture: a 100 × 100 µm pad and a 90 × 90 µm opening over it, one port.</summary>
    private static List<LayoutShape> Pad() =>
    [
        Rect(Metal, 0, 0, 100, 100),
        Rect(PadOpen, 5, 5, 95, 95),
        new LabelShape { Layer = Metal, X = 0, Y = Um(50), Text = "1", Height = Um(5), IsPort = true,
                         PortDirection = LayoutRotation.R0 },
    ];

    private static EmLayoutSource Source(Technology tech, List<LayoutShape> shapes)
    {
        var view = new LayoutView { DbuPerMicron = Dbu };
        view.Shapes.AddRange(shapes);
        return new EmLayoutSource("/nowhere/pad.clay", view, tech, Dbu);
    }

    private static Em3dGenerationResult Cem(Technology tech, List<LayoutShape> shapes)
    {
        var setup = new EmSetup
        {
            Name = "pad", LayoutRef = "pad/layout/pad.clay",
            Frequency = new FrequencySpec("1", "10", 4, SweepKind.Linear, "GHz", "GHz"),
            Solver3D = Em3dSolverId.Palace,
        };
        var r = Em3dGenerator.Generate(setup, Source(tech, shapes), tech);
        Assert.True(r.Ok, r.Refusal);
        return r;
    }

    /// <summary>The call a .c3d makes for a placed layout (C3dElaborator's child layout).</summary>
    private static Em3dLayoutSolidsResult Instance(Technology tech, List<LayoutShape> shapes)
    {
        var r = Em3dLayoutSolids.From(Source(tech, shapes), tech, null, new Em3dLayoutSolidsOptions(10e9, 20, Instance: true));
        Assert.Null(r.Refusal);
        return r;
    }

    // ── Gate 1 — the file ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheField_IsWrittenOnlyWhenSet_AndRoundTripsByteForByte()
    {
        string bare = TechPersistence.Serialize(Mmic(opening: null));
        Assert.DoesNotContain("OpenWithLayer", bare, StringComparison.Ordinal);
        Assert.Equal(bare, TechPersistence.Serialize(TechPersistence.Deserialize(bare)));

        string set = TechPersistence.Serialize(Mmic());
        Assert.Contains("\"OpenWithLayer\": \"PadOpen\"", set, StringComparison.Ordinal);
        Assert.Equal(set, TechPersistence.Serialize(TechPersistence.Deserialize(set)));
    }

    // ── Gate 2 — validation, one sentence per mistake ───────────────────────────────────────────────

    private static TechProblem Invalid(Technology t)
        => Assert.Single(TechValidation.Analyze(t), p => p.Id == TechValidation.Ids.OpenWithLayerInvalid);

    [Fact]
    public void Validation_AConductorName_SaysAnOpeningIsDrawnOnAMask()
    {
        var p = Invalid(Mmic(opening: "TopMetal"));
        Assert.Equal(DiagnosticSeverity.Error, p.Severity);
        Assert.Contains("drawn on a mask layer, not on a conductor", p.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_BothTiesOnOneEntry_NamesBoth()
    {
        var t = Mmic();
        t.Stackup.Layers[0].PresentWithLayer = "Metal";
        var p = Invalid(t);
        Assert.Equal(DiagnosticSeverity.Error, p.Severity);
        Assert.Contains("patterned with \"Metal\"", p.Message, StringComparison.Ordinal);
        Assert.Contains("opened by \"PadOpen\"", p.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_AMissingLayer_SaysItIsNoDrawingLayer()
    {
        var p = Invalid(Mmic(opening: "Nowhere"));
        Assert.Equal(DiagnosticSeverity.Error, p.Severity);
        Assert.Contains("not one of this technology's drawing layers", p.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TechValidation.Analyze(Mmic()), q => q.Id?.StartsWith("tech.stackup.open-with", StringComparison.Ordinal) == true);
    }

    // ── Gate 3 — the hole, in both routes ───────────────────────────────────────────────────────────

    [Fact]
    public void TheHole_IsOneRingMatchingTheOpening_AtTheSameCoordinates_FromACemAndInACdThreeD()
    {
        static IReadOnlyList<Point2> Hole(IEnumerable<Em3dSolid> solids)
        {
            var slab = Assert.Single(solids, s => s.Name == "Passivation");
            var prism = Assert.IsType<Em3dExtrudedPolygon>(slab.Primitive);
            return Assert.Single(prism.Holes);
        }

        var cem = Hole(Cem(Mmic(), Pad()).Problem!.Solids);
        var placed = Hole(Instance(Mmic(), Pad()).Solids);

        static (double, double, double, double) Box(IReadOnlyList<Point2> ring)
            => (ring.Min(p => p.X), ring.Min(p => p.Y), ring.Max(p => p.X), ring.Max(p => p.Y));
        var (x0, y0, x1, y1) = Box(cem);
        Assert.Equal(5e-6, x0, 9); Assert.Equal(5e-6, y0, 9);
        Assert.Equal(95e-6, x1, 9); Assert.Equal(95e-6, y1, 9);
        Assert.Equal(4, cem.Count);
        Assert.Equal(cem.OrderBy(p => p.X).ThenBy(p => p.Y), placed.OrderBy(p => p.X).ThenBy(p => p.Y));
    }

    // ── Gate 4 — exposed metal wins: only the pad and air are in the opening ────────────────────────

    [Fact]
    public void InTheOpening_AboveAndBesideThePadTop_ThereIsOnlyThePadsMetalAndAir()
    {
        var problem = Cem(Mmic(), Pad()).Problem!;
        // TopMetal 103..106 µm above the stack's bottom, passivation absorbed down to 103 µm and up to 107 µm.
        double bottom = problem.Solids.Where(s => s.Role == Em3dRole.Dielectric).Min(s => Em3dProblem.Bounds(s.Primitive).Item3);
        foreach (double zUm in new[] { 104.5, 106.5 })
            foreach (double xUm in new[] { 6.0, 50.0, 94.0 })
                foreach (double yUm in new[] { 6.0, 50.0, 94.0 })
                {
                    var q = new Point3(xUm * 1e-6, yUm * 1e-6, bottom + (zUm - 3) * 1e-6);
                    var there = problem.Solids.Where(s => Holds(s.Primitive, q)).ToList();
                    Assert.All(there, s => Assert.True(s.Role is Em3dRole.Conductor or Em3dRole.Air,
                                                       $"{s.Name} ({s.Role}) occupies ({xUm}, {yUm}, {zUm}) µm"));
                }
    }

    private static bool Holds(Em3dPrimitive prim, Point3 q) => prim switch
    {
        Em3dBox b => q.X >= b.Min.X && q.X <= b.Max.X && q.Y >= b.Min.Y && q.Y <= b.Max.Y && q.Z >= b.Min.Z && q.Z <= b.Max.Z,
        Em3dExtrudedPolygon e => q.Z >= e.ZBottom && q.Z <= e.ZTop && Inside(e.Outline, q) && !e.Holes.Any(h => Inside(h, q)),
        _ => Em3dProblem.Bounds(prim) is var (x0, y0, z0, x1, y1, z1) &&
             q.X >= x0 && q.X <= x1 && q.Y >= y0 && q.Y <= y1 && q.Z >= z0 && q.Z <= z1,
    };

    private static bool Inside(IReadOnlyList<Point2> ring, Point3 q)
        => PlanarPolygon.RingContains([.. ring.Select(p => new EmPoint(p.X, p.Y))], q.X, q.Y);

    // ── Gate 5 — a technology with no opening builds what it built before ──────────────────────────

    /// <summary>The field set but nothing drawn on its mask cuts nothing: the solid list is identical to the
    /// field unset, and only a note says why. Every code path PSV-2 added is behind the field.</summary>
    [Fact]
    public void WithNothingOpened_TheSolidsAreIdenticalToNoFieldAtAll()
    {
        var noMask = Pad().Where(s => s.Layer != PadOpen).ToList();
        var bare = Cem(Mmic(opening: null), noMask);
        var empty = Cem(Mmic(), noMask);
        Assert.Equal(Canonical(bare.Problem!.Solids), Canonical(empty.Problem!.Solids));
        Assert.Contains(empty.Notes, n => n.Contains("nothing is drawn there, so the slab is whole", StringComparison.Ordinal));
        Assert.DoesNotContain(bare.Notes, n => n.Contains("opened by", StringComparison.Ordinal));
    }

    private static string Canonical(IEnumerable<Em3dSolid> solids) => string.Join("\n", solids.Select(s =>
        $"{s.Name}|{s.Material}|{s.Role}|{s.Order}|" + s.Primitive switch
        {
            Em3dExtrudedPolygon e => $"{e.ZBottom:R},{e.ZTop:R}:{string.Join(";", e.Outline)}/{string.Join("/", e.Holes.Select(h => string.Join(";", h)))}",
            _ => s.Primitive.ToString(),
        }));

    // ── R-psv2-2 — the notes, and a wire landing in the opening ─────────────────────────────────────

    [Fact]
    public void TheRunSaysWhatWasCut_AndAnOpeningOverBareSubstrateIsANote()
    {
        var notes = Cem(Mmic(), [.. Pad(), Rect(PadOpen, 110, 0, 130, 20)]).Notes;
        Assert.Contains(notes, n => n.Contains("2 opening(s) cut through it, 8500 µm² in all", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Contains("1 of the 2 opening(s) on 'PadOpen' in 'Passivation' lie over no conductor",
                                               StringComparison.Ordinal));
    }

    /// <summary>A stacked pad — a lower metal under the pad's whole area — is metal under the opening. Its two levels
    /// overlap, and read as one even-odd region they cancelled exactly there, so the opening was called bare substrate.</summary>
    [Fact]
    public void AnOpeningOverAStackedPad_IsOverConductor()
    {
        var under = new LayerKey(3, 0);
        var tech = Mmic();
        tech.Layers.Add(new LayerDef { Key = under, Name = "Under", ZOrder = 3, Purpose = "drawing" });
        int gaas = tech.Stackup.Layers.FindIndex(l => l.Name == "GaAs");
        tech.Stackup.Layers.InsertRange(gaas,
        [
            new StackupLayer { Kind = StackupKind.Dielectric, Name = "Spacer", ThicknessDbu = Um(1), Epsr = 7, Mur = 1 },
            new StackupLayer { Kind = StackupKind.Conductor, Name = "UnderMetal", ThicknessDbu = Um(1), SigmaSm = 4.1e7,
                               DrawingLayers = [under] },
        ]);
        var notes = Cem(tech, [.. Pad(), Rect(under, 0, 0, 100, 100)]).Notes;
        Assert.Contains(notes, n => n.Contains("1 opening(s) cut through it", StringComparison.Ordinal));
        Assert.DoesNotContain(notes, n => n.Contains("lie over no conductor", StringComparison.Ordinal));
    }

    // ── Gate 6 — planar keeps the slab and says how much is open ────────────────────────────────────

    [Fact]
    public void Planar_TheCoverNoteStatesTheOpenFraction()
    {
        var r = PlanarExtractor.Extract(Pad(), Mmic(), Dbu, 1e9);
        Assert.True(r.Ok, r.Refusal);
        Assert.Contains(r.Notes, n => n.Contains("'Passivation' is opened by 'PadOpen' over 81 % of this layout's extent",
                                                 StringComparison.Ordinal));
        Assert.DoesNotContain(r.Notes, n => n.Contains("PadOpen", StringComparison.Ordinal) &&
                                            n.Contains("not metal", StringComparison.Ordinal));
    }

    // ── R-psv2-4 — the stackup drawing and the editor picker ────────────────────────────────────────

    [Fact]
    public void TheStackupDrawing_NotchesAndTagsTheOpenedBand()
    {
        var scene = StackupScene.Build(Mmic(), 900f);
        var notch = Assert.Single(scene.Openings);
        var band = scene.Bands.Single(b => b.Name == "Passivation").Rect;
        Assert.Equal((band.Top, band.Bottom), (notch.Rect.Top, notch.Rect.Bottom));
        Assert.Contains(scene.Labels, l => l.LayerName == "Passivation" && l.Text == StackupScene.OpenedText("PadOpen"));
        Assert.Empty(StackupScene.Build(Mmic(opening: null), 900f).Openings);
    }

    [Fact]
    public void TheEditorPicker_ListsDrawingLayersOnly_AndClearsPatternedWith()
    {
        var tech = Mmic(opening: null);
        tech.Stackup.Layers[0].PresentWithLayer = "TopMetal";
        var vm = new TechEditorViewModel(Path.Combine(Path.GetTempPath(), $"psv2-{Guid.NewGuid():N}.ctech"), tech);
        var row = vm.StackupLayers.Single(r => r.Layer.Name == "Passivation");
        Assert.Equal(new[] { "(none)", "Metal", "PadOpen" }, row.OpenWithChoices);

        row.SelectedOpenWith = "PadOpen";
        var entry = vm.Working.Stackup.Layers.Single(l => l.Name == "Passivation");
        Assert.Equal(("PadOpen", (string?)null), (entry.OpenWithLayer, entry.PresentWithLayer));

        vm.UndoRedo.Undo();
        entry = vm.Working.Stackup.Layers.Single(l => l.Name == "Passivation");
        Assert.Equal(((string?)null, "TopMetal"), (entry.OpenWithLayer, entry.PresentWithLayer));
    }
}
