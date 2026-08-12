using System;
using System.IO;
using Avalonia.Controls;
using CircuitRF.Ui.Harmonica;

namespace CircuitRF.Ui.Views.Harmonica;

/// <summary>
/// The standalone binary's shell: one plain <see cref="Window"/> around one
/// <see cref="HarmonicaView"/>, with no Dock and no workspace (R-h8-7, R-h8-8).
///
/// <para><b>Several documents means several windows.</b> Stated because R-h8-8 asks. Tabs would need
/// a document shell — a tab strip, an active-document notion, per-tab dirty state and a close
/// prompt — which is precisely the machinery this binary exists to do without; one window per
/// document is what a plain Window already gives, and the OS window list is then the document list.
/// </para>
/// </summary>
public partial class HarmonicaShellWindow : Window
{
    public HarmonicaShellWindow()
    {
        InitializeComponent();
        DataContext = new HarmonicaDocument("harmonicaRF", new HarmonicaDocumentViewModel());
        UpdateTitle();

        // The document owns the dirty bullet; the window title mirrors it, the same way a docked tab
        // does — so a standalone window and a docked tab say the same thing about the same document.
        Document.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HarmonicaDocumentViewModel.IsDirty)) UpdateTitle();
        };
    }

    /// <summary>The one document this window shows.</summary>
    public HarmonicaDocument Document => (HarmonicaDocument)DataContext!;

    /// <summary>
    /// Opens a <c>.charm</c> into THIS window's document — the double-click path. Reuses the view's
    /// own loader so a file opened by double-click and one opened from File ▸ Open take the same
    /// route, including the §8.1 unresolved-reference report.
    /// </summary>
    public void OpenCharm(string path)
    {
        View.LoadCharmFile(path);
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        string name = Document.FilePath is { } p
            ? Path.GetFileNameWithoutExtension(p)
            : "Untitled";
        Title = (Document.ViewModel.IsDirty ? "• " : "") + name + " — harmonicaRF";
    }
}
