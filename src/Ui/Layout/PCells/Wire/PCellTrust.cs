using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Layout.PCells.Wire;

/// <summary>What this installation has decided about one kit's generator scripts.</summary>
public enum PCellTrustDecision
{
    /// <summary>Nobody has been asked. Nothing runs — see <see cref="PCellWorkerResolver"/>.</summary>
    Unknown,
    Allowed,
    Denied,
}

/// <summary>
/// Which kits' generator scripts this installation has agreed to run.
///
/// <para><b>Recorded PER USER, not inside the workspace — and that is a deliberate departure from the
/// plan's "recorded per workspace" wording, for a reason worth stating plainly.</b> A decision stored
/// in a file that travels with the artifact can be written by whoever sends you the artifact: a
/// workspace arriving with its own scripts already marked trusted would run them on open with no
/// prompt, which defeats the entire mechanism. Consent is a property of the person at the keyboard,
/// so it lives in this installation's own preferences and is never serialized into <c>.cws</c>.</para>
///
/// <para><b>Keyed by the kit's directory, not by its content.</b> Hashing the scripts into the key
/// would re-ask on every save while somebody is authoring a generator — training exactly the reflexive
/// "Allow" this exists to prevent. Moving a kit to a new path asks again, which is the honest answer:
/// it is a different thing on disk.</para>
/// </summary>
public sealed class PCellTrustStore
{
    private readonly Dictionary<string, bool> _decisions;
    private readonly Action<IReadOnlyDictionary<string, bool>>? _persist;

    /// <param name="seed">Decisions already recorded. Keys are normalized on the way in, so a store
    /// loaded from disk answers the same as one just written.</param>
    /// <param name="persist">Where a new decision goes. Null keeps it in memory only — for a headless
    /// caller and for tests, which must not write into the developer's own preferences.</param>
    public PCellTrustStore(
        IEnumerable<KeyValuePair<string, bool>>? seed = null,
        Action<IReadOnlyDictionary<string, bool>>? persist = null)
    {
        _decisions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        _persist = persist;

        if (seed is null) return;
        foreach (var (path, allowed) in seed)
            if (Normalize(path) is { Length: > 0 } key) _decisions[key] = allowed;
    }

    /// <summary>The store the application uses: this installation's own preferences.</summary>
    public static PCellTrustStore UserLocal()
        => new(PCellTrustPreferences.Load(), PCellTrustPreferences.Save);

    public PCellTrustDecision Decide(string manifestDirectory)
        => _decisions.TryGetValue(Normalize(manifestDirectory), out bool allowed)
            ? allowed ? PCellTrustDecision.Allowed : PCellTrustDecision.Denied
            : PCellTrustDecision.Unknown;

    /// <summary>
    /// Records a decision and writes it. <b>A refusal is recorded too</b> — otherwise every open
    /// re-asks about the same kit, and a prompt that nags is a prompt people learn to dismiss without
    /// reading. Reversing a refusal is what the Settings "forget" action is for.
    /// </summary>
    public void Record(string manifestDirectory, bool allowed)
    {
        string key = Normalize(manifestDirectory);
        if (key.Length == 0) return;

        _decisions[key] = allowed;
        // Failing to persist must not undo having decided: the session's own answer stands, and the
        // only cost is being asked again next time — which errs toward asking, not toward running.
        try { _persist?.Invoke(_decisions); } catch { /* preferences are best-effort by design */ }
    }

    public int Count => _decisions.Count;

    /// <summary>Absolute, separator-normalized, no trailing separator — so the same directory named
    /// two ways is one entry rather than two.</summary>
    public static string Normalize(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return "";
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch { return directory.Trim(); }
    }
}

/// <summary>The preferences half of <see cref="PCellTrustStore"/>, kept separate so the store itself
/// has no dependency on where decisions are kept and stays trivially testable.</summary>
public static class PCellTrustPreferences
{
    public static IReadOnlyDictionary<string, bool> Load()
        => AppPreferencesIo.Load().PCellTrust ?? new Dictionary<string, bool>();

    public static void Save(IReadOnlyDictionary<string, bool> decisions)
        => AppPreferencesIo.Update(p => p.PCellTrust =
            decisions.Count == 0 ? null : new Dictionary<string, bool>(decisions));

    /// <summary>Drops every recorded decision, so circuitRF asks about each kit again. The one way
    /// back from a refusal — reachable from Settings.</summary>
    public static void Forget() => AppPreferencesIo.Update(p => p.PCellTrust = null);

    public static int RememberedCount() => AppPreferencesIo.Load().PCellTrust?.Count ?? 0;
}
