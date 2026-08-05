using System.Linq;
using CircuitRF.Core.Pdk;
using Xunit;

namespace CircuitRF.Core.Tests.Pdk;

/// <summary>
/// Reading a schematic symbol from a plain-text record format.
///
/// <para><b>Every fixture is synthetic.</b> This is a format reader, and the repository commits no
/// third-party kit data — so nothing here names a supplier, a kit, a part or a model family, and the
/// files are written to exercise a rule rather than to resemble any particular kit's.</para>
/// </summary>
public sealed class KitSymbolFileReaderTests
{
    /// <summary>
    /// A whole symbol in the shape the format takes: a version record, a global attribute block
    /// carrying the type word and the default template, some drawing, and rectangles that name
    /// terminals.
    /// </summary>
    private const string ThreeTerminal = """
        v {version=1.2 file_version=1.0}
        K {type=nfet
        format="@name @pinlist @model w=@w l=@l"
        template="name=M1 model=lv_nfet w=1u l=0.13u nf=1"}
        G {}
        V {}
        S {}
        E {}
        L 4 -20 -20 -20 20 {}
        L 4 -20 0 -40 0 {}
        B 5 -42.5 -2.5 -37.5 2.5 {name=g dir=in}
        B 5 -2.5 -22.5 2.5 -17.5 {name=d dir=inout}
        B 5 -2.5 17.5 2.5 22.5 {name=s dir=inout}
        T {M} -15 -8 0 0 0.2 0.2 {}
        """;

    // ── K1 — terminals ────────────────────────────────────────────────────────

    [Fact]
    public void K1_TerminalsAreReadWithTheirNamesAndPositions()
    {
        var s = KitSymbolFileReader.Read(ThreeTerminal);

        Assert.NotNull(s);
        Assert.Equal(["g", "d", "s"], s!.Pins.Select(p => p.Name));

        // A terminal sits at its rectangle's centre, which is where a wire is expected to meet it.
        Assert.Equal((-40, 0), (s.Pins[0].X, s.Pins[0].Y));
        Assert.Equal((0, -20),  (s.Pins[1].X, s.Pins[1].Y));
        Assert.Equal((0,  20),  (s.Pins[2].X, s.Pins[2].Y));
    }

    /// <summary>
    /// A terminal is a rectangle whose attributes NAME one — not one on a particular layer. The
    /// layer number is the format's display convention and a kit is free to renumber it; keying on
    /// it would read such a kit as a symbol with no pins, which still imports, still appears in the
    /// palette, and cannot be wired to anything.
    /// </summary>
    [Fact]
    public void K2_ATerminalIsRecognisedByItsNameAttribute_NotByItsLayer()
    {
        var s = KitSymbolFileReader.Read("""
            K {type=res}
            B 17 -2.5 -2.5 2.5 2.5 {name=p dir=inout}
            B 4 -10 -10 10 10 {}
            """);

        var pin = Assert.Single(s!.Pins);
        Assert.Equal("p", pin.Name);
    }

    // ── K3 — the parameter interface ──────────────────────────────────────────

    /// <summary>
    /// The template is the kit STATING this part's interface, with its own defaults. Those defaults
    /// are carried verbatim — circuitRF never invents one.
    /// </summary>
    [Fact]
    public void K3_TheTemplateBecomesTheParameterInterface()
    {
        var s = KitSymbolFileReader.Read(ThreeTerminal);

        Assert.Equal(["model", "w", "l", "nf"], s!.Parameters.Select(p => p.Name));
        Assert.Equal("lv_nfet", s.Parameters[0].DefaultExpression);
        Assert.Equal("1u",      s.Parameters[1].DefaultExpression);
        Assert.Equal("0.13u",   s.Parameters[2].DefaultExpression);

        // A model name is not a number, so it must not be offered a numeric editor; a width is.
        Assert.True(s.Parameters[0].IsText);
        Assert.False(s.Parameters[1].IsText);
        Assert.False(s.Parameters[3].IsText);
    }

