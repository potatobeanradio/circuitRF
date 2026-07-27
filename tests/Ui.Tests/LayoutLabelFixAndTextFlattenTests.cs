using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

// ── docs/sonnet-briefs/brief-layout-label-fix-and-text-flatten.md — gates 2-11 ──────────────────
//
// Gates 2, 5's pixel half, and part of 11 involve SkiaFonts.PlexRegular, which loads via Avalonia's
// AssetLoader and throws InvalidOperationException with no live Avalonia app host (confirmed
// empirically against this exact headless test project, matching LayoutRulerRendererTests.cs's own
// documented reason for the same constraint). Everywhere that matters, LayoutTextOutline accepts an
// internal TestOverrideTypeface (InternalsVisibleTo — see LayoutTextOutline.cs) so the REAL algorithm
// (glyph extraction, quad-to-cubic elevation, LayoutFlattener, Clipper2 nesting) still runs end to end
// headlessly, substituting SKTypeface.Default (guaranteed loadable, no asset system involved).

[Collection(CircuitRF.Ui.Tests.LayoutTextOutlineTypefaceCollection.Name)]
public class LayoutLabelFixAndTextFlattenTests : IDisposable
{
    private sealed class FakeMessageSink : IMessageSink
    {
        public List<(MessageLevel Level, string Text)> Posted { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Posted.Add((level, text));
        public void Clear() => Posted.Clear();
    }

    private static readonly LayerKey Layer1 = new(1, 0);

    private static LayoutView FreshModel(int dbuPerMicron = 1000) =>
        new() { DbuPerMicron = dbuPerMicron, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private static void Click(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default, long tolDbu = 40)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, tolDbu);
        vm.OnPointerReleased(wx, wy, mods);
    }

