using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;
using CircuitRF.Engine.Mom;
using NumFlat;
using RfCore;

namespace CircuitRF.Design.Filters;

/// <summary>
/// A realised filter on Planar MoM (docs/design/filter-synthesis.md §6d), the run the Filter Designer and
/// <c>circuitrf filter --em</c> share. The part is drawn as its PCell draws it, from the plan its model stamps. Ports 1 and
/// 2 are grounded edge ports on its pins 1 and 2, and each pair of site pins gets a port of its own: an internal delta gap
/// across a strip joining a series site's two pins, or a port to ground through a via grown under a shunt site's pin. The
/// N-port is solved as <see cref="CouplingCurvePlanarSolver"/> solves a coupling bench, on its coarse mesh, then closed
/// with the sites' values.
/// </summary>
public static class FilterEmService
{
    /// <summary>The filter's two-port; with sites, also the N-port before they were closed, its ports 1 and 2 and then one
    /// per pin pair, named in <paramref name="SitePorts"/> (<c>G1-G2</c>, or <c>G3</c> for a shunt site).</summary>
    public sealed record Result(SNP S, SNP? Raw, IReadOnlyList<string> SitePorts);

    /// <summary>Solves <paramref name="realisation"/> on <paramref name="substrate"/> at <paramref name="fHz"/>, in the
    /// spec's Z0. Refuses with a <c>filter.em.*</c> <see cref="ParameterRefusalException"/>: <c>part</c> for a part with no
    /// drawing, <c>sites</c> for a site no port can stand on. <paramref name="threads"/> is capped at 8.</summary>
    public static Result Run(FilterSpec spec, FilterRealisation realisation, MicrostripSubstrate substrate,
        IReadOnlyList<double> fHz, IProgress<RunProgress>? progress = null, CancellationToken token = default, int threads = 8)
    {
        var plan = Plan(spec, realisation, substrate);
        var cell = PassivePlanPCells.Draw(plan, null, PCellLayerSelection.Default);
        const double m = CouplingCurvePlanarSolver.MetresPerDbu;
        PCellPin? Pin(string name) => cell.Pins.FirstOrDefault(p => p.Name == name);
        if (Pin(plan.Pins[0]) is not { } p1 || Pin(plan.Pins[1]) is not { } p2)
            throw Refuse("part", "the {type} has no pins 1 and 2 to put the ports on.", ("type", realisation.PartType));

        var shapes = new List<LayoutShape>(cell.Shapes);
        var ports = new List<PlanarPort>
            { CouplingCurvePlanarSolver.EdgePort(1, p1, spec.Z0, grounded: true), CouplingCurvePlanarSolver.EdgePort(2, p2, spec.Z0, grounded: true) };
        var sites = realisation.Sites.GroupBy(x => (x.PinA, x.PinB)).ToList();
        foreach (var site in sites)
        {
            string name = Named(site.Key);
            if (site.FirstOrDefault(x => x.Role.ToUpperInvariant() is not ("R" or "L" or "C" or "VIA")) is { } other)
                throw Refuse("sites", "site {site} holds a {role}, which has no value to close its port with.", ("site", name), ("role", other.Role));
            if (Pin(site.Key.PinA) is not { } a)
                throw Refuse("sites", "site {site}'s pin {pin} is not on the drawn part.", ("site", name), ("pin", site.Key.PinA));
            int n = ports.Count + 1;
            if (site.Key.PinB is null)
            {
                // A via of the board's drill, or half the pad where that is narrower, its centre one via in from the pin.
                double w = Math.Min(ComponentModelFactory.DefaultViaDrillMeters, 0.5 * a.WidthDbu * m), t = a.OutwardDirectionDeg * Math.PI / 180.0;
                ports.Add(new PlanarPort(n, new EmPoint(a.X * m - w * Math.Cos(t), a.Y * m - w * Math.Sin(t)), PlanarPortSide.MinX, spec.Z0,
                    Kind: PlanarPortKind.Internal, GroundPathWidthM: w));
                continue;
            }
            if (Pin(site.Key.PinB) is not { } b)
                throw Refuse("sites", "site {site}'s pin {pin} is not on the drawn part.", ("site", name), ("pin", site.Key.PinB));
            // The planar mesh cannot span a slot (EmPlanarComponentPorts), so the gap gets a strip half the narrower pin
            // wide, which leaves the pads' faces as gridlines for the cut, and the port is cut across it.
            bool alongX = a.Y == b.Y && a.X != b.X, alongY = a.X == b.X && a.Y != b.Y;
            if (!(alongX || alongY) || !Faces(a, b) || !Faces(b, a))
                throw Refuse("sites", "site {site}'s pins do not face each other across a straight gap, so no gap port can cross it.", ("site", name));
            long h = Math.Min(a.WidthDbu, b.WidthDbu) / 4;
            shapes.Add(alongX
                ? new RectShape { Layer = a.Layer, X1 = Math.Min(a.X, b.X), X2 = Math.Max(a.X, b.X), Y1 = a.Y - h, Y2 = a.Y + h }
                : new RectShape { Layer = a.Layer, X1 = a.X - h, X2 = a.X + h, Y1 = Math.Min(a.Y, b.Y), Y2 = Math.Max(a.Y, b.Y) });
            ports.Add(new PlanarPort(n, new EmPoint(0.5 * (a.X + b.X) * m, 0.5 * (a.Y + b.Y) * m),
                alongX ? PlanarPortSide.MinX : PlanarPortSide.MinY, spec.Z0, Kind: PlanarPortKind.InternalDeltaGap));
        }

        double[] f = [.. fHz.Order()];
        var raw = new CouplingCurvePlanarSolver(Threads: Math.Clamp(threads, 1, 8)).Solve(shapes, ports, substrate, f,
            new RunControl { Token = token, Progress = progress, Total = f.Length });
        double[] solved = f[..raw.Length];
        var z0 = new Complex(spec.Z0, 0.0);
        var closed = raw.Select((s, i) => Close(s, [.. sites.Select(x => Gamma(x, solved[i], spec.Z0))])).ToArray();
        return new Result(new SNP(solved, closed, MatrixType.S, MatrixFormat.RI, z0),
            sites.Count == 0 ? null : new SNP(solved, [.. raw.Select(s => ToMat(s, ports.Count))], MatrixType.S, MatrixFormat.RI, z0),
            [.. sites.Select(x => Named(x.Key))]);
    }

