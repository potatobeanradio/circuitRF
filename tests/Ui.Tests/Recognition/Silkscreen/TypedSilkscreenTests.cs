// Designators typed as text on the silkscreen (a drawing's TEXT and MTEXT, imported as labels) name the parts beside them
// — brief-artsch-10 R-as10-4, R-as10-6, read through TypedSilkscreenEvidence.

using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.Recognition;
using Xunit;
using static CircuitRF.Ui.Tests.Recognition.RecognitionBoards;

namespace CircuitRF.Ui.Tests.Recognition.Silkscreen;

public sealed class TypedSilkscreenTests
{
    private static readonly LayerKey Silk = new(6, 0);

    private static Technology WithSilk()
    {
        var tech = TwoLayerWithMask();
        tech.Layers = [.. tech.Layers, new LayerDef { Key = Silk, Name = "Silk Top", Purpose = "silkscreen" }];
        return tech;
    }

    private static LabelShape Label(string text, double xUm, double yUm) =>
        new() { Layer = Silk, Text = text, X = Um(xUm), Y = Um(yUm), Height = Um(800) };

    /// <summary>The parts board's three land patterns, read with no placement file, take the designators typed beside them —
    /// one of them MTEXT with its inline codes — and a label that is no designator is counted, not used.</summary>
    [Fact]
    public void LabelsBesideFoundParts_NameThem_AndTheReportSaysSo()
    {
        var (view, _, _) = PartsBoard();
        double pitch = Ends(Land("0603", DensityLevel.Nominal, 0, 0)) is var (l, r) ? r - l : 0;
        view.Shapes.Add(Label("R1", 11_500, 6_200));
        view.Shapes.Add(Label(@"{\fArial|b0|i0;C2}", 21_500, 5_000 + pitch / 2));
        view.Shapes.Add(Label(@"\Lc3\l", 32_500, 6_200));
        view.Shapes.Add(Label("50R", 2_000, 8_000));

        var result = ArtworkRecognition.Recognize(new RecognitionInput { View = view, Technology = WithSilk(), Shapes = view.Shapes });

        Assert.True(result.Ok, result.Refusal);
        Assert.Equal(["C2", "C3", "R1"], result.Parts.Rows.Select(row => row.Refdes).Order());
        Assert.All(result.Parts.Rows, row => Assert.Equal(PartEvidenceSource.Silkscreen, row.Evidence[PartField.Refdes]));
        Assert.Equal(PartKind.R, result.Parts.Rows.Single(row => row.Refdes == "R1").Kind);
        var read = result.Report.Of(RecognitionFindingClass.SilkscreenLabelsRead)!;
        Assert.Equal(3, read.Count);
        Assert.Contains("1 other label there is no designator", read.Sentence);
        Assert.Null(SilkscreenTextLesson(result));
    }

    /// <summary>MTEXT's codes and toggles and TEXT's are how the text is set, not what it says.</summary>
    [Theory]
    [InlineData(@"{\fArial|b0|i0;C2}")]
    [InlineData(@"\A1;\LC2\l")]
    [InlineData(@"\OC2\o\P")]
    [InlineData(@"\H0.8x;\KC2\k")]
    [InlineData("%%uC2%%u")]
    [InlineData(@"C\~2")]
    [InlineData(@"\c16711680;C2")]
    public void FormattingCodes_AreNotTheText(string text)
    {
        var view = PlaneAndLine();
        view.Shapes.Add(Label(text, 20_000, 7_000));
        var evidence = new CircuitRF.Design.Layout.Recognition.Silkscreen.TypedSilkscreenEvidence(view.Shapes, WithSilk());
        Assert.Equal("C2", Assert.Single(evidence.Lines).Refdes);
        Assert.Empty(evidence.OtherLabels);
    }

    /// <summary>MTEXT's breaks part its lines, and each is read on its own: a designator over its value is the designator, and
    /// the value another label, where the two ran together into C210 or C247, or hid C2 in C210PF; two designators are two.</summary>
    [Theory]
    [InlineData(@"\c255;\PC2", "C2", 0)]
    [InlineData(@"\c255;C2\P10", "C2", 1)]
    [InlineData(@"{\c255;C2}\P47", "C2", 1)]
    [InlineData(@"\c255;C2\P10pF", "C2", 1)]
    [InlineData(@"C2\P\c255;10", "C2", 1)]
    [InlineData(@"C2\N10pF", "C2", 1)]
    [InlineData(@"C2\X10pF", "C2", 1)]
    [InlineData(@"\c255;R1\PR2", "R1 R2", 0)]
    public void MTextBreaks_PartItsLines_EachReadOnItsOwn(string text, string designators, int others)
    {
        var view = PlaneAndLine();
        view.Shapes.Add(Label(text, 20_000, 7_000));
        var evidence = new CircuitRF.Design.Layout.Recognition.Silkscreen.TypedSilkscreenEvidence(view.Shapes, WithSilk());
        Assert.Equal(designators, string.Join(" ", evidence.Lines.Select(l => l.Refdes)));
        Assert.Equal(others, evidence.OtherLabels.Count);
        if (evidence.Lines.Count == 2) Assert.True(evidence.Lines[1].Box.MaxY < evidence.Lines[0].Box.MaxY);   // the second line lower
    }

    /// <summary>With no part found to name, the designators are still reported — what says why the board has no parts.</summary>
    [Fact]
    public void LabelsWithNoPartToName_AreStillReported_AndListedAsNamingNone()
    {
        var view = PlaneAndLine();
        view.Shapes.Add(Label("C1", 20_000, 7_000));

        var result = ArtworkRecognition.Recognize(new RecognitionInput { View = view, Technology = WithSilk(), Shapes = view.Shapes });

        Assert.Empty(result.Parts.Rows);
        Assert.Equal(1, result.Report.Count(RecognitionFindingClass.SilkscreenLabelsRead));
        Assert.Equal(1, result.Report.Count(RecognitionFindingClass.SilkscreenRefdesNotAssociated));
    }

    /// <summary>A typed line has no glyphs, so renaming its part teaches nothing.</summary>
    private static object? SilkscreenTextLesson(RecognitionResult result)
    {
        var row = result.Parts.Rows.Single(r => r.Refdes == "R1");
        return CircuitRF.Design.Layout.Recognition.Silkscreen.SilkscreenText.Lesson(row with { Refdes = "R9", RenamedFrom = "R1" });
    }
}