    /// <summary>Types <paramref name="text"/> with the Label tool and commits with Enter — the exact
    /// gesture sequence <c>LayoutCanvas</c> drives (press arms typing, TextInput appends, Enter commits).</summary>
    private static void TypeLabel(LayoutEditorViewModel vm, double wx, double wy, string text, double zoomPxPerDbu = 0)
    {
        vm.ActiveTool = LayoutEditorViewModel.Tool.Label;
        vm.OnPointerPressed(wx, wy, KeyModifiers.None, 1, 0, zoomPxPerDbu);
        foreach (var ch in text) vm.OnTextInput(ch.ToString());
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);
    }

    // Any test exercising the REAL flatten pipeline must substitute SKTypeface.Default — production
    // code never does this (LayoutTextOutline.TestOverrideTypeface is internal, InternalsVisibleTo-only).
    public LayoutLabelFixAndTextFlattenTests() => LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;
    public void Dispose() => LayoutTextOutline.TestOverrideTypeface = null;

    // ── Gate 3: default label height comes from the technology; falls back to 5 µm with none ──────

    [Theory]
    [InlineData(true)]  // Pcb2Layer
    [InlineData(false)] // MmicGaAs
    public void ApplyTechResolution_SeedsLabelHeight_FromTechnologyDefault(bool pcb)
    {
        var tech = pcb ? StarterTechnologies.Pcb2Layer() : StarterTechnologies.MmicGaAs();
        var vm = new LayoutEditorViewModel(FreshModel());

        vm.ApplyTechResolution(new TechResolution(tech, "/ws/tech/t.ctech", TechResolutionSource.WorkspaceDefault, []));

        Assert.Equal(tech.DefaultLabelHeightDbu, vm.CurrentLabelHeightDbu);
    }

    [Fact]
    public void ApplyTechResolution_NoTechnology_FallsBackToFiveMicrons()
    {
        var vm = new LayoutEditorViewModel(FreshModel());
        vm.ApplyTechResolution(new TechResolution(null, null, TechResolutionSource.None, []));

        Assert.Equal(5_000, vm.CurrentLabelHeightDbu);
    }

    [Fact]
    public void ApplyTechResolution_LaterTechnologyChange_DoesNotReSeed_OnceAlreadyResolved()
    {
        var vm = new LayoutEditorViewModel(FreshModel());
        var pcb = StarterTechnologies.Pcb2Layer();
        vm.ApplyTechResolution(new TechResolution(pcb, "/t.ctech", TechResolutionSource.WorkspaceDefault, []));
        Assert.Equal(pcb.DefaultLabelHeightDbu, vm.CurrentLabelHeightDbu);

        vm.CommitLabelHeightText("2mm"); // the user's own edit, session state

        var mmic = StarterTechnologies.MmicGaAs();
        vm.ApplyTechResolution(new TechResolution(mmic, "/t2.ctech", TechResolutionSource.LayoutRef, []));

        // A later technology resolution must not clobber the user's typed value — the same rule
        // DisplayUnit/SnapDbu already follow, extended to the label-height default.
        Assert.Equal(LayoutUnits.ToDbu(2m, LayoutUnit.Mm, vm.Model.DbuPerMicron), vm.CurrentLabelHeightDbu);
    }

    // ── Gate 2 (the headline): on the PCB starter technology, a placed label is visible ─────────────

    [Fact]
    public void PlacedLabel_OnPcbTechnology_AtDefaultViewport_RendersAtLeastAFewDevicePixelsTall()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var model = new LayoutView { DbuPerMicron = LayoutUnits.DefaultDbuPerMicron, DisplayUnit = tech.DefaultDisplayUnit, SnapDbu = tech.DefaultSnapDbu };
        var vm = new LayoutEditorViewModel(model);
        vm.ApplyTechResolution(new TechResolution(tech, "/ws/tech/pcb.ctech", TechResolutionSource.WorkspaceDefault, []));

        TypeLabel(vm, 0, 0, "REF");
        var label = Assert.IsType<LabelShape>(model.Shapes[0]);

        // Mirrors LayoutCanvas.OnLayoutUpdated's initial-fit: a brand-new layout starts at
        // LayoutViewport.Default, framing ~200 snap steps across a representative window width.
        var vp = LayoutViewport.Default(width: 1000, height: 700, snapDbu: model.SnapDbu, dbuPerMicron: model.DbuPerMicron);

        double pixelHeight = label.Height * vp.Zoom;
        Assert.True(pixelHeight >= 4.0,
            $"label height {label.Height} DBU at zoom {vp.Zoom} px/DBU renders {pixelHeight} device px — " +
            "this is exactly the class of bug the hardcoded-5um default caused on PCB technologies.");
    }

    [Fact]
    public void PlacedLabel_OldHardcodedFiveMicronDefault_WouldHaveBeenSubPixel_OnPcbTechnology()
    {
        // Direct regression pin: proves the OLD constant (5000 DBU, correct only near 1000 DBU/um)
        // really was sub-pixel on the PCB starter tech's own default viewport — the exact failure the
        // brief describes, kept as a permanent negative-control alongside the positive gate above.
        var tech = StarterTechnologies.Pcb2Layer();
        var vp = LayoutViewport.Default(width: 1000, height: 700, snapDbu: tech.DefaultSnapDbu, dbuPerMicron: LayoutUnits.DefaultDbuPerMicron);

        const long oldHardcodedDefault = 5_000;
        double pixelHeight = oldHardcodedDefault * vp.Zoom;
        Assert.True(pixelHeight < 4.0, $"expected the old constant to be sub-pixel, was {pixelHeight}px");
    }

    // ── Gate 4: DefaultLabelHeightDbu round-trips in .ctech; an old file without it still loads ────

    [Fact]
    public void Ctech_DefaultLabelHeightDbu_RoundTrips()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var json1 = TechPersistence.Serialize(tech);
        var restored = TechPersistence.Deserialize(json1);

        Assert.Equal(tech.DefaultLabelHeightDbu, restored.DefaultLabelHeightDbu);
        Assert.Equal(json1, TechPersistence.Serialize(restored));
    }

    [Fact]
    public void Ctech_WithoutDefaultLabelHeightDbuField_StillLoads_DefaultsToZero()
    {
        var json = TechPersistence.Serialize(StarterTechnologies.Pcb2Layer());
        var stripped = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        stripped.Remove("DefaultLabelHeightDbu");

        var restored = TechPersistence.Deserialize(stripped.ToJsonString());

        Assert.Equal(0, restored.DefaultLabelHeightDbu); // 0 = "unset" -> VM's 5 um fallback applies
    }

    // ── Gate 5: typing status hint appears/clears; the min-pixel ghost-height ARITHMETIC boosts ────

    [Fact]
    public void TypingLabel_ShowsHint_AndClearsOnCommit()
    {
        var vm = new LayoutEditorViewModel(FreshModel()) { ActiveTool = LayoutEditorViewModel.Tool.Label };

        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 0, 0);
        Assert.True(vm.IsTypingLabel);
        Assert.Contains("Typing label", vm.DrawReadoutText);
        Assert.Contains("Enter to commit", vm.DrawReadoutText);
        Assert.Contains("Esc to cancel", vm.DrawReadoutText);

        vm.OnTextInput("Q");
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);

        Assert.False(vm.IsTypingLabel);
        Assert.Equal("", vm.DrawReadoutText);
    }

    [Fact]
    public void TypingLabel_ShowsHint_AndClearsOnEscapeCancel()
    {
        var vm = new LayoutEditorViewModel(FreshModel()) { ActiveTool = LayoutEditorViewModel.Tool.Label };

        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 0, 0);
        Assert.True(vm.IsTypingLabel);

        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.False(vm.IsTypingLabel);
        Assert.Equal("", vm.DrawReadoutText);
    }

    [Fact]
    public void TypingLabel_TooSmallForZoom_NotesItInTheHint()
    {
        var vm = new LayoutEditorViewModel(FreshModel()) { ActiveTool = LayoutEditorViewModel.Tool.Label };
        // 5000 DBU (fallback default, no technology resolved) at a very low zoom -> well under the
        // renderer's 8-device-pixel ghost floor.
        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 0, zoomPxPerDbu: 1e-6);

        Assert.Contains("smaller than the current zoom can show", vm.DrawReadoutText);
    }

    [Fact]
    public void TypingLabel_LargeEnoughForZoom_OmitsTheNote()
    {
        var vm = new LayoutEditorViewModel(FreshModel()) { ActiveTool = LayoutEditorViewModel.Tool.Label };
        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 0, zoomPxPerDbu: 1.0); // 5000 DBU * 1.0 = 5000 px

        Assert.DoesNotContain("smaller than the current zoom", vm.DrawReadoutText);
    }

    [Fact]
    public void EffectiveVisibleLabelHeightDbu_BelowFloor_BoostsToExactlyTheFloor()
    {
        // Pure arithmetic (no SkiaFonts/canvas involved — see LayoutRenderer.EffectiveVisibleLabelHeightDbu's
        // own doc comment for why this is split out): a label rendering at ~1 device pixel is boosted
        // to exactly LayoutRenderer.MinVisibleLabelDevicePixels.
        const long heightDbu = 5_000;
        const double zoomPxPerDbu = 0.0002; // 5000*0.0002 = 1 px, well under the floor

        long boosted = LayoutRenderer.EffectiveVisibleLabelHeightDbu(heightDbu, zoomPxPerDbu);
        double boostedPixels = boosted * zoomPxPerDbu;

        Assert.True(boosted > heightDbu);
        Assert.Equal(LayoutRenderer.MinVisibleLabelDevicePixels, boostedPixels, 3);
    }

    [Fact]
    public void EffectiveVisibleLabelHeightDbu_AlreadyVisible_ReturnsUnchanged()
    {
        const long heightDbu = 1_000_000;
        const double zoomPxPerDbu = 0.005; // 1,000,000*0.005 = 5000 px — nowhere near the floor

        Assert.Equal(heightDbu, LayoutRenderer.EffectiveVisibleLabelHeightDbu(heightDbu, zoomPxPerDbu));
    }

    [Fact]
    public void EffectiveVisibleLabelHeightDbu_UnknownZoom_ReturnsUnchanged()
    {
        // 0 (the default when a caller doesn't pass zoomPxPerDbu) must never boost — matches every
        // existing caller/test that predates this parameter.
        Assert.Equal(5_000, LayoutRenderer.EffectiveVisibleLabelHeightDbu(5_000, 0));
    }

    // ── Committed-label visibility floor (owner report: a label could still "disappear" the instant
    //    Enter was pressed, even though the in-progress ghost was already boosted) ───────────────────

    [Fact]
    public void CommitLabel_AtAZoomedOutView_CommitsAVisibleHeight_NotTheRawTechnologyDefault()
    {
        var vm = new LayoutEditorViewModel(FreshModel()) { ActiveTool = LayoutEditorViewModel.Tool.Label };
        // Zoomed out far enough that the 5000 DBU fallback default would render sub-pixel.
        const double zoomPxPerDbu = 1e-6;
        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 0, zoomPxPerDbu);
        vm.OnTextInput("REF");
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);

        var label = Assert.IsType<LabelShape>(vm.Model.Shapes[0]);
        double pixelHeight = label.Height * zoomPxPerDbu;
        Assert.True(pixelHeight >= LayoutRenderer.MinVisibleLabelDevicePixels - 0.01,
            $"committed label height {label.Height} DBU at zoom {zoomPxPerDbu} renders {pixelHeight}px — should never disappear on commit.");
    }

    [Fact]
    public void CommitLabel_AtAReasonableZoom_KeepsTheTechnologyDefaultUnboosted()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var model = new LayoutView { DbuPerMicron = LayoutUnits.DefaultDbuPerMicron, DisplayUnit = tech.DefaultDisplayUnit, SnapDbu = tech.DefaultSnapDbu };
        var vm = new LayoutEditorViewModel(model);
        vm.ApplyTechResolution(new TechResolution(tech, "/t.ctech", TechResolutionSource.WorkspaceDefault, []));
        var vp = LayoutViewport.Default(width: 1000, height: 700, snapDbu: model.SnapDbu, dbuPerMicron: model.DbuPerMicron);

        vm.ActiveTool = LayoutEditorViewModel.Tool.Label;
        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 0, vp.Zoom);
        vm.OnTextInput("REF");
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);

        var label = Assert.IsType<LabelShape>(model.Shapes[0]);
        Assert.Equal(tech.DefaultLabelHeightDbu, label.Height); // already comfortably visible -> unboosted
    }

    [Fact]
    public void CommitLabel_NoZoomSupplied_KeepsTheRawDefault_MatchingEveryPriorTestAndCaller()
    {
        var vm = new LayoutEditorViewModel(FreshModel()) { ActiveTool = LayoutEditorViewModel.Tool.Label };
        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 0); // zoomPxPerDbu defaults to 0
        vm.OnTextInput("REF");
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);

        var label = Assert.IsType<LabelShape>(vm.Model.Shapes[0]);
        Assert.Equal(5_000, label.Height);
    }

    // ── Gate 6: Space is an ordinary character while typing, and does not arm the pan modifier ──────

    [Fact]
    public void OnTextInput_Space_IsAppendedToTheLabelBuffer_WhileTyping()
    {
        // The condition LayoutCanvas.OnKeyDown guards Space on (IsTypingLabel) — verified at the VM
        // level, which IS drivable headlessly; LayoutCanvas itself is a Control and cannot be
        // constructed in this test project (src/Ui/CLAUDE.md's standing note).
        var vm = new LayoutEditorViewModel(FreshModel()) { ActiveTool = LayoutEditorViewModel.Tool.Label };
        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 0, 0);

        vm.OnTextInput("A");
        vm.OnTextInput(" ");
        vm.OnTextInput("B");
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);

        var label = Assert.IsType<LabelShape>(vm.Model.Shapes[0]);
        Assert.Equal("A B", label.Text);
    }

    [Fact]
    public void LayoutCanvas_Source_GuardsSpaceOnIsTypingLabel()
    {
        // Structural/source-level pin (LayoutCanvas is a Control and cannot be constructed headlessly —
        // matches every prior Layout Editor phase's precedent for canvas-level gesture code): asserts
        // the Space branch is gated on IsTypingLabel, not that the gesture visibly works end to end.
        string path = FindSourceFile("LayoutCanvas.cs");
        string src = File.ReadAllText(path);
        Assert.Contains("Key.Space && _viewModel?.IsTypingLabel != true", src);
    }

    // ── Gates 7/11: text-to-polygon flattening — hole counts, nesting via Clipper2, same font ──────

    private static LayoutEditorViewModel MakeVmWithLabel(string text, out LabelShape label, bool isPort = false)
    {
        var model = FreshModel(dbuPerMicron: 1000);
        label = new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = text, Height = 1_000_000, Rotation = LayoutRotation.R0, IsPort = isPort };
        model.Shapes.Add(label);
        return new LayoutEditorViewModel(model);
    }

    [Fact]
    public void Flatten_LetterO_ProducesOnePolygonWithOneHole()
    {
        var vm = MakeVmWithLabel("O", out _);
        Click(vm, 500_000, 300_000); // well inside the glyph's placement bbox at this height/anchor
        vm.SelectAllCommand.Execute(null);

        vm.FlattenSelectionToPolygon(2_000);

        Assert.Single(vm.Model.Shapes);
        var poly = Assert.IsType<PolygonShape>(vm.Model.Shapes[0]);
        Assert.NotNull(poly.Holes);
        Assert.Single(poly.Holes!);
    }

    [Fact]
    public void Flatten_DigitEight_ProducesOnePolygonWithTwoHoles()
    {
        var vm = MakeVmWithLabel("8", out _);
        vm.SelectAllCommand.Execute(null);

        vm.FlattenSelectionToPolygon(2_000);

        var poly = Assert.IsType<PolygonShape>(Assert.Single(vm.Model.Shapes));
        Assert.NotNull(poly.Holes);
        Assert.Equal(2, poly.Holes!.Count);
    }

    [Fact]
    public void Flatten_LowercaseI_ProducesTwoSeparatePolygons_NeitherWithHoles()
    {
        var vm = MakeVmWithLabel("i", out _);
        vm.SelectAllCommand.Execute(null);

        vm.FlattenSelectionToPolygon(2_000);

        Assert.Equal(2, vm.Model.Shapes.Count);
        foreach (var s in vm.Model.Shapes)
        {
            var poly = Assert.IsType<PolygonShape>(s);
            Assert.True(poly.Holes is null || poly.Holes.Count == 0);
        }
    }

    [Fact]
    public void Flatten_Label_OutlineBoundingBox_MatchesRawGlyphContours_WithinTolerance()
    {
        var label = new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "A", Height = 1_000_000, Rotation = LayoutRotation.R0 };
        var rawContours = LayoutTextOutline.BuildGlyphContours(label);
        var rawBbox = Bbox.Empty;
        foreach (var c in rawContours) rawBbox = rawBbox.Union(LayoutGeometry.BboxOf(c));

        var polys = LayoutTextFlatten.FlattenContoursToPolygons(rawContours, tolDbu: 2_000, Layer1, null);
        var flatBbox = Bbox.Empty;
        foreach (var p in polys) flatBbox = flatBbox.Union(LayoutGeometry.BboxOf(p));

        const long tol = 5_000; // generous vs. the 2000 DBU flatten tolerance itself
        Assert.True(Math.Abs(rawBbox.MinX - flatBbox.MinX) <= tol);
        Assert.True(Math.Abs(rawBbox.MinY - flatBbox.MinY) <= tol);
        Assert.True(Math.Abs(rawBbox.MaxX - flatBbox.MaxX) <= tol);
        Assert.True(Math.Abs(rawBbox.MaxY - flatBbox.MaxY) <= tol);
    }

    [Fact]
    public void Gate11_LayoutRendererAndLayoutTextOutline_ResolveTheSameTypeface()
    {
        // DrawLabelText and BuildGlyphContours cannot both actually be CALLED in this environment
        // (SkiaFonts.PlexRegular requires a live Avalonia app host) — this is the structural pin every
        // prior untestable-at-runtime feature in this codebase uses instead. Post owner-follow-up
        // (LabelShape.Style), both now call the SAME shared mapping function,
        // LayoutTextOutline.ResolveTypeface — an even stronger guarantee than matching font constants
        // independently, since there is only one place a style-to-typeface mapping could ever drift.
        string rendererSrc = File.ReadAllText(FindSourceFile("LayoutRenderer.cs"));
        string outlineSrc = File.ReadAllText(FindSourceFile("LayoutTextOutline.cs"));

        Assert.Contains("new SKFont(LayoutTextOutline.ResolveTypeface(label.Style), sizeUm)", rendererSrc);
        Assert.Contains("new SKFont(typeface ?? ResolveTypeface(label.Style), label.Height)", outlineSrc);
    }

    // ── Gate 8: port labels are refused — disabled alone, silently skipped + reported when mixed ────

    [Fact]
    public void FlattenAvailability_SolePortLabelSelected_DisabledWithReason()
    {
        var vm = MakeVmWithLabel("REF", out _, isPort: true);
        vm.SelectAllCommand.Execute(null);

        var avail = vm.FlattenAvailability;
        Assert.False(avail.CanExecute);
        Assert.NotNull(avail.DisabledReason);
    }

    [Fact]
    public void FlattenSelectionToPolygon_PortLabelNeverFlattens_EvenWhenExplicitlySelected()
    {
        var model = FreshModel();
        var portLabel = new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "G", Height = 1_000_000, IsPort = true };
        model.Shapes.Add(portLabel);
        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink);
        vm.SelectAllCommand.Execute(null);

        vm.FlattenSelectionToPolygon(2_000);

        Assert.Single(vm.Model.Shapes);
        Assert.Same(portLabel, vm.Model.Shapes[0]); // untouched — still the original LabelShape instance
        Assert.Contains(sink.Posted, p => p.Text.Contains("port label"));
    }

    [Fact]
    public void FlattenSelectionToPolygon_MixedSelection_FlattensOthers_AndReportsPortLabelSkip()
    {
        var model = FreshModel();
        var circle = new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 10_000 };
        var portLabel = new LabelShape { Layer = Layer1, X = 100_000, Y = 0, Text = "S", Height = 1_000_000, IsPort = true };
        model.Shapes.Add(circle);
        model.Shapes.Add(portLabel);
        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink);
        vm.SelectAllCommand.Execute(null);

        vm.FlattenSelectionToPolygon(2_000);

        Assert.Equal(2, vm.Model.Shapes.Count);
        Assert.Contains(vm.Model.Shapes, s => s is PolygonShape);         // the circle flattened
        Assert.Contains(vm.Model.Shapes, s => ReferenceEquals(s, portLabel)); // the port label untouched
        Assert.Contains(sink.Posted, p => p.Text.Contains("port label"));
    }

    // ── Gate 9: enablement — label enabled, circle enabled, Rect disabled with the existing reason ──

    [Fact]
    public void FlattenAvailability_NonPortLabel_Enabled()
    {
        var vm = MakeVmWithLabel("X", out _);
        vm.SelectAllCommand.Execute(null);
        Assert.True(vm.FlattenAvailability.CanExecute);
    }

    [Fact]
    public void FlattenAvailability_Circle_Enabled()
    {
        var model = FreshModel();
        model.Shapes.Add(new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 10_000 });
        var vm = new LayoutEditorViewModel(model);
        vm.SelectAllCommand.Execute(null);
        Assert.True(vm.FlattenAvailability.CanExecute);
    }

    [Fact]
    public void FlattenAvailability_Rect_Disabled_WithExistingReasonString()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        var vm = new LayoutEditorViewModel(model);
        vm.SelectAllCommand.Execute(null);

        var avail = vm.FlattenAvailability;
        Assert.False(avail.CanExecute);
        Assert.Equal("No curved shapes in selection", avail.DisabledReason);
    }

    // ── Gate 10: undo — one entry, byte-identical restore at the original index ─────────────────────

    [Fact]
    public void Flatten_Label_UndoRestoresTheOriginalLabelShape_AtItsOriginalIndex()
    {
        var model = FreshModel();
        var before = new RectShape { Layer = Layer1, X1 = -50_000, Y1 = -50_000, X2 = -10_000, Y2 = -10_000 };
        var label = new LabelShape { Layer = Layer1, X = 0, Y = 0, Text = "O", Height = 1_000_000, Rotation = LayoutRotation.R0 };
        model.Shapes.Add(before);  // index 0
        model.Shapes.Add(label);   // index 1
        var jsonBefore = LayoutPersistence.Serialize(model);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500_000, 300_000); // the label only

        vm.FlattenSelectionToPolygon(2_000);
        Assert.True(vm.UndoRedo.CanUndo);
        Assert.Equal(2, model.Shapes.Count);
        Assert.IsType<RectShape>(model.Shapes[0]);
        Assert.IsType<PolygonShape>(model.Shapes[1]);

        vm.UndoRedo.Undo();

        Assert.Equal(jsonBefore, LayoutPersistence.Serialize(model));
        Assert.Same(label, model.Shapes[1]);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private static string FindSourceFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("Could not locate repo root from test base directory.");

        var found = Directory.GetFiles(Path.Combine(dir.FullName, "src"), fileName, SearchOption.AllDirectories);
        return Assert.Single(found);
    }
}
