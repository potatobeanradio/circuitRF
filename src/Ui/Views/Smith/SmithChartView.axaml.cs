using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.Smith;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Views.Smith;

/// <summary>
/// The Smith Chart document's view — the generator panel, the chrome and the status strip
/// (brief-smith-4-document-window.md; <c>docs/design/smith-chart.md</c> §5.2, §5.3).
/// </summary>
/// <remarks>
/// <b>This is a <c>UserControl</c> and there is deliberately no <c>Window</c> subclass in this
/// folder.</b> <c>R-smith4-1</c> makes the Smith Chart a docked document rather than an application:
/// the tab, the dirty mark, Save / Save All / close-time prompting, Ctrl/Cmd+Z, tear-off, the Window
/// Layout and restore-on-reopen are all the shell's, inherited and not re-implemented. A
/// <c>Window</c> appearing here would mean that requirement had been missed.
///
/// <para>What is left for the code-behind is the two things a view model must not do: take keyboard
/// focus when the tab is activated, and open a file picker. The import's READ is
/// <see cref="SmithGeneratorImport"/>'s, below the firewall, reached through
/// <see cref="SmithChartViewModel.ImportGeneratorFrom"/> — which takes a resolved path, so the whole
/// import is drivable by the gate with no display.</para>
/// </remarks>
public partial class SmithChartView : UserControl
{
    private SmithChartDocument? _doc;

    public SmithChartView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_doc is not null) _doc.ActivationFocusRequested -= OnActivationFocusRequested;

        _doc = DataContext as SmithChartDocument;
        if (_doc is null) return;

        _doc.ActivationFocusRequested += OnActivationFocusRequested;
        ApplySplitFractions();

        // The request may have been made BEFORE this view existed — a document that is already the
        // active dockable at the instant its view is first realized. ConsumeActivationFocus is what
        // closes that race; see IActivatableDocument.
        if (_doc.ConsumeActivationFocus()) FocusSelf();
    }

    private void OnActivationFocusRequested() => FocusSelf();

    /// <summary>
    /// Takes the keyboard for this document.
    /// </summary>
    /// <remarks>
    /// The control focused is this one rather than a pane, because neither pane exists yet — briefs 5
    /// and 6 own them, and each will want the focus for its own shortcuts. Focusing the document
    /// root now is what makes the shell's own accelerators work on a freshly-activated tab without a
    /// preliminary click, which is the whole point of <c>IActivatableDocument</c>.
    /// </remarks>
    private void FocusSelf() => Focus(NavigationMethod.Tab);

    // ── the two splitters (R-smith4-3) ───────────────────────────────────────

    /// <summary>
    /// Puts the document's stored split fractions onto the grid definitions.
    /// </summary>
    /// <remarks>
    /// <b>In code, not as a binding, and the reason is a silent failure rather than a preference.</b>
    /// A <c>RowDefinition</c> is not in the logical tree: it inherits no DataContext, so a binding on
    /// its <c>Height</c> resolves against nothing and simply never applies — the splitter would move
    /// and the document would remember nothing, with no error anywhere. Nothing else in this
    /// application binds a definition's size, and this window is not the place to find out why.
    /// </remarks>
    private void ApplySplitFractions()
    {
        if (_doc is null) return;

        double side = _doc.ViewModel.SplitterSide;
        double main = _doc.ViewModel.SplitterMain;

        TopRegion.ColumnDefinitions[0].Width = new GridLength(side,       GridUnitType.Star);
        TopRegion.ColumnDefinitions[2].Width = new GridLength(1.0 - side, GridUnitType.Star);
        RootGrid.RowDefinitions[0].Height    = new GridLength(main,       GridUnitType.Star);
        RootGrid.RowDefinitions[2].Height    = new GridLength(1.0 - main, GridUnitType.Star);
    }

    /// <summary>
    /// A splitter was released — record where it was left.
    /// </summary>
    /// <remarks>
    /// <b>On release, not on every frame of the drag.</b> One handler for both splitters, because
    /// the honest answer to "where are the dividers" is read off the definitions rather than
    /// accumulated from deltas — a <c>GridSplitter</c> rewrites BOTH definitions it sits between, and
    /// a star value is only meaningful against its neighbour.
    ///
    /// <para>This writes into the document's <c>View</c> block and pushes NO undo entry and no dirty
    /// mark; the position rides along on the next real save. See
    /// <see cref="SmithChartViewModel.SplitterSide"/>.</para>
    /// </remarks>
    private void OnSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (_doc is null) return;

        _doc.ViewModel.SplitterSide = Fraction(TopRegion.ColumnDefinitions[0].Width,
                                               TopRegion.ColumnDefinitions[2].Width);
        _doc.ViewModel.SplitterMain = Fraction(RootGrid.RowDefinitions[0].Height,
                                               RootGrid.RowDefinitions[2].Height);

        static double Fraction(GridLength first, GridLength second)
        {
            double a = first.IsStar  ? first.Value  : 0.0;
            double b = second.IsStar ? second.Value : 0.0;
            return a + b > 0 ? a / (a + b) : 0.5;
        }
    }

    /// <summary>
    /// Import <c>.s1p</c>… — the picker, and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>A one-port file only</b>, which is the filter's own statement of what the generator's
    /// impedance comes from; an <c>.s2p</c> handed to this dialog is far more likely to be the wrong
    /// file than a deliberate request for its input reflection, and
    /// <see cref="SmithGeneratorImport.Read"/> refuses it by name rather than reading S₁₁ out of the
    /// corner of it.
    ///
    /// <para>The refusal lands in the status strip, with the file's name in it. That is the house
    /// rule (§5.3) and it is also the only place a background tab could report one.</para>
    /// </remarks>
    private async void OnImportS1pClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_doc is null || TopLevel.GetTopLevel(this) is not { } top) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Import generator impedance",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("One-port Touchstone") { Patterns = ["*.s1p", "*.S1P"] },
                new FilePickerFileType("All files")           { Patterns = ["*"] },
            ],
        });

        if (files.Count == 0) return;

        string? error = _doc.ViewModel.ImportGeneratorFrom(files[0].Path.LocalPath);
        _doc.ViewModel.ImportFailed = error;
    }
}
