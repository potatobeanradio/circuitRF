// HZN-2 R-hzn2-7 — setups: a Hertzon setup and its sweeps become `.c3d` setups for circuitRF's EMerge run (brief 7).
//
// Settings with no equivalent are not refused: they do not change the geometry. They stay in the `.hzn`, and the
// provenance lists each with its value so brief 7 can report it as "not used by circuitRF's run".

using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Design.Interchange.Hzn;

internal sealed partial class HznTranslator
{
    /// <summary>The setup keys the translation reads; every other key is listed as unused.</summary>
    private static readonly HashSet<string> UsedSetupKeys = new(StringComparer.Ordinal)
    {
        "Enabled", "SolutionFrequencyMode", "Frequency", "MaximumPasses", "BasisOrder",
    };

    /// <summary>HZN-7 — the sweep keys the translation reads (the frequency plan and the sweep's kind); every other sweep key is
    /// listed as unused beside the setup's.</summary>
    private static readonly HashSet<string> UsedSweepKeys = new(StringComparer.Ordinal)
    {
        "Enabled", "SweepType", "Distribution", "Start", "Stop", "Step", "Count", "Points",
    };

    private void Setups()
    {
        foreach (var s in _design.Setups)
        {
            string name = s.Name ?? "";
            if (s.Json.GetBool("eigen") == true)
            {
                _r.Add(HznRefusalKind.Setting, $"eigenmode setup '{name}'", "eigenmode setups are not translated yet", name);
                continue;
            }
            if (s.Props["Enabled"] is { } en && !IsTrue(en))
            {
                _notes.Add($"Setup '{name}' is switched off and was not translated.");
                continue;
            }
            int order = s.Props["BasisOrder"] switch { "First Order" => 1, "Second Order" => 2, null => 0, _ => -1 };
            if (order < 0)
            {
                _r.Add(HznRefusalKind.Setting, $"basis order '{s.Props["BasisOrder"]}'", "circuitRF's element order is 1 or more", name);
                continue;
            }
            int? passes = s.Props["MaximumPasses"] is { } mp ? (Plain(mp, name) is { } pv ? (int)pv : null) : null;

            var sweeps = s.Json.GetArray("sweeps")?.Items.OfType<HznJsonObject>().Select(o => new HznSweep(o)).Where(w => w.Enabled).ToList() ?? [];
            if (sweeps.Count == 0)
            {
                if (SolutionHz(s, name) is not { } f) continue;
                Emit(name, name, s, Single(f), order, passes, adaptiveSweep: false);
                continue;
            }
            foreach (var w in sweeps)
            {
                string c3d = sweeps.Count == 1 ? name : $"{name} - {w.Name}";
                var plan = Plan(w, name, c3d);
                if (plan is null) continue;
                Emit(c3d, $"{name}/{w.Name}", s, plan.Value, order, passes, adaptiveSweep: w.Type is "Interpolating" or "Fast", sweep: w);
            }
        }
    }

    private sealed class HznSweep(HznJsonObject json)
    {
        public string Name => json.GetString("name") ?? "";
        private HznStringMap Props => new(json, "props");
        public bool Enabled => Props["Enabled"] is not { } e || string.Equals(e, "True", StringComparison.OrdinalIgnoreCase);
        public string? Type => Props["SweepType"];
        public string? Get(string key) => Props[key];
        public IEnumerable<string> Keys => Props.Names;
    }

    private double? SolutionHz(HznSetup s, string user)
    {
        string mode = s.Props["SolutionFrequencyMode"] ?? "Single";
        string? text = mode switch { "Single" => s.Props["Frequency"], "Broadband" => s.Props["HighFrequency"], _ => null };
        if (text is null)
        {
            _r.Add(HznRefusalKind.Setting, $"solution frequency mode '{mode}'", "a single frequency or a broadband high frequency is translated", user);
            return null;
        }
        return Hz(text, user);
    }

    private double? Hz(string text, string user)
    {
        var e = _expr.Translate(text, user);
        if (e is null) return null;
        UseProjectRefs(e);
        try { return _res!.Evaluate(e.Text, null).AsReal(); }
        catch (Exception ex) when (ex is ExpressionException or InvalidCastException)
        {
            _r.Add(HznRefusalKind.Expression, "expression", $"'{text}' does not evaluate: {ex.Message}", user);
            return null;
        }
    }

