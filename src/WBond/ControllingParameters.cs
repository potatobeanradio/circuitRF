using System.Globalization;

namespace CircuitRF.WBond;

/// <summary>
/// The values a schematic's <b>controlling parameters</b> carry, already reduced to the units this
/// layer works in: <b>lengths in metres</b>, names as plain strings.
///
/// <para>Deliberately not the expression engine's <c>Value</c> type, and deliberately not raw text.
/// Two callers reduce to this from very different places — <c>ComponentModelFactory</c> from a
/// fully-resolved elaboration dictionary, <c>WBondCellSeeding</c> from an editable component's own
/// literal expressions — and the geometry below must not be able to tell them apart, which is the
/// whole reason it lives here rather than in either of them.</para>
///
/// <para>Keys are the parameter names, unsuffixed (<c>LoopHeight</c>) or array-suffixed
/// (<c>LoopHeight_G1</c>), matched case-insensitively: a schematic writes the exact spelling but a
/// hand-authored <c>.cnl</c> need not, and silently ignoring <c>loopheight_g1</c> is exactly the
/// flat-curve failure this whole area is haunted by.</para>
/// </summary>
public sealed class WBondOverrides
{
    /// <summary><c>LoopHeight</c>/<c>Diameter</c>, unsuffixed or array-suffixed, <b>in metres</b>.</summary>
    public Dictionary<string, double> Lengths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>Material</c>, unsuffixed or array-suffixed — a metal's name.</summary>
    public Dictionary<string, string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when nothing is set, so every caller can skip the work entirely.</summary>
    public bool IsEmpty => Lengths.Count == 0 && Names.Count == 0;

    /// <summary>Records a length, ignoring a null (an unset parameter is not a value).</summary>
    public void SetLength(string name, double? metres)
    {
        if (metres is { } m) Lengths[name] = m;
    }

    /// <summary>Records a name, ignoring null/blank for the same reason.</summary>
    public void SetName(string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) Names[name] = value.Trim();
    }

    private double? Length(string name) => Lengths.TryGetValue(name, out double v) ? v : null;

    private string? Name(string name) => Names.TryGetValue(name, out string? v) ? v : null;

    internal double? LengthFor(string parameter, string? arrayName) =>
        arrayName is null ? Length(parameter) : Length($"{parameter}_{arrayName}");

    internal string? NameFor(string parameter, string? arrayName) =>
        arrayName is null ? Name(parameter) : Name($"{parameter}_{arrayName}");
}

/// <summary>
/// The <b>controlling parameters</b> of <c>wbond.md</c> §5.5.1/WB44 — loop height, wire diameter and
/// wire material — applied to a <see cref="WBondDesign"/>.
///
/// <h3>They are an override layer, not an edit</h3>
/// <para>Everything here reshapes the design it is handed and reports what it did; it never touches a
/// payload or a file. The elaboration path hands it a design decoded on its way to the solver, so a
/// 21-point sweep re-elaborates 21 times and mutates the stored design <b>zero</b> times (WB44
/// property 1). That is also what makes these survive §9.6's Layout → Schematic, which replaces the
/// base geometry underneath them.</para>
///
/// <h3>Absent means "as drawn"</h3>
/// <para>Every parameter is optional. A wBond shipping <c>LoopHeight = 20 mil</c> among its DEFAULTS
/// would silently regenerate every existing design's wires on its next run, so <see cref="WBondOverrides"/>
/// carries only what was actually set and <see cref="ApplyTo"/> on an empty one is a no-op.</para>
///
/// <h3>Scope is the ARRAY</h3>
/// <para>O-10: array names <i>are</i> the pin names on the symbol, and a <see cref="LoopProfile"/> is an
/// editor-internal sharing mechanism a schematic user never sees — so <c>LoopHeight_G1</c> means array
/// G1. <c>LoopHeight_&lt;profile&gt;</c> keeps resolving for hand-authored <c>.cnl</c> files that predate
/// that decision; a name that is <b>both</b> resolves as the ARRAY and the collision is reported.</para>
///
/// <h3>A loop height rescales the wire; it does not regenerate it (owner, 2026-08-17)</h3>
/// <para><i>"I don't like this ball/wedge profile thing. It doesn't offer the user anything. Its setting
/// should never affect the geometry that the user authors."</i></para>
///
/// <para>This layer used to apply a loop height by writing the bound <see cref="LoopProfile"/>'s height
/// and re-generating every wire from it. <see cref="LoopProfile.ApplyTo"/> writes X and Y by linear
/// interpolation between the feet, so that <b>straightened any path the user had routed by hand</b>.
/// It now goes through <see cref="WireEdits.SetLoopHeightPreservingPath"/>, which changes the one
/// quantity that was asked for and leaves every X and Y exactly as authored.</para>
///
/// <para><b>Three things fell out of that, and all three are simplifications.</b> There is no longer a
/// shared-profile clone-on-write (nothing writes a profile, so one array's override cannot drag
/// another's wires); no bound-versus-detached asymmetry (a loop height is a property of the WIRE —
/// <see cref="Wire.LoopHeightNm"/> is defined as its own max z minus min z — so a wire dragged loose
/// from its profile is reached like any other, and the §2.0 report about skipped wires is retired); and
/// no span-ordering constraint, since nothing needs the points ordered along the chord.</para>
///
/// <h3>Why this is not in <c>ComponentModelFactory</c>, where it started</h3>
/// <para>It was, until the owner reported (2026-08-17) that three arrays set to 30/20/15 mil on the
/// schematic all arrived in the layout at the drawn 20 mil. <b>Update Layout from Schematic writes what
/// the schematic asks for</b>, so the seeding path needs this too, and one implementation is what keeps
/// the file that command writes and the netlist the next Run writes from disagreeing.</para>
/// </summary>
public static class ControllingParameters
{
    /// <summary>
    /// Applies <paramref name="overrides"/> to <paramref name="design"/> in place.
    /// </summary>
    /// <returns>
    /// Non-fatal things the caller should report, phrased without an instance name — the caller knows
    /// which component this is and this does not. Empty in the ordinary case.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// A non-positive length, or a metal the design does not declare. Both are refusals rather than
    /// fallbacks: <b>unset is distinct from zero</b>, and a mistyped metal quietly falling back to gold
    /// is a wrong answer that looks right.
    /// </exception>
    public static IReadOnlyList<string> ApplyTo(WBondDesign design, WBondOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(design);
        ArgumentNullException.ThrowIfNull(overrides);

        var notes = new List<string>();
        if (overrides.IsEmpty) return notes;

        double? allHeight = overrides.LengthFor("LoopHeight", null);
        double? allDiameter = overrides.LengthFor("Diameter", null);
        string? allMaterial = overrides.NameFor("Material", null);

        var arrayNames = new HashSet<string>(
            design.Arrays.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);

        // ── The legacy profile spelling, for names that are not also arrays ───
        //
        // `LoopHeight_<profile>` still resolves, for hand-authored `.cnl` files that predate O-10's
        // array scope. It reaches whichever arrays are bound to that profile and does the same
        // path-preserving thing to their wires; the PROFILE's own stated height is left alone, because
        // it no longer decides anything about geometry (see ApplyLoopHeight).
        var byProfile = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in design.Profiles)
        {
            if (overrides.LengthFor("LoopHeight", profile.Name) is not { } h) continue;

            if (arrayNames.Contains(profile.Name))
            {
                notes.Add(
                    $"'LoopHeight_{profile.Name}' names both a wire array and a loop profile. It was " +
                    $"applied to the ARRAY '{profile.Name}' — array names are the symbol's pin names, " +
                    "so they win. Rename the profile if the profile was meant.");
                continue;
            }

            byProfile[profile.Name] = h;
        }

