// brief-img-9-text-and-values.md R-im9-4 — designators and values attach to symbols by AS-10's assignment, one class
// at a time, and a value of the wrong kind is reported and left unattached.

using System.Linq;
using CircuitRF.Design.Layout.Recognition;
using CircuitRF.Design.Schematic.Recognition;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class LabelAssociationTests
{
    private static ImageWord Word(int id, string text, int cx, int cy) =>
        new(id, id, new PixelBox(cx - 10, cy - 7, cx + 9, cy + 6), ValueGrammar.Parse(text), text, 0.03, []);

    private static LabelTarget Cap(int symbol, int left) =>
        new(symbol, new PixelBox(left, 100, left + 20, 140), ImageSymbolKind.Capacitor, PartConfidence.High);

    /// <summary>C1 is nearer C2's capacitor than its own, and C2 nearer still: taken one at a time, both name the right
    /// one. As a whole, C1 names the left.</summary>
    [Fact]
    public void TwoCapacitorsWithInterleavedLabels_AssociateCorrectly()
    {
        var targets = new[] { Cap(0, 100), Cap(1, 140) };
        var words = new[]
        {
            Word(0, "C1", 134, 110), Word(1, "C2", 166, 110),
            Word(2, "10pF", 134, 130), Word(3, "22pF", 166, 130),
        };

        var r = LabelAssociation.Associate(targets, words);

        Assert.Equal(["C1", "C2"], r.Symbols.Select(s => s.Designator));
        Assert.Equal(["10pF", "22pF"], r.Symbols.Select(s => s.ValueWord?.Text));
        Assert.All(r.Symbols, s => Assert.False(s.Generated || s.Contradicts));
        Assert.Empty(r.Unattached);
    }

    [Fact]
    public void AnInductanceBesideACapacitor_IsAMismatch_AndNotAttached()
    {
        var r = LabelAssociation.Associate([Cap(0, 100)], [Word(0, "L3", 134, 110), Word(1, "3.3nH", 134, 130)]);

        var cap = Assert.Single(r.Symbols);
        Assert.Null(cap.ValueWord);
        Assert.Equal("3.3nH", Assert.Single(r.Mismatches).Word.Text);
        Assert.Single(r.Report.KindMismatches);
        // The designator contradicting the drawing is kept, and the drawing's confidence drops.
        Assert.Equal(("L3", true, PartConfidence.Medium), (cap.Designator, cap.Contradicts, cap.Confidence));
    }
}
