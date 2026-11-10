using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using CircuitRF.Design.Chain;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Chain;
using CircuitRF.Ui.Views.Chain;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The RF Chain Budget window, captured from the live window on a chain of the "RF Chain Budget" example: the chain as
/// the schematic renderer draws it, its stages and its cascade table. Hosted as <see cref="DocSignalFixtures"/> hosts the Signal Designer — the
/// window's content with its own styles moved onto it.
/// </summary>
public static class DocChainFixtures
{
    /// <summary>The example's four-element steered array.</summary>
    public static FigureScene Array() => Window("Array");

    private static FigureScene Window(string chain)
    {
        string root = ExampleWorkspaces.ResolveRoot()
            ?? throw new InvalidOperationException("No examples/ tree beside the generator or above it, so the RF Chain Budget figure has no document.");
        string path = Path.Combine(root, "RF Chain Budget", chain + ChainDesignIo.Extension);
        var vm = new ChainBudgetViewModel(ChainDesignIo.LoadFromFile(path));
        vm.SelectedStage = vm.Stages.FirstOrDefault(s => s.Part.Token == ChainParts.Combiner);
        var window = new ChainBudgetWindow { DataContext = vm };
        var content = window.Content as Control
            ?? throw new InvalidOperationException("ChainBudgetWindow has no content control to capture.");
        var styles = window.Styles.ToList();
        window.Content = null;
        window.Styles.Clear();
        foreach (var style in styles) content.Styles.Add(style);
        content.DataContext = vm;
        return new FigureScene(content);
    }
}