        foreach (var array in design.Arrays)
        {
            // ── Loop height ───────────────────────────────────────────────────
            double? height = overrides.LengthFor("LoopHeight", array.Name) ?? allHeight;

            if (height is null && byProfile.Count > 0)
                foreach (var wire in array.Wires)
                    if (wire.ProfileBinding is { } binding && byProfile.TryGetValue(binding, out double h))
                    { height = h; break; }

            if (height is { } metres)
            {
                long nm = HeightNm(metres, $"array '{array.Name}'");
                foreach (var wire in array.Wires) WireEdits.SetLoopHeightPreservingPath(wire, nm);
            }

            // ── Diameter and material ─────────────────────────────────────────
            double? diameter = overrides.LengthFor("Diameter", array.Name) ?? allDiameter;
            if (diameter is { } dm)
            {
                long dnm = DiameterNm(dm, array.Name);
                foreach (var wire in array.Wires) wire.DiameterNm = dnm;
            }

            string? material = overrides.NameFor("Material", array.Name) ?? allMaterial;
            if (material is not null)
            {
                string resolved = ResolveMaterial(design, material, array.Name);
                foreach (var wire in array.Wires) wire.Material = resolved;
            }
        }

        return notes;
    }

    /// <summary>
    /// Metres → DBU, refusing a non-positive height. <b>Unset must be distinct from zero</b>: an absent
    /// parameter means "as drawn", and <c>0</c> is a mistake worth naming rather than a wire flattened
    /// onto the plane.
    /// </summary>
    private static long HeightNm(double metres, string scope)
    {
        if (!(metres > 0.0))
            throw new InvalidOperationException(
                $"wBond: loop height for {scope} must be positive; got {metres.ToString("G6", CultureInfo.InvariantCulture)} m. " +
                "Leave the parameter blank to keep the loop height the wires were drawn with.");

        return WBondUnits.FromMetres(metres);
    }

    /// <summary>Metres → DBU, refusing a non-positive diameter — <c>WBondDesign.Validate</c>'s own rule.</summary>
    private static long DiameterNm(double metres, string arrayName)
    {
        if (!(metres > 0.0))
            throw new InvalidOperationException(
                $"wBond: wire diameter for array '{arrayName}' must be positive; got " +
                $"{metres.ToString("G6", CultureInfo.InvariantCulture)} m. " +
                "Leave the parameter blank to keep the diameter the wires were drawn with.");

        long nm = WBondUnits.FromMetres(metres);
        if (nm <= 0)
            throw new InvalidOperationException(
                $"wBond: wire diameter for array '{arrayName}' rounds to zero at the design's own " +
                "nanometre resolution.");

        return nm;
    }

    /// <summary>
    /// Resolves a material NAME against the design's own table, <b>refusing an unknown one by name</b>.
    /// <see cref="WBondDesign.Materials"/> is user-extensible, so validating against the decoded design
    /// rather than the built-in four is what lets a user-defined metal be named from the schematic — and
    /// a typo is a wrong answer that would otherwise fall back to gold and look right.
    /// </summary>
    private static string ResolveMaterial(WBondDesign design, string requested, string arrayName)
    {
        var match = design.Materials.FirstOrDefault(
            m => string.Equals(m.Name, requested, StringComparison.OrdinalIgnoreCase));

        if (match is not null) return match.Name;

        throw new InvalidOperationException(
            $"wBond: array '{arrayName}' asks for wire material '{requested}', which this design does " +
            "not declare. Available: " + string.Join(", ", design.Materials.Select(m => m.Name)) + ".");
    }
}
