using System;
using System.Collections.Generic;
using CircuitRF.Core.Devices.Microstrip;
using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// R-pc-8 (brief-L5a-pcell-contract-and-microstrip.md): "a microstrip component resolves its
/// substrate from the workspace technology's stackup — no per-instance MSUB block. Open a PCB
/// workspace, drop an MLIN, and it already knows FR-4 1.6 mm." This is the seam that makes that
/// true for the SCHEMATIC/electrical side: at extraction time (before elaboration), resolve the
/// schematic's own workspace technology (§5A.2's ancestor-<c>.cws</c> walk — the document's
/// workspace, never whichever one happens to be open) via <see cref="WorkspaceRootFinder"/>, pick
/// Signal Layer + Ground Reference (<see cref="SubstrateResolver"/>), and inject H/T/Er/Sigma/TanD
/// as plain-number <see cref="ParameterAssignment"/> overrides — mirroring the existing
/// string-param-device injection pattern documented in <c>src/Core/CLAUDE.md</c>, except this one
/// crosses the Ui→Core boundary at extraction time rather than inside the Elaborator, because only
/// Ui has workspace/file-system access (Core's numeric layer never touches a workspace — the
/// standing invariant).
///
/// H/T/Er/Sigma/TanD are deliberately NOT declared cell parameters (R-pc-2's "one list" is the
/// user-visible W/L/Angle/etc. set only) — they never appear in <c>ComponentTypeRegistry.
/// DefaultParameters</c> or the parameter editor; they exist solely as these injected overrides.
/// </summary>
public static class MicrostripSubstrateInjection
{
    private static readonly HashSet<SymbolKind> MicrostripKinds =
        [SymbolKind.Mlin, SymbolKind.MBend, SymbolKind.MTee, SymbolKind.MCross,
         SymbolKind.Mtaper, SymbolKind.Mklopf];

    public static bool IsMicrostripKind(SymbolKind kind) => MicrostripKinds.Contains(kind);

    /// <summary>
    /// R-pc-8/§5A.2: resolves the DOCUMENT's own workspace default technology — walks up from
    /// <paramref name="schematicDirectory"/> to the nearest ancestor <c>.cws</c>, reads its
    /// <c>DefaultTechRef</c>, loads that <c>.ctech</c>. Returns null (never throws) on any failure
    /// — no ancestor workspace, no default tech set, a missing/corrupt <c>.ctech</c> — matching
    /// every other technology-resolution path in this codebase's "non-fatal, resolves to null"
    /// convention. Per-schematic <c>TechRef</c> overrides do not exist yet (the brief: "add a
    /// per-schematic override only if a need appears").
    /// </summary>
    public static Technology? ResolveWorkspaceTechnology(string? schematicDirectory)
    {
        var cwsPath = WorkspaceRootFinder.FindAncestorCws(schematicDirectory);
        if (cwsPath is null) return null;

        CwsFile cws;
        try { cws = WorkspacePersistence.LoadFromFile(cwsPath); }
        catch { return null; }

        if (cws.DefaultTechRef is not { Length: > 0 } techRef) return null;

        var workspaceDir = Path.GetDirectoryName(cwsPath);
        if (workspaceDir is null) return null;

        var techPath = Path.GetFullPath(Path.Combine(workspaceDir, techRef));
        try { return TechPersistence.LoadFromFile(techPath); }
        catch { return null; }
    }

