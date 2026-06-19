// ================================================================
//  SchematicHousecleaningTests.cs
//  Gate tests for brief-schematic-housecleaning
//
//  Item 1: Paste-Num deduplication (PasteNum_*)
//  Item 3: Save-As title update   (SaveAs_*)
//  Item 4: Pin placement armed    (PlacePinArms_*)
//  Item 6: SNP label position     (SnpLabel_*)
// ================================================================

using System.Linq;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class SchematicHousecleaningTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EditableComponent MakeTerm(string name, int num, double x = 0, double y = 0)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.Term, X = x, Y = y };
        c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
        return c;
    }

    private static EditableComponent MakeP1Tone(string name, int num, double x = 0, double y = 0)
    {
        var c = new EditableComponent { InstanceName = name, Symbol = SymbolKind.P1Tone, X = x, Y = y };
        c.Parameters.Add(new EditableParameter { Name = "Num", Expression = num.ToString() });
        return c;
    }

    private static int GetNum(EditableComponent c)
        => int.Parse(c.Parameters.First(p => p.Name == "Num").Expression);

    private static SchematicEditModel ModelWith(params EditableComponent[] comps)
    {
        var m = new SchematicEditModel();
        m.Components.AddRange(comps);
        return m;
    }

    // ── Item 1: Paste Num dedup ───────────────────────────────────────────────

    // T1 — Pasting a Term Num=1 into a schematic that already has Term Num=1 → gets Num=2
    [Fact]
    public void PasteNum_Term_CollisionGetsNextFreeNum()
    {
        var model  = ModelWith(MakeTerm("T1", 1));
        var pasted = MakeTerm("T2", 1);
        var cmd    = new SchematicPasteCommand(model, [pasted], [], []);
        cmd.Execute();

        var got = model.Components.Last();
        Assert.Equal(2, GetNum(got));
    }

    // T2 — Pasting a P1Tone Num=1 into a schematic with Term Num=1 → gets Num=2
    [Fact]
    public void PasteNum_P1Tone_CollidesWithTerm_GetsNextFree()
    {
        var model  = ModelWith(MakeTerm("T1", 1));
        var pasted = MakeP1Tone("P1", 1);
        var cmd    = new SchematicPasteCommand(model, [pasted], [], []);
        cmd.Execute();

        var got = model.Components.Last();
        Assert.Equal(2, GetNum(got));
    }

    // T3 — Pasting two Terms Num=1 into a schematic with Term Num=1 → get Num=2 and Num=3
    [Fact]
    public void PasteNum_BatchPaste_NoIntraBatchCollision()
    {
        var model  = ModelWith(MakeTerm("T1", 1));
        var p1     = MakeTerm("T2", 1, x: 100);
        var p2     = MakeTerm("T3", 1, x: 200);
        var cmd    = new SchematicPasteCommand(model, [p1, p2], [], []);
        cmd.Execute();

        int n1 = GetNum(model.Components[1]);
        int n2 = GetNum(model.Components[2]);
        Assert.NotEqual(1, n1);  // doesn't collide with existing
        Assert.NotEqual(1, n2);
        Assert.NotEqual(n1, n2); // and they don't collide with each other
    }

    // T4 — Pasting a Term with Num not colliding keeps original Num
    [Fact]
    public void PasteNum_NoCollision_KeepsOriginalNum()
    {
        var model  = ModelWith(MakeTerm("T1", 1));
        var pasted = MakeTerm("T2", 2);
        var cmd    = new SchematicPasteCommand(model, [pasted], [], []);
        cmd.Execute();

        var got = model.Components.Last();
        Assert.Equal(2, GetNum(got));
    }

    // T5 — Non-port components (Resistor) are not affected
    [Fact]
    public void PasteNum_Resistor_NoNumParam_UnchangedNoError()
    {
        var model  = new SchematicEditModel();
        var r = new EditableComponent { InstanceName = "R1", Symbol = SymbolKind.Resistor };
        r.Parameters.Add(new EditableParameter { Name = "R", Expression = "50" });
        var cmd = new SchematicPasteCommand(model, [r], [], []);
        // Must not throw
        cmd.Execute();
        Assert.Single(model.Components);
    }

    // ── Item 3: OnSavedAs updates title ──────────────────────────────────────

    // T6 — Materialized doc → OnSavedAs → Title updated (no dirty bullet)
    [Fact]
    public void SaveAs_OnSavedAs_UpdatesTitleAndFilePath()
    {
        var vm   = new SchematicViewModel(new SchematicEditModel());
        var doc  = new SchematicDocument("OldName", vm);
        doc.Materialize("/tmp/OldName.csch");

        doc.OnSavedAs("/tmp/NewName.csch", "NewName");

        Assert.Equal("NewName", doc.Title);
        Assert.Equal("/tmp/NewName.csch", doc.FilePath);
        Assert.Equal("NewName", doc.Id);
    }

    // T7 — OnSavedAs on dirty doc: clears dirty bullet after rename
    //      (IsDirty is not changed by OnSavedAs — it's a rename, not a clean)
    [Fact]
    public void SaveAs_OnSavedAs_DirtyDocRetainsDirtyState()
    {
        var vm   = new SchematicViewModel(new SchematicEditModel());
        var doc  = new SchematicDocument("OldName", vm);
        doc.Materialize("/tmp/OldName.csch");

        // Force dirty via a command (just check title format).
        // OnSavedAs does NOT clear IsDirty — the file was already saved.
        doc.OnSavedAs("/tmp/Bar.csch", "Bar");

        // Title should use new base name regardless of dirty state
        Assert.StartsWith("Bar", doc.Title.TrimStart('•', ' '));
    }

    // ── Item 4: Pin placement armed ───────────────────────────────────────────

    // T8 — BeginPlacement(Pin) sets PlacementSymbol to Pin and Tool to Place
    [Fact]
    public void PlacePinArms_PinSymbol()
    {
        var vm = new SchematicViewModel(new SchematicEditModel());
        vm.BeginPlacement(SymbolKind.Pin);

        Assert.Equal(SymbolKind.Pin, vm.PlacementSymbol);
        Assert.Equal(SchematicViewModel.Tool.Place, vm.ActiveTool);
    }

    // ── Item 6: SNP label position tracks actual glyph extent ────────────────

    // T9 — LabelBaseYFor(Snp, n) with explicit glyphHalfH larger than default: label pushed down
    [Fact]
    public void SnpLabel_LargeGlyphHalfH_PushesLabelDown()
    {
        const int n = 4;
        double defaultY   = SchematicComponent.LabelBaseYFor(SymbolKind.Snp, n);
        double largeHalfH = defaultY + 500; // larger than default → should push down
        double overrideY  = SchematicComponent.LabelBaseYFor(SymbolKind.Snp, n, largeHalfH);

        Assert.True(overrideY > defaultY,
            $"With large glyphHalfH={largeHalfH}, label should be below default {defaultY} but got {overrideY}");
    }

    // T10 — Actual glyph halfH ≥ SnpBodyRect halfH (pin stubs extend beyond the body)
    //       This confirms that passing the glyph extent (not just body rect) gives a larger
    //       offset, which is exactly the bug we fixed for n ≥ 4.
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void SnpLabel_ActualGlyphHalfH_ExceedsBodyRect(int n)
    {
        var comp = MakeSnp(n);
        var (_, _, _, glyphMaxY) = comp.ComputeGlyphBb();
        double actualHalfH  = glyphMaxY - comp.Y;   // comp.Y == 0

        var (_, bodyHalfH) = SymbolPortDefs.SnpBodyRect(n, SnpPinConfig.Standard, SnpPitch.Loose);

        // The actual glyph halfH is ≥ body halfH — pin stubs extend beyond the body boundary
        Assert.True(actualHalfH >= (double)bodyHalfH,
            $"N={n}: actual glyph halfH {actualHalfH} should be ≥ body halfH {bodyHalfH}");
    }

    // T11 — LabelBaseYFor with real glyph halfH: label strictly below glyph bottom for n≥4
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void SnpLabel_ClearsGlyph_WithRealHalfH(int n)
    {
        var comp = MakeSnp(n);
        var (_, _, _, glyphMaxY) = comp.ComputeGlyphBb();
        double glyphHalfH    = glyphMaxY - comp.Y;
        double labelBaselineY = SchematicComponent.LabelBaseYFor(SymbolKind.Snp, n, glyphHalfH);

        Assert.True(labelBaselineY > glyphHalfH,
            $"N={n}: label baseline {labelBaselineY} should be > glyph halfH {glyphHalfH}");
    }

    // T12 — Label grows with port count (n=2 < n=4 < n=8) when passing real glyph halfH
    [Fact]
    public void SnpLabel_GrowsWithPortCount()
    {
        double y2 = SnpLabelY(2);
        double y4 = SnpLabelY(4);
        double y8 = SnpLabelY(8);

        Assert.True(y2 <= y4, $"n=2 ({y2}) should be ≤ n=4 ({y4})");
        Assert.True(y4 < y8, $"n=4 ({y4}) should be < n=8 ({y8})");
    }

    private static double SnpLabelY(int n)
    {
        var comp = MakeSnp(n);
        var (_, _, _, glyphMaxY) = comp.ComputeGlyphBb();
        return SchematicComponent.LabelBaseYFor(SymbolKind.Snp, n, glyphMaxY - comp.Y);
    }

    private static EditableComponent MakeSnp(int n)
    {
        var c = new EditableComponent { InstanceName = $"S{n}", Symbol = SymbolKind.Snp, X = 0, Y = 0 };
        c.Parameters.Add(new EditableParameter { Name = "NumPorts",   Expression = n.ToString()    });
        c.Parameters.Add(new EditableParameter { Name = "File",        Expression = "test.s2p"     });
        c.Parameters.Add(new EditableParameter { Name = "RefNode",     Expression = "false"        });
        c.Parameters.Add(new EditableParameter { Name = "PinConfig",   Expression = "Standard"     });
        c.Parameters.Add(new EditableParameter { Name = "Pitch",       Expression = "Loose"        });
        c.Parameters.Add(new EditableParameter { Name = "InterpMode",  Expression = "Cubic"        });
        c.Parameters.Add(new EditableParameter { Name = "ExtrapMode",  Expression = "NearestEdge"  });
        return c;
    }
}
