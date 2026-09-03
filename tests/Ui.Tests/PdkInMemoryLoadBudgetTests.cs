using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A workspace open re-reads and re-translates every referenced kit, because nothing is written into
/// the workspace any more. That is the whole bet of the in-memory design, and this is the measurement
/// that says whether it holds: a 20-symbol kit must load in under 100 ms.
///
/// <para><b>A miss here is not a slower number to write up — it means the in-memory approach is paying
/// a cost the on-disk one did not.</b> If this fails, report the number and where the time goes and
/// ask; do not relax the budget and do not cache extra artifacts to disk to get under it.</para>
///
/// <para><b>Why replaying the recorded settings is the load-bearing part.</b> Deriving them scans
/// candidate model libraries byte by byte across a separate multi-MB package — measured at ~62 ms on a
/// kit, which is most of the budget on its own. A workspace records what was settled, so an open
/// replays it. The second test below is the one that would catch that regressing.</para>
///
/// <para>The fixture names no vendor and no part: a symbol file is a FORMAT, so a synthetic one
/// exercises exactly the code a kit does.</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class PdkInMemoryLoadBudgetTests : IDisposable
{
    private const int SymbolCount    = 20;
    private const int BudgetMs       = 100;

    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-budget-" + Guid.NewGuid().ToString("N")[..8]);

    private string KitDir => Path.Combine(_root, "kit");

    public PdkInMemoryLoadBudgetTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(KitDir);
    }

    public void Dispose()
    {
        PdkKitRegistry.ResetAllForTests();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// A three-pin symbol. Two of the pins carry a nonzero rotation so the reader does the real
    /// transform work rather than a degenerate copy.
    /// </summary>
    private static string SymbolFile(int n) => $"""
        1     7.707    0 0
        10    1    "PART_{n}_SYM"    2    1    0    0    341    0
        20    0    ""    0 0 0 0 0    2 -3 1    1    0    "schematic.prf" "schematic.lay"
        44    0    -600    600    600    1    0    0
        50    2    0 0 500 0 1    0    0    0    0    0    0    0    0
        60    4    0    2    0 0 500 0 1    0    0    0    0
        70    0 0    500 0
        42    1    2    "gate"      1    2    0    0 0 180000    0    0   ""
        42    2    2    "drain"     2    1    0    500 0 0    0    0   ""
        42    3    2    "thermal"   3    0    0    0 500 90000    0    0   ""
        21
        """;

    private const string DeviceType = "CRF_BUDGET_V1";

    /// <summary>
    /// A synthetic kit of <see cref="SymbolCount"/> parts, each with its own symbol, whose devices are
    /// COMPILED — so a load that has nothing recorded genuinely has to go and find the library that
    /// serves them. Without that the two branches below would be the same path measured twice.
    /// </summary>
    private PdkImportReport BuildKit()
    {
        Directory.CreateDirectory(Path.Combine(KitDir, "symbols"));

        string netDir = Path.Combine(KitDir, "circuit", "models");
        Directory.CreateDirectory(netDir);
        File.WriteAllText(Path.Combine(netDir, "kit.net"), $"""
            define PART_CIRCUIT ( g d s )
              {DeviceType}:M1  g d s
            end PART_CIRCUIT
            """);

        // Recognised by the entry point circuitRF's OWN worker calls — a plain byte scan, which is
        // what makes this synthetic fixture exercise the real discovery.
        string libDir = Path.Combine(KitDir, "linux_x86_64");
        Directory.CreateDirectory(libDir);
        File.WriteAllBytes(Path.Combine(libDir, "models.so"),
        [
            0x7F, (byte)'E', (byte)'L', (byte)'F',
            .. System.Text.Encoding.ASCII.GetBytes(
                DeviceLibraryDiscovery.Profiles[0].ExportPrefix + DeviceType + "\0"),
        ]);

        var worker = new byte[64];
        worker[0] = 0x7F; worker[1] = (byte)'E'; worker[2] = (byte)'L'; worker[3] = (byte)'F';
        File.WriteAllBytes(Path.Combine(KitDir, DeviceLibraryDiscovery.Profiles[0].Worker), worker);

        var report = new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" };
        report.Add(new PdkAsset("circuit/models/kit.net", PdkAssetKind.Netlist,
                                PdkAssetSupport.Supported, "kit netlist"));
        for (int i = 0; i < SymbolCount; i++)
        {
            string rel = $"symbols/part{i}.dsn";
            File.WriteAllText(Path.Combine(KitDir, rel.Replace('/', Path.DirectorySeparatorChar)),
                              SymbolFile(i));
            report.Parts.Add(new PdkPart($"PART_{i}", $"Part {i}",
                SymbolArtwork: new PdkAsset(rel, PdkAssetKind.SymbolArtwork,
                                            PdkAssetSupport.Supported, "symbol description (.dsn)")));
        }
        return report;
    }

    /// <summary>Median of a warm run, first-JIT pass discarded.</summary>
    private double MedianLoadMs(PdkImportReport report, JsonNode? recorded)
    {
        _ = PdkPartInstaller.Install(report, recorded?.DeepClone());   // discard: JIT and file cache

        var samples = new double[9];
        for (int i = 0; i < samples.Length; i++)
        {
            var sw = Stopwatch.StartNew();
            var outcome = PdkPartInstaller.Install(report, recorded?.DeepClone());
            PdkKitRegistry.SetKit(null, outcome.KitName, outcome.Parts ?? []);
            sw.Stop();

            Assert.Equal(SymbolCount, outcome.SymbolsInstalled);
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        return samples[samples.Length / 2];
    }

    [Fact]
    public void ATwentySymbolKit_LoadsWellInsideTheBudget()
    {
        var report = BuildKit();

        // The shape an open takes: the settings were settled at import and are replayed, so no
        // library discovery runs here.
        var recorded = JsonNode.Parse(
            """{ "provider": "SampleKit", "workers": [ { "platform": "any", "command": "worker" } ] }""");

        double median = MedianLoadMs(report, recorded);
        _out.WriteLine($"{SymbolCount}-symbol kit, settings replayed: median {median:F1} ms");

        Assert.True(median < BudgetMs,
            $"a {SymbolCount}-symbol kit took {median:F1} ms to load, over the {BudgetMs} ms budget. " +
            $"This is a STOP, not a number to write up: it means re-deriving the kit on every open " +
            $"costs more than the on-disk translation it replaced. Report where the time goes and ask.");
    }

    /// <summary>
    /// Replaying the recorded settings is what keeps an open off the expensive path, and this is the
    /// test that would catch it regressing.
    ///
    /// <para><b>Asserted as BEHAVIOUR, not as a ratio of two timings — and the previous version's
    /// failure is why.</b> It compared a replayed load against a derived one and demanded a 10×
    /// separation, calibrated against a comment recording 199.8 ms for the derived branch. But the
    /// expense that figure describes — scanning candidate model libraries byte by byte across a
    /// multi-MB package — is a cost THIS FIXTURE NEVER CREATES: its library is a little over thirty
    /// bytes. Measured here, derived came in at ~4 ms against ~0.5 ms replayed: a real 8.6×
    /// separation, and a failure, because the threshold was calibrated against work the fixture does
    /// not do. A faster disk shrinks the ratio further. The mechanism was never broken; the gate
    /// was measuring the machine.</para>
    ///
    /// <para>Taking the library away instead settles it outright: if a replayed load still installs
    /// everything when there is nothing left on disk to discover, it provably did not re-derive.
    /// No clock is involved, so nothing here drifts with hardware.</para>
    /// </summary>
    [Fact]
    public void ReplayingRecordedSettings_DoesNotRederiveThem()
    {
        var report = BuildKit();
        var recorded = JsonNode.Parse(
            """{ "provider": "SampleKit", "workers": [ { "platform": "any", "command": "worker" } ] }""");

        // With the library present, deriving finds something. This is the baseline the control below
        // is compared against — without it, the removal might prove nothing.
        var derivedWithLibrary = PdkPartInstaller.Install(report, null);
        Assert.Equal(SymbolCount, derivedWithLibrary.SymbolsInstalled);
        string settledWith = derivedWithLibrary.Settings?.ToJsonString() ?? "";

        // Take away everything discovery could find.
        Directory.Delete(Path.Combine(KitDir, "linux_x86_64"), recursive: true);
        File.Delete(Path.Combine(KitDir, DeviceLibraryDiscovery.Profiles[0].Worker));

        // THE CONTROL, and it has to come first: deriving must now produce a DIFFERENT answer.
        // If it did not, the replay below would pass whether or not it re-derived, and the test
        // would be asserting nothing at all.
        var derivedWithout = PdkPartInstaller.Install(report, null);
        string settledWithout = derivedWithout.Settings?.ToJsonString() ?? "";
        Assert.True(settledWith != settledWithout,
            "removing the model library did not change what deriving produces, so this test cannot " +
            "tell a replayed load from a derived one. Fix the fixture before trusting the assertion.");

        // THE ASSERTION: replaying is unaffected by the library being gone, because it never looked.
        var replayed = PdkPartInstaller.Install(report, recorded!.DeepClone());

        Assert.Equal(SymbolCount, replayed.SymbolsInstalled);
        Assert.Equal(recorded.ToJsonString(), replayed.Settings?.ToJsonString());

        _out.WriteLine($"derived with library    : {settledWith}");
        _out.WriteLine($"derived without library : {settledWithout}");
        _out.WriteLine($"replayed (library gone) : {replayed.Settings?.ToJsonString()}");
    }
}