    /// <summary>
    /// Builds the H/T/Er/Sigma/TanD overrides for one microstrip instance. Values are emitted as
    /// bare numbers (no <see cref="ParameterAssignment"/> unit) since they are already resolved SI
    /// values — applying a unit on top would double-scale them.
    ///
    /// §2 of the brief: "No technology resolved — the geometry is still generatable... the
    /// electrical model is not." When <paramref name="technology"/> is null or resolution
    /// otherwise fails, this returns NO overrides (never a guessed substrate) — the electrical
    /// model then falls back to its own defaults and there is nothing here to report "why," which
    /// is why <paramref name="warning"/> carries the reason for the caller (NetExtractor) to
    /// surface via Messages, naming exactly what is missing.
    /// </summary>
    public static IReadOnlyList<ParameterAssignment> BuildOverrides(
        Technology? technology, out string? warning,
        string? signalLayerNameOverride = null, string? groundLayerNameOverride = null)
    {
        var selection = new PCellLayerSelection(signalLayerNameOverride, groundLayerNameOverride);
        var (substrate, failure, warnings) = SubstrateResolver.ResolveElectrical(technology, selection);

        if (substrate is null)
        {
            warning = failure?.Reason;
            return [];
        }

        warning = warnings.Count > 0 ? string.Join(" ", warnings) : null;

        return
        [
            new ParameterAssignment("H",     Fmt(substrate.HeightMeters)),
            new ParameterAssignment("T",     Fmt(substrate.ThicknessMeters)),
            new ParameterAssignment("Er",    Fmt(substrate.RelativePermittivity)),
            new ParameterAssignment("Sigma", Fmt(substrate.ConductivitySPerM)),
            new ParameterAssignment("TanD",  Fmt(substrate.LossTangent)),
        ];
    }