    /// <summary>
    /// The instance's own name is not a parameter of the device. Offering it as one would put a
    /// "what is this instance called" box in the parameter editor beside the real parameters.
    /// </summary>
    [Fact]
    public void K4_TheInstanceNameIsNotAParameter()
        => Assert.DoesNotContain(KitSymbolFileReader.Read(ThreeTerminal)!.Parameters,
                                 p => p.Name.Equals("name", System.StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void K5_TheTypeWordIsCarried()
        => Assert.Equal("nfet", KitSymbolFileReader.Read(ThreeTerminal)!.TypeWord);

    // ── K6 — the record grammar ───────────────────────────────────────────────

    /// <summary>
    /// An attribute block spans lines routinely — a template is written that way in real files. A
    /// line-at-a-time reader takes the first line as the whole block and loses every parameter after
    /// it, which leaves a part that looks read and has half an interface.
    /// </summary>
    [Fact]
    public void K6_AnAttributeBlockMaySpanLines()
    {
        var s = KitSymbolFileReader.Read("""
            K {type=cap
            template="name=C1
            c=1p
            m=1"}
            B 5 -2.5 -2.5 2.5 2.5 {name=p}
            """);

        Assert.Equal(["c", "m"], s!.Parameters.Select(p => p.Name));
        Assert.Equal("1p", s.Parameters[0].DefaultExpression);
    }

    [Fact]
    public void K7_TextThatIsNotThisFormatIsRefused()
    {
        Assert.Null(KitSymbolFileReader.Read(""));
        Assert.Null(KitSymbolFileReader.Read("this is prose, not a symbol"));
        Assert.False(KitSymbolFileReader.LooksLikeSymbolFile("<?xml version=\"1.0\"?><root/>"));
    }

    /// <summary>
    /// Recognition is structural. Keying on the tool named in a file's first line would put a
    /// particular editor's identity into circuitRF and would stop recognising the same format the
    /// moment a kit generated it with something else.
    /// </summary>
    [Fact]
    public void K8_RecognitionDoesNotDependOnAnyToolName()
    {
        Assert.True(KitSymbolFileReader.LooksLikeSymbolFile(ThreeTerminal));

        // The same symbol with its version line removed entirely is still one.
        string noVersion = string.Join('\n', ThreeTerminal.Split('\n').Skip(1));
        Assert.True(KitSymbolFileReader.LooksLikeSymbolFile(noVersion));
    }

    /// <summary>
    /// A symbol declaring no terminals is a real symbol — a title block, a decoration — and is
    /// reported as one with no pins rather than as a parse failure, so a caller can tell "this is
    /// not the format" from "this is the format and has nothing to wire".
    /// </summary>
    [Fact]
    public void K9_ASymbolWithNoTerminalsIsReadRatherThanRefused()
    {
        var s = KitSymbolFileReader.Read("K {type=title}\nL 4 0 0 10 0 {}\n");

        Assert.NotNull(s);
        Assert.Empty(s!.Pins);
    }

    // ── K10-K12 — rules a kit taught, each with a real defect behind it ──

    /// <summary>
    /// <b>The regression for a malformed file, and it is not hypothetical.</b> One device's symbol in
    /// a kit has a <c>template="…</c> whose closing quote is missing. Quote tracking then makes
    /// every following brace look quoted, so the attribute block runs on and swallows the rest of the
    /// symbol — the terminals included — and the device imports with NO PINS: still listed, still in
    /// the palette, impossible to wire.
    ///
    /// <para>A record always begins a line in this format, so a block that would swallow one is
    /// bounded there instead. The symbol's terminals survive; only the malformed attribute is lost.</para>
    /// </summary>
    [Fact]
    public void K10_AnUnterminatedQuoteDoesNotSwallowTheTerminals()
    {
        var s = KitSymbolFileReader.Read("""
            K {type=vertical_npn
            template="name=Q1
            model=some_model
            Nx=1
            drc="a drc rule @name"
            }
            V {}
            B 5 17.5 -52.5 22.5 -47.5 {name=C dir=inout}
            B 5 -42.5 -2.5 -37.5 2.5 {name=B dir=in}
            B 5 17.5 47.5 22.5 52.5 {name=E dir=inout}
            """);

        Assert.Equal(["C", "B", "E"], s!.Pins.Select(p => p.Name));
        Assert.Equal("vertical_npn", s.TypeWord);
    }

    /// <summary>
    /// Where a symbol states the netlist's own terminal ORDER, it is authoritative — declaration
    /// order is only the fallback. Measured, 21 of 38 pin-bearing symbols state it, so
    /// both paths are live and both are asserted here.
    /// </summary>
    [Fact]
    public void K11_TerminalsAreOrderedByTheDeclaredPinNumberWhenEveryPinHasOne()
    {
        // Declared out of order on purpose: reading them in file order would give c,a,b.
        var ordered = KitSymbolFileReader.Read("""
            K {type=dev}
            B 5 0 0 5 5 {name=c dir=in sim_pinnumber=3}
            B 5 0 0 5 5 {name=a dir=in sim_pinnumber=1}
            B 5 0 0 5 5 {name=b dir=in sim_pinnumber=2}
            """);
        Assert.Equal(["a", "b", "c"], ordered!.Pins.Select(p => p.Name));

        // One pin without a number makes the set incomplete, so ordering by it would interleave
        // numbered and unnumbered arbitrarily. File order is the honest answer there.
        var partial = KitSymbolFileReader.Read("""
            K {type=dev}
            B 5 0 0 5 5 {name=c dir=in sim_pinnumber=3}
            B 5 0 0 5 5 {name=a dir=in}
            B 5 0 0 5 5 {name=b dir=in sim_pinnumber=2}
            """);
        Assert.Equal(["c", "a", "b"], partial!.Pins.Select(p => p.Name));
    }

    /// <summary>
    /// A template states how the instance is WRITTEN INTO A NETLIST as well as what the device is.
    /// The netlisting keys are not device parameters, and offering them puts boxes in the parameter
    /// editor for things a user cannot usefully change. Both appear on every device of a kit.
    /// </summary>
    [Fact]
    public void K12_NetlistingKeysAreNotOfferedAsParameters()
    {
        var s = KitSymbolFileReader.Read("""
            K {type=nmos
            template="name=M1 spiceprefix=X model=some_fet w=0.15u"}
            B 5 0 0 5 5 {name=d}
            """);

        Assert.Equal(["model", "w"], s!.Parameters.Select(p => p.Name));
    }
}
