using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gates for brief-housekeeping-tearoff-palette-repo.md §4 — TermG: Term with port 2 permanently
/// grounded, presenting as a 1-port. R-hk-6 (reuse Term's model), R-hk-7 (reuse both glyphs
/// verbatim, no redraw/resize), R-hk-8 (placed bbox matches Term + GND placed separately).
/// </summary>
public class TermGComponentTests
{
    private static SchematicViewModel MakeVm() => new(new SchematicEditModel());

    [Fact]
    public void TermG_IsA1Port_UsingTermsPort1Identity()
    {
        var ports = SymbolPortDefs.For(SymbolKind.TermG);
        var termPorts = SymbolPortDefs.For(SymbolKind.Term);
        Assert.Single(ports);
        Assert.Equal(termPorts[0], ports[0]); // same name + local position as Term's port 1
    }

    [Fact]
    public void TermG_EngineReference_IsSameAsTerm_NoParallelModel()
    {
        Assert.Equal("Port", ComponentTypeRegistry.EngineReference(SymbolKind.Term));
        Assert.Equal("Port", ComponentTypeRegistry.EngineReference(SymbolKind.TermG));
    }

    [Fact]
    public void TermG_DefaultParameters_MatchTerms()
    {
        var term  = ComponentTypeRegistry.DefaultParameters(SymbolKind.Term, 0);
        var termG = ComponentTypeRegistry.DefaultParameters(SymbolKind.TermG, 0);
        Assert.Equal(term.Select(p => (p.Name, p.Expression, p.Unit)), termG.Select(p => (p.Name, p.Expression, p.Unit)));
    }

    // ── R-hk-7: glyph reuse, no redraw, no resize ─────────────────────────────────────────────

    [Fact]
    public void TermG_Glyph_BeginsWithTermsOwnPrimitives_Unchanged()
    {
        var termPrims  = BuiltInSymbols.Primitives(SymbolKind.Term).Primitives;
        var termGPrims = BuiltInSymbols.Primitives(SymbolKind.TermG).Primitives;

        Assert.True(termGPrims.Count > termPrims.Count, "TermG must carry MORE primitives than Term (Term's own + translated Ground).");
        for (int i = 0; i < termPrims.Count; i++)
            Assert.Same(termPrims[i], termGPrims[i]); // literally the same objects — no redraw
    }

    [Fact]
    public void TermG_Glyph_CarriesGroundsPrimitiveCount_Appended()
    {
        var termPrims   = BuiltInSymbols.Primitives(SymbolKind.Term).Primitives;
        var groundPrims = BuiltInSymbols.Primitives(SymbolKind.Ground).Primitives;
        var termGPrims  = BuiltInSymbols.Primitives(SymbolKind.TermG).Primitives;

        Assert.Equal(termPrims.Count + groundPrims.Count, termGPrims.Count);
    }

    [Fact]
    public void TermG_GroundPortion_IsGroundsGeometry_TranslatedToTermsPort2_NotRescaled()
    {
        var groundPrims = BuiltInSymbols.Primitives(SymbolKind.Ground).Primitives;
        var termPrims   = BuiltInSymbols.Primitives(SymbolKind.Term).Primitives;
        var termGPrims  = BuiltInSymbols.Primitives(SymbolKind.TermG).Primitives;

        var appended = termGPrims.Skip(termPrims.Count).ToList();
        Assert.Equal(groundPrims.Count, appended.Count);

        for (int i = 0; i < groundPrims.Count; i++)
        {
            var g  = Assert.IsType<LinePrimitive>(groundPrims[i]);
            var g2 = Assert.IsType<LinePrimitive>(appended[i]);
            // Translated by exactly (0, +200) — Term's own port-2 local Y — never scaled (same
            // segment length/orientation, just shifted).
            Assert.Equal(g.X1, g2.X1, 6);
            Assert.Equal(g.Y1 + 200, g2.Y1, 6);
            Assert.Equal(g.X2, g2.X2, 6);
            Assert.Equal(g.Y2 + 200, g2.Y2, 6);
        }
    }

    // ── R-hk-8: combined bbox matches Term + GND placed separately ───────────────────────────

