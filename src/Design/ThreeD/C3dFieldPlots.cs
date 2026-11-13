// brief-em3d-83 R-em3d83-1 — a FIELD PLOT is a record of the document: what the 3D view draws of a run's fields, saved with
// the .c3d, named, hidden and undone like a probe.
//
// A PLOT NAMES ITS SOLUTION BY VALUE, NEVER BY POSITION. A frequency in GHz (a driven run), a mode number (eigenmode), a
// terminal's name (static), a sweep point (thermal). The picker's index would move the plot to another frequency the day a
// re-run saves one more below it, and nothing would say so. A value that the run no longer holds is reported, never
// re-pointed to the nearest (R-em3d83-5).
//
// A PLOT IS DISPLAY, NOT PHYSICS. No lowering reads one, and every comparison of "is this the model that was solved" —
// the stale banner, Simulate's saved document — reads C3dPersistence.SerializeForRun, which leaves the plots out
// (R-em3d83-2). Adding a plot never makes a result stale.

using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Design.Layout.Em;

namespace CircuitRF.Design.ThreeD;

/// <summary>Where a plot is drawn — exactly one target per plot.</summary>
public enum C3dFieldPlotOn
{
    /// <summary>A cut through the volume on an axis-aligned plane, the plot's own <see cref="C3dFieldPlot.Axis"/> and
    /// <see cref="C3dFieldPlot.Offset"/>.</summary>
    ClipPlane,
    /// <summary>The selected region's boundary (a volume quantity), the conductors (a boundary quantity such as J_s), or — for a
    /// temperature — every exposed face.</summary>
    Surfaces,
    /// <summary>The faces named in <see cref="C3dFieldPlot.Faces"/>, one by one (brief 82's Plot Field).</summary>
    Faces,
}

/// <summary>Which side of a sheet a face plot shows. A sheet carrying charge has two different normal fields.</summary>
public enum C3dFieldSide { None, Top, Bottom }

/// <summary>brief-em3d-100 — what a plot's drive power is. With every other port terminated in its own Z0.</summary>
public enum C3dDriveReferredTo
{
    /// <summary>The incident power |a|²: what a source of internal resistance R makes available to the port.</summary>
    Incident,
    /// <summary>The power that enters the port, |a|²(1 − |S_kk|²).</summary>
    Accepted,
}

/// <summary>brief-em3d-100 — a drive power as the Inspector takes and shows it: W, mW, µW or dBm, stored in watts.</summary>
public static class C3dDrivePower
{
    private static readonly (string Unit, double Watts)[] Units = [("mW", 1e-3), ("µW", 1e-6), ("uW", 1e-6), ("kW", 1e3), ("W", 1)];

