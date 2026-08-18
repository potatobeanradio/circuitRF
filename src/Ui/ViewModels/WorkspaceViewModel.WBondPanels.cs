using System.Linq;
using CircuitRF.Ui.Docking;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Showing the two wirebond panels — <b>arranged, the first time this installation ever needs
/// them</b> (owner, 2026-08-17: <i>"I want the user to see the Array Inductance and Wire Profile the
/// first time they use a wBond component in a workspace"</i>).
///
/// <h3>Why an arrangement and not just "open them"</h3>
/// <para><see cref="WorkspaceViewModel.ShowToolPanel"/> opens a panel it cannot find in the tree as a
/// FLOATING window over the middle of the shell — the only answer available with nowhere remembered.
/// Two of those, over the layout the user has just generated, is not "here are your wirebond panels";
/// it is two windows to move before any work can start. The owner supplied the arrangement they
/// actually work in as a <c>.cws</c>, and this transcribes the two panels' placement out of it.</para>
///
/// <h3>Per WORKSPACE, and the layout itself is the record — not a preference</h3>
/// <para>This was gated on a per-installation preference (<c>wbond_panels_arranged</c>), and that was
/// the wrong scope (owner, 2026-08-17: a NEW workspace, first wBond in it, both panels floating). <b>A
/// panel's home is per-workspace; the flag was per-user.</b> So the second workspace on a machine found
/// the flag already spent, fell through to plain <see cref="WorkspaceViewModel.ShowToolPanel"/>, and
/// that method's only answer for a panel with nowhere remembered is to float it — which is precisely
/// the state the arrangement exists to avoid. The flag could only ever be right for the first workspace
/// a person ever opened.</para>
///
/// <para><b>The gate that is actually correct was already here</b>, one level down and per panel:
/// <see cref="IsPlacedAnywhere"/> — this layout already names the panel, so leave it exactly where it
/// is and merely open it. That is self-limiting in every workspace independently, needs nothing
/// remembered between runs, and gives the right answer in the three cases a preference cannot tell
/// apart: a fresh workspace (place them), a workspace where the user has arranged them (leave them), and
/// a workspace arriving from someone else (leave them — their layout names the panels, so it is
/// indistinguishable from the second case, which is the point).</para>
///
/// <h3>Only the two panels</h3>
/// <para>Nothing else in the workspace's layout is touched: the two panels are inserted into the
/// arrangement the user already has, rather than the reference layout being applied wholesale over it.
/// The reference <c>.cws</c> also described a project tree, a palette, a Messages pane and a document
/// order, and none of that is this command's business.</para>
/// </summary>
public partial class WorkspaceViewModel
{
    /// <summary>
    /// The Array Inductance column's share of the document row, transcribed from the owner's own
    /// <c>.cws</c> — a narrow strip between the tool columns and the documents.
    ///
    /// <para>Taken from the PANEL's recorded proportion (0.1886), not from that file's inboard
    /// <c>Sides</c> entry (0.8). For a column holding one tool dock beside the documents, the panel's
    /// proportion IS its share of that horizontal split — <c>DockLayoutCapture</c> says so where it
    /// falls back to the tool dock's own proportion for exactly this shape — while the 0.8 is the
    /// proportion of the container that holds the column AND the documents together. Transcribing
    /// that one would open the panel across four fifths of the window.</para>
    /// </summary>
    private const double WBondInductanceWidth = 0.1886;

    /// <summary>
    /// The left column's share when this workspace has no left panels at all to join — the Wire
    /// Profile then becomes that column, at the same proportion the reference file's lower group had.
    /// </summary>
    private const double WBondProfileFallbackProportion = 0.534;

    /// <summary>
    /// Shows the Wire Profile and Array Inductance panels, placing either one this workspace has no
    /// home for.
    /// </summary>
    internal void ShowWBondPanels()
    {
        // The fallback is for a workspace with no LIVE layout to insert into at all (a headless
        // workspace, a shell mid-rebuild). It floats them, which is all ShowToolPanel can do — but it
        // now runs only where there is genuinely no dock tree, rather than on every workspace after
        // the first.
        if (!TryArrangeWBondPanels())
        {
            ShowToolPanel(DockPanelIds.WBondProfile);
            ShowToolPanel(DockPanelIds.WBondInductance);
        }
    }

