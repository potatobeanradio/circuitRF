using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.WBond;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Bond wires through DXF (wbond.md §9.4) — the bridge to the assembly house.
///
/// <para>The gate that matters here is the ROUND TRIP: wires exported and re-imported must come back
/// as the same wires, in the same groups, with the same loop heights. An interchange format that
/// only writes is a format nobody can check.</para>
/// </summary>
public class WBondDxfRoundTripTests
{
    private const long Mil = 25_400;

    /// <summary>Two groups: a 3-wire fan and a single wire, all with a real loop height.</summary>
    private static WBondDesign MakeDesign()
    {
        var design = new WBondDesign();

        var gnd = new WireArray { Name = "GND" };
        for (int i = 0; i < 3; i++)
        {
            gnd.Wires.Add(LoopShape.CreateSeedWire(
                new Point3(0, i * 6 * Mil, 0),
                new Point3(60 * Mil, i * 6 * Mil, 8 * Mil),
                diameterNm: Mil,
                material: "Gold", loopHeightNm: 20 * Mil));
        }
        design.Arrays.Add(gnd);

        var vdd = new WireArray { Name = "Vdd" };
        vdd.Wires.Add(LoopShape.CreateSeedWire(
            new Point3(0, 40 * Mil, 0),
            new Point3(50 * Mil, 40 * Mil, 0),
            diameterNm: 2 * Mil,
            material: "Aluminium", loopHeightNm: 14 * Mil));
        design.Arrays.Add(vdd);

        return design;
    }

    /// <summary>A minimal one-rect layout to carry the wires — export needs a root structure.</summary>
    private static DxfExport.ExportPlan MakePlan()
    {
        var view = new LayoutView();
        view.Shapes.Add(new RectShape { X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000, Layer = new LayerKey(1, 0) });

        var structure = new InterchangeStructure("TOP", view.Shapes, view.Instances);
        return new DxfExport.ExportPlan(
            UnresolvedInstanceReferences: [],
            BlockNameByCellName: new Dictionary<string, string> { ["TOP"] = "TOP" },
            Structures: [structure],
            RootStructureName: "TOP",
            Tech: null,
            DbuPerMicron: LayoutUnits.DefaultDbuPerMicron);
    }

    private static string WriteToString(
        WBondDesign? wires,
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
        int insUnits = DxfUnits.DefaultPromptUnits)
    {
        var sw = new StringWriter();
        DxfWriter.Write(sw, MakePlan().Structures, "TOP", tech: null,
                        dbuPerMicron, new DxfExportOptions(InsUnits: insUnits), wires);
        return sw.ToString();
    }

    private static WBondDesign Reimport(string dxf, int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron)
    {
        var reader = DxfReader.Read(new StringReader(dxf));
        return DxfWireIo.BuildDesign(reader.WirePolylines, NmPerDrawingUnit(reader));
    }

    /// <summary>Nanometres per drawing unit, from the file's own $INSUNITS.</summary>
    internal static double NmPerDrawingUnit(DxfReader reader) =>
        DxfUnits.NanometersPerDrawingUnit(reader.InsUnits)
        ?? DxfUnits.NanometersPerDrawingUnit(DxfUnits.DefaultPromptUnits)!.Value;

    // ---------------------------------------------------------------- layer convention

    [Fact]
    public void LayerName_IsWiresPrefixPlusTheGroupName_BothDirections()
    {
        Assert.Equal("Wires_GND", DxfWireIo.LayerNameFor("GND"));
        Assert.Equal("GND", DxfWireIo.ArrayNameFrom("Wires_GND"));

        // A layer that is not a wire layer must not be mistaken for one.
        Assert.Null(DxfWireIo.ArrayNameFrom("Metal1"));
        Assert.Null(DxfWireIo.ArrayNameFrom("Wire_GND"));   // singular: not the prefix
    }

