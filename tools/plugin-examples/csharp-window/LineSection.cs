// The deterministic part: a frequency, an impedance, an electrical length and a substrate in; the three documents out.
// The window and the headless form both call it, so for the same parameters they stage the same bytes.
//
// What it computes, so every number can be checked by hand:
//   width   the microstrip width whose impedance circuitRF's line model gives as Z0 at the frequency, found by a secant
//           search on ln(width) — impedance falls close to linearly in ln(width) — then rounded to a whole DBU;
//   eeff    circuitRF's effective permittivity at that rounded width;
//   length  (degrees / 360) · c / (f · sqrt(eeff)), the physical length of that electrical length, rounded to a DBU.
// Nothing here models the line itself: lineModel is circuitRF's own microstrip, so what this plugin designs is what
// circuitRF will simulate after placement.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace LineSectionPlugin;

/// <summary>What the user asks for. Lengths and frequencies in base SI.</summary>
public sealed record LineSpec(double FrequencyHz, double Z0, double Degrees, string Conductor);

/// <summary>A conductor of the stackup, and the drawing layer it is drawn on.</summary>
public sealed record Conductor(string Name, string Layer, bool IsGround);

/// <summary>A technology as <c>resolve technology</c> (or a <c>technology</c> parameter) delivers it.</summary>
public sealed record Technology(string Path, int DbuPerMicron, IReadOnlyDictionary<string, (int Layer, int Datatype)> Layers,
                                IReadOnlyList<Conductor> Conductors)
{
    public static Technology From(JsonElement t)
    {
        var layers = new Dictionary<string, (int, int)>();
        foreach (var l in t.GetProperty("layers").EnumerateArray())
            layers[l.GetProperty("name").GetString()!] = (l.GetProperty("layer").GetInt32(), l.GetProperty("datatype").GetInt32());
        var conductors = new List<Conductor>();
        foreach (var s in t.GetProperty("stackup").EnumerateArray())
        {
            if (s.GetProperty("kind").GetString() != "conductor") continue;
            string? layer = s.TryGetProperty("layers", out var drawn) && drawn.GetArrayLength() > 0 ? drawn[0].GetString() : null;
            if (layer is null) continue;   // drawn on no layer: nothing to put a line on
            bool ground = s.TryGetProperty("isGround", out var g) && g.ValueKind == JsonValueKind.True;
            conductors.Add(new Conductor(s.GetProperty("name").GetString()!, layer, ground));
        }
        return new Technology(t.GetProperty("path").GetString()!, t.GetProperty("dbuPerMicron").GetInt32(), layers, conductors);
    }

    /// <summary>The conductors a line can be drawn on: every one that is not the ground reference.</summary>
    public IEnumerable<Conductor> Signals => Conductors.Where(c => !c.IsGround);
}

/// <summary>The design, in DBU and in metres.</summary>
public sealed record LineDesign(long WidthDbu, long LengthDbu, double Width, double Length, double Eeff, double ModelZ0, string Layer);

/// <summary>The three documents a placement stages, by file name.</summary>
public sealed record StagedFiles(string CellName, IReadOnlyList<(string Name, string Text)> Files);

public static class LineSection
{
    public const string CellName = "line";
    private const double C0 = 299_792_458.0;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>circuitRF's microstrip model at one width: its impedance and effective permittivity at the frequency.</summary>
    public delegate Task<(double Z0, double Eeff)> LineModel(double widthMetres);

    /// <summary>Checks the request before anything is asked of circuitRF. Null when it is usable.</summary>
    public static string? Problem(LineSpec spec)
    {
        if (!(spec.FrequencyHz > 0)) return "The frequency must be above 0 Hz.";
        if (!(spec.Z0 >= 10 && spec.Z0 <= 150)) return "The impedance must be between 10 and 150 Ohm.";
        if (!(spec.Degrees > 0 && spec.Degrees <= 720)) return "The electrical length must be above 0 and at most 720 degrees.";
        if (string.IsNullOrWhiteSpace(spec.Conductor)) return "Choose the conductor the line is drawn on.";
        return null;
    }

