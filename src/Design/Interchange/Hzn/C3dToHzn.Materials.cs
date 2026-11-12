// HZN-4 R-hzn4-1 — materials: the technology's become the project's (brief-hzn-0 D6, D9).
//
// A material is what its OBJECT says it is: the role an object states (or its material implies) decides whether the Hertzon
// material is a perfect conductor, a metal with a conductivity, or a dielectric. The cutoffs are the ones HznToC3d reads
// back with (HznTranslator.ConductorSigmaSm), so a material reads back as the kind it was written as.

using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Interchange.Hzn;

/// <summary>What a Hertzon material is, as numbers: the comparison D9's reuse-or-conflict rule makes.</summary>
public sealed record HznMaterialValues(string Name, double Permittivity, double Permeability, double Conductivity, double LossTangent, bool Pec)
{
    /// <summary>Every property that differs from <paramref name="other"/>, as "permittivity 3.55 here, 4.4 there".</summary>
    public IReadOnlyList<string> DifferencesFrom(HznMaterialValues other, string here, string there)
    {
        var list = new List<string>();
        void Check(string what, double a, double b)
        {
            if (Math.Abs(a - b) > 1e-12 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b))))
                list.Add($"{what} {a.ToString("R", CultureInfo.InvariantCulture)} {here}, {b.ToString("R", CultureInfo.InvariantCulture)} {there}");
        }
        Check("permittivity", Permittivity, other.Permittivity);
        Check("permeability", Permeability, other.Permeability);
        Check("conductivity", Conductivity, other.Conductivity);
        Check("loss tangent", LossTangent, other.LossTangent);
        if (Pec != other.Pec) list.Add($"perfect conductor {Pec} {here}, {other.Pec} {there}");
        return list;
    }

    /// <summary>The project material as a JSON object, in the order a Hertzon project lists a material's keys.</summary>
    public HznJsonObject ToJson()
    {
        static HznJsonScalar N(double v) => HznJsonScalar.String(v.ToString("R", CultureInfo.InvariantCulture));
        var o = new HznJsonObject();
        o.SetString("name", Name);
        o.Set("permittivity", N(Permittivity));
        o.Set("permeability", N(Permeability));
        o.Set("conductivity", N(Conductivity));
        o.Set("loss_tangent", N(LossTangent));
        o.Set("magnetic_loss_tangent", HznJsonScalar.String("0"));
        o.SetBool("pec", Pec);
        o.SetString("dispersion", "None");
        o.SetBool("anisotropic", false);
        return o;
    }

    /// <summary>The numbers a project material states; null when a property is an expression, or a kind (dispersion, anisotropy, a
    /// magnetic loss) this comparison does not cover.</summary>
    public static HznMaterialValues? Of(HznMaterial m)
    {
        double? Num(string key, double fallback)
        {
            string? t = m.Json.GetString(key);
            if (string.IsNullOrWhiteSpace(t)) return fallback;
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
        }
        string disp = m.Json.GetString("dispersion") ?? "None";
        if (disp.Length > 0 && disp != "None") return null;
        if (m.Json.GetBool("anisotropic") == true) return null;
        if (Num("magnetic_loss_tangent", 0) is not 0) return null;
        if (Num("permittivity", 1) is not { } e || Num("permeability", 1) is not { } mu || Num("conductivity", 0) is not { } s
            || Num("loss_tangent", 0) is not { } t) return null;
        return new HznMaterialValues(m.Name ?? "", e, mu, s, t, m.Json.GetBool("pec") == true);
    }
}

internal sealed partial class C3dToHznWriter
{
    /// <summary>The materials Hertzon has built in, as the values circuitRF reads them back with.</summary>
    internal static readonly IReadOnlyList<HznMaterialValues> BuiltIn =
    [
        new("pec", 1, 1, 0, 0, true),
        new("vacuum", 1, 1, 0, 0, false),
        new("air", 1, 1, 0, 0, false),
        new("copper", 1, 1, 5.8e7, 0, false),
    ];

    private const double ConductorSigmaSm = HznTranslator.ConductorSigmaSm;

    private readonly Dictionary<string, HznMaterialValues> _wanted = new(StringComparer.Ordinal);   // by technology name
    private readonly Dictionary<string, string> _hznNameOf = new(StringComparer.Ordinal);            // technology name -> Hertzon name
    private readonly List<HznJsonObject> _projectMaterials = [];
    private readonly Dictionary<HObj, string> _materialOf = [];

