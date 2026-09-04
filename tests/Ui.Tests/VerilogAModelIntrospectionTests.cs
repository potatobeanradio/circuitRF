using System.IO;
using System.Linq;
using CircuitRF.Core.Devices;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner questions about the VerilogA component: what is <c>Model</c> for, how is <c>Pins</c>
/// supposed to be known, and can both be filled in from the chosen <c>.osdi</c> — with a picker
/// when the file declares more than one model.
///
/// The file itself is the authority for all three, so these gate the reading of it. A real
/// <c>.osdi</c> cannot be stood up here (it needs a compiled artefact and the model-hosting worker),
/// so what is tested is everything around that: the selection rule the dialog and the engine must
/// agree on, and that an unreadable pick reports rather than throws or silently overwrites.
/// </summary>
public sealed class VerilogAModelIntrospectionTests
{
    // ── Reading a file that is not there / not a model ────────────────────────

    [Fact]
    public void NoFileChosenYet_IsNotAnError()
    {
        Assert.Empty(VerilogAModelIntrospection.Describe("", out string? error));
        Assert.Null(error);

        Assert.Empty(VerilogAModelIntrospection.Describe(null, out error));
        Assert.Null(error);
    }

    [Fact]
    public void AMissingFile_IsReportedByPath_NotThrown()
    {
        string path = Path.Combine(Path.GetTempPath(), "crf-no-such-" + System.Guid.NewGuid().ToString("N") + ".osdi");

        var models = VerilogAModelIntrospection.Describe(path, out string? error);

        Assert.Empty(models);
        Assert.NotNull(error);
        Assert.Contains(path, error);
    }

    // ── The one test in this file that starts a real process, and why it is opt-in ────────────────
    // Every other test here either passes a path that does not exist (refused before anything is
    // launched) or exercises pure selection logic. This one deliberately hands the REAL provider a
    // file that exists and is not a model, so it goes all the way to `ExternalDeviceRegistry.Find`
    // and a worker is started to attempt the load.
    //
    // It is tagged for a THIRD reason, not either of the two src/Ui/CLAUDE.md already records: it is
    // neither slow (~ms) nor itself wall-clock-sensitive. It adds concurrent PROCESS load, and
    // `Core.Tests`' `DeviceWorkerProcessTests.AWorkerThatDiesImmediately_StillReportsWhatItSaidOnTheWayOut`
    // is a deliberate under-load race whose own comment forbids "stabilising" it by waiting longer —
    // that wait IS the grace period it exists to remove. Measured: 4 full-solution runs clean without
    // this file, 2 failures in 3 runs with it. So the load is moved out of the routine gate rather
    // than the other test's guarantee being weakened.
    //
    // COST, stated rather than implied: the routine gate no longer covers "picking the wrong file is
    // reported, not thrown" — a real user-facing path (it is what a file picker hands you). Run it
    // with the ordinary opt-in: dotnet test --settings circuitrf.benchmark.runsettings

