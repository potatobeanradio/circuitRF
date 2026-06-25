using System.Collections.Generic;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for the Analyses↔DataDisplay cross-pane link: focusing a Data Display tab whose base
/// filename matches an OPEN schematic points the Analyses panel at that schematic.
/// Exercises the pure matcher <see cref="WorkspaceViewModel.MatchSchematicForDataDisplay"/> directly
/// (WorkspaceViewModel itself needs the Avalonia runtime; the matcher is framework-free).
/// </summary>
public class AnalysesDataDisplayLinkTests
{
    private static SchematicDocument Sch(string cellName, string? filePath = null)
        => new(cellName, new SchematicViewModel(new SchematicEditModel()), filePath);

    private static DataDisplayDocument Dd(string title, string? filePath = null)
        => new(title, new DataDisplayDocumentViewModel(), filePath);

    private static SchematicDocument? Match(DataDisplayDocument dd, params SchematicDocument[] open)
        => WorkspaceViewModel.MatchSchematicForDataDisplay(dd, open);

    [Fact]
    public void Match_ByFileName_IgnoresDirectory()
    {
        var amp  = Sch("Amp",  "/ws/Amp/schematic/Amp.csch");
        var bias = Sch("Bias", "/ws/Bias/schematic/Bias.csch");
        var dd   = Dd("Amp", "/ws/results/Amp.cdd");   // different folder, same base name

        Assert.Same(amp, Match(dd, bias, amp));
    }

    [Fact]
    public void Match_IsCaseInsensitive()
    {
        var amp = Sch("Amp", "/ws/Amp.csch");
        var dd  = Dd("amp",  "/ws/AMP.cdd");

        Assert.Same(amp, Match(dd, amp));
    }

    [Fact]
    public void NoMatch_DifferentBaseName_ReturnsNull()
    {
        var amp = Sch("Amp", "/ws/Amp.csch");
        var dd  = Dd("Other", "/ws/Other.cdd");

        Assert.Null(Match(dd, amp));
    }

    [Fact]
    public void Match_PicksTheNamedSchematic_AmongMany()
    {
        var a = Sch("A", "/A.csch");
        var b = Sch("B", "/B.csch");
        var c = Sch("C", "/C.csch");
        var dd = Dd("B", "/displays/B.cdd");

        Assert.Same(b, Match(dd, a, b, c));
    }

    [Fact]
    public void NoOpenSchematics_ReturnsNull()
    {
        var dd = Dd("Amp", "/ws/Amp.cdd");
        Assert.Null(Match(dd));
    }

    [Fact]
    public void ScratchDataDisplay_NoMatchingTitle_ReturnsNull()
    {
        // A scratch .cdd (no FilePath) falls back to its title; an unrelated title must not match.
        var amp = Sch("Amp", "/ws/Amp.csch");
        var dd  = Dd("Untitled-Display-1");   // no FilePath

        Assert.Null(Match(dd, amp));
    }
}
