using System.IO;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Clipboard;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// Second ruler round (owner, 2026-08-27): readout precision, the properties dialog's read-only
/// measurements, higher-contrast defaults — and the three bugs the round turned up (a ruler-only copy
/// silently not reaching the clipboard, a flat selection refusing its own graphic export, and the DXF
/// exporter reporting its OWN Δ as a non-ASCII fidelity note about the user's drawing).
/// </summary>
public class LayoutRulerRound2Tests : System.IDisposable
{
    public LayoutRulerRound2Tests() => LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;

    public void Dispose()
    {
        LayoutTextOutline.TestOverrideTypeface = null;
        System.GC.SuppressFinalize(this);
    }

    private static readonly LayerKey Metal = new(1, 0);

    // ── Precision ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(LayoutUnit.Nm, 0)]
    [InlineData(LayoutUnit.Um, 2)]
    [InlineData(LayoutUnit.Mm, 4)]
    [InlineData(LayoutUnit.Mil, 1)]     // the owner's own stated default
    [InlineData(LayoutUnit.Inch, 4)]
    public void EachDisplayUnit_HasItsOwnSensibleDefaultPrecision(LayoutUnit unit, int expected)
        => Assert.Equal(expected, RulerAnnotation.DefaultDecimalsFor(unit));

    [Fact]
    public void TheImperialPair_AgreesByConstruction()
    {
        // 1 mil is 0.001 inch, so mil at 1 decimal and inch at 4 are the SAME physical step. If one
        // is ever retuned without the other, the same ruler reports two different precisions
        // depending only on which imperial unit the document happens to be set to.
        Assert.Equal(RulerAnnotation.DefaultDecimalsFor(LayoutUnit.Mil) + 3,
                     RulerAnnotation.DefaultDecimalsFor(LayoutUnit.Inch));
    }

    [Fact]
    public void ANullPrecision_FollowsTheDisplayUnit_SoChangingTheUnitIsStillFree()
    {
        // 25,400 DBU at 1,000 DBU/µm = 25.4 µm = 1 mil = 0.0254 mm.
        var r = new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 25_400, Y2 = 0, TextHeightDbu = 500 };
        Assert.Null(r.Decimals);

