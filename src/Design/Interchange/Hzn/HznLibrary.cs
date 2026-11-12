// The materials Hertzon has built in that a design may name without defining: the values it states for each.
//
// Read from an installed Hertzon (0.3.19) as a black box: its material listing, through its own automation interface. Only the
// materials with a generic name are here (a substance, not a product), and only those circuitRF's material model states exactly:
// no magnetic conductors (μr far from 1) and no conductor below the 1e6 S/m line a project material is read by. A metal's
// permeability within 1e-4 of 1 (copper 0.999991, aluminum 1.000021, silver 0.99998) is written 1: it enters a metal only through
// the square root in its surface impedance, a few parts per million, and a technology's metal states none, so the export would
// otherwise refuse every technology copper as different from Hertzon's.

namespace CircuitRF.Design.Interchange.Hzn;

/// <summary>A material of Hertzon's built-in library, in the units a project material states them.</summary>
internal sealed record HznLibraryMaterial(string Name, double Epsr, double Mur, double SigmaSm, double TanD, bool Pec = false)
{
    public bool Conductor => Pec || SigmaSm >= HznTranslator.ConductorSigmaSm;
}

internal static class HznLibrary
{
    /// <summary>Every library material circuitRF reads, by Hertzon's name (case as Hertzon spells it).</summary>
    public static readonly IReadOnlyList<HznLibraryMaterial> Materials =
    [
        new("pec", 1, 1, 0, 0, Pec: true),
        new("vacuum", 1, 1, 0, 0),
        new("air", 1.0006, 1.0000004, 0, 0),
        new("copper", 1, 1, 5.8e7, 0),
        new("aluminum", 1, 1, 3.8e7, 0),
        new("gold", 1, 1, 4.1e7, 0),
        new("silver", 1, 1, 6.1e7, 0),
        new("brass", 1, 1, 1.5e7, 0),
        new("stainless_steel", 1, 1, 1.1e6, 0),
        new("tin", 1, 1, 8.67e6, 0),
        new("titanium", 1, 1, 2.38e6, 0),
        new("tungsten", 1, 1, 1.82e7, 0),
        new("platinum", 1, 1, 9.3e6, 0),
        new("solder", 1, 1, 7e6, 0),
        new("FR4_epoxy", 4.4, 1, 0, 0.02),
        new("polyethylene", 2.25, 1, 0, 0.0002),
        new("polystyrene", 2.6, 1, 0, 0.0003),
        new("polyimide", 3.5, 1, 0, 0.008),
        new("Alumina_92pct", 9.2, 1, 0, 0.008),
        new("Al2_O3_ceramic", 9.8, 1, 0, 0.0001),
        new("quartz", 3.78, 1, 0, 0.0001),
        new("glass", 5.5, 1, 0, 0.0005),
        new("silicon", 11.9, 1, 0.01, 0),
        new("silicon_high_resistivity", 11.9, 1, 0.0005, 0),
        new("silicon_dioxide", 4, 1, 0, 0),
        new("silicon_nitride", 7.5, 1, 0, 0.001),
        new("gallium_arsenide", 12.9, 1, 0, 0.0006),
        new("indium_phosphide", 12.4, 1, 0, 0.0005),
        new("aluminum_nitride", 8.8, 1, 0, 0.0005),
        new("beryllium_oxide", 6.7, 1, 0, 0.0003),
        new("sapphire", 10.06, 1, 0, 0.0001),
        new("water_distilled", 81, 1, 0, 0.0001),
        new("water_sea", 81, 1, 4, 0),
        new("ABS_plastic", 2.8, 1, 0, 0.01),
        new("PLA", 2.75, 1, 0, 0.01),
        new("PEEK", 3.2, 1, 0, 0.003),
        new("nylon", 3.0, 1, 0, 0.02),
        new("PTFE_glass", 2.5, 1, 0, 0.0015),
        // The one trademarked name read here, by the owner's decision (2026-11-11): Hertzon's own coax examples name it, as plain PTFE.
        // The laminate products in Hertzon's library stay unread; a design naming one is refused and can define it in the project.
        new("Teflon (tm)", 2.1, 1, 0, 0.001),
    ];

    /// <summary>The library material of that exact name, or null.</summary>
    public static HznLibraryMaterial? Find(string name) => Materials.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));

    /// <summary>The names the export treats as Hertzon's own: a technology material of one of these names is written as the built-in,
    /// never as a project material of the same name.</summary>
    public static readonly IReadOnlyList<string> Reserved = ["pec", "vacuum", "air", "copper"];
}
