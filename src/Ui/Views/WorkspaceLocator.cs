using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.Views;

/// <summary>
/// Which workspace a piece of UI belongs to — MW1 R-mw1-14, and the only place that question is
/// answered.
///
/// <para><b>Nine sites used to answer it with
/// <c>desktop.Windows.OfType&lt;WorkspaceWindow&gt;().FirstOrDefault()</c></b>, every one of them
/// reached from a view whose own DataContext is a document rather than a workspace. With one window
/// that is right by accident; with two it returns an arbitrary one, so a command invoked in window B
/// runs against window A's workspace — a Reveal that opens the wrong folder, a technology resolved
/// from the wrong project, a panel toggled in a window the user is not looking at.</para>
///
/// <para>The answer is taken from the CALLER, in this order:</para>
/// <list type="number">
///   <item>the <see cref="WorkspaceWindow"/> the control lives in, if any;</item>
///   <item>the workspace a floating <see cref="CrfHostWindow"/> was STAMPED with when its owning
///         factory created it (never inferred from position, z-order or title);</item>
///   <item>the workspace window most recently brought to the front;</item>
///   <item>the only workspace window there is.</item>
/// </list>
///
/// <para>Steps 3 and 4 are the honest fallbacks for a caller that genuinely has no window — a global
/// keyboard hook fired before anything is focused, a menu item on the macOS application menu. With
/// one window open every step agrees, which is why R-mw1-1 holds: a single-window session behaves
/// exactly as it did.</para>
/// </summary>
public static class WorkspaceLocator
{
    /// <summary>The workspace <paramref name="source"/> belongs to, or null when there is none open.</summary>
    public static WorkspaceViewModel? For(object? source)
    {
        if (WindowOf(source) is { } window)
        {
            if (window is WorkspaceWindow shell && shell.DataContext is WorkspaceViewModel vm) return vm;
            if (window is CrfHostWindow { OwningWorkspace: { } owner }) return owner;
        }
        return Any();
    }

    /// <summary>
    /// The WINDOW <paramref name="source"/> belongs to: the shell itself for a control docked in one,
    /// and the shell that OWNS a float for a control inside one.
    /// </summary>
    public static WorkspaceWindow? ShellWindowFor(object? source) => WindowFor(For(source));

    /// <summary>The shell window showing <paramref name="workspace"/>, or null. The sibling of
    /// <see cref="For"/>, in the other direction — one lookup, not two implementations.</summary>
    public static WorkspaceWindow? WindowFor(WorkspaceViewModel? workspace)
    {
        if (workspace is null) return null;
        return Desktop()?.Windows.OfType<WorkspaceWindow>()
                        .FirstOrDefault(w => ReferenceEquals(w.DataContext, workspace));
    }

    /// <summary>
    /// The workspace to use when the caller has no window at all: the most recently active one,
    /// falling back to the first. Named rather than reached by passing null, so a call site that is
    /// guessing reads as one.
    /// </summary>
    public static WorkspaceViewModel? Any()
    {
        if (Desktop() is not { } desktop) return null;

        if (App.LastActiveWorkspace is { } last
            && desktop.Windows.Contains(last)
            && last.DataContext is WorkspaceViewModel recent) return recent;

        return desktop.Windows.OfType<WorkspaceWindow>().FirstOrDefault()?.DataContext as WorkspaceViewModel;
    }

    /// <summary>Every open workspace window, in creation order.</summary>
    public static IReadOnlyList<WorkspaceWindow> AllWindows()
        => Desktop() is { } desktop ? [.. desktop.Windows.OfType<WorkspaceWindow>()] : [];

    private static Window? WindowOf(object? source) => source switch
    {
        Window w  => w,
        Visual v  => TopLevel.GetTopLevel(v) as Window,
        _         => null,
    };

    private static IClassicDesktopStyleApplicationLifetime? Desktop()
        => Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
}
