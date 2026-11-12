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
/// The RF Chain Budget window, captured from the live window on a chain of the "RF Chain Budget" example: the chain's
/// blocks (the window's first tab), its stages and its cascade table. Hosted as <see cref="DocSignalFixtures"/> hosts the Signal Designer — the
/// window's content with its own styles moved onto it.
/// </summary>
public static class DocChainFixtures
{
    /// <summary>The example's four-element steered array.</summary>
    public static FigureScene Array() => Window("Array");

    /// <summary>The example's 26 GHz link, on its Frequency sweep tab.</summary>
    public static FigureScene Link() => Window("26 GHz Link", "Frequency sweep");

    private static FigureScene Window(string chain, string? tab = null)
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
        var templates = window.DataTemplates.ToList();
        window.Content = null;
        window.Styles.Clear();
        window.DataTemplates.Clear();
        foreach (var style in styles) content.Styles.Add(style);
        foreach (var t in templates) content.DataTemplates.Add(t);
        if (tab is not null)
            foreach (var item in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(content).OfType<TabItem>().Where(t => Equals(t.Header, tab)))
                item.IsSelected = true;
        content.DataContext = vm;
        return new FigureScene(content);
    }
}
