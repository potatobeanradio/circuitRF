// ================================================================
//  HarmonicaMenuAndInputTests.cs  —  M1's gate, brief-harmonicarf-h7
//
//  R-h7-1  every §7.6 menu on BOTH surfaces, and NO Simulate menu on either.
//  R-h7-2  the Markers menu creates the marker AND marks the band; band 1 refuses removal.
//  R-h7-3  a frequency change rebuilds the context once and resets the ladder; a bias change
//          rebuilds it ZERO times. Counters, not clocks.
//  R-h7-4  the input list is READ from the model, so two models produce two lists.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaMenuAndInputTests(ITestOutputHelper output)
{
    // ══ R-h7-1 — the menu set, on both hand-mirrored surfaces ════════════════

    private const string MenuAxaml = "src/Ui/Views/Harmonica/HarmonicaMenuView.axaml";

    private static XDocument LoadMenuAxaml()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, MenuAxaml)))
            dir = Path.GetDirectoryName(dir) ?? "";
        Assert.True(dir.Length > 0, $"could not locate {MenuAxaml} from {AppContext.BaseDirectory}");
        return XDocument.Load(Path.Combine(dir, MenuAxaml));
    }

    /// <summary>§7.6's six menus, in the order the design note lists them.</summary>
    private static readonly string[] ExpectedMenus =
        ["File", "Edit", "Markers", "Display", "Grid", "Help"];

    [Fact]
    public void EverySection76Menu_ExistsOnBothSurfaces_InTheSameOrder()
    {
        var doc = LoadMenuAxaml();

        // Surface 1 — the macOS NativeMenu. Top level only: an item whose PARENT is the root
        // NativeMenu, which is itself the child of NativeMenu.Menu.
        var nativeRoot = doc.Descendants()
            .First(e => e.Name.LocalName == "NativeMenu.Menu")
            .Elements().First(e => e.Name.LocalName == "NativeMenu");

        var native = nativeRoot.Elements()
            .Where(e => e.Name.LocalName == "NativeMenuItem")
            .Select(e => (string?)e.Attribute("Header"))
            .ToArray();

        // Surface 2 — the in-window Menu. Accelerator underscores stripped for the comparison.
        var inWindow = doc.Descendants()
            .First(e => e.Name.LocalName == "Menu")
            .Elements().Where(e => e.Name.LocalName == "MenuItem")
            .Select(e => ((string?)e.Attribute("Header"))?.Replace("_", ""))
            .ToArray();

        output.WriteLine("native   : " + string.Join(", ", native));
        output.WriteLine("in-window: " + string.Join(", ", inWindow));

        Assert.Equal(ExpectedMenus, native);
        Assert.Equal(ExpectedMenus, inWindow);
    }

    [Fact]
    public void NeitherSurface_CarriesASimulateMenu_BecauseHarmonicaRfIsAlwaysSimulating()
    {
        var doc = LoadMenuAxaml();

        // Asserted over EVERY header at every depth, not only the top level: a Simulate item buried
        // in a submenu would be the same lie about what the tool does.
        var offenders = doc.Descendants()
            .Where(e => e.Name.LocalName is "NativeMenuItem" or "MenuItem")
            .Select(e => ((string?)e.Attribute("Header"))?.Replace("_", "") ?? "")
            .Where(h => h.Contains("Simulate", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "harmonicaRF is always simulating; a Simulate menu would be a lie about what the tool " +
            "does. Found: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryMenuCommand_ExistsOnBothSurfaces_SoNeitherPlatformIsMissingAnAction()
    {
        var doc = LoadMenuAxaml();

        static SortedSet<string> CommandsUnder(XElement root, string itemName) =>
            [.. root.Descendants()
                    .Where(e => e.Name.LocalName == itemName)
                    .Select(e => (string?)e.Attribute("Command"))
                    .Where(c => c is { Length: > 0 })
                    .Select(c => c!)];

        var nativeRoot = doc.Descendants().First(e => e.Name.LocalName == "NativeMenu.Menu");
        var menuRoot   = doc.Descendants().First(e => e.Name.LocalName == "Menu");

        var native   = CommandsUnder(nativeRoot, "NativeMenuItem");
        var inWindow = CommandsUnder(menuRoot,   "MenuItem");

        output.WriteLine($"native {native.Count} commands, in-window {inWindow.Count}");
        var onlyNative   = native.Except(inWindow).ToArray();
        var onlyInWindow = inWindow.Except(native).ToArray();

        Assert.True(onlyNative.Length == 0,
            "on the macOS surface only: " + string.Join(", ", onlyNative));
        Assert.True(onlyInWindow.Length == 0,
            "on the in-window surface only: " + string.Join(", ", onlyInWindow));
        Assert.True(native.Count >= 20, $"only {native.Count} commands — the menu set looks truncated");
    }

    // ══ R-h7-2 — the Markers menu creates and removes bands ══════════════════

    [Fact]
    public void AddingAnL2Marker_CreatesTheMarkerAndMarksTheBand_ThroughOneCall()
    {
        var vm = new HarmonicaViewModel();
        var menus = new HarmonicaMenuViewModel(vm);

        Assert.False(vm.Terminations.IsMarked(TerminationSide.Load, 2));
        Assert.DoesNotContain(vm.Markers, m => m is { Side: TerminationSideKind.Load, Band: 2 });

        var band2 = menus.LoadBands.Single(b => b.Band == 2);
        band2.IsPresent = true;                       // exactly what clicking the menu item does

        Assert.Contains(vm.Markers, m => m is { Side: TerminationSideKind.Load, Band: 2 });
        Assert.True(vm.Terminations.IsMarked(TerminationSide.Load, 2),
            "the marker exists but the band is unmarked — two sources for 'what is band 2 " +
            "terminated in' have already drifted");
    }

    [Fact]
    public void RemovingAMarker_LeavesTheBandUNMARKED_NotResetToADefaultValue()
    {
        var vm = new HarmonicaViewModel();
        var menus = new HarmonicaMenuViewModel(vm);

        var band2 = menus.LoadBands.Single(b => b.Band == 2);
        band2.IsPresent = true;
        vm.SetMarkerImpedance(vm.Markers.Single(m => m is { Side: TerminationSideKind.Load, Band: 2 }),
                              new System.Numerics.Complex(12, -30));
        Assert.True(vm.Terminations.IsMarked(TerminationSide.Load, 2));

        band2.IsPresent = false;

        Assert.DoesNotContain(vm.Markers, m => m is { Side: TerminationSideKind.Load, Band: 2 });
        Assert.False(vm.Terminations.IsMarked(TerminationSide.Load, 2),
            "§4.2 — an unmarked band is the ABSENCE of a marker, not a marker with a default value. " +
            "TerminationSet.Remove is what expresses that, and it was not called.");

        // …and the unmarked value is D9's near-short, which is what the engine will now see.
        Assert.Equal(TerminationSet.UnmarkedBandOhms,
                     vm.Terminations.Z(TerminationSide.Load, 2).Real, 12);
    }

    [Theory]
    [InlineData(TerminationSideKind.Source)]
    [InlineData(TerminationSideKind.Load)]
    public void Band1_RefusesRemoval_OnBothSides(TerminationSideKind side)
    {
        var vm = new HarmonicaViewModel();
        var menus = new HarmonicaMenuViewModel(vm);

        var bands = side == TerminationSideKind.Source ? menus.SourceBands : menus.LoadBands;
        var band1 = bands.Single(b => b.Band == 1);

        Assert.False(band1.CanRemove, "the fundamental's menu entry must be disabled");

        band1.IsPresent = false;                       // a determined caller, or a keyboard route

        Assert.Contains(vm.Markers, m => m.Side == side && m.Band == 1);
        Assert.True(band1.IsPresent, "the checkmark must be put back rather than left disagreeing " +
                                     "with the model");
    }

    [Fact]
    public void ResetMarkers_ReturnsToS1AndL1Alone()
    {
        var vm = new HarmonicaViewModel();
        var menus = new HarmonicaMenuViewModel(vm);

        menus.LoadBands.Single(b => b.Band == 2).IsPresent = true;
        menus.SourceBands.Single(b => b.Band == 3).IsPresent = true;
        Assert.Equal(4, vm.Markers.Count);

        menus.ResetMarkersCommand.Execute(null);

        Assert.Equal(2, vm.Markers.Count);
        Assert.All(vm.Markers, m => Assert.Equal(1, m.Band));
        Assert.False(vm.Terminations.IsMarked(TerminationSide.Load, 2));
    }

    // ══ R-h7-3 — value vs structural, decided by StructuralKey ═══════════════

    [Fact]
    public void ChangingTheFrequency_RebuildsTheContextExactlyOnce_AndResetsTheLadder()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });
        int before = vm.ContextRebuildCount;
        int resets = vm.ScheduleResetCount;

        Assert.True(vm.ApplyInput(HarmonicaInputs.KeyFrequency, "2.4"));
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });

        output.WriteLine($"rebuilds {before} → {vm.ContextRebuildCount}, ladder resets {resets} → {vm.ScheduleResetCount}");
        Assert.Equal(before + 1, vm.ContextRebuildCount);
        Assert.Equal(resets + 1, vm.ScheduleResetCount);
        Assert.Equal(2.4e9, vm.Model.Settings.FrequencyHz, 3);
    }

    [Fact]
    public void ChangingTheBias_RebuildsTheContextZeroTimes()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });
        int before = vm.ContextRebuildCount;
        int resets = vm.ScheduleResetCount;

        Assert.True(vm.ApplyInput(HarmonicaInputs.KeyVds, "40"));
        Assert.True(vm.ApplyInput(HarmonicaInputs.KeyVgs, "-3.2"));
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });

        output.WriteLine($"rebuilds {before} → {vm.ContextRebuildCount}");
        Assert.Equal(before, vm.ContextRebuildCount);
        Assert.Equal(resets, vm.ScheduleResetCount);
        Assert.Equal(40.0, vm.Model.Bias.Vds, 9);
    }

    [Fact]
    public void TheStructuralFlag_IsMeasuredFromStructuralKey_NotFromATable()
    {
        var model = HarmonicaViewModel.DefaultModel();
        var inputs = HarmonicaInputs.Build(model).ToDictionary(i => i.Key);

        // §6.1's own split, restated: the DUT, the embedding stack, K, the frequency, FftOverSample
        // and ComputeCharge rebuild; bias and drive do not.
        Assert.True(inputs[HarmonicaInputs.KeyFrequency].Structural);
        Assert.True(inputs[HarmonicaInputs.KeyHarmonicCount].Structural);
        Assert.True(inputs[HarmonicaInputs.KeyFftOverSample].Structural);
        Assert.True(inputs[HarmonicaInputs.KeyComputeCharge].Structural);
        Assert.True(inputs[HarmonicaInputs.KeyMultiplicity].Structural);

        Assert.False(inputs[HarmonicaInputs.KeyVgs].Structural);
        Assert.False(inputs[HarmonicaInputs.KeyVds].Structural);
        Assert.False(inputs[HarmonicaInputs.KeyIdq].Structural);
        Assert.False(inputs[HarmonicaInputs.KeyCompression].Structural);

        // The claim is not a table: prove it by APPLYING each and comparing the key directly.
        foreach (var input in inputs.Values)
        {
            var probed = HarmonicaInputs.Apply(model, input.Key, ProbeFor(model, input.Key), out var err);
            if (err is not null || probed is null) continue;
            bool moved = probed.StructuralKey != model.StructuralKey;
            Assert.True(moved == input.Structural,
                $"{input.Key}: declared Structural={input.Structural} but the key {(moved ? "moved" : "did not move")}");
        }

        static string ProbeFor(CircuitModel m, string key) => key switch
        {
            HarmonicaInputs.KeyComputeCharge => m.Settings.ComputeCharge ? "0" : "1",
            HarmonicaInputs.KeyHarmonicCount => (m.Settings.HarmonicCount + 1).ToString(),
            HarmonicaInputs.KeyFftOverSample => (m.Settings.FftOverSample + 1).ToString(),
            HarmonicaInputs.KeyFrequency     => "3.7",
            HarmonicaInputs.KeyCompression   => "4.5",
            HarmonicaInputs.KeyVgs           => "-2.11",
            HarmonicaInputs.KeyIdq           => "0.37",
            HarmonicaInputs.KeyVds           => "31",
            HarmonicaInputs.KeyMultiplicity  => "3",
            _ when key.StartsWith(HarmonicaInputs.ParameterPrefix, StringComparison.Ordinal) => "1.234",
            _ => "",
        };
    }

    [Fact]
    public void RaisingK_KeepsTheBandsThatStillFitAndDropsTheRest()
    {
        var vm = new HarmonicaViewModel();
        vm.AddMarkerBand(TerminationSideKind.Load, 3);
        vm.SetMarkerImpedance(vm.Markers.Single(m => m is { Side: TerminationSideKind.Load, Band: 3 }),
                              new System.Numerics.Complex(7, 11));

        Assert.True(vm.ApplyInput(HarmonicaInputs.KeyHarmonicCount, "5"));
        Assert.Equal(5, vm.Terminations.HarmonicCount);
        Assert.True(vm.Terminations.IsMarked(TerminationSide.Load, 3));
        Assert.Equal(7.0, vm.Terminations.Z(TerminationSide.Load, 3).Real, 9);

        // …and lowering it below a marked band drops that band with its marker, rather than clamping
        // two markers onto one band, which the file format cannot express.
        Assert.True(vm.ApplyInput(HarmonicaInputs.KeyHarmonicCount, "2"));
        Assert.Equal(2, vm.Terminations.HarmonicCount);
        Assert.DoesNotContain(vm.Markers, m => m.Band > 2);
    }

    [Fact]
    public void ARejectedInput_LeavesTheModelAloneAndSaysWhy()
    {
        var vm = new HarmonicaViewModel();
        double before = vm.Model.Settings.FrequencyHz;

        Assert.False(vm.ApplyInput(HarmonicaInputs.KeyFrequency, "not a number"));
        Assert.Equal(before, vm.Model.Settings.FrequencyHz, 3);
        Assert.False(string.IsNullOrWhiteSpace(vm.InputError));
        output.WriteLine(vm.InputError);
    }

    // ══ R-h7-4 — the model's OWN parameters, never a faked list ══════════════

    [Fact]
    public void TwoModelsWithDifferentParameterSets_ProduceDifferentInputLists()
    {
        var sdd = HarmonicaViewModel.DefaultModel();                       // an SDD: its equations
        var fet = sdd with
        {
            Dut = new DutSpec
            {
                Kind = DutKind.NativeFet, TypeName = "FET_Angelov",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal),
            },
        };

        var sddNames = HarmonicaInputs.DeclaredModelParameters(sdd).Select(i => i.Label).ToArray();
        var fetNames = HarmonicaInputs.DeclaredModelParameters(fet).Select(i => i.Label).ToArray();

        output.WriteLine("SDD: " + string.Join(", ", sddNames));
        output.WriteLine("FET_Angelov: " + string.Join(", ", fetNames));

        // An SDD's parameters ARE its equations, keyed as the .cnl spells them.
        Assert.Contains("I[2,0]", sddNames);
        Assert.DoesNotContain("Ipk", sddNames);

        // The Angelov law's own set — read from the registry the schematic editor renders, not from
        // a list written here.
        Assert.Contains("Ipk", fetNames);
        Assert.Contains("Vpk", fetNames);
        Assert.DoesNotContain("I[2,0]", fetNames);

        Assert.NotEqual(sddNames, fetNames);
    }

    [Fact]
    public void TwoDIFFERENTFetLaws_DeclareDifferentParameters_NotOneSharedBlock()
    {
        static string[] Names(string typeName)
        {
            var m = HarmonicaViewModel.DefaultModel() with
            {
                Dut = new DutSpec
                {
                    Kind = DutKind.NativeFet, TypeName = typeName,
                    Parameters = new Dictionary<string, string>(StringComparer.Ordinal),
                },
            };
            return [.. HarmonicaInputs.DeclaredModelParameters(m).Select(i => i.Label)];
        }

        var angelov = Names("FET_Angelov");
        var materka = Names("FET_Materka");

        output.WriteLine("Angelov: " + string.Join(", ", angelov));
        output.WriteLine("Materka: " + string.Join(", ", materka));

        Assert.Contains("Ipk",  angelov);
        Assert.Contains("Idss", materka);
        Assert.DoesNotContain("Idss", angelov);
        Assert.DoesNotContain("Ipk",  materka);
    }

    [Fact]
    public void EditingAnSddEquation_IsStructural_AndReachesTheModel()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });
        int before = vm.ContextRebuildCount;

        Assert.True(vm.ApplyInput(HarmonicaInputs.ParameterPrefix + "I[1,0]", "_v1/75"));
        Assert.Equal("_v1/75", vm.Model.Dut.Parameters["I[1,0]"]);

        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });
        Assert.Equal(before + 1, vm.ContextRebuildCount);
    }
}
