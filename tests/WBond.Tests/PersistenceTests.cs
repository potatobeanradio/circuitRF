using System.Text.Json;

namespace CircuitRF.WBond.Tests;

/// <summary>
/// R-wb-11 and R-wb-12 — <c>.wBond</c> I/O and the CSV wirebond-table importer.
/// </summary>
public class PersistenceTests
{
    private static WBondDesign SampleDesign()
    {
        var profile = LoopProfile.BallBond(WBondUnits.ToNm(22.0, WBondUnit.Mil), points: 7);

        var design = new WBondDesign { OperatingTempC = 105.0 };
        design.Profiles.Add(profile);

        var g1 = new WireArray { Name = "G1", Profile = "ball" };
        for (int i = 0; i < 4; i++)
        {
            g1.Wires.Add(profile.CreateWire(
                Point3.Mils(0, i * 6, 4), Point3.Mils(100, i * 6, 1),
                WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold"));
        }

        var d1 = new WireArray { Name = "D1" };
        d1.Wires.Add(profile.CreateWire(
            Point3.Mils(0, 200, 4), Point3.Mils(90, 210, 2),
            WBondUnits.ToNm(1.25, WBondUnit.Mil), "Aluminium"));
        d1.Wires[0].Locked = true;

        design.Arrays.Add(g1);
        design.Arrays.Add(d1);
        return design;
    }

    // ---------------------------------------------------------------- .wBond round trip

    /// <summary>
    /// R-wb-11 — everything the file claims to carry survives a write/read cycle exactly.
    /// Coordinates are integer DBU, so "exactly" means bit-for-bit, not to a tolerance.
    /// </summary>
    [Fact]
    public void WBondIo_RoundTripsTheWholeDesign()
    {
        var original = SampleDesign();
        var restored = WBondIo.Read(WBondIo.Write(original));

        Assert.Equal(original.OperatingTempC, restored.OperatingTempC);
        Assert.Equal(original.GroundPlane.Enabled, restored.GroundPlane.Enabled);
        Assert.Equal(original.Materials.Count, restored.Materials.Count);
        Assert.Equal(original.Arrays.Count, restored.Arrays.Count);
        Assert.Equal(original.WireCount, restored.WireCount);

        for (int a = 0; a < original.Arrays.Count; a++)
        {
            Assert.Equal(original.Arrays[a].Name, restored.Arrays[a].Name);
            Assert.Equal(original.Arrays[a].Profile, restored.Arrays[a].Profile);

            for (int w = 0; w < original.Arrays[a].Wires.Count; w++)
            {
                var before = original.Arrays[a].Wires[w];
                var after = restored.Arrays[a].Wires[w];

                Assert.Equal(before.DiameterNm, after.DiameterNm);
                Assert.Equal(before.Material, after.Material);
                Assert.Equal(before.ProfileBinding, after.ProfileBinding);
                Assert.Equal(before.Locked, after.Locked);
                Assert.Equal(before.Points, after.Points);   // integer DBU — exact
            }
        }

        Assert.Equal(original.Profiles[0].Name, restored.Profiles[0].Name);
        Assert.Equal(original.Profiles[0].LoopHeightNm, restored.Profiles[0].LoopHeightNm);
        Assert.Equal(original.Profiles[0].Shape, restored.Profiles[0].Shape);
    }

    /// <summary>
    /// R-wb-11 — the design survives a round trip well enough that the <b>physics</b> is unchanged.
    /// The sharpest end-to-end statement available: L_arr from the restored design must be
    /// bit-identical, not merely close.
    /// </summary>
    [Fact]
    public void WBondIo_RoundTrip_LeavesTheArrayInductanceBitIdentical()
    {
        var original = SampleDesign();
        var restored = WBondIo.Read(WBondIo.Write(original));

        var before = ArrayReduction.Reduce(InductanceMatrix.Fill(WireMesh.Build(original)), WireMesh.Build(original));
        var after = ArrayReduction.Reduce(InductanceMatrix.Fill(WireMesh.Build(restored)), WireMesh.Build(restored));

        for (int i = 0; i < before.ArrayCount; i++)
            for (int j = 0; j < before.ArrayCount; j++)
                Assert.Equal(before[i, j], after[i, j], 0.0);
    }

    /// <summary>
    /// <b>R-wb-11's central obligation: embedded layout geometry is an OPAQUE passthrough.</b>
    ///
    /// <para>WB-A must preserve it without interpreting a byte — the <c>.clay</c> model lives on the
    /// far side of the UI firewall. The blob here deliberately contains everything that trips a
    /// careless passthrough: nested objects and arrays, nulls, booleans, exponent notation, a
    /// high-precision decimal, unicode, an empty object and an empty array.</para>
    /// </summary>
    [Fact]
    public void WBondIo_EmbeddedGeometry_IsPreservedOpaquely()
    {
        const string blob = """
            {
              "cells": [
                { "name": "bondpad", "layer": 3, "xy": [0, 0, 2540, 2540], "rotation": null },
                { "name": "lead", "flags": { "pdk": false, "flattened": true }, "eps": 1.0E-9 }
              ],
              "precision": 0.30000000000000004,
              "label": "π · µm · 日本語",
              "emptyObject": {},
              "emptyArray": []
            }
            """;

        var design = SampleDesign();
        design.EmbeddedGeometryJson = blob;

        string firstSave = WBondIo.Write(design);
        var reloaded = WBondIo.Read(firstSave);

        Assert.NotNull(reloaded.EmbeddedGeometryJson);

        // Semantic equality: every value survives, including the awkward ones.
        using var before = JsonDocument.Parse(blob);
        using var after = JsonDocument.Parse(reloaded.EmbeddedGeometryJson!);
        Assert.Equal(before.RootElement.GetRawText(), after.RootElement.GetRawText());

        // Idempotence: save -> load -> save must be byte-identical, which is the practical guarantee
        // that nothing is being quietly reformatted or dropped on each cycle.
        string secondSave = WBondIo.Write(reloaded);
        Assert.Equal(firstSave, secondSave);

        // And it nests as a real object in the file, not as an escaped string.
        Assert.Contains("\"bondpad\"", firstSave, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\"bondpad\\\"", firstSave, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file written by an older build — one carrying only the fields that existed then — must load,
    /// with every absent field taking its built-in default. That is the property that lets a field be
    /// added without a version bump.
    /// </summary>
    [Fact]
    public void WBondIo_AbsentFields_TakeTheirBuiltInDefaults()
    {
        const string minimal = """
            {
              "FormatVersion": 1,
              "Arrays": [ { "Name": "G1", "Wires": [ { "DiameterNm": 25400, "Points": [[0,0,101600],[2540000,0,25400]] } ] } ]
            }
            """;

        var design = WBondIo.Read(minimal);

        Assert.True(design.GroundPlane.Enabled);
        Assert.Equal(WireMaterials.DefaultOperatingTempC, design.OperatingTempC);
        Assert.Equal(WireMaterials.Default.Name, design.Arrays[0].Wires[0].Material);
        Assert.False(design.Arrays[0].Wires[0].Locked);
        Assert.Equal(4, design.Materials.Count);   // the shipped table
        Assert.Null(design.EmbeddedGeometryJson);

        // And it is a usable design, not just a parsed one.
        design.Validate();
        Assert.True(InductanceMatrix.Fill(WireMesh.Build(design))[0, 0] > 0.0);
    }

    /// <summary>
    /// A file from a NEWER build is refused, naming both versions. Partly reading it would silently
    /// drop whatever the new version added.
    /// </summary>
    [Fact]
    public void WBondIo_NewerFormatVersion_IsRefusedNotPartlyRead()
    {
        string future = $$"""{ "FormatVersion": {{WBondIo.CurrentFormatVersion + 1}}, "Arrays": [] }""";

        var ex = Assert.Throws<InvalidDataException>(() => WBondIo.Read(future));
        Assert.Contains((WBondIo.CurrentFormatVersion + 1).ToString(), ex.Message);
        Assert.Contains(WBondIo.CurrentFormatVersion.ToString(), ex.Message);
    }

    // ---------------------------------------------------------------- CSV import

    private const string SampleCsv = """
        # a wirebond table as a bonder program would export it
        # units: mil
        array,x1,y1,z1,x2,y2,z2,profile,diameter,material
        G1,0,0,4,100,0,1,ball,1.0,Gold
        G1,0,6,4,100,6,1,ball,1.0,Gold
        G1,0,12,4,100,12,1,ball,1.0,Gold
        D1,0,200,4,90,210,2,wedge,1.25,Aluminium
        """;

    /// <summary>R-wb-12 — the table parses into arrays, wires, profiles and metals.</summary>
    [Fact]
    public void WireTableCsv_ReadsArraysWiresAndProfiles()
    {
        var design = WireTableCsv.Read(SampleCsv);

        Assert.Equal(2, design.Arrays.Count);
        Assert.Equal("G1", design.Arrays[0].Name);
        Assert.Equal(3, design.Arrays[0].Wires.Count);
        Assert.Equal("D1", design.Arrays[1].Name);
        Assert.Single(design.Arrays[1].Wires);

        var wire = design.Arrays[0].Wires[0];
        Assert.Equal(7, wire.Points.Count);                                  // the default profile
        Assert.Equal(WBondUnits.ToNm(1.0, WBondUnit.Mil), wire.DiameterNm);
        Assert.Equal("Gold", wire.Material);
        Assert.Equal("ball", wire.ProfileBinding);

        // The feet are EXACT — a generated wire must land on the pad the table named, to the DBU.
        Assert.Equal(Point3.Mils(0, 0, 4), wire.Points[0]);
        Assert.Equal(Point3.Mils(100, 0, 1), wire.Points[^1]);

        var wedge = design.Arrays[1].Wires[0];
        Assert.Equal("wedge", wedge.ProfileBinding);
        Assert.Equal(WBondUnits.ToNm(1.25, WBondUnit.Mil), wedge.DiameterNm);
        Assert.Equal("Aluminium", wedge.Material);

        // And the whole thing solves.
        design.Validate();
        var reduction = ArrayReduction.Reduce(InductanceMatrix.Fill(WireMesh.Build(design)), WireMesh.Build(design));
        Assert.True(reduction.PicoHenries(0, 0) > 0.0);
    }

    /// <summary>
    /// The loop rises above the chord between the feet — the imported wire is a loop, not a straight
    /// line. Guards against a profile that silently generated a chord.
    /// </summary>
    [Fact]
    public void WireTableCsv_GeneratedWires_ActuallyLoop()
    {
        var design = WireTableCsv.Read(SampleCsv);
        var wire = design.Arrays[0].Wires[0];

        long peak = wire.Points.Max(p => p.Z);
        long footMax = Math.Max(wire.Points[0].Z, wire.Points[^1].Z);

        Assert.True(peak > footMax + WBondUnits.ToNm(15.0, WBondUnit.Mil),
            $"An imported ball bond should peak well above its feet; peak {peak} nm vs foot {footMax} nm.");

        // The loop is asymmetric — a ball bond peaks early, not at mid-span.
        int peakIndex = wire.Points.FindIndex(p => p.Z == peak);
        Assert.True(peakIndex < wire.Points.Count / 2,
            $"A ball bond peaks in the first half of the span; the apex was at point {peakIndex} of {wire.Points.Count}.");
    }

    /// <summary>The <c># units:</c> directive is honoured, not ignored.</summary>
    [Fact]
    public void WireTableCsv_UnitsDirective_ChangesTheInterpretation()
    {
        const string micronCsv = """
            # units: um
            array,x1,y1,z1,x2,y2,z2
            G1,0,0,100,2540,0,25
            """;

        var design = WireTableCsv.Read(micronCsv);
        Assert.Equal(WBondUnits.ToNm(2540, WBondUnit.Um), design.Arrays[0].Wires[0].Points[^1].X);
    }

    /// <summary>
    /// <b>R-wb-12 — a malformed row names its line and what was expected; it is never skipped.</b>
    ///
    /// <para>A silently dropped wire is an inductance that is quietly too high — plausible, and wrong
    /// in the optimistic direction, which is the worst kind.</para>
    /// </summary>
    [Theory]
    [InlineData("array,x1,y1,z1,x2,y2,z2\nG1,0,0,4,100,0,banana", "2", "z2", "banana")]
    [InlineData("array,x1,y1,z1,x2,y2,z2\nG1,0,0,4,100,0,", "2", "z2", "empty")]
    [InlineData("array,x1,y1,z1,x2,y2,z2\n,0,0,4,100,0,1", "2", "array", "empty")]
    [InlineData("array,x1,y1,z1,x2,y2,z2\nG1,0,0,4,0,0,4", "2", "same point", "span")]
    public void WireTableCsv_MalformedRow_ReportsItsLineAndWhatWasExpected(
        string csv, string expectedLine, string expectedA, string expectedB)
    {
        var ex = Assert.Throws<InvalidDataException>(() => WireTableCsv.Read(csv));

        Assert.Contains($"Line {expectedLine}", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Message.Contains(expectedA, StringComparison.OrdinalIgnoreCase)
                 || ex.Message.Contains(expectedB, StringComparison.OrdinalIgnoreCase),
            $"Expected the message to mention '{expectedA}' or '{expectedB}'. Got: {ex.Message}");
    }

    /// <summary>A header missing a required column names every one that is absent.</summary>
    [Fact]
    public void WireTableCsv_MissingRequiredColumn_NamesAllOfThem()
    {
        var ex = Assert.Throws<InvalidDataException>(
            () => WireTableCsv.Read("array,x1,y1\nG1,0,0"));

        Assert.Contains("z1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("x2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("z2", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Optional columns fall back to the import defaults rather than failing.</summary>
    [Fact]
    public void WireTableCsv_OptionalColumnsAbsent_UseTheImportDefaults()
    {
        var design = WireTableCsv.Read(
            "array,x1,y1,z1,x2,y2,z2\nG1,0,0,4,100,0,1",
            new WireTableCsv.ImportSettings { DefaultDiameter = 0.8, DefaultMaterial = "Silver", PointsPerWire = 5 });

        var wire = design.Arrays[0].Wires[0];
        Assert.Equal(WBondUnits.ToNm(0.8, WBondUnit.Mil), wire.DiameterNm);
        Assert.Equal("Silver", wire.Material);
        Assert.Equal(5, wire.Points.Count);
    }

    /// <summary>An imported table survives a .wBond round trip unchanged — the whole M7 path, end to end.</summary>
    [Fact]
    public void ImportThenSaveThenLoad_PreservesEverything()
    {
        var imported = WireTableCsv.Read(SampleCsv);
        var restored = WBondIo.Read(WBondIo.Write(imported));

        Assert.Equal(imported.WireCount, restored.WireCount);
        foreach (var (before, after) in imported.AllWires().Zip(restored.AllWires()))
            Assert.Equal(before.Points, after.Points);
    }

    // ---------------------------------------------------------------- loop profile

    /// <summary>
    /// WB24a's invariant, at the level the profile itself guarantees: scaling the loop height leaves
    /// both feet exactly where they were, because their normalised height is zero.
    /// </summary>
    [Fact]
    public void LoopProfile_ScalingHeight_LeavesBothFeetExactlyInPlace()
    {
        var profile = LoopProfile.BallBond(WBondUnits.ToNm(20.0, WBondUnit.Mil));

        // Deliberately unequal foot heights — die surface to package lead, the case that breaks a
        // profile scaled about a flat baseline.
        var start = Point3.Mils(0, 0, 8);
        var end = Point3.Mils(120, 30, 1);

        var wire = profile.CreateWire(start, end, WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold");
        long peakBefore = wire.Points.Max(p => p.Z);

        profile.ScaleHeight(1.5);
        profile.ApplyTo(wire, start, end);

        Assert.Equal(start, wire.Points[0]);
        Assert.Equal(end, wire.Points[^1]);

        long peakAfter = wire.Points.Max(p => p.Z);
        double chordAtPeak = 8.0;   // mils, near the apex; the rise above the chord is what scales
        Assert.True(peakAfter > peakBefore,
            $"Scaling the height by 1.5 must raise the apex: {peakBefore} -> {peakAfter} nm.");
        GC.KeepAlive(chordAtPeak);
    }

    /// <summary>A profile whose feet are not at zero height is refused — that invariant is load-bearing.</summary>
    [Fact]
    public void LoopProfile_NonZeroFootHeight_IsRefused()
    {
        var bad = new LoopProfile
        {
            Name = "bad",
            Shape = [new ProfilePoint(0.0, 0.2), new ProfilePoint(0.5, 1.0), new ProfilePoint(1.0, 0.0)],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => bad.Validate());
        Assert.Contains("zero height at both feet", ex.Message, StringComparison.Ordinal);
    }
}
