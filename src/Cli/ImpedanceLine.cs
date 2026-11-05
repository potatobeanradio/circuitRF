using System.Globalization;
using System.Text;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf impedance --tech &lt;t&gt; --layer &lt;name&gt; (--width &lt;w&gt; | --z0 &lt;ohms&gt;)</c> — the line
/// calculator (AA-3): a line's width, Z0, ε_eff, loss and guided wavelength on a technology layer with
/// nothing drawn, the circuit model's answer beside the cross-section's.
///
/// <para><b>It owns no analysis</b>, on <c>src/Cli/Authoring.cs</c>' terms: every number comes out of
/// <see cref="LineCalculator.Calculate"/> (<c>src/Design/Layout/Em</c>), whose model column IS an
/// elaborated MLIN, CPWG or SLIN and whose cross-section column IS <c>TraceImpedanceAnalysis.Analyze</c>. This file is
/// the technology lookup, refusals and reporting.</para>
///
/// <para><b>A mode of <c>impedance</c>, not a verb</b> (owner, AA-3): that verb already owns the
/// cross-section, and the question is the same one asked before the line is drawn. <c>--tech</c> selects
/// it; a layout path beside it, or a flag that reviews a drawn layout, is refused rather than ignored.</para>
///
/// <para><b>Exit codes.</b> 0 when every row has an answer in every column it can have one in (a coplanar
/// line between two planes has no circuit model, which is said, not failed); 1 when the calculator is refused or a row has no
/// answer at all. A row that one column could not answer is <c>impedance.line.unanswered</c>, a warning.</para>
/// </summary>
internal static partial class Impedance
{
    private static int RunLine(Options o)
    {
        if (o.Path is { } path) return JsonRun.Fail(CliDiagnostics.ImpedanceLinePathNotUsed(path));
        if (o.ReviewOptions.FirstOrDefault() is { } review) return JsonRun.Fail(CliDiagnostics.ImpedanceLineOptionNotUsed(review));

        if (ResolveTechnology(o.Tech!, out string label) is not { } tech)
            return 1;   // the refusal is reported

        // The layer: --layer, or the layer a "<layer>=<w>" --width names.
        var layerNames = o.Layers.Concat(o.Widths.Select(w => w.Layer))
                                 .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (layerNames.Count == 0)
            return JsonRun.Fail(CliDiagnostics.ImpedanceLineLayerRequired(string.Join(", ", LineCalculator.CopperLayerNames(tech))));
        if (layerNames.Count > 1) return JsonRun.Fail(CliDiagnostics.ImpedanceLineOneLayer());
        if (LineCalculator.ResolveLayer(tech, layerNames[0]) is null)
            return JsonRun.Fail(CliDiagnostics.ImpedanceUnknownLayer(layerNames[0], string.Join(", ", LineCalculator.CopperLayerNames(tech))));

        var widths = o.PlainWidthsMeters.Concat(o.Widths.SelectMany(w => w.Microns.Select(um => um * 1e-6))).ToList();
        if (widths.Count == 0 && o.Z0s.Count == 0) return JsonRun.Fail(CliDiagnostics.ImpedanceLineNothingAsked());

        var control = new RunControl { Token = RunHost.Cancellation };
        LineCalcResult result;
        try
        {
            result = LineCalculator.Calculate(tech, new LineCalcRequest(layerNames[0], widths, o.Z0s, o.GapMeters, o.FreqHz), control);
        }
        catch (OperationCanceledException)
        {
            JsonRun.Report(CliDiagnostics.ImpedanceCancelled());
            return 130;
        }
        if (result.Refusal is { } why) return JsonRun.Fail(CliDiagnostics.ImpedanceLineRefused(why));

        foreach (string w in result.Warnings) Console.Error.WriteLine($"[circuitRF] {w}");
        var unit = tech.DefaultDisplayUnit;
        Console.Write(LineText(result, label, unit));
        JsonRun.ImpedanceLine = LineJson(result, label);

        // A column that should have answered and did not is a warning; a row with no answer is a failure.
        bool modelExpected = result.ModelComponent is not null && (result.Substrate is not null || result.Stripline is not null);
        bool failed = false;
        foreach (var row in result.Rows)
        {
            string name = RowName(row, unit);
            bool model = row.Model is not null, section = row.CrossSection?.Z0 is not null;
            if (modelExpected && !model)
                JsonRun.Report(CliDiagnostics.ImpedanceLineUnanswered(name, "circuit model", row.ModelRefusal ?? "no reason given"));
            if (!section)
                JsonRun.Report(CliDiagnostics.ImpedanceLineUnanswered(name, "cross-section", row.CrossSection?.Refusal ?? "no reason given"));
            if (!model && !section) failed = true;
        }
        return failed ? 1 : 0;
    }

