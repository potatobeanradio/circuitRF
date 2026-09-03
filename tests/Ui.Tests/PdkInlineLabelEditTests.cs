using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Inline label editing on an imported kit part must work exactly as it does on a built-in.
///
/// <para>It did not: three places independently decided "how tall is this component's glyph", and
/// only the renderer used the RESOLVED cell symbol. The label hit-test and the inline editor's
/// anchor both used the built-in placeholder glyph, so the clickable zone sat where no text was and
/// neither the Type nor the Name label could be reached. They all read
/// <see cref="SchematicEditModel.EffectiveGlyphBbOf"/> now.</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class PdkInlineLabelEditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-inl-" + Guid.NewGuid().ToString("N")[..8]);

    private string KitDir       => Path.Combine(_root, "kit");
    private string WorkspaceDir => Path.Combine(_root, "ws");
    private string SchematicDir => Path.Combine(WorkspaceDir, "tb", "schematic");

    public PdkInlineLabelEditTests()
    {
        Directory.CreateDirectory(KitDir);
        Directory.CreateDirectory(SchematicDir);
    }

    public void Dispose()
    {
        PdkKitRegistry.ResetAllForTests();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // A deliberately TALL symbol: the whole point is that its drawn extent differs from the
    // built-in placeholder glyph the component's SymbolKind names, so a hit-test that used the
    // placeholder lands in the wrong place.
    private const string SymbolFile = """
        1     7.707    0 0
        10    1    "PART_SYM"    2    1    0    0    341    0
        20    0    ""    0 0 0 0 0    2 -3 1    1    0    "schematic.prf" "schematic.lay"
        44    0    -1200    1200    1200    1    0    0
        50    2    0 -1000 0 1000 1    0    0    0    0    0    0    0    0
        60    4    0    2    0 -1000 0 1000 1    0    0    0    0
        70    0 -1000    0 1000
        42    1    2    "gate"      1    2    0    0 -1000 180000    0    0   ""
        42    2    2    "drain"     2    1    0    0 1000 0    0    0   ""
        21
        """;

    /// <summary>
    /// Imports a one-kit PDK holding the named parts and returns the first part's reference. Every
    /// part goes in ONE import: registering a kit REPLACES what was held for it, so two imports
    /// would leave only the second part loaded.
    /// </summary>
    private string InstallPart(params string[] partIds)
    {
        if (partIds.Length == 0) partIds = ["PART_A"];

        var report = new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" };
        foreach (string partId in partIds)
        {
            string symRel = Path.Combine("symbols", partId + ".dsn");
            string symAbs = Path.Combine(KitDir, symRel);
            Directory.CreateDirectory(Path.GetDirectoryName(symAbs)!);
            File.WriteAllText(symAbs, SymbolFile);

            report.Parts.Add(new PdkPart(
                partId, partId,
                SymbolArtwork: new PdkAsset(symRel.Replace(Path.DirectorySeparatorChar, '/'),
                                            PdkAssetKind.SymbolArtwork, PdkAssetSupport.Supported,
                                            "symbol description (.dsn)")));
        }

        var outcome = PdkPartInstaller.Install(report);
        PdkKitRegistry.SetKit(WorkspaceDir, outcome.KitName, outcome.Parts ?? []);
        return outcome.Items[0].Pdk!.CellDir!;
    }

    private (SchematicEditModel Model, EditableComponent Comp) ModelWithPart(string cellRef)
    {
        var model = new SchematicEditModel { SchematicDirectory = SchematicDir };
        var comp = new EditableComponent
        {
            InstanceName     = "X1",
            Symbol           = SymbolKind.Generic,
            // Virtual, not a relative path: the part is held in memory.
            CellRef          = cellRef,
            X = 0, Y = 0,
            ShowTypeLabel    = true,
            ShowInstanceName = true,
        };
        model.Components.Add(comp);
        return (model, comp);
    }

    /// <summary>Probes the centre of a rendered label row through the real hit-test.</summary>
    private static SchematicHitTest.HitResult ProbeLabelRow(
        SchematicEditModel model, EditableComponent comp, int row, string labelText)
    {
        var (render, index) = model.BuildRenderModel();
        double glyphHalfH = model.EffectiveGlyphBbOf(comp).MaxY - comp.Y;
        var (baseX, _, bandTop, bandBot) = SchematicComponent.LabelRowGeometry(
            comp.X, comp.Y, row, 0, 0, comp.Symbol, comp.PortCount, glyphHalfH);

        // Inside the text horizontally, not merely at its left edge.
        double probeX = baseX + Math.Max(1, labelText.Length / 2.0) * 5;
        return SchematicHitTest.Test(model, render, index, probeX, (bandTop + bandBot) * 0.5,
                                     includeLabels: true);
    }

    // ── The reported bug ──────────────────────────────────────────────────────

    [Fact]
    public void TheTypeLabelOfAKitPart_IsClickable_AtTheRowItActuallyRendersIn()
    {
        var (model, comp) = ModelWithPart(InstallPart());

        var hit = ProbeLabelRow(model, comp, row: 0, comp.TypeLabelText());

        Assert.Equal(SchematicHitTest.HitKind.ComponentType, hit.Kind);
        Assert.Equal(comp.Id, hit.Id);
    }

    [Fact]
    public void TheInstanceNameOfAKitPart_IsClickable_AtTheRowItActuallyRendersIn()
    {
        var (model, comp) = ModelWithPart(InstallPart());

        var hit = ProbeLabelRow(model, comp, row: 1, comp.InstanceName);

        Assert.Equal(SchematicHitTest.HitKind.ComponentName, hit.Kind);
        Assert.Equal(comp.Id, hit.Id);
    }

    /// <summary>
    /// The direct regression: the label band a kit part's text renders in is NOT the one the
    /// built-in placeholder glyph would put it in. Without this difference the two tests above
    /// would pass against the pre-fix code by coincidence.
    /// </summary>
    [Fact]
    public void AKitPartsDrawnGlyph_IsTallerThanItsPlaceholderSymbolKindsGlyph()
    {
        var (model, comp) = ModelWithPart(InstallPart());

        double effective = model.EffectiveGlyphBbOf(comp).MaxY;
        double builtIn   = comp.ComputeGlyphBb().MaxY;

        Assert.True(effective > builtIn + 100,
            $"fixture is not discriminating: effective={effective}, built-in={builtIn}");
    }

    // ── The rendered text, the clickable zone and the seeded edit value agree ──

    [Fact]
    public void TheTypeLabelText_IsTheCellName_NotTheRegistryNameOfItsPlaceholderKind()
    {
        var (_, comp) = ModelWithPart(InstallPart());

        Assert.Equal("PART_A", comp.TypeLabelText());
        Assert.NotEqual(ComponentTypeRegistry.DisplayName(comp.Symbol, comp.PortCount),
                        comp.TypeLabelText());
    }

    // ── Committing a type change ──────────────────────────────────────────────

    [Fact]
    public void RetypingAKitPartAsABuiltIn_ReplacesIt()
    {
        var (model, comp) = ModelWithPart(InstallPart());
        var vm = new SchematicViewModel(model);

        vm.BeginInlineEditForHit(
            new SchematicHitTest.HitResult(SchematicHitTest.HitKind.ComponentType, comp.Id), 0, 0);
        vm.InlineEditValue = "R";
        vm.CommitInlineEdit();

        var now = Assert.Single(model.Components);
        Assert.Equal(SymbolKind.Resistor, now.Symbol);
        Assert.Null(now.CellRef);
    }

    [Fact]
    public void RetypingAKitPartAsAnotherKitPart_SwapsTheCellReference()
    {
        var (model, comp) = ModelWithPart(InstallPart("PART_A", "PART_B"));
        var vm = new SchematicViewModel(model) { WorkspaceRootProvider = () => WorkspaceDir };

        vm.BeginInlineEditForHit(
            new SchematicHitTest.HitResult(SchematicHitTest.HitKind.ComponentType, comp.Id), 0, 0);
        vm.InlineEditValue = "PART_B";
        vm.CommitInlineEdit();

        var now = Assert.Single(model.Components);
        Assert.Equal("PART_B", now.TypeLabelText());
        Assert.NotNull(now.CellRef);
    }

    /// <summary>
    /// Typing the label back unchanged is not an edit. Before the fix a cell name fell through to
    /// the built-in code parser, which cannot parse one, and answered a no-op with a warning.
    /// </summary>
    [Fact]
    public void RetypingTheSameCellName_ChangesNothing_AndDoesNotWarn()
    {
        var (model, comp) = ModelWithPart(InstallPart());
        var sink = new RecordingSink();
        var vm = new SchematicViewModel(model, sink) { WorkspaceRootProvider = () => WorkspaceDir };
        string originalId = comp.Id;

        vm.BeginInlineEditForHit(
            new SchematicHitTest.HitResult(SchematicHitTest.HitKind.ComponentType, comp.Id), 0, 0);
        vm.InlineEditValue = "PART_A";
        vm.CommitInlineEdit();

        Assert.Equal(originalId, Assert.Single(model.Components).Id);
        Assert.Empty(sink.Warnings);
    }

    [Fact]
    public void RenamingAKitPartInstance_Works()
    {
        var (model, comp) = ModelWithPart(InstallPart());
        var vm = new SchematicViewModel(model);

        vm.BeginInlineEditForHit(
            new SchematicHitTest.HitResult(SchematicHitTest.HitKind.ComponentName, comp.Id), 0, 0);
        vm.InlineEditValue = "Q7";
        vm.CommitInlineEdit();

        Assert.Equal("Q7", Assert.Single(model.Components).InstanceName);
    }

    private sealed class RecordingSink : CircuitRF.Ui.Messages.IMessageSink
    {
        public System.Collections.Generic.List<string> Warnings { get; } = [];

        public void Post(CircuitRF.Ui.Messages.MessageLevel level, string text, string? filePath = null)
        {
            if (level == CircuitRF.Ui.Messages.MessageLevel.Warning) Warnings.Add(text);
        }

        public void Clear() => Warnings.Clear();
    }
}