    /// <summary>The Hertzon name of the material <paramref name="techName"/> is when an object of role <paramref name="role"/> uses it, or
    /// null with a refusal item.</summary>
    private string? Material(string techName, Em3dRole? role, string user)
    {
        if (_o.Technology is not { } tech)
        {
            _r.Add(HznRefusalKind.Material, $"material '{techName}'", "the export was given no technology to take it from", user);
            return null;
        }
        // The built-in Air is in no technology: it is free space, which Hertzon has built in as `air`.
        if (tech.FindMaterial(techName) is null && string.Equals(techName, Em3dGenerator.AirMaterial, StringComparison.OrdinalIgnoreCase) && role is null or Em3dRole.Dielectric)
            return "air";
        if (tech.FindMaterial(techName) is not { } m)
        {
            _r.Add(HznRefusalKind.Material, $"material '{techName}'", "it is not in the document's technology", user);
            return null;
        }
        if (Values(m, role, user) is not { } want) return null;
        if (_wanted.TryGetValue(m.Name, out var had))
        {
            if (had.DifferencesFrom(want, "for one object", "for another").Count > 0)
            {
                _r.Add(HznRefusalKind.Material, $"material '{m.Name}' used as two things",
                       "objects use it as a conductor and as a dielectric, and a Hertzon material is one or the other", user);
                return null;
            }
            return _hznNameOf[m.Name];
        }
        _wanted[m.Name] = want;

        var builtIn = BuiltIn.FirstOrDefault(b => string.Equals(b.Name, m.Name, StringComparison.OrdinalIgnoreCase));
        if (builtIn is not null)
        {
            var diff = builtIn.DifferencesFrom(want with { Name = builtIn.Name }, "in Hertzon's built-in", "here");
            if (diff.Count > 0)
            {
                _r.Add(HznRefusalKind.Material, $"material '{m.Name}'",
                       $"Hertzon has a built-in material of that name and these differ: {string.Join("; ", diff)}", user);
                return null;
            }
            return _hznNameOf[m.Name] = builtIn.Name;
        }
        _hznNameOf[m.Name] = m.Name;
        _projectMaterials.Add(want.ToJson());
        return m.Name;
    }

    private HznMaterialValues? Values(TechMaterial m, Em3dRole? role, string user)
    {
        if (m.EpsrTensor is { Length: 3 })
        {
            _r.Add(HznRefusalKind.Material, $"anisotropic material '{m.Name}'", "a direction-dependent permittivity is not written", user);
            return null;
        }
        if (MaterialDispersion.IsDispersive(m))
        {
            _r.Add(HznRefusalKind.Material, $"dispersive material '{m.Name}'", "a frequency-dependent permittivity and conductivity is not written", user);
            return null;
        }
        var implied = C3dMaterialRole.Implied(m);
        var effective = role ?? implied switch
        {
            C3dImpliedRole.Conductor => Em3dRole.Conductor,
            C3dImpliedRole.Dielectric or C3dImpliedRole.Air => Em3dRole.Dielectric,
            _ => (Em3dRole?)null,
        };
        if (effective is null)
        {
            _r.Add(HznRefusalKind.Material, $"material '{m.Name}'", "it states both a conductivity and a permittivity, or neither, and the object states no role", user);
            return null;
        }
        double mur = m.Mur ?? 1;
        if (effective == Em3dRole.Conductor)
        {
            if (m.Sigma20 is > 0 and var sigma)
            {
                if (sigma < ConductorSigmaSm)
                {
                    _r.Add(HznRefusalKind.Material, $"conductor '{m.Name}' below 1e6 S/m",
                           $"{sigma.ToString("R", CultureInfo.InvariantCulture)} S/m reads back as a lossy dielectric", user);
                    return null;
                }
                return new HznMaterialValues(m.Name, 1, mur, sigma, 0, false);
            }
            return new HznMaterialValues(m.Name, 1, mur, 0, 0, true);
        }
        double sig = m.Sigma20 ?? 0;
        if (sig >= ConductorSigmaSm)
        {
            _r.Add(HznRefusalKind.Material, $"dielectric '{m.Name}' with the conductivity of a metal",
                   "a material that conducts that well reads back as a conductor", user);
            return null;
        }
        return new HznMaterialValues(m.Name, m.Epsr ?? 1, mur, sig, m.TanD ?? 0, false);
    }

    /// <summary>A material for the sheet an object has no solid of: Hertzon's own vacuum.</summary>
    private const string SheetMaterial = "vacuum";

    private void MaterialsUsed()
    {
        foreach (var h in _objects)
        {
            if (h.IsSheet || h.Source is null) { _materialOf[h] = SheetMaterial; continue; }
            if (string.IsNullOrEmpty(h.Material))
            {
                // The solver ignores an object with no material; Hertzon needs one, so it is written out of the solve.
                _notes.Add($"'{h.Name}' has no material, so it is written as vacuum and left out of the solve.");
                h.Model = false;
                _materialOf[h] = SheetMaterial;
                continue;
            }
            if (Material(h.Material, h.Role, h.Name) is { } name) _materialOf[h] = name;
        }
        if (_regionMaterial is { Length: > 0 } rm && Material(rm, null, "air box") is { } rn) _regionMaterialName = rn;
    }
}
