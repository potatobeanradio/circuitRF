// HZN-4 R-hzn4-1 — setups, and the design as a JSON tree in the key order a Hertzon project lists.

using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Interchange.Hzn;

internal sealed partial class C3dToHznWriter
{
    // ── setups ────────────────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, double> FrequencyUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hz"] = 1, ["kHz"] = 1e3, ["MHz"] = 1e6, ["GHz"] = 1e9, ["THz"] = 1e12,
    };

    private static readonly string[] FrequencySpellings = ["Hz", "kHz", "MHz", "GHz", "THz"];

    /// <summary>A frequency in Hertzon's spelling and in hertz: a number is written with its unit, any other expression through the
    /// document's own scope.</summary>
    private (string Text, double? Hz)? Frequency(string expr, string unit, string who)
    {
        string spelled = FrequencySpellings.FirstOrDefault(s => string.Equals(s, unit, StringComparison.OrdinalIgnoreCase)) ?? "";
        if (spelled.Length == 0)
        {
            _r.Add(HznRefusalKind.Setting, $"frequency unit '{unit}'", "Hertzon's frequencies are in Hz, kHz, MHz, GHz or THz", who);
            return null;
        }
        if (double.TryParse(expr, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return (HznExpressionWriter.Digits(v) + spelled, v * FrequencyUnits[spelled]);
        if (_x.Write(expr, spelled, who) is not { } t) return null;
        double? hz = null;
        try { hz = _res.Evaluate(expr, spelled).AsReal(); } catch (Exception e) when (e is Core.Expressions.ExpressionException or InvalidCastException) { }
        return (t.Text, hz);
    }

    private List<HznJsonObject> Setups()
    {
        var list = new List<HznJsonObject>();
        foreach (var s in C3dSetups.Read(_doc))
        {
            _user = s.Name;
            if (s.Setup is not { } setup)
            {
                _r.Add(HznRefusalKind.Setting, $"setup '{s.Name}'", s.Refusal ?? "it cannot be read", s.Name);
                continue;
            }
            if (setup.IsThermal) { _r.Add(HznRefusalKind.Setting, "thermal setup", "Hertzon has no thermal solution", s.Name); continue; }
            if (setup.IsStatic3D) { _r.Add(HznRefusalKind.Setting, "electrostatic or magnetostatic setup", "Hertzon solves driven problems", s.Name); continue; }
            if (setup.Problem3D != Em3dProblemType.Driven)
            {
                _r.Add(HznRefusalKind.Setting, "eigenmode setup", "eigenmode setups are not read back by circuitRF yet", s.Name);
                continue;
            }
            if (SetupJson(setup) is { } json) list.Add(json);
        }
        var dup = list.GroupBy(j => j.GetString("name")).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) _r.Add(HznRefusalKind.Setting, $"setup name '{dup.Key}'", "two setups have it", DesignName);
        return list;
    }

    private HznJsonObject? SetupJson(EmSetup setup)
    {
        string who = setup.Name;
        var plan = setup.FrequencyPlan.ToList();
        if (plan.Any(f => f.Kind == SweepKind.Log))
        {
            _r.Add(HznRefusalKind.Setting, "logarithmic frequency sweep", "a linear count, a linear step or single points is written", who);
            return null;
        }
        static bool Single(FrequencySpec f) => f.StartExpr == f.StopExpr || (f.Mode == FreqSpecMode.PointCount && f.NumPoints == 1);

        var setupProps = new List<(string, string)> { ("Enabled", "True") };
        var sweeps = new HznJsonArray();
        string solution;
        if (plan.All(Single))
        {
            var points = new List<(string Text, double? Hz)>();
            foreach (var f in plan)
                if (Frequency(f.StartExpr, f.StartUnit, who) is { } p) points.Add(p);
                else return null;
            solution = points.OrderByDescending(p => p.Hz ?? 0).First().Text;
            if (points.Count > 1)
                sweeps.Add(Sweep("Sweep", "Discrete", "Single Points", points: string.Join(",", points.Select(p => p.Text))));
        }
        else if (plan.Count == 1)
        {
            var f = plan[0];
            if (Frequency(f.StartExpr, f.StartUnit, who) is not { } a || Frequency(f.StopExpr, f.StopUnit, who) is not { } b) return null;
            solution = b.Text;
            string type = setup.Emerge?.AdaptiveSweep ?? true ? "Interpolating" : "Discrete";
            if (f.Mode == FreqSpecMode.PointCount)
                sweeps.Add(Sweep("Sweep", type, "Linear Count", start: a.Text, stop: b.Text, count: f.NumPoints!.Value.ToString(CultureInfo.InvariantCulture)));
            else
            {
                if (Frequency(f.StepExpr, f.StepUnit, who) is not { } st) return null;
                sweeps.Add(Sweep("Sweep", type, "Linear Step", start: a.Text, stop: b.Text, step: st.Text));
            }
        }
        else
        {
            _r.Add(HznRefusalKind.Setting, "a frequency band beside further frequencies", "a Hertzon sweep is one band, or a list of single points", who);
            return null;
        }
        setupProps.Add(("SolutionFrequencyMode", "Single"));
        setupProps.Add(("Frequency", solution));
        if (setup.Emerge?.AdaptiveMesh is { } passes) setupProps.Add(("MaximumPasses", passes.ToString(CultureInfo.InvariantCulture)));
        setupProps.Add(("MaximumDeltaS", "0.02"));
        switch (setup.Emerge?.ElementOrder)
        {
            case 1: setupProps.Add(("BasisOrder", "First Order")); break;
            case 2: setupProps.Add(("BasisOrder", "Second Order")); break;
            case { } other: _r.Add(HznRefusalKind.Setting, $"element order {other}", "Hertzon's basis order is first or second", who); return null;
        }
        var o = new HznJsonObject();
        o.SetString("name", setup.Name);
        o.Set("props", Strings(setupProps));
        o.Set("sweeps", sweeps);
        o.SetBool("eigen", false);
        return o;
    }

    private static HznJsonObject Sweep(string name, string type, string distribution, string? start = null, string? stop = null,
                                       string? step = null, string? count = null, string? points = null)
    {
        var props = new List<(string, string)>
        {
            ("Enabled", "True"), ("SweepType", type), ("Distribution", distribution),
            ("Start", start ?? "1GHz"), ("Stop", stop ?? "2GHz"), ("Step", step ?? "0.1GHz"), ("Count", count ?? "101"),
            ("SamplesPerDecade", "20"), ("Points", points ?? "1GHz,2GHz"), ("SaveFields", "False"),
            ("ErrorTolerance", "0.5"), ("MaxSolutions", "250"), ("Parallel", "False"),
        };
        var o = new HznJsonObject();
        o.SetString("name", name);
        o.Set("props", Strings(props));
        return o;
    }

    // ── the tree ──────────────────────────────────────────────────────────────────────────────────

    private static HznJsonObject Strings(IEnumerable<(string Key, string Value)> pairs)
    {
        var o = new HznJsonObject();
        foreach (var (k, v) in pairs) o.SetString(k, v);
        return o;
    }

    private static HznJsonArray Array(IEnumerable<HznJsonValue> items)
    {
        var a = new HznJsonArray();
        foreach (var i in items) a.Add(i);
        return a;
    }

    private static string UnitName(LayoutUnit u) => u switch
    {
        LayoutUnit.Nm => "nm", LayoutUnit.Um => "um", LayoutUnit.Mm => "mm", LayoutUnit.Mil => "mil", _ => "in",
    };

    private HznJsonObject ObjectJson(HObj h, string material, string color, double transparency)
    {
        var o = new HznJsonObject();
        o.SetString("name", h.Name);
        o.Set("history", Array(h.History.Select(s =>
        {
            var step = new HznJsonObject();
            step.SetString("kind", s.Kind);
            step.Set("props", Strings(s.Props));
            step.SetBool("suppressed", s.Suppressed);
            return (HznJsonValue)step;
        })));
        o.SetString("material", material);
        o.SetBool("solve_inside", h.SolveInside);
        o.SetBool("model", h.Model);
        o.SetString("color", color);
        o.Set("transparency", HznJsonScalar.Number(transparency));
        o.SetBool("wireframe", false);
        o.SetBool("visible", h.Visible);
        o.SetString("consumed_by", h.ConsumedBy);
        o.SetString("group", h.Group ?? "");
        o.SetString("orientation", "Global");
        return o;
    }

    private static HznJsonObject AssignJson(HAssign a)
    {
        var o = new HznJsonObject();
        o.SetString("name", a.Name);
        o.SetString("kind", a.Kind);
        o.Set("props", Strings(a.Props));
        o.Set("faces", Array(a.Faces.Select(f =>
        {
            var face = new HznJsonObject();
            face.SetString("obj", f.Obj);
            face.Set("face", HznJsonScalar.Number((long)f.Face));
            face.Set("sig", Array(f.Sig.Select(d => (HznJsonValue)HznJsonScalar.Number(d))));
            return (HznJsonValue)face;
        })));
        o.Set("objects", Array(a.Objects.Select(n => (HznJsonValue)HznJsonScalar.String(n))));
        o.Set("edges", new HznJsonArray());
        o.SetBool("enabled", a.Enabled);
        return o;
    }

    private HznJsonObject AssembleDesign(List<(string Name, string Text)> variables, List<HznJsonObject> setups)
    {
        RegionBoundaries();
        var objects = new List<HznJsonValue>();
        foreach (var h in _objects) objects.Add(ObjectJson(h, _materialOf.GetValueOrDefault(h, SheetMaterial), "#808080", 0.0));
        if (_region is not null)
            objects.Add(ObjectJson(_region, _regionMaterialName ?? FreeSpace, "#e6f2ff", 0.85));

        var d = new HznJsonObject();
        d.SetString("name", DesignName);
        d.SetString("units", UnitName(_unit));
        d.Set("settings", Strings([("SolutionType", "Modal"), ("AutoOpen", "Off")]));
        d.Set("variables", Strings(variables.Select(v => (v.Name, v.Text))));
        d.Set("objects", Array(objects));
        d.Set("boundaries", Array(_boundaries.Select(b => (HznJsonValue)AssignJson(b))));
        d.Set("excitations", Array(_excitations.Select(x => (HznJsonValue)AssignJson(x))));
        d.Set("mesh_ops", new HznJsonArray());
        d.Set("setups", Array(setups));
        d.Set("spheres", new HznJsonArray());
        d.Set("reports", new HznJsonArray());
        d.Set("field_plots", new HznJsonArray());
        d.Set("coord_systems", new HznJsonArray());
        d.Set("optimetrics", new HznJsonArray());
        d.Set("parametrics", new HznJsonArray());
        d.SetString("working_cs", "Global");
        d.Set("notes", new HznJsonArray());
        return d;
    }
}
