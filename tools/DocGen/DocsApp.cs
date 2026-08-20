using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using CircuitRF.Ui;

namespace CircuitRF.DocGen;

/// <summary>
/// The Application object the docs factory renders under. It is deliberately NOT circuitRF's own
/// <see cref="App"/>: that one's OnFrameworkInitializationCompleted opens windows, restores the last
/// workspace and loads installed PDKs, none of which a figure generator wants. What a figure DOES
/// need is the identical style and resource set, and those already live in one place for exactly
/// this reason (Styles/CircuitRfResources.axaml and Styles/CircuitRfStyles.axaml carry a header
/// saying so), so this third Application merges the same two files rather than restating anything.
///
/// Built in C# rather than XAML so tools/DocGen needs no Avalonia XAML compiler of its own.
/// </summary>
public sealed class DocsApp : Application
{
    /// <summary>The theme brushes the black-alpha remap re-pointed, for the generator's report.</summary>
    public static System.Collections.Generic.IReadOnlyList<string> RemapReport { get; private set; } = [];

    public override void Initialize()
    {
        var baseUri = new Uri("avares://CircuitRF.Ui/");

        Resources.MergedDictionaries.Add(
            new ResourceInclude(baseUri) { Source = new Uri("avares://CircuitRF.Ui/Styles/CircuitRfResources.axaml") });

        Styles.Add(
            new StyleInclude(baseUri) { Source = new Uri("avares://CircuitRF.Ui/Styles/CircuitRfStyles.axaml") });

        // The docs-only paint remap (see UiArtworkGenerator / SvgPostPass): Skia's SVG device drops
        // `fill` AND `fill-opacity` when the colour is pure black, so Fluent's #33000000 button
        // background would serialise as an opaque black slab. Merged LAST so it wins.
        Resources.MergedDictionaries.Add(
            CircuitRF.Ui.Diagnostics.DocsPaintRemap.Build(this, out var remapped));
        RemapReport = remapped;

        // The ViewLocator maps view-models to views exactly as the running app does, so a fixture
        // may hand a document view-model straight to a ContentControl.
        DataTemplates.Add(new ViewLocator());
    }
}
