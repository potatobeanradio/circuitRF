using System.Runtime.CompilerServices;

namespace CircuitRF.Ui.Filters;

/// <summary>
/// One LC Filter Designer per placed Filter (brief FD-P §2): opening the designer on a Filter that
/// already has one brings that one forward instead of opening a second window bound to the same
/// instance — two windows applying to one block would each overwrite the other. The Match Designer
/// keeps the same rule (<c>MatchDesignerWindow.Show</c>). Generic over the window so the rule is
/// testable without a display.
/// </summary>
public sealed class LcFilterDesignerWindows<TWindow> where TWindow : class
{
    private readonly ConditionalWeakTable<EditableComponent, TWindow> _open = new();

    /// <summary>
    /// The window already open on <paramref name="filter"/>, activated; otherwise a new one from
    /// <paramref name="create"/>, remembered until <see cref="Closed"/> is called for it.
    /// </summary>
    /// <returns>The window, and whether it was created by this call.</returns>
    public (TWindow Window, bool Created) Show(EditableComponent filter, Func<TWindow> create, Action<TWindow> activate)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (_open.TryGetValue(filter, out var existing))
        {
            activate(existing);
            return (existing, false);
        }
        var window = create();
        _open.Add(filter, window);
        return (window, true);
    }

    /// <summary>Forgets <paramref name="filter"/>'s window, when it is the one remembered.</summary>
    public void Closed(EditableComponent filter, TWindow window)
    {
        if (_open.TryGetValue(filter, out var w) && ReferenceEquals(w, window)) _open.Remove(filter);
    }

    /// <summary>True while a window is open on <paramref name="filter"/>.</summary>
    public bool IsOpen(EditableComponent filter) => _open.TryGetValue(filter, out _);
}
