using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Design.Cells;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The two current sources on the schematic side: ITone (<see cref="SymbolKind.CurrentToneSource"/>)
/// and the VCCS (<see cref="SymbolKind.Vccs"/>) — registry metadata, symbol geometry, pin ORDER
/// (which is the engine contract), and the netlist spellings they extract to.
/// </summary>
public class CurrentSourceComponentTests
{
    // ── Registry ──────────────────────────────────────────────────────────────

    [Fact]
    public void ITone_HasItsOwnDisplayNameEngineReferenceAndPrefix()
    {
        Assert.Equal("ITone",   ComponentTypeRegistry.DisplayName(SymbolKind.CurrentToneSource));
        Assert.Equal("I_1Tone", ComponentTypeRegistry.EngineReference(SymbolKind.CurrentToneSource));
        Assert.Equal("I",       ComponentTypeRegistry.Get(SymbolKind.CurrentToneSource).InstancePrefix);
        Assert.Equal(ComponentCategory.Sources, ComponentTypeRegistry.Get(SymbolKind.CurrentToneSource).Category);
    }

    [Fact]
    public void Vccs_HasItsOwnDisplayNameEngineReferenceAndPrefix()
    {
        Assert.Equal("VCCS", ComponentTypeRegistry.DisplayName(SymbolKind.Vccs));
        Assert.Equal("VCCS", ComponentTypeRegistry.EngineReference(SymbolKind.Vccs));
        Assert.Equal("G",    ComponentTypeRegistry.Get(SymbolKind.Vccs).InstancePrefix);
    }

    [Theory]
    [InlineData("ITone", SymbolKind.CurrentToneSource)]
    [InlineData("itone", SymbolKind.CurrentToneSource)]
    [InlineData("VCCS",  SymbolKind.Vccs)]
    [InlineData("G",     SymbolKind.Vccs)]
    public void TryParseCode_ResolvesTheNewCodes(string code, SymbolKind expected)
    {
        Assert.True(ComponentTypeRegistry.TryParseCode(code, out var kind, out _));
        Assert.Equal(expected, kind);
    }

    // ── Default parameters ────────────────────────────────────────────────────
    //
    // The amplitude keys are I/Idc, matching the I_1Tone factory — not V/Vdc, which the factory
    // would never look at and which would leave a placed ITone stamping nothing at all.
    [Fact]
    public void ITone_DefaultParameters_UseTheCurrentSourceKeys()
    {
        var ps = ComponentTypeRegistry.DefaultParameters(SymbolKind.CurrentToneSource, 0);
        Assert.Equal(["I", "Freq", "Phase", "Idc"], ps.Select(p => p.Name));
        Assert.DoesNotContain(ps, p => p.Name is "V" or "Vdc");
    }

    // A freshly placed ITone reads "I = 1 mA", and that mA must be a real thousandth. Until
    // 2026-08-29 every prefixed current unit was an IDENTITY unit and this default would have
    // stamped one AMP; the assertion pairs the declared unit with the scale it must carry, so the
    // two can never be separated again.
    [Fact]
    public void ITone_DefaultUnits_ArePrefixed_AndThosePrefixesActuallyScale()
    {
        var ps = ComponentTypeRegistry.DefaultParameters(SymbolKind.CurrentToneSource, 0);
        Assert.Equal("mA", ps.First(p => p.Name == "I").Unit);
        Assert.Equal("mA", ps.First(p => p.Name == "Idc").Unit);
        Assert.Equal(1e-3, CircuitRF.Core.Expressions.Units.Scale("mA"));

        var g = ComponentTypeRegistry.DefaultParameters(SymbolKind.Vccs, 0).Single();
        Assert.Equal("mS", g.Unit);
        Assert.Equal(1e-3, CircuitRF.Core.Expressions.Units.Scale("mS"));
    }

    [Fact]
    public void Vccs_DefaultParameters_AreTheSingleTransconductance()
    {
        var ps = ComponentTypeRegistry.DefaultParameters(SymbolKind.Vccs, 0);
        var g  = Assert.Single(ps);
        Assert.Equal("G", g.Name);
        Assert.Equal(UnitDimension.Conductance, g.Dimension);
        Assert.True(g.ShowOnSchematic);
    }

    // ── Pin order is the engine contract ──────────────────────────────────────
    //
    // VccsModel reads Nodes as [out+, out−, ctrl+, ctrl−]. Swapping either pair reverses the
    // source's sign and still solves, so the order is asserted rather than assumed.
    [Fact]
    public void Vccs_HasFourPins_InTheOrderTheModelReads()
    {
        var pins = SymbolPortDefs.For(SymbolKind.Vccs);
        Assert.Equal(["out+", "out-", "ctrl+", "ctrl-"], pins.Select(p => p.Name));
        Assert.Equal((0f, -200f),    (pins[0].LocalX, pins[0].LocalY));
        Assert.Equal((0f, +200f),    (pins[1].LocalX, pins[1].LocalY));
        Assert.Equal((-300f, -100f), (pins[2].LocalX, pins[2].LocalY));
        Assert.Equal((-300f, +100f), (pins[3].LocalX, pins[3].LocalY));
    }

