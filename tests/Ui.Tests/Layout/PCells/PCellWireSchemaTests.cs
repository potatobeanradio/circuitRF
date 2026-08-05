using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Layout.PCells.Wire;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// The PCell wire schema (docs/design/pcell-wire-schema.md) — the third-party-facing format, which
/// is the one part of Track B that cannot be revised once anyone ships a cell against it.
///
/// <para>The assertions worth reading are the ones about what the schema is <b>unable to express</b>:
/// a metre, a resolution to convert one with, and a fractional coordinate. Those are the mechanism
/// behind R7's single rounding rule surviving a process boundary, and each is checked against the
/// bytes rather than against a policy the code could stop applying.</para>
/// </summary>
public sealed class PCellWireSchemaTests
{
    // ── Framing ───────────────────────────────────────────────────────────────

    [Fact]
    public void AFrameRoundTrips_JsonAndPayloadIntact()
    {
        long[] payload = [0, 0, 300_000, 0, 300_000, 150_000];
        var written = new PCellWireFrame("""{"op":"generate"}""", payload);

        var ms = new MemoryStream();
        PCellWireProtocol.WriteFrame(ms, written);
        ms.Position = 0;

        var read = PCellWireProtocol.ReadFrame(ms);
        Assert.Equal(written.Json, read.Json);
        Assert.Equal(payload, read.Payload.ToArray());
    }

    /// <summary>
    /// A partial read on a pipe is normal and must be LOOPED. Getting this wrong produces frames that
    /// decode as garbage only under load — so it is tested here independently of the device path's own
    /// copy, because a shared test would not prove it of both implementations.
    /// </summary>
    [Fact]
    public void APayloadDeliveredOneByteAtATime_StillDecodesWhole()
    {
        long[] payload = Enumerable.Range(0, 4096).Select(i => (long)i * 7).ToArray();
        var ms = new MemoryStream();
        PCellWireProtocol.WriteFrame(ms, new PCellWireFrame("""{"op":"x"}""", payload));

        var dribble = new OneByteAtATimeStream(ms.ToArray());
        var read = PCellWireProtocol.ReadFrame(dribble);

        Assert.Equal(payload, read.Payload.ToArray());
    }

