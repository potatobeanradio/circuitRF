// Owner, 2026-09-21: a numeric field must take '.' or ',' as the decimal point, whatever the
// machine's region says.
//
// brief-localization-groundwork.md §2.3 already had this symptom on record and chose the other
// remedy — it made the REJECTION consistent (R-loc-1) rather than accepting the value:
//
//     type 4,4 into epsilon-r, focus out, the field silently reverts to its old value with no message
//
// That is what these tests now forbid. The four stackup rows named there are the fixture, because
// they are the ones the report was written about; the rule itself is pinned once in
// Core.Tests/NumericTextTests.cs and the expression half in ExpressionCultureInvarianceTests.

using System.Linq;
using CircuitRF.Ui.Layout;
using Avalonia.Controls;

namespace CircuitRF.Ui.Tests.Localization;

public class DecimalCommaInputTests
{
    private static string TempPath() => System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"comma-{System.Guid.NewGuid():N}.ctech");

    private static Technology Tech()
    {
        var t = new Technology { Name = "T", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000 };
        t.Layers.Add(new LayerDef
        { Key = new LayerKey(1, 0), Name = "M1", Color = new CircuitRF.Design.Theming.Rgba(1, 2, 3), ZOrder = 1 });
        t.Stackup.Layers.Add(new StackupLayer
        { Kind = StackupKind.Conductor, Name = "Top", ThicknessDbu = 35_000, SigmaSm = 5.8e7 });
        t.Stackup.Layers.Add(new StackupLayer
        { Kind = StackupKind.Dielectric, Name = "Core", ThicknessDbu = 1_778_000, Epsr = 4.4, TanD = 0.02, Mur = 1.0 });
        return t;
    }

    private static StackupLayerRowViewModel Row(string name)
        => new TechEditorViewModel(TempPath(), Tech()).StackupLayers.Single(r => r.Layer.Name == name);

    /// <summary>
    /// The reported failure, in the four fields it was reported in. The assertion is on the VALUE,
    /// not on the absence of an error: the old behaviour raised nothing at all, so "it did not throw"
    /// would have passed before the fix too.
    /// </summary>
    [Fact]
    public void ACommaDecimal_CommitsInTheStackupFields_InsteadOfSilentlyReverting()
    {
        var core = Row("Core");
        core.StagedEpsr = "3,66"; core.CommitEpsr();
        Assert.Equal(3.66, core.Layer.Epsr, 12);

        core.StagedTanD = "0,0021"; core.CommitTanD();
        Assert.Equal(0.0021, core.Layer.TanD, 12);

        core.StagedMur = "1,5"; core.CommitMur();
        Assert.Equal(1.5, core.Layer.Mur, 12);

        var top = Row("Top");
        top.StagedSigmaSm = "4,1e7"; top.CommitSigmaSm();
        Assert.Equal(4.1e7, top.Layer.SigmaSm, 1);
    }

    /// <summary>The point spelling is untouched — this is a second spelling, not a replacement.</summary>
    [Fact]
    public void ADecimalPoint_StillCommits()
    {
        var core = Row("Core");
        core.StagedEpsr = "3.66"; core.CommitEpsr();
        Assert.Equal(3.66, core.Layer.Epsr, 12);
    }

    /// <summary>
    /// A dimension field carries a UNIT, and the number in front of it takes either separator too —
    /// this is the path every layout, stackup and railRF length box shares
    /// (<c>LayoutUnits.TryParse</c>), not the plain-double path above.
    /// </summary>
    [Theory]
    [InlineData("1,5um",  1_500)]
    [InlineData("1.5um",  1_500)]
    [InlineData("0,5 mm", 500_000)]
    public void ADimensionFieldTakesEitherSeparator(string typed, long expectedDbu)
    {
        Assert.True(LayoutUnits.TryParse(typed, LayoutUnit.Um, LayoutUnits.DefaultDbuPerMicron, out long dbu));
        Assert.Equal(expectedDbu, dbu);
    }

    // ── Avalonia's own NumericUpDown, which parses its own text ──────────────────────────────────

    /// <summary>
    /// The control ships with <c>ParsingNumberStyle = NumberStyles.Any</c> against
    /// <c>CurrentCulture</c>, and <c>Any</c> admits a GROUP separator. Measured, not assumed:
    /// <c>double.TryParse("4,4", NumberStyles.Any, en-US)</c> returns <b>44</b>. So the failure here
    /// was never a refusal — it was a plausible wrong number, in 14 views, with nothing shown to the
    /// user.
    ///
    /// <para>Both corrections are setters on the application-scope style, which needs a styled visual
    /// tree to apply, so this is a source scan on the same fallback
    /// <c>StackupInlineEditTests</c> documents for host-requiring behaviour. What it protects is not
    /// the spelling of two setters but the fact that they are on the APPLICATION style — the sheet
    /// all three binaries include — rather than on one view that happened to be noticed.</para>
    /// </summary>
    [Fact]
    public void NumericUpDown_AdmitsNoGroupSeparatorAndDoesNotFollowTheMachine()
    {
        string styles = System.IO.File.ReadAllText(
            System.IO.Path.Combine(RepoRoot(), "src", "Ui", "Styles", "CircuitRfStyles.axaml"));

        int block = styles.IndexOf("<Style Selector=\"NumericUpDown\">", System.StringComparison.Ordinal);
        Assert.True(block >= 0, "the application-scope NumericUpDown style is gone");
        string body = styles[block..styles.IndexOf("</Style>", block, System.StringComparison.Ordinal)];

        Assert.Contains("Property=\"ParsingNumberStyle\" Value=\"Float\"", body);
        Assert.Contains("NumberFormatInfo.InvariantInfo", body);
    }

    private static string RepoRoot()
    {
        var d = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "circuitRF.slnx")))
            d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }

    /// <summary>
    /// And the comma itself: text typed into the control is rewritten to the canonical spelling
    /// before the control parses it, so <c>4,4</c> is four point four here as it is everywhere else.
    /// The rewrite terminates on its own — the result holds no comma for the rule to act on again.
    /// </summary>
    [Theory]
    [InlineData("4,4",   "4.4")]
    [InlineData("1,5e9", "1.5e9")]
    [InlineData("4.4",   "4.4")]
    public void NumericUpDown_RewritesATypedCommaToTheCanonicalSpelling(string typed, string expected)
    {
        var box = new NumericUpDown { Text = typed };
        Assert.Equal(expected, box.Text);
    }
}
