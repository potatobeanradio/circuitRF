using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;
using NumFlat;
using RfCore;

namespace CircuitRF.Design.Filters;

/// <summary>
/// The one seam the Filter Designer, <c>circuitrf filter</c> and the MCP tool share (docs/design/filter-synthesis.md §6):
/// the substrate a technology gives a realiser, the realised part as a placed part, the test bench it runs in, and the
/// ideal response it is judged against. The realisers themselves are Core's (<see cref="FilterRealisers"/>); nothing here
/// designs anything.
/// </summary>
public static class FilterSynthesisService
{
    /// <summary>The microstrip substrate between <paramref name="signal"/> and <paramref name="ground"/> (the stack's own
    /// defaults when null), as a run resolves it; null, with why, when the technology has none.</summary>
    public static MicrostripSubstrate? Substrate(Technology? technology, string? signal, string? ground, out string? refusal)
    {
        var (s, failure, _) = SubstrateResolver.ResolveElectrical(technology, new PCellLayerSelection(signal, ground));
        refusal = failure?.Reason;
        return s is null ? null
            : new MicrostripSubstrate(s.HeightMeters, s.ThicknessMeters, s.RelativePermittivity, s.ConductivitySPerM, s.LossTangent);
    }

    /// <summary>The realised part as a palette drop makes it, each row the realisation states set to its value. A value
    /// with a spaced unit (<c>50 Ohm</c>) is split into the row's expression and unit; any other value is the row's
    /// expression with no unit, so a glued unit (<c>1.2mm</c>) or a word reads as written.</summary>
    public static EditableComponent Part(FilterRealisation realisation, string instanceName)
    {
        if (!ComponentTypeRegistry.TryParseCode(realisation.PartType, out var kind, out int ports))
            throw new ArgumentException($"'{realisation.PartType}' is not a part type.", nameof(realisation));
        var info = ComponentTypeRegistry.Get(kind);
        var part = new EditableComponent
        {
            InstanceName = instanceName, Symbol = kind,
            ShowTypeLabel = info.DefaultShowTypeLabel, ShowInstanceName = info.DefaultShowInstanceName,
        };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, ports))
            part.Parameters.Add(new EditableParameter
                { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension });
        foreach (var (name, value) in realisation.Parameters)
        {
            var (expression, unit) = Units.SplitTrailingUnit(value, includeIdentityUnits: true);
            var row = part.Parameters.FirstOrDefault(p => p.Name == name);
            if (row is null) part.Parameters.Add(row = new EditableParameter { Name = name, ShowOnSchematic = false });
            row.Expression = expression;
            row.Unit = unit ?? "";
        }
        return part;
    }

    /// <summary>Where the bench sweeps: a lowpass from near DC to three times its cutoff, a highpass from a third of its
    /// cutoff to three times it, a band two bandwidths either side of its edges.</summary>
    public static double[] Frequencies(FilterSpec spec, int points)
    {
        double bw = spec.F2Hz - spec.F1Hz;
        var (start, stop) = spec.Band switch
        {
            FilterBand.Lowpass => (spec.F1Hz / 50.0, 3.0 * spec.F1Hz),
            FilterBand.Highpass => (spec.F1Hz / 3.0, 3.0 * spec.F1Hz),
            _ => (Math.Max(spec.F1Hz - 2.0 * bw, spec.F1Hz / 3.0), spec.F2Hz + 2.0 * bw),
        };
        return [.. Enumerable.Range(0, points).Select(i => start + (stop - start) * i / Math.Max(1, points - 1))];
    }

    /// <summary>
    /// The realisation as a runnable <c>.cnl</c>: two ports of Z0, the part with its pins 1 and 2 on them and every other
    /// pin on a net of its own name, one ordinary part per site (an R, L or C of the site's value with the part's
    /// <c>Footprint</c>, else the R/C default on <paramref name="technology"/>; a VIAGND on a via) and an S-parameter
    /// sweep over <see cref="Frequencies"/>. The substrate is stated on the part's line, so the netlist runs the same
    /// in any folder; a site of another role is left out, with a note in <paramref name="notes"/>.
    /// </summary>
    public static string Cnl(FilterSpec spec, FilterRealisation realisation, string realiser, MicrostripSubstrate? substrate,
        Technology? technology, int points, List<string> notes)
        => Circuit(spec, realisation, realiser, substrate, technology, notes) + Analysis(spec, points);

    /// <summary><see cref="Cnl"/> without its sweep: the two ports, the part and its site parts. With no
    /// <paramref name="substrate"/> nothing states one, so the parts take their technology's, as a designed cell's do
    /// (brief-pcd-12 D3).</summary>
    public static string Circuit(FilterSpec spec, FilterRealisation realisation, string realiser, MicrostripSubstrate? substrate,
        Technology? technology, List<string> notes)
    {
        var part = Part(realisation, "F1");
        string Net(string pin) => pin switch { "1" => "p1", "2" => "p2", _ => pin.ToLowerInvariant() };
        string sub = substrate is { } s ? $" H={F(s.HMeters)} T={F(s.TMeters)} Er={F(s.EpsR)} Sigma={F(s.SigmaSPerM)} TanD={F(s.TanD)}" : "";

        var b = new StringBuilder()
            .AppendLine($"; {realiser}: {Describe(spec)}")
            .AppendLine($"Port:P1 p1 0 Num=1 Z={F(spec.Z0)} Ohm")
            .AppendLine($"Port:P2 p2 0 Num=2 Z={F(spec.Z0)} Ohm")
            .Append($"{realisation.PartType}:F1 ").Append(string.Join(" ", SymbolPortDefs.For(part).Select(d => Net(d.Name))));
        // Every row a placed part would extract, as the extractor writes them: its layers are resolved into the substrate
        // above, and its Footprint is the site parts' (below).
        foreach (var p in part.Parameters.Where(p => p.Expression.Trim().Length > 0
                                                     && p.Name is not ("SignalLayer" or "GroundReference")
                                                     && !ArtworkParameters.IsArtworkOnly(p.Name)))
        {
            string e = p.Expression.Trim(), unit = UnitNormalizer.ToEngineUnit(p.Unit);
            if (!e.StartsWith('"') && e.IndexOfAny([' ', ',', ';', '(', '=']) >= 0) e = $"\"{e}\"";
            b.Append($" {p.Name}={e}").Append(unit.Length > 0 ? " " + unit : "");
        }
        b.AppendLine(sub);

        string? footprint = PassiveSites.FootprintOf(part);
        int k = 0;
        foreach (var site in realisation.Sites)
        {
            string a = Net(site.PinA), c = site.PinB is { } pb ? Net(pb) : "0", role = site.Role.ToUpperInvariant();
            k++;
            if (role == "VIA") { b.AppendLine($"VIAGND:V{k} {a} 0" + (substrate is { } v ? $" H={F(v.HMeters)} Er={F(v.EpsR)}" : "")); continue; }
            var kind = role switch { "R" => SymbolKind.Resistor, "L" => SymbolKind.Inductor, "C" => SymbolKind.Capacitor, _ => (SymbolKind?)null };
            if (kind is not { } rlc) { notes.Add($"Site {site.PinA}{(site.PinB is null ? "" : "-" + site.PinB)} ({site.Role}) is left to you."); continue; }
            string land = footprint ?? FootprintDefaults.For(rlc, technology) ?? "";
            b.AppendLine($"{role}:{role}{k} {a} {c} {role}={F(site.Value)}" + (land.Length > 0 ? $" Footprint={land}" : ""));
        }
        return b.ToString();
    }

    /// <summary>
    /// The ideal response as a <c>.cnl</c>: the doubly terminated lowpass prototype (<see cref="LowpassPrototype"/>),
    /// series element first, transformed to the band by the textbook frequency transformations (Matthaei, Young and
    /// Jones, as cited on the prototype; a book, no DOI). Port 2 is the prototype's load, g(N+1)·Z0 for an even order
    /// and Z0 otherwise, so an even Chebyshev's ripple is its own and not a mismatch.
    /// </summary>
    public static string Ideal(FilterSpec spec, int points)
    {
        double[] g = LowpassPrototype.GValues(spec.Response, spec.Order, spec.RippleDb);
        int n = spec.Order, mid = 0;
        double z = spec.Z0, w = 2.0 * Math.PI * spec.CentreHz, d = spec.FractionalBandwidth;
        var b = new StringBuilder().AppendLine($"; the ideal {Describe(spec)}").AppendLine($"Port:P1 p1 0 Num=1 Z={F(z)} Ohm");
        string at = "p1";
        for (int k = 1; k <= n; k++)
        {
            double gk = g[k];
            bool series = k % 2 == 1;
            // The arm's L and C (zero when it has none) and whether the two are in parallel.
            var (l, c, parallel) = (spec.Band, series) switch
            {
                (FilterBand.Lowpass, true) => (gk * z / w, 0.0, false),
                (FilterBand.Lowpass, false) => (0.0, gk / (z * w), false),
                (FilterBand.Highpass, true) => (0.0, 1.0 / (w * gk * z), false),
                (FilterBand.Highpass, false) => (z / (w * gk), 0.0, false),
                (FilterBand.Bandpass, true) => (gk * z / (w * d), d / (w * gk * z), false),
                (FilterBand.Bandpass, false) => (d * z / (w * gk), gk / (w * d * z), true),
                (FilterBand.Bandstop, true) => (gk * d * z / w, 1.0 / (w * gk * d * z), true),
                _ => (z / (w * gk * d), gk * d / (w * z), false),
            };
            string from = at, to = series ? $"n{k}" : "0";
            if (series) at = to;
            if (l > 0 && c > 0 && !parallel)
            {
                string m = $"m{++mid}";
                b.AppendLine($"L:L{k} {from} {m} L={F(l)}").AppendLine($"C:C{k} {m} {to} C={F(c)}");
                continue;
            }
            if (l > 0) b.AppendLine($"L:L{k} {from} {to} L={F(l)}");
            if (c > 0) b.AppendLine($"C:C{k} {from} {to} C={F(c)}");
        }
        double load = n % 2 == 0 ? g[n + 1] * z : z / g[n + 1];
        return b.AppendLine($"Port:P2 {at} 0 Num=2 Z={F(load)} Ohm").Append(Analysis(spec, points)).ToString();
    }

    /// <summary>
    /// A bandpass's ideal response with every resonator at the unloaded Q <paramref name="unloadedQ"/>: the inline coupling
    /// matrix of the same lowpass prototype (<see cref="CouplingMatrix.AllPole"/>) through
    /// <see cref="CouplingMatrix.Response"/> at ω = (f/f0 − f0/f)/FBW, which with no loss is <see cref="Ideal"/>'s own
    /// response. Only S11 and S21 are computed; S22 is taken as S11.
    /// </summary>
    public static SNP LossyIdeal(FilterSpec spec, double[] frequencies, double unloadedQ)
    {
        var m = CouplingMatrix.AllPole(LowpassPrototype.GValues(spec.Response, spec.Order, spec.RippleDb));
        double f0 = spec.CentreHz, fbw = spec.FractionalBandwidth;
        var mats = new Mat<Complex>[frequencies.Length];
        for (int i = 0; i < mats.Length; i++)
        {
            var (s11, s21) = CouplingMatrix.Response(m, (frequencies[i] / f0 - f0 / frequencies[i]) / fbw, unloadedQ, fbw);
            mats[i] = new Mat<Complex>(2, 2);
            mats[i][0, 0] = mats[i][1, 1] = s11;
            mats[i][0, 1] = mats[i][1, 0] = s21;
        }
        return new SNP(frequencies, mats, MatrixType.S, MatrixFormat.RI, new Complex(spec.Z0, 0.0));
    }

    /// <summary>A bench's S-parameters over its own sweep, read and run as a run verb reads a <c>.cnl</c>: the two-port's
    /// S at each frequency, labelled with <paramref name="z0"/> (each port's S is referred to its own Port's Z).</summary>
    public static SNP Response(string cnl, double[] frequencies, double z0)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        using var netlist = new Elaborator(lib).Elaborate(tb);
        var cube = SParameterEngine.Run(netlist, frequencies)["S"];
        var raw = cube.ComplexValues;
        var mats = new Mat<Complex>[frequencies.Length];
        for (int f = 0; f < mats.Length; f++)
        {
            mats[f] = new Mat<Complex>(2, 2);
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    mats[f][i, j] = raw[f * 4 + i * 2 + j];
        }
        return new SNP(frequencies, mats, MatrixType.S, MatrixFormat.RI, new Complex(z0, 0.0));
    }

    /// <summary>The N+2 matrix a bandpass is checked against: the all-pole prototype's inline chain
    /// (<see cref="CouplingMatrix.AllPole"/>), or with transmission zeros the folded generalised Chebyshev
    /// (<see cref="CouplingMatrix.Synthesise"/>) at the ripple's return loss.</summary>
    public static double[,] Target(FilterSpec spec)
    {
        if (spec.TransmissionZerosHz is not { Count: > 0 } zeros)
            return CouplingMatrix.AllPole(LowpassPrototype.GValues(spec.Response, spec.Order, spec.RippleDb));
        double f0 = spec.CentreHz, fbw = spec.FractionalBandwidth;
        return CouplingMatrix.Synthesise(spec.Order, -10.0 * Math.Log10(1.0 - Math.Pow(10.0, -spec.RippleDb / 10.0)),
            [.. zeros.Select(f => (f / f0 - f0 / f) / fbw)]);
    }

    /// <summary>A two-port's prototype back from its S (<see cref="FilterExtraction"/>), checked against the spec and
    /// <see cref="Target"/>: what the Designer's metrics and the polish read.</summary>
    public static ExtractedFilter Extract(FilterSpec spec, SNP s)
        => FilterExtraction.Extract(spec, s.Frequencies, [.. s.Matrices.Select(m => m[0, 0])], [.. s.Matrices.Select(m => m[1, 0])],
            Target(spec));

    /// <summary>"Chebyshev bandpass, order 3, 2.2 to 2.6 GHz, 0.1 dB ripple, Z0 50 Ω".</summary>
    public static string Describe(FilterSpec s)
        => $"{s.Response} {s.Band.ToString().ToLowerInvariant()}, order {s.Order}, "
         + (s.Band is FilterBand.Bandpass or FilterBand.Bandstop ? $"{Ghz(s.F1Hz)} to {Ghz(s.F2Hz)} GHz" : $"{Ghz(s.F1Hz)} GHz")
         + (s.Response == FilterResponse.Chebyshev ? $", {F(s.RippleDb)} dB ripple" : "") + $", Z0 {F(s.Z0)} Ohm";

    private static string Analysis(FilterSpec spec, int points)
    {
        var f = Frequencies(spec, points);
        return $"analysis SP1 type=sparam start={F(f[0])} stop={F(f[^1])} npts={points} Unit=Hz\n";
    }

    private static string Ghz(double hz) => (hz / 1e9).ToString("0.######", CultureInfo.InvariantCulture);
    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
