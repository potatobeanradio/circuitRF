// brief-em3d-7 R-em3d7-4 — the Palace lowering's physics half: the resolved problem and the mesh
// groups GmshGeoWriter assigned, as Palace's JSON configuration.
//
// WRITTEN AGAINST THE PINNED VERSION'S SCHEMA (R-em3d7-4a; em-3d.md §5.3; F0 Q10). The schema Palace
// 0.18.1 installs is committed at testdata/em3d/palace-schema/0.18.1.json, and the gate validates this
// writer's goldens against it. Every key below is that schema's, never one from memory or from an
// example: Palace says its configuration is not compatible across versions, and its schema rejects an
// unknown key (additionalProperties: false) — which is the behaviour this writer wants, since a key
// read differently is a plausible wrong answer.
//
// Deterministic like the .geo (R-em3d7-2b): Utf8JsonWriter's round-trip number format, two-space
// indent, '\n' on every platform, groups in attribute order. NO comments: Palace's own parser accepts
// them, a schema validator does not.

using System.Numerics;
using System.Text;
using System.Text.Json;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

/// <summary>The configuration, or why the problem cannot be stated to Palace.</summary>
public sealed record PalaceConfig(string? Json, string? Refusal)
{
    public bool Ok => Refusal is null;
}

public static class PalaceConfigWriter
{
    public const string ConfigFile = "config.json";

    /// <summary>Palace's output directory, relative to the run directory.</summary>
    public const string OutputDirectory = "postpro";

    /// <summary>
    /// The configuration for <paramref name="problem"/> meshed as <paramref name="groups"/> describes.
    /// </summary>
    public static PalaceConfig Write(Em3dProblem problem, IReadOnlyList<Em3dGroup> groups, PalaceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(settings);

        var materials = problem.Materials.ToDictionary(m => m.Name, StringComparer.Ordinal);
        materials.TryAdd(GmshGeoWriter.FreeSpace.Name, GmshGeoWriter.FreeSpace);

        if (problem.IsStatic && StaticRefusal(problem) is { } staticRefusal) return new(null, staticRefusal);

        // A lumped port's reference is a resistance. A reactive Z0 would need an inductance or a
        // capacitance, which is one frequency's reactance and not the whole sweep's: refused rather
        // than fitted at a frequency nobody chose.
        foreach (var p in problem.Ports)
            if (p.Z0.Imaginary != 0 || !(p.Z0.Real > 0))
                return new(null, $"Port {p.Number}'s reference impedance is {Ohms(p.Z0)}. A Palace lumped port " +
                                 "is referenced to a positive resistance; a reactive or non-positive reference is " +
                                 "not supported in this version. Set the port's Z0 to a real, positive value.");

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            w.WriteStartObject();

            w.WriteStartObject("Problem");
            w.WriteString("Type", problem.Type.ToString());
            w.WriteNumber("Verbose", 2);
            w.WriteString("Output", OutputDirectory);
            w.WriteEndObject();

            w.WriteStartObject("Model");
            w.WriteString("Mesh", GmshGeoWriter.MeshFile);
            w.WriteNumber("L0", GmshGeoWriter.LengthUnitM);
            w.WriteStartObject("Refinement");
            w.WriteNumber("Tol", settings.AdaptiveTol);
            w.WriteNumber("MaxIts", settings.AdaptiveMaxIterations);
            w.WriteEndObject();
            w.WriteEndObject();

            // ── Domains: a material per meshed volume group ───────────────────────────────────
            w.WriteStartObject("Domains");
            w.WriteStartArray("Materials");
            foreach (var g in groups.Where(g => g.Dimension == 3))
            {
                var m = materials[g.Material!];
                w.WriteStartObject();
                Attributes(w, [g.Attribute]);
                w.WriteNumber("Permeability", m.Mur);
                if (m.EpsrTensor is { Count: 3 } t)
                {
                    w.WriteStartArray("Permittivity");
                    foreach (double v in t) w.WriteNumberValue(v);
                    w.WriteEndArray();
                }
                else w.WriteNumber("Permittivity", m.Epsr);
                w.WriteNumber("LossTan", m.TanD);
                if (m.SigmaSm > 0 && !double.IsInfinity(m.SigmaSm)) w.WriteNumber("Conductivity", m.SigmaSm);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();

            if (problem.IsStatic) WriteStatic(w, problem, groups, settings);
            else WriteDriven(w, problem, groups, settings, materials);

            w.WriteEndObject();
        }
        return new(Encoding.UTF8.GetString(ms.ToArray()) + "\n", null);
    }

