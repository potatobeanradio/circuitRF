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

        // Every DataTemplate the running application declares, taken from the running application's
        // own App.axaml rather than restated here.
        //
        // The ViewLocator alone is not enough. It maps CircuitRF.Ui.ViewModels.XViewModel to
        // CircuitRF.Ui.Views.XView by name, which covers ordinary MVVM pairs — but every Dock tool
        // and document view-model (ProjectTreeTool -> ProjectTreeView, SchematicDocument ->
        // SchematicView, LayoutDocument -> LayoutEditorView, ...) is named nothing like its view and
        // is mapped by an explicit template. Without them the workspace capture rendered every dock
        // panel as the LITERAL TEXT of its view-model's type name, and reported nothing.
        //
        // Copied from a real App instance because a second hand-maintained list of nineteen
        // templates is a list that drifts. Constructing App does NOT start the application:
        // Initialize is only AvaloniaXamlLoader.Load(this), and it is
        // OnFrameworkInitializationCompleted — never called here — that opens windows, restores the
        // last workspace and loads installed PDKs.
        var app = new App();
        app.Initialize();
        foreach (var template in app.DataTemplates) DataTemplates.Add(template);
    }
}
