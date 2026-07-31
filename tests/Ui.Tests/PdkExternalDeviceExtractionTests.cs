using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A cell backed by a device provider is a LEAF: it has a symbol and deliberately no schematic, so
/// extraction must emit one ExtDevice instance rather than trying to descend into it. These tests
/// drive the real installer and the real extractor end to end over a synthetic kit.
/// </summary>
public sealed class PdkExternalDeviceExtractionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-ext-" + Guid.NewGuid().ToString("N")[..8]);

    private string KitDir       => Path.Combine(_root, "kit");
    private string WorkspaceDir => Path.Combine(_root, "ws");
    private string SchematicDir => Path.Combine(WorkspaceDir, "tb", "schematic");

    public PdkExternalDeviceExtractionTests()
    {
        Directory.CreateDirectory(KitDir);
        Directory.CreateDirectory(SchematicDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // Three pins: two signal terminals and a thermal terminal, which is routinely left open.
    private const string SymbolFile = """
        1     7.707    0 0
        10    1    "PART_SYM"    2    1    0    0    341    0
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

    /// <summary>Installs a one-part kit and returns the generated cell folder.</summary>
    private string InstallPart(params string[] declaredParams)
    {
        string symRel = Path.Combine("symbols", "part.dsn");
        string symAbs = Path.Combine(KitDir, symRel);
        Directory.CreateDirectory(Path.GetDirectoryName(symAbs)!);
        File.WriteAllText(symAbs, SymbolFile);

        var report = new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" };
        report.Parts.Add(new PdkPart(
            "PART_A", "Part A",
            SymbolArtwork: new PdkAsset(symRel.Replace(Path.DirectorySeparatorChar, '/'),
                                        PdkAssetKind.SymbolArtwork, PdkAssetSupport.Supported,
                                        "symbol description (.dsn)"),
            Parameters: declaredParams.Length > 0
                ? declaredParams.Select(n => new PdkPartParameter(n, "")).ToList()
                : null));

        var outcome = PdkPartInstaller.Install(report, WorkspaceDir);
        return outcome.Items[0].Pdk!.CellDir!;
    }

    private SchematicEditModel ModelWithPart(string cellDir, out EditableComponent comp)
    {
        var model = new SchematicEditModel { SchematicDirectory = SchematicDir };
        comp = new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.Generic,
            CellRef      = Path.GetRelativePath(SchematicDir, cellDir),
            X = 0, Y = 0,
        };
        model.Components.Add(comp);
        return model;
    }

    private static Instance Extracted(SchematicEditModel model)
        => Assert.Single(NetExtractor.Extract(model, "tb").TestBench.Instances);

    // ── Emission ──────────────────────────────────────────────────────────────

    [Fact]
    public void ProviderBackedCell_EmitsOneExtDeviceInstance_NotAHierarchicalCell()
    {
        var model = ModelWithPart(InstallPart(), out _);

        var inst = Extracted(model);

        Assert.Equal("X1", inst.InstanceName);
        Assert.Equal("ExtDevice", inst.Reference);
    }

    [Fact]
    public void ProviderAndTypeAreEmittedAsParameters_NamingTheKitAndThePart()
    {
        var model = ModelWithPart(InstallPart(), out _);

        var inst = Extracted(model);

        Assert.Equal("SampleKit", inst.Overrides.Single(o => o.Name == "Provider").Expression);
        Assert.Equal("PART_A",    inst.Overrides.Single(o => o.Name == "Type").Expression);
    }

    [Fact]
    public void EveryPinIsBound_InPinOrder_IncludingOneLeftUnconnected()
    {
        // The thermal terminal is wired to nothing — ordinary and correct for an external device,
        // whose every node is its own ground-referenced port. It must still get a net, and must
        // never be reported as an extraction error.
        var model = ModelWithPart(InstallPart(), out _);

        var inst = Assert.Single(NetExtractor.Extract(model, "tb").TestBench.Instances);

        Assert.Equal(3, inst.NetBindings.Count);
        Assert.All(inst.NetBindings, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.Equal(3, inst.NetBindings.Distinct().Count());   // three independent, unconnected nets
    }

    [Fact]
    public void AnUnconnectedPin_IsNotAnExtractionConflict()
    {
        var model = ModelWithPart(InstallPart(), out _);

        var result = NetExtractor.Extract(model, "tb");

        Assert.Empty(result.Conflicts);
    }

    // ── Parameters ────────────────────────────────────────────────────────────

    [Fact]
    public void DeclaredParameters_BecomeTheCellsPublishedInterface_SoAPlacedPartCarriesThem()
    {
        // This is what makes the ordinary Parameter Editor work on a kit part: no separate
        // parameter surface is needed, the cell interface already drives it.
        string cellDir = InstallPart("Ldrawn", "Nfingers");

        var ccell = CellPersistence.LoadFromFile(Path.Combine(cellDir, CellFolder.CcellFileName));

        Assert.Equal(["Ldrawn", "Nfingers"], ccell.Parameters.Select(p => p.Name));
        // Blank defaults on purpose — the provider owns them; inventing values here would
        // silently override whatever the kit itself specifies.
        Assert.All(ccell.Parameters, p => Assert.Equal("", p.DefaultExpression));
    }

    [Fact]
    public void SetParameters_AreForwardedVerbatim_ForTheProviderToMatchAgainstItsDescriptor()
    {
        var model = ModelWithPart(InstallPart("Nfingers"), out var comp);
        comp.Parameters.Add(new EditableParameter { Name = "Nfingers", Expression = "12" });

        var inst = Extracted(model);

        Assert.Equal("12", inst.Overrides.Single(o => o.Name == "Nfingers").Expression);
    }

    [Fact]
    public void UnsetParameters_AreOmitted_SoTheProvidersOwnDefaultStands()
    {
        var model = ModelWithPart(InstallPart("Nfingers"), out var comp);
        comp.Parameters.Add(new EditableParameter { Name = "Nfingers", Expression = "" });

        var inst = Extracted(model);

        Assert.DoesNotContain(inst.Overrides, o => o.Name == "Nfingers");
    }

    [Fact]
    public void AStrayProviderOverrideOnTheInstance_CannotShadowTheCellsOwnIdentity()
    {
        var model = ModelWithPart(InstallPart(), out var comp);
        comp.Parameters.Add(new EditableParameter { Name = "Provider", Expression = "somethingElse" });
        comp.Parameters.Add(new EditableParameter { Name = "Type",     Expression = "somethingElse" });

        var inst = Extracted(model);

        Assert.Equal("SampleKit", Assert.Single(inst.Overrides, o => o.Name == "Provider").Expression);
        Assert.Equal("PART_A",    Assert.Single(inst.Overrides, o => o.Name == "Type").Expression);
    }

    // ── Ordinary cells are untouched ──────────────────────────────────────────

    [Fact]
    public void ACellWithNoProviderMarker_StillTakesTheOrdinaryHierarchicalPath()
    {
        string cellDir = InstallPart();

        // Strip the marker: this is now an ordinary cell with a symbol but no schematic, which the
        // hierarchical path reports as such. The point is that it does NOT emit an ExtDevice.
        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.ExternalProvider = null;
        ccell.ExternalType     = null;
        CellPersistence.SaveToFile(ccellPath, ccell);

        var model = ModelWithPart(cellDir, out _);
        var tb    = NetExtractor.Extract(model, "tb").TestBench;

        Assert.DoesNotContain(tb.Instances, i => i.Reference == "ExtDevice");
    }

    [Fact]
    public void AProviderWithNoDeviceType_IsReportedAndSkipped_NeverEmittedHalfFormed()
    {
        string cellDir   = InstallPart();
        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.ExternalType = null;
        CellPersistence.SaveToFile(ccellPath, ccell);

        var model  = ModelWithPart(cellDir, out _);
        var result = NetExtractor.Extract(model, "tb");

        Assert.Empty(result.TestBench.Instances);
        Assert.Contains(result.Conflicts, c => c.Contains("device type", StringComparison.OrdinalIgnoreCase));
    }

    // ── Round trip ────────────────────────────────────────────────────────────

    [Fact]
    public void MarkerFields_AreOmittedFromOrdinaryCells_SoExistingFilesAreUnchanged()
    {
        string json = CellPersistence.Serialize(new CcellFile());

        Assert.DoesNotContain("ExternalProvider", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ExternalType",     json, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkerFields_RoundTripThroughTheCellFile()
    {
        var round = CellPersistence.Deserialize(CellPersistence.Serialize(
            new CcellFile { ExternalProvider = "K", ExternalType = "T" }));

        Assert.Equal("K", round.ExternalProvider);
        Assert.Equal("T", round.ExternalType);
    }

    // ── Kit infrastructure parameters ─────────────────────────────────────────

    [Fact]
    public void ATextValuedParameter_IsKeptOffTheEditableInterface_ButStillEmitted()
    {
        // A path to the kit's own model data is infrastructure, not a design quantity: pointing one
        // instance at a different folder is a mistake, not a choice. It must still reach the
        // provider, so it is emitted without ever being offered for editing.
        string cellDir = InstallPartWith(
            new PdkPartParameter("Rth", "-1"),
            new PdkPartParameter("DataPath", "Kit_Data", IsText: true));

        var ccell = CellPersistence.LoadFromFile(Path.Combine(cellDir, CellFolder.CcellFileName));
        Assert.Equal(["Rth"], ccell.Parameters.Select(p => p.Name));
        Assert.Equal("Kit_Data", ccell.ExternalFixedParameters!["DataPath"]);

        var model = ModelWithPart(cellDir, out _);
        var inst  = Extracted(model);

        Assert.Equal("Kit_Data", inst.Overrides.Single(o => o.Name == "DataPath").Expression);
    }

    [Fact]
    public void AKitWithNoTextParameters_WritesNoFixedParameterBlock()
    {
        string cellDir = InstallPartWith(new PdkPartParameter("Rth", "-1"));

        var ccell = CellPersistence.LoadFromFile(Path.Combine(cellDir, CellFolder.CcellFileName));

        Assert.Null(ccell.ExternalFixedParameters);
    }

    /// <summary>Installs a one-part kit carrying the given declared parameters.</summary>
    private string InstallPartWith(params PdkPartParameter[] pars)
    {
        string symRel = Path.Combine("symbols", "part.dsn");
        string symAbs = Path.Combine(KitDir, symRel);
        Directory.CreateDirectory(Path.GetDirectoryName(symAbs)!);
        File.WriteAllText(symAbs, SymbolFile);

        var report = new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" };
        report.Parts.Add(new PdkPart("PART_A", "Part A",
            SymbolArtwork: new PdkAsset(symRel.Replace(Path.DirectorySeparatorChar, '/'),
                                        PdkAssetKind.SymbolArtwork, PdkAssetSupport.Supported,
                                        "symbol description (.dsn)"),
            Parameters: pars));

        return PdkPartInstaller.Install(report, WorkspaceDir).Items[0].Pdk!.CellDir!;
    }
}
