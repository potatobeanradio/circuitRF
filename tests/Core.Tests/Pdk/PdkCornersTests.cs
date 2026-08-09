using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Pdk;
using Xunit;

namespace CircuitRF.Core.Tests.Pdk;

/// <summary>
/// The corners a kit offers, and what choosing one binds.
///
/// <para>Fixtures are synthetic but written in the SHAPE a kit uses: a corner file whose every
/// section binds a couple of process constants and then includes the same shared model file. That
/// shape is the whole reason corner selection is a globals substitution.</para>
/// </summary>
public sealed class PdkCornersTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "crf-corners-" + Guid.NewGuid().ToString("N")[..8]);

    public PdkCornersTests()
    {
        Directory.CreateDirectory(_dir);

        // The shared model file every section includes — identical across corners, by construction.
        Write("caps_mod.lib", """
            .subckt plate a b
            .param w=7u l=7u
            C1 a b c={carea*w*l}
            .ends plate
            """);

        Write("capCorners.lib", """
            * corners for the capacitor family
            .LIB cap_typ
            .param carea = 1.5E-15
            .param cpara = 1.0
            .include caps_mod.lib
            .ENDL cap_typ

            .LIB cap_wcs
            .param carea = 1.1*1.5E-15
            .param cpara = 1.1
            .include caps_mod.lib
            .ENDL cap_wcs
            """);

        // A netlist with no sections at all — the overwhelmingly common case.
        Write("plain.lib", """
            .subckt part a b
            R1 a b 1k
            .ends
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_dir, name), text);
    private string Path_(string name) => Path.Combine(_dir, name);

    private IReadOnlyList<PdkCornerAxis> DiscoverAll() =>
        PdkCorners.Discover(Directory.GetFiles(_dir, "*.lib").OrderBy(p => p)
                                     .Select(p => (p, Path.GetFileName(p))));

    // ── discovery ─────────────────────────────────────────────────────────────

    [Fact]
    public void K1_OneAxisPerFileThatDeclaresSections_AndNoneForFilesThatDoNot()
    {
        var axes = DiscoverAll();

        var axis = Assert.Single(axes);
        Assert.Equal("capCorners.lib", axis.AxisId);
        Assert.Equal("capCorners", axis.DisplayName);
        Assert.Equal(["cap_typ", "cap_wcs"], axis.Options);
    }

    [Fact]
    public void K2_TwoFamiliesAreTwoINDEPENDENTAxes_NotOneFlatList()
    {
        Write("resCorners.lib", """
            .LIB res_typ
            .param rsh = 7.0
            .ENDL res_typ
            .LIB res_wcs
            .param rsh = 7.7
            .ENDL res_wcs
            """);

        var axes = DiscoverAll();

        Assert.Equal(2, axes.Count);
        Assert.Equal(["capCorners", "resCorners"], axes.Select(a => a.DisplayName));
        Assert.All(axes, a => Assert.Equal(2, a.Options.Count));
    }

    [Fact]
    public void K3_AFileThatWillNotReadDeclaresNoCornersWeCanTrust()
    {
        // Left unreadable on purpose: an unterminated subcircuit. It must cost itself and nothing
        // else — a kit's other axes still have to come through.
        Write("broken.lib", ".subckt never_closed a b\nR1 a b 1k\n");

        Assert.Single(DiscoverAll());
    }

    // ── what a corner binds ───────────────────────────────────────────────────

    [Fact]
    public void K4_ChoosingASectionBindsThatSectionsOwnValues()
    {
        var typ = PdkCorners.BindingsFor(Path_("capCorners.lib"), "cap_typ", out var pTyp);
        var wcs = PdkCorners.BindingsFor(Path_("capCorners.lib"), "cap_wcs", out var pWcs);

        Assert.Empty(pTyp);
        Assert.Empty(pWcs);

        Assert.Equal("1.5E-15", Value(typ, "carea"));
        Assert.Equal("1", Value(typ, "cpara"));

        // The other corner is genuinely different — a picker that binds the same thing whatever you
        // choose is the failure mode worth guarding, and it looks exactly like success.
        Assert.NotEqual(Value(typ, "carea"), Value(wcs, "carea"));
        Assert.NotEqual(Value(typ, "cpara"), Value(wcs, "cpara"));
    }

    [Fact]
    public void K5_TheOtherSectionsValuesDoNotComeAlong()
    {
        // Sections are ALTERNATIVES. Binding two at once would leave whichever was read last silently
        // deciding the design.
        var typ = PdkCorners.BindingsFor(Path_("capCorners.lib"), "cap_typ", out _);

        Assert.Single(typ, v => v.Name == "carea");
        Assert.Single(typ, v => v.Name == "cpara");
    }

    [Fact]
    public void K6_TheIncludedModelLibraryContributesNoTopLevelBinding()
    {
        // Measured and reproduced here: a model file declares its parameters INSIDE its
        // subcircuits, so a corner's bindings are exactly its own process constants. That is what
        // makes it safe for a caller to read the library through the corner rather than separately.
        var typ = PdkCorners.BindingsFor(Path_("capCorners.lib"), "cap_typ", out _);

        Assert.Equal(["carea", "cpara"], typ.Select(v => v.Name).OrderBy(n => n));
    }

    [Fact]
    public void K7_ASectionTheFileDoesNotOfferBindsNothing_AndSaysSo()
    {
        var bound = PdkCorners.BindingsFor(Path_("capCorners.lib"), "cap_nonesuch", out var problems);

        Assert.Empty(bound);
        Assert.NotEmpty(problems);
    }

    [Fact]
    public void K8_NoSectionChosenBindsNothing_RatherThanEverything()
    {
        // Reading the file whole would bind no section — but going through the reader for a blank
        // choice would still cost a file read per axis per elaboration. More importantly it must
        // never come back with a corner nobody picked.
        Assert.Empty(PdkCorners.BindingsFor(Path_("capCorners.lib"), "", out _));
    }

    // ── a stale selection ─────────────────────────────────────────────────────

    [Fact]
    public void K9_AnOfferedSectionIsRecognised_CaseInsensitively()
    {
        var axis = Assert.Single(DiscoverAll());

        Assert.True(PdkCorners.Offers(axis, "cap_typ"));
        Assert.True(PdkCorners.Offers(axis, "CAP_TYP"));
    }

    [Fact]
    public void K10_ASelectionTheKitNoLongerOffersIsDetectable()
    {
        // A recorded choice outlives the kit it was made against. Silently binding nothing would
        // leave the design at a corner nobody chose, with every number still plausible.
        var axis = Assert.Single(DiscoverAll());

        Assert.False(PdkCorners.Offers(axis, "cap_from_an_older_kit"));
    }

    private static string Value(IEnumerable<CircuitRF.Core.Design.Variable> vars, string name)
        => vars.Single(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Expression;
}
