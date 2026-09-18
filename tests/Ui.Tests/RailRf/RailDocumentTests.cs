// ================================================================
//  RailDocumentTests.cs — brief-railrf-1-document.md §8
//
//  Pure model tests: no window, no app host, no Avalonia. Everything under test lives in
//  src/Design/RailRf, which is below the UI firewall, and these are here rather than in a project of
//  their own because that is where src/Design's existing tests already are.
//
//  One test per CLAIM the brief makes, not one per field.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public class RailDocumentTests
{
    // ── the fixture: §8's own document, and the shape every other test narrows from ──────────

    /// <summary>
    /// Two rails, three sources, five loads, a mask, four aggressors — and <b>a regulator in two
    /// rows</b>: <c>U2</c> is a load on <c>+3V3</c> and a source on <c>+1V8</c>, which is the only
    /// thing in the model that links the two rails.
    /// </summary>
    private static RailDocument Fixture()
    {
        var doc = new RailDocument
        {
            Name           = "evk_compact",
            ArtworkCellRef = "cells/evk_board",
            TechnologyRef  = "stackup.ctech",
            PartLibraryRef = "parts/library.csv",
            Settings = new RailSettings { ViaPlatingThicknessMicrometres = 25.0 },
        };

        var input = new RailSpec
        {
            Name            = "+3V3",
            NetName         = "+3V3",
            ReferenceLayer  = new LayerKey(2, 0),
            ReferenceExtent = RailReferenceExtent.AsImported,
            Band            = new RailBand(1e4, 2e8, 201, true),
            DropBudget      = RailTarget.OfDropBudget(80),
            ImpedanceTarget = RailTarget.OfFlatImpedance(50),
        };
        input.Sources.Add(new RailSource
        {
            Anchor                  = new RailPortAnchor { Refdes = "BT1", Pin = "1" },
            OpenCircuitVoltageV     = 3.7,
            SeriesResistanceOhms    = 2.5,
            SeriesInductanceHenries = 8e-9,
        });
        // The regulator's INPUT field — a load on this rail, and the row that links the two.
        input.Loads.Add(new RailLoad
        {
            Anchor                 = new RailPortAnchor { Refdes = "U2", Pin = "VIN" },
            RegulatorInputCurrentA = 0.085,
            MinimumInputVoltageV   = 2.9,
        });
        input.Loads.Add(new RailLoad
        {
            Anchor     = new RailPortAnchor { Refdes = "U3", Pin = "VCC" },
            DcCurrentA = 0.015,
        });
        input.Aggressors.Add(new RailAggressor("32 kHz xtal", 32768, 1));
        input.Aggressors.Add(new RailAggressor("converter", 3.3e6, 8)
                             { Origin = RailAggressorOrigin.Bom });

        var output = new RailSpec
        {
            Name            = "+1V8",
            NetName         = "+1V8",
            ReferenceLayer  = new LayerKey(2, 0),
            ReferenceExtent = RailReferenceExtent.FilledToOutline,
            Band            = new RailBand(1e3, 5e8, 401, true),
            DropBudget      = RailTarget.OfDropBudget(40),
            ImpedanceTarget = RailTarget.OfTransient(deltaIAmps: 0.5, deltaVVolts: 0.036,
                                                     riseTimeSeconds: 2e-9),
        };
        // The regulator's OUTPUT field — a source on this rail. Same refdes, second row.
        output.Sources.Add(new RailSource
        {
            Anchor               = new RailPortAnchor { Refdes = "U2", Pin = "VOUT" },
            OpenCircuitVoltageV  = 1.8,
            SeriesResistanceOhms = 0.012,
        });
        output.Sources.Add(new RailSource
        {
            Anchor        = new RailPortAnchor { Refdes = "U5", Pin = "OUT" },
            TouchstoneRef = "parts/ldo_zout.s1p",
        });
        output.Loads.Add(new RailLoad
        {
            Anchor       = new RailPortAnchor { Refdes = "U1", Pin = "VDD" },
            DcCurrentA   = 0.120,
            PeakCurrentA = 0.5,
            Mask         = RailTarget.OfMask(
            [
                new RailMaskPoint(1e4, 0.050),
                new RailMaskPoint(1e6, 0.050),
                new RailMaskPoint(2e8, 0.200),
            ]),
        });
        output.Loads.Add(new RailLoad
        {
            Anchor     = new RailPortAnchor { Refdes = "U4", Pin = "VDDIO" },
            DcCurrentA = 0.030,
        });
        // The coordinate fallback, and an observation port: no current at all.
        output.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Point = (1_250_000, 880_000) } });

        output.Aggressors.Add(new RailAggressor("38.4 MHz xtal", 38.4e6, 3));
        output.Aggressors.Add(new RailAggressor("PA envelope", 1.2e6, 5)
                              { Origin = RailAggressorOrigin.Bom });

        doc.Rails.Add(input);
        doc.Rails.Add(output);
        return doc;
    }

    // ── §8: round trip ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole document survives a write and a read element-wise, and a second write of what came
    /// back is byte-identical — so the format carries everything the model holds, and opening a file
    /// and saving it changes nothing on disk.
    /// </summary>
    [Fact]
    public void ADocumentWithEveryFeature_RoundTripsElementWise_AndReSerialisesByteIdentically()
    {
        var doc   = Fixture();
        string j1 = RailDocumentIo.Serialize(doc);
        var back  = RailDocumentIo.Deserialize(j1);
        string j2 = RailDocumentIo.Serialize(back);

        Assert.Equal(j1, j2);

        Assert.Equal(doc.Name,           back.Name);
        Assert.Equal(doc.ArtworkCellRef, back.ArtworkCellRef);
        Assert.Equal(doc.TechnologyRef,  back.TechnologyRef);
        Assert.Equal(doc.PartLibraryRef, back.PartLibraryRef);
        Assert.Equal(doc.Settings.CopperTemperatureCelsius,       back.Settings.CopperTemperatureCelsius);
        Assert.Equal(doc.Settings.ViaPlatingThicknessMicrometres, back.Settings.ViaPlatingThicknessMicrometres);

        Assert.Equal(doc.Rails.Count, back.Rails.Count);
        foreach (var (a, b) in doc.Rails.Zip(back.Rails))
        {
            Assert.Equal(a.Name,            b.Name);
            Assert.Equal(a.NetName,         b.NetName);
            Assert.Equal(a.ReferenceLayer,  b.ReferenceLayer);
            Assert.Equal(a.ReferenceExtent, b.ReferenceExtent);
            Assert.Equal(a.Band,            b.Band);
            Assert.Equal(a.DropBudget,      b.DropBudget);
            Assert.Equal(a.ImpedanceTarget, b.ImpedanceTarget);
            Assert.Equal(a.Sources,         b.Sources);
            Assert.Equal(a.Loads,           b.Loads);      // records, mask points included
            Assert.Equal(a.Aggressors,      b.Aggressors);
        }

        // The features the fixture exists to carry actually made it, rather than all being absent on
        // both sides — which the comparison above would have reported as a pass.
        Assert.Equal(3, back.Rails.Sum(r => r.Sources.Count));
        Assert.Equal(5, back.Rails.Sum(r => r.Loads.Count));
        Assert.Equal(4, back.Rails.Sum(r => r.Aggressors.Count));
        Assert.Equal(3, back.Rails[1].Loads[0].Mask!.Mask!.Count);
        Assert.Contains(back.Rails[1].Sources, s => s.Anchor.Point is null && s.Anchor.Refdes == "U2");
        Assert.Contains(back.Rails[1].Loads,   l => l.Anchor.Point == (1_250_000L, 880_000L));
        Assert.Equal(RailAggressorOrigin.Bom, back.Rails[0].Aggressors[1].Origin);
    }

    // ── R-rail1-1: a pad OR a coordinate, and exactly one ─────────────────────────────────────

    /// <summary>
    /// An anchor carrying both forms, and one carrying neither, are each refused <b>by name</b> —
    /// the rail and the row. A silently preferred one of the two is the bug
    /// <see cref="RailPortAnchor"/> exists to prevent: after a re-layout a coordinate points at
    /// different copper, and brief 16's comparison would then compare nothing while looking entirely
    /// normal.
    /// </summary>
    [Fact]
    public void AnAnchorThatIsBothAPadAndACoordinate_OrNeither_IsRefusedByName()
    {
        var both = OneLoadRail(new RailPortAnchor
                                { Refdes = "U1", Pin = "VDD", Point = (10, 20) });
        var ex1 = Assert.Throws<InvalidDataException>(() => RailDocumentIo.Serialize(both));
        Assert.Contains("+1V8", ex1.Message);
        Assert.Contains("load 1", ex1.Message);
        Assert.Contains("U1.VDD", ex1.Message);

        var neither = OneLoadRail(new RailPortAnchor());
        var ex2 = Assert.Throws<InvalidDataException>(() => RailDocumentIo.Serialize(neither));
        Assert.Contains("+1V8", ex2.Message);
        Assert.Contains("load 1", ex2.Message);
        Assert.Contains("refdes", ex2.Message);

        // And it is a refusal on the way IN as well, so a hand-edited file cannot get past it.
        string json = RailDocumentIo.Serialize(OneLoadRail(new RailPortAnchor { Refdes = "U1" }))
                                   .Replace("\"Refdes\": \"U1\"",
                                            "\"Refdes\": \"U1\",\n          \"PointX\": 10,\n          \"PointY\": 20");
        Assert.Contains("U1", Assert.Throws<InvalidDataException>(
            () => RailDocumentIo.Deserialize(json)).Message);
    }

    /// <summary>
    /// R-rail1-2 — an anchor is CAPABLE of naming a pin field. Resolving <c>U1.VDD</c> to the six
    /// pads that net reaches is brief 3's extractor; what brief 1 owns is that the document can say
    /// it, so the note's own worked example is representable.
    /// </summary>
    [Fact]
    public void AnAnchorCanNameAPinField_AndAPinFieldIsSpeltLikeAnySinglePin()
    {
        var field  = new RailPortAnchor { Refdes = "U1", Pin = "VDD" };
        var single = new RailPortAnchor { Refdes = "U1", Pin = "7" };

        Assert.True(field.IsPad);
        Assert.True(single.IsPad);
        Assert.Null(field.Refusal("x"));
        Assert.Null(single.Refusal("x"));
        Assert.Equal("U1.VDD", field.Describe());
    }

    // ── R-rail1-4: the solve order, and the cycle refusal ─────────────────────────────────────

    /// <summary>
    /// A three-rail ladder — cell → regulator → regulator — resolves to the ladder's own order, and
    /// the DECLARATION order is deliberately not it: the rails are declared backwards here, so a
    /// pass would be impossible if <see cref="RailOrder"/> were returning what it was given.
    /// </summary>
    [Fact]
    public void AThreeRailLadder_ResolvesToTheLaddersOwnOrder()
    {
        var doc = new RailDocument();
        doc.Rails.Add(Ladder("+1V2", sourceRefdes: "U3", loadRefdes: null));
        doc.Rails.Add(Ladder("+1V8", sourceRefdes: "U2", loadRefdes: "U3"));
        doc.Rails.Add(Ladder("VBAT", sourceRefdes: "BT1", loadRefdes: "U2"));

        var order = RailOrder.Resolve(doc);

        Assert.Null(order.Refusal);
        Assert.Equal(["VBAT", "+1V8", "+1V2"], order.Order);
    }

    /// <summary>
    /// A rail pair where each is a load on the other is <b>refused, naming both rails and the
    /// refdes</b> — and the refusal arrives as a SENTENCE rather than as a thrown type, because the
    /// callers are a status strip and a verb's stderr.
    ///
    /// <para>The refusal is load-bearing and not a limitation to be lifted (note §9): solving the
    /// rails together needs a regulator's forward transfer and its PSRR, which §8.2 records as
    /// frequently impossible to obtain.</para>
    /// </summary>
    [Fact]
    public void TwoRailsThatEachLoadTheOther_AreRefusedAsASentenceNamingBothRailsAndTheRefdes()
    {
        var doc = new RailDocument();
        doc.Rails.Add(Ladder("+3V3", sourceRefdes: "U2", loadRefdes: "U7"));
        doc.Rails.Add(Ladder("+1V8", sourceRefdes: "U7", loadRefdes: "U2"));

        var order = RailOrder.Resolve(doc);

        Assert.NotNull(order.Refusal);
        Assert.Empty(order.Order);
        Assert.Contains("+3V3", order.Refusal);
        Assert.Contains("+1V8", order.Refusal);
        Assert.True(order.Refusal!.Contains("U2") || order.Refusal.Contains("U7"),
                    $"the refusal names neither refdes: {order.Refusal}");
    }

    // ── R-rail1-5: what a regulator needs typed, and what happens when it is not ──────────────

    /// <summary>
    /// A regulator load with no <c>MinimumInputVoltageV</c> round-trips as null and <b>nothing in the
    /// model substitutes a value</b>. Without it railRF can report the input rail's drop and cannot
    /// report that the drop BROKE the rail downstream, which is the finding the whole chain exists to
    /// produce — brief 5 says on the report which rails had no minimum, and it can only do that
    /// because the absence survives the file.
    /// </summary>
    [Fact]
    public void ARegulatorWithNoMinimumInputVoltage_RoundTripsAsNull_AndNothingSubstitutesAValue()
    {
        var doc = Fixture();
        doc.Rails[0].Loads[0] = doc.Rails[0].Loads[0] with { MinimumInputVoltageV = null };

        var back = RailDocumentIo.Deserialize(RailDocumentIo.Serialize(doc));
        var regulator = back.Rails[0].Loads[0];

        Assert.Equal("U2", regulator.Anchor.Refdes);
        Assert.Null(regulator.MinimumInputVoltageV);
        Assert.Equal(0.085, regulator.RegulatorInputCurrentA);        // the stated one still survives
        Assert.DoesNotContain("MinimumInputVoltage", RailDocumentIo.Serialize(back));
    }

    // ── R-rail1-7: a load with no current is an observation port ──────────────────────────────

    /// <summary>
    /// The property, stated as the brief states it: three loads, one of them currentless, solves with
    /// <b>two</b> current injections and reports <b>three</b> ports. A defaulted zero and a stated
    /// zero are the same number and mean different things.
    /// </summary>
    [Fact]
    public void ALoadWithNoCurrent_IsAnObservationPort_CountedButNotInjected()
    {
        var rail = RailDocumentIo.Deserialize(RailDocumentIo.Serialize(Fixture())).Rails[1];

        Assert.Equal(3, rail.Loads.Count);
        Assert.Equal(3, rail.ObservationPorts.Count());
        Assert.Equal(2, rail.DcInjections.Count());
        Assert.Equal(2, rail.Loads.Count(l => l.DcCurrentA is not null));
        Assert.Single(rail.Loads, l => l.IsObservationOnly);

        // A STATED zero is a load, and is not the same row as the one that states nothing.
        var stated = rail.Loads[0] with { DcCurrentA = 0.0 };
        Assert.False(stated.IsObservationOnly);
    }

    // ── R-rail1-8: the transient target's derivation is a pure function ───────────────────────

    /// <summary>
    /// A transient target derives the same flat Z and band top from the same ΔI/ΔV/rise on two
    /// separate reads. The derivation belongs to the document and not to the window, because the same
    /// document opened headlessly has to produce the same target.
    /// </summary>
    [Fact]
    public void ATransientTarget_DerivesTheSameFlatZAndBandTop_OnTwoSeparateReads()
    {
        string json = RailDocumentIo.Serialize(Fixture());

        var a = RailDocumentIo.Deserialize(json).Rails[1].ImpedanceTarget!;
        var b = RailDocumentIo.Deserialize(json).Rails[1].ImpedanceTarget!;

        Assert.Equal(RailTargetKind.Transient, a.Kind);
        Assert.Equal(a.FlatTargetOhms, b.FlatTargetOhms);
        Assert.Equal(a.BandTopHz,      b.BandTopHz);

        // And it is the arithmetic the note names, not merely a repeatable one: ΔV/ΔI, and the
        // 10–90 % knee 0.35/t_rise.
        Assert.Equal(0.036 / 0.5,          a.FlatTargetOhms!.Value, 12);
        Assert.Equal(0.35  / 2e-9,         a.BandTopHz!.Value,      6);
    }

    // ── §8: forward compatibility ────────────────────────────────────────────────────────────

    /// <summary>
    /// A <c>.crail</c> carrying a key this version does not know reads without loss of the keys it
    /// does — the <c>.ctech</c> precedent — and a document written by this version and read by it
    /// changes no byte.
    /// </summary>
    [Fact]
    public void AnUnknownKey_IsIgnoredWithoutLosingTheKeysBesideIt()
    {
        string json = RailDocumentIo.Serialize(Fixture());
        string withUnknown = json.Replace("\"FormatVersion\": 1,",
                                          "\"FormatVersion\": 1,\n  \"SomethingFromLater\": { \"a\": 1 },");

        var back = RailDocumentIo.Deserialize(withUnknown);

        Assert.Equal(2, back.Rails.Count);
        Assert.Equal("evk_compact", back.Name);
        Assert.Equal(json, RailDocumentIo.Serialize(back));
    }

    /// <summary>A file from a NEWER circuitRF is refused rather than half-read — the
    /// <c>.cem</c>/<c>.ctech</c> rule, and the reason a format version is an integer.</summary>
    [Fact]
    public void AFileFromANewerVersion_IsRefused()
    {
        string json = RailDocumentIo.Serialize(Fixture())
                                   .Replace("\"FormatVersion\": 1", "\"FormatVersion\": 2");
        Assert.Contains("newer", Assert.Throws<InvalidDataException>(
            () => RailDocumentIo.Deserialize(json)).Message);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static RailDocument OneLoadRail(RailPortAnchor anchor)
    {
        var doc  = new RailDocument();
        var rail = new RailSpec { Name = "+1V8" };
        rail.Loads.Add(new RailLoad { Anchor = anchor, DcCurrentA = 0.1 });
        doc.Rails.Add(rail);
        return doc;
    }

    /// <summary>One rung: a rail fed by <paramref name="sourceRefdes"/> and (where there is one)
    /// feeding the next through <paramref name="loadRefdes"/>.</summary>
    private static RailSpec Ladder(string name, string sourceRefdes, string? loadRefdes)
    {
        var rail = new RailSpec { Name = name };
        rail.Sources.Add(new RailSource { Anchor = new RailPortAnchor { Refdes = sourceRefdes, Pin = "OUT" } });
        if (loadRefdes is not null)
            rail.Loads.Add(new RailLoad { Anchor = new RailPortAnchor { Refdes = loadRefdes, Pin = "VIN" } });
        return rail;
    }
}
