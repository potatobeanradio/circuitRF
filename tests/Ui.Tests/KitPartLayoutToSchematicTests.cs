using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// "Update Schematic from Layout" on a KIT's own cell.
///
/// <para><b>It did nothing at all, silently.</b> The command decided what a generated cell IS by
/// looking its generator id up in a hardcoded table of circuitRF's six built-in microstrip
/// generators. A kit's cell is discovered at run time and is in no table, so every PDK component in
/// a layout failed that lookup and was skipped — no component created, and no parameter pushed back
/// onto one already linked. Nothing was reported, because nothing was treated as having happened.</para>
///
/// <para>The second half is the same kind/units question the forward direction has: a vendor cell
/// states its dimensions as TEXT, in metres. Compared against the schematic's Real it always reads as
/// changed, and written back into a row whose unit is µm it is wrong by a factor of a million.</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class KitPartLayoutToSchematicTests : IDisposable
{
    // Distinct from every other kit fixture's names — see KitPartLayoutParametersTests for why.
    private const string Kit       = "PushBackKit";
    private const string Part      = "PUSHBACK_PART";
    private const string Generator = "pushback_cell";

    private readonly string _root;

    public KitPartLayoutToSchematicTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-l2s-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        PdkKitRegistry.ResetAllForTests();
        PdkKitRegistry.SetKit(null, Kit, [MakePart()]);
        KitLayoutGenerators.Publish(null, [
            new PaletteItem(SymbolKind.Generic, 0, Part, ComponentCategory.Other, [Part], false, null,
                new PdkPartRef(Kit, Part, null, PdkKitRegistry.RefFor(Kit, Part), "", "a_model"),
                Generator),
        ]);
    }

    public void Dispose()
    {
        KitLayoutGenerators.ResetAllForTests();
        PdkKitRegistry.ResetAllForTests();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static PdkKitPart MakePart()
    {
        var sym = new Symbol(
            primitives: [new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal, -100, 0, 100, 0)],
            pins:       [new SymbolPin(-200, 0, 1, "a"), new SymbolPin(200, 0, 2, "b")],
            portCount:  2);

        // The published interface a placed instance seeds from — the kit's own rows, plus circuitRF's
        // ModelLibrary, exactly as PdkPartInstaller builds them.
        var ccell = new CcellFile
        {
            NumPorts = 2,
            Parameters =
            {
                new CcellParameter { Name = "ModelLibrary", DefaultExpression = "", IsFilePath = true },
                new CcellParameter { Name = "w", DefaultExpression = "7.0e-6" },
                new CcellParameter { Name = "l", DefaultExpression = "7.0e-6" },
            },
        };
        return new PdkKitPart(Part, sym, ccell, IconPath: null);
    }

    /// <summary>A generated cell folder on disk, carrying the PCellOrigin the command reads.</summary>
    private string WriteGeneratedCell(string name, Dictionary<string, PCellValue> parameters)
    {
        string cellDir = Path.Combine(_root, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        Directory.CreateDirectory(layoutDir);

        var view = new LayoutView { DbuPerMicron = 1000, PCellOrigin = new PCellOrigin(Generator, parameters) };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, name + CellFolder.ViewExtension(ViewType.Layout)), view);
        CellPersistence.SaveToFile(Path.Combine(cellDir, CellFolder.CcellFileName),
                                   new CcellFile { PrimaryLayout = name + CellFolder.ViewExtension(ViewType.Layout) });
        return cellDir;
    }

    private LayoutView LayoutWith(string cellDir, string? schematicId = null)
    {
        var layout = new LayoutView { DbuPerMicron = 1000 };
        layout.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(_root, cellDir), X = 0, Y = 0, Mag = 1.0,
            SchematicId = schematicId,
        });
        return layout;
    }

    // ── the create half ───────────────────────────────────────────────────────

    /// <summary>
    /// A kit cell placed in a layout with no schematic component behind it creates one — as the KIT's
    /// part, carrying the reference that resolves the kit's own symbol, not a bare box.
    /// </summary>
    [Fact]
    public void AKitCellInTheLayout_CreatesTheKitsOwnPart()
    {
        string cellDir = WriteGeneratedCell("cell_a_1", new()
        {
            ["w"] = PCellValue.Text("3E-05"),
            ["l"] = PCellValue.Text("4E-05"),
        });

        var schematic = new SchematicEditModel { SchematicDirectory = _root };
        var result = LayoutToSchematicGenerator.Run(LayoutWith(cellDir), schematic, _root);

        Assert.Equal(1, result.CreatedCount);
        result.Command!.Execute();

        var comp = Assert.Single(schematic.Components);
        Assert.Equal(SymbolKind.Generic, comp.Symbol);
        Assert.Equal(PdkKitRegistry.RefFor(Kit, Part), comp.CellRef);
        Assert.StartsWith("X", comp.InstanceName);

        // Seeded from the part's own published interface — the same rows placing it from the palette
        // gives — and then the layout's values written over them.
        Assert.Equal(["ModelLibrary", "w", "l"], comp.Parameters.Select(p => p.Name));
        Assert.Equal(3E-05, double.Parse(comp.Parameters.Single(p => p.Name == "w").Expression,
                                         System.Globalization.CultureInfo.InvariantCulture), 15);
        Assert.Equal(4E-05, double.Parse(comp.Parameters.Single(p => p.Name == "l").Expression,
                                         System.Globalization.CultureInfo.InvariantCulture), 15);
    }

    /// <summary>
    /// A sub-micron dimension survives the round trip. The push-back formats to six decimal places,
    /// which is a readable number in mm or mil and cannot express 6.99 µm in METRES — the unit a kit
    /// part's rows carry. 0.000007 is a different capacitor.
    /// </summary>
    [Fact]
    public void ASubMicronDimension_IsNotRoundedAwayOnTheWayBack()
    {
        string cellDir = WriteGeneratedCell("cell_a_2", new() { ["w"] = PCellValue.Text("6.99E-06") });

        var schematic = new SchematicEditModel { SchematicDirectory = _root };
        var result = LayoutToSchematicGenerator.Run(LayoutWith(cellDir), schematic, _root);
        result.Command!.Execute();

        string written = Assert.Single(schematic.Components).Parameters.Single(p => p.Name == "w").Expression;
        Assert.Equal(6.99E-06, double.Parse(written, System.Globalization.CultureInfo.InvariantCulture), 15);
    }

    /// <summary>
    /// A kit that is not loaded is its own state, and it is REPORTED rather than skipped: the part's
    /// symbol and parameter interface both live in the kit, so a component created without it would
    /// be an unresolved box with no rows at all.
    /// </summary>
    [Fact]
    public void AKitThatIsNotLoaded_SaysSoRatherThanCreatingAnEmptyComponent()
    {
        string cellDir = WriteGeneratedCell("cell_a_3", new() { ["w"] = PCellValue.Text("3E-05") });
        PdkKitRegistry.ResetAllForTests();

        var schematic = new SchematicEditModel { SchematicDirectory = _root };
        var result = LayoutToSchematicGenerator.Run(LayoutWith(cellDir), schematic, _root);

        Assert.Equal(0, result.CreatedCount);
        Assert.Empty(schematic.Components);
        Assert.Contains(result.Lines, l => l.Text.Contains("is not loaded") && l.Text.Contains(Part));
    }

    // ── the push-back half ────────────────────────────────────────────────────

    /// <summary>An already-linked kit component takes the layout's value.</summary>
    [Fact]
    public void ALinkedKitComponent_TakesTheLayoutsValue()
    {
        string cellDir = WriteGeneratedCell("cell_a_4", new() { ["w"] = PCellValue.Text("5E-05") });

        var schematic = new SchematicEditModel { SchematicDirectory = _root };
        schematic.Components.Add(new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.Generic,
            CellRef      = PdkKitRegistry.RefFor(Kit, Part),
            Parameters   = { new EditableParameter { Name = "w", Expression = "3e-5" } },
        });

        var result = LayoutToSchematicGenerator.Run(LayoutWith(cellDir, "X1"), schematic, _root);

        Assert.Equal(1, result.UpdatedCount);
        result.Command!.Execute();

        Assert.Equal(5E-05, double.Parse(schematic.Components[0].Parameters[0].Expression,
                                         System.Globalization.CultureInfo.InvariantCulture), 15);
        Assert.Contains(result.Lines, l => l.Text.Contains("w changed"));
    }

    /// <summary>
    /// The two views already agree, so the command reports NOTHING CHANGED — even though the layout
    /// holds text and the schematic holds an expression. This is the assertion that fails when a
    /// number spelled as text is compared by kind: every kit parameter then reads as changed on every
    /// push, forever, and every push rewrites the schematic.
    /// </summary>
    [Fact]
    public void AgreementIsRecognisedAcrossTheTextNumberBoundary()
    {
        string cellDir = WriteGeneratedCell("cell_a_5", new() { ["w"] = PCellValue.Text("3E-05") });

        var schematic = new SchematicEditModel { SchematicDirectory = _root };
        schematic.Components.Add(new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.Generic,
            CellRef      = PdkKitRegistry.RefFor(Kit, Part),
            Parameters   = { new EditableParameter { Name = "w", Expression = "30", Unit = "µm" } },
        });

        var result = LayoutToSchematicGenerator.Run(LayoutWith(cellDir, "X1"), schematic, _root);

        Assert.Equal(1, result.UnchangedCount);
        Assert.Equal(0, result.UpdatedCount);
        Assert.True(result.NothingChanged);
    }

    /// <summary>
    /// A row that carries a unit gets the value IN that unit. Writing the cell's own metres into a µm
    /// row would be wrong by a factor of a million, from the one command whose purpose is to keep the
    /// two views agreeing.
    /// </summary>
    [Fact]
    public void ARowWithAUnit_ReceivesTheValueInThatUnit()
    {
        string cellDir = WriteGeneratedCell("cell_a_6", new() { ["w"] = PCellValue.Text("5E-05") });

        var schematic = new SchematicEditModel { SchematicDirectory = _root };
        schematic.Components.Add(new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.Generic,
            CellRef      = PdkKitRegistry.RefFor(Kit, Part),
            Parameters   = { new EditableParameter { Name = "w", Expression = "30", Unit = "µm" } },
        });

        var result = LayoutToSchematicGenerator.Run(LayoutWith(cellDir, "X1"), schematic, _root);
        result.Command!.Execute();

        Assert.Equal(50.0, double.Parse(schematic.Components[0].Parameters[0].Expression,
                                        System.Globalization.CultureInfo.InvariantCulture), 9);
        Assert.Equal("µm", schematic.Components[0].Parameters[0].Unit);
    }

    /// <summary>A word-valued parameter still pushes back as its word.</summary>
    [Fact]
    public void AWordValuedParameter_PushesBackAsThatWord()
    {
        string cellDir = WriteGeneratedCell("cell_a_7", new() { ["w"] = PCellValue.Text("Selected") });

        var schematic = new SchematicEditModel { SchematicDirectory = _root };
        schematic.Components.Add(new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.Generic,
            CellRef      = PdkKitRegistry.RefFor(Kit, Part),
            Parameters   = { new EditableParameter { Name = "w", Expression = "3e-5" } },
        });

        var result = LayoutToSchematicGenerator.Run(LayoutWith(cellDir, "X1"), schematic, _root);
        result.Command!.Execute();

        Assert.Equal("Selected", schematic.Components[0].Parameters[0].Expression);
    }
}