    private static string Named((string PinA, string? PinB) site) => site.PinB is { } b ? $"{site.PinA}-{b}" : site.PinA;

    /// <summary>The two-port left when site port r (ports 3 on) is closed on a load of reflection g[r]:
    /// S' = S_ee + S_es·Γ·(I − S_ss·Γ)⁻¹·S_se.</summary>
    private static Mat<Complex> Close(Complex[,] s, Complex[] g)
    {
        var two = ToMat(s, 2);
        int k = g.Length;
        if (k == 0) return two;
        var a = new Mat<Complex>(k, k);
        for (int r = 0; r < k; r++)
            for (int c = 0; c < k; c++) a[r, c] = (r == c ? 1.0 : 0.0) - s[2 + r, 2 + c] * g[c];
        var inv = a.Inverse();
        for (int p = 0; p < 2; p++)
            for (int q = 0; q < 2; q++)
                for (int r = 0; r < k; r++)
                    for (int c = 0; c < k; c++) two[p, q] += s[p, 2 + r] * g[r] * inv[r, c] * s[2 + c, q];
        return two;
    }

    private static Mat<Complex> ToMat(Complex[,] s, int n)
    {
        var mat = new Mat<Complex>(n, n);
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++) mat[r, c] = s[r, c];
        return mat;
    }

    /// <summary>The plan the realised part's model stamps, which is what its PCell draws: the part read off the bench's own
    /// elaboration, so its values are read exactly as a run reads them.</summary>
    private static PassivePlan Plan(FilterSpec spec, FilterRealisation realisation, MicrostripSubstrate s)
    {
        var (lib, tb) = new CnlReader().Read(FilterSynthesisService.Cnl(spec, realisation, "", s, null, 2, []));
        using var netlist = new Elaborator(lib).Elaborate(tb);
        return PassiveSites.PlansOf(netlist).GetValueOrDefault("F1")
               ?? throw Refuse("part", "a {type} is not a planned passive, so there is no drawing to solve.", ("type", realisation.PartType));
    }

    /// <summary>Whether <paramref name="a"/>'s outward direction points towards <paramref name="b"/>.</summary>
    private static bool Faces(PCellPin a, PCellPin b)
    {
        double t = a.OutwardDirectionDeg * Math.PI / 180.0;
        return Math.Cos(t) * (b.X - a.X) + Math.Sin(t) * (b.Y - a.Y) > 0;
    }

    /// <summary>The reflection of a site's parts, in parallel on its port: −1 for a short (a via, or 0 Ω).</summary>
    private static Complex Gamma(IEnumerable<SiteValue> parts, double fHz, double z0)
    {
        double w = 2.0 * Math.PI * fHz;
        Complex y = 0;
        foreach (var x in parts)
        {
            Complex z = x.Role.ToUpperInvariant() switch
            {
                "R" => x.Value, "L" => new Complex(0, w * x.Value), "C" => new Complex(0, -1.0 / (w * x.Value)), _ => 0,
            };
            if (z == Complex.Zero) return -1;
            y += 1.0 / z;
        }
        return (1.0 - z0 * y) / (1.0 + z0 * y);
    }

    private static ParameterRefusalException Refuse(string code, string message, params (string, object?)[] args)
        => new(Diagnostic.Create("filter.em." + code, DiagnosticSeverity.Error, "Planar EM: " + message, args));
}
