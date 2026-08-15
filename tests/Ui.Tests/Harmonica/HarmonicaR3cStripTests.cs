// ================================================================
//  HarmonicaR3cStripTests.cs  —  brief-harmonicarf-r3c-strip-columns-titles-and-axis
//
//  §1  the seven named inputs become a Settings column; an open inline editor survives a
//      published-frame refresh (§1.2 trap 1).
//  §2  the operating-point figures (Pin/Pout/Gain/DE/PAE/Pdc) become their own column.
//  §3  f0/Vds/Vgs no longer duplicated between the readouts and the inputs; the solved-Vgs
//      information the removed readout row carried survives as the input's own Placeholder.
//  §4  the Smith title rows are 85% of their previous size, and the band is rows + a named padding
//      — GammaToCanvas/CanvasToGamma must still round-trip exactly (the R1B regression stays pinned).
//  §5  no green fringe on the power-sweep plot's right (efficiency) axis.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Harmonica.Renderers;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaR3cStripTests(ITestOutputHelper output)
{
    // ── shared source-reading helper (the pattern every other Harmonica strip test already uses —
    //    Ui.Tests may not instantiate live Avalonia controls, so a structural claim about a view is
    //    pinned by scanning its own source rather than by building it) ────────────────────────────

    private static string ReadSource(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine([dir!.FullName, .. parts]);
        Assert.True(File.Exists(path), $"source not found at {path}");
        return File.ReadAllText(path);
    }

    // ══ §1/§2 — column order, gated on the panel composition rather than by eye (gate 4) ═════════

    /// <summary>
    /// R6C §1 — the owner's 2 × 4 grid replaces the old single horizontal row, and the owner's own
    /// left-to-right ORDER within a row is no longer what determines position (a Grid's children can
    /// appear in any order in markup) — so this reads each chunk's actual <c>Grid.Row</c>/
    /// <c>Grid.Column</c> attributes off the XAML rather than trusting declaration order.
    /// </summary>
    private static (int Row, int Column) GridPositionOf(string axaml, string name)
    {
        int idx = axaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(idx >= 0, $"{name} not found in ReadoutStripView.axaml");

        // The tag containing this x:Name runs from the preceding '<' to the following '/>' or '>'.
        int tagStart = axaml.LastIndexOf('<', idx);
        int tagEnd    = axaml.IndexOf('>', idx);
        string tag = axaml[tagStart..(tagEnd + 1)];

        int row = ReadIntAttribute(tag, "Grid.Row");
        int col = ReadIntAttribute(tag, "Grid.Column");
        return (row, col);
    }

    private static int ReadIntAttribute(string tag, string attribute)
    {
        int idx = tag.IndexOf($"{attribute}=\"", StringComparison.Ordinal);
        Assert.True(idx >= 0, $"{attribute} not found on tag: {tag}");
        int start = idx + attribute.Length + 2;
        int end = tag.IndexOf('"', start);
        return int.Parse(tag[start..end]);
    }

    [Fact]
    public void ColumnsGrid_PlacesTheSevenChunks_AtTheOwnersOwnPositions()
    {
        string axaml = ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml");

        // (row, column) — row 1: Settings (spans both rows) · OperatingPoint · MXP · MXE
        //                 row 2: [Settings continues]        · Terminations  · IntrinsicVDS · IntrinsicIDS
        // R-hui-1 — Settings spans down into row 2's own column-0 slot, and Source+Load merge into
        // ONE Terminations chunk at (2,2) — the owner's revised layout.
        var expected = new (string Name, int Row, int Column)[]
        {
            ("SettingsColumn",       0, 0),
            ("OperatingPointColumn", 0, 1),
            ("MxpColumn",            0, 2),
            ("MxeColumn",            0, 3),
            ("TerminationsColumn",   1, 1),
            ("IntrinsicVdsColumn",   1, 2),
            ("IntrinsicIdsColumn",   1, 3),
        };

        foreach (var (name, row, column) in expected)
        {
            var pos = GridPositionOf(axaml, name);
            Assert.True(pos == (row, column),
                $"{name} is at {pos}, expected {(row, column)}");
        }
    }

    // ══ §2 — the operating-point column exists, headed with HarmonicaTitles' own vocabulary ═══════

    private static HarmonicaViewModel NewSolvedVm()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 2, Spokes = 6, MaxGamma = 0.6 });
        Assert.Null(vm.SolveError);
        return vm;
    }

    [Fact]
    public void OperatingPointColumn_CarriesTheSixFigures_HeadedByHarmonicaTitles()
    {
        var vm = NewSolvedVm();
        var opColumn = vm.Frame.Readouts.Where(r => r.Column == ReadoutColumn.OperatingPoint).ToArray();

        Assert.NotEmpty(opColumn);
        Assert.Equal(HarmonicaTitles.CompressionLabel(vm.Model.Settings.CompressionDb), opColumn[0].Label);
        Assert.Equal("", opColumn[0].Value);   // header row — label only, exactly like MXP/MXE's own

        var byLabel = opColumn.Skip(1).ToDictionary(r => r.Label);
        // R-hui-1/R-hui-7 — Pin is gone; Gp/Zin/AM-PM joined, matching the MXP/MXE chunks' own row set.
        foreach (string expected in new[] { "Pout", "Eff", "PAE", "Gain", "Gp", "Zin", "AM/PM", "Pdc" })
            Assert.True(byLabel.ContainsKey(expected), $"OperatingPoint column is missing '{expected}'");

        output.WriteLine(string.Join(" | ", opColumn.Select(r => $"{r.Label}={r.Value}")));
    }

    // ══ §3 — f0/Vds/Vgs appear exactly once each across the WHOLE strip (gate 5) ══════════════════

    [Fact]
    public void F0VdsVgs_AppearExactlyOnce_AcrossReadoutsAndInputsCombined()
    {
        var vm = NewSolvedVm();

        // Combine every label the strip could possibly render — the readout half (General plus the
        // new columns) and the input half — and scan for a duplicate. A future addition that
        // reintroduces a "Vds" row anywhere would fail this without anyone having to remember why.
        var readoutLabels = vm.Frame.Readouts.Select(r => r.Label);
        var inputLabels   = vm.Inputs.Select(i => i.Label);
        var all = readoutLabels.Concat(inputLabels).ToArray();

        // R6C §3 renamed the frequency input's LABEL from "f₀" to "Freq:" — the KEY is what still
        // identifies it uniquely, so the duplicate scan below checks by key for that one and by label
        // for Vds/Vgs, which R6C left untouched.
        foreach (string label in new[] { "Vds", "Vgs" })
        {
            int count = all.Count(l => l == label);
            Assert.True(count == 1, $"'{label}' appears {count} times in the strip (readouts+inputs); expected 1");
        }
        Assert.Equal(1, vm.Inputs.Count(i => i.Key == HarmonicaInputs.KeyFrequency) +
                         vm.Frame.Readouts.Count(r => r.Label == "Freq:"));

        // And specifically: the READOUT half no longer carries them at all — the input is what's left.
        Assert.DoesNotContain(vm.Frame.Readouts, r => r.Label is "f₀" or "Freq:" or "Vds" or "Vgs");
        Assert.Contains(vm.Inputs, i => i.Key == HarmonicaInputs.KeyFrequency);
        Assert.Contains(vm.Inputs, i => i.Label == "Vds");
        Assert.Contains(vm.Inputs, i => i.Label == "Vgs");
    }

    // ══ owner follow-up — Idq⇄Vgs is a REAL solve now, in mA, and stays synchronized (gate 6) ═════

    [Fact]
    public void VgsAndIdq_StaySynchronized_ThroughApplyInput()
    {
        // R3C's own placeholder text ("(from Idq)") is GONE — HarmonicaContext.SolveVgsForIdq means
        // there is a real number to show now, not a stand-in for one that didn't exist.
        var vm = new HarmonicaViewModel();
        var directVgs = vm.Inputs.Single(i => i.Key == HarmonicaInputs.KeyVgs);
        Assert.Equal("", directVgs.Placeholder);
        Assert.NotEqual("", directVgs.Text);

        // Vgs-driven: the Idq row shows the LIVE current this Vgs actually draws (mA), not blank.
        var liveIdqRow = vm.Inputs.Single(i => i.Key == HarmonicaInputs.KeyIdq);
        output.WriteLine($"Vgs-driven: Vgs={directVgs.Text} V, live Idq={liveIdqRow.Text} mA");
        Assert.NotEqual("", liveIdqRow.Text);
        Assert.True(double.TryParse(liveIdqRow.Text, out _), $"'{liveIdqRow.Text}' is not a number");

        // Setting Idq (in mA) solves a real Vgs and the strip shows it on THIS SAME edit — the bug
        // being fixed here is exactly "the corresponding Vgs does not update."
        double targetMa = double.Parse(liveIdqRow.Text) * 1.2;   // a nearby, plausibly-reachable target
        Assert.True(vm.ApplyInput(HarmonicaInputs.KeyIdq, targetMa.ToString("0.###")));
        Assert.Equal(targetMa / 1000.0, vm.Model.Bias.Idq!.Value, 3);   // stored internally in AMPS

        var solvedVgs = vm.Inputs.Single(i => i.Key == HarmonicaInputs.KeyVgs);
        var idqRowAfter = vm.Inputs.Single(i => i.Key == HarmonicaInputs.KeyIdq);
        output.WriteLine($"Idq-driven: target={idqRowAfter.Text} mA, solved Vgs={solvedVgs.Text} V");
        Assert.NotEqual("", solvedVgs.Text);
        // Owner follow-up — the STRIP rounds Idq to 1 decimal place; the full target survives in the
        // MODEL (asserted above) and in the editor's own seed (EditValue), just not in this row's text.
        Assert.Equal(targetMa, double.Parse(idqRowAfter.Text, System.Globalization.CultureInfo.InvariantCulture), 1);
        Assert.Equal(targetMa.ToString("0.###"), idqRowAfter.EditValue);

        // Setting Vgs directly clears Idq — the document is Vgs-driven again, and the Idq row goes
        // back to showing a LIVE computed value rather than the old target.
        Assert.True(vm.ApplyInput(HarmonicaInputs.KeyVgs, "-1.4"));
        Assert.Null(vm.Model.Bias.Idq);
        var backToLive = vm.Inputs.Single(i => i.Key == HarmonicaInputs.KeyIdq);
        Assert.NotEqual("", backToLive.Text);
    }

    // ══ §1.2 trap 1 — an open Settings editor survives a published-frame refresh (gate 3) ═════════

    [Fact]
    public void SettingsRowMayBeOverwritten_IsAPureGuard_FalseWhileEditing()
    {
        var method = typeof(CircuitRF.Ui.Views.Harmonica.ReadoutStripView).GetMethod(
            "SettingsRowMayBeOverwritten", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
        Assert.NotNull(method);

        Assert.False((bool)method!.Invoke(null, [true])!);
        Assert.True((bool)method.Invoke(null, [false])!);
    }

    [Fact]
    public void UpdateSettingsColumnRow_SkipsTheValueSlot_WhileStateIsEditing_SourceScan()
    {
        // The production guard lives in UpdateSettingsColumnRow; pin that it actually consults the
        // same pure predicate rather than re-deriving the decision inline.
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml.cs");

        int m = src.IndexOf("private static void UpdateSettingsColumnRow(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    /// <summary>\n    /// Writes one already-built input row",
                                m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        Assert.Contains("SettingsRowMayBeOverwritten(state.IsEditing)", body, StringComparison.Ordinal);

        // BuildSettingsColumnRow's DoubleTapped handler must also consult it before opening a second
        // editor on an already-open row.
        int bm = src.IndexOf("private Grid BuildSettingsColumnRow(", StringComparison.Ordinal);
        Assert.True(bm >= 0);
        int bmEnd = src.IndexOf("\n    private static void UpdateSettingsColumnRow", bm, StringComparison.Ordinal);
        string buildBody = src[bm..bmEnd];
        // R7D §3.4 extended this guard with a Locked check (a nonlinear capacitance row's own
        // inline-edit block) — the substring below is what survives that extension.
        Assert.Contains("if (!SettingsRowMayBeOverwritten(state.IsEditing) || state.Locked) return;",
            buildBody, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsColumn_IsBuiltOnceAndUpdatedInPlace_NeverRebuiltFromScratch()
    {
        // §1.2 trap 1's actual fix: SettingsColumn must not be `.Clear()`d unconditionally the way
        // SetItems clears its own four columns every frame. Pin that SetInputs' clear is GATED on the
        // shape check, unlike Items/SourceColumn/etc in SetItems.
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml.cs");

        int m = src.IndexOf("private void UpdateSettingsColumn(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    /// <summary>Builds one Settings row", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0);
        string body = src[m..mEnd];

        // R7D §3.1 — the shape check now compares against EffectiveSettingsColumnKeys(named) (the
        // base seven, or the base seven plus Cgs/Cdg/Cds for an SDD DUT) rather than the fixed
        // 7-key SettingsColumnKeys array directly; the gate the check enforces is unchanged.
        Assert.Contains("if (SettingsColumn.Children.Count != keys.Length)", body, StringComparison.Ordinal);
        Assert.Contains("SettingsColumn.Children.Clear();", body, StringComparison.Ordinal);
    }

    // ══ §4 — Smith title rows: 85%, named constants, GammaToCanvas/CanvasToGamma still agree ══════

    [Fact]
    public void TitleConstants_AreNamed_AndTheBandIsRowsPlusPadding()
    {
        string src = ReadSource("src", "Ui", "Harmonica", "Renderers", "HarmonicaPanelRenderer.cs");

        Assert.Contains("private const double TitleSizeR3C = 0.85;", src, StringComparison.Ordinal);
        Assert.Contains("private const double TitleBottomPaddingFraction", src, StringComparison.Ordinal);

        // brief-harmonicarf-r6b §4.1 made this PUBLIC (the fly-menu dispatch needs it) — still the
        // same method, just a different modifier.
        int m = src.IndexOf("public static double TitleBandHeight(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n\n    // ── §7.2", m, StringComparison.Ordinal);
        if (mEnd < 0) mEnd = m + 800;
        string body = src[m..Math.Min(mEnd, src.Length)];

        Assert.Contains("TitleSizeR3C", body, StringComparison.Ordinal);
        Assert.Contains("double padding = m * TitleBottomPaddingFraction;", body, StringComparison.Ordinal);
        // The 7.0pt floor is untouched by the size factor — see TitleSizeR3C's own doc comment for why.
        Assert.Contains("Math.Max(7.0, m * TitleRowFontFraction * TitleFontShrink * TitleSizeR3C)", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(420.0, 340.0)]
    [InlineData(800.0, 600.0)]
    [InlineData(120.0, 100.0)]   // small panel — exercises the 7.0pt floor
    public void GammaToCanvas_CanvasToGamma_StillRoundTripExactly_AtTheNewTitleSize(double w, double h)
    {
        foreach (var g in new[]
        {
            Complex.Zero, new Complex(0.5, 0.2), new Complex(-0.3, -0.6), new Complex(0.95, 0.0),
        })
        {
            var px = HarmonicaPanelRenderer.GammaToCanvas(g, (w, h));
            var back = HarmonicaPanelRenderer.CanvasToGamma(px, (w, h));

            Assert.Equal(g.Real, back.Real, 6);
            Assert.Equal(g.Imaginary, back.Imaginary, 6);
        }
    }

    // ══ owner follow-up, 3rd report — the REAL cause was AnnulusHeadroom, not the band padding ═════

    [Fact]
    public void AnnulusHeadroom_IsZero_OwnerAcceptedTradeoff()
    {
        // The first two "fix the title height" attempts both tuned TitleBottomPaddingFraction (a few
        // px) — the gap was actually ~11% of the chart's own height, entirely from AnnulusHeadroom's
        // 20% panel-wide shrink (R-h45-4's out-of-circle-glyph headroom). Owner chose to remove it
        // (AskUserQuestion, 2026-08-13) rather than tune padding a third time. Pin the actual number,
        // not just its presence in source, so a future "restore the margin" edit is a deliberate,
        // visible choice rather than a silent revert.
        Assert.Equal(0.0, HarmonicaPanelRenderer.AnnulusHeadroom);

        // IntrinsicGlyphScale.DefaultMargin (the compression CURVE) is a separate concern and must be
        // untouched — the owner's request was about panel size, not about how far a compressed glyph's
        // position is allowed to read.
        Assert.Equal(0.25, IntrinsicGlyphScale.DefaultMargin);
    }

    [Fact]
    public void GammaToCanvas_TheGapAboveTheVisibleRim_IsNowJustChartMarginPlusPlotRenderersOwn()
    {
        // Numerically re-derive the ~63px finding that justified removing AnnulusHeadroom, at the SAME
        // representative size, and confirm the gap now reads as the owner's OWN requested ~20px
        // ChartMargin (plus a small residual from PlotRenderer's own ~1% built-in margin) rather than
        // either the old 63px or a bare-zero gap.
        (double W, double H) size = (700, 650);

        // brief-harmonicarf-r6b §4.1 made TitleBandHeight PUBLIC (the fly-menu dispatch needs it) —
        // call it directly now rather than through reflection.
        double bandH = HarmonicaPanelRenderer.TitleBandHeight(size);

        var rimTop = HarmonicaPanelRenderer.GammaToCanvas(new Complex(0, 1), size);
        double gapPx = rimTop.Y - bandH;
        double chartHeight = size.H - bandH;

        output.WriteLine($"bandH={bandH:F2}, rim top Y={rimTop.Y:F2}, gap={gapPx:F2}px " +
                         $"({gapPx / chartHeight:P1} of chart height)");

        // Was ~63px/~11% before AnnulusHeadroom was removed, ~6px with no ChartMargin at all; now
        // ChartMargin (3% of the panel's shorter side ≈ 19.5px here) plus that same small residual —
        // bounded well clear of both the old 63px regression and a bare-zero gap.
        Assert.True(gapPx is > 15.0 and < 40.0,
            $"expected roughly ChartMargin's own ~20px plus a small residual, was {gapPx:F2}px");
        Assert.True(gapPx / chartHeight < 0.10, $"expected well under the old ~11%, was {gapPx / chartHeight:P2}");
    }

    // ══ §5 — no green fringe: the cover paints match the covered paints exactly (gate 9) ══════════

    [Fact]
    public void DrawSecondaryAxisOverlay_LineAndTickPaints_AreAntialiasedWithSquareCaps()
    {
        // brief-harmonicarf-r6d §1 renamed this (and made the colour a parameter, for the time-domain
        // view's own right axis) once DrawWithSuppressedSecondaryChrome removed the underlying stroke
        // this shape-matching used to defend against — see HarmonicaPanelTests' pixel oracle for the
        // gate that actually matters now (no pixel of the ordinary axis colour survives at all).
        string src = ReadSource("src", "Ui", "Harmonica", "Renderers", "HarmonicaPanelRenderer.cs");

        int m = src.IndexOf("private static void DrawSecondaryAxisOverlay(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    /// <summary>\n    /// R-h9r2-23", m, StringComparison.Ordinal);
        Assert.True(mEnd >= 0, "could not find DrawSecondaryAxisLabel's doc comment after the overlay method");
        string body = src[m..mEnd];

        // linePaint (the border cover) and tickPaint (the tick-mark cover) must both now match
        // AxesRenderer.StrokePaint's own shape: antialiased, Square cap. Neither was true before this
        // brief (both were `IsAntialias = false` with the default Butt cap), which is exactly what let
        // the wider, softer, longer AxesRenderer stroke show as a fringe underneath.
        int lineStart = body.IndexOf("using var linePaint", StringComparison.Ordinal);
        int lineEnd   = body.IndexOf("};", lineStart, StringComparison.Ordinal);
        Assert.True(lineStart >= 0 && lineEnd > lineStart);
        string linePaintBlock = body[lineStart..lineEnd];
        Assert.Contains("IsAntialias = true", linePaintBlock, StringComparison.Ordinal);
        Assert.Contains("StrokeCap = SKStrokeCap.Square", linePaintBlock, StringComparison.Ordinal);

        int tickStart = body.IndexOf("using var tickPaint", StringComparison.Ordinal);
        int tickEnd   = body.IndexOf("};", tickStart, StringComparison.Ordinal);
        Assert.True(tickStart >= 0 && tickEnd > tickStart);
        string tickPaintBlock = body[tickStart..tickEnd];
        Assert.Contains("IsAntialias = true", tickPaintBlock, StringComparison.Ordinal);
        Assert.Contains("StrokeCap = SKStrokeCap.Square", tickPaintBlock, StringComparison.Ordinal);
    }

    // ══ owner-reported follow-ups — Escape, click-away, and no column-shift while editing ═════════

    [Fact]
    public void Constructor_WiresTunnelHandlers_ForEscapeAndClickAway_HandledEventsToo()
    {
        // A docked document's WorkspaceWindow has its own <KeyBinding Gesture="Escape" .../>, which
        // marks the event Handled before ordinary bubble routing ever reaches a focused TextBox deep
        // in this strip — the exact mechanism SchematicView.OnViewKeyDownTunnel's own comment
        // documents for its inline editor. Pin that this view registers the same defence:
        // Tunnel + handledEventsToo:true, so it still sees Escape even though it is already Handled.
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml.cs");

        int ctor = src.IndexOf("public ReadoutStripView()", StringComparison.Ordinal);
        Assert.True(ctor >= 0);
        int ctorEnd = src.IndexOf("\n    private readonly List<(TextBox Box", ctor, StringComparison.Ordinal);
        Assert.True(ctorEnd >= 0);
        string body = src[ctor..ctorEnd];

        Assert.Contains("AddHandler(KeyDownEvent, OnStripKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);",
                         body, StringComparison.Ordinal);
        Assert.Contains("AddHandler(PointerPressedEvent, OnStripPointerPressedTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);",
                         body, StringComparison.Ordinal);
    }

    [Fact]
    public void PointerPressedTunnel_CommitsEveryOpenEditor_ThePressDidNotLandInside()
    {
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml.cs");

        int m = src.IndexOf("private void OnStripPointerPressedTunnel(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    /// <summary>\n    /// §4 (R1C)", m, StringComparison.Ordinal);
        Assert.True(mEnd > m, "could not bound OnStripPointerPressedTunnel");
        string body = src[m..mEnd];

        Assert.Contains("!ReferenceEquals(source, box) && !box.IsVisualAncestorOf(source)", body, StringComparison.Ordinal);
        Assert.Contains("endEdit(true);", body, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyDownTunnel_CancelsTheFocusedOpenEditor_OnEscape()
    {
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml.cs");

        int m = src.IndexOf("private void OnStripKeyDownTunnel(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("private void OnStripPointerPressedTunnel(", m, StringComparison.Ordinal);
        Assert.True(mEnd > m);
        string body = src[m..mEnd];

        Assert.Contains("e.Key != Key.Escape", body, StringComparison.Ordinal);
        Assert.Contains("x.Box.IsFocused", body, StringComparison.Ordinal);
        Assert.Contains("focused.EndEdit(false);", body, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true;", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BeginInlineEdit_FloatsInEditorOverlay_RatherThanSplicingIntoTheRow()
    {
        // The original scheme did `pair.Children.RemoveAt(index); pair.Children.Insert(index, box);`
        // — the box's own MinWidth widened whichever row it opened on, and since a StackPanel column
        // sizes to its widest row, every column to the right visibly shifted (owner-reported). Pin
        // that BeginInlineEdit no longer touches a row's Children at all: it positions the box in
        // EditorOverlay and only ever toggles the ORIGINAL control's Opacity (which reserves its
        // layout slot instead of collapsing it).
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml.cs");

        int m = src.IndexOf("private void BeginInlineEdit(Control valueControl,", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("private static double CalcInlineEditWidth(", m, StringComparison.Ordinal);
        Assert.True(mEnd > m);
        string body = src[m..mEnd];

        Assert.Contains("EditorOverlay.Children.Add(box);", body, StringComparison.Ordinal);
        Assert.Contains("valueControl.Opacity = 0;", body, StringComparison.Ordinal);
        Assert.Contains("EditorOverlay.Children.Remove(box);", body, StringComparison.Ordinal);
        Assert.Contains("valueControl.Opacity = 1;", body, StringComparison.Ordinal);
        Assert.DoesNotContain("pair.Children", body, StringComparison.Ordinal);
    }

    // ══ R7C §1.3 — the editor's width is now an actual MEASUREMENT (FormattedText against the live
    // typeface), not the old text.Length * 0.55 formula. Ui.Tests cannot invoke FormattedText.Width
    // headlessly — confirmed directly (it throws "Unable to locate 'Avalonia.Platform.IFontManagerImpl'"
    // with no live Application/font-manager registered), which is exactly why the brief's own gate for
    // this is a screenshot, not a unit test. Pinned by source scan instead: the 0.55 formula is GONE,
    // and a real measurement (FormattedText, against the passed-in typeface) replaced it.

    [Fact]
    public void CalcInlineEditWidth_MeasuresAgainstTheTypeface_NotAnAssumedPerCharacterAdvance()
    {
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml.cs");

        int m = src.IndexOf("private static double CalcInlineEditWidth(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("\n    public static IBrush BrushFor", m, StringComparison.Ordinal);
        Assert.True(mEnd > m);
        string body = src[m..mEnd];

        Assert.Contains("Typeface typeface", body, StringComparison.Ordinal);
        Assert.Contains("new FormattedText(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("* 0.55", body, StringComparison.Ordinal);
    }

    [Fact]
    public void BeginInlineEdit_SetsWidthFromCalcInlineEditWidth_AndRecomputesOnTextChanged()
    {
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml.cs");

        int m = src.IndexOf("private void BeginInlineEdit(Control valueControl,", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("private static double CalcInlineEditWidth(", m, StringComparison.Ordinal);
        Assert.True(mEnd > m);
        string body = src[m..mEnd];

        Assert.DoesNotContain("MinWidth          =", body, StringComparison.Ordinal);
        Assert.Contains("Width             = CalcInlineEditWidth(pristine, fontSize, editTypeface),", body, StringComparison.Ordinal);
        Assert.Contains(
            "box.TextChanged += (_, _) => box.Width = CalcInlineEditWidth(box.Text ?? \"\", fontSize, editTypeface);",
            body, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorOverlay_IsTopmostAndHasNoBackground_SoEmptySpacePassesClicksThrough()
    {
        string axaml = ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml");

        int overlay = axaml.IndexOf("<Canvas x:Name=\"EditorOverlay\"", StringComparison.Ordinal);
        int columns = axaml.IndexOf("x:Name=\"Columns\"", StringComparison.Ordinal);
        Assert.True(overlay > 0 && columns > 0 && overlay > columns,
            "EditorOverlay must be declared AFTER the content StackPanel in the same Panel, so it " +
            "paints on top of every column");

        // No Background attribute at all on the tag itself — Transparent would still be hit-testable
        // across the WHOLE overlay and would block every click to the strip beneath it.
        int tagEnd = axaml.IndexOf('>', overlay);
        string tag = axaml[overlay..tagEnd];
        Assert.DoesNotContain("Background", tag, StringComparison.Ordinal);
    }

    // ══ owner request — loadline pts/FFT×/charge/M moved out of the strip into a dialog ═══════════

    [Fact]
    public void HiddenFromStripKeys_IsExactlyTheFourMovedInputs()
    {
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml.cs");

        int m = src.IndexOf("private static readonly string[] HiddenFromStripKeys", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("];", m, StringComparison.Ordinal);
        string body = src[m..mEnd];

        Assert.Contains("HarmonicaInputs.KeyLoadlineSamples", body, StringComparison.Ordinal);
        Assert.Contains("HarmonicaInputs.KeyFftOverSample", body, StringComparison.Ordinal);
        Assert.Contains("HarmonicaInputs.KeyComputeCharge", body, StringComparison.Ordinal);
        Assert.Contains("HarmonicaInputs.KeyMultiplicity", body, StringComparison.Ordinal);
    }

    [Fact]
    public void SetInputs_SkipsHiddenKeys_RatherThanRenderingThem()
    {
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "ReadoutStripView.axaml.cs");

        int m = src.IndexOf("public void SetInputs(", StringComparison.Ordinal);
        Assert.True(m >= 0);
        int mEnd = src.IndexOf("UpdateSettingsColumn(named", m, StringComparison.Ordinal);
        Assert.True(mEnd > m);
        string body = src[m..mEnd];

        Assert.Contains("Array.IndexOf(HiddenFromStripKeys, i.Key) >= 0", body, StringComparison.Ordinal);
    }

    [Fact]
    public void HarmonicaInputsBuild_StillReturnsTheFourMovedInputs_AsPlainData()
    {
        // ReadoutStripView hides them from RENDERING; HarmonicaInputs.Build itself is unchanged — the
        // Advanced Settings dialog (and any test) reads the SAME data, never a second source.
        var model = HarmonicaViewModel.DefaultModel();
        var keys = HarmonicaInputs.Build(model).Select(i => i.Key).ToHashSet();

        Assert.Contains(HarmonicaInputs.KeyLoadlineSamples, keys);
        Assert.Contains(HarmonicaInputs.KeyFftOverSample, keys);
        Assert.Contains(HarmonicaInputs.KeyComputeCharge, keys);
        Assert.Contains(HarmonicaInputs.KeyMultiplicity, keys);
    }

    [Fact]
    public void AdvancedSettingsDialog_CommitsThroughTheSameHarmonicaInputsKeys()
    {
        string src = ReadSource("src", "Ui", "Views", "Dialogs", "HarmonicaAdvancedSettingsView.axaml.cs");

        Assert.Contains("HarmonicaInputs.KeyLoadlineSamples", src, StringComparison.Ordinal);
        Assert.Contains("HarmonicaInputs.KeyFftOverSample", src, StringComparison.Ordinal);
        Assert.Contains("HarmonicaInputs.KeyComputeCharge", src, StringComparison.Ordinal);
        Assert.Contains("HarmonicaInputs.KeyMultiplicity", src, StringComparison.Ordinal);
        Assert.Contains("_vm.ApplyInput(", src, StringComparison.Ordinal);
        // Never a second write path straight into the model.
        Assert.DoesNotContain("_vm.Model =", src, StringComparison.Ordinal);
    }

    // ══ owner-reported — Edit ▸ Settings… (and Set Z0…) report instead of silently doing nothing ═

    [Theory]
    [InlineData("ShowSettingsAsync", "Settings…")]
    [InlineData("ShowSetZ0Async", "Set Z0…")]
    public void DialogHooks_ReportByName_RatherThanSilentlyReturning(string method, string label)
    {
        string src = ReadSource("src", "Ui", "Views", "Harmonica", "HarmonicaView.axaml.cs");

        int m = src.IndexOf($"private async System.Threading.Tasks.Task {method}(", StringComparison.Ordinal);
        Assert.True(m >= 0, $"could not find {method}");
        int mEnd = src.IndexOf("\n    }", m, StringComparison.Ordinal);
        Assert.True(mEnd > m);
        string body = src[m..mEnd];

        // R-h9c-10's own fix shape: a failed TopLevel resolution sets SolveError (by name) and
        // Refreshes, rather than a bare `return` a discarded exception can't help with.
        Assert.DoesNotContain("_doc is null || TopLevel.GetTopLevel(this) is not Window owner) return;",
                              body, StringComparison.Ordinal);
        Assert.DoesNotContain("Vm is not { } h || TopLevel.GetTopLevel(this) is not Window owner) return;",
                              body, StringComparison.Ordinal);
        Assert.Contains("h.SolveError = ", body, StringComparison.Ordinal);
        Assert.Contains(label, body, StringComparison.Ordinal);
    }
}
