using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using CircuitRF.Engine.Transient;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// brief TRN-7 R-trn7-7 — whether a usable ngspice is discovered, as the transient card's Solver choice shows it. Asked off the
/// UI thread (<c>ngspice -v</c> is a short process), and asked again whenever that can change: an install finishing
/// (<see cref="Views.Dialogs.SolverInstallRunner.Finished"/>), a path set in Settings ▸ Solvers, an uninstall completing. The
/// card follows it live, with no restart and no re-opening. It uses <see cref="NgspiceRunner.Locate"/>, the very question a run
/// asks, so the card and a run cannot disagree.
/// </summary>
public static class NgspiceAvailability
{
    static NgspiceAvailability()
    {
        // an install finishing, or any removal (Remove all included), can change the answer
        Views.Dialogs.SolverInstallRunner.Finished += tool => { if (tool == CircuitRF.Design.Em3d.SolverTool.Ngspice) Refresh(); };
        Views.Dialogs.SolverRemovalRunner.Finished += () => Refresh();
    }

    private static NgspiceProgram? _current;
    private static bool _known;
    private static int _generation;
    private static readonly List<WeakReference<Action>> Listeners = [];

    /// <summary>The ngspice discovered at the last check, or null (none, or not checked yet).</summary>
    public static NgspiceProgram? Current => _current;

    /// <summary>Whether a check has answered yet.</summary>
    public static bool Known => _known;

    /// <summary>Calls <paramref name="changed"/> on the UI thread whenever the answer changes. Held weakly: a card that is closed
    /// is not kept alive by this list, so the caller keeps its own reference to the delegate.</summary>
    public static void Subscribe(Action changed)
    {
        lock (Listeners) Listeners.Add(new WeakReference<Action>(changed));
        if (!_known) Refresh();
    }

    /// <summary>Asks discovery again, off the UI thread, and tells every listener the answer.</summary>
    public static Task Refresh()
    {
        int gen = System.Threading.Interlocked.Increment(ref _generation);
        return Task.Run(() =>
        {
            NgspiceProgram? found;
            try { found = NgspiceRunner.Locate(); }
            catch (Exception) { found = null; }
            if (gen == _generation) Publish(found);
        });
    }

    /// <summary>Sets the answer — a test's stub — superseding any check still running, and tells every listener.</summary>
    public static void Set(NgspiceProgram? program)
    {
        System.Threading.Interlocked.Increment(ref _generation);
        Publish(program);
    }

    private static void Publish(NgspiceProgram? program)
    {
        _current = program;
        _known = true;
        void Notify()
        {
            var alive = new List<Action>();
            lock (Listeners)
            {
                Listeners.RemoveAll(w => !w.TryGetTarget(out _));
                foreach (var w in Listeners) if (w.TryGetTarget(out var a)) alive.Add(a);
            }
            foreach (var a in alive) a();
        }
        // with no application running (a test, a headless tool) there is no UI thread to post to
        if (Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess()) Notify();
        else Dispatcher.UIThread.Post(Notify);
    }
}