    public static async Task<LineDesign> DesignAsync(LineSpec spec, Technology tech, LineModel model)
    {
        var conductor = tech.Conductors.FirstOrDefault(c => c.Name == spec.Conductor)
                        ?? throw new InvalidOperationException($"The technology has no conductor called {spec.Conductor}.");
        double dbuPerMetre = tech.DbuPerMicron * 1e6;

        // Secant search on ln(width) for the impedance asked for.
        double x0 = Math.Log(1e-3), z0 = (await model(Math.Exp(x0))).Z0;
        double x1 = x0 + (z0 > spec.Z0 ? 0.4 : -0.4), z1 = (await model(Math.Exp(x1))).Z0;
        for (int i = 0; i < 30 && Math.Abs(z1 - spec.Z0) > 1e-6 * spec.Z0; i++)
        {
            if (z1 == z0) break;
            double step = Math.Clamp((spec.Z0 - z1) * (x1 - x0) / (z1 - z0), -1.5, 1.5);
            (x0, z0) = (x1, z1);
            x1 += step;
            z1 = (await model(Math.Exp(x1))).Z0;
        }

        // The drawn width is whole DBU, so the numbers reported are circuitRF's at THAT width.
        long widthDbu = Math.Max(1, Dbu(Math.Exp(x1), dbuPerMetre));
        double width = widthDbu / dbuPerMetre;
        var (modelZ0, eeff) = await model(width);
        double length = spec.Degrees / 360.0 * C0 / (spec.FrequencyHz * Math.Sqrt(eeff));
        long lengthDbu = Dbu(length, dbuPerMetre);
        return new LineDesign(widthDbu, lengthDbu, width, lengthDbu / dbuPerMetre, eeff, modelZ0, conductor.Layer);
    }

    /// <summary>A length in DBU, rounded once, half away from zero — as circuitRF rounds a typed length.</summary>
    public static long Dbu(double metres, double dbuPerMetre)
        => (long)Math.Round(metres * dbuPerMetre, MidpointRounding.AwayFromZero);

    /// <summary>The layout (the line, a port label at each end), the circuit, and an EM setup naming the layout.
    /// <paramref name="techRef"/> names the technology from the placed layout folder; null for the workspace's default.</summary>
    public static StagedFiles Documents(LineSpec spec, Technology tech, LineDesign d, string? techRef)
        => new(CellName,
        [
            (CellName + ".clay", Clay(tech, d, techRef)),
            (CellName + ".cnl", Netlist(spec, d)),
            (CellName + ".cem", EmSetup(spec, d)),
        ]);

    private static string Clay(Technology tech, LineDesign d, string? techRef)
    {
        var (layer, datatype) = tech.Layers.TryGetValue(d.Layer, out var key)
            ? key : throw new InvalidOperationException($"The technology has no drawing layer called {d.Layer}.");
        return Json(w =>
        {
            w.WriteNumber("FormatVersion", 1);
            w.WriteNumber("DbuPerMicron", tech.DbuPerMicron);
            if (techRef is not null) w.WriteString("TechRef", techRef);
            w.WriteStartArray("Shapes");
            w.WriteStartObject();
            w.WriteString("$type", "Rect");
            w.WriteNumber("X1", 0);
            w.WriteNumber("Y1", -(d.WidthDbu / 2));
            w.WriteNumber("X2", d.LengthDbu);
            w.WriteNumber("Y2", d.WidthDbu - d.WidthDbu / 2);
            Layer(w, "Layer", layer, datatype);
            w.WriteEndObject();
            // A port label faces INTO its conductor: port 1 at the left end faces +x (R0), port 2 at the right faces -x.
            Port(w, "1", 0, "R0");
            Port(w, "2", d.LengthDbu, "R180");
            w.WriteEndArray();
            w.WriteStartArray("Instances");
            w.WriteEndArray();

            void Port(Utf8JsonWriter w, string name, long x, string facing)
            {
                w.WriteStartObject();
                w.WriteString("$type", "Label");
                w.WriteNumber("X", x);
                w.WriteNumber("Y", 0);
                w.WriteString("Text", name);
                w.WriteNumber("Height", Math.Max(d.WidthDbu / 2, tech.DbuPerMicron));
                w.WriteString("Rotation", "R0");
                w.WriteBoolean("IsPort", true);
                w.WriteString("PortDirection", facing);
                Layer(w, "PortLayer", layer, datatype);
                Layer(w, "Layer", layer, datatype);
                w.WriteEndObject();
            }
        });
    }