    /// <summary>
    /// The technology <c>--tech</c> names: a <c>.ctech</c>; a <c>.clay</c> by the layout's own walk; any
    /// other file or folder by the walk a schematic in it uses (the nearest <c>.cws</c> and its default
    /// technology), because that is the substrate an MLIN there elaborates on; else a shipped id.
    /// Null, with the refusal reported, when none resolves.
    /// </summary>
    private static Technology? ResolveTechnology(string text, out string label)
    {
        label = text;
        if (File.Exists(text) || Directory.Exists(text))
        {
            string full = Path.GetFullPath(text);
            Technology? tech;
            if (File.Exists(full) && full.EndsWith(".ctech", StringComparison.OrdinalIgnoreCase))
            {
                tech = TechnologyResolver.LoadForPath(full);
                if (tech is null) { JsonRun.Report(CliDiagnostics.ImpedanceTechUnreadable(full)); return null; }
                label = tech.Name.Length > 0 ? tech.Name : Path.GetFileNameWithoutExtension(full);
                return tech;
            }
            if (DocumentKinds.Classify(full) == DocumentKind.Layout)
            {
                string? techRef = null;
                try { techRef = LayoutPersistence.LoadFromFile(full).TechRef; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) { }
                tech = TechnologyResolver.ResolveForDocument(techRef, full, null, new TechnologyCache()).Resolution.Tech;
            }
            else
            {
                tech = MicrostripSubstrateInjection.ResolveWorkspaceTechnology(Directory.Exists(full) ? full : Path.GetDirectoryName(full));
            }
            if (tech is null) { JsonRun.Report(CliDiagnostics.ImpedanceTechNone(full)); return null; }
            label = tech.Name.Length > 0 ? tech.Name : Path.GetFileName(full);
            return tech;
        }

        if (ShippedTechnologies.All.FirstOrDefault(e => string.Equals(e.Id, text, StringComparison.OrdinalIgnoreCase)) is { } shipped)
        {
            var tech = ShippedTechnologies.Load(shipped);
            label = shipped.Id;
            return tech;
        }
        JsonRun.Report(CliDiagnostics.ImpedanceTechNotFound(text, string.Join(", ", ShippedTechnologies.All.Select(e => e.Id))));
        return null;
    }

    // ── the text report (stdout) ─────────────────────────────────────────────────────────────

    private const double DbPerNp = 8.685889638065035;   // 20 / ln 10

    /// <summary>SI-SIG — the substrate's own conductivity, printed only where one is stated.</summary>
    private static string DielectricSigma(double sigmaSm) => sigmaSm > 0
        ? string.Create(CultureInfo.InvariantCulture, $", substrate σ {sigmaSm:G3} S/m") : "";

    private static string RowName(LineCalcRow row, LayoutUnit unit) => row.TargetZ0 is { } z
        ? $"Z0 {z.ToString("0.###", CultureInfo.InvariantCulture)} Ω"
        : $"W {Len(row.CrossSection?.WidthM ?? row.Model?.WidthM ?? 0, unit)}";

