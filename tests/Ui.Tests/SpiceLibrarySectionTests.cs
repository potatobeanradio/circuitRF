using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Choosing a <c>.lib</c> SECTION from the import and place gestures.
///
/// <para>The reader has handled sections since it was written; what nobody could do was ASK. Both
/// public entry points hard-coded <c>section: null</c>, so a purely sectioned library file opened
/// through either gesture reported "one of several alternatives … none was requested" for each
/// section and imported nothing at all.</para>
///
/// <para><b>The default stays "no section", which is not "all of them".</b> Sections are
/// alternatives; reading them all would produce a library holding two mutually exclusive versions of
/// one part, and reading the first would be a guess.</para>
/// </summary>
public sealed class SpiceLibrarySectionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "crf-spicesec-" + Guid.NewGuid().ToString("N")[..8]);

    public SpiceLibrarySectionTests()
    {
        Directory.CreateDirectory(_root);
        SpiceModelPeek.InvalidateAll();
    }

    public void Dispose()
    {
        SpiceModelPeek.InvalidateAll();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private const string TwoSections = """
        .lib nominal
        .subckt PART_A p n
        R1 p n 1k
        .ends
        .endl

        .lib fast
        .subckt PART_B p n c d
        R1 p n 2k
        R2 c d 3k
        .ends
        .endl
        """;

    private string Write(string name, string text)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    // ── The scan ─────────────────────────────────────────────────────────────

    [Fact]
    public void ScanningASectionedFileWhole_FindsNothing_ButLearnsWhatItOffers()
    {
        var scan = SpiceCellImport.Scan(Write("kit.lib", TwoSections));

        Assert.Empty(scan.Candidates);
        Assert.Equal(["nominal", "fast"], scan.SectionNames);
        Assert.Null(scan.Section);
    }

    [Fact]
    public void ScanningOneSection_FindsThatSectionsDefinitionsOnly()
    {
        var path = Write("kit.lib", TwoSections);

        Assert.Equal(["PART_A"], SpiceCellImport.Scan(path, "nominal").Candidates.Select(c => c.Name));
        Assert.Equal(["PART_B"], SpiceCellImport.Scan(path, "fast").Candidates.Select(c => c.Name));

        // Both readings still know the alternatives — which is what lets the picker keep offering the
        // other one after a choice has been made.
        Assert.Equal(["nominal", "fast"], SpiceCellImport.Scan(path, "fast").SectionNames);
    }

    [Fact]
    public void OnlyTheScannedFilesOwnSectionsAreReported_NotThoseOfAnythingItIncludes()
    {
        // A kit states one axis per FILE, so an included file's sections are an independent choice.
        // Flattening them into one list would offer a single pick where the deck offers several.
        Write("inner.lib", TwoSections);
        var outer = Write("outer.lib", """
            .include inner.lib
            .subckt WRAPPER p n
            R1 p n 1k
            .ends
            """);

        var scan = SpiceCellImport.Scan(outer);
        Assert.Empty(scan.SectionNames);
        Assert.Contains(scan.Candidates, c => c.Name == "WRAPPER");
    }

    // ── The placed component ─────────────────────────────────────────────────

    [Fact]
    public void TheSectionIsPartOfTheSymbolReference_OrTheCacheReturnsThePreviousOne()
    {
        // CellSymbolResolver's cache is keyed on this exact string. Two sections of one file are two
        // different sets of definitions, so a reference that named only file and definition would be
        // the same string for both — and the symbol would not change when the section did.
        string nominal = SpiceModelSymbolProvider.RefFor(
            "kit.lib", "", SnpPinConfig.Standard, SnpPitch.Loose, "nominal");
        string fast = SpiceModelSymbolProvider.RefFor(
            "kit.lib", "", SnpPinConfig.Standard, SnpPitch.Loose, "fast");

        Assert.NotEqual(nominal, fast);
        Assert.Equal("nominal", SpiceModelSymbolProvider.Parse(nominal)!.Value.Section);
        Assert.Equal("kit.lib", SpiceModelSymbolProvider.Parse(fast)!.Value.File);
    }

    [Fact]
    public void TheSymbolDrawnForOneSection_IsTheOneThatSectionDefines()
    {
        Write("kit.lib", TwoSections);

        // PART_A has two ports and PART_B four, so the pin count alone separates them — nothing here
        // depends on reading the glyph.
        var a = SpiceModelSymbolProvider.Resolve(
            SpiceModelSymbolProvider.RefFor("kit.lib", "", SnpPinConfig.Standard, SnpPitch.Loose, "nominal"),
            _root);
        var b = SpiceModelSymbolProvider.Resolve(
            SpiceModelSymbolProvider.RefFor("kit.lib", "", SnpPinConfig.Standard, SnpPitch.Loose, "fast"),
            _root);

        Assert.Equal(CellSymbolState.Resolved, a.State);
        Assert.Equal(CellSymbolState.Resolved, b.State);
        Assert.Equal(2, a.Symbol!.Pins.Count);
        Assert.Equal(4, b.Symbol!.Pins.Count);
    }

    [Fact]
    public void OneCachedReadPerSection_SoTwoInstancesOfOneFileDoNotOverwriteEachOther()
    {
        string path = Write("kit.lib", TwoSections);

        var nominal = SpiceModelPeek.Read(path, "nominal");
        var fast    = SpiceModelPeek.Read(path, "fast");
        var again   = SpiceModelPeek.Read(path, "nominal");

        Assert.Equal(["PART_A"], nominal.Definitions.Select(d => d.Name));
        Assert.Equal(["PART_B"], fast.Definitions.Select(d => d.Name));
        Assert.Same(nominal, again);                     // the cache still works, keyed per section
    }

    [Fact]
    public void AWholeFileReadOfASectionedFile_SaysWhatItOffersRatherThanJustHoldsNothing()
    {
        var file = SpiceModelPeek.Read(Write("kit.lib", TwoSections));

        Assert.NotNull(file.Error);
        Assert.Contains("nominal", file.Error);
        Assert.Contains("fast", file.Error);
    }

    // ── The parameter panel ──────────────────────────────────────────────────

    private ParameterEditorViewModel Open(string file, string name = "", string section = "")
    {
        var model = new SchematicEditModel { SchematicDirectory = _root };
        var comp  = new EditableComponent { Symbol = SymbolKind.SpiceModel, InstanceName = "X1" };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.SpiceModel, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        comp.Parameters.First(p => p.Name == "File").Expression    = file;
        comp.Parameters.First(p => p.Name == "Name").Expression    = name;
        comp.Parameters.First(p => p.Name == "Section").Expression = section;
        model.Components.Add(comp);

        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(new SchematicViewModel(model), comp, showClose: false);
        return editor;
    }

    [Fact]
    public void ThePanelOffersTheSectionsOnlyWhenTheFileDeclaresAny()
    {
        Write("kit.lib", TwoSections);
        Write("plain.lib", ".subckt PLAIN p n\nR1 p n 1k\n.ends\n");

        var sectioned = Open("kit.lib");
        Assert.True(sectioned.SpiceModelShowSections);
        Assert.Equal(["Whole file (no section)", "nominal", "fast"], sectioned.SpiceModelSectionOptions);
        Assert.Equal(0, sectioned.SpiceModelSectionIndex);        // today's default, unchanged

        // A file declaring none gets no combo at all — a question with one answer is worse than none.
        Assert.False(Open("plain.lib").SpiceModelShowSections);
    }

    [Fact]
    public void ChoosingASection_ChangesWhatTheNameComboOffers()
    {
        Write("kit.lib", TwoSections);

        var editor = Open("kit.lib", section: "fast");

        Assert.Equal(2, editor.SpiceModelSectionIndex);            // "fast", past the leading entry
        Assert.Single(editor.SpiceModelNameOptions);
        Assert.StartsWith("PART_B", editor.SpiceModelNameOptions[0], StringComparison.Ordinal);
        Assert.False(editor.SpiceModelStatusIsProblem);
    }

    [Fact]
    public void ASectionTheFileDoesNotDeclare_ReportsWhatItDoesOffer()
    {
        Write("kit.lib", TwoSections);

        var editor = Open("kit.lib", section: "slow");

        Assert.True(editor.SpiceModelStatusIsProblem);
        Assert.Contains("does not declare a section called 'slow'", editor.SpiceModelStatus);
        Assert.Contains("nominal", editor.SpiceModelStatus);
        Assert.Contains("fast", editor.SpiceModelStatus);
    }

    [Fact]
    public void SectionIsAPanelParameter_SoItNeverAppearsAsAGenericRow()
    {
        Assert.True(SpiceModelSymbolProvider.IsPanelParameter(SpiceModelSymbolProvider.SectionParameter));

        Write("kit.lib", TwoSections);
        var editor = Open("kit.lib", section: "nominal");
        Assert.DoesNotContain(editor.Rows, r => r.Name == "Section");
    }
}
