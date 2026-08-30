// ================================================================
//  GoldenFilesStayFrozenTests.cs — the goldens are frozen data, not scratch output.
//
//  Before HB-P3 (2026-08-30) the four `Generate…Golden` [Fact]s ran on every `dotnet test` and
//  overwrote the committed CSVs they exist to compare against. xunit runs classes in parallel, so a
//  regression test could be checking the engine against a file that engine had written moments
//  earlier — a golden that re-freezes itself proves nothing, and a green suite was not evidence the
//  frozen answers had held.
//
//  This test drives every writer in the suite that targets `testdata/` and asserts the tree comes
//  back byte-identical. It is the behavioural gate; `GoldenRegen` is the mechanism.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests;

public sealed class GoldenFilesStayFrozenTests(ITestOutputHelper output)
{
    private static string TestDataRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata not found above " + AppContext.BaseDirectory);
    }

    private static Dictionary<string, string> Snapshot(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            using var stream = File.OpenRead(f);
            map[Path.GetRelativePath(root, f)] = Convert.ToHexString(SHA256.HashData(stream));
        }
        return map;
    }

    /// <summary>
    /// Runs every test method that writes into <c>testdata/</c> and asserts nothing under it moved.
    ///
    /// <para>Calling the methods directly rather than trusting the suite's own scheduling is
    /// deliberate: this must fail when a NEW writer is added without the gate, and it can only do
    /// that if the writers it knows about are actually exercised here. A writer added elsewhere and
    /// left ungated will be caught by the same assertion the moment it is added to this list — and
    /// the list is the checklist that says one is owed.</para>
    ///
    /// <para>Skipped, not inverted, when regeneration is requested: under
    /// <c>CIRCUITRF_REGENERATE_GOLDENS</c> the whole point is that these files DO move.</para>
    /// </summary>
    [Fact]
    public void EveryGoldenWriter_LeavesTestDataByteIdentical()
    {
        if (GoldenRegen.Enabled)
        {
            output.WriteLine($"{GoldenRegen.EnvVar} is set — regeneration is the intent here; skipping.");
            return;
        }

        string root   = TestDataRoot();
        var    before = Snapshot(root);
        output.WriteLine($"testdata: {before.Count} files hashed under {root}");

        // Every writer in the suite whose destination is inside testdata/.
        new HarmonicBalance.Hero2GoldenGenerator(output).GenerateHero2Golden();
        new HarmonicBalance.Hero5GoldenGenerator(output).GenerateHero5Golden();
        new Loadpull.Hero3LoadpullTests(output).GenerateHero3Golden();
        new Loadpull.Hero3LoadpullTests(output).RLSweep();
        new Loadpull.Hero3BPursuitTests(output).GenerateHero3BGolden();
        new Loadpull.PursuitSearchDiagnosticTests().Diagnostic1_TruthSurface();
        new Loadpull.PursuitSearchDiagnosticTests().Diagnostic2_InstrumentedWalk();

        var after = Snapshot(root);

        var added   = after.Keys.Except(before.Keys).Order().ToList();
        var removed = before.Keys.Except(after.Keys).Order().ToList();
        var changed = before.Keys.Intersect(after.Keys)
                            .Where(k => before[k] != after[k]).Order().ToList();

        foreach (var f in added)   output.WriteLine($"  ADDED   {f}");
        foreach (var f in removed) output.WriteLine($"  REMOVED {f}");
        foreach (var f in changed) output.WriteLine($"  CHANGED {f}");

        Assert.True(added.Count == 0 && removed.Count == 0 && changed.Count == 0,
            $"a test wrote into testdata/ during an ordinary run: {changed.Count} changed, " +
            $"{added.Count} added, {removed.Count} removed. Committed golden data must only move " +
            $"when {GoldenRegen.EnvVar} is set — route the write through GoldenRegen.OpenWriter, " +
            $"or guard it with GoldenRegen.Enabled.");
    }

    /// <summary>The gate is OFF by default, which is the whole basis of the test above.</summary>
    [Fact]
    public void RegenerationIsOffUnlessAskedFor()
    {
        string? raw = Environment.GetEnvironmentVariable(GoldenRegen.EnvVar);
        output.WriteLine($"{GoldenRegen.EnvVar}={raw ?? "(unset)"} → Enabled={GoldenRegen.Enabled}");

        // A value of "0" or "false" reads as off, so an inherited variable cannot arm it by accident.
        Assert.False(GoldenRegen.Enabled && (raw is "0" or "false"));
    }
}
