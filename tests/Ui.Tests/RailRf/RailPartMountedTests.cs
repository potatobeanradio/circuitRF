// ================================================================
//  RailPartMountedTests.cs — brief-railrf-23-mount-and-unmount.md, the design-layer half
//
//  A PART IS MOUNTED OR IT IS NOT, AND THAT IS NOT THE SAME AS UNRESOLVED.
//
//  Depopulating a board is the commonest what-if in power integrity. Until brief 23 it cost an edit
//  to the ARTWORK, which is destructive, is not what the designer means, and throws away the
//  mounting loop the geometry gave the part so it cannot be put back the way it was.
//
//  The window's half of the gate — the shipped example, the cross-check against Q2's own ranking,
//  the baseline and the pin — is MountUnmountTests.cs. What is here is the three claims that need
//  no board and no window: the format, the two states, and the exclusion itself.
//
//  One test per CLAIM the brief makes.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.RailRf;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class RailPartMountedTests(ITestOutputHelper output)
{
    // ══ R-rail23-1a — the flag, and what its ABSENCE means ══════════════════════════════════════

    /// <summary>
    /// <b>Gate 1.</b> <c>Mounted=false</c> saves and loads; and a <c>.crail</c> written before this
    /// flag existed reads every part MOUNTED.
    /// </summary>
    /// <remarks>
    /// The second half is the one that matters and it is asserted against a document with the key
    /// physically absent rather than against a round trip that happens not to write it — that is
    /// what every already-saved document on a user's disk looks like, and a default of false there
    /// would silently depopulate boards nobody touched.
    /// </remarks>
    [Fact]
    public void MountedRoundTrips_AndAnOlderDocumentReadsEveryPartMounted()
    {
        var doc = Document(
            new RailPart { Refdes = "C1", PartNumber = "PN-100N", MountingInductanceHenries = 0.56e-9 },
            new RailPart { Refdes = "C10", PartNumber = "PN-100U", MountingInductanceHenries = 1.2e-9,
                           Mounted = false });

        string json = RailDocumentIo.Serialize(doc);
        output.WriteLine(json);

        var back = RailDocumentIo.Deserialize(json).Rails[0];
        Assert.True(back.Parts[0].Mounted);
        Assert.False(back.Parts[1].Mounted);

        // And the number that makes this not a deletion survived with it.
        Assert.Equal(1.2e-9, back.Parts[1].MountingInductanceHenries!.Value, 15);

        // ABSENT MEANS MOUNTED. The mounted row wrote no key at all — that is what keeps an
        // ordinary document free of a line saying nothing — so the same file shape is what every
        // pre-brief-23 .crail already has.
        Assert.DoesNotContain("\"Mounted\": true", json, StringComparison.Ordinal);

        string older = System.Text.RegularExpressions.Regex.Replace(
            json, ",\\s*\"Mounted\": false", "");
        Assert.DoesNotContain("Mounted", older, StringComparison.Ordinal);
        Assert.All(RailDocumentIo.Deserialize(older).Rails[0].Parts, p => Assert.True(p.Mounted));
    }

    // ══ R-rail23-1d — two states, two spellings ═════════════════════════════════════════════════

    /// <summary>
    /// <b>Gate 5.</b> An unmounted part RESOLVES and is absent; an unresolved part does not resolve
    /// and is reported. Nothing may collapse the two.
    /// </summary>
    /// <remarks>
    /// The whole point of unmounting rather than deleting is that the part keeps its numbers, so
    /// "unmounted" must not travel through the resolver as a failure to model. The unresolved row
    /// beside it is what proves the two are told apart rather than both being "the ones that are
    /// not in the answer".
    /// </remarks>
    [Fact]
    public void UnmountedResolvesAndIsAbsent_WhileUnresolvedIsAFailureToModel()
    {
        var set = new RailPartResolver(Library()).ResolveAll(
            [
                new RailPart { Refdes = "C1",  PartNumber = "PN-100N", MountingInductanceHenries = 0.5e-9 },
                new RailPart { Refdes = "C10", PartNumber = "PN-100N", MountingInductanceHenries = 1.2e-9,
                               Mounted = false },
                new RailPart { Refdes = "C99", PartNumber = "PN-NOT-IN-THE-LIBRARY" },
            ],
            railVoltageV: 3.3);

        var unmounted = set.Models.Single(m => m.Refdes == "C10");
        var unknown   = set.Models.Single(m => m.Refdes == "C99");

        // The unmounted one is a fully resolved part that is simply not fitted…
        Assert.True(unmounted.IsResolved);
        Assert.False(unmounted.Mounted);
        Assert.Equal(1.2e-9, unmounted.MountingInductanceHenries!.Value, 15);

        // …and the unresolved one is the other thing entirely.
        Assert.False(unknown.IsResolved);
        Assert.True(unknown.Mounted);

        // The set tells them apart: Unresolved is about the MOUNTED rows, so C10 is not in it and
        // C99 is, and C10 is named on its own list instead.
        Assert.Equal(["C10"], set.Unmounted.Select(m => m.Refdes));
        Assert.Equal(["C99"], set.Unresolved.Select(m => m.Refdes));
        Assert.Equal(["C1", "C99"], set.Mounted.Select(m => m.Refdes));

        output.WriteLine(set.Summary);
        Assert.Contains("unmounted and not in this answer", set.Summary, StringComparison.Ordinal);
    }

    // ══ R-rail23-1b / R-rail23-4a — out of the model, and out of the ranking ════════════════════

    /// <summary>
    /// <b>Part of gates 2 and 9, at the level where there is no board to confuse it.</b> An
    /// unmounted part contributes no branch, is absent from Q2's ranking rather than ranked at
    /// zero, and is NAMED on the result.
    /// </summary>
    /// <remarks>
    /// <b>Absent, not ranked at zero</b> (R-rail23-4a): a ranking that included unmounted parts
    /// would report what removing an absent part would cost, which is nothing, which reads exactly
    /// like a part that is not earning its place.
    ///
    /// <para>And it is named. A curve that has quietly lost a decoupling capacitor looks entirely
    /// normal, so the one thing this feature must never do is subtract a part from an answer
    /// without saying which.</para>
    /// </remarks>
    [Fact]
    public void AnUnmountedPartLeavesTheModelAndTheRanking_AndIsNamedOnTheResult()
    {
        var withBoth = Sweep(mountedC2: true);
        var without  = Sweep(mountedC2: false);

        Assert.Contains(withBoth.Removal, r => r.Name.StartsWith("C2 ", StringComparison.Ordinal));
        Assert.DoesNotContain(without.Removal, r => r.Name.StartsWith("C2 ", StringComparison.Ordinal));
        Assert.Contains(without.Removal, r => r.Name.StartsWith("C1 ", StringComparison.Ordinal));

        // It left the model, so the curve moved — an unmounted part that changed nothing would not
        // be evidence of anything.
        double a = withBoth.Ports[0].MagnitudeOhms.Max();
        double b = without.Ports[0].MagnitudeOhms.Max();
        output.WriteLine($"|Z| max fitted {a:0.####} Ω, depopulated {b:0.####} Ω");
        Assert.True(b > a, "removing a capacitor has to raise the rail's impedance somewhere.");

        Assert.Contains(without.Notes, n => n.Contains("UNMOUNTED", StringComparison.Ordinal)
                                         && n.Contains("C2", StringComparison.Ordinal));
        Assert.DoesNotContain(withBoth.Notes, n => n.Contains("UNMOUNTED", StringComparison.Ordinal));
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────

    private static PdnSweepResult Sweep(bool mountedC2)
    {
        var rail = Rail(mountedC2);

        return PdnSweep.Run(new PdnSweepRequest
        {
            Rail  = rail,
            Parts = new RailPartResolver(Library()).ResolveAll(rail.Parts, rail.NominalVoltageV),
            Sources = [RailSourceLife.Of(rail.Sources[0], 0, null)],
            Model = CircuitRF.Design.Layout.Pdn.PdnModelKind.Fast,
        });
    }

    private static RailSpec Rail(bool mountedC2)
    {
        var rail = new RailSpec
        {
            Name = "+3V3",
            NetName = "+3V3",
            ImpedanceTarget = RailTarget.OfFlatImpedance(milliohms: 200),
        };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "U1", Pin = "IN" },
            OpenCircuitVoltageV = 3.3,
            SeriesResistanceOhms = 0.05,
            SeriesInductanceHenries = 1e-6,
        });
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U2", Pin = "VDD" } });
        rail.Parts.Add(new RailPart { Refdes = "C1", PartNumber = "PN-100N",
                                      MountingInductanceHenries = 0.6e-9 });
        rail.Parts.Add(new RailPart { Refdes = "C2", PartNumber = "PN-100U",
                                      MountingInductanceHenries = 1.2e-9, Mounted = mountedC2 });
        return rail;
    }

    private static PartLibrary Library()
    {
        var library = new PartLibrary();
        library.Rows.Add(new PartLibraryRow
        {
            PartNumber = "PN-100N", CapacitanceFarads = 100e-9,
            SelfResonantFrequencyHz = 20e6, DielectricClass = "X7R",
        });
        library.Rows.Add(new PartLibraryRow
        {
            PartNumber = "PN-100U", CapacitanceFarads = 100e-6,
            SelfResonantFrequencyHz = 0.3e6, DielectricClass = "X5R",
        });
        return library;
    }

    private static RailDocument Document(params RailPart[] parts)
    {
        var doc = new RailDocument { Name = "fixture" };
        var rail = new RailSpec { Name = "+3V3", NetName = "+3V3" };
        rail.Sources.Add(new RailSource
        {
            Anchor = new RailPortAnchor { Refdes = "U1", Pin = "IN" },
            OpenCircuitVoltageV = 3.3,
            SeriesResistanceOhms = 0.05,
            SeriesInductanceHenries = 1e-6,
        });
        rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = "U1", Pin = "VDD" } });
        foreach (var p in parts) rail.Parts.Add(p);
        doc.Rails.Add(rail);
        return doc;
    }
}
