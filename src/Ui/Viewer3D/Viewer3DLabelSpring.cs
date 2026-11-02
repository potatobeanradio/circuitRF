// The 3D view's labels (ports, probes, heat sources, mesh regions) are placed afresh every frame by Viewer3DLabelLayout, and
// while the camera orbits the best place for one changes from frame to frame: a label snapped from one corner of its point to
// another in a single frame, which read as twitching rather than as a label making room. This gives each label a little mass.
// The layout's box is the TARGET; what is drawn is a box on a damped spring that follows it.
//
// What springs is the label's OFFSET from its point, never its position on screen: the point itself is projected from the
// camera and the box stays fastened to it exactly, so an orbit never leaves a label trailing behind its object. Only the
// change of side is animated. Slightly under-damped, so a label that swings over settles with a small overshoot.

using Avalonia;

namespace CircuitRF.Ui.Viewer3D;

/// <summary>Follows each label's laid-out box with a damped spring on its offset from its point.</summary>
internal sealed class Viewer3DLabelSpring
{
    /// <summary>Spring stiffness, 1/s² (natural frequency √k ≈ 12.2 rad/s).</summary>
    public const double Stiffness = 150;

    /// <summary>Below 1, so a swing ends in a small overshoot rather than a dead stop.</summary>
    public const double DampingRatio = 0.75;

    /// <summary>The longest step a frame advances by, seconds. A frame after an idle spell would otherwise carry the whole
    /// gap and land every label at once.</summary>
    public const double MaxStep = 1.0 / 30;

    /// <summary>A label within this many DIPs of its target, moving slower than <see cref="RestSpeed"/>, is at rest.</summary>
    public const double RestDistance = 0.3, RestSpeed = 3;

    private sealed class State
    {
        public Vector Offset, Velocity;
        public int Frame;
    }

    private readonly Dictionary<string, State> _states = [];
    private int _frame;
    private double _dt;
    private bool _moving;

    /// <summary>Whether a label was still travelling at the end of the last frame — the caller asks for another frame.</summary>
    public bool Moving { get; private set; }

    /// <summary>Starts a frame <paramref name="dt"/> seconds after the last one.</summary>
    public void BeginFrame(double dt)
    {
        _frame++;
        _dt = Math.Clamp(dt, 0, MaxStep);
        _moving = false;
    }

    /// <summary>Ends the frame: a label not drawn in it is forgotten, so one that comes back appears in place.</summary>
    public void EndFrame()
    {
        foreach (var key in _states.Where(kv => kv.Value.Frame != _frame).Select(kv => kv.Key).ToList()) _states.Remove(key);
        Moving = _moving;
    }

    /// <summary>
    /// The boxes to draw for labels <paramref name="texts"/> at <paramref name="anchors"/> whose layout put them at
    /// <paramref name="targets"/>. <paramref name="channel"/> keeps two label sets drawn in one frame apart. A label seen for
    /// the first time is drawn at its target.
    /// </summary>
    public Rect[] Follow(string channel, IReadOnlyList<Point> anchors, IReadOnlyList<string> texts, IReadOnlyList<Rect> targets)
    {
        var result = new Rect[targets.Count];
        var seen = new Dictionary<string, int>();
        double damping = 2 * DampingRatio * Math.Sqrt(Stiffness);
        for (int i = 0; i < targets.Count; i++)
        {
            // Two labels may share a text (two probes both called P1): the n-th of a text keeps the n-th one's state.
            int n = seen.TryGetValue(texts[i], out var c) ? c : 0;
            seen[texts[i]] = n + 1;
            string key = $"{channel}\u0001{texts[i]}\u0001{n}";
            var target = targets[i].TopLeft - anchors[i];
            if (!_states.TryGetValue(key, out var s))
                _states[key] = s = new State { Offset = target };
            else if (s.Frame != _frame)
            {
                int steps = Math.Max(1, (int)Math.Ceiling(_dt * 240));
                double h = _dt / steps;
                for (int k = 0; k < steps; k++)
                {
                    var accel = (target - s.Offset) * Stiffness - s.Velocity * damping;
                    s.Velocity += accel * h;
                    s.Offset += s.Velocity * h;
                }
                var off = target - s.Offset;
                if (Math.Sqrt(off.X * off.X + off.Y * off.Y) < RestDistance
                    && Math.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y) < RestSpeed)
                {
                    s.Offset = target;
                    s.Velocity = default;
                }
                else _moving = true;
            }
            s.Frame = _frame;
            result[i] = new Rect(anchors[i] + s.Offset, targets[i].Size);
        }
        return result;
    }
}