    private static void Layer(Utf8JsonWriter w, string name, int layer, int datatype)
    {
        w.WriteStartObject(name);
        w.WriteNumber("Layer", layer);
        w.WriteNumber("Datatype", datatype);
        w.WriteEndObject();
    }

    private static string Netlist(LineSpec spec, LineDesign d)
    {
        double f = spec.FrequencyHz / 1e9;
        var sb = new StringBuilder();
        sb.Append(Inv, $"; A {G(spec.Degrees)} degree line at {G(f)} GHz: {G(d.Width * 1e3)} mm wide, {G(d.Length * 1e3)} mm long.\n");
        sb.Append(Inv, $"Port:P1 in 0 Num=1 Z={G(spec.Z0)} Ohm\n");
        sb.Append(Inv, $"TLIN:TL1 in out Z={G(d.ModelZ0)} Ohm L={G(d.Length * 1e3)} mm Eeff={G(d.Eeff)}\n");
        sb.Append(Inv, $"Port:P2 out 0 Num=2 Z={G(spec.Z0)} Ohm\n");
        sb.Append(Inv, $"analysis SP1 type=sparam start={G(f / 2)} stop={G(f * 1.5)} npts=101 Unit=GHz\n");
        return sb.ToString();
    }

    private static string EmSetup(LineSpec spec, LineDesign d)
    {
        double f = spec.FrequencyHz / 1e9;
        return Json(w =>
        {
            w.WriteNumber("FormatVersion", 1);
            w.WriteString("Name", CellName);
            // Relative to the staging folder; circuitRF re-points it at the placed layout.
            w.WriteString("LayoutRef", CellName + ".clay");
            w.WriteString("SignalStackupLayerName", spec.Conductor);
            w.WriteStartObject("Frequency");
            w.WriteString("StartExpr", G(f / 2));
            w.WriteString("StopExpr", G(f * 1.5));
            w.WriteString("StepExpr", "");
            w.WriteNumber("NumPoints", 21);
            w.WriteString("Mode", "PointCount");
            w.WriteString("Kind", "Linear");
            w.WriteString("StartUnit", "GHz");
            w.WriteString("StopUnit", "GHz");
            w.WriteString("StepUnit", "Hz");
            w.WriteEndObject();
            // Both ports referenced to the line's own impedance, so a well-made line shows |S11| near 0.
            w.WriteNumber("Port1Z0Real", Math.Round(spec.Z0, 6));
            w.WriteNumber("Port1Z0Imag", 0);
            w.WriteNumber("Port2Z0Real", Math.Round(spec.Z0, 6));
            w.WriteNumber("Port2Z0Imag", 0);
            w.WriteString("AnalysisKind", "Planar");
        });
    }

    /// <summary>Six significant digits, invariant culture: what a reader would write, and the same on every machine.</summary>
    private static string G(double v) => v.ToString("G6", Inv);

    private static string Json(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n") + "\n";
    }

    /// <summary>Writes the documents into the staging folder and returns the <c>placed</c> cell naming them.</summary>
    public static PlacedCell Stage(StagedFiles staged, string stagingDir)
    {
        var cell = new PlacedCell { Name = staged.CellName };
        foreach (var (name, text) in staged.Files)
        {
            File.WriteAllText(System.IO.Path.Combine(stagingDir, name), text, new UTF8Encoding(false));
            cell.Files.Add(name);
        }
        return cell;
    }
}
