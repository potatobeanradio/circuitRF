using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels.Dock;
using Dock.Model.Core;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// The <b>Window</b> menu: every open circuitRF window, selectable to bring it to the front.
///
/// <para>Order is deliberate and fixed: the workspace window first (it is the anchor of the whole
/// session), then any torn-off DOCUMENT windows, then a separator, then any floating TOOL windows.
/// Documents are the user's own work and tools are chrome, so tools sit below the rule.</para>
///
/// <para>A dirty entry is marked with the same leading bullet the document tabs already use
/// (<see cref="DirtyMark"/>), so the menu and the tab strip cannot disagree about what "unsaved"
/// looks like.</para>
///
/// <para>Built as a live <see cref="ObservableCollection{T}"/> of ready-made controls bound through
/// <c>ItemsSource</c>, plus an event the macOS <c>NativeMenu</c> listens to — the exact shape
/// "Open Recent" already established (<c>RecentMenuItems</c> / <c>RecentWorkspacesChanged</c>), so
/// both menu surfaces stay in sync from one rebuild.</para>
/// </summary>
public partial class WorkspaceViewModel
{
    /// <summary>Same marker the document tab titles use for unsaved work.</summary>
    public const string DirtyMark = "• ";

    /// <summary>Menu items for the in-window <c>Window</c> menu (bound via <c>ItemsSource</c>).</summary>
    public ObservableCollection<Control> WindowMenuItems { get; } = new();

    /// <summary>Raised after every rebuild so the macOS NativeMenu can mirror the same entries.</summary>
    public event Action? WindowMenuChanged;

    /// <summary>One entry in the Window menu, resolved independently of any menu framework.</summary>
    public sealed record WindowMenuEntry(string Header, Window Target, bool SeparatorBefore);

    /// <summary>
    /// Rebuilds <see cref="WindowMenuItems"/> and notifies the native-menu mirror.
    ///
    /// <para>Called on demand — immediately before either menu surface opens — rather than tracked
    /// incrementally. Window lifetime here is driven by Dock (tear-off, re-dock, close) and by dirty
    /// state that changes on every keystroke; subscribing to all of that would be a lot of
    /// bookkeeping to keep a menu correct that is only ever read at the moment it is opened.</para>
    /// </summary>
    public void RebuildWindowMenuItems()
    {
        WindowMenuItems.Clear();

        foreach (var entry in EnumerateWindowEntries())
        {
            if (entry.SeparatorBefore)
                WindowMenuItems.Add(new Separator());

            var item = new MenuItem { Header = entry.Header };
            var target = entry.Target;                 // capture, never the loop variable
            item.Click += (_, _) => FocusWindow(target);
            WindowMenuItems.Add(item);
        }

        // NEVER leave this collection empty.
        //
        // An Avalonia MenuItem reports HasSubMenu from its item COUNT, so an empty ItemsSource makes
        // "Window" a leaf: clicking it opens nothing, and SubmenuOpened — where the rebuild is
        // driven from — can never fire. That is a self-latching dead menu, and it is exactly the bug
        // this guard fixes (the menu appeared but was permanently empty). The same disabled-placeholder
        // shape "Open Recent" already uses for its own empty case.
        if (WindowMenuItems.Count == 0)
            WindowMenuItems.Add(new MenuItem { Header = "(No Windows)", IsEnabled = false });

        WindowMenuChanged?.Invoke();
    }

    /// <summary>
    /// The Window menu's contents, in display order. Public so the macOS NativeMenu builder and the
    /// tests can consume the same resolution the in-window menu uses — there is one ordering rule,
    /// not two.
    /// </summary>
    public IReadOnlyList<WindowMenuEntry> EnumerateWindowEntries()
    {
        var entries = new List<WindowMenuEntry>();
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return entries;

        // 1. The workspace window itself, first.
        var shell = desktop.Windows.FirstOrDefault(w => ReferenceEquals(w.DataContext, this));
        if (shell is not null)
            entries.Add(new WindowMenuEntry(ShellHeader(), shell, SeparatorBefore: false));

        // 2/3. Floating windows, split document-first then tool. A closed window can linger in
        // desktop.Windows briefly, so PlatformImpl is checked (the same guard RaiseFloatingToolWindows
        // uses — a stale entry there once crashed Window.SortWindowsByZOrder).
        var floats = desktop.Windows
            .OfType<CrfHostWindow>()
            .Where(w => w.PlatformImpl is not null)
            .ToList();

        foreach (var w in floats.Where(w => !w.FloatsAnyTool()))
            entries.Add(new WindowMenuEntry(FloatHeader(w, "Document"), w, SeparatorBefore: false));

        var tools = floats.Where(w => w.FloatsAnyTool()).ToList();
        for (int i = 0; i < tools.Count; i++)
            entries.Add(new WindowMenuEntry(FloatHeader(tools[i], "Panel"), tools[i], SeparatorBefore: i == 0));

        return entries;
    }