    [Trait("Category", "Benchmark")]
    [Fact]
    public void AFileThatIsNotACompiledModel_ReportsRatherThanThrowing()
    {
        // Exactly what a user gets for picking the wrong file: it exists and is not a model. This
        // must come back as a note the dialog can show, never an exception out of a file picker.
        string path = Path.Combine(Path.GetTempPath(), "crf-bad-" + System.Guid.NewGuid().ToString("N") + ".osdi");
        File.WriteAllText(path, "this is not a compiled model");
        try
        {
            var models = VerilogAModelIntrospection.Describe(path, out string? error);

            Assert.Empty(models);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
        finally { File.Delete(path); }
    }

    // ── Which model a component's `Model` value selects ───────────────────────
    //
    // This rule is duplicated nowhere: it is the same one ComponentModelFactory.CreateVerilogAModel
    // applies at Run, so the dialog can never promise a device Run then refuses.

    [Fact]
    public void ASingleDeclaredModel_IsSelectedWithoutTheUserNamingIt()
    {
        var declared = new[] { new VerilogAModelInfo("MODELA_VA", 4, 809) };

        var picked = VerilogAModelIntrospection.Select(declared, "");

        Assert.NotNull(picked);
        Assert.Equal("MODELA_VA", picked!.TypeId);
        Assert.Equal(4, picked.PinCount);   // the answer to "how many Pins?"
    }

    [Fact]
    public void SeveralDeclaredModels_SelectNothingUntilOneIsNamed()
    {
        // With a choice to make, guessing would place a different device from the one intended —
        // this is exactly why the row becomes a picker instead of being decided for the user.
        var declared = new[]
        {
            new VerilogAModelInfo("MODELA_VA",    4, 809),
            new VerilogAModelInfo("MODELA_NQS_VA", 4, 812),
        };

        Assert.Null(VerilogAModelIntrospection.Select(declared, ""));
        Assert.Equal("MODELA_NQS_VA", VerilogAModelIntrospection.Select(declared, "MODELA_NQS_VA")!.TypeId);
    }

    [Fact]
    public void ANameThatMatchesNothing_SelectsNothing_RatherThanFallingBackToTheFirst()
    {
        var declared = new[] { new VerilogAModelInfo("MODELA_VA", 4, 809) };

        Assert.Null(VerilogAModelIntrospection.Select(declared, "auxres"));
    }

    [Fact]
    public void SelectionIsCaseSensitive_MatchingTheFactorysOwnRule()
    {
        // CreateVerilogAModel compares the Model value with StringComparison.Ordinal; a dialog that
        // accepted "modela_va" would fill in Pins for a device Run then refuses by name.
        var declared = new[] { new VerilogAModelInfo("MODELA_VA", 4, 809) };

        Assert.Null(VerilogAModelIntrospection.Select(declared, "modela_va"));
    }

    [Fact]
    public void SelectingFromAnEmptyDeclarationSetIsNull_NotAnException()
        => Assert.Null(VerilogAModelIntrospection.Select([], "anything"));

    // ── Which model a file DEFAULTS to ────────────────────────────────────────
    //
    // Select stays strict: blank means "nothing named". Default is the separate question of what to
    // OFFER when nothing is named, and the dialog writes its answer onto the component rather than
    // leaving Model blank — CreateVerilogAModel refuses a blank Model outright once a file declares
    // more than one type, so an unset component is one that fails at Run.

    [Fact]
    public void TheDefaultIsTheModelWithTheMostTerminals()
    {
        // A family ships its variants side by side and the fuller formulation carries the extra
        // terminals — a substrate node, a self-heating node, or both.
        var declared = new[]
        {
            new VerilogAModelInfo("REDUCED_VA",  3, 700),
            new VerilogAModelInfo("FULL_VA",     4, 700),
            new VerilogAModelInfo("FULL_T_VA",   5, 700),
            new VerilogAModelInfo("JUNCTION_VA", 2, 120),
        };

        Assert.Equal("FULL_T_VA", VerilogAModelIntrospection.Default(declared)!.TypeId);
    }

    [Fact]
    public void EqualTerminalCounts_AreBrokenByTheDeclaredParameterCount()
    {
        var declared = new[]
        {
            new VerilogAModelInfo("PLAIN_VA", 4, 809),
            new VerilogAModelInfo("NQS_VA",   4, 812),
        };

        Assert.Equal("NQS_VA", VerilogAModelIntrospection.Default(declared)!.TypeId);
    }

    [Fact]
    public void AFullyTiedRankIsBrokenByName_SoTheDefaultIsTheSameOnEveryMachine()
    {
        // Otherwise the default would follow whatever order the artefact happened to enumerate in,
        // and two machines opening the same design would fill in different devices.
        var forward = new[]
        {
            new VerilogAModelInfo("BBB_VA", 4, 700),
            new VerilogAModelInfo("AAA_VA", 4, 700),
        };
        var reversed = new[]
        {
            new VerilogAModelInfo("AAA_VA", 4, 700),
            new VerilogAModelInfo("BBB_VA", 4, 700),
        };

        Assert.Equal("AAA_VA", VerilogAModelIntrospection.Default(forward)!.TypeId);
        Assert.Equal("AAA_VA", VerilogAModelIntrospection.Default(reversed)!.TypeId);
    }

    [Fact]
    public void ASingleDeclaredModelIsItsOwnDefault()
    {
        // The common case by far: every published model file checked declares exactly one module.
        var declared = new[] { new VerilogAModelInfo("MODELA_VA", 4, 809) };

        Assert.Equal("MODELA_VA", VerilogAModelIntrospection.Default(declared)!.TypeId);
    }

    [Fact]
    public void AnEmptyDeclarationSetHasNoDefault()
        => Assert.Null(VerilogAModelIntrospection.Default([]));

    // ── What the dialog says about the three parameters ───────────────────────

    [Fact]
    public void EachVerilogAParameter_ExplainsItselfInTheDialog()
    {
        foreach (var name in new[] { "File", "Model", "Pins" })
            Assert.False(string.IsNullOrWhiteSpace(
                ComponentTypeRegistry.ParameterDescription(SymbolKind.VerilogA, name)));
    }

    [Fact]
    public void OrdinaryPrimitiveParameters_CarryNoDescription()
    {
        // "R: the resistance" is noise — descriptions are for the parameters whose answer is inside
        // a file the user just chose, not for every parameter of every primitive.
        Assert.Equal("", ComponentTypeRegistry.ParameterDescription(SymbolKind.Resistor, "R"));
        Assert.Equal("", ComponentTypeRegistry.ParameterDescription(SymbolKind.VerilogA, "Temp"));
    }

    // ── The dialog, driven for real ──────────────────────────────────────────

    [Fact]
    public void OpeningTheDialogOnAVerilogA_WithNoFileYet_ChangesNothingAndSaysNothing()
    {
        var (vm, comp) = PlaceVerilogA(file: "");
        var editor = new ParameterEditorViewModel();

        editor.SetTargetDirect(vm, comp);

        // Opening a dialog is not an edit — autofill must never dirty a schematic just by looking.
        Assert.False(vm.UndoRedo.CanUndo);
        Assert.Equal("", editor.VerilogAFileNote);
        Assert.False(editor.HasVerilogAFileNote);
        Assert.Equal("2", ValueOf(comp, "Pins"));   // the placement default, untouched
    }

    [Fact]
    public void OpeningTheDialogOnAVerilogA_WithAFileThatCannotBeRead_ReportsAndLeavesTheParametersAlone()
    {
        string path = Path.Combine(Path.GetTempPath(), "crf-nope-" + System.Guid.NewGuid().ToString("N") + ".osdi");
        var (vm, comp) = PlaceVerilogA(path);
        var editor = new ParameterEditorViewModel();

        editor.SetTargetDirect(vm, comp);

        Assert.True(editor.HasVerilogAFileNote);
        Assert.Contains(path, editor.VerilogAFileNote);
        Assert.False(vm.UndoRedo.CanUndo);
        Assert.Equal("2", ValueOf(comp, "Pins"));
        Assert.Equal("", ValueOf(comp, "Model"));
    }

    [Fact]
    public void TheModelRow_OffersNoPickerUntilAFileDeclaresMoreThanOneType()
    {
        var (vm, comp) = PlaceVerilogA(file: "");
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp);

        var modelRow = editor.Rows.Single(r => r.Name == "Model");
        Assert.False(modelRow.IsChoiceParam);
        Assert.True(modelRow.ShowExpressionTextBox);
    }

