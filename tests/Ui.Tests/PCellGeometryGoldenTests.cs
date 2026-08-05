using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Every built-in PCell's geometry, over a grid of parameter sets, written out as text and compared
/// byte for byte against a recorded baseline.
///
/// <para><b>What this is for, and it is a narrow thing.</b> The PCell contract is about to change
/// shape — parameters widen from bare doubles to kinded values so a third party can pass a string or
/// an integer. That migration touches every generator, and the ONE thing it must not do is move a
/// coordinate. A per-generator assertion could not show that: there are six generators, several with
/// conditional branches, and "no geometry change" is a claim about all of them at once.</para>
///
/// <para><b>The baseline is circuitRF's own output, recorded before the change.</b> It is not a
/// reference from another tool and proves nothing about whether the geometry is RIGHT — that is what
/// the generators' own tests are for. It proves only that it did not move, which is exactly the
/// question a mechanical migration raises.</para>
///
/// <para><b>Regenerating it is deliberate and visible.</b> Set <see cref="RecordVariable"/> and the
/// test rewrites the baseline instead of asserting; the diff then shows precisely which coordinates
/// moved, which is the review a geometry change deserves. A silently self-updating golden file is
/// worse than no golden file.</para>
/// </summary>
public sealed class PCellGeometryGoldenTests
{
    private const string RecordVariable = "CIRCUITRF_RECORD_PCELL_GOLDEN";
    private const string GoldenRelative = "testdata/PCells/pcell-geometry.txt";

    /// <summary>
    /// Parameter sets per generator. Chosen to exercise each generator's own branches — a mitred and
    /// an unmitred bend, a taper with and without a segment-count override — rather than to be
    /// realistic. A migration bug that only shows on one branch is exactly the kind that survives a
    /// single-case check.
    /// </summary>
    private static readonly (string Generator, Dictionary<string, double> Parameters)[] Cases =
    [
        ("MLIN",   new() { ["W"] = 300e-6, ["L"] = 2e-3 }),
        ("MLIN",   new() { ["W"] = 1.2e-3, ["L"] = 250e-6 }),

        ("MBEND",  new() { ["W"] = 300e-6, ["Angle"] = 90,  ["Miter"] = 0 }),
        ("MBEND",  new() { ["W"] = 300e-6, ["Angle"] = 90,  ["Miter"] = 1 }),
        ("MBEND",  new() { ["W"] = 300e-6, ["Angle"] = 90,  ["Miter"] = 2 }),
        ("MBEND",  new() { ["W"] = 500e-6, ["Angle"] = 45,  ["Miter"] = 2 }),

        ("MTEE",   new() { ["W1"] = 300e-6, ["W2"] = 300e-6, ["W3"] = 500e-6 }),
        ("MTEE",   new() { ["W1"] = 1e-3,   ["W2"] = 250e-6, ["W3"] = 250e-6 }),

        ("MCROSS", new() { ["W1"] = 300e-6, ["W2"] = 300e-6, ["W3"] = 300e-6, ["W4"] = 300e-6 }),
        ("MCROSS", new() { ["W1"] = 1e-3,   ["W2"] = 250e-6, ["W3"] = 700e-6, ["W4"] = 400e-6 }),

        ("MTAPER", new() { ["W1"] = 300e-6, ["W2"] = 1e-3, ["L"] = 2e-3 }),
        ("MTAPER", new() { ["W1"] = 1e-3,   ["W2"] = 300e-6, ["L"] = 500e-6, ["N"] = 8 }),

        ("MKLOPF", new() { ["Z1"] = 50, ["Z2"] = 100, ["L"] = 5e-3, ["GammaMax"] = 0.05 }),
        ("MKLOPF", new() { ["Z1"] = 75, ["Z2"] = 25,  ["L"] = 2e-3, ["GammaMax"] = 0.02, ["Offset"] = 1e-4 }),
    ];

