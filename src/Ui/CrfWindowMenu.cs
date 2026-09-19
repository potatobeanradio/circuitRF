using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui;

/// <summary>
/// The <b>Window</b> menu for a standalone circuitRF window — every open window, selectable to
/// bring it to the front, on either menu surface.
/// </summary>
/// <remarks>
/// <b>It resolves ONE ordering, not a second one.</b> <see cref="WorkspaceViewModel.EnumerateWindowEntries"/>
/// is what the workspace's own Window menu shows, and it already answers this question properly:
/// the shell first, then torn-off documents, then floating tool panels, then standalone editor
/// windows through <see cref="ICrfMenuWindow"/>, then the other workspace windows — each in its own
/// band, each marked with the one dirty bullet the whole application uses. A second enumeration
/// written here would be a second list that disagrees with that one the first time either changes.
///
/// <para><b>Why a standalone window needs this at all:</b> railRF and the Match Designer are shown
/// UNOWNED so they can go behind the workspace they were opened from. That is the right behaviour
/// and it is also how a window gets lost — so each of them owes the user a way back, and the way
/// back is the same list the workspace offers (owner, 2026-09-19).</para>
///
/// <para>The fallback path exists for a process with no workspace window in it — a standalone
/// binary, or a test host. It lists what is open by title rather than reporting nothing.</para>
/// </remarks>
public static class CrfWindowMenu
{
    /// <summary>One row: what it is called, which window it raises, and whether a rule precedes it.</summary>
    public sealed record Entry(string Header, Window Target, bool SeparatorBefore);

    /// <summary>Every open circuitRF window, in display order.</summary>
    public static IReadOnlyList<Entry> Enumerate()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return [];

        // PlatformImpl is checked everywhere a window is listed here: a closed window can linger in
        // desktop.Windows briefly, and a stale one there once crashed Window.SortWindowsByZOrder.
        var shell = desktop.Windows
            .OfType<Views.WorkspaceWindow>()
            .Where(w => w.PlatformImpl is not null)
            .Select(w => w.DataContext)
            .OfType<WorkspaceViewModel>()
            .FirstOrDefault();

        List<Entry> entries = shell is not null
            ? [.. shell.EnumerateWindowEntries().Select(e => new Entry(e.Header, e.Target, e.SeparatorBefore))]
            : [.. desktop.Windows.Where(w => w.PlatformImpl is not null)
                                 .Select(w => new Entry(HeaderOf(w), w, SeparatorBefore: false))];

        // A rule can only ever come BETWEEN two bands. The workspace's own menu never sees a leading
        // one because its first entry is always the shell; nothing guarantees that here.
        if (entries.Count > 0 && entries[0].SeparatorBefore)
            entries[0] = entries[0] with { SeparatorBefore = false };

        return entries;
    }

    private static string HeaderOf(Window window) =>
        window is ICrfMenuWindow m && !string.IsNullOrWhiteSpace(m.WindowMenuHeader) ? m.WindowMenuHeader
        : !string.IsNullOrWhiteSpace(window.Title) ? window.Title!
        : window.GetType().Name;

    /// <summary>Ready-made items for an in-window <c>Menu</c>, bound through <c>ItemsSource</c>.</summary>
    /// <remarks>
    /// <b>Never returns an empty list.</b> An Avalonia <c>MenuItem</c> reports <c>HasSubMenu</c> from
    /// its item COUNT, so an empty <c>ItemsSource</c> turns the parent into a leaf: clicking it opens
    /// nothing, and the <c>SubmenuOpened</c> that would have refilled it can never fire. That is a
    /// self-latching dead menu; the workspace shipped it once and the disabled placeholder is its fix.
    /// </remarks>
    public static IReadOnlyList<Control> BuildItems(IReadOnlyList<Entry>? entries = null)
    {
        entries ??= Enumerate();

        var items = new List<Control>();
        foreach (var entry in entries)
        {
            if (entry.SeparatorBefore) items.Add(new Separator());

            var item   = new MenuItem { Header = entry.Header };
            var target = entry.Target;                 // capture, never the loop variable
            item.Click += (_, _) => Focus(target);
            items.Add(item);
        }

        if (items.Count == 0) items.Add(new MenuItem { Header = "(No Windows)", IsEnabled = false });
        return items;
    }

    /// <summary>
    /// Refills a macOS <c>NativeMenu</c> in place.
    /// </summary>
    /// <remarks>
    /// <b>In place, and that is an invariant rather than an economy</b> (src/Ui/CLAUDE.md): a
    /// window's <c>NativeMenu</c> INSTANCE is fixed for its lifetime, so the contents are cleared and
    /// refilled and a second <c>NativeMenu.SetMenu</c> is never called.
    /// </remarks>
    public static void Fill(NativeMenu menu, IReadOnlyList<Entry>? entries = null)
    {
        entries ??= Enumerate();

        menu.Items.Clear();
        foreach (var entry in entries)
        {
            if (entry.SeparatorBefore) menu.Items.Add(new NativeMenuItemSeparator());

            var item   = new NativeMenuItem(entry.Header);
            var target = entry.Target;
            item.Click += (_, _) => Focus(target);
            menu.Items.Add(item);
        }

        // Avalonia does not reliably sync IsEnabled=false back to AppKit once a menu has been shown,
        // so the placeholder is added disabled from the start rather than toggled — the same shape
        // "Open Recent" uses for its own empty case.
        if (menu.Items.Count == 0)
            menu.Items.Add(new NativeMenuItem("(No Windows)") { IsEnabled = false });
    }

    /// <summary>Brings a window to the front, un-minimizing it first if it needs it.</summary>
    public static void Focus(Window window)
    {
        if (window.PlatformImpl is null) return;      // already closed

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
        window.Focus();
    }
}
