using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Dock.Model.Core;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Converters;

/// <summary>
/// Whether one of circuitRF's own entries applies to the document tab that was right-clicked —
/// bound from <c>Styles/DocumentTabContextMenu.axaml</c> to HIDE an item rather than disable it,
/// the convention the Reveal entry already set: an item that does not apply to this kind of document
/// is not an item that is temporarily unavailable.
///
/// <para><b>Visibility carries every decision the menu makes, deliberately.</b> The menu is one
/// shared <c>ContextMenu</c> whose DataContext changes as tabs are right-clicked; a converter on
/// <c>IsVisible</c> is re-evaluated on that change, which is what the Reveal item has always relied
/// on. Whether a parameterized command's <c>CanExecute</c> is re-queried at the same moment depends
/// on attachment order — and a stale grey-out on a dirty document is a save the user cannot reach,
/// with nothing on screen to explain it. So nothing here gates on dirtiness: "nothing to save" is
/// REPORTED by the route (WorkspaceViewModel.TabSave.cs), where it can say so.</para>
/// </summary>
public sealed class DocumentTabMenuVisibility : IValueConverter
{
    /// <summary>The Save item: shown on every document kind that has a Save route at all.</summary>
    public static readonly DocumentTabMenuVisibility Save = new(Kind.Save);

    /// <summary>The Save As… item: shown where the document can be written to a different file and
    /// followed there.</summary>
    public static readonly DocumentTabMenuVisibility SaveAs = new(Kind.SaveAs);

    /// <summary>The separator that closes circuitRF's block — shown when ANY of the three entries
    /// above it is, so no rule is ever left stranded at the top of Dock's own menu.</summary>
    public static readonly DocumentTabMenuVisibility Any = new(Kind.Any);

    /// <summary>The separator under Copy Path / Copy Full Path — shown when those are AND a save item
    /// follows them; otherwise the <see cref="Any"/> separator is the next rule and two would stack.</summary>
    public static readonly DocumentTabMenuVisibility AfterPaths = new(Kind.AfterPaths);

    /// <summary>The Reveal in Workspace Tree item: shown on a file of the open workspace's tree.</summary>
    public static readonly DocumentTabMenuVisibility RevealInTree = new(Kind.RevealInTree);

    /// <summary>The separator under Reveal in Workspace Tree — shown when it is AND a save item follows; otherwise the
    /// <see cref="Any"/> separator is the next rule and two would stack.</summary>
    public static readonly DocumentTabMenuVisibility AfterRevealInTree = new(Kind.AfterRevealInTree);

    private enum Kind { Save, SaveAs, Any, AfterPaths, RevealInTree, AfterRevealInTree }

    private readonly Kind _kind;

    private DocumentTabMenuVisibility(Kind kind) => _kind = kind;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var dockable = value as IDockable;
        bool anySave = WorkspaceViewModel.HasSaveRoute(dockable) || WorkspaceViewModel.HasSaveAsRoute(dockable);
        return _kind switch
        {
            Kind.Save   => WorkspaceViewModel.HasSaveRoute(dockable),
            Kind.SaveAs => WorkspaceViewModel.HasSaveAsRoute(dockable),
            Kind.RevealInTree => WorkspaceViewModel.HasTreeRevealRoute(dockable),
            Kind.AfterRevealInTree => anySave && WorkspaceViewModel.HasTreeRevealRoute(dockable),
            Kind.AfterPaths => FileBackedDocumentConverter.Instance.Convert(
                                   value, targetType, parameter, culture) is true
                               && (anySave || WorkspaceViewModel.HasTreeRevealRoute(dockable)),
            _           => anySave
                           || WorkspaceViewModel.HasTreeRevealRoute(dockable)
                           // the Reveal item's own rule, asked of the converter that states it
                           // rather than restated here — one file-backed test, not two.
                           || FileBackedDocumentConverter.Instance.Convert(
                                  value, targetType, parameter, culture) is true,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