    /// <summary>A group name containing DXF-illegal characters is sanitised, not written raw.</summary>
    [Fact]
    public void LayerName_SanitisesCharactersDxfForbidsInALayerName()
    {
        string name = DxfWireIo.LayerNameFor("A/B:C*D");

        Assert.StartsWith("Wires_", name, StringComparison.Ordinal);
        foreach (char c in "<>/\\\":;?*|,='`")
            Assert.DoesNotContain(c.ToString(), name[6..], StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- export

    [Fact]
    public void Export_WritesOneThreeDPolylinePerWire_OnItsGroupsOwnLayer()
    {
        string dxf = WriteToString(MakeDesign());

        // A LAYER record must exist for every wire layer, or a strict reader rejects the file.
        Assert.Contains("Wires_GND", dxf, StringComparison.Ordinal);
        Assert.Contains("Wires_Vdd", dxf, StringComparison.Ordinal);

        // Four wires -> four POLYLINE entities, each declared 3D (the subclass marker says so).
        int polylines = CountOccurrences(dxf, "AcDb3dPolyline\n");
        Assert.Equal(4, polylines);

        Assert.Contains("AcDb3dPolylineVertex", dxf, StringComparison.Ordinal);
        Assert.Contains("SEQEND", dxf, StringComparison.Ordinal);
    }

    /// <summary>
    /// The 3D flag is what makes Z meaningful. Without group 70 = 8 on the POLYLINE header, a reader
    /// is entitled to flatten the loop — which would export the one coordinate a bond wire is about
    /// and then silently lose it.
    /// </summary>
    [Fact]
    public void Export_MarksEachWirePolylineAsThreeD()
    {
        string dxf = WriteToString(MakeDesign());
        string[] lines = dxf.Split('\n');

        int checkedHeaders = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() != "AcDb3dPolyline") continue;

            // Group 70 follows within the header block; its value must carry bit 8.
            bool found = false;
            for (int j = i; j < Math.Min(i + 20, lines.Length) - 1; j++)
            {
                if (lines[j].Trim() != "70") continue;
                Assert.True((int.Parse(lines[j + 1].Trim()) & 8) != 0,
                            "a wire polyline must set the 3D bit (70 & 8)");
                found = true;
                break;
            }
            Assert.True(found, "no group 70 found on a wire polyline header");
            checkedHeaders++;
        }

        Assert.Equal(4, checkedHeaders);
    }

    [Fact]
    public void Export_CarriesDiameterAndMaterialAsXdata()
    {
        string dxf = WriteToString(MakeDesign());

        Assert.Contains(DxfWireIo.XdataAppName, dxf, StringComparison.Ordinal);
        Assert.Contains("Gold", dxf, StringComparison.Ordinal);
        Assert.Contains("Aluminium", dxf, StringComparison.Ordinal);
    }

    /// <summary>A filled circle at each foot: eight ends across four wires.</summary>
    [Fact]
    public void Export_AddsAFilledCircleAtEveryWireFoot()
    {
        string dxf = WriteToString(MakeDesign());

        Assert.Equal(8, CountOccurrences(dxf, "AcDbCircle\n"));
        Assert.Equal(8, CountOccurrences(dxf, "AcDbHatch\n"));
    }

