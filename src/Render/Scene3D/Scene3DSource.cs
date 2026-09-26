// brief-em3d-28 R-em3d28-2d — regeneration off the drawing path (em-3d.md §8.2 point 4, §8.4).
//
// A change to the .cem, .clay, .ctech or .wBond asks for a new scene. The problem is regenerated and
// tessellated on the thread pool under a GENERATION number; the view keeps drawing the last FINISHED
// scene the whole time, and a result for a generation that has since been superseded is discarded —
// never handed to the renderer, not even for one frame (gate 4). The in-flight build is also told to
// stop, but a build that ignores the token is harmless: its result is dropped by number, not by luck.

namespace CircuitRF.Render.Scene3D;

/// <summary>Builds scenes in the background and publishes only the newest.</summary>
public sealed class Scene3DSource : IDisposable
{
    private readonly Func<long, object?, CancellationToken, Scene3DModel> _build;
    private readonly object _gate = new();
    private long _requested, _published = -1;
    private CancellationTokenSource? _inFlight;
    private Scene3DModel _current;
    private long _builds, _discarded, _handed;

    /// <param name="build">Generates the problem and builds the scene for a generation, from the state
    /// its request captured on the caller's thread. Runs on the thread pool; may throw (the failure is
    /// published as an empty scene carrying the message).</param>
    public Scene3DSource(Func<long, object?, CancellationToken, Scene3DModel> build)
    {
        _build = build;
        _current = Scene3DModel.Empty();
    }

    /// <summary>Raised on the builder's thread when a newer scene is ready. A subscriber marshals.</summary>
    public event Action<Scene3DModel>? SceneReady;

    /// <summary>The last finished, not-superseded scene.</summary>
    public Scene3DModel Current { get { lock (_gate) return _current; } }

    /// <summary>How many builds (problem generations) have started.</summary>
    public long Builds => Interlocked.Read(ref _builds);

    /// <summary>How many finished builds were dropped because a newer generation had been asked for.</summary>
    public long Discarded => Interlocked.Read(ref _discarded);

    /// <summary>How many scenes have been handed on (published) — gate 4's counter.</summary>
    public long Handed => Interlocked.Read(ref _handed);

    /// <summary>The newest generation asked for.</summary>
    public long Requested => Interlocked.Read(ref _requested);

    /// <summary>Asks for a new scene built from <paramref name="state"/> — a snapshot the caller took on
    /// its own thread, so the build never reads live state. Returns its generation number.</summary>
    public long Request(object? state = null)
    {
        long gen;
        CancellationTokenSource cts;
        lock (_gate)
        {
            gen = ++_requested;
            _inFlight?.Cancel();
            _inFlight = cts = new CancellationTokenSource();
        }
        Interlocked.Increment(ref _builds);
        _ = Task.Run(() => Run(gen, state, cts.Token));
        return gen;
    }

    private void Run(long gen, object? state, CancellationToken ct)
    {
        Scene3DModel scene;
        try { scene = _build(gen, state, ct); }
        catch (OperationCanceledException) { Interlocked.Increment(ref _discarded); return; }
        catch (Exception ex) { scene = Scene3DModel.Empty(gen, [ex.Message]); }

        lock (_gate)
        {
            if (gen != _requested || gen <= _published)
            {
                Interlocked.Increment(ref _discarded);
                return;
            }
            _published = gen;
            _current = scene;
            Interlocked.Increment(ref _handed);
        }
        SceneReady?.Invoke(scene);
    }

    public void Dispose()
    {
        lock (_gate) { _inFlight?.Cancel(); _requested = long.MaxValue - 1; }
    }
}
