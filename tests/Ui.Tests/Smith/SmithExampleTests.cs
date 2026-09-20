using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.Smith;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Smith;

/// <summary>
/// The shipped <c>Smith Chart</c> example, and the three defects driving it as a user turned up
/// (<c>brief-smith-11-docs-and-example.md</c> <c>R-smith11-3</c>, <c>R-smith11-4</c>).
///
/// <para><b>An example that is wrong is worse than no example</b>, because it is the first thing
/// somebody copies. So the gate is not "it opens": it opens the shipped document, evaluates it with
/// the same evaluator the window's status strip reads, and holds the answer against <b>the numbers
/// its own README quotes</b> — both directions, so a drift in either one fails here rather than in
/// front of a reader.</para>
/// </summary>
public sealed class SmithExampleTests(ITestOutputHelper output)
{
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root.");
        return dir!;
    }

    private static string ExampleDir() => Path.Combine(RepoRoot(), "examples", "Smith Chart");

    private static SmithDesign Example()
        => SmithDesignIo.LoadFromFile(Path.Combine(ExampleDir(), "Gate match.csmith"));

    // ══ 1. the example is what it says it is ════════════════════════════════

    /// <summary>
    /// The three rows of the README's own table, read out of the shipped document by the shipped
    /// evaluator — and each one's impedance found in the README as well.
    /// </summary>
    /// <remarks>
    /// <b>The tolerances are two decimal places on an ohm and three on |Γ|</b>, which is the
    /// precision the README quotes to. A looser gate would pass a design whose printed numbers were
    /// wrong in the digit somebody reads.
    /// </remarks>
    [Theory]
    [InlineData(2.30e9, 35.40,  +7.87, 0.193, 1.48,  "35.40 + j7.87")]
    [InlineData(2.45e9, 49.98,  -0.10, 0.001, 1.002, "49.98 &minus; j0.10")]
    [InlineData(2.60e9, 56.14, -23.09, 0.220, 1.56,  "56.14 &minus; j23.09")]
    public void TheShippedExampleEvaluatesToTheNumbersItsReadmeQuotes(
        double fHz, double r, double x, double gamma, double vswr, string readmeSpelling)
    {
        var reading = SmithReadings.At(Example(), fHz, ExampleDir());

        output.WriteLine($"{fHz / 1e9:0.00} GHz  Z = {reading.LoadZ.Real:0.00} {reading.LoadZ.Imaginary:+0.00;-0.00}j  "
                       + $"|G| = {reading.Gamma.Magnitude:0.0000}  VSWR = {reading.Vswr:0.000}");

        Assert.Equal(r, reading.LoadZ.Real,      0.005);
        Assert.Equal(x, reading.LoadZ.Imaginary, 0.005);
        Assert.Equal(gamma, reading.Gamma.Magnitude, 0.0005);
        Assert.Equal(vswr,  reading.Vswr,            0.005);

        // The README's own table, in its own spelling (a real minus sign, an HTML entity in the
        // Markdown). A number the document no longer produces is a number the reader is being told.
        string readme = File.ReadAllText(Path.Combine(ExampleDir(), "README.md"));
        Assert.Contains(readmeSpelling.Replace("&minus;", "−"), readme, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Copy produces a circuit, and the circuit is the one that is committed.</b>
    /// </summary>
    /// <remarks>
    /// The example ships the copied network as a real <c>.csch</c>, which is what the chapter's
    /// figure is of and what a reader runs. It is built by <see cref="SmithSchematicCopy.Build"/>
    /// rather than drawn, so this compares the two component for component — a hand edit to the
    /// committed file, or a change to the projection, and they stop being the same circuit while
    /// both still look perfectly ordinary.
    /// </remarks>
    [Fact]
    public void TheExamplesCommittedSchematicIsTheOneTheNetworkStripsCopyProduces()
    {
        var projected = SmithSchematicCopy.Build(Example(), ExampleDir());

        string csch = Path.Combine(ExampleDir(), "Matched input", "schematic", "Matched input.csch");
        var (committed, _, _) = SchematicPersistence.LoadFromFile(csch);

        Assert.Equal(projected.Components.Select(c => c.InstanceName),
                     committed.Components.Select(c => c.InstanceName));

        foreach (var (p, c) in projected.Components.Zip(committed.Components))
        {
            Assert.Equal(p.Symbol, c.Symbol);
            Assert.Equal(p.X, c.X);
            Assert.Equal(p.Y, c.Y);
            Assert.Equal(p.MirrorX, c.MirrorX);
            Assert.Equal(p.Parameters.Select(q => $"{q.Name}={q.Expression}"),
                         c.Parameters.Select(q => $"{q.Name}={q.Expression}"));
        }

        // And the analysis card, which is the ONE thing a copy deliberately does not carry: the
        // committed bench has to have grown one, or the example has nothing to run.
        Assert.Empty(projected.Analyses);
        Assert.NotEmpty(committed.Analyses);
    }

    // ══ 2. R-smith11-4 — what driving it turned up ══════════════════════════

    /// <summary>
    /// <b>A refusal spells a frequency the way the user typed it</b> — <c>2.9 GHz</c>, never
    /// <c>2.9E+09 Hz</c>.
    /// </summary>
    /// <remarks>
    /// <b>Failed at HEAD.</b> <c>SmithDesign.Fmt</c> was <c>"G6"</c>, and .NET's <c>G</c> switches to
    /// exponential the moment the decimal exponent reaches the precision — so <i>every</i> frequency
    /// this tool refuses over came out in scientific notation and in bare hertz. It is the same
    /// defect <c>MatchValueFormat.Significant</c>'s remarks record from the Match Designer's value
    /// grid one project along, which is why the fix is a call to that rather than a third spelling.
    ///
    /// <para>Asserted as an ABSENCE as well as a presence: a sentence that happened to contain both
    /// spellings would satisfy the presence half on its own.</para>
    /// </remarks>
    [Fact]
    public void AFrequencyRefusal_IsSpelledAsTheUserTypedIt_NotInScientificNotation()
    {
        var design = Example();
        design.Chart.DesignFrequencyHz = 2.9e9;

        string refusal = design.Refusal()
            ?? throw new Xunit.Sdk.XunitException("2.9 GHz is outside 2.3-2.6 GHz and was not refused.");
        output.WriteLine(refusal);

        Assert.Contains("2.9 GHz", refusal, StringComparison.Ordinal);
        Assert.Contains("2.3 GHz", refusal, StringComparison.Ordinal);
        Assert.Contains("2.6 GHz", refusal, StringComparison.Ordinal);
        Assert.False(Regex.IsMatch(refusal, @"\d[Ee][+-]\d"),
            $"The refusal is in scientific notation: {refusal}");
    }

    /// <summary>
    /// <b>And so does the message a DRAG pins with</b>, which is the one a user meets by accident
    /// rather than by typing something wrong.
    /// </summary>
    /// <remarks>
    /// <b>Failed at HEAD</b>, for the same reason and with a worse reading: an inductor pinned at its
    /// floor reported <c>L = -1.97E-09 H</c> in the status strip, beside a slider showing
    /// <c>1.97 nH</c>.
    /// </remarks>
    [Fact]
    public void ADragPinnedAtItsFloor_NamesTheValueWithItsOwnPrefix()
    {
        var vm = new SmithChartViewModel(Example())
        {
            DocumentDirectory = ExampleDir(),
            ChartCanvasSize   = (900, 700),
        };

        // Node 1 is L1's. Dragging it hard into the capacitive half asks for a negative inductance,
        // which pins at zero and reports.
        Assert.True(vm.BeginGripperDrag(1));
        vm.DragGripperTo(new System.Numerics.Complex(-0.6, -0.6));
        string pin = vm.DragPin ?? "";
        vm.EndGripperDrag(cancelled: true);

        output.WriteLine(pin);
        Assert.NotEqual("", pin);

        // Any scientific mantissa at all, not one particular exponent: at HEAD this sentence read
        // "L = -5.55285E-10 H", whose exponent is -10 rather than a single digit.
        Assert.False(Regex.IsMatch(pin, @"\d[Ee][+-]\d"),
            $"The pin message is in scientific notation: {pin}");
        Assert.Contains("pH", pin, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Every column header in the generator table sits over its own column.</b>
    /// </summary>
    /// <remarks>
    /// <b>Failed at HEAD.</b> The header grid and the row template share
    /// <c>ColumnDefinitions="86,*,*"</c>, but the headers took <c>gridhdr</c>'s left alignment while
    /// R and X are right-aligned numbers — so "X" was drawn directly above the R column's digits and
    /// "R" above nothing at all. Two ohm values one place out of step is a wrong impedance read off a
    /// table that looks entirely ordinary, and nothing reports it.
    ///
    /// <para><b>A source scan, which is this file's own fallback</b> and the one
    /// <c>SmithWindowTests</c> already documents: this project's tests do not stand up an Avalonia
    /// application, so a real arrange pass over a compiled <c>.axaml</c> is not available. What is
    /// checkable is the declaration, and the declaration is where the defect was.</para>
    /// </remarks>
    [Fact]
    public void TheGeneratorTablesHeadersTakeTheAlignmentOfTheColumnsTheyLabel()
    {
        string markup = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Ui", "Views", "Smith", "SmithChartView.axaml"));
        markup = Regex.Replace(markup, @"<!--.*?-->", "", RegexOptions.Singleline);

        foreach (string column in new[] { "R", "X" })
        {
            var m = Regex.Match(markup,
                @"<TextBlock[^>]*Classes=""gridhdr""[^>]*Text=""" + column + @"""[^>]*/>",
                RegexOptions.Singleline);

            Assert.True(m.Success, $"The generator table has no '{column}' header any more.");
            Assert.Contains("HorizontalAlignment=\"Right\"", m.Value, StringComparison.Ordinal);
        }

        // And the 'f' column is the one that stays LEFT, because its cell does.
        var freq = Regex.Match(markup,
            @"<TextBlock[^>]*Classes=""gridhdr""[^>]*Text=""f""[^>]*/>", RegexOptions.Singleline);
        Assert.True(freq.Success);
        Assert.DoesNotContain("HorizontalAlignment=\"Right\"", freq.Value, StringComparison.Ordinal);
    }

    // ══ 3. the chapter and the figures ══════════════════════════════════════

    /// <summary>
    /// Every figure the chapter cites is a row of the catalog, and every Smith row is cited. A
    /// picture nobody cites is a picture nobody regenerates for a reason.
    /// </summary>
    [Fact]
    public void TheChapterAndTheFigureCatalogNameTheSameSmithFigures()
    {
        string chapter = File.ReadAllText(
            Path.Combine(RepoRoot(), "docs", "user", "src", "reference", "smith-chart.md"));

        var cited = Regex.Matches(chapter, @"\{\{\s*ui:\s*(?<id>[a-z0-9-]+)\s*\}\}")
                         .Select(m => m.Groups["id"].Value)
                         .ToHashSet(StringComparer.Ordinal);

        var catalogued = CircuitRF.Ui.Diagnostics.FigureCatalog.Catalog
            .Select(r => r.Id)
            .Where(id => id.StartsWith("smith-", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(catalogued);
        Assert.Empty(cited.Except(catalogued, StringComparer.Ordinal).Where(
            id => id.StartsWith("smith-", StringComparison.Ordinal)));
        Assert.Empty(catalogued.Except(cited, StringComparer.Ordinal));
    }

    /// <summary>The chapter is in the reading order, which is what gives it a Previous/Next and a row
    /// on the contents page. A generated page that is not listed there fails the docs run by name —
    /// this says so a build earlier, and in one second rather than forty-five.</summary>
    [Fact]
    public void TheChapterIsInTheReadingOrder()
    {
        string nav = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "user", "src", "_nav.txt"));
        Assert.Contains("reference/smith-chart.html", nav, StringComparison.Ordinal);
    }
}