    /// <summary>Supplying no design leaves the file byte-for-byte what the Layout Editor always wrote.</summary>
    [Fact]
    public void Export_WithNoWires_IsUnchangedFromThePlainLayoutExport()
    {
        string without = WriteToString(null);

        Assert.DoesNotContain("Wires_", without, StringComparison.Ordinal);
        Assert.DoesNotContain(DxfWireIo.XdataAppName, without, StringComparison.Ordinal);
        Assert.DoesNotContain("AcDb3dPolyline", without, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- round trip

    /// <summary><b>The headline gate.</b> Out and back: same groups, same wires, same loop heights.</summary>
    [Fact]
    public void RoundTrip_ReproducesEveryGroupWireAndLoopHeight()
    {
        var original = MakeDesign();
        string dxf = WriteToString(original);

        var reader = DxfReader.Read(new StringReader(dxf));
        var rebuilt = DxfWireIo.BuildDesign(reader.WirePolylines, NmPerDrawingUnit(reader));

        Assert.Equal(2, rebuilt.Arrays.Count);
        Assert.Equal(["GND", "Vdd"], rebuilt.Arrays.Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(original.WireCount, rebuilt.WireCount);

        var before = original.AllWires().ToList();
        var after = rebuilt.AllWires().ToList();

        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Points.Count, after[i].Points.Count);

            // The loop height is the definition (§3.1a) and is what a round trip must not lose. One
            // DBU of slack per end for the decimal round trip through drawing units.
            Assert.InRange(after[i].LoopHeightNm, before[i].LoopHeightNm - 2, before[i].LoopHeightNm + 2);

            for (int p = 0; p < before[i].Points.Count; p++)
            {
                Assert.InRange(after[i].Points[p].X, before[i].Points[p].X - 2, before[i].Points[p].X + 2);
                Assert.InRange(after[i].Points[p].Y, before[i].Points[p].Y - 2, before[i].Points[p].Y + 2);
                Assert.InRange(after[i].Points[p].Z, before[i].Points[p].Z - 2, before[i].Points[p].Z + 2);
            }
        }
    }

    [Fact]
    public void RoundTrip_ReproducesDiameterAndMaterial()
    {
        var original = MakeDesign();
        var reader = DxfReader.Read(new StringReader(WriteToString(original)));
        var rebuilt = DxfWireIo.BuildDesign(reader.WirePolylines, NmPerDrawingUnit(reader));

        var before = original.AllWires().ToList();
        var after = rebuilt.AllWires().ToList();

        for (int i = 0; i < before.Count; i++)
        {
            Assert.InRange(after[i].DiameterNm, before[i].DiameterNm - 2, before[i].DiameterNm + 2);
            Assert.Equal(before[i].Material, after[i].Material);
        }
    }

    /// <summary>
    /// <b>Wires must NOT also arrive as layout geometry.</b> A wire flattened into a PathShape would
    /// re-export as flat copper on the next round trip — a trip that silently destroys the design it
    /// was meant to preserve.
    /// </summary>
    [Fact]
    public void RoundTrip_WiresDoNotAlsoAppearAsLayoutShapes()
    {
        var reader = DxfReader.Read(new StringReader(WriteToString(MakeDesign())));

        foreach (var structure in reader.Structures)
            foreach (var shape in structure.Shapes)
                Assert.Null(DxfWireIo.ArrayNameFrom(shape.LayerName));

        Assert.Equal(4, reader.WirePolylines.Count);
    }

    /// <summary>A DXF carrying no wire layers yields no wires, and says nothing about them.</summary>
    [Fact]
    public void Import_OfAPlainLayoutDxf_YieldsNoWires()
    {
        var reader = DxfReader.Read(new StringReader(WriteToString(null)));

        Assert.Empty(reader.WirePolylines);

        var rebuilt = DxfWireIo.BuildDesign(reader.WirePolylines, NmPerDrawingUnit(reader));
        Assert.Equal(0, rebuilt.WireCount);
    }

    /// <summary>
    /// A wire polyline with no XDATA — one drawn by another CAD tool, following only the layer
    /// convention — still imports, at a sane default diameter and material.
    /// </summary>
    [Fact]
    public void Import_ForeignPolylineWithoutXdata_StillBecomesAWire()
    {
        var poly = new DxfWireIo.WirePolyline(
            "Wires_Foreign",
            [(0, 0, 0), (0.5, 0, 0.2), (1.0, 0, 0)],
            DiameterDrawingUnits: null,
            Material: null);

        var design = DxfWireIo.BuildDesign([poly], 1_000_000.0);

        Assert.Equal(1, design.WireCount);
        Assert.Equal("Foreign", design.Arrays[0].Name);

        var wire = design.AllWires().First();
        Assert.True(wire.DiameterNm > 0, "an unstated diameter must fall back to a usable one, not zero");
        Assert.Equal(WireMaterials.Default.Name, wire.Material);
    }

    /// <summary>An unknown material name falls back rather than minting a material with no conductivity.</summary>
    [Fact]
    public void Import_UnknownMaterial_FallsBackToTheDefault_NotAConductivitylessOne()
    {
        var poly = new DxfWireIo.WirePolyline(
            "Wires_G", [(0, 0, 0), (1, 0, 0.1)], DiameterDrawingUnits: 0.001, Material: "Unobtainium");

        var design = DxfWireIo.BuildDesign([poly], 1_000_000.0);

        Assert.Equal(WireMaterials.Default.Name, design.AllWires().First().Material);
    }

    /// <summary>
    /// An imported wire is an ordinary wire: its polyline IS its shape, exactly as for one drawn here.
    ///
    /// <para>This used to assert the wire arrived bound to no loop profile. With the profile object
    /// removed (2026-08-18) there is no other kind of wire to distinguish it from, so what is left to
    /// state is that the polyline came back intact.</para>
    /// </summary>
    [Fact]
    public void Import_WiresArriveAsOrdinaryPolylines()
    {
        var original = MakeDesign();
        var reader = DxfReader.Read(new StringReader(WriteToString(original)));
        var rebuilt = DxfWireIo.BuildDesign(reader.WirePolylines, NmPerDrawingUnit(reader));

        Assert.Equal(original.WireCount, rebuilt.WireCount);
        Assert.All(rebuilt.AllWires(), w => Assert.True(w.Points.Count >= 2));
        Assert.All(rebuilt.AllWires(), w => Assert.True(w.LoopHeightNm > 0));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }
}

/// <summary>
/// The export must honour the FILE's units and the LAYOUT's resolution together (wbond.md §9.4).
///
/// <para>These live in their own class because the trap they guard is invisible on a default
/// document: a wire point is stored in nanometres while the rest of the DXF writer works in the
/// layout's own database units, and the two coincide exactly at 1,000 DBU/µm. A test written only
/// against the default resolution cannot tell a correct writer from one that omits the conversion
/// entirely — which is how the same bridge shipped broken once already in the renderer.</para>
/// </summary>
public class WBondDxfUnitsTests
{
    private const long Mil = 25_400;

