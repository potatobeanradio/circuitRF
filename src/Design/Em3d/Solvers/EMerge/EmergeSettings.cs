// brief-emi-4 R-emi4-1 — EMerge's own settings: the .cem/.c3d section (CemEmerge) and that section RESOLVED (EmergeSettings),
// which is the one place the defaults and the quality presets live. Mirrors CemPalace/PalaceSettings field for field where
// the two solvers share a notion, under the same key, so one word means one thing in both sections.

using System.Text.Json.Serialization;

namespace CircuitRF.Design.Layout.Em;

/// <summary>EMerge's own settings. Every field may be omitted, and an omitted field takes the preset's value for it
/// (<see cref="Quality"/>, Standard when omitted); an omitted section takes every default, and an empty one is never
/// written.</summary>
public sealed class CemEmerge
{
    /// <summary>The quality preset: Draft, Standard or Accurate, Palace's names. Omitted means Standard. An explicit field
    /// overrides the preset's value for that field only.</summary>
    public PalaceQuality? Quality { get; set; }

    /// <summary>The largest element allowed in each material, as a fraction of the wavelength in that material at the
    /// sweep's top frequency: EMerge's own resolution. Default by quality.</summary>
    public double? MaxElementWavelengths { get; set; }

    /// <summary>The element size along the edges of conductors and sheets, as a fraction of the smallest per-material
    /// maximum. Default by quality.</summary>
    public double? EdgeRefinement { get; set; }

    /// <summary>The finite-element order EMerge solves with, 1 or 2. Default 2.</summary>
    public int? ElementOrder { get; set; }

    /// <summary>Whether a driven sweep is built adaptively: EMerge solves a few frequencies and fits every requested point
    /// from them. Default true.</summary>
    public bool? AdaptiveSweep { get; set; }

    /// <summary>The adaptive sweep's tolerance on |S|. Default by quality.</summary>
    public double? Tolerance { get; set; }

    /// <summary>The most frequencies the adaptive sweep solves. Default 100, EMerge's own.</summary>
    public int? MaxSamples { get; set; }

    /// <summary>The most adaptive mesh refinement passes before the sweep; 0 solves on the initial mesh. Default by
    /// quality.</summary>
    public int? AdaptiveMesh { get; set; }

    /// <summary>
    /// brief-sdl-8 D7 — every one of <see cref="AdaptiveMesh"/>'s passes is made, whatever the change between two of them, so
    /// two geometries evaluated for one optimisation are meshed by the same number of passes. An EM-cell evaluation sets it;
    /// omitted, EMerge stops refining once S has converged, as it does by default.
    /// </summary>
    public bool? HoldMeshPasses { get; set; }

    /// <summary>
    /// brief-emi-7 — the frequencies, GHz, whose fields EMerge saves for the 3D view, as Palace's key means it: omitted saves
    /// the sweep's centre only, <c>[]</c> saves none, a list saves those. Each is a direct solve of its own after the sweep (the
    /// adaptive sweep fits S between its own samples and holds no field elsewhere), so a frequency outside the sweep changes
    /// nothing the sweep reports. An eigenmode run saves every mode unless this is <c>[]</c>.
    /// </summary>
    public List<double>? SaveFieldsGHz { get; set; }

    /// <summary>A copy, so an editor can change one without touching a setup that shares it.</summary>
    public CemEmerge Clone()
    {
        var c = (CemEmerge)MemberwiseClone();
        c.SaveFieldsGHz = SaveFieldsGHz is { } f ? [.. f] : null;
        return c;
    }

    /// <summary>True when no field is set: the same run as an omitted section, and never written. Not serialised: an
    /// embedded setup is written by reflection, which writes a get-only property too.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        Quality is null && MaxElementWavelengths is null && EdgeRefinement is null && ElementOrder is null &&
        AdaptiveSweep is null && Tolerance is null && MaxSamples is null && AdaptiveMesh is null && SaveFieldsGHz is null &&
        HoldMeshPasses is null;
}