    [Fact]
    public void TermG_BoundingBox_MatchesTermPlusGndPlacedSeparately()
    {
        var termPrims   = BuiltInSymbols.Primitives(SymbolKind.Term).Primitives;
        var groundPrims = BuiltInSymbols.Primitives(SymbolKind.Ground).Primitives;
        var termGPrims  = BuiltInSymbols.Primitives(SymbolKind.TermG).Primitives;

        var termBb = SymbolGeometry.ComputeBb(termPrims);
        // A GND placed separately, wired to Term's port 2 (0,+200) with GND's own pin (local
        // origin) landing exactly there: translate Ground's bbox by (0, +200).
        var groundBbLocal = SymbolGeometry.ComputeBb(groundPrims);
        var groundBbAtPort2 = (
            MinX: groundBbLocal.MinX, MinY: groundBbLocal.MinY + 200,
            MaxX: groundBbLocal.MaxX, MaxY: groundBbLocal.MaxY + 200);

        var expected = (
            MinX: Math.Min(termBb.MinX, groundBbAtPort2.MinX),
            MinY: Math.Min(termBb.MinY, groundBbAtPort2.MinY),
            MaxX: Math.Max(termBb.MaxX, groundBbAtPort2.MaxX),
            MaxY: Math.Max(termBb.MaxY, groundBbAtPort2.MaxY));

        var actual = SymbolGeometry.ComputeBb(termGPrims);

        Assert.Equal(expected.MinX, actual.MinX, 6);
        Assert.Equal(expected.MinY, actual.MinY, 6);
        Assert.Equal(expected.MaxX, actual.MaxX, 6);
        Assert.Equal(expected.MaxY, actual.MaxY, 6);
    }

    // ── R-hk-6: electrical equivalence to Term + GND wired ────────────────────────────────────

    [Fact]
    public void TermG_Netlist_IdenticalToTerm_WithPort2WiredToGround()
    {
        // TermG alone.
        var vmG = MakeVm();
        vmG.CommitPlacement(SymbolKind.TermG, 0, SymbolRotation.R0, 0, 0);
        var resultG = NetExtractor.Extract(vmG.EditModel, "tbG");
        Assert.Empty(resultG.Conflicts);
        var instG = resultG.TestBench.Instances.Single();

        // Term + Ground, Ground's pin wired directly onto Term's port-2 world position.
        var vmT = MakeVm();
        vmT.CommitPlacement(SymbolKind.Term, 0, SymbolRotation.R0, 0, 0);
        vmT.CommitPlacement(SymbolKind.Ground, 0, SymbolRotation.R0, 0, 200); // Term's port 2 = (0,+200)
        var resultT = NetExtractor.Extract(vmT.EditModel, "tbT");
        Assert.Empty(resultT.Conflicts);
        var instT = resultT.TestBench.Instances.Single(i => i.Reference == "Port");

        Assert.Equal("Port", instG.Reference);
        Assert.Equal(instT.NetBindings, instG.NetBindings); // [signalNet, "0"] either way
        Assert.Equal(
            instT.Overrides.Select(o => (o.Name, o.Expression, o.Unit)),
            instG.Overrides.Select(o => (o.Name, o.Expression, o.Unit)));
    }

    // ── Palette ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TermG_AppearsInPalette_UnderTerminals()
    {
        Assert.Contains(LibraryCatalog.ByCategory(ComponentCategory.Terminals), i => i.Kind == SymbolKind.TermG);
    }

    // ── §1 (brief-misc-termg-units-technologies.md): TermG's Num must be uniqued everywhere Term's
    // is — paste, placement, and inline type-change. Paste and inline type-change were the two real
    // gaps (each had its own hand-typed SymbolKind test that included Term/P1Tone but not TermG);
    // placement already worked. All three now route through the single
    // ComponentTypeRegistry.OwnsUniquePortNum predicate. Duplicate (Ctrl/Cmd+D) and drag-copy do not
    // exist as Schematic Editor features at all (checked directly: no such command, binding, or
    // modifier-drag path exists anywhere in SchematicViewModel/SchematicCanvas/SchematicView) — so
    // there is no third and fourth entry point to gate here; only the two below plus placement.