    [Fact]
    public void ITone_IsTwoPinAndVertical_LikeTheVoltageToneSource()
    {
        var i = SymbolPortDefs.For(SymbolKind.CurrentToneSource);
        var v = SymbolPortDefs.For(SymbolKind.ToneSource);
        Assert.Equal(v.Select(p => (p.LocalX, p.LocalY)), i.Select(p => (p.LocalX, p.LocalY)));
    }

    // ── The glyphs ────────────────────────────────────────────────────────────

    // The arrow IS the direction cue and there is no other one, so its presence is a gate, not a
    // cosmetic detail: a filled polygon whose apex is nearer pin 1 than its base.
    [Fact]
    public void ITone_Glyph_CarriesAFilledArrowheadPointingAtPinOne()
    {
        var sym = BuiltInSymbols.Primitives(SymbolKind.CurrentToneSource);
        var head = Assert.Single(sym.Primitives.OfType<PolygonPrimitive>().Where(p => p.Filled));

        double tipY  = head.Points.Min(pt => pt[1]);   // most negative Y = nearest the top pin
        double baseY = head.Points.Max(pt => pt[1]);
        Assert.True(tipY < baseY, "the arrowhead must point toward pin 1 at (0,-200)");
        Assert.True(head.Points.Count(pt => pt[1] == baseY) == 2, "a three-point arrowhead has a flat base");
    }

    // The VCCS's arrow points DOWN, at out− — the opposite of ITone's, because a controlled
    // transconductance SINKS its current from out+ (the SPICE G element's own direction, and how a
    // small-signal gm source is drawn in every device model). Asserted against ITone's own
    // arrowhead in the same test so the two can never drift into agreeing by accident.
    [Fact]
    public void Vccs_Glyph_IsADiamondWithAnArrowheadPointingDownAtOutMinus()
    {
        var sym    = BuiltInSymbols.Primitives(SymbolKind.Vccs);
        var polys  = sym.Primitives.OfType<PolygonPrimitive>().ToList();
        var body   = Assert.Single(polys.Where(p => !p.Filled));
        var head   = Assert.Single(polys.Where(p =>  p.Filled));

        Assert.Equal(4, body.Points.Count);                       // the dependent-source diamond

        // Tip = the point furthest from the flat base. Pointing DOWN means the tip has the LARGEST y.
        double tipY  = head.Points.Max(pt => pt[1]);
        double baseY = head.Points.Min(pt => pt[1]);
        Assert.True(tipY > baseY, "the arrowhead must point toward out− at (0,+200)");
        Assert.Equal(2, head.Points.Count(pt => pt[1] == baseY));  // a three-point head, flat base

        // …and the other way round from ITone's.
        var iHead = BuiltInSymbols.Primitives(SymbolKind.CurrentToneSource)
            .Primitives.OfType<PolygonPrimitive>().Single(p => p.Filled);
        Assert.True(iHead.Points.Min(pt => pt[1]) < iHead.Points.Max(pt => pt[1]),
            "ITone's arrow points up; the VCCS's points down. If this ever fails, one of the two "
          + "was flipped without the other and the schematic now lies about a direction.");
        // The control leads stop short of the body — a touching lead would draw a connection the
        // device does not have.
        var ctrlLeads = sym.Primitives.OfType<LinePrimitive>()
            .Where(l => l.X1 <= -170 || l.X2 <= -170).ToList();
        Assert.Equal(2, ctrlLeads.Count);
        Assert.All(ctrlLeads, l => Assert.True(System.Math.Max(l.X1, l.X2) <= -170,
            "a control lead must not reach the diamond's left vertex at x=-90"));
    }

    // ── Palette ───────────────────────────────────────────────────────────────

    [Fact]
    public void BothAppearInThePalette_AndITone_SitsNextToVTone()
    {
        var kinds = LibraryCatalog.AllItems.Select(i => i.Kind).ToList();
        Assert.Contains(SymbolKind.CurrentToneSource, kinds);
        Assert.Contains(SymbolKind.Vccs, kinds);

        var pinned = LibraryCatalog.AllItemsPinnedOrder().Select(i => i.Kind).ToList();
        Assert.Equal(pinned.IndexOf(SymbolKind.ToneSource) + 1,
                     pinned.IndexOf(SymbolKind.CurrentToneSource));
    }

