namespace CircuitRF.Design.Plugins;

/// <summary>
/// The running <c>window</c> plugins (brief-plg-4 §3, <c>plugins.md</c> §8): one process per plugin at a time. circuitRF
/// draws nothing for one — it starts it with the same <c>hello</c>, then only answers its <c>placed</c>, <c>host</c> and
/// <c>open</c> until it exits. A second start while one runs is refused, because circuitRF cannot raise another
/// process's window; when circuitRF exits, each is sent <c>bye</c>, given 2 s, and killed.
/// </summary>
public static class PluginWindows
{
    private static readonly Dictionary<string, PluginSession> Running = new(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="pluginId"/> has a window process alive.</summary>
    public static bool IsRunning(string pluginId)
    {
        lock (Running) return Running.TryGetValue(pluginId, out var s) && Alive(s);
    }

    /// <summary>The sessions alive now.</summary>
    public static IReadOnlyList<PluginSession> All
    {
        get { lock (Running) return [.. Running.Values.Where(Alive)]; }
    }

    /// <summary>
    /// Starts <paramref name="entry"/>'s window program, or refuses — already running, or any refusal
    /// <see cref="PluginSession.Start"/> makes. The caller sends <c>hello</c>. When the process exits, the session is
    /// closed (its staging folder goes) once every message it sent has been answered.
    /// </summary>
    public static PluginOutcome<PluginSession> Start(PluginEntry entry, PluginStartOptions options)
    {
        string id = entry.Manifest?.Id ?? entry.Directory;
        lock (Running)
        {
            if (Running.TryGetValue(id, out var open) && Alive(open))
                return PluginOutcome<PluginSession>.Fail(new PluginFailure(PluginFailureKind.AlreadyRunning,
                    $"{entry.DisplayName} is already running. It draws its own window, so circuitRF cannot bring it forward; switch to it.", []));

            var started = PluginSession.Start(entry, options);
            if (started.Value is not { } session) return started;
            Running[id] = session;
            session.Ended += failure => { _ = CloseAsync(id, session); };
            return started;
        }
    }

    /// <summary>Closes every running window plugin — <c>bye</c>, 2 s, then killed — and waits for them, at most
    /// <paramref name="within"/>. What circuitRF calls on its way out.</summary>
    public static void CloseAll(TimeSpan? within = null)
    {
        PluginSession[] all;
        lock (Running) { all = [.. Running.Values]; Running.Clear(); }
        if (all.Length == 0) return;
        try { Task.WhenAll(all.Select(s => s.ByeAsync())).Wait(within ?? TimeSpan.FromSeconds(4)); }
        catch (AggregateException) { /* each session kills its own process whatever happens */ }
    }

    private static async Task CloseAsync(string id, PluginSession session)
    {
        await session.IdleAsync();
        lock (Running) if (Running.TryGetValue(id, out var s) && ReferenceEquals(s, session)) Running.Remove(id);
        await session.ByeAsync();
    }

    private static bool Alive(PluginSession s) => s.Fatal is null && !s.HasExited;
}