    /// <summary>
    /// Workspace name (or a neutral label when none is open), marked dirty if any DOCKED document is.
    ///
    /// <para>Scoped to docked documents on purpose: a torn-off document gets its own entry in this
    /// menu carrying its own bullet, so counting it here too would mark the workspace dirty for work
    /// that the menu already attributes to another window.</para>
    ///
    /// <para>The name comes from the containing FOLDER, never the file stem — <c>.cws</c> is a
    /// dotfile, so <c>Path.GetFileNameWithoutExtension(".cws")</c> returns ".cws", not the workspace
    /// name. This trap is documented in src/Ui/CLAUDE.md; do not "simplify" it back.</para>
    /// </summary>
    private string ShellHeader()
    {
        var dir = CurrentWorkspacePath is null ? null : System.IO.Path.GetDirectoryName(CurrentWorkspacePath);
        var name = string.IsNullOrWhiteSpace(dir) ? "circuitRF" : System.IO.Path.GetFileName(dir);
        if (string.IsNullOrWhiteSpace(name)) name = "circuitRF";

        var dirty = _factory.DocumentDock?.VisibleDockables?.Any(IsDockableDirty) == true;
        return dirty ? DirtyMark + name : name;
    }

    /// <summary>
    /// Label for a floating window: the titles of what it hosts, comma-joined (a torn-off window can
    /// hold more than one tab). Falls back to <paramref name="fallback"/> for a window whose layout is
    /// mid-teardown and reports nothing.
    /// </summary>
    private static string FloatHeader(CrfHostWindow window, string fallback)
    {
        var parts = DockablesIn(window.Window?.Layout)
            .Select(DockableLabel)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        return parts.Count == 0 ? fallback : string.Join(", ", parts);
    }

    /// <summary>
    /// A dockable's menu label. Document titles already carry <see cref="DirtyMark"/> when unsaved, so
    /// they are used as-is — EXCEPT <see cref="DataDisplayDocument"/>, whose <c>IsDirty</c> is not wired
    /// to live edits (its title bullet can be stale), so its dirty state is re-derived from the
    /// authoritative baseline comparison instead.
    /// </summary>
    private static string DockableLabel(IDockable dockable)
    {
        var title = dockable.Title ?? string.Empty;

        if (dockable is DataDisplayDocument dd)
        {
            var bare = title.StartsWith(DirtyMark, StringComparison.Ordinal)
                ? title[DirtyMark.Length..]
                : title;
            return IsDockableDirty(dd) ? DirtyMark + bare : bare;
        }

        return title;
    }

    /// <summary>
    /// Unsaved-work test per document type. Mirrors the set <c>HasAnyDirtyWork</c> already covers, so
    /// the Window menu marks exactly what the close/quit prompt would offer to save.
    /// </summary>
    private static bool IsDockableDirty(IDockable dockable) => dockable switch
    {
        SchematicDocument d    => d.IsDirty,
        SymbolEditorDocument d => d.IsDirty,
        LayoutDocument d       => d.IsDirty,
        TechDocument d         => d.IsDirty,
        // DataDisplayDocument.IsDirty is never wired to live edits (documented in src/Ui/CLAUDE.md);
        // HasUnsavedChanges() is the authoritative baseline comparison.
        DataDisplayDocument d  => d.ViewModel.Window.HasUnsavedChanges(),
        _                      => false,
    };

    /// <summary>Every dockable in a floating window's layout, depth-first. Null-guarded throughout:
    /// a window being torn down can present a partially-stripped tree.</summary>
    private static IEnumerable<IDockable> DockablesIn(IDockable? root)
    {
        if (root is null) yield break;

        if (root is IDock { VisibleDockables: { } children })
        {
            foreach (var child in children)
            {
                if (child is null) continue;
                foreach (var d in DockablesIn(child))
                    yield return d;
            }
            yield break;
        }

        yield return root;
    }

    /// <summary>Brings a window to the front and gives it focus, un-minimizing first if needed.</summary>
    private static void FocusWindow(Window window)
    {
        if (window.PlatformImpl is null) return;      // already closed

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
        window.Focus();
    }
}