    /// <summary>
    /// Inserts the two panels into the LIVE arrangement and applies it. Returns false when there is
    /// no live arrangement to insert into, so the caller can fall back to opening them plainly.
    /// </summary>
    private bool TryArrangeWBondPanels()
    {
        if (CaptureDockLayout() is not { } live) return false;

        ArrangeWBondPanels(live);
        ApplyDockLayout(live);
        return true;
    }

    /// <summary>
    /// Places the two panels into <paramref name="live"/>. Separated from the capture-and-apply above
    /// so the ARRANGEMENT can be tested against a layout value, with no shell, no factory and — the
    /// part that matters — no writing to this machine's real <c>preferences.json</c>.
    /// </summary>
    internal static void ArrangeWBondPanels(CwsDockLayout live)
    {
        // A panel the user has ALREADY placed by hand — they found it in View ▸ Panels before ever
        // generating a wBond — is left exactly where they put it, and only opened. Moving a panel
        // somebody has positioned is worse than never having arranged it for them.
        if (!IsPlacedAnywhere(live, DockPanelIds.WBondProfile)) AddProfilePanel(live);
        else Open(live, DockPanelIds.WBondProfile);

        if (!IsPlacedAnywhere(live, DockPanelIds.WBondInductance)) AddInductancePanel(live);
        else Open(live, DockPanelIds.WBondInductance);
    }

    /// <summary>Whether this layout already names <paramref name="panelId"/> somewhere — docked or floating.</summary>
    private static bool IsPlacedAnywhere(CwsDockLayout live, string panelId) =>
        live.Panels.Any(p => p.Id == panelId) || live.FloatingWindows.Any(w => w.Panels.Contains(panelId));

    /// <summary>Opens a panel this layout already places, leaving its placement untouched.</summary>
    private static void Open(CwsDockLayout live, string panelId)
    {
        if (live.Panels.FirstOrDefault(p => p.Id == panelId) is { } panel) panel.Open = true;
    }

    /// <summary>
    /// The Wire Profile joins the LEFT column's lower group — the one holding the Properties
    /// inspector in the owner's file — as its front tab.
    ///
    /// <para>Tabbed rather than given a row of its own because that is what the reference does, and
    /// because it is the panel you look at while editing one wire: it belongs beside the inspector
    /// showing that wire's numbers, not in competition with it for height.</para>
    /// </summary>
    private static void AddProfilePanel(CwsDockLayout live)
    {
        var leftOuter = live.Panels.Where(p => p.Side == DockSide.Left && !p.Inboard && p.Open).ToList();

        var host = leftOuter.FirstOrDefault(p => p.Id == DockPanelIds.Properties)
                ?? leftOuter.OrderByDescending(p => p.Group).FirstOrDefault();

        int group = host?.Group ?? 0;
        double proportion = host?.Proportion ?? WBondProfileFallbackProportion;

        var siblings = leftOuter.Where(p => p.Group == group).ToList();
        foreach (var sibling in siblings) sibling.Active = false;

        live.Panels.Add(new CwsDockPanel
        {
            Id = DockPanelIds.WBondProfile,
            Open = true,
            Side = DockSide.Left,
            Inboard = false,
            Group = group,
            Order = siblings.Count == 0 ? 0 : siblings.Max(p => p.Order) + 1,
            Active = true,          // the front tab — it is what the user was just told to look at
            Proportion = proportion,
        });
    }

    /// <summary>
    /// The Array Inductance goes in its own column BETWEEN the tool columns and the documents, which
    /// is where the owner keeps it: it is a table read against the layout beside it, so it wants to be
    /// adjacent to the canvas rather than at the far edge of the window.
    /// </summary>
    private static void AddInductancePanel(CwsDockLayout live)
    {
        live.Panels.Add(new CwsDockPanel
        {
            Id = DockPanelIds.WBondInductance,
            Open = true,
            Side = DockSide.Left,
            Inboard = true,
            Group = 0,
            Order = 0,
            Active = true,
            Proportion = WBondInductanceWidth,
        });

        // The COLUMN's width lives on the side entry, not on the panel — see WBondInductanceWidth and
        // CwsDockSide.Inboard. An existing entry is left alone: the user already has an inboard column
        // there and its width is theirs.
        if (!live.Sides.Any(s => s.Side == DockSide.Left && s.Inboard))
            live.Sides.Add(new CwsDockSide
            {
                Side = DockSide.Left,
                Inboard = true,
                Proportion = WBondInductanceWidth,
            });
    }
}