    private static EditableComponent MakeTermG(string name, int num, double x = 0, double y = 0)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.TermG, X = x, Y = y };
        c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
        return c;
    }

    [Fact]
    public void OwnsUniquePortNum_TermIncludedTermGIncludedP1ToneIncluded_OthersNot()
    {
        Assert.True(ComponentTypeRegistry.OwnsUniquePortNum(SymbolKind.Term));
        Assert.True(ComponentTypeRegistry.OwnsUniquePortNum(SymbolKind.TermG));
        Assert.True(ComponentTypeRegistry.OwnsUniquePortNum(SymbolKind.P1Tone));
        Assert.False(ComponentTypeRegistry.OwnsUniquePortNum(SymbolKind.Pin)); // its own separate pool
        Assert.False(ComponentTypeRegistry.OwnsUniquePortNum(SymbolKind.Resistor));
    }

    // Paste — the bug as originally reported: pasting a TermG beside an existing TermG produced a
    // duplicate Num, because the old paste-side "PortFamily" set was [Term, P1Tone] only.
    [Fact]
    public void PasteNum_TermG_CollidesWithExistingTermG_GetsNextFreeNum()
    {
        var model  = new SchematicEditModel();
        model.Components.Add(MakeTermG("Term1", 1));
        var pasted = MakeTermG("Term2", 1);
        var cmd    = new SchematicPasteCommand(model, [pasted], [], []);
        cmd.Execute();

        var got = model.Components.Last().Parameters.First(p => p.Name == "Num").Expression;
        Assert.Equal("2", got);
    }

    // Mixed Term + TermG selection: both share the pool, so a pasted TermG must dodge an existing
    // Term's Num (and vice versa), not just its own kind.
    [Fact]
    public void PasteNum_MixedTermAndTermGSelection_BothDodgeEachOthersNums()
    {
        var model = new SchematicEditModel();
        model.Components.Add(MakeTermG("Term1", 1)); // existing TermG holds Num=1
        var pastedTermG = MakeTermG("Term2", 1);     // pasted TermG collides with it
        var pastedTerm  = new EditableComponent { InstanceName = "Term3", Symbol = SymbolKind.Term, X = 100 };
        pastedTerm.Parameters.Add(new EditableParameter { Name = "Num", Expression = "1" }); // pasted plain Term ALSO collides
        var cmd = new SchematicPasteCommand(model, [pastedTermG, pastedTerm], [], []);
        cmd.Execute();

        int n1 = int.Parse(model.Components[1].Parameters.First(p => p.Name == "Num").Expression);
        int n2 = int.Parse(model.Components[2].Parameters.First(p => p.Name == "Num").Expression);
        Assert.NotEqual(1, n1);
        Assert.NotEqual(1, n2);
        Assert.NotEqual(n1, n2); // and the two pasted-in-the-same-batch components don't collide either
    }

    // Placement — was already correct before this brief (CommitPlacement's own kind-check already
    // included TermG); pinned here as a permanent regression guard alongside the two real fixes above.
    [Fact]
    public void CommitPlacement_TermG_CollidesWithExistingTerm_GetsNextFreeNum()
    {
        var vm = MakeVm();
        vm.CommitPlacement(SymbolKind.Term, 0, SymbolRotation.R0, 0, 0);     // Num=1
        vm.CommitPlacement(SymbolKind.TermG, 0, SymbolRotation.R0, 100, 0); // must dodge it

        var nums = vm.EditModel.Components
            .Select(c => int.Parse(c.Parameters.First(p => p.Name == "Num").Expression))
            .ToList();
        Assert.Equal(2, nums.Distinct().Count());
    }

    // Inline type-change ("TG" typed over an existing component) — the second real gap: the old
    // check was "newKind == SymbolKind.Term" only, so converting a component's type to TermG kept
    // whatever placeholder Num DefaultParameters produced, silently duplicating an existing one.
    [Fact]
    public void CommitInlineEdit_TypeChangeToTermG_CollidesWithExistingTerm_GetsNextFreeNum()
    {
        var vm = MakeVm();
        var em = vm.EditModel;
        vm.CommitPlacement(SymbolKind.Term, 0, SymbolRotation.R0, 0, 0); // Num=1

        var src = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor, X = 100, Y = 0 };
        em.Components.Add(src);
        em.NotifyChanged();

        var hit = new SchematicHitTest.HitResult(SchematicHitTest.HitKind.ComponentType, src.Id);
        vm.BeginInlineEditForHit(hit, 0, 0);
        vm.InlineEditValue = "TG";
        vm.CommitInlineEdit();

        var newTermG = em.Components.Single(c => c.Symbol == SymbolKind.TermG);
        var num = newTermG.Parameters.First(p => p.Name == "Num").Expression;
        Assert.NotEqual("1", num); // must not collide with the pre-existing Term's Num=1
    }
}
