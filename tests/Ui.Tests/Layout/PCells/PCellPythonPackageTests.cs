using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Layout.PCells.Wire;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// Like <see cref="FactAttribute"/>, but SKIPS with a reason when no Python interpreter is on PATH.
/// Mirrors this repo's own convention for a toolchain the build must not require (the OSDI worker's
/// C compiler, the loadpull fixtures): a missing tool reports Skipped with a reason naming it, never
/// a red build on a machine that simply does not have it.
/// </summary>
public sealed class PythonFactAttribute : FactAttribute
{
    public PythonFactAttribute()
    {
        if (PythonRunner.Interpreter is null)
            Skip = "No python3 on PATH — install Python 3.9+ to run the PCell package tests.";
    }
}

/// <inheritdoc cref="PythonFactAttribute"/>
public sealed class PythonTheoryAttribute : TheoryAttribute
{
    public PythonTheoryAttribute()
    {
        if (PythonRunner.Interpreter is null)
            Skip = "No python3 on PATH — install Python 3.9+ to run the PCell package tests.";
    }
}

/// <summary>
/// Drives <c>tools/pcell-python</c> as a REAL subprocess over the real wire format.
///
/// <para><b>Why the package is written from the specification rather than ported from circuitRF's
/// own codec, and why that matters here.</b> These tests compare two implementations that were
/// arrived at independently — one in C#, one in Python, both from
/// <c>docs/design/pcell-wire-schema.md</c>. A port would make this a check that one implementation
/// agrees with itself, which proves nothing about the format. It is the same rule
/// <c>tools/DeviceWorkerExample</c> already follows for the device path.</para>
/// </summary>
public sealed class PCellPythonPackageTests
{
    // ── The early B7 gate ─────────────────────────────────────────────────────

    /// <summary>
    /// <b>The same cell, written twice, produces byte-identical geometry.</b> The development plan
    /// asks for this test early rather than at the end of Track B, and this is it: circuitRF's own
    /// <c>MlinPCell</c> against the Python reference generator, over a real pipe.
    ///
    /// <para>One artifact pins several things at once — the metre→DBU conversion happening host-side
    /// and only once, the wire carrying it faithfully, and the two generators agreeing about where a
    /// 300 µm edge lands. If it ever fails, the interesting question is which of those moved.</para>
    /// </summary>
    [PythonTheory]
    [InlineData(300e-6, 2e-3)]
    [InlineData(1.2e-3, 250e-6)]
    [InlineData(2.9e-3, 10e-3)]
    [InlineData(115e-6, 1e-3)]
    public void PythonMlinAndTheBuiltInMlin_ProduceByteIdenticalGeometry(double wMetres, double lMetres)
    {
        var parameters = new Dictionary<string, PCellValue> { ["W"] = wMetres, ["L"] = lMetres };
        var layerSelection = PCellLayerSelection.Default;

        Assert.True(PCellRegistry.TryGet("MLIN", out var builtIn));
        var expected = builtIn(parameters, technology: null, layerSelection);

        using var python = PythonRunner.Start(ExampleScript);
        var declarations = python.Describe().Generators.Single(g => g.Id == "MLIN").Parameters;

        var actual = python.Generate("MLIN", parameters, declarations, null, layerSelection);

        Assert.Equal(Describe(expected), Describe(actual));
    }

    // ── describe ──────────────────────────────────────────────────────────────

    [PythonFact]
    public void DescribeReportsEachParametersDimension_WhichIsWhatLetsTheHostConvert()
    {
        using var python = PythonRunner.Start(ExampleScript);
        var reply = python.Describe();

        Assert.True(reply.Ok);
        Assert.Equal(PCellWireVersion.Current, reply.WireVersion);

        var mlin = reply.Generators.Single(g => g.Id == "MLIN");
        Assert.All(mlin.Parameters, p => Assert.Equal(PCellWireDimension.Length, p.Dimension));

        var array = reply.Generators.Single(g => g.Id == "VIAARRAY");
        Assert.Equal(PCellWireDimension.None,   Param(array, "Rows").Dimension);
        Assert.Equal(PCellValueKind.Int,        Param(array, "Rows").Kind);
        Assert.Equal(PCellWireDimension.Length, Param(array, "Pitch").Dimension);
        Assert.Equal(PCellValueKind.Bool,       Param(array, "Staggered").Kind);
        Assert.Equal(PCellValueKind.String,     Param(array, "Note").Kind);
    }

