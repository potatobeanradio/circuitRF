// ================================================================
//  HarmonicaColorEditorTests.cs  —  M5's gate, brief-harmonicarf-h7
//
//  R-h7-14  the two inherited fixes are not optional — the hex-field key handling and ColorView's
//           Fluent theme. Asserted against the dialog's own AXAML and code-behind.
//  R-h7-15  colours live in the .charm, both variants.
//  R-h7-16  a colour change must not invalidate physics — extended to the EDITOR path, with a
//           negative control proving the counters can move.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Xml.Linq;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaColorEditorTests(ITestOutputHelper output)
{
    // ══ R-h7-16 — a colour change costs a render, and nothing else ══════════

    [Fact]
    public void RecolouringThroughTheEDITOR_ReRendersButDoesNotReSolveReFitOrReFactorize()
    {
        var model = HarmonicaViewModel.DefaultModel();
        var terms = new TerminationSet(model.Settings.HarmonicCount);
        terms.Set(TerminationSide.Source, 1, new Complex(25, 0));
        terms.Set(TerminationSide.Load,   1, new Complex(80, 10));

        var ctx  = HarmonicaContext.Create(model);
        var grid = new ContourGrid();
        grid.Build(ctx, terms, ContourGrid.RingGrid(2, 8));
        var fit = grid.Fit(GridMetric.PoutDbm);

        int solves  = grid.SolveCount;
        int factors = grid.FactorizationCount;
        double probe = fit.Evaluate(0.1, 0.2);
        int rebuilds = ctx.RebuildCount;

        // Twenty full recolours THROUGH THE EDITOR — not through the raw property, which is what
        // R-h45-11's own test already covered.
        var vm = new HarmonicaViewModel(model);
        int redraws = 0;
        vm.RedrawRequested += () => redraws++;

        var editor = vm.ColorEditor;
        for (int i = 0; i < 20; i++)
        {
            byte v = (byte)(i * 11);
            editor.Set(ColorRole.HarmonicaIsoline,   ColorVariant.Dark,  new Rgba(v, 255, 65));
            editor.Set(ColorRole.HarmonicaGridPoint, ColorVariant.Light, new Rgba(60, v, 90));
            editor.IsoAlphaFloor = 0.1 + i * 0.04;
        }

        output.WriteLine($"{redraws} redraws requested; solves {solves} → {grid.SolveCount}, " +
                         $"factorizations {factors} → {grid.FactorizationCount}, " +
                         $"context rebuilds {rebuilds} → {ctx.RebuildCount}");

        Assert.True(redraws >= 20, "a colour change must re-render");
        Assert.Equal(solves,  grid.SolveCount);
        Assert.Equal(factors, grid.FactorizationCount);
        Assert.Equal(rebuilds, ctx.RebuildCount);
        Assert.Equal(probe, grid.Fit(GridMetric.PoutDbm).Evaluate(0.1, 0.2), 15);

        // ── the negative control: those counters CAN move, so the assertions above are not vacuous.
        grid.Build(ctx, terms, ContourGrid.RingGrid(3, 8));
        grid.Fit(GridMetric.PoutDbm);
        Assert.True(grid.SolveCount != solves);
        Assert.True(grid.FactorizationCount > factors);
    }

    [Fact]
    public void TheRenderTheme_HasNoFieldWhoseTypeLivesInTheHarmonicaAssembly()
    {
        // R-h45-11 by construction, re-asserted because M5 gives the appearance an editor: if a
        // render-theme token ever carried a grid, a context or a scheduler, a recolour would have a
        // path to invalidate one.
        var harmonicaAssembly = typeof(ContourGrid).Assembly;
        var offenders = typeof(HarmonicaRenderTheme)
            .GetProperties()
            .Where(p => p.PropertyType.Assembly == harmonicaAssembly)
            .Select(p => $"{p.Name}: {p.PropertyType.Name}")
            .ToArray();

        Assert.True(offenders.Length == 0, string.Join(", ", offenders));
    }

    // ══ R-h7-15 — both variants, in the .charm ══════════════════════════════

    [Fact]
    public void ARecolouredRole_RoundTripsThroughTheCharm_InBothVariants()
    {
        var vm = new HarmonicaViewModel();
        vm.ColorEditor.Set(ColorRole.HarmonicaLoadline, ColorVariant.Dark,  new Rgba(1, 2, 3, 4));
        vm.ColorEditor.Set(ColorRole.HarmonicaLoadline, ColorVariant.Light, new Rgba(9, 8, 7, 6));

        var reloaded = new HarmonicaViewModel();
        reloaded.LoadCharm(vm.ToCharmJson(), baseDirectory: null);

        Assert.Equal(new Rgba(1, 2, 3, 4),
                     reloaded.ColorEditor.Resolve(ColorRole.HarmonicaLoadline, ColorVariant.Dark));
        Assert.Equal(new Rgba(9, 8, 7, 6),
                     reloaded.ColorEditor.Resolve(ColorRole.HarmonicaLoadline, ColorVariant.Light));
    }

    [Fact]
    public void ARoleTheDocumentNeverStated_ResolvesToTheBuiltInDefault()
    {
        var vm = new HarmonicaViewModel();
        foreach (string role in HarmonicaColorEditor.Roles)
            foreach (var variant in new[] { ColorVariant.Light, ColorVariant.Dark })
                Assert.Equal(ColorTheme.BuiltIn.Resolve(role, variant),
                             vm.ColorEditor.Resolve(role, variant));

        Assert.True(vm.ColorEditor.IsDefault);
    }

    // ══ §7.9.4 — .ccolor export → import ════════════════════════════════════

    [Fact]
    public void ACcolorExportedFromOneDocument_ReproducesEveryHarmonicaRoleInASecond()
    {
        var a = new HarmonicaViewModel();

        // Recolour every role, differently per variant, so a bug that dropped one variant or one
        // role could not pass.
        int n = 0;
        foreach (string role in HarmonicaColorEditor.Roles)
        {
            n++;
            a.ColorEditor.Set(role, ColorVariant.Dark,  new Rgba((byte)n, (byte)(255 - n), 7, 200));
            a.ColorEditor.Set(role, ColorVariant.Light, new Rgba(11, (byte)n, (byte)(200 - n), 255));
        }

        string ccolor = a.ColorEditor.ExportCcolor("phosphor-test");
        output.WriteLine(ccolor[..Math.Min(400, ccolor.Length)] + " …");

        var b = new HarmonicaViewModel();
        var (light, dark) = b.ColorEditor.ImportCcolor(ccolor);

        Assert.Equal(HarmonicaColorEditor.Roles.Count, light);
        Assert.Equal(HarmonicaColorEditor.Roles.Count, dark);

        foreach (string role in HarmonicaColorEditor.Roles)
        {
            Assert.Equal(a.ColorEditor.Resolve(role, ColorVariant.Dark),
                         b.ColorEditor.Resolve(role, ColorVariant.Dark));
            Assert.Equal(a.ColorEditor.Resolve(role, ColorVariant.Light),
                         b.ColorEditor.Resolve(role, ColorVariant.Light));
        }
    }

    [Fact]
    public void AnExportedCcolor_CarriesOnlyHarmonicaRoles_SoImportingItDoesNotOverwriteTheAppTheme()
    {
        var vm = new HarmonicaViewModel();
        string ccolor = vm.ColorEditor.ExportCcolor();
        var theme = ColorThemeIo.Load(ccolor);
        var (light, dark) = theme.GetRoleMaps();

        Assert.All(light.Keys, k => Assert.StartsWith("Harmonica.", k, StringComparison.Ordinal));
        Assert.All(dark.Keys,  k => Assert.StartsWith("Harmonica.", k, StringComparison.Ordinal));
        Assert.DoesNotContain(ColorRole.SchematicBackground, light.Keys);
    }

    [Fact]
    public void ImportingACcolorWithNoHarmonicaRoles_ChangesNothing()
    {
        var vm = new HarmonicaViewModel();
        vm.ColorEditor.Set(ColorRole.HarmonicaIsoline, ColorVariant.Dark, new Rgba(5, 5, 5));

        // The schematic's own palette — a perfectly valid .ccolor that says nothing about harmonicaRF.
        string foreign = ColorThemeIo.Save(new ColorTheme("schematic-only",
            new Dictionary<string, Rgba> { [ColorRole.SchematicBackground] = new(1, 1, 1) },
            new Dictionary<string, Rgba> { [ColorRole.SchematicBackground] = new(2, 2, 2) }));

        var (light, dark) = vm.ColorEditor.ImportCcolor(foreign);

        Assert.Equal(0, light);
        Assert.Equal(0, dark);
        Assert.Equal(new Rgba(5, 5, 5), vm.ColorEditor.Resolve(ColorRole.HarmonicaIsoline, ColorVariant.Dark));
    }

    // ══ §7.9.4 — reset-all and per-role revert ══════════════════════════════

    [Fact]
    public void ResetAll_LandsOnTheBuiltInValuesForBothVariants()
    {
        var vm = new HarmonicaViewModel();
        foreach (string role in HarmonicaColorEditor.Roles)
        {
            vm.ColorEditor.Set(role, ColorVariant.Dark,  new Rgba(1, 1, 1));
            vm.ColorEditor.Set(role, ColorVariant.Light, new Rgba(2, 2, 2));
        }
        Assert.False(vm.ColorEditor.IsDefault);

        vm.ColorEditor.ResetAllColours();

        foreach (string role in HarmonicaColorEditor.Roles)
            foreach (var variant in new[] { ColorVariant.Light, ColorVariant.Dark })
                Assert.Equal(ColorTheme.BuiltIn.Resolve(role, variant),
                             vm.ColorEditor.Resolve(role, variant));
    }

    [Fact]
    public void PerRoleRevert_TouchesThatRoleOnlyAndBothItsVariants()
    {
        var vm = new HarmonicaViewModel();
        vm.ColorEditor.Set(ColorRole.HarmonicaIsoline,  ColorVariant.Dark,  new Rgba(1, 1, 1));
        vm.ColorEditor.Set(ColorRole.HarmonicaIsoline,  ColorVariant.Light, new Rgba(2, 2, 2));
        vm.ColorEditor.Set(ColorRole.HarmonicaLoadline, ColorVariant.Dark,  new Rgba(3, 3, 3));

        vm.ColorEditor.Revert(ColorRole.HarmonicaIsoline);

        Assert.Equal(ColorTheme.BuiltIn.Resolve(ColorRole.HarmonicaIsoline, ColorVariant.Dark),
                     vm.ColorEditor.Resolve(ColorRole.HarmonicaIsoline, ColorVariant.Dark));
        Assert.Equal(ColorTheme.BuiltIn.Resolve(ColorRole.HarmonicaIsoline, ColorVariant.Light),
                     vm.ColorEditor.Resolve(ColorRole.HarmonicaIsoline, ColorVariant.Light));

        // The OTHER role is untouched — "undo one role" is what §7.9.4 asks for.
        Assert.Equal(new Rgba(3, 3, 3),
                     vm.ColorEditor.Resolve(ColorRole.HarmonicaLoadline, ColorVariant.Dark));
    }

    [Fact]
    public void ResetAllColours_LeavesTheFadeParametersAlone()
    {
        var vm = new HarmonicaViewModel();
        vm.ColorEditor.IsoAlphaFloor = 1.0;
        vm.ColorEditor.Set(ColorRole.HarmonicaIsoline, ColorVariant.Dark, new Rgba(1, 1, 1));

        vm.ColorEditor.ResetAllColours();

        Assert.Equal(1.0, vm.ColorEditor.IsoAlphaFloor, 9);
        Assert.Equal(ColorTheme.BuiltIn.Resolve(ColorRole.HarmonicaIsoline, ColorVariant.Dark),
                     vm.ColorEditor.Resolve(ColorRole.HarmonicaIsoline, ColorVariant.Dark));
    }

    // ══ §7.2 — α_floor = 1 flattens the fade, through the EDITOR ════════════

    [Fact]
    public void AlphaFloorOfOne_FlattensTheIsoLineFade_ReachedThroughTheEditor()
    {
        var vm = new HarmonicaViewModel();

        // Default: a ramp, with the top level exactly opaque and the lowest well below it.
        var before = IsoLineAlphaRamp.ForLevels(10, vm.RenderTheme.IsoAlphaFloor,
                                                    vm.RenderTheme.IsoAlphaExponent);
        output.WriteLine("default ramp: " + string.Join(", ", before.Select(a => a.ToString("F3"))));
        Assert.Equal(1.0, before[^1], 12);
        Assert.True(before[0] < 0.5);

        vm.ColorEditor.IsoAlphaFloor = 1.0;

        var after = IsoLineAlphaRamp.ForLevels(10, vm.RenderTheme.IsoAlphaFloor,
                                                   vm.RenderTheme.IsoAlphaExponent);
        output.WriteLine("flattened:    " + string.Join(", ", after.Select(a => a.ToString("F3"))));
        Assert.All(after, a => Assert.Equal(1.0, a, 12));
    }

    // ══ R-h7-14 — the two inherited fixes ═══════════════════════════════════

    private static string RepoFile(string relative)
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, relative)))
            dir = Path.GetDirectoryName(dir) ?? "";
        Assert.True(dir.Length > 0, $"could not locate {relative}");
        return File.ReadAllText(Path.Combine(dir, relative));
    }

    [Fact]
    public void TheHexField_AppliesOnReturnAndHandlesIt_RevertsOnEscape_AndAppliesOnLostFocus()
    {
        string code = RepoFile("src/Ui/Views/Dialogs/HarmonicaPreferencesDialog.axaml.cs");

        // Return applies AND sets e.Handled — or the window's default button closes the dialog
        // instead of applying, which is the defect SettingsView already absorbed.
        Assert.Contains("Key.Return", code, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true", code, StringComparison.Ordinal);
        Assert.Contains("Key.Escape", code, StringComparison.Ordinal);
        Assert.Contains("OnHexLostFocus", code, StringComparison.Ordinal);

        // RRGGBBAA, with a six-digit entry taken as opaque.
        Assert.Contains("if (txt.Length == 6) txt += \"FF\";", code, StringComparison.Ordinal);

        string axaml = RepoFile("src/Ui/Views/Dialogs/HarmonicaPreferencesDialog.axaml");
        Assert.Contains("LostFocus=\"OnHexLostFocus\"", axaml, StringComparison.Ordinal);
        Assert.Contains("KeyDown=\"OnHexKeyDown\"", axaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TheColourPicker_IsCircuitRfsOwnDialog_WhichAlreadyCarriesColorViewsFluentTheme()
    {
        string code = RepoFile("src/Ui/Views/Dialogs/HarmonicaPreferencesDialog.axaml.cs");
        Assert.Contains("new ColorPickerDialog(", code, StringComparison.Ordinal);

        // …and the app really does include the theme ColorView needs, or it instantiates blank.
        // Note the resource name ends .xaml, NOT .axaml — that is the actual embedded name.
        // Declared in the SHARED style file since H8, deliberately: there are two Applications now
        // and this is the include whose absence fails SILENTLY (§7.9.4 — ColorView renders as an
        // empty box with no error), so both must carry it and one file is how that is guaranteed.
        string styles = RepoFile("src/Ui/Styles/CircuitRfStyles.axaml");
        Assert.Contains("Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml", styles,
                        StringComparison.Ordinal);
    }

    [Fact]
    public void ThePreferencesDialog_OffersImportExportResetAndTheFadeParameters()
    {
        var axaml = XDocument.Parse(RepoFile("src/Ui/Views/Dialogs/HarmonicaPreferencesDialog.axaml"));
        var names = axaml.Descendants()
            .Select(e => (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")))
            .Where(n => n is { Length: > 0 })
            .ToArray();

        output.WriteLine(string.Join(", ", names));
        Assert.Contains("ImportButton",     names);
        Assert.Contains("ExportButton",     names);
        Assert.Contains("ResetAllButton",   names);
        Assert.Contains("RevertButton",     names);
        Assert.Contains("AlphaFloorSlider", names);
        Assert.Contains("AlphaExpSlider",   names);
        Assert.Contains("IsoLabelsCheck",   names);
        Assert.Contains("LightRadio",       names);
        Assert.Contains("DarkRadio",        names);
    }
}
