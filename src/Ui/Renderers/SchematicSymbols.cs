using CircuitRF.Ui.Schematic;
using SkiaSharp;

namespace CircuitRF.Ui.Renderers;

/// <summary>
/// Built-in vector geometry for schematic component symbols.
///
/// All coordinates are in component-LOCAL space (100 units = 1 grid square).
/// Standard 2-terminal horizontal orientation: port 1 at (-200, 0), port 2 at (200, 0).
/// Each float[] array encodes line segments as successive (x1,y1,x2,y2) quads.
///
/// Drawing applies rotation + translation via the renderer's LocalToPixel helper.
/// The caller is responsible for scaling (zoom) and canvas transforms.
/// </summary>
public static class SchematicSymbols
{
    // ── 2-terminal components (ports at ±200 on X axis) ──────────────────────

    /// <summary>Resistor: IEC box style. Leads ±200→±60; box ±60 × ±30.</summary>
    public static readonly float[] Resistor = [
        -200f, 0f, -60f, 0f,     // left lead
          60f, 0f, 200f, 0f,     // right lead
        -60f,-30f,  60f,-30f,    // top
         60f,-30f,  60f, 30f,    // right
         60f, 30f, -60f, 30f,    // bottom
        -60f, 30f, -60f,-30f,    // left
    ];

    /// <summary>Inductor: 3 arc-bumps (ascending arcs from left to right).</summary>
    public static readonly float[] Inductor = BuildInductor();

    /// <summary>Capacitor: two parallel plates with leads.</summary>
    public static readonly float[] Capacitor = [
        -200f, 0f, -18f, 0f,     // left lead
          18f, 0f, 200f, 0f,     // right lead
        -18f,-40f, -18f, 40f,    // left plate
         18f,-40f,  18f, 40f,    // right plate
    ];

    /// <summary>DC Voltage Source body: circle + leads (no +/- marks — those are in VoltageSourcePlus).</summary>
    public static readonly float[] VoltageSource = BuildCircleSource();

    /// <summary>DC Voltage Source +/- polarity marks, drawn in SymbolPlus color.</summary>
    public static readonly float[] VoltageSourcePlus =
    [
        -12f, -22f, 12f, -22f,   // horizontal bar of +
          0f, -32f,  0f, -12f,   // vertical bar of +
        -12f,  22f, 12f,  22f,   // − bar
    ];

    /// <summary>Tone Source: circle + sine-wave mark + leads.</summary>
    public static readonly float[] ToneSource = BuildToneSource();

    /// <summary>Ground: vertical stem + 3 tapering horizontal bars.</summary>
    public static readonly float[] Ground = [
        0f,   0f, 0f,  50f,     // stem (port is at 0,0)
       -60f,  50f, 60f, 50f,    // top bar
       -40f,  70f, 40f, 70f,    // middle bar
       -20f,  90f, 20f, 90f,    // bottom bar
    ];

    /// <summary>Port / terminal: circle with a lead to the left.</summary>
    public static readonly float[] Port = BuildCirclePort();

    /// <summary>FET/SDD: simplified box with gate (L), drain (UR), source (LR) leads.</summary>
    public static readonly float[] FetSdd = [
        // Gate lead (tip at -200)
        -200f,  0f, -80f,  0f,
        // Box body
        -80f,-100f,  80f,-100f,
         80f,-100f,  80f, 100f,
         80f, 100f, -80f, 100f,
        -80f, 100f, -80f,-100f,
        // Gate horizontal bar inside
        -80f,  0f, -30f,  0f,
        // Channel vertical bar
        -30f,-70f, -30f,  70f,
        // Drain lead (tip at 200,-100)
        -30f,-50f,  80f,-50f,
         80f,-50f, 200f,-100f,
        // Source lead (tip at 200,100)
        -30f, 50f,  80f,  50f,
         80f, 50f, 200f, 100f,
        // Arrow on drain (direction indicator)
        -30f,-50f, -20f,-40f,
        -30f,-50f, -20f,-60f,
    ];

    /// <summary>
    /// Z-Port / N-port termination body: box + Z label lines.
    /// Port lead stubs are NOT included here — the renderer draws them dynamically
    /// from each port's LocalX/LocalY so they adapt to any port count.
    /// Body edges: left x=−70, right x=+70.
    /// </summary>
    public static readonly float[] ZPort = [
        -70f,-50f,  70f,-50f,
         70f,-50f,  70f, 50f,
         70f, 50f, -70f, 50f,
        -70f, 50f, -70f,-50f,
        // Z-shape inside
        -40f,-30f,  40f,-30f,
         40f,-30f, -40f, 30f,
        -40f, 30f,  40f, 30f,
    ];

    /// <summary>
    /// SDD body: box only, no port leads.
    /// Port lead stubs are drawn dynamically by the renderer per port count.
    /// Body edges: left x=−80, right x=+80.
    /// </summary>
    public static readonly float[] SddBody = [
        -80f,-50f,  80f,-50f,
         80f,-50f,  80f, 50f,
         80f, 50f, -80f, 50f,
        -80f, 50f, -80f,-50f,
    ];

