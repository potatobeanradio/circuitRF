using System;
using System.Linq;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Core.Pdk;
using Xunit;

namespace CircuitRF.Core.Tests.Netlist;

/// <summary>
/// Learning which CORNERS a kit offers — the fact that has to exist before any UI can offer a choice.
///
/// <para>A corner is a named set of global bindings: a <c>.lib</c> section binds a few process
/// parameters and includes the same shared model file every other section does. So discovering the
/// corners is discovering the section names, grouped by the file that declares them.</para>
///
/// <para>Synthetic fixtures; the repository commits no third-party kit data.</para>
/// </summary>
public sealed class SpiceCornerDiscoveryTests
{
    // ── the reader reports the alternatives ───────────────────────────────────

    [Fact]
    public void C1_SectionNamesAreReported_InDeclarationOrder()
    {
        var r = SpiceNetlistReader.Read("""
            .LIB cap_typ
            .param carea = 1.5E-15
            .ENDL cap_typ
            .LIB cap_wcs
            .param carea = 1.65E-15
            .ENDL cap_wcs
            .LIB cap_bcs
            .param carea = 1.35E-15
            .ENDL cap_bcs
            """);

        var axis = Assert.Single(r.Sections);
        Assert.Equal(["cap_typ", "cap_wcs", "cap_bcs"], axis.Names);
    }

    [Fact]
    public void C2_AFileDeclaringNoSectionOffersNoAxis()
    {
        // Nearly every netlist is this. An axis per file regardless would put an empty corner picker
        // in front of every user of every kit.
        var r = SpiceNetlistReader.Read("""
            .subckt part a b
            R1 a b 1k
            .ends
            """);

        Assert.Empty(r.Sections);
    }

    [Fact]
    public void C3_SectionsAreCollectedWhileTheyAreBeingSKIPPED()
    {
        // The pass that learns what the alternatives are is the one that deliberately reads none of
        // them: a file read whole skips every section, because choosing one nobody asked for is a
        // guess. Collecting the names anywhere downstream of that skip would collect nothing.
        var r = SpiceNetlistReader.Read("""
            .LIB typ
            .subckt only_in_typ a b
            R1 a b 1k
            .ends
            .ENDL typ
            """);

        Assert.Equal(["typ"], Assert.Single(r.Sections).Names);
        Assert.Empty(r.Library.Cells);          // and nothing was read out of it
    }

    [Fact]
    public void C4_NoSectionIsFilteredOutForNotLookingLikeACorner()
    {
        // The kit declaring them as alternatives IS the semantic. Matching names against _typ/_wcs
        // would encode one supplier's habits and go blank on the next kit.
        var r = SpiceNetlistReader.Read("""
            .LIB anything_at_all
            .param x = 1
            .ENDL anything_at_all
            """);

        Assert.Equal(["anything_at_all"], Assert.Single(r.Sections).Names);
    }

    // ── the classifier has to SEE the file in the first place ─────────────────

    [Fact]
    public void C5_AFileThatIsNothingButSectionsIsStillRecognised()
    {
        // A corner file contains no '.subckt' and no '.model' — it is nothing but sections binding
        // parameters and including the shared model file. Keyed on those two markers alone it
        // classifies as unrecognised and its corners are invisible to the import.
        var asset = Recognize("capCorners.lib", """
            * a corner file
            .LIB cap_typ
            .param carea = 1.5E-15
            .include capacitors.lib
            .ENDL cap_typ
            """);

        Assert.NotNull(asset);
        Assert.Equal(PdkAssetKind.Netlist, asset!.Kind);
    }

    [Fact]
    public void C6_AMarkerPastTheOldPeekWindowIsStillFound()
    {
        // The regression this exists for, measured rather than imagined: a kit declares its
        // first '.lib' at byte 4,114 — eighteen bytes past the old 4,096-byte window — behind a
        // license header and a long parameter block, and was silently classified as unrecognised.
        string header = string.Join('\n', Enumerable.Repeat("* license text and preamble", 200));
        Assert.True(header.Length > 4096, "the fixture must actually exceed the old window");

        var asset = Recognize("mosCorners.lib", header + "\n.LIB mos_tt\n.param x = 1\n.ENDL mos_tt\n");

        Assert.NotNull(asset);
        Assert.Equal(PdkAssetKind.Netlist, asset!.Kind);
    }

    [Fact]
    public void C7_ADocumentThatMerelyMentionsADirectiveIsNotANetlist()
    {
        // The widening must not turn prose into a netlist. Markers count at LINE START only.
        var a = Recognize("readme.txt",
            "This kit ships corner files. Use .lib to select one, and see .subckt for the devices.");
        Assert.True(a is null || a.Kind != PdkAssetKind.Netlist,
                    $"prose classified as {a?.Kind} by '{a?.FormatName}'");
    }

    /// <summary>Runs the real recogniser chain over one synthetic file.</summary>
    private static PdkAsset? Recognize(string path, string text)
    {
        foreach (var r in PdkFormatRegistry.All)
        {
            var a = r.Recognize(path, () => text);
            if (a is not null) return a;
        }
        return null;
    }
}
