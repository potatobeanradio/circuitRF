using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner report: importing a kit says "This kit names no program to evaluate its devices" — "I thought
/// we were able to simulate some of these components."
///
/// <para>The message was one fixed sentence that went on to reassure the reader "its parts are still
/// built from the kit's own netlists". That is true of a kit shipping netlists and a false comfort on
/// one that does not — and the kit in question ships neither netlists nor terminals: its parts are
/// binary cell views and its model data is a proprietary dataset format, so the parts import as names
/// with no pins and no behaviour, and the message pointed the reader at a fallback that was not
/// there. Which of the three situations a kit is in is now read off the import.</para>
///
/// <para>Every fixture here is synthetic — the repository commits no third-party kit data, and a test
/// keyed to a vendor kit on one machine fails on a fresh clone.</para>
/// </summary>
public sealed class PdkNoProgramMessageTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-noprog-" + Guid.NewGuid().ToString("N")[..8]);
    private string Root => Path.Combine(_scratch, "delivery", "root");
    private string KitDir => Path.Combine(Root, "kit");

    public PdkNoProgramMessageTests()
    {
        PdkKitRegistry.ResetAllForTests();
        Directory.CreateDirectory(KitDir);
    }

    public void Dispose()
    {
        PdkKitRegistry.ResetAllForTests();
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>The note the installer emits about how a kit's devices get evaluated. There is exactly
    /// one, and it is the only note mentioning a program to evaluate devices.</summary>
    private static string SimulationNote(PdkPartInstaller.InstallOutcome outcome)
    {
        Assert.NotNull(outcome.Notes);
        return Assert.Single(outcome.Notes, n => n.Contains("names no program", StringComparison.Ordinal)
                                              || n.Contains("simulation settings", StringComparison.Ordinal));
    }

    private PdkImportReport Report() => new() { RootPath = KitDir, KitName = "SampleKit" };

    [Fact]
    public void APartDefinedByANetlist_KeepsTheOriginalReassurance()
    {
        // The case the old wording was written for, and the one it was right about.
        var report = Report();
        report.Parts.Add(new PdkPart("PART_A", "Part A", PinCount: 2,
                                     DefinitionRelativePath: "netlists/parts.cnl", DefinitionCell: "PART_A"));

        string note = SimulationNote(PdkPartInstaller.Install(report));

        Assert.Contains("still built from the kit's own netlists", note);
    }

    [Fact]
    public void PartsWithTerminalsButNoDefinition_SayTheyPlaceAndWireButDoNotSimulate()
    {
        var report = Report();
        report.Parts.Add(new PdkPart("PART_A", "Part A", PinCount: 4));

        string note = SimulationNote(PdkPartInstaller.Install(report));

        Assert.Contains("placed and wired", note);
        Assert.DoesNotContain("still built from the kit's own netlists", note);
    }

    [Fact]
    public void PartsWithNoTerminalsAtAll_SayTheyImportAsNamesOnly()
    {
        // The reported kit: names, and nothing else.
        var report = Report();
        report.Parts.Add(new PdkPart("PART_A", "Part A"));
        report.Parts.Add(new PdkPart("PART_B", "Part B"));

        string note = SimulationNote(PdkPartInstaller.Install(report));

        Assert.Contains("as names only", note);
        Assert.Contains("cannot be wired", note);
        Assert.DoesNotContain("still built from the kit's own netlists", note);
    }

    [Fact]
    public void TheNamesOnlyMessage_PointsAtADifferentBuildOfTheKit()
    {
        // The owner's actual question — "I thought we could simulate these" — has an answer, and it is
        // the one thing the old message could not give: they were simulating a different build.
        var report = Report();
        report.Parts.Add(new PdkPart("PART_A", "Part A"));

        string note = SimulationNote(PdkPartInstaller.Install(report));

        Assert.Contains("different build of the kit", note);
    }

    [Fact]
    public void WhatTheKitDoesHold_IsNamedByFormatAndCount_NotJustCalledUnsupported()
    {
        var report = Report();
        report.Parts.Add(new PdkPart("PART_A", "Part A"));
        report.Add(new PdkAsset("a/symbol.oa", PdkAssetKind.SymbolArtwork,
                                PdkAssetSupport.RecognizedNotSupported, "binary cell view (symbol)"));
        report.Add(new PdkAsset("b/symbol.oa", PdkAssetKind.SymbolArtwork,
                                PdkAssetSupport.RecognizedNotSupported, "binary cell view (symbol)"));
        report.Add(new PdkAsset("m/lib.bin", PdkAssetKind.ModelData,
                                PdkAssetSupport.RecognizedNotSupported, "compiled model library"));

        string note = SimulationNote(PdkPartInstaller.Install(report));

        Assert.Contains("binary cell view (symbol) (2)", note);
        Assert.Contains("compiled model library (1)", note);
    }

    [Fact]
    public void AKitWithNothingUnreadableToName_SaysNothingAboutFormats()
    {
        // No "What it does hold:" clause when there is nothing to put in it.
        var report = Report();
        report.Parts.Add(new PdkPart("PART_A", "Part A"));

        string note = SimulationNote(PdkPartInstaller.Install(report));

        Assert.DoesNotContain("What it does hold", note);
    }
}