    private static string Fmt(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The unit <see cref="ComponentTypeRegistry.DefaultParameters"/> hardcodes for every
    /// microstrip Length-dimension default (W/L/W1-W4) — the fixed, technology-independent
    /// baseline <see cref="ApplyTechnologyLengthUnit"/> rewrites away from.</summary>
    private const string DefaultParameterUnit = "mm";

    /// <summary>
    /// <b>SUPERSEDED (2026-07-30) — do NOT wire this into placement again.</b> Converting the fixed
    /// 2.9 mm baseline is exactly what produced the owner-reported <c>W = 114.1732 mil</c>: an ugly
    /// starting point, and the wrong width for anything that is not 1.6 mm FR-4.
    /// <see cref="ApplyTechnologyDefaults"/> replaced it at the placement call site and is what new
    /// code should use — it synthesises the width for 50 Ω on the technology's own substrate and
    /// rounds in that technology's own unit. This overload is retained only because its unit-mapping
    /// behaviour is still covered by tests; it has no production caller.
    ///
    /// <para>Original note follows.</para>
    ///
    /// Owner-reported usability fix: a freshly-placed MLIN/MBend/MTee/MCross's W/L/W1-W4 defaulted
    /// to millimetres regardless of the placing workspace's own convention (mil on a PCB board, µm
    /// on an MMIC die) — jarring on a technology whose own <c>DefaultDisplayUnit</c> says
    /// otherwise. Called once, right after <c>ComponentTypeRegistry.DefaultParameters</c>
    /// materializes a freshly-placed component's parameters (<c>SchematicViewModel.
    /// CommitPlacement</c>): rewrites every Length-dimension parameter still carrying the
    /// hardcoded <see cref="DefaultParameterUnit"/> to the resolved workspace technology's own
    /// unit, preserving the SAME physical magnitude (2.9mm becomes ~114.2mil, not a bare "2.9mil").
    /// A no-op — the mm default stands — when no workspace technology resolves, or its default
    /// unit already IS mm.
    /// </summary>
    public static void ApplyTechnologyLengthUnit(IEnumerable<EditableParameter> parameters, string? schematicDirectory)
        => ApplyTechnologyLengthUnitCore(parameters, ResolveWorkspaceTechnology(schematicDirectory));

    /// <summary>Overload taking an already-resolved technology directly (avoids re-walking the
    /// ancestor-<c>.cws</c> chain when the caller already has one, e.g. from the same placement
    /// call that also injects substrate overrides).</summary>
    public static void ApplyTechnologyLengthUnit(IEnumerable<EditableParameter> parameters, Technology? technology)
        => ApplyTechnologyLengthUnitCore(parameters, technology);

    private static void ApplyTechnologyLengthUnitCore(IEnumerable<EditableParameter> parameters, Technology? technology)
    {
        if (technology is null) return;
        string targetUnit = SchematicLengthUnit(technology.DefaultDisplayUnit);
        if (targetUnit == DefaultParameterUnit) return;

        foreach (var p in parameters)
        {
            if (p.Unit != DefaultParameterUnit) continue;
            if (!double.TryParse(p.Expression, NumberStyles.Float, CultureInfo.InvariantCulture, out double mmValue))
                continue;

            p.Expression = FormatLength(ConvertMmTo(targetUnit, mmValue));
            p.Unit = targetUnit;
        }
    }

    /// <summary>
    /// The Length-dimension unit string a NEWLY WRITTEN microstrip parameter should use for the
    /// given (already-resolved) technology — the same PCB→mil / MMIC→µm / else-mm mapping
    /// <see cref="ApplyTechnologyLengthUnit"/> itself rewrites an existing "mm" default to. Exposed
    /// directly for a caller building a brand-new expression from scratch (e.g. the MKlopf entry-
    /// mode switch converting Z1/Z2↔W1/W2 or L↔F3db in <c>ParameterEditorViewModel</c>), which needs
    /// to pick the right unit UP FRONT rather than write "mm" and rely on a later rewrite pass that
    /// only fires at placement time. <c>null</c> (no technology resolved) falls back to "mm", same
    /// as every other technology-absent case in this class.
    /// </summary>
    public static string LengthUnitFor(Technology? technology)
        => technology is null ? DefaultParameterUnit : SchematicLengthUnit(technology.DefaultDisplayUnit);

    /// <summary>Maps a layout DBU unit to the matching schematic Length-dimension unit string
    /// (<c>ComponentTypeRegistry.UnitOptions(UnitDimension.Length)</c>) — the two unit systems are
    /// independent (DBU integers vs. expression-engine doubles), so this is a name translation,
    /// not a numeric conversion. <see cref="LayoutUnit.Inch"/> has no matching Length option (that
    /// list stops at "mil"); falls back to mm rather than guessing.</summary>
    private static string SchematicLengthUnit(LayoutUnit unit) => unit switch
    {
        LayoutUnit.Nm  => "nm",
        LayoutUnit.Um  => "µm",
        LayoutUnit.Mm  => "mm",
        LayoutUnit.Mil => "mil",
        _              => DefaultParameterUnit,
    };

    // ── Placement-time "nice" defaults ────────────────────────────────────────

    /// <summary>
    /// Rounding step for a freshly-placed microstrip default, in that unit's own terms. A default is
    /// a STARTING POINT the user edits, so a round number is worth more than the last decimal of a
    /// synthesised width — 42 mil, not 42.0138 mil.
    /// </summary>
    private static double RoundStepFor(string unit) => unit switch
    {
        "mil" => 1.0,        // owner: "for mil units, be sure to round to the nearest mil"
        "µm"  => 1.0,        // MMIC linewidths are quoted in whole microns
        "nm"  => 10.0,
        "mm"  => 0.1,        // 2.9 mm, not 2.8734 mm
        "cm"  => 0.01,
        "metre" => 0.0001,
        _     => 0.01,
    };

    private static double RoundToStep(double value, double step)
        => step <= 0 ? value : System.Math.Round(value / step, System.MidpointRounding.AwayFromZero) * step;

    /// <summary>
    /// Default LENGTH for a freshly-placed microstrip component, in the technology's own unit — a
    /// round number rather than whatever a fixed millimetre baseline converts to. Null means "no
    /// opinion", and the caller keeps the converted registry default.
    ///
    /// <para>MKlopf gets a longer line on purpose: a Klopfenstein taper needs real electrical length
    /// to do its job, so a length that suits an MLIN would place a visibly useless taper.</para>
    /// </summary>
    private static double? NiceLengthFor(string unit, SymbolKind kind)
    {
        bool klopf = kind == SymbolKind.Mklopf;
        return unit switch
        {
            "mil" => klopf ? 800.0 : 400.0,     // owner's own suggestions
            "mm"  => klopf ? 20.0  : 10.0,      // ~the same physical size, rounded for mm
            "µm"  => klopf ? 1000.0 : 500.0,    // MMIC scale — 400 mil on a die would be absurd
            _     => null,
        };
    }

    /// <summary>True for the width-carrying parameter names: W, W1…W4.</summary>
    private static bool IsWidthParam(string name)
        => name.Length >= 1 && name[0] == 'W'
           && (name.Length == 1 || (name.Length == 2 && char.IsDigit(name[1])));

    /// <summary>
    /// Rewrites a freshly-placed microstrip component's Length defaults to values that suit the
    /// placing technology: widths SYNTHESISED for 50 Ω on that technology's own substrate, lengths
    /// set to a round number, everything rounded to a sensible step in the technology's own unit.
    ///
    /// <para>Supersedes the unit-only rewrite this class used to do. That preserved the physical
    /// magnitude of a fixed 2.9 mm baseline, which on a PCB technology surfaced as <c>114.1732
    /// mil</c> — arithmetically right, and a poor thing to hand a user as a starting point. It was
    /// also wrong for the board: 2.9 mm is 50 Ω on 1.6 mm FR-4, not on 20 mil RO4350B, where 50 Ω is
    /// ~42 mil.</para>
    ///
    /// <para>Degrades in two steps, never throws: if the substrate cannot be resolved (no ground
    /// reference, say) the widths fall back to the converted registry default; if no technology
    /// resolves at all the millimetre defaults stand untouched. Both still get unit rounding, so the
    /// long-decimal number cannot come back.</para>
    /// </summary>
    public static void ApplyTechnologyDefaults(
        IEnumerable<EditableParameter> parameters, string? schematicDirectory, SymbolKind kind)
        => ApplyTechnologyDefaultsCore(parameters, ResolveWorkspaceTechnology(schematicDirectory), kind);

    /// <summary>Overload taking an already-resolved technology (avoids re-walking the ancestor
    /// <c>.cws</c> chain when the caller already has one).</summary>
    public static void ApplyTechnologyDefaults(
        IEnumerable<EditableParameter> parameters, Technology? technology, SymbolKind kind)
        => ApplyTechnologyDefaultsCore(parameters, technology, kind);

    private static void ApplyTechnologyDefaultsCore(
        IEnumerable<EditableParameter> parameters, Technology? technology, SymbolKind kind)
    {
        if (technology is null) return;   // registry mm defaults stand — nothing to resolve against
        string targetUnit = SchematicLengthUnit(technology.DefaultDisplayUnit);
        double step = RoundStepFor(targetUnit);

        // 50 Ω for every width; 100 Ω for a taper's narrow end, so MTaper still tapers (and matches
        // MKlopf's own 50→100 default rather than inventing a second convention).
        double? w50Mm = null, w100Mm = null;
        var (substrate, _, _) = SubstrateResolver.ResolveElectrical(technology, new PCellLayerSelection(null, null));
        if (substrate is not null)
        {
            var quiet = new MicrostripValidityReporter("(placement default width synthesis)");
            w50Mm  = HammerstadJensen.SynthesizeWidth(50.0,  substrate.HeightMeters,
                        substrate.ThicknessMeters, substrate.RelativePermittivity, quiet) * 1000.0;
            w100Mm = HammerstadJensen.SynthesizeWidth(100.0, substrate.HeightMeters,
                        substrate.ThicknessMeters, substrate.RelativePermittivity, quiet) * 1000.0;
        }

        foreach (var p in parameters)
        {
            if (p.Unit != DefaultParameterUnit) continue;
            if (!double.TryParse(p.Expression, NumberStyles.Float, CultureInfo.InvariantCulture, out double mmValue))
                continue;

            double value;
            if (IsWidthParam(p.Name) && w50Mm is not null)
            {
                // MTaper's W2 is the narrow end; every other width is the 50 Ω line.
                bool narrowEnd = kind == SymbolKind.Mtaper && p.Name == "W2";
                value = ConvertMmTo(targetUnit, narrowEnd ? w100Mm!.Value : w50Mm.Value);
            }
            else if (p.Name == "L" && NiceLengthFor(targetUnit, kind) is { } nice)
            {
                value = nice;
            }
            else
            {
                // Offset (0), or a width with no resolvable substrate: keep the physical magnitude.
                value = ConvertMmTo(targetUnit, mmValue);
            }

            p.Expression = FormatLength(RoundToStep(value, step));
            p.Unit = targetUnit;
        }
    }

    private static double ConvertMmTo(string unit, double mm) => unit switch
    {
        "nm"  => mm * 1_000_000.0,
        "µm"  => mm * 1_000.0,
        "mm"  => mm,
        "cm"  => mm / 10.0,
        "metre" => mm / 1000.0,
        "mil" => mm / 0.0254,
        _     => mm,
    };

    private static string FormatLength(double value)
        => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
}