        Assert.Equal("25.4 µm", LayoutRenderer.RulerReadoutLines(r, LayoutUnit.Um, 1000)[0]);
        Assert.Equal("1 mil", LayoutRenderer.RulerReadoutLines(r, LayoutUnit.Mil, 1000)[0]);
        // mm's default is 4 precisely so this stays EXACT — at 3 it read "0.025 mm".
        Assert.Equal("0.0254 mm", LayoutRenderer.RulerReadoutLines(r, LayoutUnit.Mm, 1000)[0]);
        // …and the ceiling costs nothing when it is not needed: trailing zeros are still trimmed.
        var round = new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 3_590_000, Y2 = 0 };
        Assert.Equal("3.59 mm", LayoutRenderer.RulerReadoutLines(round, LayoutUnit.Mm, 1000)[0]);

        // §1.3: nothing stored changed.
        Assert.Null(r.Decimals);
        Assert.Equal(25_400, r.DistanceDbu);
    }

    [Fact]
    public void AnExplicitPrecision_OverridesTheUnitDefault_AndTravelsWithTheRuler()
    {
        var r = new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 25_400, Y2 = 0, Decimals = 0 };
        Assert.Equal("25 µm", LayoutRenderer.RulerReadoutLines(r, LayoutUnit.Um, 1000)[0]);
        Assert.Equal("1 mil", LayoutRenderer.RulerReadoutLines(r, LayoutUnit.Mil, 1000)[0]);

        r.Decimals = 4;
        Assert.Equal("25.4 µm", LayoutRenderer.RulerReadoutLines(r, LayoutUnit.Um, 1000)[0]);
        Assert.Equal("1 mil", LayoutRenderer.RulerReadoutLines(r, LayoutUnit.Mil, 1000)[0]);
    }

    [Fact]
    public void ThePrecision_GovernsTheComponentsToo_NotJustTheDistance()
    {
        // One measurement stated three ways would be a ruler disagreeing with itself.
        var r = new RulerAnnotation
        {
            X1 = 0, Y1 = 0, X2 = 12_345, Y2 = 6_789, ShowComponents = true, Decimals = 1,
        };
        var lines = LayoutRenderer.RulerReadoutLines(r, LayoutUnit.Um, 1000);
        Assert.Equal(2, lines.Count);
        Assert.Equal("14.1 µm", lines[0]);
        Assert.Equal("Δx 12.3  Δy 6.8", lines[1]);
    }

    [Fact]
    public void Precision_RoundTripsThroughTheClay_AndIsOmittedWhenUnset()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crf-ruler-prec-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var view = new LayoutView { DbuPerMicron = 1000 };
            view.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 0 });                 // null
            view.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 500, X2 = 1_000, Y2 = 500, Decimals = 3 });

            var path = Path.Combine(dir, "p.clay");
            LayoutPersistence.SaveToFile(path, view);

            // Additive: absent from the file for a ruler that never set one.
            string json = File.ReadAllText(path);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"Decimals\""));

            var reloaded = LayoutPersistence.LoadFromFile(path);
            Assert.Null(reloaded.Rulers[0].Decimals);
            Assert.Equal(3, reloaded.Rulers[1].Decimals);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ThePrecisionField_IsAMultiEdit_LikeEveryOtherRulerField()
    {
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Mil };
        for (int i = 0; i < 4; i++)
            model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = i * 1_000, X2 = 25_400, Y2 = i * 1_000 });

        var vm = new LayoutEditorViewModel(model);
        var panel = new LayoutShapePropertiesViewModel();
        panel.SetContext(vm);
        vm.SelectRulers(Enumerable.Range(0, 4));

        // Blank while every ruler is on the unit default; the watermark is the bare digit (the field
        // is one character wide) and the tooltip is where it is spelled out.
        Assert.Equal("", panel.RulerDecimalsText);
        Assert.Equal("1", panel.RulerDecimalsPlaceholder);
        Assert.Contains("(1 for mil)", panel.RulerDecimalsHint);

        panel.CommitRulerDecimalsText("3");
        Assert.All(model.Rulers, r => Assert.Equal(3, r.Decimals));
        vm.UndoCommand.Execute(null);
        Assert.All(model.Rulers, r => Assert.Null(r.Decimals));

        // Blank CLEARS the override back to the unit default — the only way to get back to it.
        panel.CommitRulerDecimalsText("3");
        panel.CommitRulerDecimalsText("");
        Assert.All(model.Rulers, r => Assert.Null(r.Decimals));
    }

    [Fact]
    public void AnOutOfRangePrecision_IsRefused_WithNothingWritten()
    {
        var model = new LayoutView { DbuPerMicron = 1000 };
        model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 0 });
        var vm = new LayoutEditorViewModel(model);
        var panel = new LayoutShapePropertiesViewModel();
        panel.SetContext(vm);
        vm.SelectRulers([0]);

        panel.CommitRulerDecimalsText("-1");
        Assert.NotNull(panel.RulerDecimalsError);
        Assert.Null(model.Rulers[0].Decimals);

        panel.CommitRulerDecimalsText("nonsense");
        Assert.NotNull(panel.RulerDecimalsError);
        Assert.Null(model.Rulers[0].Decimals);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    // -- Number format (owner, 2026-08-27) --------------------------------------------------------

    [Fact]
    public void GeneralIsTheDefault_AndTrimsTrailingZeros()
    {
        var r = new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 40_000, Y2 = 0 };
        Assert.Equal(LayoutUnits.LayoutNumberFormat.General, r.NumberFormat);
        Assert.Equal("40 µm", LayoutRenderer.RulerReadoutLines(r, LayoutUnit.Um, 1000)[0]);
    }

    [Fact]
    public void Fixed_KeepsTheZeros_WhichIsTheOneThingGeneralCannotDo()
    {
        var r = new RulerAnnotation
        {
            X1 = 0, Y1 = 0, X2 = 40_000, Y2 = 0,
            NumberFormat = LayoutUnits.LayoutNumberFormat.Fixed, Decimals = 2,
        };
        Assert.Equal("40.00 µm", LayoutRenderer.RulerReadoutLines(r, LayoutUnit.Um, 1000)[0]);
    }

    [Fact]
    public void TheExponentialForms_DifferOnlyInTheCaseOfTheLetter()
    {
        var upper = new RulerAnnotation
        {
            X1 = 0, Y1 = 0, X2 = 40_000, Y2 = 0,
            NumberFormat = LayoutUnits.LayoutNumberFormat.Exponential, Decimals = 2,
        };
        var lower = upper.Clone();
        lower.NumberFormat = LayoutUnits.LayoutNumberFormat.ExponentialLower;

        string up = LayoutRenderer.RulerReadoutLines(upper, LayoutUnit.Um, 1000)[0];
        string lo = LayoutRenderer.RulerReadoutLines(lower, LayoutUnit.Um, 1000)[0];

        Assert.Contains("E+", up);
        Assert.Contains("e+", lo);
        Assert.Equal(up.ToLowerInvariant(), lo.ToLowerInvariant());
    }

    [Fact]
    public void DecimalsMeansDecimalPlaces_NotDotNetsGeneralSignificantDigits()
    {
        // Passing Decimals straight through as .NET's G precision would make "one decimal place"
        // mean "one SIGNIFICANT digit", and 40 would render as "4E+01". It does not.
        var r = new RulerAnnotation
        {
            X1 = 0, Y1 = 0, X2 = 40_000, Y2 = 0,
            NumberFormat = LayoutUnits.LayoutNumberFormat.General, Decimals = 1,
        };
        Assert.Equal("40 µm", LayoutRenderer.RulerReadoutLines(r, LayoutUnit.Um, 1000)[0]);
    }

    [Fact]
    public void TheFormat_GovernsTheComponentsAndTheDxfReadout_Too()
    {
        var r = new RulerAnnotation
        {
            X1 = 0, Y1 = 0, X2 = 40_000, Y2 = 30_000, ShowComponents = true,
            SizeMode = RulerSizeMode.Scaled, TextHeightDbu = 5_000,
            NumberFormat = LayoutUnits.LayoutNumberFormat.Fixed, Decimals = 1,
        };
        var lines = LayoutRenderer.RulerReadoutLines(r, LayoutUnit.Um, 1000);
        Assert.Equal("50.0 µm", lines[0]);
        Assert.Equal("Δx 40.0  Δy 30.0", lines[1]);

        var structure = new InterchangeStructure("TOP", [], []);
        var sw = new StringWriter();
        DxfWriter.Write(sw, [structure], "TOP", null, 1000, new DxfExportOptions(), null, [r], LayoutUnit.Um);
        string dxf = sw.ToString();
        Assert.Contains("50.0 ", dxf);                          // the picture block's readout
        Assert.Contains(@"\U+0394x 40.0", dxf);                 // and the live-measurement override
    }

    [Fact]
    public void NumberFormat_RoundTrips_AndIsOmittedAtItsDefault()
    {
        var dir = Path.Combine(Path.GetTempPath(), "crf-ruler-fmt-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var view = new LayoutView { DbuPerMicron = 1000 };
            view.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 0 });   // General
            view.Rulers.Add(new RulerAnnotation
            {
                X1 = 0, Y1 = 500, X2 = 1_000, Y2 = 500,
                NumberFormat = LayoutUnits.LayoutNumberFormat.Exponential,
            });

            var path = Path.Combine(dir, "f.clay");
            LayoutPersistence.SaveToFile(path, view);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(
                File.ReadAllText(path), "\"NumberFormat\""));

            var reloaded = LayoutPersistence.LoadFromFile(path);
            Assert.Equal(LayoutUnits.LayoutNumberFormat.General, reloaded.Rulers[0].NumberFormat);
            Assert.Equal(LayoutUnits.LayoutNumberFormat.Exponential, reloaded.Rulers[1].NumberFormat);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TheFormatCombo_IsAMultiEdit_WithATriStateForMixedValues()
    {
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        for (int i = 0; i < 3; i++)
            model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = i * 1_000, X2 = 40_000, Y2 = i * 1_000 });
        model.Rulers[2].NumberFormat = LayoutUnits.LayoutNumberFormat.Fixed;

        var vm = new LayoutEditorViewModel(model);
        var panel = new LayoutShapePropertiesViewModel();
        panel.SetContext(vm);
        vm.SelectRulers([0, 1, 2]);

        Assert.Null(panel.RulerNumberFormatValue);          // they differ

        panel.RulerNumberFormatValue = LayoutUnits.LayoutNumberFormat.Fixed;
        Assert.All(model.Rulers, r => Assert.Equal(LayoutUnits.LayoutNumberFormat.Fixed, r.NumberFormat));

        vm.UndoCommand.Execute(null);
        Assert.Equal(LayoutUnits.LayoutNumberFormat.General, model.Rulers[0].NumberFormat);
        Assert.Equal(LayoutUnits.LayoutNumberFormat.Fixed, model.Rulers[2].NumberFormat);
    }

    // ── The dialog's read-only measurements ───────────────────────────────────────────────────────

    [Fact]
    public void TheDialogShowsDistanceAndBothComponents_AtTheRulersOwnPrecision()
    {
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 12_345, Y2 = 6_789, Decimals = 1 });
        var vm = new LayoutEditorViewModel(model);
        var panel = new LayoutShapePropertiesViewModel();
        panel.SetContext(vm);
        vm.SelectRulers([0]);

        Assert.Equal("14.1 µm", panel.RulerDistanceText);
        Assert.Equal("12.3 µm", panel.RulerDeltaXText);
        Assert.Equal("6.8 µm", panel.RulerDeltaYText);
    }

    [Fact]
    public void TheComponentReadouts_AreIndependentOfTheCanvasDeltaToggle()
    {
        // The toggle decides what the CANVAS paints; the inspector is where a user comes to READ them.
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 3_000, Y2 = 4_000, ShowComponents = false });
        var vm = new LayoutEditorViewModel(model);
        var panel = new LayoutShapePropertiesViewModel();
        panel.SetContext(vm);
        vm.SelectRulers([0]);

        Assert.Single(LayoutRenderer.RulerReadoutLines(model.Rulers[0], LayoutUnit.Um, 1000)); // canvas: distance only
        Assert.Equal("3 µm", panel.RulerDeltaXText);                                            // panel: both, anyway
        Assert.Equal("4 µm", panel.RulerDeltaYText);
    }

    [Fact]
    public void AcrossAMultiSelection_TheMeasurementsReadMultiple_WhenTheyDiffer()
    {
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 0 });
        model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 2_000, Y2 = 0 });
        var vm = new LayoutEditorViewModel(model);
        var panel = new LayoutShapePropertiesViewModel();
        panel.SetContext(vm);
        vm.SelectRulers([0, 1]);

        Assert.Equal("(multiple)", panel.RulerDistanceText);
        Assert.Equal("(multiple)", panel.RulerDeltaXText);
        Assert.Equal("0 µm", panel.RulerDeltaYText);   // both are horizontal — this one DOES agree
    }

    [Fact]
    public void ARulerSelection_ShowsNeitherLayerNorNet()
    {
        // R-rul-1 / §9B.11: a ruler has NO Layer field (absent, not ignored) and no Net. Showing
        // either would be a control with nothing behind it — the same trap the model avoided by
        // refusing to declare the fields.
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        // Kept well clear of the ruler so a click can land on one without the other.
        model.Shapes.Add(new RectShape { Layer = Metal, X1 = 0, Y1 = 20_000, X2 = 1_000, Y2 = 21_000 });
        model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 4_000, Y2 = 0, TextHeightDbu = 300 });

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.NoteZoomPxPerDbu(0.01);
        var panel = new LayoutShapePropertiesViewModel();
        panel.SetContext(vm);

        // A SHAPE selection still shows both — this is a ruler carve-out, not a regression.
        vm.OnPointerPressed(500, 20_500, KeyModifiers.None, 1, 40, 0.01);
        vm.OnPointerReleased(500, 20_500, KeyModifiers.None);
        Assert.Single(vm.SelectedIndices);
        Assert.True(panel.ShowLayer);
        Assert.True(panel.ShowNet);

        vm.SelectRulers([0]);
        Assert.True(panel.IsRulerContext);
        Assert.False(panel.ShowLayer);
        Assert.False(panel.ShowNet);
    }

    // ── Contrast (the owner asked for more of it, 2026-08-27) ─────────────────────────────────────

    private static double Luminance(Rgba c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    [Theory]
    [InlineData(ColorVariant.Light)]
    [InlineData(ColorVariant.Dark)]
    public void BothRulerRoles_StandWellClearOfTheirVariantsBackground(ColorVariant variant)
    {
        // The floor is 160 on a 0-255 luminance scale, and it has teeth: the ORIGINAL light line
        // (200,90,20) scored 138 against the light ground and the original dark line (255,170,80)
        // scored 149 against the dark one — both would fail this. It is what the owner's request for
        // more contrast is worth as an assertion.
        const double floor = 160.0;

        double bg = Luminance(ColorTheme.BuiltIn.Resolve(ColorRole.LayoutBackground, variant));
        foreach (string role in new[] { ColorRole.LayoutRulerAnnotationLine, ColorRole.LayoutRulerAnnotationText })
        {
            double lum = Luminance(ColorTheme.BuiltIn.Resolve(role, variant));
            Assert.True(System.Math.Abs(lum - bg) >= floor,
                        $"{role} ({variant}) is only {System.Math.Abs(lum - bg):F0} from the background.");
        }
    }

    [Fact]
    public void TheLineAndTheTextDefaultToTheSameColour()
    {
        // Owner, 2026-08-27: a ruler reads as one object, so it starts out one colour. They stay two
        // ROLES so anyone who wants them apart can pull them apart in the theme editor.
        foreach (var variant in new[] { ColorVariant.Light, ColorVariant.Dark })
            Assert.Equal(ColorTheme.BuiltIn.Resolve(ColorRole.LayoutRulerAnnotationText, variant),
                         ColorTheme.BuiltIn.Resolve(ColorRole.LayoutRulerAnnotationLine, variant));
    }

    [Fact]
    public void TheRulerGoesDarkOnLight_AndLightOnDark()
    {
        // The DIRECTION, stated separately from the magnitude — a role that was merely far from the
        // background on the wrong side would pass the test above.
        double lightBg = Luminance(ColorTheme.BuiltIn.Resolve(ColorRole.LayoutBackground, ColorVariant.Light));
        double darkBg  = Luminance(ColorTheme.BuiltIn.Resolve(ColorRole.LayoutBackground, ColorVariant.Dark));

        foreach (string role in new[] { ColorRole.LayoutRulerAnnotationLine, ColorRole.LayoutRulerAnnotationText })
        {
            Assert.True(Luminance(ColorTheme.BuiltIn.Resolve(role, ColorVariant.Light)) < lightBg);
            Assert.True(Luminance(ColorTheme.BuiltIn.Resolve(role, ColorVariant.Dark)) > darkBg);
        }
    }

    // ── Bug: a ruler-only copy never reached the clipboard ────────────────────────────────────────

    private static Technology Tech() => new()
    {
        Name = "T",
        Layers = [new LayerDef { Key = Metal, Name = "L", Color = new Rgba(0, 200, 0), Visible = true }],
    };

    [Fact]
    public void ARulerOnlySelection_ProducesACopyPayload_ThatIsNotEmptyContent()
    {
        // The guard in CopyAsync used to be "no shapes AND no instances -> return", which threw away a
        // ruler-only copy WITHOUT touching the system clipboard, so Ctrl+V pasted the PREVIOUS copy.
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Mil };
        model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 0, Y2 = 3_530_600, TextHeightDbu = 508_000 });
        var vm = new LayoutEditorViewModel(model);
        vm.SelectRulers([0]);

        var payload = vm.BuildCopyPayload();
        Assert.NotNull(payload);
        Assert.Empty(payload!.Shapes);
        Assert.Empty(payload.Instances);
        Assert.Single(payload.Rulers);

        // …and it survives the round-trip the clipboard actually performs.
        Assert.True(LayoutFragment.TryDeserialize(LayoutFragment.Serialize(payload), out var back));
        Assert.Single(back!.Rulers);
        Assert.Equal(3_530_600, back.Rulers[0].Y2);
    }

    [Theory]
    [InlineData(40_000, 0)]      // purely horizontal
    [InlineData(0, 40_000)]      // purely vertical — the owner's own file
    public void AFlatRulerOnlySelection_StillGetsAGraphicPage(long dx, long dy)
    {
        // ComputeSelectionBounds refused any selection with zero extent on an axis, which is EVERY
        // axis-aligned ruler. No PDF, no SVG, no bitmap, and nothing said about it.
        var payload = new LayoutFragment.Payload { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        payload.Rulers.Add(new RulerAnnotation
        {
            X1 = 0, Y1 = 0, X2 = dx, Y2 = dy, SizeMode = RulerSizeMode.Fixed, TextSizePt = 11,
        });

        var ctx = LayoutClipboard.MakeExportContext(payload, Tech(), LayoutRenderTheme.Light, true);

        var bounds = LayoutClipboard.SelectionBoundsForTests(ctx);
        Assert.NotNull(bounds);
        Assert.True(bounds!.Value.WorldW >= 1 && bounds.Value.WorldH >= 1);

        var svg = LayoutClipboard.TryRenderToSvg(ctx);
        Assert.NotNull(svg);
        Assert.Contains("40 µm", svg!.Value.Svg);
    }

    // ── Bug: the DXF exporter reported its OWN Δ as a user-data fidelity note ─────────────────────

    private static DxfExportSummary ExportWith(RulerAnnotation ruler, params LayoutShape[] shapes)
    {
        var structure = new InterchangeStructure("TOP", [.. shapes], []);
        return DxfWriter.Write(TextWriter.Null, [structure], "TOP", null, 1000,
                               new DxfExportOptions(), null, [ruler], LayoutUnit.Um);
    }

    [Fact]
    public void OurOwnGeneratedRulerText_IsNotReportedAsANonAsciiFidelityNote()
    {
        // Owner report, 2026-08-27: a layout containing ONE ruler and no non-ASCII character anywhere
        // exported with "1 non-ASCII text value(s) … will be escaped". It was the Δ in our own Δx/Δy
        // readout, reported back to the user as a caveat about their drawing.
        var ruler = new RulerAnnotation
        {
            X1 = 0, Y1 = 0, X2 = 0, Y2 = 40_000, SizeMode = RulerSizeMode.Scaled,
            TextHeightDbu = 5_000, ShowComponents = true,
        };
        Assert.Equal(0, ExportWith(ruler).NonAsciiTextEscaped);
    }

    [Fact]
    public void ButTheDeltaIsStillEscapedCorrectlyOnDisk()
    {
        // Not reporting it must not mean not doing it — \U+0394 is AutoCAD's own convention, and a
        // real reader renders it as Δ. Suppressing the escape would be the wrong fix.
        var structure = new InterchangeStructure("TOP", [], []);
        var ruler = new RulerAnnotation
        {
            X1 = 0, Y1 = 0, X2 = 0, Y2 = 40_000, SizeMode = RulerSizeMode.Scaled,
            TextHeightDbu = 5_000, ShowComponents = true,
        };
        var sw = new StringWriter();
        DxfWriter.Write(sw, [structure], "TOP", null, 1000, new DxfExportOptions(), null, [ruler], LayoutUnit.Um);
        Assert.Contains(@"\U+0394x", sw.ToString());
    }

    [Fact]
    public void AUserAuthoredNonAsciiCaption_IsStillReported()
    {
        // The report exists for THEIR data and must keep working — this is the half that would be
        // wrong to suppress.
        var ruler = new RulerAnnotation
        {
            X1 = 0, Y1 = 0, X2 = 40_000, Y2 = 0, SizeMode = RulerSizeMode.Scaled,
            TextHeightDbu = 5_000, Caption = "søk avstand",
        };
        Assert.Equal(1, ExportWith(ruler).NonAsciiTextEscaped);
    }

    [Fact]
    public void AUserAuthoredNonAsciiLabel_IsStillReported()
    {
        var ruler = new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 40_000, Y2 = 0, SizeMode = RulerSizeMode.Scaled, TextHeightDbu = 5_000 };
        var label = new LabelShape { Layer = Metal, X = 0, Y = 0, Text = "Ω port", Height = 2_000 };
        Assert.Equal(1, ExportWith(ruler, label).NonAsciiTextEscaped);
    }

    // ── Bug: Alt+drag showed no ghost for the copy ────────────────────────────────────────────────

    [Fact]
    public void AltDraggingARuler_ShowsAGhostOfTheCopy_AndLeavesTheOriginalPut()
    {
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        model.Rulers.Add(new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 0, TextHeightDbu = 500 });

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.NoteZoomPxPerDbu(0.01);
        vm.OnPointerPressed(5_000, 0, KeyModifiers.None, 1, 40, 0.01);
        vm.OnPointerReleased(5_000, 0, KeyModifiers.None);

        vm.OnPointerPressed(5_000, 0, KeyModifiers.None, 1, 40, 0.01);
        vm.OnPointerMoved(5_000, 4_000, leftDown: true, KeyModifiers.Alt, 40);

        var ghosts = vm.Overlay.RulerPastePreview;
        Assert.NotNull(ghosts);
        var ghost = Assert.Single(ghosts!);
        Assert.Equal(4_000, ghost.Y1);
        Assert.Equal(4_000, ghost.Y2);

        // R-dup-1: the ORIGINAL stays visibly put — no drag override, and the model is untouched.
        Assert.Null(vm.Overlay.RulerDragOverrides);
        Assert.Equal(0, model.Rulers[0].Y1);

        vm.OnPointerReleased(5_000, 4_000, KeyModifiers.Alt);
        Assert.Equal(2, model.Rulers.Count);
        Assert.Equal(0, model.Rulers[0].Y1);
        Assert.Equal(4_000, model.Rulers[1].Y1);
    }

    [Fact]
    public void APastePlacementCarryingARuler_ShowsItsGhostToo()
    {
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um };
        var vm = new LayoutEditorViewModel(model);

        vm.BeginPastePlacement([], 0, 0, null,
                               [new RulerAnnotation { X1 = 0, Y1 = 0, X2 = 4_000, Y2 = 0 }]);
        vm.OnPointerMoved(1_000, 2_000, leftDown: false, KeyModifiers.None, 40);

        var ghosts = vm.Overlay.RulerPastePreview;
        Assert.NotNull(ghosts);
        Assert.Equal(2_000, Assert.Single(ghosts!).Y1);
    }
}