    [Fact]
    public void AnImplausibleLength_IsReportedAsADesync_NotBelieved()
    {
        // A corrupt length must not become a multi-gigabyte allocation.
        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 16u);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), uint.MaxValue);

        var ex = Assert.Throws<PCellWireException>(() => PCellWireProtocol.ReadFrame(new MemoryStream(bytes)));
        Assert.Contains("out of step", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void APayloadThatIsNotAWholeNumberOfCoordinates_IsRefused()
    {
        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 2u);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), 12u); // not a multiple of 8
        var ex = Assert.Throws<PCellWireException>(() => PCellWireProtocol.ReadFrame(new MemoryStream(bytes)));
        Assert.Contains("whole number", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AClosedStream_SaysTheGeneratorStoppedAnswering_NotAnIoError()
    {
        var ex = Assert.Throws<PCellWireException>(() => PCellWireProtocol.ReadFrame(new MemoryStream()));
        Assert.Contains("closed its output", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── §1: the wire has no metres in it ──────────────────────────────────────

    /// <summary>
    /// <b>The load-bearing assertion of the whole schema, and it survived the version-2 change
    /// unaltered.</b> A length parameter crosses ALREADY CONVERTED and no metre appears anywhere — so
    /// there is nothing for a script to convert and exactly one rounding rule across the boundary.
    ///
    /// <para>Version 1 also withheld <c>dbuPerMicron</c>. That was a second, weaker guarantee layered
    /// on this one, and version 2 gives it up deliberately (see the test below). This one is the
    /// guarantee that actually matters, so it is asserted separately — if the two were checked
    /// together, giving up the weaker one would have looked like giving up both.</para>
    /// </summary>
    [Fact]
    public void ALengthCrossesInDbu_AndNoMetreIsAnywhereInTheMessage()
    {
        var frame = PCellWireCodec.EncodeGenerate(
            "MLIN",
            new Dictionary<string, PCellValue> { ["W"] = 300e-6, ["L"] = 2e-3 },
            [Decl("W", PCellWireDimension.Length), Decl("L", PCellWireDimension.Length)],
            technology: null, PCellLayerSelection.Default, dbuPerMicron: 1000);

        using var doc = JsonDocument.Parse(frame.Json);
        var parameters = doc.RootElement.GetProperty("parameters");

        Assert.Equal(300_000, parameters.GetProperty("W").GetDouble());   // 300 µm in DBU
        Assert.Equal(2_000_000, parameters.GetProperty("L").GetDouble()); // 2 mm in DBU

        // No metre-valued field: the SI value the caller supplied is not carried alongside its
        // converted form, so a script cannot reach one to convert.
        Assert.DoesNotContain("metre", frame.Json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0.0003", frame.Json);
        Assert.DoesNotContain("0.002", frame.Json);
    }

    /// <summary>
    /// Wire version 2: the request states the resolution of the layout being drawn into, so a
    /// generator carrying a PROCESS CONSTANT of its own — a dimension in micrometres out of a kit's
    /// data — can turn it into a coordinate. Version 1 could not express that at all, and said so.
    ///
    /// <para>The value is the one the host ALREADY used to convert the length parameters, so the two
    /// cannot disagree; that is why it is taken from the same argument rather than resolved again.</para>
    /// </summary>
    [Fact]
    public void TheRequestStatesTheResolution_SoAGeneratorsOwnConstantsCanBecomeCoordinates()
    {
        var frame = PCellWireCodec.EncodeGenerate(
            "VENDOR",
            new Dictionary<string, PCellValue> { ["W"] = 300e-6 },
            [Decl("W", PCellWireDimension.Length)],
            technology: null, PCellLayerSelection.Default, dbuPerMicron: 2000);

        using var doc = JsonDocument.Parse(frame.Json);

        Assert.Equal(2000, doc.RootElement.GetProperty("dbuPerMicron").GetInt32());
        // Same figure that converted the parameter — 300 µm at 2000 DBU/µm.
        Assert.Equal(600_000, doc.RootElement.GetProperty("parameters").GetProperty("W").GetDouble());
    }

    /// <summary>The host converts with the SAME function an in-process generator calls, which is what
    /// keeps B7's "the same cell written twice is byte-identical" reachable.</summary>
    [Theory]
    [InlineData(300e-6)]
    [InlineData(2.9e-3)]
    [InlineData(115e-6)]
    [InlineData(0.0)]
    public void TheConversionIsPCellUnits_NotASecondRoundingRule(double metres)
    {
        var frame = PCellWireCodec.EncodeGenerate("G",
            new Dictionary<string, PCellValue> { ["W"] = metres },
            [Decl("W", PCellWireDimension.Length)],
            null, PCellLayerSelection.Default, 1000);

        using var doc = JsonDocument.Parse(frame.Json);
        Assert.Equal(PCellUnits.MetresToDbu(metres, 1000),
                     (long)doc.RootElement.GetProperty("parameters").GetProperty("W").GetDouble());
    }

    [Fact]
    public void AParameterThatIsNotALength_CrossesUntouched()
    {
        var frame = PCellWireCodec.EncodeGenerate("G",
            new Dictionary<string, PCellValue>
            {
                ["Angle"]    = 45.0,
                ["GammaMax"] = 0.05,
                ["Turns"]    = PCellValue.Int(4),
                ["Model"]    = PCellValue.Text("nch_lvt"),
            },
            [Decl("Angle", PCellWireDimension.Angle), Decl("GammaMax", PCellWireDimension.None),
             Decl("Turns", PCellWireDimension.None),  Decl("Model", PCellWireDimension.None)],
            null, PCellLayerSelection.Default, 1000);

        using var doc = JsonDocument.Parse(frame.Json);
        var p = doc.RootElement.GetProperty("parameters");
        Assert.Equal(45.0, p.GetProperty("Angle").GetDouble());
        Assert.Equal(0.05, p.GetProperty("GammaMax").GetDouble());
        Assert.Equal(4, p.GetProperty("Turns").GetProperty("int").GetInt64()); // §3's tagged Int form
        Assert.Equal("nch_lvt", p.GetProperty("Model").GetString());
    }

    /// <summary>A length keeps its KIND across the conversion — the kind says what the parameter is
    /// (continuous, versus a count), not what unit it is in.</summary>
    [Fact]
    public void ConvertingALengthDoesNotChangeItsKind()
    {
        var frame = PCellWireCodec.EncodeGenerate("G",
            new Dictionary<string, PCellValue> { ["W"] = PCellValue.Real(300e-6) },
            [Decl("W", PCellWireDimension.Length)], null, PCellLayerSelection.Default, 1000);

        using var doc = JsonDocument.Parse(frame.Json);
        // A bare number is a Real in this encoding; the tagged {"int":n} form would be an Int.
        Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("parameters").GetProperty("W").ValueKind);
    }

    // ── §4.3: a fractional coordinate is unrepresentable ──────────────────────

    /// <summary>
    /// Every coordinate lives in the int64 payload and none appears in the JSON, which is what makes
    /// "a script cannot emit a fractional coordinate" structural rather than a validation rule
    /// somebody could forget to apply — there is nowhere to write one.
    /// </summary>
    [Fact]
    public void NoCoordinateEverAppearsInTheJson_OnlySpansIntoThePayload()
    {
        var result = new PCellResult(
            [new PolygonShape { Layer = new LayerKey(1, 0), Xy = [0, 0, 1_234_567, 0, 1_234_567, 89_000] }],
            [new PCellPin("1", 0, 0, new LayerKey(1, 0), 300_000, 180.0)]);

        var frame = PCellWireCodec.EncodeGenerateReply(result);

        // The vertex values are in the payload…
        Assert.Contains(1_234_567L, frame.Payload.ToArray());
        // …and nowhere in the control plane.
        Assert.DoesNotContain("1234567", frame.Json);

        using var doc = JsonDocument.Parse(frame.Json);
        var xy = doc.RootElement.GetProperty("shapes")[0].GetProperty("xy");
        Assert.Equal(0, xy.GetProperty("at").GetInt32());
        Assert.Equal(6, xy.GetProperty("count").GetInt32());
    }

    [Fact]
    public void ASpanPointingOutsideThePayload_IsRefusedAsInconsistent()
    {
        var frame = new PCellWireFrame(
            """{"ok":true,"shapes":[{"kind":"rect","layer":{"layer":1,"datatype":0},"xy":{"at":0,"count":4}}],"pins":[]}""",
            new long[] { 0, 0 }); // only two coordinates present, four claimed

        var ex = Assert.Throws<PCellWireException>(() => PCellWireCodec.DecodeGenerateReply(frame));
        Assert.Contains("not self-consistent", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnOddCoordinateCount_IsRefused()
    {
        var frame = new PCellWireFrame(
            """{"ok":true,"shapes":[{"kind":"poly","layer":{"layer":1,"datatype":0},"xy":{"at":0,"count":5}}],"pins":[]}""",
            new long[] { 0, 0, 1, 1, 2 });

        var ex = Assert.Throws<PCellWireException>(() => PCellWireCodec.DecodeGenerateReply(frame));
        Assert.Contains("whole number of points", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── §4.4: the shape vocabulary ────────────────────────────────────────────

    /// <summary>
    /// Every built-in generator's real output survives the round trip. This is what says the schema is
    /// actually big enough for the geometry circuitRF already produces — a vocabulary check against
    /// hand-built shapes would only prove the encoder agrees with itself.
    /// </summary>
    [Theory]
    [InlineData("MLIN")]
    [InlineData("MBEND")]
    [InlineData("MTEE")]
    [InlineData("MCROSS")]
    [InlineData("MTAPER")]
    [InlineData("MKLOPF")]
    public void EveryBuiltInGeneratorsOutput_SurvivesTheRoundTrip(string generatorId)
    {
        Assert.True(PCellRegistry.TryGet(generatorId, out var generate));
        var original = generate(DefaultsFor(generatorId), technology: null, PCellLayerSelection.Default);

        var decoded = PCellWireCodec.DecodeGenerateReply(PCellWireCodec.EncodeGenerateReply(original));

        Assert.Equal(original.Shapes.Count, decoded.Shapes.Count);
        for (int i = 0; i < original.Shapes.Count; i++)
            Assert.Equal(Describe(original.Shapes[i]), Describe(decoded.Shapes[i]));

        Assert.Equal(original.Pins.Count, decoded.Pins.Count);
        for (int i = 0; i < original.Pins.Count; i++)
            Assert.Equal(original.Pins[i], decoded.Pins[i]);
    }

    /// <summary>A curve crosses as a curve. A generator that flattened its own would bake a tolerance
    /// into the geometry, and flattening is a rendering decision made at screen resolution.</summary>
    [Fact]
    public void CurvedEdgesCrossAsEdges_NotAsFlattenedPoints()
    {
        var curve = new CurveShape
        {
            Layer = new LayerKey(1, 0),
            Xy    = [0, 0, 1000, 0, 1000, 1000, 0, 1000],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.41421356237309515 },
                new LayoutEdge { Kind = EdgeKind.Cubic, C1X = 900, C1Y = 1100, C2X = 100, C2Y = 1100 },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
        };

        var decoded = (CurveShape)RoundTripOne(curve);

        Assert.Equal(EdgeKind.Arc, decoded.Edges![1].Kind);
        Assert.Equal(0.41421356237309515, decoded.Edges[1].Bulge, 15);
        Assert.Equal(EdgeKind.Cubic, decoded.Edges[2].Kind);
        Assert.Equal((900L, 1100L, 100L, 1100L),
                     (decoded.Edges[2].C1X, decoded.Edges[2].C1Y, decoded.Edges[2].C2X, decoded.Edges[2].C2Y));
    }

    [Fact]
    public void HolesSurviveTheRoundTrip()
    {
        var poly = new PolygonShape
        {
            Layer = new LayerKey(2, 0),
            Xy    = [0, 0, 1000, 0, 1000, 1000, 0, 1000],
            Holes = [[200, 200, 400, 200, 400, 400, 200, 400], [600, 600, 800, 600, 800, 800, 600, 800]],
        };

        var decoded = (PolygonShape)RoundTripOne(poly);
        Assert.Equal(2, decoded.Holes!.Count);
        Assert.Equal(poly.Holes[0], decoded.Holes[0]);
        Assert.Equal(poly.Holes[1], decoded.Holes[1]);
    }

    [Fact]
    public void EveryShapeKindInTheVocabulary_RoundTrips()
    {
        var shapes = new LayoutShape[]
        {
            new RectShape        { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 20 },
            new RoundedRectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 20, CornerRadius = 3 },
            new CircleShape      { Layer = new LayerKey(1, 0), Cx = 5, Cy = 5, R = 4, FlattenTolDbu = 7 },
            new PolygonShape     { Layer = new LayerKey(1, 0), Xy = [0, 0, 10, 0, 10, 10] },
            new CurveShape       { Layer = new LayerKey(1, 0), Xy = [0, 0, 10, 0, 10, 10] },
            new PathShape        { Layer = new LayerKey(1, 0), Xy = [0, 0, 10, 0], Width = 5, End = PathEndStyle.Round },
            new ViaShape         { Layer = new LayerKey(7, 0), X = 1, Y = 2, PadSize = 9, DrillSize = 4,
                                   LandingLayer = new LayerKey(1, 0) },
            new LabelShape       { Layer = new LayerKey(3, 0), X = 1, Y = 2, Text = "A1", Height = 500,
                                   Rotation = LayoutRotation.R90, IsPort = true },
        };

        var decoded = PCellWireCodec.DecodeGenerateReply(
            PCellWireCodec.EncodeGenerateReply(new PCellResult(shapes, [])));

        Assert.Equal(shapes.Length, decoded.Shapes.Count);
        for (int i = 0; i < shapes.Length; i++)
            Assert.Equal(Describe(shapes[i]), Describe(decoded.Shapes[i]));
    }

    /// <summary>An unknown kind is REFUSED, never skipped. A silently dropped shape leaves a cell that
    /// renders, looks complete, and is missing a piece — the worst failure this boundary can have.</summary>
    [Fact]
    public void AnUnknownShapeKind_IsRefusedByName_NotSkipped()
    {
        var frame = new PCellWireFrame(
            """{"ok":true,"shapes":[{"kind":"donut","layer":{"layer":1,"datatype":0},"xy":{"at":0,"count":2}}],"pins":[]}""",
            new long[] { 0, 0 });

        var ex = Assert.Throws<PCellWireException>(() => PCellWireCodec.DecodeGenerateReply(frame));
        Assert.Contains("donut", ex.Message, StringComparison.Ordinal);
        Assert.Contains("refused rather than skipped", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BitmapIsNotInTheVocabulary_AndIsNotEncoded()
    {
        Assert.DoesNotContain("bitmap", PCellWireShapeKind.All, StringComparer.OrdinalIgnoreCase);

        // Encoding a hand-built result containing one drops it rather than inventing a kind for it.
        var frame = PCellWireCodec.EncodeGenerateReply(new PCellResult(
            [new BitmapShape { Layer = new LayerKey(1, 0) },
             new RectShape   { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1, Y2 = 1 }], []));

        var decoded = PCellWireCodec.DecodeGenerateReply(frame);
        Assert.Single(decoded.Shapes);
        Assert.IsType<RectShape>(decoded.Shapes[0]);
    }

    // ── §5: errors and diagnostics are different channels ─────────────────────

    [Fact]
    public void ARefusal_SurfacesTheGeneratorsOwnReason()
    {
        var frame = new PCellWireFrame("""{"ok":false,"error":"turns must be at least 1"}""");
        var ex = Assert.Throws<PCellWireException>(() => PCellWireCodec.DecodeGenerateReply(frame));
        Assert.Contains("turns must be at least 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsAreNotAnErrorChannel_AndSurviveAlongsideGeometry()
    {
        var result = new PCellResult(
            [new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1, Y2 = 1 }],
            [], ["minimum bend radius exceeded on turn 4"]);

        var decoded = PCellWireCodec.DecodeGenerateReply(PCellWireCodec.EncodeGenerateReply(result));

        Assert.Single(decoded.Shapes);
        Assert.Equal(["minimum bend radius exceeded on turn 4"], decoded.Diagnostics);
    }

    // ── §7: versioning ────────────────────────────────────────────────────────

    [Fact]
    public void DescribeCarriesBothVersions_InBothDirections()
    {
        using var doc = JsonDocument.Parse(PCellWireCodec.EncodeDescribe().Json);
        Assert.Equal(PCellWireVersion.Current, doc.RootElement.GetProperty("wireVersion").GetInt32());
        Assert.Equal(PCellContractVersion.Current, doc.RootElement.GetProperty("contractVersion").GetInt32());

        var reply = new PCellWireDescribeReply();
        Assert.Equal(PCellWireVersion.Current, reply.WireVersion);
        Assert.Equal(PCellContractVersion.Current, reply.ContractVersion);
    }

    /// <summary>They version different things and must be able to move independently — a byte-layout
    /// change need not change the semantics, and conflating them means a host that only speaks a new
    /// layout claims to implement a new contract.</summary>
    [Fact]
    public void TheWireVersionIsNotTheContractVersion()
        => Assert.NotSame((object)nameof(PCellWireVersion), (object)nameof(PCellContractVersion));

    // ── §4.2: the resolved technology ─────────────────────────────────────────

    [Fact]
    public void TheLayerChoiceCrossesResolved_NeverAsTheQuestion()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var frame = PCellWireCodec.EncodeGenerate("MLIN",
            new Dictionary<string, PCellValue> { ["W"] = 300e-6 },
            [Decl("W", PCellWireDimension.Length)],
            tech, PCellLayerSelection.Default, 1000);

        using var doc = JsonDocument.Parse(frame.Json);
        var layers = doc.RootElement.GetProperty("layers");

        // The answer circuitRF's own resolver produced, not a rule for the script to re-apply.
        var expected = SubstrateResolver.ResolveSignalLayerKey(tech, PCellLayerSelection.Default, out _);
        Assert.Equal(expected.Layer, layers.GetProperty("signal").GetProperty("layer").GetInt32());
        Assert.True(layers.GetProperty("table").GetArrayLength() > 0);
    }

    [Fact]
    public void StackupThicknessesCrossInDbu_AndOnlyKindRelevantPropertiesAreSent()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        var frame = PCellWireCodec.EncodeGenerate("MLIN", new Dictionary<string, PCellValue>(), [],
            tech, PCellLayerSelection.Default, 1000);

        using var doc = JsonDocument.Parse(frame.Json);
        var layers = doc.RootElement.GetProperty("stackup").GetProperty("layers");

        for (int i = 0; i < layers.GetArrayLength(); i++)
        {
            var entry = layers[i];
            string kind = entry.GetProperty("kind").GetString()!;
            long thickness = entry.GetProperty("thickness").GetInt64();
            Assert.Equal(tech.Stackup.Layers[i].ThicknessDbu, thickness);

            if (kind == "dielectric")
            {
                Assert.True(entry.TryGetProperty("epsr", out _));
                Assert.False(entry.TryGetProperty("sigma", out _)); // noise on a dielectric
            }
            else
            {
                Assert.True(entry.TryGetProperty("sigma", out _));
                Assert.False(entry.TryGetProperty("epsr", out _)); // noise on a conductor
            }
        }
    }

    [Fact]
    public void NoTechnology_StillProducesAValidRequest_WithNoStackup()
    {
        var frame = PCellWireCodec.EncodeGenerate("MLIN",
            new Dictionary<string, PCellValue> { ["W"] = 300e-6 },
            [Decl("W", PCellWireDimension.Length)], null, PCellLayerSelection.Default, 1000);

        using var doc = JsonDocument.Parse(frame.Json);
        // pcell-contract.md §2: a generator still produces geometry with no technology; only the
        // ELECTRICAL stamp refuses without one.
        Assert.False(doc.RootElement.TryGetProperty("stackup", out _));
        Assert.Equal(300_000, doc.RootElement.GetProperty("parameters").GetProperty("W").GetDouble());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PCellWireParameterDecl Decl(string name, PCellWireDimension dim)
        => new() { Name = name, Kind = PCellValueKind.Real, Dimension = dim };

    private static LayoutShape RoundTripOne(LayoutShape shape)
        => PCellWireCodec.DecodeGenerateReply(
               PCellWireCodec.EncodeGenerateReply(new PCellResult([shape], []))).Shapes[0];

    private static IReadOnlyDictionary<string, PCellValue> DefaultsFor(string generatorId)
        => PCellParameters.FromReals(generatorId switch
        {
            "MLIN"   => new() { ["W"] = 300e-6, ["L"] = 2e-3 },
            "MBEND"  => new() { ["W"] = 300e-6, ["Angle"] = 90, ["Miter"] = 2 },
            "MTEE"   => new() { ["W1"] = 300e-6, ["W2"] = 300e-6, ["W3"] = 500e-6 },
            "MCROSS" => new() { ["W1"] = 300e-6, ["W2"] = 300e-6, ["W3"] = 300e-6, ["W4"] = 300e-6 },
            "MTAPER" => new() { ["W1"] = 300e-6, ["W2"] = 1e-3, ["L"] = 2e-3 },
            "MKLOPF" => new() { ["Z1"] = 50, ["Z2"] = 100, ["L"] = 5e-3, ["GammaMax"] = 0.05, ["Offset"] = 1e-4 },
            _        => new Dictionary<string, double>(),
        });

    /// <summary>Full structural description — compared as text so a failure names the field that
    /// moved rather than reporting that two objects differ.</summary>
    private static string Describe(LayoutShape shape) => shape switch
    {
        RectShape r        => $"rect {r.Layer} {r.X1},{r.Y1},{r.X2},{r.Y2}",
        RoundedRectShape r => $"rrect {r.Layer} {r.X1},{r.Y1},{r.X2},{r.Y2} cr={r.CornerRadius} tol={r.FlattenTolDbu}",
        CircleShape c      => $"circle {c.Layer} {c.Cx},{c.Cy} r={c.R} tol={c.FlattenTolDbu}",
        PolygonShape p     => $"poly {p.Layer} [{string.Join(",", p.Xy)}] holes={Holes(p.Holes)}",
        CurveShape c       => $"curve {c.Layer} [{string.Join(",", c.Xy)}] holes={Holes(c.Holes)} " +
                              $"edges={Edges(c.Edges)} tol={c.FlattenTolDbu}",
        PathShape p        => $"path {p.Layer} [{string.Join(",", p.Xy)}] w={p.Width} end={p.End} " +
                              $"edges={Edges(p.Edges)} tol={p.FlattenTolDbu}",
        ViaShape v         => $"via {v.Layer} {v.X},{v.Y} pad={v.PadSize} drill={v.DrillSize} land={v.LandingLayer}",
        LabelShape l       => $"label {l.Layer} {l.X},{l.Y} '{l.Text}' h={l.Height} rot={l.Rotation} port={l.IsPort}",
        _                  => shape.GetType().Name,
    };

    private static string Holes(List<long[]>? holes)
        => holes is null ? "-" : string.Join(";", holes.Select(h => string.Join(",", h)));

    private static string Edges(List<LayoutEdge>? edges)
        => edges is null ? "-" : string.Join(";", edges.Select(e =>
               $"{e.Kind}:{e.Bulge.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}:" +
               $"{e.C1X},{e.C1Y},{e.C2X},{e.C2Y}"));

    /// <summary>Delivers one byte per read — what a pipe under load looks like.</summary>
    private sealed class OneByteAtATimeStream(byte[] data) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length || count == 0) return 0;
            buffer[offset] = data[_pos++];
            return 1;
        }
        public override int Read(Span<byte> buffer)
        {
            if (_pos >= data.Length || buffer.Length == 0) return 0;
            buffer[0] = data[_pos++];
            return 1;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
