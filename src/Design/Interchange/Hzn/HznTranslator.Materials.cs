// HZN-2 R-hzn2-5 — materials: the project's become the document's own in-memory technology.

using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Interchange.Hzn;

internal sealed partial class HznTranslator
{
    private Technology? _technology;

    /// <summary>A conductivity at or above this (S/m) makes a Hertzon material with conductivity a conductor; below it,
    /// a lossy dielectric. circuitRF needs a role for a material that states both, and Hertzon states none.</summary>
    internal const double ConductorSigmaSm = 1e6;

    private sealed record Mat(TechMaterial Material, Em3dRole? Role, bool Conductor);

    private readonly Dictionary<string, Mat?> _mats = new(StringComparer.Ordinal);

    /// <summary>The materials Hertzon has built in that circuitRF can state exactly (brief-hzn-0 Q3). Conductivity of
    /// copper: 5.8e7 S/m, the value a Finite Conductivity boundary in Hertzon's own examples states for it.</summary>
    private static Mat? BuiltIn(string name) => name switch
    {
        "pec" => new Mat(new TechMaterial { Name = "pec", Epsr = 1, TanD = 0, Mur = 1 }, Em3dRole.Conductor, true),
        "vacuum" => new Mat(new TechMaterial { Name = "vacuum", Epsr = 1, TanD = 0, Mur = 1 }, null, false),
        "air" => new Mat(new TechMaterial { Name = "air", Epsr = 1, TanD = 0, Mur = 1 }, null, false),
        "copper" => new Mat(new TechMaterial { Name = "copper", Sigma20 = 5.8e7 }, null, true),
        _ => null,
    };

    private Mat? Material(string name, string user)
    {
        if (_mats.TryGetValue(name, out var known)) return known;
        Mat? made = null;
        var hz = _project.Materials.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));
        if (hz is not null) made = FromProject(hz, user);
        else if (BuiltIn(name) is { } b) made = b;
        else
            _r.Add(HznRefusalKind.Material, $"material '{name}'",
                   "it is not defined in the project and is not one circuitRF can state from Hertzon's built-in library", user);
        return _mats[name] = made;
    }

    private (string Name, Em3dRole? Role) UseMaterial(string name, string user, bool sheet)
    {
        var m = Material(name, user);
        return m is null ? (name, null) : (m.Material.Name, m.Role);
    }

    private bool IsConductorMaterial(string? name) => name is not null && _mats.TryGetValue(name, out var m) && m is { Conductor: true };

    /// <summary>A property of a project material, evaluated in the project's scope to a plain number.</summary>
    private double? MaterialNumber(HznMaterial hz, string key, string user, double? fallback)
    {
        string? text = hz.Json.GetString(key);
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var e = _projectExpr.Translate(text, user);
        if (e is null) return null;
        foreach (string r in e.Refs) _projectUsed.Add(r);
        try
        {
            double v = _res!.Evaluate(e.Text, null).AsReal();
            if (e.Refs.Count > 0)
                _notes.Add($"{user}: '{text}' was evaluated to {v.ToString("R", CultureInfo.InvariantCulture)}; a technology material holds a number, so it does not follow the variable.");
            return v;
        }
        catch (Exception ex) when (ex is Core.Expressions.ExpressionException or InvalidCastException)
        {
            _r.Add(HznRefusalKind.Expression, "expression", $"'{text}' does not evaluate to a number: {ex.Message}", user);
            return null;
        }
    }

    private Mat? FromProject(HznMaterial hz, string user)
    {
        string name = hz.Name!;
        string who = $"material {name}";
        bool ok = true;
        string Str(string key) => hz.Json.GetString(key) ?? "";
        bool IsOn(string key) => hz.Json.GetBool(key) == true;

        string dispersion = Str("dispersion");
        if (dispersion.Length > 0 && dispersion != "None")
        {
            _r.Add(HznRefusalKind.Material, $"{dispersion} dispersion", "the technology's material model has no such dispersion", name);
            ok = false;
        }
        if (IsOn("anisotropic"))
        {
            _r.Add(HznRefusalKind.Material, "anisotropic material", "tensors are not translated yet", name);
            ok = false;
        }
        double? magLoss = MaterialNumber(hz, "magnetic_loss_tangent", who, 0);
        if (magLoss is not 0)
        {
            _r.Add(HznRefusalKind.Material, "magnetic loss tangent", "a technology material has no magnetic loss", name);
            ok = false;
        }
        double? eps = MaterialNumber(hz, "permittivity", who, 1);
        double? mur = MaterialNumber(hz, "permeability", who, 1);
        double? sigma = MaterialNumber(hz, "conductivity", who, 0);
        double? tan = MaterialNumber(hz, "loss_tangent", who, 0);
        if (!ok || eps is null || mur is null || sigma is null || tan is null) return null;

        if (IsOn("pec"))
            return new Mat(new TechMaterial { Name = name, Epsr = 1, TanD = 0, Mur = mur }, Em3dRole.Conductor, true);
        if (sigma >= ConductorSigmaSm)
            return new Mat(new TechMaterial { Name = name, Sigma20 = sigma, Mur = mur }, null, true);
        var tm = new TechMaterial { Name = name, Epsr = eps, TanD = tan, Mur = mur };
        if (sigma > 0)
        {
            tm.Sigma20 = sigma;
            return new Mat(tm, Em3dRole.Dielectric, false);
        }
        return new Mat(tm, null, false);
    }

    /// <summary>Gives a material a conductor's role for a sheet or a boundary: the material, and whether it is registered.</summary>
    private string ConductiveMaterial(double sigma)
    {
        string name = "hzn_fc_" + sigma.ToString("R", CultureInfo.InvariantCulture).Replace('+', '_').Replace('-', '_');
        if (!_mats.ContainsKey(name))
            _mats[name] = new Mat(new TechMaterial { Name = name, Sigma20 = sigma }, null, true);
        return name;
    }

    private void MaterialsToTechnology()
    {
        // Only what the finished document uses: a port sheet's vacuum, and a material nothing reaches, stay out.
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in _doc.Objects)
            foreach (var d in C3dOperands.SelfAndDescendants(o))
                if (d.Material is { Length: > 0 } m) used.Add(m);
        foreach (var fb in _doc.FaceBoundaries) if (fb.Material is { Length: > 0 } m) used.Add(m);
        if (_doc.AirBoxMaterial is { Length: > 0 } a) used.Add(a);
        var tech = new Technology { Name = _project.Name ?? DesignName };
        foreach (string name in used.Order(StringComparer.Ordinal))
            if (_mats.TryGetValue(name, out var m) && m is not null) tech.Materials.Add(m.Material);
        _technology = tech;
    }
}
