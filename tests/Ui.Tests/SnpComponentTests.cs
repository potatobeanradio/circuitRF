using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for brief-snp-component (Parts 2–5).
///
/// Part 2 gates: registry, DefaultParameters, PortCount, TryParseCode.
/// Part 3 gates: pin positions (N=1,2,3,4), body rect growth, pitch.
/// Part 5 gates: NetExtractor emits NumPorts, signal nets, RefNetBinding.
/// </summary>
public class SnpComponentTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static EditableComponent MakeSnp(int n, bool refNode = false,
        string file = "test.s2p", string cfg = "Standard", string pitch = "Loose",
        double x = 0, double y = 0)
    {
        var comp = new EditableComponent
        {
            InstanceName = $"S{n}",
            Symbol       = SymbolKind.Snp,
            X            = x,
            Y            = y,
        };
        comp.Parameters.Add(new EditableParameter { Name = "NumPorts",  Expression = n.ToString()         });
        comp.Parameters.Add(new EditableParameter { Name = "File",      Expression = file                  });
        comp.Parameters.Add(new EditableParameter { Name = "RefNode",   Expression = refNode ? "true" : "false" });
        comp.Parameters.Add(new EditableParameter { Name = "PinConfig", Expression = cfg                  });
        comp.Parameters.Add(new EditableParameter { Name = "Pitch",     Expression = pitch                 });
        comp.Parameters.Add(new EditableParameter { Name = "InterpMode",Expression = "Cubic"               });
        comp.Parameters.Add(new EditableParameter { Name = "ExtrapMode",Expression = "NearestEdge"         });
        return comp;
    }

    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    // ── Part 2: Registry ─────────────────────────────────────────────────────

    [Fact]
    public void Registry_Snp_IsCommon_And_DataFiles()
    {
        var info = ComponentTypeRegistry.Get(SymbolKind.Snp);
        Assert.True(info.IsCommon);
        Assert.Equal(ComponentCategory.DataFiles, info.Category);
    }

    [Fact]
    public void EngineReference_Snp_IsSnP()
    {
        Assert.Equal("SnP", ComponentTypeRegistry.EngineReference(SymbolKind.Snp));
    }

    [Fact]
    public void DisplayName_Snp_WithPortCount_IsSNP()
    {
        Assert.Equal("S2P", ComponentTypeRegistry.DisplayName(SymbolKind.Snp, 2));
        Assert.Equal("S3P", ComponentTypeRegistry.DisplayName(SymbolKind.Snp, 3));
        Assert.Equal("SnP", ComponentTypeRegistry.DisplayName(SymbolKind.Snp, 0));
    }

    [Fact]
    public void TryParseCode_S2P_ReturnsSnpWith2Ports()
    {
        Assert.True(ComponentTypeRegistry.TryParseCode("S2P", out var kind, out int n));
        Assert.Equal(SymbolKind.Snp, kind);
        Assert.Equal(2, n);
    }

    [Fact]
    public void TryParseCode_S3P_ReturnsSnpWith3Ports()
    {
        Assert.True(ComponentTypeRegistry.TryParseCode("s3p", out var kind, out int n));
        Assert.Equal(SymbolKind.Snp, kind);
        Assert.Equal(3, n);
    }

    [Fact]
    public void DefaultParameters_Snp2_Has8Params_NumPortsIs2()
    {
        var ps = ComponentTypeRegistry.DefaultParameters(SymbolKind.Snp, 2);
        Assert.Equal(8, ps.Count);
        bool hasNumPorts = ps.Any(p => p.Name == "NumPorts" && p.Expression == "2" && !p.ShowOnSchematic);
        Assert.True(hasNumPorts, "NumPorts param must be hidden and equal 2");
    }

    [Fact]
    public void DefaultParameters_Snp_InterpModeIsCubicSpline_InterpDomainIsMA()
    {
        var ps = ComponentTypeRegistry.DefaultParameters(SymbolKind.Snp, 2);
        Assert.Equal("CubicSpline", ps.Single(p => p.Name == "InterpMode").Expression);
        Assert.Equal("MA",          ps.Single(p => p.Name == "InterpDomain").Expression);
    }

    [Fact]
    public void PortCount_Snp_ReadFromNumPorts()
    {
        var comp = MakeSnp(3);
        Assert.Equal(3, comp.PortCount);
    }

    [Fact]
    public void UserParamTemplate_Snp_IsNull()
    {
        Assert.Null(ComponentTypeRegistry.UserParamTemplate(SymbolKind.Snp));
    }

    // ── Part 3: Pin positions ─────────────────────────────────────────────────

    [Fact]
    public void GenerateSnpPorts_N1_OnePinOnLeft()
    {
        var pins = SymbolPortDefs.GenerateSnpPorts(1, refNode: false, SnpPinConfig.Standard, SnpPitch.Loose);
        Assert.Single(pins);
        Assert.Equal("1", pins[0].Name);
        Assert.Equal(-200f, pins[0].LocalX);
    }

    [Fact]
    public void GenerateSnpPorts_N2_LeftAndRight()
    {
        var pins = SymbolPortDefs.GenerateSnpPorts(2, false, SnpPinConfig.Standard, SnpPitch.Loose);
        Assert.Equal(2, pins.Length);
        Assert.True(pins.Any(p => p.LocalX < 0), "pin 1 should be on left");
        Assert.True(pins.Any(p => p.LocalX > 0), "pin 2 should be on right");
    }

    [Fact]
    public void GenerateSnpPorts_N3_SpecialCase_OneLeft_OneRight_OneTop()
    {
        // n=3 special case: 1=left-mid, 2=right-mid, 3=top-mid
        var pins = SymbolPortDefs.GenerateSnpPorts(3, false, SnpPinConfig.Standard, SnpPitch.Loose);
        Assert.Equal(3, pins.Length);
        Assert.Equal(-200f, pins[0].LocalX); Assert.Equal(  0f, pins[0].LocalY);
        Assert.Equal(+200f, pins[1].LocalX); Assert.Equal(  0f, pins[1].LocalY);
        Assert.Equal(   0f, pins[2].LocalX); Assert.Equal(-200f, pins[2].LocalY);
    }

    [Fact]
    public void GenerateSnpPorts_N3_RefNode_RefBelowBody()
    {
        var pins = SymbolPortDefs.GenerateSnpPorts(3, refNode: true, SnpPinConfig.Standard, SnpPitch.Loose);
        Assert.Equal(4, pins.Length);
        Assert.Equal("Ref", pins[3].Name);
        Assert.Equal(0f, pins[3].LocalX);
        Assert.True(pins[3].LocalY > 0, "Ref pin should be below the body (positive Y)");
        Assert.Equal(0f, pins[3].LocalY % 100f); // must be on grid
    }

    [Fact]
    public void GenerateSnpPorts_N4_TwoLeftTwoRight()
    {
        var pins = SymbolPortDefs.GenerateSnpPorts(4, false, SnpPinConfig.Standard, SnpPitch.Loose);
        Assert.Equal(4, pins.Length);
        int left  = pins.Count(p => p.LocalX < 0);
        int right = pins.Count(p => p.LocalX > 0);
        Assert.Equal(2, left);
        Assert.Equal(2, right);
    }

    [Fact]
    public void GenerateSnpPorts_WithRefNode_HasExtraRefPin()
    {
        var withRef    = SymbolPortDefs.GenerateSnpPorts(2, refNode: true,  SnpPinConfig.Standard, SnpPitch.Loose);
        var withoutRef = SymbolPortDefs.GenerateSnpPorts(2, refNode: false, SnpPinConfig.Standard, SnpPitch.Loose);
        Assert.Equal(withoutRef.Length + 1, withRef.Length);
        Assert.Equal("Ref", withRef[^1].Name);
    }

    [Fact]
    public void SnpBodyRect_N2_MinimumHeight()
    {
        var (w, halfH) = SymbolPortDefs.SnpBodyRect(2, SnpPinConfig.Standard, SnpPitch.Loose);
        Assert.True(halfH >= 100f);
        Assert.True(w > 0);
    }

    [Fact]
    public void SnpBodyRect_N4_LooseTallerThanTight()
    {
        var (_, loosH) = SymbolPortDefs.SnpBodyRect(4, SnpPinConfig.Standard, SnpPitch.Loose);
        var (_, tightH) = SymbolPortDefs.SnpBodyRect(4, SnpPinConfig.Standard, SnpPitch.Tight);
        Assert.True(loosH >= tightH);
    }

    // ── Part 5: NetExtractor ──────────────────────────────────────────────────

    [Fact]
    public void Snp2_NoRefNode_EmitsNSignalNets_NumPorts()
    {
        // S2P at (0,0), standard pins on left (-200,0) and right (+200,0).
        // Wire port 0 to net "A" (left pin via wire at (-200,0)).
        var comp = MakeSnp(2, refNode: false, x: 0, y: 0);

        var model = new SchematicEditModel();
        model.Components.Add(comp);
        // Connect both pins to distinct wires so they get real net names.
        model.Wires.Add(Wire((-200, 0), (-400, 0)));
        model.Wires.Add(Wire(( 200, 0), ( 400, 0)));

        var cell = NetExtractor.Extract(model);
        var inst = cell.TestBench.Instances.FirstOrDefault(i => i.Reference == "SnP");

        Assert.NotNull(inst);
        Assert.Equal(2, inst!.NetBindings.Count);
        Assert.Null(inst.RefNetBinding);

        // NumPorts must be in overrides
        var numPorts = inst.Overrides.FirstOrDefault(o => o.Name == "NumPorts");
        Assert.NotNull(numPorts);
        Assert.Equal("2", numPorts!.Expression);
    }

    [Fact]
    public void Snp2_WithRefNode_EmitsNSignalNets_PlusRefNetBinding()
    {
        // S2P with RefNode=true: 3 pins total (2 signal + 1 ref).
        var comp = MakeSnp(2, refNode: true, x: 0, y: 0);

        var model = new SchematicEditModel();
        model.Components.Add(comp);
        // The ref pin is at (0, halfH+100) — just let it float (unconnected → net "0").
        // Connect both signal pins to distinct wires.
        model.Wires.Add(Wire((-200, 0), (-400, 0)));
        model.Wires.Add(Wire(( 200, 0), ( 400, 0)));

        var cell = NetExtractor.Extract(model);
        var inst = cell.TestBench.Instances.FirstOrDefault(i => i.Reference == "SnP");

        Assert.NotNull(inst);
        Assert.Equal(2, inst!.NetBindings.Count); // 2 signal nets
        // RefNetBinding may be "0" (unconnected) — the important thing is it's set.
        Assert.NotNull(inst.RefNetBinding);
    }

    [Fact]
    public void Snp_UiOnlyParams_NotInOverrides()
    {
        var comp = MakeSnp(2);
        var model = new SchematicEditModel();
        model.Components.Add(comp);

        var cell = NetExtractor.Extract(model);
        var inst = cell.TestBench.Instances.FirstOrDefault(i => i.Reference == "SnP");
        Assert.NotNull(inst);

        var overrideNames = inst!.Overrides.Select(o => o.Name).ToHashSet();
        Assert.DoesNotContain("RefNode",   overrideNames);
        Assert.DoesNotContain("PinConfig", overrideNames);
        Assert.DoesNotContain("Pitch",     overrideNames);
    }

    // ── Grid-alignment gate ──────────────────────────────────────────────────

    [Theory]
    [InlineData(1, false, SnpPinConfig.Standard, SnpPitch.Loose)]
    [InlineData(1, true,  SnpPinConfig.Standard, SnpPitch.Loose)]
    [InlineData(2, false, SnpPinConfig.Standard, SnpPitch.Loose)]
    [InlineData(2, true,  SnpPinConfig.Standard, SnpPitch.Loose)]
    [InlineData(3, false, SnpPinConfig.Standard, SnpPitch.Loose)]
    [InlineData(3, true,  SnpPinConfig.Standard, SnpPitch.Loose)]
    [InlineData(4, false, SnpPinConfig.Standard, SnpPitch.Loose)]
    [InlineData(4, true,  SnpPinConfig.Standard, SnpPitch.Loose)]
    [InlineData(4, false, SnpPinConfig.Standard, SnpPitch.Tight)]
    [InlineData(4, true,  SnpPinConfig.Standard, SnpPitch.Tight)]
    [InlineData(5, false, SnpPinConfig.Standard, SnpPitch.Loose)]
    [InlineData(5, false, SnpPinConfig.Standard, SnpPitch.Tight)]
    [InlineData(5, false, SnpPinConfig.SplitLR,  SnpPitch.Tight)]
    [InlineData(5, false, SnpPinConfig.DualRow,  SnpPitch.Tight)]
    [InlineData(6, false, SnpPinConfig.Standard, SnpPitch.Loose)]
    [InlineData(6, true,  SnpPinConfig.Standard, SnpPitch.Loose)]
    [InlineData(6, false, SnpPinConfig.Standard, SnpPitch.Tight)]
    [InlineData(6, true,  SnpPinConfig.Standard, SnpPitch.Tight)]
    [InlineData(8, false, SnpPinConfig.Standard, SnpPitch.Loose)]
    [InlineData(8, true,  SnpPinConfig.Standard, SnpPitch.Loose)]
    [InlineData(8, false, SnpPinConfig.Standard, SnpPitch.Tight)]
    [InlineData(8, true,  SnpPinConfig.Standard, SnpPitch.Tight)]
    public void SnpPinsAreGridAligned(int n, bool refNode, SnpPinConfig cfg, SnpPitch pitch)
    {
        var pins = SymbolPortDefs.GenerateSnpPorts(n, refNode, cfg, pitch);
        foreach (var (name, lx, ly) in pins)
        {
            Assert.True(lx % 100f == 0f, $"Pin '{name}' X={lx} is not grid-aligned (n={n} refNode={refNode} cfg={cfg} pitch={pitch})");
            Assert.True(ly % 100f == 0f, $"Pin '{name}' Y={ly} is not grid-aligned (n={n} refNode={refNode} cfg={cfg} pitch={pitch})");
        }
    }

    [Fact]
    public void GenerateSnpPorts_N1_RefNode_RefOnRight()
    {
        var pins = SymbolPortDefs.GenerateSnpPorts(1, refNode: true, SnpPinConfig.Standard, SnpPitch.Loose);
        Assert.Equal(2, pins.Length);
        Assert.Equal("Ref", pins[1].Name);
        Assert.Equal(+200f, pins[1].LocalX);
        Assert.Equal(0f, pins[1].LocalY);
    }
}