    private static string LineText(LineCalcResult r, string label, LayoutUnit unit)
    {
        var sb = new StringBuilder();
        string f = r.FreqHz is { } hz ? TraceImpedanceReport.Hz(hz) : "";
        sb.Append($"Line on {r.LayerName} ({label})");
        if (r.GapM is { } gap) sb.Append($" — coplanar, gap {Len(gap, unit)} to ground on {r.LayerName} either side");
        sb.AppendLine();
        if (r.Stripline is { } st)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"Model substrate: {st.SignalConductorName} between {st.PlaneAboveName} and {st.PlaneBelowName}, " +
                $"H1 {Len(st.H1Meters, unit)}, H2 {Len(st.H2Meters, unit)}, T {Len(st.ThicknessMeters, unit)}, " +
                $"εr {st.RelativePermittivity:0.###}, tanδ {st.LossTangent:0.#####}, σ {st.ConductivitySPerM:0.###e0} S/m{DielectricSigma(st.DielectricSigmaSPerM)}"));
        else if (r.Substrate is { } s)
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"Model substrate: {s.SignalConductorName} over {s.GroundConductorName}, H {Len(s.HeightMeters, unit)}, " +
                $"T {Len(s.ThicknessMeters, unit)}, εr {s.RelativePermittivity:0.###}, tanδ {s.LossTangent:0.#####}, σ {s.ConductivitySPerM:0.###e0} S/m{DielectricSigma(s.DielectricSigmaSPerM)}"));
        else if (r.SubstrateRefusal is { } why)
            sb.AppendLine($"Model substrate: none — {why}");
        if (r.FreqHz is null) sb.AppendLine("No --freq: static values only; loss and λg need a frequency.");

        var said = new HashSet<string>(StringComparer.Ordinal);   // a note is said once, under the first row it applies to
        foreach (var row in r.Rows)
        {
            sb.AppendLine();
            var m = row.Model;
            var x = row.CrossSection;
            sb.AppendLine($"{RowName(row, unit),-24}  {$"circuit model ({r.ModelComponent ?? "none"})",-24}  {"cross-section (impedance)",-28}  difference");
            if (row.TargetZ0 is { } target)
            {
                sb.AppendLine(Row($"W for {target.ToString("0.###", CultureInfo.InvariantCulture)} Ω",
                    m is null ? Missing(row.ModelRefusal) : Len(m.WidthM, unit),
                    x?.Z0 is null ? Missing(x?.Refusal) : Len(x.WidthM, unit),
                    m is not null && x?.Z0 is not null ? Pct(x.WidthM, m.WidthM) : ""));
                if (m is not null && row.CrossSectionAtModelWidth is { } at)
                    sb.AppendLine(Row($"Z0 at the model's W", Ohm(m.Line.Z0Static),
                        at.Z0 is { } za ? Ohm(za) : Missing(at.Refusal),
                        at.Z0 is { } zb ? Pct(zb, m.Line.Z0Static) : ""));
                sb.AppendLine("  at each column's own width:");
            }
            else
            {
                sb.AppendLine(Row("Z0 (static)", m is null ? Missing(row.ModelRefusal) : Ohm(m.Line.Z0Static),
                    x?.Z0 is { } z ? Ohm(z) : Missing(x?.Refusal),
                    m is not null && x?.Z0 is { } z2 ? Pct(z2, m.Line.Z0Static) : ""));
            }
            if (r.FreqHz is not null && m is not null)
                sb.AppendLine(Row($"Z0 at {f}", Ohm(m.Line.Z0), "— (quasi-static)", ""));
            sb.AppendLine(Row("εeff (static)", m is null ? "—" : Num(m.Line.EeffStatic),
                x?.Eeff is { } e ? Num(e) : "—",
                m is not null && x?.Eeff is { } e2 ? Pct(e2, m.Line.EeffStatic) : ""));
            if (r.FreqHz is not null)
            {
                if (m is not null) sb.AppendLine(Row($"εeff at {f}", Num(m.Line.Eeff), "— (quasi-static)", ""));
                sb.AppendLine(Row($"loss at {f}",
                    m is null ? "—" : string.Create(CultureInfo.InvariantCulture,
                        $"{m.Line.AlphaNpPerM * DbPerNp * 1e-3:0.####} dB/mm"),
                    "— (lossless solve)", ""));
                sb.AppendLine(Row($"λg at {f}", m?.LambdaGM is { } lm ? Len(lm, unit) : "—",
                    x?.LambdaGM is { } lx ? Len(lx, unit) + " (static εeff)" : "—", ""));
            }
            sb.AppendLine(Row("line type", m is null ? "—" : LineTypeOf(m.Component), x?.Configuration is { Length: > 0 } c ? c : "—", ""));
            foreach (string w in m?.Warnings ?? []) sb.AppendLine($"  ? model: {w}");
            foreach (string n in x?.Notes ?? [])
                if (said.Add(n)) sb.AppendLine($"  · cross-section: {n}");
            if (m is null && row.ModelRefusal is { } mr) sb.AppendLine($"  · model: {mr}");
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"  ({row.CrossSectionSolves} cross-section solve(s))"));
        }
        return sb.ToString();

        // The trace review's own names, so the two columns' line types compare word for word.
        static string LineTypeOf(string component) => component switch
        {
            "CPWG" => "grounded coplanar waveguide",
            "SLIN" => "stripline",
            _      => "microstrip",
        };
        static string Row(string what, string model, string section, string diff) =>
            $"  {what,-22}  {model,-24}  {section,-28}  {diff}".TrimEnd();
        static string Missing(string? why) => why is null ? "—" : "— (see below)";
        static string Ohm(double z) => z.ToString("0.00", CultureInfo.InvariantCulture) + " Ω";
        static string Num(double v) => v.ToString("0.0000", CultureInfo.InvariantCulture);
        static string Pct(double value, double reference) =>
            ((value - reference) / reference * 100).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " %";
    }

    /// <summary>A length in metres, in the technology's display unit, to four places.</summary>
    private static string Len(double metres, LayoutUnit unit)
    {
        double nmPerUnit = (double)LayoutUnits.ToDbu(1m, unit, 1000);
        double v = metres * 1e9 / nmPerUnit;
        // Four places for a width, fewer as the number grows: a guided wavelength is not read to 0.1 nm.
        string format = Math.Abs(v) >= 1000 ? "0.#" : Math.Abs(v) >= 100 ? "0.##" : "0.####";
        return v.ToString(format, CultureInfo.InvariantCulture) + " " + LayoutUnits.Suffix(unit);
    }

    // ── --json ───────────────────────────────────────────────────────────────────────────────

    private static ImpedanceLineJson LineJson(LineCalcResult r, string label)
    {
        static double Um(double m) => m * 1e6;
        static ImpedanceLineSectionJson? Section(LineCalcCrossSection? x) => x is null ? null
            : new ImpedanceLineSectionJson(Um(x.WidthM), x.Z0, x.Eeff, x.LambdaGM is { } l ? Um(l) : null,
                                           x.Configuration, x.Refusal, x.Notes);
        bool f = r.FreqHz is not null;
        var s = r.Substrate;
        var st = r.Stripline;
        return new ImpedanceLineJson(
            label, r.LayerName, r.ConductorName,
            st is not null ? $"{st.PlaneAboveName}/{st.PlaneBelowName}" : s?.GroundConductorName,
            st is not null ? Um(st.H1Meters) : s is null ? null : Um(s.HeightMeters),
            st is not null ? Um(st.ThicknessMeters) : s is null ? null : Um(s.ThicknessMeters),
            st?.RelativePermittivity ?? s?.RelativePermittivity, st?.LossTangent ?? s?.LossTangent,
            st?.ConductivitySPerM ?? s?.ConductivitySPerM, r.SubstrateRefusal,
            r.GapM is { } g ? Um(g) : null, r.FreqHz,
            [.. r.Rows.Select(row =>
            {
                var m = row.Model;
                var same = row.TargetZ0 is null ? row.CrossSection : row.CrossSectionAtModelWidth;
                return new ImpedanceLineRowJson(
                    row.TargetZ0,
                    m is null ? null : new ImpedanceLineModelJson(
                        Um(m.WidthM), m.Line.Z0Static, m.Line.EeffStatic,
                        f ? m.Line.Z0 : null, f ? m.Line.Eeff : null,
                        f ? m.Line.AlphaNpPerM * DbPerNp * 1e-3 : null,
                        f ? m.Line.ConductorLossNpPerM * DbPerNp * 1e-3 : null,
                        f ? m.Line.DielectricLossNpPerM * DbPerNp * 1e-3 : null,
                        m.LambdaGM is { } l ? Um(l) : null, m.Warnings),
                    row.ModelRefusal,
                    Section(row.CrossSection), Section(row.CrossSectionAtModelWidth),
                    m is not null && same?.Z0 is { } z ? (z - m.Line.Z0Static) / m.Line.Z0Static * 100 : null,
                    row.TargetZ0 is not null && m is not null && row.CrossSection?.Z0 is not null
                        ? (row.CrossSection.WidthM - m.WidthM) / m.WidthM * 100 : null,
                    row.CrossSectionSolves);
            })],
            r.Warnings,
            r.ModelComponent,
            st is null ? null : Um(st.H2Meters));
    }
}