    /// <summary>Generic cell: box with 2 port leads.</summary>
    public static readonly float[] Generic = [
        -200f,  0f, -80f,  0f,
          80f,  0f, 200f,  0f,
        -80f,-50f,  80f,-50f,
         80f,-50f,  80f, 50f,
         80f, 50f, -80f, 50f,
        -80f, 50f, -80f,-50f,
    ];

    // ── Dispatch by SymbolKind ─────────────────────────────────────────────────

    /// <summary>
    /// Returns the SymbolPlus-colored segment array for symbols that have one (e.g. +/− marks
    /// on VoltageSource), or null if the symbol has no such marks.
    /// Drawn with a separate paint so the polarity indicator can use a distinct theme color.
    /// </summary>
    public static float[]? ForSymbolPlusSegments(SymbolKind kind) => kind switch
    {
        SymbolKind.VoltageSource => VoltageSourcePlus,
        _                        => null,
    };

    public static float[] For(SymbolKind kind) => kind switch
    {
        SymbolKind.Resistor      => Resistor,
        SymbolKind.Inductor      => Inductor,
        SymbolKind.Capacitor     => Capacitor,
        SymbolKind.VoltageSource => VoltageSource,
        SymbolKind.ToneSource    => ToneSource,
        SymbolKind.Ground        => Ground,
        SymbolKind.Port          => Port,
        SymbolKind.FetSdd        => FetSdd,
        SymbolKind.ZPort         => ZPort,
        SymbolKind.Sdd           => SddBody,
        _                        => Generic,
    };

    // ── Geometry builders ─────────────────────────────────────────────────────

    private static float[] BuildInductor()
    {
        // 3 bump arcs from x=-90 to x=90, each bump radius=30
        // Approximated with 4 segments per arc (5 points: 180°,135°,90°,45°,0°)
        const float r = 30f;
        float sin45 = r * 0.7071f;
        float[] bumps = [-60f, 0f, 30f];  // bump centers
        var segs = new List<float>();

        segs.AddRange([-200f, 0f, -90f, 0f]);  // left lead

        foreach (float cx in bumps)
        {
            float[] pts = [
                cx - r,   0f,
                cx - sin45, -sin45,
                cx,       -r,
                cx + sin45, -sin45,
                cx + r,   0f,
            ];
            for (int i = 0; i < pts.Length - 2; i += 2)
            {
                segs.Add(pts[i]); segs.Add(pts[i + 1]);
                segs.Add(pts[i + 2]); segs.Add(pts[i + 3]);
            }
        }

        segs.AddRange([90f, 0f, 200f, 0f]);   // right lead
        return [.. segs];
    }

    // Body-only (circle + leads, no +/- marks — those live in VoltageSourcePlus).
    private static float[] BuildCircleSource()
    {
        const float r = 50f;
        float d = r * 0.7071f;
        float[] pts = [r, 0f, d, -d, 0f, -r, -d, -d, -r, 0f, -d, d, 0f, r, d, d];

        var segs = new List<float>
        {
            -200f, 0f, -r, 0f,   // left lead
               r,  0f, 200f, 0f, // right lead
        };
        for (int i = 0; i < 8; i++)
        {
            int j = (i + 1) % 8;
            segs.Add(pts[i * 2]); segs.Add(pts[i * 2 + 1]);
            segs.Add(pts[j * 2]); segs.Add(pts[j * 2 + 1]);
        }
        return [.. segs];
    }

    private static float[] BuildToneSource()
    {
        const float r = 50f;
        float d = r * 0.7071f;
        float[] pts = [r, 0f, d, -d, 0f, -r, -d, -d, -r, 0f, -d, d, 0f, r, d, d];

        var segs = new List<float>
        {
            -200f, 0f, -r, 0f,
               r,  0f, 200f, 0f,
        };
        for (int i = 0; i < 8; i++)
        {
            int j = (i + 1) % 8;
            segs.Add(pts[i * 2]); segs.Add(pts[i * 2 + 1]);
            segs.Add(pts[j * 2]); segs.Add(pts[j * 2 + 1]);
        }
        segs.AddRange([-28f, 0f, -14f, -18f]);
        segs.AddRange([-14f, -18f, 14f, 18f]);
        segs.AddRange([14f, 18f, 28f, 0f]);
        return [.. segs];
    }

    private static float[] BuildCirclePort()
    {
        // Circle at origin, radius 30; lead extends to (-150, 0)
        const float r = 30f;
        float d = r * 0.7071f;
        float[] pts = [r, 0f, d, -d, 0f, -r, -d, -d, -r, 0f, -d, d, 0f, r, d, d];

        var segs = new List<float> { -200f, 0f, -r, 0f };  // lead

        for (int i = 0; i < 8; i++)
        {
            int j = (i + 1) % 8;
            segs.Add(pts[i * 2]); segs.Add(pts[i * 2 + 1]);
            segs.Add(pts[j * 2]); segs.Add(pts[j * 2 + 1]);
        }

        return [.. segs];
    }
}