    /// <summary>The Boundaries and Solver of a driven problem: brief-em3d-7's, unchanged.</summary>
    private static void WriteDriven(Utf8JsonWriter w, Em3dProblem problem, IReadOnlyList<Em3dGroup> groups,
                                    PalaceSettings settings, Dictionary<string, Em3dMaterial> materials)
    {
        // ── Boundaries ─────────────────────────────────────────────────────────────────────
        var pec        = new List<int>();
        var pmc        = new List<int>();
        var absorbing  = new List<int>();
        var conductive = new List<(Em3dGroup Group, Em3dMaterial Metal)>();
        foreach (var g in groups)
        {
            switch (g.Kind)
            {
                case Em3dGroupKind.Face when g.Boundary == Em3dBoundaryKind.Pec: pec.Add(g.Attribute); break;
                case Em3dGroupKind.Face when g.Boundary == Em3dBoundaryKind.Pmc: pmc.Add(g.Attribute); break;
                case Em3dGroupKind.Face:                                          absorbing.Add(g.Attribute); break;
                case Em3dGroupKind.Conductor or Em3dGroupKind.Sheet:
                {
                    var metal = materials[g.Material!];
                    // A conductor stated with no finite conductivity is a perfect one.
                    if (!(metal.SigmaSm > 0) || double.IsInfinity(metal.SigmaSm)) pec.Add(g.Attribute);
                    else conductive.Add((g, metal));
                    break;
                }
            }
        }

        w.WriteStartObject("Boundaries");
        if (pec.Count > 0)
        {
            w.WriteStartObject("PEC");
            Attributes(w, pec);
            w.WriteEndObject();
        }
        if (pmc.Count > 0)
        {
            w.WriteStartObject("PMC");
            Attributes(w, pmc);
            w.WriteEndObject();
        }
        if (absorbing.Count > 0)
        {
            w.WriteStartObject("Absorbing");
            Attributes(w, absorbing);
            w.WriteNumber("Order", 1);
            w.WriteEndObject();
        }
        if (conductive.Count > 0)
        {
            w.WriteStartArray("Conductivity");
            foreach (var (g, metal) in conductive)
            {
                w.WriteStartObject();
                Attributes(w, [g.Attribute]);
                w.WriteNumber("Conductivity", metal.SigmaSm);
                w.WriteNumber("Permeability", metal.Mur);
                // A sheet carries its real thickness, in the mesh's length unit.
                if (g.ThicknessM is { } t && t > 0)
                    w.WriteNumber("Thickness", Math.Round(t / GmshGeoWriter.LengthUnitM, 9));
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        // One lumped port per port, each its own excitation, so Palace solves every column of S
        // and writes it to port-S.csv under the port's own number.
        w.WriteStartArray("LumpedPort");
        foreach (var g in groups.Where(g => g.Kind == Em3dGroupKind.Port))
        {
            var p = problem.Ports.Single(q => q.Number == g.PortNumber);
            w.WriteStartObject();
            w.WriteNumber("Index", p.Number);
            Attributes(w, [g.Attribute]);
            WriteDirection(w, p);
            w.WriteNumber("R", p.Z0.Real);
            w.WriteNumber("Excitation", p.Number);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();

        // ── Solver ─────────────────────────────────────────────────────────────────────────
        var f = problem.Frequency;
        w.WriteStartObject("Solver");
        w.WriteNumber("Order", settings.ElementOrder);
        w.WriteString("Device", "CPU");
        w.WriteStartObject("Driven");
        w.WriteStartArray("Samples");
        w.WriteStartObject();
        if (f.Points == 1 || f.StopHz == f.StartHz)
        {
            w.WriteString("Type", "Point");
            w.WriteStartArray("Freq");
            w.WriteNumberValue(f.StartHz / 1e9);
            w.WriteEndArray();
        }
        else
        {
            w.WriteString("Type", f.Kind == Em3dSweepKind.Log ? "Log" : "Linear");
            w.WriteNumber("MinFreq", f.StartHz / 1e9);
            w.WriteNumber("MaxFreq", f.StopHz / 1e9);
            w.WriteNumber("NSample", f.Points);
        }
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteNumber("AdaptiveTol", settings.SweepAdaptiveTol);
        w.WriteEndObject();
        // F0's linear solver, which every reference ran with.
        w.WriteStartObject("Linear");
        w.WriteString("Type", "Default");
        w.WriteString("KSPType", "GMRES");
        w.WriteNumber("Tol", 1e-8);
        w.WriteNumber("MaxIts", 400);
        w.WriteEndObject();
        w.WriteEndObject();

    }

    // ── brief-em3d-22 — the electrostatic and magnetostatic problems ─────────────────────────────

    /// <summary>
    /// What Palace cannot be told about a static problem, before anything runs. <b>Palace 0.18.1's
    /// electrostatic solver has no floating conductor</b> (R-em3d22-2b): its boundaries are Terminal
    /// (a prescribed potential), Ground and ZeroCharge, and ZeroCharge is a Neumann wall, not an
    /// isolated equipotential. So an electrostatic problem with a conductor in no terminal is refused
    /// naming it, never grounded silently. A magnetostatic conductor in no terminal is simply a
    /// conductor carrying no source current.
    /// </summary>
    public static string? StaticRefusal(Em3dProblem problem)
    {
        if (problem.Type == Em3dProblemType.Electrostatic && problem.FloatingConductors() is { Count: > 0 } floating)
            return $"{string.Join(", ", floating.Select(n => $"'{n}'"))} {(floating.Count == 1 ? "is" : "are")} in no terminal " +
                   "and not ground. Palace's electrostatic solver (0.18.1) has no floating conductor — its only boundaries " +
                   "are a terminal's potential, ground and a zero-charge wall — so the metal would have to be grounded or " +
                   "given a potential, and either changes the matrix. List its net as a terminal, or make it the ground.";
        return null;
    }

    /// <summary>
    /// The Boundaries and Solver of a static problem. Each air-box face is what it is: a PEC face is
    /// ground (electrostatic) or a perfect conductor (magnetostatic); a PMC face — and an absorbing
    /// one, which a static field has no radiation to absorb through — is the natural boundary, Palace's
    /// ZeroCharge (no field line ends on it) or PMC (no current crosses it). Conductivity plays no part
    /// in either solve (Palace says so itself for the magnetostatic one), so every conductor is a
    /// perfect one here, whatever its metal.
    /// </summary>
    private static void WriteStatic(Utf8JsonWriter w, Em3dProblem problem, IReadOnlyList<Em3dGroup> groups, PalaceSettings settings)
    {
        bool es = problem.Type == Em3dProblemType.Electrostatic;
        var ground   = new List<int>();         // ES: Ground; MS: PEC
        var natural  = new List<int>();         // ES: ZeroCharge; MS: PMC
        var terminal = problem.Terminals.Select(_ => new List<int>()).ToList();
        var index = problem.Terminals.Select((t, k) => (t.Name, k)).ToDictionary(x => x.Name, x => x.k, StringComparer.Ordinal);
        foreach (var g in groups)
        {
            switch (g.Kind)
            {
                case Em3dGroupKind.Face when g.Boundary == Em3dBoundaryKind.Pec: ground.Add(g.Attribute); break;
                case Em3dGroupKind.Face:                                          natural.Add(g.Attribute); break;
                case Em3dGroupKind.Conductor or Em3dGroupKind.Sheet:
                    if (es && g.Terminal is { Length: > 0 } t) terminal[index[t]].Add(g.Attribute);
                    else ground.Add(g.Attribute);       // ES: the ground's conductors; MS: every conductor
                    break;
            }
        }

        w.WriteStartObject("Boundaries");
        if (ground.Count > 0)
        {
            w.WriteStartObject(es ? "Ground" : "PEC");
            Attributes(w, ground);
            w.WriteEndObject();
        }
        if (natural.Count > 0)
        {
            w.WriteStartObject(es ? "ZeroCharge" : "PMC");
            Attributes(w, natural);
            w.WriteEndObject();
        }
        if (es)
        {
            // One terminal per matrix row, indexed 1..N in the setup's order: terminal-C.csv's columns.
            w.WriteStartArray("Terminal");
            for (int k = 0; k < problem.Terminals.Count; k++)
            {
                w.WriteStartObject();
                w.WriteNumber("Index", k + 1);
                Attributes(w, terminal[k]);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        else
        {
            // R-em3d22-4a — each terminal's current enters through its source port's sheet: Palace drives
            // one ampere across it, in the port's direction, one terminal at a time.
            w.WriteStartArray("SurfaceCurrent");
            for (int k = 0; k < problem.Terminals.Count; k++)
            {
                var port = problem.Ports.Single(p => p.Name == problem.Terminals[k].SourcePort);
                var sheet = groups.Single(g => g.Kind == Em3dGroupKind.Port && g.PortNumber == port.Number);
                w.WriteStartObject();
                w.WriteNumber("Index", k + 1);
                Attributes(w, [sheet.Attribute]);
                WriteDirection(w, port);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();

        w.WriteStartObject("Solver");
        w.WriteNumber("Order", settings.ElementOrder);
        w.WriteString("Device", "CPU");
        w.WriteStartObject(es ? "Electrostatic" : "Magnetostatic");
        w.WriteEndObject();
        // Palace's own choice of solver for each problem (CG, with AMG or AMS). The magnetostatic
        // tolerance is looser, and measured: curl-curl is singular, and a coaxial source's 1/r current
        // is divergence-free only to quadrature accuracy, so on the coax gate CG reached a relative
        // residual of 1.6e-5 and then DIVERGED (to 1e8) at a tighter tolerance. At 1e-4 it stops before
        // that, 0.06 % from the closed form (src/Design/RESOLVED.md §brief-em3d-22).
        w.WriteStartObject("Linear");
        w.WriteNumber("Tol", es ? 1e-8 : 1e-4);
        w.WriteNumber("MaxIts", 400);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    /// <summary>A port's direction: +R/-R for a coaxial one, +X… for an axis, else the vector.</summary>
    private static void WriteDirection(Utf8JsonWriter w, Em3dPort p)
    {
        if (p.Annulus is { } ring) w.WriteString("Direction", ring.Outward ? "+R" : "-R");
        else if (AxisDirection(p.Direction) is { } axis) w.WriteString("Direction", axis);
        else
        {
            w.WriteStartArray("Direction");
            w.WriteNumberValue(p.Direction.X);
            w.WriteNumberValue(p.Direction.Y);
            w.WriteNumberValue(p.Direction.Z);
            w.WriteEndArray();
        }
    }

    private static void Attributes(Utf8JsonWriter w, IEnumerable<int> attributes)
    {
        w.WriteStartArray("Attributes");
        foreach (int a in attributes) w.WriteNumberValue(a);
        w.WriteEndArray();
    }

    /// <summary>"+Z" and so on for an axis-aligned unit vector; null for any other.</summary>
    private static string? AxisDirection(Point3 d) => d switch
    {
        { X: 1, Y: 0, Z: 0 }  => "+X",
        { X: -1, Y: 0, Z: 0 } => "-X",
        { X: 0, Y: 1, Z: 0 }  => "+Y",
        { X: 0, Y: -1, Z: 0 } => "-Y",
        { X: 0, Y: 0, Z: 1 }  => "+Z",
        { X: 0, Y: 0, Z: -1 } => "-Z",
        _ => null,
    };

    private static string Ohms(Complex z)
        => $"{GmshGeoWriter.Num(z.Real)}{(z.Imaginary < 0 ? "-" : "+")}j{GmshGeoWriter.Num(Math.Abs(z.Imaginary))} Ohm";
}
