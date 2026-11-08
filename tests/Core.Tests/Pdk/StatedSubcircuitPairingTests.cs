using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Pdk;
using Xunit;

namespace CircuitRF.Core.Tests.Pdk;

/// <summary>
/// brief-gdf-1b D1: a record symbol whose netlist line calls a subcircuit BY NAME is paired with that
/// subcircuit — strictly, and never with a copy in a test folder or a name-alike. Synthetic kits.
/// </summary>
public sealed class StatedSubcircuitPairingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "crf-stated-" + Guid.NewGuid().ToString("N")[..8]);

    public StatedSubcircuitPairingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string relative, string text)
    {
        string full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    /// <summary>A two-pin symbol whose line calls <c>amp_core</c>.</summary>
    private void WriteSymbol() => Write("symbols/amp.sym", """
        v {version=1.2}
        K {type=subcircuit
        format="@name @pinlist amp_core g=@g"
        template="name=X1
        g=2"
        }
        B 5 -2.5 -2.5 2.5 2.5 {name=in dir=inout}
        B 5 37.5 -2.5 42.5 2.5 {name=out dir=inout}
        """);

    private PdkPart Amp() => PdkImporter.Import(_root).Parts.Single(p => p.Id == "amp");

    /// <summary>
    /// Test 6. The one definition the format names is paired, and a same-named copy in a regression
    /// folder does not make it ambiguous: checking folders never define a part.
    /// </summary>
    [Fact]
    public void ARecordSymbol_PairsWithTheSubcircuitItsFormatNames_NotATestFolderCopy()
    {
        WriteSymbol();
        Write("models/parts.lib", """
            .subckt amp_core in out g=1
            R1 in out {g}
            .ends amp_core
            .subckt amp_wide in out
            R1 in out 50
            .ends amp_wide
            """);
        Write("testing/regression/deck.spice", """
            .subckt amp_core in out
            R1 in out 1
            .ends amp_core
            """);

        var part = Amp();

        Assert.Equal("X", part.Element);
        Assert.Equal("amp_core", part.DefinitionCell);
        Assert.Equal("models/parts.lib", part.DefinitionRelativePath);
    }

    /// <summary>
    /// A kit that defines the called subcircuit once PER CORNER is paired with the definition its
    /// nominal corner reaches — through however many inclusions — so every other corner remains a
    /// selection away. The alternatives alone are a tie, and a tie is never guessed.
    /// </summary>
    [Fact]
    public void ANameDefinedOncePerCorner_PairsWithTheNominalCornersDefinition()
    {
        WriteSymbol();
        foreach (string corner in new[] { "tt", "ss" })
            Write($"models/amp__{corner}.lib", $"""
                .subckt amp_core in out g=1
                R1 in out {(corner == "tt" ? 10 : 12)}
                .ends amp_core
                """);
        Write("models/tt.inc", ".include amp__tt.lib\n");
        Write("models/ss.inc", ".include amp__ss.lib\n");
        Write("models/corners.lib", """
            .lib tt
            .include tt.inc
            .endl tt
            .lib ss
            .include ss.inc
            .endl ss
            """);

        Assert.Equal("models/amp__tt.lib", Amp().DefinitionRelativePath);
    }

    /// <summary>
    /// A symbol stating a body terminal pairs with a definition that has only the drawn pins, as
    /// extraction accepts it: requiring pins plus extras left that part unpaired.
    /// </summary>
    [Fact]
    public void ASymbolStatingABodyTerminal_PairsWithADefinitionOfItsPinsAlone()
    {
        Write("symbols/amp.sym", """
            v {version=1.2}
            K {type=subcircuit
            format="@name @pinlist @body amp_core g=@g"
            template="name=X1 body=sub! g=2"
            }
            B 5 -2.5 -2.5 2.5 2.5 {name=in dir=inout}
            B 5 37.5 -2.5 42.5 2.5 {name=out dir=inout}
            """);
        Write("models/parts.lib", """
            .subckt amp_core in out g=1
            R1 in out {g}
            .ends amp_core
            """);

        Assert.Equal("amp_core", Amp().DefinitionCell);
    }

    /// <summary>A folder of real models whose name merely begins with "test" letters is not a checking folder.</summary>
    [Fact]
    public void AModelFolderWhoseNameBeginsWithTestLetters_StillDefinesParts()
    {
        WriteSymbol();
        Write("testchip_models/parts.lib", """
            .subckt amp_core in out g=1
            R1 in out {g}
            .ends amp_core
            """);

        Assert.Equal("testchip_models/parts.lib", Amp().DefinitionRelativePath);
    }

    /// <summary>
    /// A primitive part whose card is declared once per corner file points at the file its nominal
    /// corner reads — the tie-break a stated subcircuit already had. Without it the part was unpaired.
    /// </summary>
    [Fact]
    public void APrimitiveCardDeclaredOncePerCorner_PointsAtTheNominalCornersFile()
    {
        Write("symbols/dprim.sym", """
            v {version=1.2}
            K {type=diode
            format="@name @pinlist @model"
            template="name=D1 model=dcard"
            }
            B 5 -2.5 -2.5 2.5 2.5 {name=p dir=inout}
            B 5 37.5 -2.5 42.5 2.5 {name=n dir=inout}
            """);
        foreach (string corner in new[] { "tt", "ss" })
            Write($"models/d__{corner}.lib", $".model dcard d is={(corner == "tt" ? "1e-15" : "2e-15")}\n");
        Write("models/corners.lib", """
            .lib tt
            .include d__tt.lib
            .endl tt
            .lib ss
            .include d__ss.lib
            .endl ss
            """);

        var part = PdkImporter.Import(_root).Parts.Single(p => p.Id == "dprim");
        Assert.Equal("models/d__tt.lib", part.DefinitionRelativePath);
    }

    /// <summary>
    /// One library shipped once per simulator, the shape a multi-simulator kit has: each tree holds the
    /// same-named library beside its own corner file, whose first section includes it. The copies share a
    /// file name and each is its own tree's nominal, so only the main tree (the one defining the most
    /// subcircuits; here it also ships a mismatch variant) can decide.
    /// </summary>
    private void WriteOneLibraryPerSimulator(bool mainTreeIsLarger, string library)
    {
        Write("sim_a/models/corner.lib", mainTreeIsLarger
            ? ".lib typ\n.include parts.lib\n.endl typ\n.lib typ_mm\n.include parts_mm.lib\n.endl typ_mm\n"
            : ".lib typ\n.include parts.lib\n.endl typ\n");
        Write("sim_b/models/corner.lib", ".lib typ\n.include parts.lib\n.endl typ\n");
        Write("sim_a/models/parts.lib", library);
        Write("sim_b/models/parts.lib", library);
        if (mainTreeIsLarger) Write("sim_a/models/parts_mm.lib", library);
    }

    /// <summary>The symbol of such a kit: the call written as a prefix and a model attribute.</summary>
    private void WriteModelSymbol() => Write("symbols/amp.sym", """
        v {version=1.2}
        K {type=subcircuit
        format="@spiceprefix@name @pinlist @model g=@g"
        template="name=M1
        model=amp_core
        spiceprefix=X
        g=2"
        }
        B 5 -2.5 -2.5 2.5 2.5 {name=in dir=inout}
        B 5 37.5 -2.5 42.5 2.5 {name=out dir=inout}
        """);

    private const string AmpLibrary = """
        .subckt amp_core in out g=1
        .if (g > 1)
        R1 in out {g}
        .else
        R1 in out 1
        .endif
        .ends amp_core
        """;

    /// <summary>Every device such a kit defined in both trees was left unpaired; it pairs with the main tree's copy.</summary>
    [Fact]
    public void ALibraryShippedOncePerSimulator_PairsWithTheMainTreesCopy()
    {
        WriteModelSymbol();
        WriteOneLibraryPerSimulator(mainTreeIsLarger: true, AmpLibrary);

        var part = Amp();
        Assert.Equal("amp_core", part.DefinitionCell);
        Assert.Equal("sim_a/models/parts.lib", part.DefinitionRelativePath);
    }

    /// <summary>Two trees of equal size are a tie, and a tie is never guessed.</summary>
    [Fact]
    public void ALibraryShippedOncePerSimulator_TreesOfEqualSize_StayUnpaired()
    {
        WriteModelSymbol();
        WriteOneLibraryPerSimulator(mainTreeIsLarger: false, AmpLibrary);

        Assert.Null(Amp().DefinitionRelativePath);
    }

    /// <summary>A primitive part's card, shipped once per simulator, points at the main tree's file.</summary>
    [Fact]
    public void APrimitiveCardShippedOncePerSimulator_PointsAtTheMainTreesFile()
    {
        Write("symbols/dprim.sym", """
            v {version=1.2}
            K {type=diode
            format="@name @pinlist @model"
            template="name=D1 model=dcard"
            }
            B 5 -2.5 -2.5 2.5 2.5 {name=p dir=inout}
            B 5 37.5 -2.5 42.5 2.5 {name=n dir=inout}
            """);
        WriteOneLibraryPerSimulator(mainTreeIsLarger: true, ".model dcard d is=1e-15\n" + AmpLibrary);

        var part = PdkImporter.Import(_root).Parts.Single(p => p.Id == "dprim");
        Assert.Equal("sim_a/models/parts.lib", part.DefinitionRelativePath);
    }
}