    /// <summary>A version mismatch is refused with both numbers named, never negotiated.</summary>
    [PythonFact]
    public void AWireVersionMismatch_IsRefusedByTheScript_NamingBothVersions()
    {
        using var python = PythonRunner.Start(ExampleScript);
        var frame = python.Exchange(new PCellWireFrame(
            """{"op":"describe","wireVersion":99,"contractVersion":2}"""));

        using var doc = JsonDocument.Parse(frame.Json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        string error = doc.RootElement.GetProperty("error").GetString()!;
        Assert.Contains("99", error, StringComparison.Ordinal);
        Assert.Contains(PCellWireVersion.Current.ToString(), error, StringComparison.Ordinal);
    }

    // ── The whole vocabulary, over the wire ───────────────────────────────────

    /// <summary>
    /// A generator using the parts of the schema MLIN does not reach — a count, a flag, a text
    /// parameter, vias, and the diagnostics channel — decodes into circuitRF's own types.
    /// </summary>
    [PythonFact]
    public void ACellUsingCountsFlagsTextAndVias_DecodesIntoRealGeometry()
    {
        using var python = PythonRunner.Start(ExampleScript);
        var declarations = python.Describe().Generators.Single(g => g.Id == "VIAARRAY").Parameters;

        var result = python.Generate("VIAARRAY", new Dictionary<string, PCellValue>
        {
            ["Rows"]      = PCellValue.Int(2),
            ["Cols"]      = PCellValue.Int(3),
            ["Pitch"]     = 100e-6,
            ["Pad"]       = 50e-6,
            ["Drill"]     = 25e-6,
            ["Staggered"] = PCellValue.Bool(true),
            ["Note"]      = PCellValue.Text("from the kit"),
        }, declarations, null, PCellLayerSelection.Default);

        var vias = result.Shapes.OfType<ViaShape>().ToList();
        Assert.Equal(6, vias.Count);
        Assert.All(vias, v => Assert.Equal(50_000, v.PadSize));   // 50 µm in DBU
        Assert.All(vias, v => Assert.Equal(25_000, v.DrillSize));

        // The odd row is staggered by half a pitch — proof the flag arrived as a flag, not as the
        // integer 1 or a string, and that dbu() rounded it the same way circuitRF would.
        Assert.Contains(vias, v => v.X == 50_000 && v.Y == 100_000);

        Assert.Contains("from the kit", result.Diagnostics!);
    }

    /// <summary>Diagnostics are not an error channel: geometry AND a caveat arrive together.</summary>
    [PythonFact]
    public void AGeneratorThatProducedGeometryAndHasACaveat_ReturnsBoth()
    {
        using var python = PythonRunner.Start(ExampleScript);
        var declarations = python.Describe().Generators.Single(g => g.Id == "VIAARRAY").Parameters;

        var result = python.Generate("VIAARRAY", new Dictionary<string, PCellValue>
        {
            ["Rows"] = PCellValue.Int(1), ["Cols"] = PCellValue.Int(1),
            ["Pitch"] = 100e-6, ["Pad"] = 20e-6, ["Drill"] = 25e-6, // pad smaller than drill
        }, declarations, null, PCellLayerSelection.Default);

        Assert.Single(result.Shapes);
        Assert.Contains(result.Diagnostics!, d => d.Contains("annular ring", StringComparison.OrdinalIgnoreCase));
    }

    // ── Failure ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A generator that raises becomes a refusal carrying its own message — not a crashed process,
    /// and not a silently empty cell. The traceback rides along because a script is somebody's own
    /// code and it is the only view they get of it failing.
    /// </summary>
    [PythonFact]
    public void AGeneratorThatRaises_BecomesARefusalCarryingItsOwnMessage()
    {
        using var python = PythonRunner.Start(ExampleScript);
        var declarations = python.Describe().Generators.Single(g => g.Id == "VIAARRAY").Parameters;

        var ex = Assert.Throws<PCellWireException>(() => python.Generate("VIAARRAY",
            new Dictionary<string, PCellValue> { ["Rows"] = PCellValue.Int(0), ["Cols"] = PCellValue.Int(1) },
            declarations, null, PCellLayerSelection.Default));

        Assert.Contains("at least one row", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Traceback", ex.Message, StringComparison.Ordinal);
    }

    [PythonFact]
    public void AnUnknownGeneratorId_IsRefusedByName_ListingWhatTheScriptOffers()
    {
        using var python = PythonRunner.Start(ExampleScript);
        var ex = Assert.Throws<PCellWireException>(() => python.Generate("NOSUCHCELL",
            new Dictionary<string, PCellValue>(), [], null, PCellLayerSelection.Default));

        Assert.Contains("NOSUCHCELL", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MLIN", ex.Message, StringComparison.Ordinal);
    }

    // ── Volume ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A large cell over a real pipe. This only passes if partial reads are looped on BOTH sides —
    /// the one subtlety of the codec, and the failure it causes (frames decoding as garbage) shows
    /// up only under load, which is exactly what a small round trip cannot produce.
    /// </summary>
    [PythonFact]
    public void ALargeCell_SurvivesTheRealPipe()
    {
        using var python = PythonRunner.Start(ExampleScript);
        var declarations = python.Describe().Generators.Single(g => g.Id == "VIAARRAY").Parameters;

        var result = python.Generate("VIAARRAY", new Dictionary<string, PCellValue>
        {
            ["Rows"] = PCellValue.Int(60), ["Cols"] = PCellValue.Int(60),
            ["Pitch"] = 100e-6, ["Pad"] = 50e-6, ["Drill"] = 25e-6,
        }, declarations, null, PCellLayerSelection.Default);

        Assert.Equal(3600, result.Shapes.Count); // 7,200 coordinates, far past any pipe buffer
    }

    /// <summary>Several requests down one process — a framing error leaving one stray byte is
    /// invisible on the first call and corrupts every one after it.</summary>
    [PythonFact]
    public void ManySequentialRequests_StayInStep()
    {
        using var python = PythonRunner.Start(ExampleScript);
        var declarations = python.Describe().Generators.Single(g => g.Id == "MLIN").Parameters;

        for (int i = 1; i <= 50; i++)
        {
            double lengthMetres = i * 10e-6;
            var result = python.Generate("MLIN",
                new Dictionary<string, PCellValue> { ["W"] = i * 1e-6, ["L"] = lengthMetres },
                declarations, null, PCellLayerSelection.Default);

            var rect = Assert.IsType<RectShape>(Assert.Single(result.Shapes));
            // Compared against the conversion rather than a hand-computed number: the value under
            // test is that the i-th reply is the i-th request's, so the expectation must not be a
            // place this test can itself be wrong about the arithmetic.
            Assert.Equal(PCellUnits.MetresToDbu(lengthMetres, LayoutUnits.DefaultDbuPerMicron), rect.X2);
        }
    }

    // ── The technology, as the script sees it ─────────────────────────────────

    [PythonFact]
    public void TheResolvedSignalLayerReachesTheScript_AndIsUsed()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        using var python = PythonRunner.Start(ExampleScript);
        var declarations = python.Describe().Generators.Single(g => g.Id == "MLIN").Parameters;

        var result = python.Generate("MLIN",
            new Dictionary<string, PCellValue> { ["W"] = 300e-6, ["L"] = 2e-3 },
            declarations, tech, PCellLayerSelection.Default);

        var expected = SubstrateResolver.ResolveSignalLayerKey(tech, PCellLayerSelection.Default, out _);
        Assert.Equal(expected, Assert.Single(result.Shapes).Layer);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PCellWireParameterDecl Param(PCellWireGeneratorDecl g, string name)
        => g.Parameters.Single(p => p.Name == name);

    private static string ExampleScript => Path.Combine(PythonRunner.PackageRoot, "example", "mlin.py");

    private static string Describe(PCellResult result)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var shape in result.Shapes)
            sb.Append(shape switch
            {
                RectShape r => $"rect {r.Layer} {r.X1},{r.Y1},{r.X2},{r.Y2}\n",
                _           => $"{shape.GetType().Name} {shape.Layer}\n",
            });
        foreach (var pin in result.Pins)
            sb.Append($"pin {pin.Name} ({pin.X},{pin.Y}) w={pin.WidthDbu} " +
                      $"dir={pin.OutwardDirectionDeg.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                      $"layer={pin.Layer}\n");
        return sb.ToString();
    }
}
