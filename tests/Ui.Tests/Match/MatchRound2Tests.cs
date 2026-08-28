// ================================================================
//  MatchRound2Tests.cs  —  the owner's 2026-08-19 round-2 list for the Match Designer.
//
//  Same discipline as round 1: view-model, geometry and projection tests, never pixels. Where the ask
//  is about layout (a slider's height, a panel's background) the assertion is made against the AXAML
//  the layout is declared in — Ui.Tests has no live Avalonia application, so there is no control tree
//  to measure, and a source scan that names the specific mechanism is the honest substitute.
// ================================================================

using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests.Match;

public sealed class MatchRound2Tests
{
    private static (SchematicViewModel Vm, EditableComponent Comp, MatchDesignerViewModel Designer)
        Open()
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { InstanceName = "MN1", Symbol = SymbolKind.Match, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        model.Components.Add(comp);
        var vm = new SchematicViewModel(model);
        var designer = new MatchDesignerViewModel();
        designer.SetTarget(vm, comp);
        return (vm, comp, designer);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Xaml() => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "Ui", "Views", "Match", "MatchDesignerWindow.axaml"));

    // ── N may never land on unity ─────────────────────────────────────────────

    /// <summary>
    /// <b>"If slider N1 goes to 1, the plots all fail."</b> It is not a plotting bug: N = 1 is an
    /// identity transformer, and every one of the four product formulae divides by (N − 1) or
    /// multiplies by it, so the ladder acquires an infinite or a zero element and there is no response
    /// left to draw. Unity is a pole exactly like the positivity threshold, and the range excluded
    /// only the latter.
    /// </summary>
    /// <remarks>
    /// The oracle is the published formula, evaluated here rather than through <c>Apply</c> — which
    /// now clamps and therefore can no longer reach the pole. That is the point: the test has to show
    /// the pole is real, or the exclusion reads as decoration.
    /// </remarks>
    [Theory]
    [InlineData(ElementType.L)]
    [InlineData(ElementType.C)]
    public void UnityIsAPole_WhichIsWhyTheRangeExcludesIt(ElementType type)
    {
        const double z1 = 4e-9, z2 = 9e-12;
        double s1 = type == ElementType.C ? 1.0 / z1 : z1;
        double s2 = type == ElementType.C ? 1.0 / z2 : z2;

        // pi, N = 1 exactly (NortonTransform.Apply's (Pi, false) branch).
        double piFirst = 1.0 * 1.0 * s1 / (1.0 - 1.0);
        Assert.False(double.IsFinite(piFirst));

        // T, N = 1 exactly — a zero element, which for a capacitor pair inverts to infinite.
        double tLast = (1.0 - 1.0) * s2;
        Assert.Equal(0.0, tLast);
        Assert.False(double.IsFinite(1.0 / tLast));
    }

    /// <summary>
    /// The recomputed range is now an OPEN interval at both ends, so no slider position and no
    /// clamped stored N can reach unity.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheRange_NeverContainsUnity(bool allowNegative)
    {
        var (_, _, d) = Open();
        var basis = MatchSynthesis.Synthesize(d.Design);
        Assert.True(basis.Ok);

        var pairs = NortonTransform.Discover(basis.Network!);
        Assert.NotEmpty(pairs);

        foreach (var pair in pairs)
        {
            var range = NortonTransform.Range(basis.Network!, pair, basis.AnalysisIsTerm1, allowNegative);
            Assert.NotEqual(1.0, range.Min);
            Assert.NotEqual(1.0, range.Max);
            Assert.Equal(1.0, range.Clamp(1.0), 6);          // still essentially unity…
            Assert.NotEqual(1.0, range.Clamp(1.0));          // …but never exactly it
        }
    }

    /// <summary>
    /// Dragging the slider onto its unity end leaves a network the response engine can still evaluate
    /// — which is the whole of the owner's report.
    /// </summary>
    [Fact]
    public void DrivingNToUnity_StillLeavesAPlottableResponse()
    {
        var (_, _, d) = Open();
        var pairs = d.AvailablePairs();
        Assert.NotEmpty(pairs);
        d.AddTransform(pairs[0]);

        var row = d.Transforms[^1];
        row.N = 1.0;

        Assert.NotEqual(1.0, row.N);
        Assert.All(d.Elements, e => Assert.True(double.IsFinite(e.Value), $"{e.Name} is not finite"));
        Assert.Equal("", d.ResponseError);
        Assert.NotNull(d.MagnitudePlot);
        Assert.NotEmpty(d.MagnitudePlot!.Traces);
    }

    /// <summary>The slider's own bounds are those recomputed bounds, so it cannot be dragged to 1.</summary>
    [Fact]
    public void TheSliderBounds_AreTheRecomputedOnes_AndExcludeUnity()
    {
        var (_, _, d) = Open();
        var pairs = d.AvailablePairs();
        Assert.NotEmpty(pairs);
        d.AddTransform(pairs[0]);

        var row = d.Transforms[^1];
        Assert.NotEqual(1.0, row.NMin);
        Assert.NotEqual(1.0, row.NMax);
        Assert.True(row.NMin < row.NMax);
    }

    // ── The selectors ─────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Every radio selector is gone</b> (owner: "replace all radio UI selectors with the custom UI
    /// element we created for the trace card S/Z/Y selection"). Three remained after round 1 — the
    /// termination topology, its reactance kind, and the transform form — plus the network pane's own
    /// schematic/grid pair, which round 2 removes outright.
    /// </summary>
    [Fact]
    public void TheDesigner_HasNoRadioButtonsLeft()
    {
        string xaml = Xaml();
        Assert.DoesNotContain("RadioButton", xaml, StringComparison.Ordinal);
        Assert.Contains("ddc:IconSelectButton", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// The three selectors are list-driven, and each list round-trips through the design rather than
    /// through a display string held in the view-model.
    /// </summary>
    [Fact]
    public void TheSelectors_RoundTripThroughTheDesign()
    {
        var (_, _, d) = Open();
        var t = d.Term1;

        Assert.Equal(["Series", "Parallel"], MatchTerminationViewModel.TopologyOptions);
        t.TopologyChoice = "Parallel";
        Assert.Equal(TerminationTopology.Parallel, t.Topology);
        Assert.Equal("Parallel", t.TopologyChoice);
        t.TopologyChoice = "Series";
        Assert.Equal(TerminationTopology.Series, t.Topology);

        Assert.Equal(["C", "L", "–"], MatchTerminationViewModel.KindOptions);
        t.KindChoice = "L";
        Assert.Equal(ReactanceKind.L, t.Kind);
        t.KindChoice = "–";
        Assert.Equal(ReactanceKind.None, t.Kind);
        t.KindChoice = "C";
        Assert.Equal(ReactanceKind.C, t.Kind);

        Assert.Equal(["π", "T"], MatchTransformRowViewModel.FormOptions);
        var row = d.Transforms.FirstOrDefault();
        if (row is null) return;
        row.FormChoice = "T";
        Assert.Equal(TransformForm.T, row.Form);
        Assert.Equal("T", row.FormChoice);
        row.FormChoice = "π";
        Assert.Equal(TransformForm.Pi, row.Form);
    }

    /// <summary>
    /// The IconSelectButton's template lives in ONE place now — an application-scope dictionary — so
    /// the Match Designer and the Data Display's trace card cannot drift apart.
    /// </summary>
    [Fact]
    public void TheIconSelectButtonTheme_IsDeclaredExactlyOnce()
    {
        string root = RepoRoot();
        string shared = File.ReadAllText(Path.Combine(root, "src", "Ui", "Styles", "SegmentedSelect.axaml"));
        Assert.Contains("ControlTheme TargetType=\"ctl:IconSelectButton\"", shared, StringComparison.Ordinal);
        Assert.Contains("PART_Button", shared, StringComparison.Ordinal);
        Assert.Contains("PART_Popup", shared, StringComparison.Ordinal);
        Assert.Contains("PART_ListBox", shared, StringComparison.Ordinal);

        string resources = File.ReadAllText(Path.Combine(root, "src", "Ui", "Styles", "CircuitRfResources.axaml"));
        Assert.Contains("Styles/SegmentedSelect.axaml", resources, StringComparison.Ordinal);

        // The base seg-btn look moved with it, or the template's own button would be unstyled in
        // every window but the one that used to declare it.
        string styles = File.ReadAllText(Path.Combine(root, "src", "Ui", "Styles", "CircuitRfStyles.axaml"));
        Assert.Contains("Button.seg-btn", styles, StringComparison.Ordinal);

        string inspector = File.ReadAllText(Path.Combine(
            root, "src", "Ui", "Views", "DataDisplay", "PlotInspectorView.axaml"));
        Assert.DoesNotContain("ControlTheme TargetType=\"ctl:IconSelectButton\"", inspector, StringComparison.Ordinal);
    }

    // ── The panes ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The schematic and the listing are both on screen, and the selector between them is gone</b>
    /// (owner: "move the grid listing of components so that it appears in a scrollview under the
    /// schematic; remove the schematic/grid radio UI selector").
    /// </summary>
    [Fact]
    public void TheNetworkPane_ShowsTheSchematicAndTheListingTogether()
    {
        string xaml = Xaml();
        int canvas = xaml.IndexOf("mv:MatchSchematicCanvas", StringComparison.Ordinal);
        int listing = xaml.IndexOf("ItemsSource=\"{Binding Elements}\"", StringComparison.Ordinal);

        Assert.True(canvas > 0, "the network pane does not host the schematic canvas");
        Assert.True(listing > canvas, "the component listing is not below the schematic");

        // Nothing hides either half any more — the property the selector drove is gone from the
        // view-model, so a stale binding could not survive.
        Assert.DoesNotContain("ShowGrid", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowGrid", File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Ui", "Match", "MatchDesignerViewModel.Network.cs")), StringComparison.Ordinal);
    }

    /// <summary>
    /// The panel language is the Array Inductance panel's: a pane in one chrome tone, the group boxes
    /// inside it in a DIFFERENT one, both carrying the application's own tile border.
    /// </summary>
    [Fact]
    public void ThePanels_UseTheTwoToneCardLanguage()
    {
        string xaml = Xaml();

        Assert.Contains("Selector=\"Border.pane\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Selector=\"Border.card\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SystemChromeMediumLowColor", xaml, StringComparison.Ordinal);
        Assert.Contains("SystemChromeLowColor", xaml, StringComparison.Ordinal);
        Assert.Contains("CrfTileBorderBrush", xaml, StringComparison.Ordinal);

        // The row shape the two tones exist to serve.
        Assert.Contains("Classes=\"detailLabel\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Headings are sentence case</b> (owner: "all the upper case panel text SPECIFICATION, BAND,
    /// RESPONSE etc. needs to be lower case with 1st letter as a capital").
    /// </summary>
    [Fact]
    public void ThePanelHeadings_AreSentenceCase()
    {
        string xaml = Xaml();
        foreach (string shouted in new[]
        {
            "SPECIFICATION", "BAND", "RESPONSE", "ORDER", "NETWORK", "TRANSFORMS", "SOLUTIONS",
        })
            Assert.DoesNotContain($"Text=\"{shouted}\"", xaml, StringComparison.Ordinal);

        // "Band", "Response" and "Network" were RENAMED on 2026-08-20 (owner: "change the Network
        // text above the schematic to Impedance Matching Network"; "change Band in the specification
        // panel to Frequency Band; change Response below it to Filter Response"). The sentence-case
        // rule is what this test is about and it still holds of all three — the right-hand plots pane
        // is still headed "Response", so that spelling is checked as well.
        // "Filter Response" and "Order" are no longer among them: both cards were removed on
        // 2026-08-28, because the Solutions list spans every family and every order. The rule this
        // test is about — sentence case — still holds of every heading that is left.
        // "Frequency Band" and "Ripple" are ONE heading since 2026-08-28 (owner: the two groups were
        // to be merged into one of three rows), to give the Solutions list the height the second
        // card's framing was spending. The rule this test is about — sentence case — holds of the
        // merged heading exactly as it held of the two it replaces.
        foreach (string heading in new[]
        {
            "Specification", "Frequency Band &amp; Ripple",
            "Impedance Matching Network", "Transforms", "Solutions", "Response",
        })
            Assert.Contains($"Text=\"{heading}\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Both band edges are labelled</b> (owner: "needs text labels for f1 and f2, there's currently
    /// only just 'f'"), and both still write the design.
    /// </summary>
    [Fact]
    public void TheBandCard_LabelsBothEdges()
    {
        string xaml = Xaml();
        Assert.Contains("Text=\"f1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"f2\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"f \"", xaml, StringComparison.Ordinal);

        var (_, _, d) = Open();
        d.F1Entry = "1 GHz";
        d.F2Entry = "2 GHz";
        Assert.Equal(1e9, d.Design.F1, 3);
        Assert.Equal(2e9, d.Design.F2, 3);
    }

    /// <summary>
    /// <b>The unit combos beside the inline editors are gone</b> (owner: "many of the SPECIFICATION
    /// parameters that use the inline text editor no longer need the units combobox"). They were
    /// redundant the moment the fields became <c>InlineEditText</c>: the entry string CARRIES its
    /// unit, and typing one both parses and pins the display unit — which the combo then duplicated
    /// as a second way to set the same thing.
    /// </summary>
    [Fact]
    public void TheSpecificationPane_HasNoUnitCombosLeft()
    {
        string xaml = Xaml();
        foreach (string source in new[]
        {
            "MatchDesignerSettings.ResistanceUnitOptions",
            "MatchDesignerSettings.FrequencyUnitOptions",
            "Binding ReactanceUnitOptions",
        })
            Assert.DoesNotContain(source, xaml, StringComparison.Ordinal);

        // …and the entry strings still do the whole job the combos were duplicating.
        var (_, _, d) = Open();
        d.Term1.ResistanceEntry = "1.2 kΩ";
        Assert.Equal(1200.0, d.Term1.Resistance, 6);
        Assert.Equal("kΩ", d.Term1.ResistanceUnit);
    }

    /// <summary>
    /// <b>The N row is compact and its number is centred</b> (owner: "the N indicator in the
    /// TRANSFORMS panel has a very large height — perhaps the slider is messing with it. Use the
    /// compact slider style if possible (but keep the current width of the slider)"; and "text within
    /// the N textedit box needs to be aligned to the center").
    /// </summary>
    [Fact]
    public void TheTransformRow_IsCompact_AndCentresItsNumber()
    {
        string xaml = Xaml();

        Assert.Contains("Selector=\"Slider.compact\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"compact\"", xaml, StringComparison.Ordinal);

        // The height comes back by pulling the layout footprint in, NOT by setting a Height that
        // would push the thumb off the track — the Data Display's inspector already established this.
        int style = xaml.IndexOf("Selector=\"Slider.compact\"", StringComparison.Ordinal);
        int close = xaml.IndexOf("</Style>", style, StringComparison.Ordinal);
        string body = xaml[style..close];
        Assert.Contains("Margin", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Property=\"Height\"", body, StringComparison.Ordinal);

        // The slider still occupies the row's star column, so its width is unchanged.
        Assert.Contains("<Slider Grid.Column=\"3\" Classes=\"compact\"", xaml, StringComparison.Ordinal);

        // The N field is an InlineEditText now (owner, 2026-08-20: "the textedit box of the N1/N2…
        // transforms to text; allow user to change it using the inline text editor"), so the
        // centring is that control's HorizontalContentAlignment rather than a TextBox's
        // TextAlignment. The property under test — the number reads centred in its column — is
        // unchanged; only the control that carries it is.
        int n = xaml.IndexOf("Text=\"{Binding NEntry, Mode=TwoWay}\"", StringComparison.Ordinal);
        Assert.True(n > 0, "the transform row's N is not bound to NEntry");
        int rowStart = xaml.LastIndexOf("<ctl:InlineEditText Grid.Column=\"2\"", n, StringComparison.Ordinal);
        Assert.True(rowStart > 0, "N is not an InlineEditText in the row's third column");
        Assert.Contains("HorizontalContentAlignment=\"Center\"", xaml[rowStart..n],
                        StringComparison.Ordinal);
    }
}