    [Fact]
    public void EveryBuiltInPCellsGeometryIsUnchanged()
    {
        string actual = Render();
        string path   = GoldenPath();

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RecordVariable)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path),
            $"No recorded geometry at '{path}'. Record it with {RecordVariable}=1 — and review the " +
            "diff, because that file is the only thing standing between a refactor and a silently " +
            "moved coordinate.");

        // Compared as whole text so the failure shows the offending line rather than a hash mismatch.
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual.ReplaceLineEndings("\n"));
    }

    // ── rendering ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Every case, deterministically. Coordinates are integers in database units, so this is exact
    /// text with no formatting tolerance anywhere — which is the point: a tolerance would hide the
    /// one-unit rounding shift a units change causes.
    /// </summary>
    private static string Render()
    {
        var sb = new StringBuilder();
        sb.Append("# circuitRF built-in PCell geometry. Recorded output, not a reference.\n");
        // The contract version is deliberately NOT stamped here. It was, and it made this file
        // un-comparable across exactly the change it exists to survive: bumping the version rewrote
        // the header, the byte-compare failed, and the only way to see that no coordinate had moved
        // was to read the diff and decide the one changed line was the harmless one. A baseline that
        // needs that judgement on every contract change is not a baseline. Generator VERSIONS are
        // still stamped per case below — those genuinely mean "this geometry is allowed to differ."

        foreach (var (generatorId, parameters) in Cases)
        {
            Assert.True(PCellRegistry.TryGet(generatorId, out var generate),
                        $"{generatorId} is not a registered generator");

            var result = generate(ToContractParameters(parameters), technology: null,
                                  PCellLayerSelection.Default);

            string args = string.Join(" ", parameters.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key}={p.Value.ToString("R", CultureInfo.InvariantCulture)}"));
            sb.Append($"\n== {generatorId} v{PCellRegistry.GeneratorVersion(generatorId)} {args}\n");

            foreach (var shape in result.Shapes) sb.Append(Describe(shape));
            foreach (var pin in result.Pins)
                sb.Append($"  pin {pin.Name} at ({pin.X},{pin.Y}) w={pin.WidthDbu} " +
                          $"dir={pin.OutwardDirectionDeg.ToString("R", CultureInfo.InvariantCulture)} " +
                          $"layer={pin.Layer}\n");

            foreach (var d in result.Diagnostics ?? []) sb.Append($"  ! {d}\n");
        }

        return sb.ToString();
    }

    private static string Describe(LayoutShape shape) => shape switch
    {
        RectShape r        => $"  rect   layer={r.Layer} ({r.X1},{r.Y1})-({r.X2},{r.Y2})\n",
        RoundedRectShape r => $"  rrect  layer={r.Layer} ({r.X1},{r.Y1})-({r.X2},{r.Y2})\n",
        PolygonShape p     => $"  poly   layer={p.Layer} [{Join(p.Xy)}]" +
                              (p.Holes is { Count: > 0 } h
                                  ? " holes=" + string.Join(";", h.Select(x => $"[{Join(x)}]"))
                                  : "") + "\n",
        PathShape p        => $"  path   layer={p.Layer} w={p.Width} end={p.End} [{Join(p.Xy)}]\n",
        _                  => $"  {shape.GetType().Name} layer={shape.Layer}\n",
    };

    private static string Join(long[] xy) => string.Join(",", xy);

    /// <summary>
    /// Adapts a case's plain doubles to whatever the contract currently takes. <b>This is the only
    /// line this file will need when the contract widens</b> — which is what keeps the baseline
    /// comparable across the change rather than being regenerated by it.
    /// </summary>
    private static IReadOnlyDictionary<string, PCellValue> ToContractParameters(
        Dictionary<string, double> plain) => PCellParameters.FromReals(plain);

    private static string GoldenPath()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "testdata");
            if (Directory.Exists(candidate))
                return Path.Combine(dir, GoldenRelative.Replace('/', Path.DirectorySeparatorChar));
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/ not found above the test binary");
    }
}
