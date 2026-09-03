using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// "Update Layout from Schematic" on a placed kit part.
///
/// <para>A kit part is a VIRTUAL reference. Resolving it as a path made this report "referenced cell
/// not found" for every one of them — which is false: the part is loaded and drawing perfectly on the
/// schematic. What such a part may genuinely lack is a LAYOUT generator, and that is a different
/// sentence with a different answer, so it is the one the user gets.</para>
///
/// <para>Fixtures name no vendor and no part.</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class KitPartToLayoutTests : IDisposable
{
    private const string Kit  = "SampleKit";
    private const string Part = "PART_A";

    private readonly string _root;

    public KitPartToLayoutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-kit-layout-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        PdkKitRegistry.ResetAllForTests();
        PdkKitRegistry.SetKit(_root, Kit, [MakePart()]);
    }

    public void Dispose()
    {
        PdkKitRegistry.ResetAllForTests();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static PdkKitPart MakePart()
    {
        var sym = new Symbol(
            primitives: [new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, -100, 0, 100, 0)],
            pins:       [new SymbolPin(-200, 0, 1, "a"), new SymbolPin(200, 0, 2, "b")],
            portCount:  2);
        return new PdkKitPart(Part, sym, new CcellFile { NumPorts = 2 }, IconPath: null);
    }

    private SchematicEditModel ModelWithPlacedKitPart()
    {
        var model = new SchematicEditModel { SchematicDirectory = _root };
        model.Components.Add(new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.Generic,
            CellRef      = PdkKitRegistry.RefFor(Kit, Part),
            X = 0, Y = 0,
        });
        return model;
    }

    /// <summary>
    /// The kit has no layout generator for the part, so nothing can be placed — and the report says
    /// exactly that, naming the kit and the part, instead of claiming the cell is missing.
    /// </summary>
    [Fact]
    public void L1_APartWithNoLayoutGenerator_IsReportedAccurately()
    {
        var result = SchematicToLayoutGenerator.Run(
            ModelWithPlacedKitPart(), new LayoutView(), _root, _root, _root, null, null, null);

        string warning = Assert.Single(result.NoLayoutWarnings);

        Assert.Contains("X1", warning);
        Assert.Contains(Kit,  warning);
        Assert.Contains(Part, warning);
        Assert.Contains("no layout cell", warning);

        // The old message sent the user looking for a folder that was never supposed to exist.
        Assert.DoesNotContain("referenced cell not found", warning);

        // SHORT, and that is the point of this half. This line used to carry three clauses telling
        // the user to go and drop the cell from the palette themselves — written when the pairing
        // rules were routinely failing to match a kit's parts to its cells at all. Once the pairing
        // works, a part that reaches here is a MODEL-ONLY part (a parasitic capacitance, a technology
        // include) with no artwork to place, and a paragraph of recovery advice per placed part is
        // noise. If a kit's cells stop being paired, that is KitPaletteMerge's business.
        Assert.True(warning.Length < 120, $"the skip line is {warning.Length} characters: {warning}");
    }

    /// <summary>
    /// A reference to a kit that is not loaded is its own state — repairable by adding the kit back,
    /// which is a different instruction from "this part has no artwork".
    /// </summary>
    [Fact]
    public void L2_AReferenceToAnUnloadedKit_SaysSo()
    {
        var model = ModelWithPlacedKitPart();
        PdkKitRegistry.ResetAllForTests();

        var result = SchematicToLayoutGenerator.Run(
            model, new LayoutView(), _root, _root, _root, null, null, null);

        string warning = Assert.Single(result.NoLayoutWarnings);
        Assert.Contains("is not loaded", warning);
    }

    /// <summary>
    /// Whatever is reported, nothing is placed and nothing is silently skipped — a component that
    /// contributed no artwork must always leave a line saying why.
    /// </summary>
    [Fact]
    public void L3_NothingIsPlaced_AndNothingIsSilentlySkipped()
    {
        var target = new LayoutView();

        var result = SchematicToLayoutGenerator.Run(
            ModelWithPlacedKitPart(), target, _root, _root, _root, null, null, null);

        Assert.Empty(target.Instances);
        Assert.Equal(0, result.AddedCount);
        Assert.NotEmpty(result.NoLayoutWarnings);
    }
}
