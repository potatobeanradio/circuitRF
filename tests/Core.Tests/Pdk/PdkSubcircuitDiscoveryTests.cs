using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Pdk;
using Xunit;

namespace CircuitRF.Core.Tests.Pdk;

/// <summary>
/// A part's identity is the SUBCIRCUIT it is netlisted as — that is what carries the terminals and
/// the parameters, and what a simulator must name. A symbol file is a drawing OF a part, never a
/// part in its own right. These tests pin that distinction, and the rule that attaches a drawing to
/// the subcircuit it depicts.
///
/// <para>All fixtures are synthetic and name no kit.</para>
/// </summary>
public sealed class PdkSubcircuitDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-sub-" + Guid.NewGuid().ToString("N")[..8]);

    public PdkSubcircuitDiscoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string relative, string content)
    {
        string abs = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
    }

    private PdkImportReport Import() => PdkImporter.Import(_root);

    /// <summary>
    /// A kit's netlist subcircuits only become PARTS when the kit also drew them — one without
    /// artwork is an internal building block. Fixtures that are about parsing therefore need a
    /// drawing per subcircuit for the subcircuit to surface at all.
    /// </summary>
    private void WriteDrawingFor(string subcircuitName) =>
        Write($"symbols/{subcircuitName}_SYM.dsn", "1  0 0 0\n");

    // ── Identity ──────────────────────────────────────────────────────────────

    [Fact]
    public void PartsAreTheSubcircuits_ATerminalListAndParametersComeWithThem()
    {
        Write("models/lib.net", """
            ; a comment
            define WIDGET_A ( in out gnd )
            parameters  Rs=1.5  Cj=0  Tag="abc"
            R:R1 in out R=Rs
            end WIDGET_A
            """);
        WriteDrawingFor("WIDGET_A");

        var part = Assert.Single(Import().Parts);

        Assert.Equal("WIDGET_A", part.Id);
        Assert.Equal(3, part.PinCount);
        Assert.Equal(["Rs", "Cj", "Tag"], part.Parameters!.Select(p => p.Name));
        Assert.Equal("1.5", part.Parameters![0].DefaultExpression);
        Assert.Equal("abc", part.Parameters![2].DefaultExpression);
    }

    [Fact]
    public void ASubcircuitsParameters_NeverLeakOntoTheNextSubcircuit()
    {
        Write("models/lib.net", """
            define FIRST ( a b )
            parameters  Only=1
            define SECOND ( a b c )
            R:R1 a b R=1
            """);
        WriteDrawingFor("FIRST");
        WriteDrawingFor("SECOND");

        var parts = Import().Parts.OrderBy(p => p.Id).ToList();

        Assert.Equal(["FIRST", "SECOND"], parts.Select(p => p.Id));
        Assert.Equal(["Only"], parts[0].Parameters!.Select(p => p.Name));
        Assert.Empty(parts[1].Parameters!);
    }

    [Fact]
    public void ATerminalListSpanningSeveralLines_IsCountedWhole()
    {
        Write("models/lib.net", """
            define BIG ( p1 p2 p3 p4
                         p5 p6 )
            parameters  X=0
            """);
        WriteDrawingFor("BIG");

        Assert.Equal(6, Assert.Single(Import().Parts).PinCount);
    }

    [Fact]
    public void AContinuedParameterLine_IsReadThrough()
    {
        Write("models/lib.net", """
            define WIDGET ( a b )
            parameters  P1=1  P2=2  \
                        P3=3
            """);
        WriteDrawingFor("WIDGET");

        Assert.Equal(["P1", "P2", "P3"], Assert.Single(Import().Parts).Parameters!.Select(p => p.Name));
    }

    // ── Drawing attachment ────────────────────────────────────────────────────

    [Fact]
    public void ADrawingNamedAfterThePart_AttachesToEveryVariantOfThatPart()
    {
        // The common real shape: one drawing per PART, several subcircuit variants per part.
        Write("models/lib.net", """
            define WIDGET_Rev0_VariantA ( a b )
            define WIDGET_Rev0_VariantB ( a b )
            """);
        Write("symbols/WIDGET_SYM.dsn", "1  0 0 0\n");

        var parts = Import().Parts.OrderBy(p => p.Id).ToList();

        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.EndsWith("WIDGET_SYM.dsn", p.SymbolArtwork!.RelativePath));
    }

    [Fact]
    public void TwoUnrelatedNamesSharingAFamilyPrefix_AreNotMatched()
    {
        // "TECH_INCLUDE" and "TECH_FET" share "TECH_" and are completely different things. A
        // loose contains-match would wrongly attach one's drawing to the other.
        Write("models/lib.net", "define TECH_FET_ROOT ( a b )\n");
        Write("symbols/TECH_INCLUDE_SYM.dsn", "1  0 0 0\n");   // unrelated: "include" is absent
        Write("symbols/TECH_FET_SYM.dsn",     "1  0 0 0\n");   // the real one

        var sym = Assert.Single(Import().Parts).SymbolArtwork;

        Assert.EndsWith("TECH_FET_SYM.dsn", sym!.RelativePath);
    }

    [Fact]
    public void TheMostSpecificDrawing_WinsOverAMoreGeneralOne()
    {
        Write("models/lib.net", "define WIDGET_Rev2 ( a b )\n");
        Write("symbols/WIDGET_SYM.dsn",      "1  0 0 0\n");
        Write("symbols/WIDGET_Rev2_SYM.dsn", "1  0 0 0\n");

        Assert.EndsWith("WIDGET_Rev2_SYM.dsn", Assert.Single(Import().Parts).SymbolArtwork!.RelativePath);
    }

    [Fact]
    public void AReadableDrawing_BeatsAMoreSpecificallyNamedOneThatCannotBeRead()
    {
        // Kits commonly ship the same symbol twice — once as text, once in a binary cell database —
        // and the binary copy often carries the more precise name. Preferring specificity alone
        // attaches the unusable copy and leaves the part unplaceable, which is the whole point of
        // attaching a drawing in the first place.
        Write("models/lib.net", "define WIDGET_Rev0_VariantA ( a b )\n");
        Write("cells/WIDGET_Rev0_MODEL/symbol/symbol.oa", "\0\0binary");   // longer stem, unreadable
        Write("symbols/WIDGET_SYM.dsn", "1  0 0 0\n");                     // shorter stem, readable

        var sym = Assert.Single(Import().Parts).SymbolArtwork;

        Assert.Equal(PdkAssetSupport.Supported, sym!.Support);
        Assert.EndsWith("WIDGET_SYM.dsn", sym.RelativePath);
    }

    [Fact]
    public void ASymbolFile_NeverBecomesAPartOfItsOwn()
    {
        // The bug this rule exists to prevent: a kit that stores each symbol as its own cell
        // directory produced a duplicate, symbol-named part beside every real one.
        Write("models/lib.net", "define WIDGET_Rev0 ( a b )\n");
        Write("cells/WIDGET_SYM/symbol/symbol.oa", "\0\0binary");
        Write("symbols/WIDGET_SYM.dsn", "1  0 0 0\n");

        var parts = Import().Parts;

        Assert.Equal(["WIDGET_Rev0"], parts.Select(p => p.Id));
    }

    [Fact]
    public void AKitWithNoNetlistAtAll_StillListsItsCellDirectories()
    {
        // The fallback must survive: a kit that ships only a cell database has no subcircuits to
        // key on, and listing nothing would be worse than listing the directories.
        Write("cells/WIDGET/symbol/symbol.oa", "\0\0binary");

        Assert.Equal(["WIDGET"], Import().Parts.Select(p => p.Id));
    }

    // ── Robustness ────────────────────────────────────────────────────────────

    [Fact]
    public void ASubcircuitWithNoTerminalList_IsStillAPart_WithNoPinCount()
    {
        Write("models/lib.net", "define BARE\n");
        WriteDrawingFor("BARE");

        var part = Assert.Single(Import().Parts);

        Assert.Equal("BARE", part.Id);
        Assert.Equal(0, part.PinCount);
    }

    [Fact]
    public void TheSameSubcircuitDeclaredTwice_ProducesOnePart()
    {
        Write("models/a.net", "define WIDGET ( a b )\n");
        Write("models/b.net", "define WIDGET ( a b )\n");
        WriteDrawingFor("WIDGET");

        Assert.Single(Import().Parts);
    }

    // ── Sentinel defaults ─────────────────────────────────────────────────────

    [Fact]
    public void ASentinelDefault_IsReplacedByTheValueTheKitItselfComputes()
    {
        // circuitRF hands the part straight to a device provider, so the netlist wrapper that
        // resolves the sentinel never runs. Left verbatim, a thermal resistance of -1 would reach
        // the model raw — not a default, just nonsense.
        Write("models/lib.net", """
            define WIDGET ( a b )
            parameters  RTH1=-1  RTH2=-1  CTH1=-1
            NEW_RTH1=if(RTH1==-1) then ((1.0e-6)*1) else (RTH1*1) endif
            NEW_RTH2=if(RTH2==-1) then ((1.0e-6)*2) else (RTH2*2) endif
            NEW_CTH1=if(CTH1==-1) then ((1.0e-7)/2) else (CTH1/2) endif
            """);
        WriteDrawingFor("WIDGET");

        var pars = Assert.Single(Import().Parts).Parameters!.ToDictionary(p => p.Name, p => p.DefaultExpression);

        Assert.Equal(1e-6, double.Parse(pars["RTH1"]), 15);
        Assert.Equal(2e-6, double.Parse(pars["RTH2"]), 15);
        Assert.Equal(5e-8, double.Parse(pars["CTH1"]), 15);
    }

    [Fact]
    public void AParameterTheKitNeverResolves_KeepsItsOwnValue_NoInventedDefault()
    {
        Write("models/lib.net", """
            define WIDGET ( a b )
            parameters  TSNK=-1  RTH=-1
            NEW_RTH=if(RTH==-1) then (1.0e-6) else (RTH) endif
            """);
        WriteDrawingFor("WIDGET");

        var pars = Assert.Single(Import().Parts).Parameters!.ToDictionary(p => p.Name, p => p.DefaultExpression);

        Assert.Equal("-1", pars["TSNK"]);                       // no expression exists for it
        Assert.Equal(1e-6, double.Parse(pars["RTH"]), 15);
    }

    [Fact]
    public void ADefaultThatIsNotTheSentinel_IsLeftAlone()
    {
        Write("models/lib.net", """
            define WIDGET ( a b )
            parameters  RTH=42
            NEW_RTH=if(RTH==-1) then (1.0e-6) else (RTH) endif
            """);
        WriteDrawingFor("WIDGET");

        Assert.Equal("42", Assert.Single(Import().Parts).Parameters!.Single(p => p.Name == "RTH").DefaultExpression);
    }

    [Fact]
    public void AnExpressionTooComplexToEvaluate_LeavesTheKitsOwnTextUntouched()
    {
        // Better a visible sentinel than a confidently wrong number.
        Write("models/lib.net", """
            define WIDGET ( a b )
            parameters  RTH=-1
            NEW_RTH=if(RTH==-1) then (someOtherParam*2) else (RTH) endif
            """);
        WriteDrawingFor("WIDGET");

        Assert.Equal("-1", Assert.Single(Import().Parts).Parameters!.Single(p => p.Name == "RTH").DefaultExpression);
    }

    [Theory]
    [InlineData("((1.0e-6)*1)", 1e-6)]
    [InlineData("(1.0e-7)/2", 5e-8)]
    [InlineData("2 + 3 * 4", 14.0)]
    [InlineData("-5", -5.0)]
    public void TheArithmeticEvaluator_HandlesTheFormsAKitActuallyWrites(string expr, double expected)
    {
        Assert.True(PdkImporter.TryEvaluateNumber(expr, out double v));
        Assert.Equal(expected, v, 15);
    }

    [Theory]
    [InlineData("someParam*2")]
    [InlineData("sqrt(4)")]
    [InlineData("")]
    public void TheArithmeticEvaluator_RefusesWhatItCannotFullyUnderstand(string expr)
    {
        Assert.False(PdkImporter.TryEvaluateNumber(expr, out _));
    }
}
