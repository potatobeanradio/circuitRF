using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// brief-technology-editor-units-and-layers.md R-tec-6/7/8/9/10 — the Parameter Editor's SignalLayer/
/// GroundReference picker: options resolved from the schematic's own workspace technology, "(Default)"
/// as the way back to automatic (R-tec-8), Ground filtered to ONLY IsGroundReference conductors per the
/// owner's explicit instruction, and a stale/unknown name surfaced informationally (the real fallback
/// itself already lives at the SubstrateResolver layer, tested separately).
/// </summary>
public class LayerChoicePickerTests : IDisposable
{
    private readonly string _root;

    public LayerChoicePickerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-layerchoice-picker-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string WriteWorkspaceWithTech(Technology tech)
    {
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        TechPersistence.SaveToFile(Path.Combine(_root, "tech", "t.ctech"), tech);

        var cws = new CwsFile { DefaultTechRef = "tech/t.ctech" };
        WorkspacePersistence.SaveToFile(Path.Combine(_root, ".cws"), cws);

        var schematicDir = Path.Combine(_root, "Amp", "schematic");
        Directory.CreateDirectory(schematicDir);
        return schematicDir;
    }

    private static (SchematicViewModel Vm, EditableComponent Comp, ParameterEditorViewModel Editor) MakeMlin(
        string? schematicDir, string signalLayer = "", string groundReference = "")
    {
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        var comp = new EditableComponent { Symbol = SymbolKind.Mlin, InstanceName = "ML1", X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 0))
        {
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name,
                Expression = dp.Name == "SignalLayer" ? signalLayer
                           : dp.Name == "GroundReference" ? groundReference
                           : dp.Expression,
                Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic,
                Dimension = dp.Dimension,
            });
        }
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(vm, comp, showClose: false);
        return (vm, comp, editor);
    }

    private static ParameterRowViewModel SignalRow(ParameterEditorViewModel editor)
        => editor.Rows.Single(r => r.Name == "SignalLayer");

    private static ParameterRowViewModel GroundRow(ParameterEditorViewModel editor)
        => editor.Rows.Single(r => r.Name == "GroundReference");

    // ── Row identification ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void SignalLayerAndGroundReference_AreLayerChoiceParams_OthersAreNot()
    {
        var (_, _, editor) = MakeMlin(null);

        Assert.True(SignalRow(editor).IsLayerChoiceParam);
        Assert.Equal(ComponentTypeRegistry.LayerChoiceKind.Signal, SignalRow(editor).LayerChoiceKind);
        Assert.True(GroundRow(editor).IsLayerChoiceParam);
        Assert.Equal(ComponentTypeRegistry.LayerChoiceKind.Ground, GroundRow(editor).LayerChoiceKind);

        var wRow = editor.Rows.Single(r => r.Name == "W");
        Assert.False(wRow.IsLayerChoiceParam);
        Assert.Null(wRow.LayerChoiceKind);
        Assert.True(wRow.ShowUnitCombo); // W still shows its ordinary Unit combo
    }

    [Fact]
    public void ShowUnitCombo_IsFalse_ForLayerChoiceRows_TrueForOrdinaryRows()
    {
        var (_, _, editor) = MakeMlin(null);
        Assert.False(SignalRow(editor).ShowUnitCombo);
        Assert.False(GroundRow(editor).ShowUnitCombo);
        Assert.True(editor.Rows.Single(r => r.Name == "W").ShowUnitCombo);
    }

    // ── Owner follow-up: ONE combobox, no adjacent text field ("it's a little strange... a little
    // buggy right now") — the earlier text-field-plus-picker design is gone; a layer-choice row
    // shows ONLY the picker ComboBox, exactly like an enum-style row shows ONLY its combo. ────────

    [Fact]
    public void ShowExpressionTextBox_IsFalse_ForLayerChoiceRows_TrueForOrdinaryRows_FalseForEnumRows()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var (_, _, editor) = MakeMlin(schematicDir);

        Assert.False(SignalRow(editor).ShowExpressionTextBox);
        Assert.False(GroundRow(editor).ShowExpressionTextBox);
        Assert.True(editor.Rows.Single(r => r.Name == "W").ShowExpressionTextBox);
    }

    // ── R-tec-7/8: no workspace → just "(Default)", empty commits fine ────────────────────────────

    [Fact]
    public void NoWorkspace_OptionsAreJustDefault_SelectedIsDefault()
    {
        var (_, _, editor) = MakeMlin(null);

        Assert.Equal(new[] { "(Default)" }, SignalRow(editor).LayerChoiceOptions);
        Assert.Equal(new[] { "(Default)" }, GroundRow(editor).LayerChoiceOptions);
        Assert.Equal("(Default)", SignalRow(editor).SelectedLayerChoice);
    }

    // ── Gate 7 (regression guard): defaults-unset behavior is unaffected ───────────────────────────

    [Fact]
    public void UnsetSignalLayerAndGroundReference_StagedExpressionEmpty_NoWarning()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var (_, comp, editor) = MakeMlin(schematicDir);

        Assert.Equal("", comp.Parameters.Single(p => p.Name == "SignalLayer").Expression);
        Assert.Equal("", comp.Parameters.Single(p => p.Name == "GroundReference").Expression);
        Assert.Equal("(Default)", SignalRow(editor).SelectedLayerChoice);
        Assert.Equal("(Default)", GroundRow(editor).SelectedLayerChoice);
        Assert.False(SignalRow(editor).HasLayerChoiceMissingWarning);
        Assert.False(GroundRow(editor).HasLayerChoiceMissingWarning);
    }

    // ── Gate 11: options come from the RESOLVED technology; different techs list different conductors ──

    [Fact]
    public void SignalLayerOptions_Pcb_ListsBothConductors()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var (_, _, editor) = MakeMlin(schematicDir);

        var options = SignalRow(editor).LayerChoiceOptions;
        Assert.Equal("(Default)", options[0]);
        Assert.Contains("Top Copper (1 oz)", options);
        Assert.Contains("Bottom Copper (1 oz)", options);
    }

    [Fact]
    public void SignalLayerOptions_Mmic_ListsDifferentConductors_ThanPcb()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.MmicGaAs());
        var (_, _, editor) = MakeMlin(schematicDir);

        var options = SignalRow(editor).LayerChoiceOptions;
        Assert.Contains("Metal1", options);
        Assert.Contains("Metal2", options);
        Assert.DoesNotContain("Top Copper (1 oz)", options);
    }

    // ── Ground filtered to ONLY IsGroundReference conductors (owner's explicit instruction) ────────

    [Fact]
    public void GroundReferenceOptions_Pcb_ListsOnlyTheGroundMarkedConductor_NotBothConductors()
    {
        var tech = StarterTechnologies.Pcb2Layer();
        Assert.True(tech.Stackup.Layers.Single(l => l.Name == "Bottom Copper (1 oz)").IsGroundReference);
        Assert.False(tech.Stackup.Layers.Single(l => l.Name == "Top Copper (1 oz)").IsGroundReference);

        var schematicDir = WriteWorkspaceWithTech(tech);
        var (_, _, editor) = MakeMlin(schematicDir);

        var groundOptions = GroundRow(editor).LayerChoiceOptions;
        Assert.Equal(new[] { "(Default)", "Bottom Copper (1 oz)" }, groundOptions);

        // The Signal row, by contrast, lists every conductor (not ground-filtered).
        var signalOptions = SignalRow(editor).LayerChoiceOptions;
        Assert.Contains("Top Copper (1 oz)", signalOptions);
        Assert.Contains("Bottom Copper (1 oz)", signalOptions);
    }

    [Fact]
    public void GroundReferenceOptions_NoConductorMarkedGround_IsJustDefault()
    {
        var tech = new Technology { Name = "No Ground Marked" };
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "Top", SigmaSm = 5.8e7 });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Dielectric, Name = "D1", ThicknessDbu = 1_000_000, Epsr = 4.4 });
        tech.Stackup.Layers.Add(new StackupLayer { Kind = StackupKind.Conductor, Name = "Bottom", SigmaSm = 5.8e7 });

        var schematicDir = WriteWorkspaceWithTech(tech);
        var (_, _, editor) = MakeMlin(schematicDir);

        Assert.Equal(new[] { "(Default)" }, GroundRow(editor).LayerChoiceOptions);
    }

    // ── Selecting a picker option commits immediately, undoably ─────────────────────────────────

    [Fact]
    public void SelectingAConductor_CommitsExpression_Undoable()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var (vm, comp, editor) = MakeMlin(schematicDir);
        var row = SignalRow(editor);

        row.SelectedLayerChoice = "Bottom Copper (1 oz)";

        Assert.Equal("Bottom Copper (1 oz)", comp.Parameters.Single(p => p.Name == "SignalLayer").Expression);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.Equal("", comp.Parameters.Single(p => p.Name == "SignalLayer").Expression);
    }

    [Fact]
    public void SelectingSameChoice_IsANoOp_PushesNoUndoEntry()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var (vm, comp, editor) = MakeMlin(schematicDir);
        var row = SignalRow(editor);
        bool couldUndoBefore = vm.UndoRedo.CanUndo;

        row.SelectedLayerChoice = "(Default)"; // already the default — no-op

        Assert.Equal("", comp.Parameters.Single(p => p.Name == "SignalLayer").Expression);
        Assert.Equal(couldUndoBefore, vm.UndoRedo.CanUndo);
    }

    // ── Owner bug report: "sometimes does not register my new choice... I have to set my choice
    // multiple times." Root cause: RefreshFromModel runs on EVERY row after ANY parameter edit —
    // including, reentrantly, the very row whose own combo selection triggered the edit — and used
    // to unconditionally swap LayerChoiceOptions to a brand-new (content-identical) list every time,
    // resetting the ComboBox's ItemsSource mid-selection. Fixed: the list is only ever replaced (and
    // its PropertyChanged only ever raised) when the CONTENT genuinely differs. ─────────────────────

    [Fact]
    public void SelectingAConductor_NeverReassignsLayerChoiceOptions_WhenContentIsUnchanged()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var (_, _, editor) = MakeMlin(schematicDir);
        var row = SignalRow(editor);
        var optionsBefore = row.LayerChoiceOptions;

        int layerChoiceOptionsRaised = 0;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ParameterRowViewModel.LayerChoiceOptions))
                layerChoiceOptionsRaised++;
        };

        row.SelectedLayerChoice = "Bottom Copper (1 oz)";

        Assert.Equal(0, layerChoiceOptionsRaised);
        Assert.Same(optionsBefore, row.LayerChoiceOptions); // the exact same list instance — ItemsSource never swapped
    }

    [Fact]
    public void SelectingBackAndForthBetweenTwoKnownConductors_NeverChurnsLayerChoiceOptions()
    {
        // The reported symptom was intermittent across repeated selections, not a one-shot glitch —
        // this drives several picks in a row and asserts the ItemsSource stays completely stable
        // throughout, not just for a single selection.
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var (_, _, editor) = MakeMlin(schematicDir);
        var row = SignalRow(editor);
        var optionsBefore = row.LayerChoiceOptions;

        row.SelectedLayerChoice = "Bottom Copper (1 oz)";
        row.SelectedLayerChoice = "Top Copper (1 oz)";
        row.SelectedLayerChoice = "Bottom Copper (1 oz)";
        row.SelectedLayerChoice = "(Default)";
        row.SelectedLayerChoice = "Top Copper (1 oz)";

        Assert.Same(optionsBefore, row.LayerChoiceOptions);
        Assert.Equal("Top Copper (1 oz)", row.SelectedLayerChoice);
    }

    [Fact]
    public void MovingAwayFromAGhostedCustomValue_ReassignsOptionsExactlyOnce_ToDropTheGhost()
    {
        // The one legitimate case where the option list DOES need to change on selection: moving
        // away from a custom/ghosted value (R-tec-9) to a real conductor correctly drops the now-
        // stale ghost entry — this is real content change, not needless churn, so it's expected to
        // reassign (and notify) exactly once.
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var (_, _, editor) = MakeMlin(schematicDir, signalLayer: "SomeGhostedCustomName");
        var row = SignalRow(editor);
        Assert.Contains("SomeGhostedCustomName", row.LayerChoiceOptions);

        int layerChoiceOptionsRaised = 0;
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ParameterRowViewModel.LayerChoiceOptions))
                layerChoiceOptionsRaised++;
        };

        row.SelectedLayerChoice = "Bottom Copper (1 oz)";

        Assert.Equal(1, layerChoiceOptionsRaised);
        Assert.DoesNotContain("SomeGhostedCustomName", row.LayerChoiceOptions);
    }

    // ── R-tec-9's "(Default)" way back — gate 9 ──────────────────────────────────────────────────

    [Fact]
    public void PickingDefault_AfterAnOverride_ClearsTheExpression_ReturnsToAutomatic()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var (_, comp, editor) = MakeMlin(schematicDir, signalLayer: "Bottom Copper (1 oz)");
        var row = SignalRow(editor);
        Assert.Equal("Bottom Copper (1 oz)", row.SelectedLayerChoice);

        row.SelectedLayerChoice = "(Default)";

        Assert.Equal("", comp.Parameters.Single(p => p.Name == "SignalLayer").Expression);
        Assert.Equal("(Default)", row.SelectedLayerChoice);
    }

    // ── Gate 9's other half: a later stackup edit is picked up again on refresh ─────────────────

    [Fact]
    public void RefreshFromModel_ReResolvesOptions_AfterATechnologyEditOnDisk()
    {
        var pcb = StarterTechnologies.Pcb2Layer();
        var schematicDir = WriteWorkspaceWithTech(pcb);
        var (_, _, editor) = MakeMlin(schematicDir);
        var row = GroundRow(editor);

        Assert.Equal(new[] { "(Default)", "Bottom Copper (1 oz)" }, row.LayerChoiceOptions);

        // Mark the top conductor as ALSO a ground reference and save — a stackup edit made while
        // the editor is open (mirrors TechEditorViewModel's own SetLive/save path).
        var edited = StarterTechnologies.Pcb2Layer();
        edited.Stackup.Layers.Single(l => l.Name == "Top Copper (1 oz)").IsGroundReference = true;
        TechPersistence.SaveToFile(Path.Combine(_root, "tech", "t.ctech"), edited);

        row.RefreshFromModel();

        Assert.Equal(
            new[] { "(Default)", "Top Copper (1 oz)", "Bottom Copper (1 oz)" },
            row.LayerChoiceOptions);
    }

    // ── R-tec-9's UI-side informational surfacing of a stale/unknown name ──────────────────────

    [Fact]
    public void UnknownTypedLayerName_SurfacesAnInformationalWarning_NamingTheBadValue()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var (_, _, editor) = MakeMlin(schematicDir, signalLayer: "NoLongerExists");
        var row = SignalRow(editor);

        Assert.True(row.HasLayerChoiceMissingWarning);
        Assert.Contains("NoLongerExists", row.LayerChoiceMissingWarning);
    }

    [Fact]
    public void KnownLayerName_NoWarning()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var (_, _, editor) = MakeMlin(schematicDir, signalLayer: "Bottom Copper (1 oz)");
        Assert.False(SignalRow(editor).HasLayerChoiceMissingWarning);
    }

    [Fact]
    public void DirectStagedExpressionMutation_KeepsSelectedLayerChoiceAndWarningInSync()
    {
        // There is no text field in the UI anymore for a layer-choice row (the ComboBox is the ONLY
        // editing surface) — this exercises the underlying property-notification wiring directly
        // rather than any real UI gesture, proving SelectedLayerChoice/HasLayerChoiceMissingWarning
        // never go stale regardless of how StagedExpression ends up changing.
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var (_, _, editor) = MakeMlin(schematicDir);
        var row = SignalRow(editor);

        row.StagedExpression = "Bottom Copper (1 oz)";
        Assert.Equal("Bottom Copper (1 oz)", row.SelectedLayerChoice);
        Assert.False(row.HasLayerChoiceMissingWarning);

        row.StagedExpression = "SomeStaleName";
        Assert.Equal("SomeStaleName", row.SelectedLayerChoice);
        Assert.True(row.HasLayerChoiceMissingWarning);
    }

    // ── The actual bug behind "it's a little buggy right now": a value that arrives some other way
    // (the schematic canvas's own inline label text-edit is the escape hatch this brief now points
    // users to) must never leave the picker showing a BLANK selection. ────────────────────────────

    [Fact]
    public void ExternalEdit_SimulatingTheCanvasInlineLabelEdit_GhostsTheCustomValue_ComboNeverBlank()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var (vm, comp, editor) = MakeMlin(schematicDir);
        var row = SignalRow(editor);
        var param = comp.Parameters.Single(p => p.Name == "SignalLayer");

        // The inline canvas label edit commits through the exact same EditParameterCommand a picker
        // selection uses — it just originates from the canvas instead of this dialog.
        vm.Execute(new EditParameterCommand(vm.EditModel, param, "MyCustomLayerName", param.Unit));

        // ParameterEditorViewModel.OnModelChanged reacts to EditModel.Changed and calls
        // RefreshFromModel() on every existing row (the visible parameter NAME set is unchanged) —
        // no manual row.RefreshFromModel() call should be needed here.
        Assert.Contains("MyCustomLayerName", row.LayerChoiceOptions);
        Assert.Equal("MyCustomLayerName", row.SelectedLayerChoice);
        Assert.True(row.HasLayerChoiceMissingWarning);
        Assert.Contains("MyCustomLayerName", row.LayerChoiceMissingWarning);
    }

    [Fact]
    public void LayerChoiceOptions_NeverOmitsTheCurrentlyStagedValue_EvenWhenUnknown()
    {
        var schematicDir = WriteWorkspaceWithTech(StarterTechnologies.Pcb2Layer());
        var (_, _, editor) = MakeMlin(schematicDir, signalLayer: "SomeRenamedOrForeignLayer");
        var row = SignalRow(editor);

        // The combo's SelectedItem binding requires a literal match in ItemsSource or Avalonia
        // renders a blank selection — this is what guarantees it never does.
        Assert.Contains(row.SelectedLayerChoice, row.LayerChoiceOptions);
    }
}