    [Fact]
    public void Search_FindsBothByName()
    {
        Assert.Contains(LibraryCatalog.Search("ITone"), i => i.Kind == SymbolKind.CurrentToneSource);
        Assert.Contains(LibraryCatalog.Search("VCCS"),  i => i.Kind == SymbolKind.Vccs);
    }

    // ── Indexed tone groups ───────────────────────────────────────────────────

    // The "+" button's template for an ITone must extend I[n], not V[n]: the I_nTone factory reads
    // I[n] and a V[n] tone would be silently absent from the source.
    [Fact]
    public void ITone_UserParamTemplate_ExtendsTheCurrentAmplitude()
    {
        var t = ComponentTypeRegistry.UserParamTemplate(SymbolKind.CurrentToneSource);
        Assert.NotNull(t);
        Assert.Equal(["Freq[{0}]", "I[{0}]", "Phase[{0}]"], t!.NameFormats);
        Assert.Equal(2, t.FirstAddIndex);
    }

    [Fact]
    public void MigrateToneSourceToIndexed_RenamesWhicheverAmplitudeIsPresent()
    {
        var v = ParameterEditorViewModel.MigrateToneSourceToIndexed(
            [new() { Name = "V" }, new() { Name = "Freq" }, new() { Name = "Phase" },
             new() { Name = "Vdc" }]);
        Assert.Equal(["V[1]", "Freq[1]", "Phase[1]", "Vdc", "NumFreqs"], v.Select(p => p.Name));

        var i = ParameterEditorViewModel.MigrateToneSourceToIndexed(
            [new() { Name = "I" }, new() { Name = "Freq" }, new() { Name = "Phase" },
             new() { Name = "Idc" }]);
        Assert.Equal(["I[1]", "Freq[1]", "Phase[1]", "Idc", "NumFreqs"], i.Select(p => p.Name));
    }

    // ── Owner report, 2026-08-29: an added tone rendered no schematic label ───
    //
    // A parameter with a BLANK expression renders no label at all, whatever ShowOnSchematic says
    // (EditableSchematic.BuildRenderModel skips it) — so a group added with blank members read as a
    // broken checkbox. Every tone/impedance group the "+" button adds now carries a real default.
    [Theory]
    [InlineData(SymbolKind.ToneSource)]
    [InlineData(SymbolKind.CurrentToneSource)]
    [InlineData(SymbolKind.PnTone)]
    [InlineData(SymbolKind.P1Tone)]
    [InlineData(SymbolKind.ZPort)]
    public void EveryShownMemberOfAnAddedGroup_HasANonBlankDefault(SymbolKind kind)
    {
        var t = ComponentTypeRegistry.UserParamTemplate(kind);
        Assert.NotNull(t);
        for (int i = 0; i < t!.NameFormats.Length; i++)
        {
            if (i >= t.ShowOnSchematic.Length || !t.ShowOnSchematic[i]) continue;
            Assert.False(string.IsNullOrEmpty(t.DefaultExpression(i)),
                $"{kind}: '{t.NameFormats[i]}' shows on the schematic, so it must not be added blank — " +
                 "a blank expression renders no label and looks like the checkbox is ignored.");
        }
    }

    // The added group really does reach the schematic as a label — the end of the chain the owner
    // reported broken, asserted through BuildRenderModel rather than through the template alone.
    [Theory]
    [InlineData(SymbolKind.ToneSource,        "V[2]",  "Freq[2]")]
    [InlineData(SymbolKind.CurrentToneSource, "I[2]",  "Freq[2]")]
    public void AnAddedTone_RendersItsLabelsOnTheSchematic(SymbolKind kind, string amp, string freq)
    {
        var edit = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "S1", Symbol = kind, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        edit.Components.Add(comp);

        // What the "+" button does: migrate to indexed form, then append the next group.
        var t = ComponentTypeRegistry.UserParamTemplate(kind)!;
        var ps = ParameterEditorViewModel.MigrateToneSourceToIndexed(comp.Parameters.ToList());
        for (int i = 0; i < t.NameFormats.Length; i++)
            ps.Add(new EditableParameter
            {
                Name = string.Format(t.NameFormats[i], 2),
                Expression = t.DefaultExpression(i),
                Unit = t.DefaultUnits[i],
                ShowOnSchematic = t.ShowOnSchematic[i],
            });
        comp.Parameters.Clear();
        foreach (var p in ps) comp.Parameters.Add(p);

        var (render, _) = edit.BuildRenderModel();
        var labels = render.Components[0].Labels;
        Assert.Contains(labels, l => l.StartsWith(amp  + " =", System.StringComparison.Ordinal));
        Assert.Contains(labels, l => l.StartsWith(freq + " =", System.StringComparison.Ordinal));
    }
}
