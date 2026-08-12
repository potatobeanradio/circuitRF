// ================================================================
//  HarmonicaSetDutTests.cs  —  M1's gate, brief-harmonicarf-h8
//
//  R-h8-1  the dialog EDITS DutSpec and hands it to the same structural write-back H7 built.
//          Switching device rebuilds the context exactly ONCE and resets the ladder exactly once —
//          counters, not clocks.
//  R-h8-2  the parameter list is READ from the model. Five FET laws are five different lists, and
//          none of them is the SDD's.
//  R-h8-3  an external DUT with no IntrinsicMapping draws the intrinsic panels EMPTY and the
//          readouts say why. Asserted, because "empty" and "broken" look identical otherwise.
//  §8.1    an SDD or built-in is EMBEDDED WHOLE; an .osdi or kit part is a REFERENCE.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices.External;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaSetDutTests(ITestOutputHelper output)
{
    // ══ R-h8-1 — one structural write-back, one rebuild, one ladder reset ════

    [Fact]
    public void SwitchingSddToAngelov_RebuildsTheContextExactlyOnce_AndResetsTheLadderOnce()
    {
        var vm = new HarmonicaViewModel();
        Assert.Equal(DutKind.Sdd, vm.Model.Dut.Kind);

        // Solve once so a context exists to be rebuilt — a rebuild counter is meaningless before
        // anything has been built.
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });
        int rebuildsBefore = vm.ContextRebuildCount;
        int resetsBefore   = vm.ScheduleResetCount;

        var editor = new HarmonicaDutEditor(vm.Model.Dut);
        editor.SetKind(DutKind.NativeFet);
        editor.SetNativeLaw("FET_Angelov");

        Assert.True(vm.ApplyDut(editor.Build()));
        Assert.Equal(resetsBefore + 1, vm.ScheduleResetCount);

        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });
        Assert.Equal(rebuildsBefore + 1, vm.ContextRebuildCount);

        output.WriteLine($"rebuilds {rebuildsBefore} → {vm.ContextRebuildCount}, " +
                         $"ladder resets {resetsBefore} → {vm.ScheduleResetCount}");
    }

    [Fact]
    public void ApplyingTheSameDut_IsANoOp_NotASecondRebuild()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });
        int rebuilds = vm.ContextRebuildCount;
        int resets   = vm.ScheduleResetCount;

        // Re-opening the dialog and pressing Set DUT without changing anything must change nothing.
        Assert.False(vm.ApplyDut(new HarmonicaDutEditor(vm.Model.Dut).Build()));

        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });
        Assert.Equal(rebuilds, vm.ContextRebuildCount);
        Assert.Equal(resets,   vm.ScheduleResetCount);
    }

    [Fact]
    public void SwitchingDevice_ProducesADifferentSection75InputList()
    {
        var vm = new HarmonicaViewModel();
        var sddInputs = vm.Inputs.Select(i => i.Key).ToArray();

        var editor = new HarmonicaDutEditor(vm.Model.Dut);
        editor.SetKind(DutKind.NativeFet);
        editor.SetNativeLaw("FET_Angelov");
        vm.ApplyDut(editor.Build());

        var fetInputs = vm.Inputs.Select(i => i.Key).ToArray();

        Assert.NotEqual(sddInputs, fetInputs);
        Assert.Contains(sddInputs, k => k.Contains("I[1,0]", StringComparison.Ordinal));
        Assert.DoesNotContain(fetInputs, k => k.Contains("I[1,0]", StringComparison.Ordinal));
    }

    // ══ R-h8-2 — the parameter list is READ from the model ═══════════════════

    [Fact]
    public void TheFiveFetLaws_ProduceFiveDifferentParameterLists_AndNoneIsTheSdds()
    {
        var sdd = new HarmonicaViewModel().Model;
        var sddNames = ParamNames(sdd);

        var byLaw = new Dictionary<string, string[]>(StringComparer.Ordinal);

        foreach (var (typeName, _) in HarmonicaDutCatalog.NativeFetLaws)
        {
            var editor = new HarmonicaDutEditor(sdd.Dut);
            editor.SetKind(DutKind.NativeFet);
            editor.SetNativeLaw(typeName);

            var model = sdd with { Dut = editor.Build() };
            var names = ParamNames(model);

            Assert.NotEmpty(names);
            Assert.NotEqual(sddNames, names);
            byLaw[typeName] = names;
            output.WriteLine($"{typeName}: {names.Length} params — {string.Join(", ", names.Take(6))}…");
        }

        Assert.Equal(5, byLaw.Count);

        // Five DIFFERENT lists, not five copies of one: the laws reuse spellings for different
        // quantities, which is exactly why each is its own device type rather than a variant.
        var distinct = byLaw.Values
            .Select(v => string.Join("|", v))
            .Distinct(StringComparer.Ordinal)
            .Count();
        Assert.Equal(5, distinct);
    }

    private static string[] ParamNames(CircuitModel model)
        => [.. HarmonicaInputs.DeclaredModelParameters(model)
                              .Select(i => i.Key[HarmonicaInputs.ParameterPrefix.Length..])];

    [Fact]
    public void AnExternalDutWhoseModelCannotBeReached_ShowsWhatTheDocumentCarries_NotAnInventedList()
    {
        var model = HarmonicaViewModel.DefaultModel() with
        {
            Dut = new DutSpec
            {
                Kind     = DutKind.External,
                TypeName = "crf_fet",
                Provider = "AKitThatIsNotInstalled",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["beta"] = "0.02", ["vth"] = "-2",
                },
            },
        };

        var names = ParamNames(model);
        Assert.Equal(["beta", "vth"], names.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    // ══ §8.1 — embed or reference, round-tripped ═════════════════════════════

    [Fact]
    public void AnSddIsEmbeddedWhole_AndComesBackAsTheModelsOwn()
    {
        var vm = new HarmonicaViewModel();
        string json = vm.ToCharmJson();

        var reloaded = new HarmonicaViewModel();
        reloaded.LoadCharm(json, baseDirectory: null);

        Assert.Equal(DutKind.Sdd, reloaded.Model.Dut.Kind);
        Assert.Equal(vm.Model.Dut.Parameters.Count, reloaded.Model.Dut.Parameters.Count);
        foreach (var (k, v) in vm.Model.Dut.Parameters)
            Assert.Equal(v, reloaded.Model.Dut.Parameters[k]);
    }

    [Fact]
    public void ANativeFetIsEmbeddedWhole_ParametersAndAll()
    {
        var vm = new HarmonicaViewModel();
        var editor = new HarmonicaDutEditor(vm.Model.Dut);
        editor.SetKind(DutKind.NativeFet);
        editor.SetNativeLaw("FET_Materka");
        editor.SetParameter("Idss", "0.123");
        vm.ApplyDut(editor.Build());

        var reloaded = new HarmonicaViewModel();
        reloaded.LoadCharm(vm.ToCharmJson(), baseDirectory: null);

        Assert.Equal(DutKind.NativeFet, reloaded.Model.Dut.Kind);
        Assert.Equal("FET_Materka", reloaded.Model.Dut.TypeName);
        Assert.Equal("0.123", reloaded.Model.Dut.Parameters["Idss"]);
    }

    [Fact]
    public void AnOsdiModelIsAReference_AndAMissingOneIsNamedRatherThanSubstituted()
    {
        string dir  = Path.Combine(Path.GetTempPath(), "crf-h8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string osdi = Path.Combine(dir, "mymodel.osdi");
        File.WriteAllText(osdi, "not really a compiled model, but it exists");

        try
        {
            var vm = new HarmonicaViewModel();
            var editor = new HarmonicaDutEditor(vm.Model.Dut);
            editor.SetKind(DutKind.External);
            editor.SetExternal(HarmonicaDutCatalog.ProviderForModelFile(osdi), descriptor: null);
            // The type id would come from the model; state one directly so the spec is complete.
            var dut = editor.Build() with { TypeName = "mydevice" };
            vm.ApplyDut(dut);

            string json = vm.ToCharmJson();

            // Present: the reference resolves, nothing is reported.
            var ok = new HarmonicaViewModel();
            var unresolvedWhilePresent = ok.LoadCharm(json, dir);
            Assert.DoesNotContain(unresolvedWhilePresent, u => u.Kind == "model");

            // Gone: NAMED, not substituted (§8.1). The document still opens so it can be re-pointed.
            File.Delete(osdi);
            var missing = new HarmonicaViewModel();
            var unresolved = missing.LoadCharm(json, dir);

            var modelRef = Assert.Single(unresolved, u => u.Kind == "model");
            Assert.Contains("mymodel.osdi", modelRef.Message, StringComparison.Ordinal);
            Assert.Equal(DutKind.External, missing.Model.Dut.Kind);
            output.WriteLine(modelRef.Message);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ══ R-h8-3 — no mapping ⇒ the intrinsic panels are EMPTY, and it SAYS SO ══

    [Fact]
    public void AnExternalDutWithNoIntrinsicMapping_ReportsTheLoadAndSourcePlanesUnavailable()
    {
        var map = IntrinsicPortMap.For(
            new DutSpec { Kind = DutKind.External, TypeName = "x", Provider = "p" },
            StubExternalModel(),
            LumpedPackage.None);

        Assert.False(map.LoadAvailable);
        Assert.False(map.SourceAvailable);
        Assert.Equal(-1, map.DrainPort);
        Assert.NotNull(map.Reason);
        Assert.Contains("Set DUT", map.Reason!, StringComparison.Ordinal);
        output.WriteLine(map.Reason!);
    }

    [Fact]
    public void ATwoPortDevice_NeedsNoMapping_AndIsAvailableOnBothSides()
    {
        foreach (var kind in new[] { DutKind.Sdd, DutKind.NativeFet, DutKind.Diode })
        {
            var map = IntrinsicPortMap.For(
                new DutSpec { Kind = kind, TypeName = "whatever" },
                StubExternalModel(),      // ignored for a non-external DUT, by construction
                LumpedPackage.None);

            Assert.True(map.LoadAvailable);
            Assert.True(map.SourceAvailable);
            Assert.Equal(0, map.GatePort);
            Assert.Equal(1, map.DrainPort);
            Assert.Equal(-1, map.SourcePort);   // already source-referenced
        }
    }

    [Fact]
    public void AMappingNamingANodeTheModelDoesNotDeclare_IsRefusedAndListsWhatItDoes()
    {
        var map = IntrinsicPortMap.For(
            new DutSpec
            {
                Kind = DutKind.External, TypeName = "x", Provider = "p",
                IntrinsicMapping = new IntrinsicMapping("gate", "NOT_A_NODE", "src"),
            },
            StubExternalModel(),
            LumpedPackage.None);

        Assert.False(map.LoadAvailable);
        Assert.Contains("NOT_A_NODE", map.Reason!, StringComparison.Ordinal);
        Assert.Contains("drain", map.Reason!, StringComparison.Ordinal);   // the model's own labels
        output.WriteLine(map.Reason!);
    }

    [Fact]
    public void AResolvedMapping_LocatesTheLoadPlane_AndReferencesVoltagesToTheSourcePin()
    {
        var map = IntrinsicPortMap.For(
            new DutSpec
            {
                Kind = DutKind.External, TypeName = "x", Provider = "p",
                IntrinsicMapping = new IntrinsicMapping("gate", "drain", "src"),
            },
            StubExternalModel(),
            LumpedPackage.None);

        Assert.True(map.LoadAvailable);
        Assert.Equal(0, map.GatePort);
        Assert.Equal(1, map.DrainPort);
        Assert.Equal(2, map.SourcePort);   // NOT −1: an external model's ports are node-to-ground
    }

    [Fact]
    public void AResolvedMappingWithASourceLead_RefusesTheSourceSideByName_AndKeepsTheLoadSide()
    {
        var map = IntrinsicPortMap.For(
            new DutSpec
            {
                Kind = DutKind.External, TypeName = "x", Provider = "p",
                IntrinsicMapping = new IntrinsicMapping("gate", "drain", "src"),
            },
            StubExternalModel(),
            new LumpedPackage { Rs = 0.8, Ls = 50e-12 });

        // §4.5.1's ratio needs only the drain port and still works…
        Assert.True(map.LoadAvailable);
        // …while §4.5.3's route reads the gate port's own incidence, which is gate-to-GROUND for an
        // external model and stops being the gate-source port the moment a lead lifts the source.
        Assert.False(map.SourceAvailable);
        Assert.Contains("source lead", map.SourceUnavailable!, StringComparison.Ordinal);
        output.WriteLine(map.SourceUnavailable!);
    }

    // ══ the editor's own refusals ════════════════════════════════════════════

    [Fact]
    public void APartlyNamedIntrinsicPlane_IsRefused_BecauseTwoThirdsResolvesToNothing()
    {
        var editor = new HarmonicaDutEditor(new DutSpec { Kind = DutKind.External, TypeName = "t" });
        editor.SetExternal("p", new ExternalDeviceDescriptor("t", "T", 3, 0, [], []));

        editor.GateNode  = "gate";
        editor.DrainNode = "drain";
        // SourcePin left unnamed.

        Assert.NotNull(editor.Validate());
        Assert.Contains("all three", editor.Validate()!, StringComparison.Ordinal);
    }

    [Fact]
    public void NoMappingAtAll_IsNotAnError_BecauseItIsALegitimateState()
    {
        var editor = new HarmonicaDutEditor(new DutSpec { Kind = DutKind.External, TypeName = "t" });
        editor.SetExternal("p", new ExternalDeviceDescriptor("t", "T", 3, 0, [], []));
        editor.SetParameter("k", "1");
        // Provider and type are set; no mapping named. §4.5.5 — the panels draw empty and say why.
        var dut = editor.Build();
        Assert.Null(dut.IntrinsicMapping);
    }

    [Fact]
    public void SwitchingToADifferentExternalModel_DropsTheOldMapping_RatherThanCarryingItAcross()
    {
        var editor = new HarmonicaDutEditor(new DutSpec
        {
            Kind = DutKind.External, TypeName = "old", Provider = "p",
            IntrinsicMapping = new IntrinsicMapping("g", "d", "s"),
        });

        editor.SetExternal("p", new ExternalDeviceDescriptor("new", "New", 3, 0, [], []));

        Assert.Null(editor.GateNode);
        Assert.Null(editor.Build().IntrinsicMapping);
    }

    /// <summary>
    /// A stand-in for a compiled model: it declares three nodes with the labels an intrinsic mapping
    /// would name. A REAL <c>ExternalDeviceModel</c> around a stub instance — the class is sealed,
    /// and using the real one is the point: the mapping resolves against
    /// <c>ExternalDeviceModel.Descriptor</c>, so a hand-rolled substitute would test a type the
    /// product never sees. Deliberately not a real worker, though: the resolution is node-label
    /// arithmetic and driving a kit to check it would test the kit.
    /// </summary>
    private static ExternalDeviceModel StubExternalModel()
        => new(new StubInstance(), "p", "DUT");

    private sealed class StubInstance : IExternalDeviceInstance
    {
        public ExternalDeviceDescriptor Descriptor { get; } = new(
            "x", "X", ExternalPinCount: 3, InternalNodeCount: 0,
            Parameters: [],
            Nodes:
            [
                new ExternalNodeDescriptor(0, External: true, Label: "gate"),
                new ExternalNodeDescriptor(1, External: true, Label: "drain"),
                new ExternalNodeDescriptor(2, External: true, Label: "src"),
            ]);

        public ExternalDeviceEvaluation Evaluate(IReadOnlyList<double> nodeVoltages)
            => new(new double[3], new double[3], new double[3, 3], new double[3, 3]);

        public void Dispose() { }
    }
}
