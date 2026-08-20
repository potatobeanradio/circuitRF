using System;
using System.Linq;
using Avalonia.Controls;
using CircuitRF.Core.Matching;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.Match;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The Match Designer, on a real synthesised design.
///
/// <para>Two designs, both synthesised here and now by the shipped code: the one a freshly placed
/// <c>Match</c> carries (50 Ω to 10 Ω over 1.8-2.2 GHz, with a Norton solution already applied), and
/// the interstage problem the synthesis is gated on (200 Ω ‖ 0.125 pF to 1.25 Ω + 10 pF over
/// 3.3-5.0 GHz). Every element value, every response curve and every status number in these figures
/// is what the synthesis computes, not a mock-up of what it would compute.</para>
/// </summary>
public static class DocMatchFixtures
{
    /// <summary>A schematic holding one freshly placed Match, seeded exactly as placement seeds it.</summary>
    private static (SchematicViewModel Vm, EditableComponent Comp) Placed(MatchDesign? design = null)
    {
        var comp = new EditableComponent { InstanceName = "MN1", Symbol = SymbolKind.Match, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Match, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name,
                Expression = dp.Expression,
                Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic,
            });

        if (design is not null)
        {
            Set(comp, "Design", MatchEmbedding.Encode(design));
            Set(comp, "F1", (design.F1 / 1e9).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set(comp, "F2", (design.F2 / 1e9).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set(comp, "Order", design.Order.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set(comp, "R1", design.Term1.R.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Set(comp, "R2", design.Term2.R.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var model = new SchematicEditModel();
        model.Components.Add(comp);
        return (new SchematicViewModel(model), comp);
    }

    /// <summary>Overwrite one already-seeded parameter, or fail rather than silently adding a second.</summary>
    private static void Set(EditableComponent comp, string name, string expression)
    {
        var p = comp.Parameters.FirstOrDefault(x => x.Name == name)
            ?? throw new InvalidOperationException(
                $"A placed Match no longer declares a '{name}' parameter, so the documentation figure "
              + "cannot state its design.");
        p.Expression = expression;
    }

    /// <summary>
    /// The §4.9 interstage problem: a stage's 200 Ω ‖ 0.125 pF output into the next stage's
    /// 1.25 Ω + 10 pF input, over 3.3-5.0 GHz. This is the synthesis's own golden case.
    /// </summary>
    public static MatchDesign Interstage()
    {
        var design = new MatchDesign
        {
            F1 = 3.3e9,
            F2 = 5.0e9,
            Order = 4,
            Response = ResponseShape.ChebyshevFano,
            Term1 = new Termination(200.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
            Term2 = new Termination(1.25,  ReactanceKind.C, TerminationTopology.Series,   10e-12),
        };

        // A 200 ohm to 1.25 ohm transformation needs Norton transforms to REACH: synthesised bare,
        // the far end sits at 1.68 ohm and the panel flags termination 1 red. The figure applies the
        // search's own first-ranked solution — the same list the Designer offers and the same
        // deterministic ranking — so the picture is of a design that is actually matched, which is
        // what the worked example in the chapter walks through.
        var set = MatchSolutionSearch.Search(design, includeQAdjust: false);
        var pick = set.Solutions.FirstOrDefault(s => s.QAdjust == 0.0 && !s.ImplausibleValues)
            ?? throw new InvalidOperationException(
                "The solution search found no plain solution for the interstage example, so the "
              + "worked-example figure would show an unmatched design.");

        design.Transforms = [.. pick.Transforms];
        design.AppliedSolutions.Add(pick.Fingerprint);
        return design;
    }

    /// <summary>The Designer, wired to that instance and refreshed — spec, ladder, plots, solutions.</summary>
    private static MatchDesignerViewModel DesignerVm(MatchDesign? design = null)
    {
        var (schematic, comp) = Placed(design);
        var vm = new MatchDesignerViewModel();
        vm.SetTarget(schematic, comp);

        if (vm.Elements.Count == 0)
            throw new InvalidOperationException(
                "The Match Designer produced no ladder for the design a freshly placed Match carries, "
              + "so every figure on this page would show an empty network pane.");

        return vm;
    }

    /// <summary>The whole Designer window: specification, network, response, transforms, status.</summary>
    public static FigureScene Designer()
    {
        var vm = DesignerVm();
        var window = new MatchDesignerWindow { DataContext = vm };
        var content = window.Content as Control
            ?? throw new InvalidOperationException("MatchDesignerWindow has no content control to capture.");

        window.Content = null;   // detach, so the figure is the panel and not a nested window
        content.DataContext = vm;
        return new FigureScene(content);
    }

    /// <summary>The interstage worked example, solved: the ladder, its values and its response.</summary>
    public static FigureScene Interstage2Stage()
    {
        var vm = DesignerVm(Interstage());
        var window = new MatchDesignerWindow { DataContext = vm };
        var content = window.Content as Control
            ?? throw new InvalidOperationException("MatchDesignerWindow has no content control to capture.");

        window.Content = null;
        content.DataContext = vm;
        return new FigureScene(content);
    }

    /// <summary>The solutions list, opened — the alternative transform sets, simplest first.</summary>
    public static FigureScene Solutions()
    {
        var vm = DesignerVm();
        vm.SolutionsPanelOpen = true;

        var window = new MatchDesignerWindow { DataContext = vm };
        var content = window.Content as Control
            ?? throw new InvalidOperationException("MatchDesignerWindow has no content control to capture.");

        window.Content = null;
        content.DataContext = vm;
        return new FigureScene(content);
    }
}
