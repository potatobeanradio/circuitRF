using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// PnTone — the multi-tone power source (a P1Tone clone for convenient two-tone authoring). Gate
/// tests for its registration: engine reference, seeded two-tone defaults, the per-tone "+"/"−"
/// template, the shared P1Tone symbol, and the short-code parse.
/// </summary>
public sealed class PnToneComponentTests
{
    [Fact]
    public void EngineReference_IsPnTone()
        => Assert.Equal("PnTone", ComponentTypeRegistry.EngineReference(SymbolKind.PnTone));

    [Fact]
    public void DefaultParameters_SeedTwoTones_PlusSharedZ()
    {
        var names = ComponentTypeRegistry.DefaultParameters(SymbolKind.PnTone, 0).Select(p => p.Name).ToList();
        // Two tones seeded so a freshly-placed PnTone is ready for two-tone, plus the shared Z reference.
        Assert.Contains("Freq[1]",  names);
        Assert.Contains("Pavl[1]",  names);
        Assert.Contains("Phase[1]", names);
        Assert.Contains("Freq[2]",  names);
        Assert.Contains("Pavl[2]",  names);
        Assert.Contains("Phase[2]", names);
        Assert.Contains("Z",        names);
        Assert.DoesNotContain("Num", names);   // not an S-param port
    }

    [Fact]
    public void UserParamTemplate_IsPerToneGroup()
    {
        var tpl = ComponentTypeRegistry.UserParamTemplate(SymbolKind.PnTone);
        Assert.NotNull(tpl);
        Assert.Equal(new[] { "Freq[{0}]", "Pavl[{0}]", "Phase[{0}]" }, tpl!.NameFormats);
        Assert.Equal(2, tpl.FirstAddIndex);   // tone 1 is protected; "+" adds tone 3, 4, …
    }

    [Fact]
    public void Symbol_IsSharedWithP1Tone()
        => Assert.Equal(BuiltInSymbols.Primitives(SymbolKind.P1Tone),
                        BuiltInSymbols.Primitives(SymbolKind.PnTone));

    [Fact]
    public void TryParseCode_PnTone_Resolves()
    {
        Assert.True(ComponentTypeRegistry.TryParseCode("PnTone", out var kind, out _));
        Assert.Equal(SymbolKind.PnTone, kind);
    }

    // Schematic authoring path: a placed PnTone extracts to a "PnTone" instance with 2 nets + its
    // per-tone overrides (CnlReader → elaboration → engine is covered by the Engine two-tone test).
    [Fact]
    public void NetExtractor_EmitsPnToneInstance_WithTwoTonesAndTwoNets()
    {
        var comp = new EditableComponent { InstanceName = "Pd", Symbol = SymbolKind.PnTone, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.PnTone, 0))
            comp.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit });

        var model = new SchematicEditModel();
        model.Components.Add(comp);
        // PnTone uses the default 2-pin vertical layout: pins at (0,-200) and (0,+200).
        var w0 = new EditableWire(); w0.Points.AddRange([(0, -200), (0, -400)]);
        var w1 = new EditableWire(); w1.Points.AddRange([(0, 200), (0, 400)]);
        model.Wires.Add(w0);
        model.Wires.Add(w1);

        var cell = NetExtractor.Extract(model);
        var inst = cell.TestBench.Instances.FirstOrDefault(i => i.Reference == "PnTone");

        Assert.NotNull(inst);
        Assert.Equal(2, inst!.NetBindings.Count);
        var ovNames = inst.Overrides.Select(o => o.Name).ToList();
        Assert.Contains("Freq[1]", ovNames);
        Assert.Contains("Pavl[1]", ovNames);
        Assert.Contains("Freq[2]", ovNames);
        Assert.Contains("Pavl[2]", ovNames);
        Assert.Contains("Z",       ovNames);
    }
}