    private static JsonObject Freq(double startHz, double stopHz, int? points, double? stepHz)
    {
        string Ghz(double hz) => Num(hz / 1e9);
        var o = new JsonObject
        {
            ["StartExpr"] = Ghz(startHz), ["StopExpr"] = Ghz(stopHz),
            ["StepExpr"] = stepHz is { } st ? Ghz(st) : "",
            ["Mode"] = stepHz is null ? "PointCount" : "StepSize", ["Kind"] = "Linear",
            ["StartUnit"] = "GHz", ["StopUnit"] = "GHz", ["StepUnit"] = "GHz",
        };
        if (stepHz is null) o["NumPoints"] = points ?? 1;
        return o;
    }

    private static (JsonObject First, List<JsonObject> More) Single(double hz) => (Freq(hz, hz, 1, null), []);

    private (JsonObject First, List<JsonObject> More)? Plan(HznSweep w, string setup, string user)
    {
        string dist = w.Get("Distribution") ?? "";
        double? start = w.Get("Start") is { } a ? Hz(a, user) : null;
        double? stop = w.Get("Stop") is { } b ? Hz(b, user) : null;
        switch (dist)
        {
            case "Linear Count":
                if (start is null || stop is null || Plain(w.Get("Count"), user) is not { } n) return null;
                return (Freq(start.Value, stop.Value, (int)n, null), []);
            case "Linear Step":
                if (start is null || stop is null || w.Get("Step") is not { } st || Hz(st, user) is not { } step) return null;
                return (Freq(start.Value, stop.Value, null, step), []);
            case "Single Points":
            {
                var pts = HznExpressionTranslator.SplitTopLevel(w.Get("Points") ?? "", ',').Where(x => x.Length > 0).Select(x => Hz(x, user)).ToList();
                if (pts.Count == 0 || pts.Any(x => x is null)) { if (pts.Count == 0) _r.Add(HznRefusalKind.Setting, "sweep without points", "it lists none", user); return null; }
                return (Freq(pts[0]!.Value, pts[0]!.Value, 1, null), [.. pts.Skip(1).Select(x => Freq(x!.Value, x.Value, 1, null))]);
            }
            default:
                _r.Add(HznRefusalKind.Setting, $"sweep distribution '{dist}'", "a linear count, a linear step or single points is translated", user);
                return null;
        }
    }

    private void Emit(string c3dName, string origin, HznSetup s, (JsonObject First, List<JsonObject> More) plan, int order, int? passes, bool adaptiveSweep, HznSweep? sweep = null)
    {
        var emerge = new JsonObject { ["AdaptiveSweep"] = adaptiveSweep };
        if (order > 0) emerge["ElementOrder"] = order;
        if (passes is { } mp) emerge["AdaptiveMesh"] = mp;
        var o = new JsonObject
        {
            ["FormatVersion"] = 1, ["Name"] = c3dName, ["Frequency"] = plan.First,
            ["Solver3D"] = "Emerge", ["Problem3D"] = "Driven", ["Emerge"] = emerge,
        };
        if (plan.More.Count > 0) o["AdditionalFrequencies"] = new JsonArray([.. plan.More]);
        if (_airBox is { } air)
        {
            JsonObject F(Layout.Em.CemAirBoxFace f) => new() { ["PaddingUm"] = f.PaddingUm, ["Boundary"] = f.Boundary?.ToString() };
            o["AirBox"] = new JsonObject
            {
                ["XMin"] = F(air.XMin!), ["XMax"] = F(air.XMax!), ["YMin"] = F(air.YMin!), ["YMax"] = F(air.YMax!),
                ["ZMin"] = F(air.ZMin!), ["ZMax"] = F(air.ZMax!),
            };
        }
        _doc.Setups.Add(JsonSerializer.SerializeToElement(o));
        _prov.Setups[c3dName] = origin;
        foreach (string k in s.Props.Names)
            if (!UsedSetupKeys.Contains(k)) _prov.Unused.Add(new HznUnusedSetting($"setup {s.Name}", k, s.Props[k] ?? ""));
        if (sweep is not null)
            foreach (string k in sweep.Keys)
                if (!UsedSweepKeys.Contains(k)) _prov.Unused.Add(new HznUnusedSetting($"sweep {s.Name}/{sweep.Name}", k, sweep.Get(k) ?? ""));
        if (s.Props["SolutionFrequencyMode"] is "Multi-Frequencies" or "Broadband")
            _prov.Unused.Add(new HznUnusedSetting($"setup {s.Name}", "adaptive frequency", s.Props["SolutionFrequencyMode"] ?? ""));
        else if (plan.First["StartExpr"]?.ToString() != plan.First["StopExpr"]?.ToString() || plan.More.Count > 0)
            _prov.Unused.Add(new HznUnusedSetting($"setup {s.Name}", "adaptive frequency", s.Props["Frequency"] ?? ""));
        if (_doc.ActiveSetup is null) _doc.ActiveSetup = c3dName;
    }
}