    /// <summary><paramref name="text"/> in watts: a number, optionally followed (spaced or not) by W, mW, µW (uW), kW or dBm.
    /// A plain number is watts. False, and the sentence, when it is not a positive power.</summary>
    public static bool TryParse(string? text, out double watts, out string? error)
    {
        watts = double.NaN;
        string t = (text ?? "").Trim();
        if (t.Length == 0) { error = "Enter a power, e.g. 10 W, 250 mW or 30 dBm."; return false; }
        double scale = 1;
        bool dbm = false;
        if (t.EndsWith("dBm", StringComparison.OrdinalIgnoreCase)) { dbm = true; t = t[..^3].TrimEnd(); }
        else
            foreach (var (unit, w) in Units)
                if (t.EndsWith(unit, StringComparison.Ordinal)) { scale = w; t = t[..^unit.Length].TrimEnd(); break; }
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
        {
            error = $"'{text!.Trim()}' is not a power: enter one such as 10 W, 250 mW or 30 dBm.";
            return false;
        }
        watts = dbm ? 1e-3 * Math.Pow(10, v / 10) : v * scale;
        if (!(watts > 0) || !double.IsFinite(watts))
        {
            error = "A drive power is above zero.";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary><paramref name="watts"/> as a person reads it: <c>10 W</c>, <c>0.5 W</c>, <c>250 mW</c>, <c>3 µW</c>.</summary>
    public static string Format(double watts)
    {
        static string G(double v) => v.ToString("G4", CultureInfo.InvariantCulture);
        return watts >= 0.1 ? $"{G(watts)} W" : watts >= 1e-4 ? $"{G(watts * 1e3)} mW" : $"{G(watts * 1e6)} µW";
    }
}

/// <summary>One face a <see cref="C3dFieldPlotOn.Faces"/> plot paints: <c>object/face</c>, and on a sheet the side shown.</summary>
public sealed class C3dFieldPlotFace
{
    /// <summary><c>object/face</c> — §6.4's naming, the scene's face name.</summary>
    public string Face { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public C3dFieldSide Side { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }

    /// <summary>The object's name and the face's (split at the last <c>/</c>).</summary>
    [JsonIgnore]
    public (string Object, string Face) Parts
    {
        get
        {
            int slash = Face.LastIndexOf('/');
            return slash <= 0 ? (Face, "") : (Face[..slash], Face[(slash + 1)..]);
        }
    }
}

/// <summary>brief-dfr34-3 R-dfr34-3-1 — a section's two points in plan, DBU, <c>[x, y]</c> each: the plot's vertical plane runs
/// through both, its own horizontal axis is the distance from <see cref="From"/> toward <see cref="To"/>, and its vertical is z.
/// </summary>
public sealed class C3dFieldPlotThrough
{
    [JsonConverter(typeof(C3dPoint2JsonConverter))]
    public C3dPoint2 From { get; set; }

    [JsonConverter(typeof(C3dPoint2JsonConverter))]
    public C3dPoint2 To { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }

    /// <summary>The two points are one: no plane runs through them alone.</summary>
    [JsonIgnore]
    public bool IsDegenerate => From == To;

    public C3dFieldPlotThrough Clone() => new() { From = From, To = To };
}

/// <summary>
/// Which saved solution a plot shows, by value. Exactly one of <see cref="GHz"/>, <see cref="Mode"/>, <see cref="Terminal"/>
/// or <see cref="Point"/> is set; <see cref="Port"/> names the excited port of a driven run that excited several.
/// </summary>
public sealed class C3dFieldSolution
{
    /// <summary>A driven run's saved frequency, GHz.</summary>
    public double? GHz { get; set; }

    /// <summary>The port whose excitation it is, when a driven run excited more than one (1-based).</summary>
    public int? Port { get; set; }

    /// <summary>An eigenmode run's mode, 1-based.</summary>
    public int? Mode { get; set; }

    /// <summary>A static run's terminal, by name.</summary>
    public string? Terminal { get; set; }

    /// <summary>A thermal run's sweep point, 1-based.</summary>
    public int? Point { get; set; }

    /// <summary>brief-sar-9 — a schematic's fields analysis, by name: the field the circuit recombined from the run's per-port
    /// fields, at <see cref="GHz"/>, rather than one port's own.</summary>
    public string? Cosim { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }

    /// <summary>Two solutions name the same saved step. A frequency matches to one part in 10⁹ — what G6 printing and a
    /// round trip through the <c>.pvd</c>'s timestep can move it by.</summary>
    public bool SameAs(C3dFieldSolution? other)
        => other is not null && Near(GHz, other.GHz) && Port == other.Port && Mode == other.Mode &&
           string.Equals(Terminal, other.Terminal, StringComparison.Ordinal) && Point == other.Point &&
           string.Equals(Cosim, other.Cosim, StringComparison.Ordinal);

    private static bool Near(double? a, double? b)
        => a is null ? b is null : b is not null && Math.Abs(a.Value - b.Value) <= 1e-9 * Math.Max(Math.Abs(a.Value), Math.Abs(b.Value));

    /// <summary>What the plot shows, as a person reads it: <c>10 GHz</c>, <c>10 GHz, port 2</c>, <c>mode 3</c>,
    /// <c>terminal 'P1'</c>, <c>point 4</c>.</summary>
    public string Describe()
    {
        string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
        if (GHz is { } fc && Cosim is { } c) return $"{G(fc)} GHz, fields '{c}'";
        if (GHz is { } f) return $"{G(f)} GHz" + (Port is { } p ? $", port {p}" : "");
        if (Mode is { } m) return $"mode {m}";
        if (Terminal is { } t) return $"terminal '{t}'";
        if (Point is { } k) return $"point {k}";
        return "the first saved solution";
    }

    public C3dFieldSolution Clone() => new() { GHz = GHz, Port = Port, Mode = Mode, Terminal = Terminal, Point = Point, Cosim = Cosim };
}

/// <summary>R-em3d83-1 — a field plot: a named reading of a setup's run, drawn where it says.</summary>
public sealed class C3dFieldPlot
{
    /// <summary>The name its row shows: unique among the plots, <c>Field1</c>, <c>Field2</c> … by default.</summary>
    public string Name { get; set; } = "";

    /// <summary>The setup whose run it reads, by name. Null reads the setup active when it is drawn.</summary>
    public string? Setup { get; set; }

    /// <summary>Which solver's fields, for a setup that runs both; null otherwise.</summary>
    public Em3dSolverId? Solver { get; set; }

    /// <summary>The saved solution shown, by value. Null shows the first the run saved.</summary>
    public C3dFieldSolution? Solution { get; set; }

    /// <summary>The field array's name, as the solver wrote it: <c>E</c>, <c>B</c>, <c>J_s</c>, <c>V</c>, <c>T_C</c> …</summary>
    public string Quantity { get; set; } = "E";

    /// <summary>How it is read: <c>Peak</c> (a magnitude), <c>Instantaneous</c> (animated), <c>Value</c> (a real scalar).</summary>
    public string? Mode { get; set; }

    public C3dFieldPlotOn On { get; set; }

    /// <summary>A <see cref="C3dFieldPlotOn.ClipPlane"/> plot's plane: its axis …</summary>
    public C3dAxis? Axis { get; set; }

    /// <summary>… and its coordinate along that axis, DBU. The plot owns its plane, so two plots cut in two places.</summary>
    public long? Offset { get; set; }

    /// <summary>brief-dfr34-3 R-dfr34-3-1 — a <see cref="C3dFieldPlotOn.ClipPlane"/> plot's plane stated instead by two points in
    /// plan: the vertical plane through both, spanning the whole height. Set, it is the plane and <see cref="Axis"/> and
    /// <see cref="Offset"/> are not read; null (every plot written before it) is the axis plane.</summary>
    public C3dFieldPlotThrough? Through { get; set; }

    /// <summary>A <see cref="C3dFieldPlotOn.Faces"/> plot's faces, in the order they were added.</summary>
    public List<C3dFieldPlotFace> Faces { get; set; } = [];

    /// <summary>The colour scale in dB (an EM magnitude).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Db { get; set; }

    /// <summary>brief-cxp-5 — drawn smooth: each value averaged over the coincident nodes of cells of one material
    /// (<c>FieldSmoothing</c>), for a solver that writes every cell's own nodes. Computed once per loaded field, so it costs
    /// nothing while the view moves.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Smooth { get; set; }

    /// <summary>The colour scale's top: this percentile of what is drawn (95, 99, 99.9, 100).</summary>
    [DefaultValue(99.0)]
    public double Percentile { get; set; } = 99;

    /// <summary>A temperature plot's range is the union of every sweep point's, so a step never rescales it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool FixRange { get; set; }

    /// <summary>brief-em3d-100 R-em3d100-2 — the power a driven field is shown at, W, time-averaged, peak convention. Null is
    /// the solver's own (PalaceDrive.IncidentPowerW). Display: the solve is linear, so this rescales what is drawn and never
    /// what was solved.</summary>
    public double? DrivePowerW { get; set; }

    /// <summary>brief-em3d-100 — what <see cref="DrivePowerW"/> is: the power a matched source makes available (incident), or
    /// the power that enters the port after reflection.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public C3dDriveReferredTo DriveReferredTo { get; set; }

    /// <summary>brief-sar-6 R-sar6-5 — a SAR <see cref="C3dFieldPlotOn.ClipPlane"/> plot drawn as the maximum-intensity projection
    /// along its <see cref="Axis"/>: each point of the plane shows the largest value on the line through it, so the hottest
    /// tissue shows wherever it lies. Offered for SAR quantities only; the plane is drawn through the middle of the volume.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Mip { get; set; }

    /// <summary>brief-dfr31-3 R-dfr31-3-1 — a <see cref="C3dFieldPlotOn.ClipPlane"/> plot's plane is drawn in the 3D view even
    /// while the plot is not selected. Display only, like the rest of the plot.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ShowPlane { get; set; }

    /// <summary>The tree's tick, as <see cref="C3dObject.Hidden"/>. One plot is drawn at a time: showing one hides the others.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Hidden { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unread { get; set; }

    /// <summary>The temperature array a thermal run writes (ThermalFieldFiles).</summary>
    public const string TemperatureQuantity = "T_C";

    [JsonIgnore]
    public bool IsTemperature => Quantity == TemperatureQuantity;

    /// <summary>The name a new plot takes: <c>Field1</c>, <c>Field2</c> …, the first not already a plot's.</summary>
    public static string NextName(IEnumerable<C3dFieldPlot> plots, string stem = "Field")
    {
        var used = plots.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        for (int k = 1; ; k++)
            if (!used.Contains(stem + k.ToString(CultureInfo.InvariantCulture))) return stem + k.ToString(CultureInfo.InvariantCulture);
    }
}