    [Fact]
    public void RuntimeChoices_TurnARowIntoAPicker_AndAlwaysContainTheCurrentValue()
    {
        var (vm, comp) = PlaceVerilogA(file: "");
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp);

        var modelRow = editor.Rows.Single(r => r.Name == "Model");
        modelRow.StagedExpression = "SOMETHING_ELSE";
        modelRow.SetRuntimeChoices(["MODELA_VA", "MODELA_NQS_VA"]);

        Assert.True(modelRow.IsChoiceParam);
        Assert.False(modelRow.ShowExpressionTextBox);
        // A ComboBox whose selection is absent from its items renders blank, which reads as the
        // value having been lost — so the staged value is always among the options.
        Assert.Contains("SOMETHING_ELSE", modelRow.ChoiceOptions);
        Assert.Equal(["MODELA_VA", "MODELA_NQS_VA", "SOMETHING_ELSE"], modelRow.ChoiceOptions);

        modelRow.SetRuntimeChoices([]);
        Assert.False(modelRow.IsChoiceParam);
    }

    [Fact]
    public void EveryVerilogARow_ExplainsItselfOnHover()
    {
        var (vm, comp) = PlaceVerilogA(file: "");
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp);

        Assert.All(editor.Rows, r => Assert.False(string.IsNullOrWhiteSpace(r.NameTooltip)));
    }

    // ── Removing a parameter ─────────────────────────────────────────────────
    //
    // The footer's "−" removes the LAST indexed GROUP, which is right for P1Tone's Z[k] and
    // ToneSource's Freq[n]/V[n]/Phase[n] (a sequence, and members that must go together) and is no
    // way at all to reach the FIRST of a hundred independent model parameters. Hence a per-row "×".

    [Fact]
    public void TheThreeParametersCircuitRfOwns_AreNotRemovable()
    {
        // Pins in particular is structural: the symbol cannot decide how many terminals to draw
        // without it.
        foreach (var name in new[] { "File", "Model", "Pins" })
            Assert.False(ComponentTypeRegistry.IsRemovableParameter(SymbolKind.VerilogA, name));
    }

    [Fact]
    public void AModelsOwnParameter_IsRemovableOnItsOwn()
        => Assert.True(ComponentTypeRegistry.IsRemovableParameter(SymbolKind.VerilogA, "vth"));

    [Fact]
    public void AnIndexedGroupComponent_GetsNoPerRowRemove()
    {
        // Removing Freq[3] while leaving V[3] would half-delete a tone — that is exactly what the
        // group-wise "−" exists for, and why per-row removal must not reach these.
        Assert.False(ComponentTypeRegistry.IsRemovableParameter(SymbolKind.ToneSource, "Freq[2]"));
        Assert.False(ComponentTypeRegistry.IsRemovableParameter(SymbolKind.P1Tone, "Z[2]"));
        Assert.False(ComponentTypeRegistry.IsRemovableParameter(SymbolKind.Resistor, "R"));
    }

    [Fact]
    public void RemovingARow_TakesThatOneParameter_NotTheLast()
    {
        var (vm, comp) = PlaceVerilogA(file: "");
        comp.Parameters.Add(new EditableParameter { Name = "vth",    Expression = "-2.5" });
        comp.Parameters.Add(new EditableParameter { Name = "beta",   Expression = "0.06" });
        comp.Parameters.Add(new EditableParameter { Name = "lambda", Expression = "0.02" });

        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp);

        // The FIRST model parameter, deliberately — the case the group-wise "−" cannot reach.
        editor.Rows.Single(r => r.Name == "vth").RemoveSelf();

        Assert.DoesNotContain(comp.Parameters, p => p.Name == "vth");
        Assert.Contains(comp.Parameters, p => p.Name == "beta");
        Assert.Contains(comp.Parameters, p => p.Name == "lambda");
        Assert.Contains(comp.Parameters, p => p.Name == "Pins");
    }

    [Fact]
    public void RemovingARow_IsOneUndoEntry_ThatPutsItBack()
    {
        var (vm, comp) = PlaceVerilogA(file: "");
        comp.Parameters.Add(new EditableParameter { Name = "vth", Expression = "-2.5" });

        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp);
        editor.Rows.Single(r => r.Name == "vth").RemoveSelf();

        Assert.True(vm.UndoRedo.CanUndo);
        vm.UndoRedo.Undo();

        var restored = vm.EditModel.Components.Single().Parameters.Single(p => p.Name == "vth");
        Assert.Equal("-2.5", restored.Expression);
    }

    [Fact]
    public void ARowCircuitRfOwns_RefusesToRemoveItself_EvenIfAsked()
    {
        var (vm, comp) = PlaceVerilogA(file: "");
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp);

        var pins = editor.Rows.Single(r => r.Name == "Pins");
        Assert.False(pins.CanRemove);
        pins.RemoveSelf();

        Assert.Contains(comp.Parameters, p => p.Name == "Pins");
        Assert.False(vm.UndoRedo.CanUndo);
    }

    private static (SchematicViewModel Vm, EditableComponent Comp) PlaceVerilogA(string file)
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent
        {
            InstanceName = "X1",
            Symbol       = SymbolKind.VerilogA,
            X            = 0,
            Y            = 0,
        };
        foreach (var d in ComponentTypeRegistry.DefaultParameters(SymbolKind.VerilogA, 2))
            comp.Parameters.Add(new EditableParameter
            {
                Name            = d.Name,
                Expression      = d.Name == "File" ? file : d.Expression,
                Unit            = d.Unit,
                Dimension       = d.Dimension,
                ShowOnSchematic = d.ShowOnSchematic,
            });
        model.Components.Add(comp);
        return (new SchematicViewModel(model), comp);
    }

    private static string ValueOf(EditableComponent comp, string name)
        => comp.Parameters.First(p => p.Name == name).Expression;

    // ── The registry's own defaults still say what the symbol needs ───────────

    /// <summary>
    /// Exactly what a placed component starts with, in order. <c>File</c>, <c>Model</c> and
    /// <c>Pins</c> are the three the dialog fills in from the chosen model; <c>OpVars</c> is the
    /// read-back switch (PM3, 2026-09-03), which nothing fills in because it is already right.
    /// </summary>
    [Fact]
    public void APlacedVerilogA_StartsWithTheParametersTheDialogFillsIn_PlusTheReadBackSwitch()
    {
        var names = ComponentTypeRegistry.DefaultParameters(SymbolKind.VerilogA, 2)
                                         .Select(p => p.Name)
                                         .ToArray();

        Assert.Equal(["File", "Model", "Pins", "OpVars"], names);
    }

    /// <summary>
    /// <b>Every parameter a placed component starts with is one circuitRF owns, and every rule that
    /// enumerates them has to agree.</b>
    ///
    /// <para>Reported 2026-09-04, one day after <c>OpVars</c> was added: a freshly placed compiled
    /// model announced "Not declared by this model: OpVars — these will be refused at Run". Both
    /// halves were false. <c>OpVars</c> is circuitRF's own read-back switch, the factory's forwarding
    /// filter is precisely what stops it ever reaching a model, and so nothing could refuse it. The
    /// list of these names existed in four places; three gained the new one and the parameter
    /// editor's copy did not.</para>
    ///
    /// <para>This is the gate for the fifth such parameter rather than for that one bug: it asserts
    /// the seeded set and the predicate are the same set, so adding a name in one place and not the
    /// other fails here instead of in a dialog.</para>
    /// </summary>
    [Fact]
    public void EverySeededVerilogAParameter_IsOneCircuitRfOwns()
    {
        foreach (var p in ComponentTypeRegistry.DefaultParameters(SymbolKind.VerilogA, 2))
        {
            Assert.True(ComponentModelFactory.IsVerilogAHostParameter(p.Name),
                $"'{p.Name}' is seeded onto every placed VerilogA component but is not one of "
                + "circuitRF's own names, so the parameter editor will report it as undeclared by "
                + "the model and claim it will be refused at Run. Add it to "
                + "ComponentModelFactory.IsVerilogAHostParameter.");

            // The same fact from the other side: a name circuitRF owns is never forwarded, and a
            // name that is never forwarded cannot be refused by anything downstream.
            Assert.False(ComponentTypeRegistry.IsRemovableParameter(SymbolKind.VerilogA, p.Name),
                $"'{p.Name}' is structural but the editor offers to delete it.");
        }
    }

    /// <summary>
    /// And the model's OWN parameters are not swept up by that predicate — the case that would make
    /// the rule above safe and useless. A compact model's names are lowercase and its own.
    /// </summary>
    [Theory]
    [InlineData("l")]
    [InlineData("w")]
    [InlineData("nf")]
    [InlineData("rth0")]
    public void AModelsOwnParameter_IsNotMistakenForOneOfCircuitRfs(string name)
    {
        Assert.False(ComponentModelFactory.IsVerilogAHostParameter(name));
        Assert.True(ComponentTypeRegistry.IsRemovableParameter(SymbolKind.VerilogA, name));
    }
}