    /// <summary>One wire 1 mm long and 0.2 mm high, in round numbers that are exact in every unit.</summary>
    private static WBondDesign OneWire()
    {
        var design = new WBondDesign();
        var array = new WireArray { Name = "G" };
        array.Wires.Add(new Wire
        {
            Points =
            [
                new Point3(0, 0, 0),
                new Point3(500_000, 0, 200_000),      // 0.5 mm across, 0.2 mm up
                new Point3(1_000_000, 0, 0),          // 1 mm across
            ],
            DiameterNm = Mil,
            Material = "Gold",
        });
        design.Arrays.Add(array);
        return design;
    }

    private static string Write(int dbuPerMicron, int insUnits)
    {
        var view = new LayoutView();
        view.Shapes.Add(new RectShape { X1 = 0, Y1 = 0, X2 = 100, Y2 = 100, Layer = new LayerKey(1, 0) });
        var structure = new InterchangeStructure("TOP", view.Shapes, view.Instances);

        var sw = new StringWriter();
        DxfWriter.Write(sw, [structure], "TOP", tech: null, dbuPerMicron,
                        new DxfExportOptions(InsUnits: insUnits), OneWire());
        return sw.ToString();
    }

    /// <summary>
    /// The same physical wire written at three layout resolutions produces the SAME drawing-unit
    /// coordinates — because the resolution is a property of the database, not of the wire.
    ///
    /// <para>This is the test that fails outright when the nm-to-DBU step is omitted: without it the
    /// coordinates scale with the resolution, so 100 DBU/µm comes out ten times smaller than 1,000.</para>
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(1_000)]
    [InlineData(10_000)]
    public void WireCoordinates_AreTheSamePhysicalSize_AtEveryLayoutResolution(int dbuPerMicron)
    {
        // Millimetres: a 1 mm wire must span exactly 1.0 drawing unit.
        string dxf = Write(dbuPerMicron, DxfUnits.Millimeters);

        double maxX = MaxVertexGroup(dxf, code: 10);
        double maxZ = MaxVertexGroup(dxf, code: 30);

        Assert.Equal(1.0, maxX, 6);     // 1 mm across
        Assert.Equal(0.2, maxZ, 6);     // 0.2 mm up
    }

