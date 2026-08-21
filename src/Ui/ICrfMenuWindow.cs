namespace CircuitRF.Ui;

/// <summary>
/// A top-level circuitRF window that is <b>not</b> a dock host but still belongs in the
/// <b>Window</b> menu.
/// </summary>
/// <remarks>
/// <b>Owner, 2026-08-20:</b> <i>"Can we make the Match Designer a non-modal window? And have it show
/// up in the circuitRF Window menu (just like any other window?)"</i> — the first half was already
/// true (<c>MatchDesignerWindow.Show</c> calls <c>Show</c>, never <c>ShowDialog</c>); the second was
/// not, and could not be: <c>WorkspaceViewModel.EnumerateWindowEntries</c> listed the shell plus the
/// floating <c>CrfHostWindow</c>s, and a Designer is neither.
///
/// <para><b>An interface rather than a type check</b>, for one reason worth the file: the menu is
/// built in a view-model, and the alternative is a growing <c>OfType&lt;…&gt;</c> list of concrete
/// window classes there — a place a NEW window is silently absent from until somebody remembers to
/// add it. Implementing this is the whole registration; the header a window wants in the menu is the
/// only thing it has to decide.</para>
/// </remarks>
public interface ICrfMenuWindow
{
    /// <summary>What this window is called in the <b>Window</b> menu.</summary>
    string WindowMenuHeader { get; }
}