/// <summary>
/// brief-emi-4 R-emi4-1/3 — the EMerge section RESOLVED: every field a value. <b>The defaults live here and nowhere
/// else</b>; the lowering, <c>check</c> and <c>explain</c> all read them from <see cref="Preset"/>.
/// </summary>
/// <param name="InitialSamples">The equally spaced frequencies the adaptive sweep starts from — EMerge's own default; not
/// a field of the section.</param>
public sealed record EmergeSettings(
    double MaxElementWavelengths,
    double EdgeRefinement,
    int    ElementOrder,
    bool   AdaptiveSweep,
    double Tolerance,
    int    MaxSamples,
    int    AdaptiveMesh,
    int    InitialSamples = EmergeSettings.EmergeInitialSamples)
{
    /// <summary>EMerge's own number of initial adaptive-sweep samples (<c>run_adaptive_sweep(n_initial=5)</c>).</summary>
    public const int EmergeInitialSamples = 5;

    /// <summary>brief-sdl-8 D7 — <see cref="CemEmerge.HoldMeshPasses"/>, resolved.</summary>
    public bool HoldMeshPasses { get; init; }

    /// <summary>
    /// A preset's values. Second order everywhere (EMerge's default; its first-order elements are for checks, not answers).
    /// <b>Draft</b> is calibrated on the 3D Eigenmode example's Cavity: its notch lands within one 5 MHz step of the
    /// converged answer (src/Design/RESOLVED.md, EMI-4). Standard and Accurate refine from there and, at Accurate, let
    /// EMerge refine the mesh itself.
    /// </summary>
    public static EmergeSettings Preset(PalaceQuality quality) => quality switch
    {
        PalaceQuality.Draft    => new(0.2,  0.25, 2, true, 1e-3, 100, 0),
        PalaceQuality.Accurate => new(0.1,  0.15, 2, true, 1e-4, 100, 3),
        _                      => new(0.15, 0.2,  2, true, 1e-3, 100, 0),
    };

    /// <summary>The settings an omitted section resolves to: the Standard preset.</summary>
    public static EmergeSettings Default { get; } = Preset(PalaceQuality.Standard);

    /// <summary>The section's values: the preset first (Standard when the section names none), then every field the
    /// section sets, each overriding the preset's value for that field.</summary>
    public static EmergeSettings Resolve(CemEmerge? section)
    {
        if (section is null) return Default;
        var p = Preset(section.Quality ?? PalaceQuality.Standard);
        return new(
            section.MaxElementWavelengths ?? p.MaxElementWavelengths,
            section.EdgeRefinement        ?? p.EdgeRefinement,
            section.ElementOrder          ?? p.ElementOrder,
            section.AdaptiveSweep         ?? p.AdaptiveSweep,
            section.Tolerance             ?? p.Tolerance,
            section.MaxSamples            ?? p.MaxSamples,
            section.AdaptiveMesh          ?? p.AdaptiveMesh) { HoldMeshPasses = section.HoldMeshPasses == true };
    }

    /// <summary>Every value that cannot be run, as sentences naming the field — empty when all can.</summary>
    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        // EMerge caps a resolution above 0.5 at 0.4 with a warning of its own; circuitRF says so before it starts instead.
        if (!(MaxElementWavelengths > 0 && MaxElementWavelengths <= 0.5))
            p.Add($"EMerge.MaxElementWavelengths is {MaxElementWavelengths}; it must be above 0 and at most 0.5 of a wavelength.");
        if (!(EdgeRefinement > 0 && EdgeRefinement <= 1))
            p.Add($"EMerge.EdgeRefinement is {EdgeRefinement}; it must be above 0 and at most 1.");
        if (ElementOrder is not (1 or 2))
            p.Add($"EMerge.ElementOrder is {ElementOrder}; EMerge solves with order 1 or 2.");
        if (!(Tolerance > 0) || double.IsInfinity(Tolerance))
            p.Add($"EMerge.Tolerance is {Tolerance}; it must be positive.");
        if (MaxSamples < InitialSamples)
            p.Add($"EMerge.MaxSamples is {MaxSamples}; the adaptive sweep starts from {InitialSamples}, so it must be at least that.");
        if (AdaptiveMesh < 0)
            p.Add($"EMerge.AdaptiveMesh is {AdaptiveMesh}; it must be 0 or more.");
        return p;
    }
}