    /// <summary>Changing the FILE's units rescales the numbers by exactly that ratio.</summary>
    [Fact]
    public void WireCoordinates_ScaleWithTheFilesOwnInsUnits()
    {
        double mm = MaxVertexGroup(Write(1_000, DxfUnits.Millimeters), 10);
        double um = MaxVertexGroup(Write(1_000, DxfUnits.Microns), 10);
        double inch = MaxVertexGroup(Write(1_000, DxfUnits.Inches), 10);

        Assert.Equal(1.0, mm, 6);
        Assert.Equal(1_000.0, um, 3);                 // 1 mm = 1000 um
        Assert.Equal(1.0 / 25.4, inch, 6);            // 1 mm in inches
    }

    /// <summary>The XDATA diameter travels in the file's units too, not raw nanometres.</summary>
    [Fact]
    public void Diameter_IsWrittenInTheFilesOwnUnits()
    {
        string dxf = Write(1_000, DxfUnits.Millimeters);

        double diameter = XdataDiameter(dxf);
        Assert.Equal(25.4 / 1000.0, diameter, 6);     // 1 mil in mm
    }

    /// <summary>And the whole trip closes at a non-default resolution, which is the point of all of it.</summary>
    [Theory]
    [InlineData(100)]
    [InlineData(10_000)]
    public void RoundTrip_ClosesAtANonDefaultResolution(int dbuPerMicron)
    {
        string dxf = Write(dbuPerMicron, DxfUnits.Millimeters);

        var reader = DxfReader.Read(new StringReader(dxf));
        var rebuilt = DxfWireIo.BuildDesign(
            reader.WirePolylines, WBondDxfRoundTripTests.NmPerDrawingUnit(reader));

        var wire = Assert.Single(rebuilt.AllWires());
        var original = OneWire().AllWires().First();

        for (int i = 0; i < original.Points.Count; i++)
        {
            Assert.InRange(wire.Points[i].X, original.Points[i].X - 2, original.Points[i].X + 2);
            Assert.InRange(wire.Points[i].Z, original.Points[i].Z - 2, original.Points[i].Z + 2);
        }

        Assert.InRange(wire.LoopHeightNm, original.LoopHeightNm - 2, original.LoopHeightNm + 2);
        Assert.InRange(wire.DiameterNm, original.DiameterNm - 2, original.DiameterNm + 2);
    }

    /// <summary>
    /// The largest value of a group code appearing inside VERTEX entities.
    ///
    /// <para><b>Walks strict (code, value) PAIRS, not every line.</b> DXF handles are hexadecimal, so
    /// a handle whose value happens to be "30" is indistinguishable from a group-code line if you scan
    /// line by line — which is exactly how the first version of this helper read an owner-handle group
    /// code (330) as a Z coordinate and failed a perfectly correct writer.</para>
    /// </summary>
    private static double MaxVertexGroup(string dxf, int code)
    {
        string[] lines = dxf.Split('\n');
        double max = double.MinValue;
        bool inVertex = false;

        for (int i = 0; i + 1 < lines.Length; i += 2)
        {
            string c = lines[i].Trim();
            string v = lines[i + 1].Trim();

            if (c == "0")
            {
                inVertex = v == "VERTEX";
                continue;
            }

            if (inVertex && c == code.ToString()
                && double.TryParse(v, System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out double d))
                max = Math.Max(max, d);
        }

        Assert.True(max > double.MinValue, $"no VERTEX group {code} found in the file");
        return max;
    }

    /// <summary>The XDATA diameter (group 1040) that follows our own application name.</summary>
    private static double XdataDiameter(string dxf)
    {
        string[] lines = dxf.Split('\n');
        bool ours = false;

        for (int i = 0; i + 1 < lines.Length; i += 2)
        {
            string c = lines[i].Trim();
            string v = lines[i + 1].Trim();

            if (c == "1001") { ours = v == DxfWireIo.XdataAppName; continue; }
            if (ours && c == "1040")
                return double.Parse(v, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.Fail("no wBond XDATA diameter found in the file");
        return 0;
    }
}
