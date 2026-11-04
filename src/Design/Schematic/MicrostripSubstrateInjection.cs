using System;
using System.Collections.Generic;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Devices.Planar;
using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Design.Layout.PCells;

using CircuitRF.Design.Layout;
using CircuitRF.Design.Workspace;
namespace CircuitRF.Design.Schematic;

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
        [SymbolKind.Mlin, SymbolKind.MCLine, SymbolKind.MBend, SymbolKind.MCurve, SymbolKind.MMiter, SymbolKind.MGap, SymbolKind.MTee, SymbolKind.MCross,
         SymbolKind.Mtaper, SymbolKind.Mklopf, SymbolKind.GsgPad];

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

        var techPath = CircuitRF.Core.RefPath.Resolve(workspaceDir, techRef);
        try { return TechPersistence.LoadFromFile(techPath); }
        catch { return null; }
    }

    /// <summary>
    /// The <c>.ctech</c> PATH <see cref="ResolveWorkspaceTechnology"/> would load, without loading it
    /// — the same walk, answered as a location.
    ///
    /// <para>Wanted by anything that has to COMPARE this resolution with another, rather than use it:
    /// a layout resolves its own <c>TechRef</c> first and only then the workspace default, so the two
    /// halves of one cell can legitimately be on different technologies and only the paths say
    /// so.</para>
    /// </summary>
    public static string? ResolveWorkspaceTechnologyPath(string? schematicDirectory)
    {
        var cwsPath = WorkspaceRootFinder.FindAncestorCws(schematicDirectory);
        if (cwsPath is null) return null;

        CwsFile cws;
        try { cws = WorkspacePersistence.LoadFromFile(cwsPath); }
        catch { return null; }

        if (cws.DefaultTechRef is not { Length: > 0 } techRef) return null;

        var workspaceDir = Path.GetDirectoryName(cwsPath);
        if (workspaceDir is null) return null;

        try   { return CircuitRF.Core.RefPath.Resolve(workspaceDir, techRef); }
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

        var overrides = new List<ParameterAssignment>
        {
            new("H",     Fmt(substrate.HeightMeters)),
            new("T",     Fmt(substrate.ThicknessMeters)),
            new("Er",    Fmt(substrate.RelativePermittivity)),
            new("Sigma", Fmt(substrate.ConductivitySPerM)),
            new("TanD",  Fmt(substrate.LossTangent)),
        };
        // The technology's copper roughness, only when it states one: a smooth technology's netlist is
        // unchanged. An instance's OWN Roughness wins over this (NetExtractor drops the injected one).
        if (substrate.RoughnessRmsMeters > 0)
            overrides.Add(new(RoughnessParameter, Fmt(substrate.RoughnessRmsMeters)));
        return overrides;
    }

    /// <summary>The engine parameter MLIN and MCURVE read the RMS roughness from (metres).</summary>
    public const string RoughnessParameter = "Roughness";

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
    /// True for a JUNCTION arm-length name — MTee's L1/L2/L3 and MCross's L1-L4. Deliberately NOT the
    /// bare <c>L</c>: an MLIN/MTaper/MKlopf <c>L</c> is a real transmission line whose length is the
    /// user's design choice, so it gets <see cref="NiceLengthFor"/>'s round 400 mil / 10 mm. A junction
    /// arm is a short stub whose only job is to give the junction pins at its own edge, so it defaults
    /// to its own arm's width — square — on every technology, rather than a round line length.
    /// </summary>
    private static bool IsJunctionArmLengthParam(SymbolKind kind, string name)
        => (kind is SymbolKind.MTee or SymbolKind.MCross)
           && name.Length == 2 && name[0] == 'L' && char.IsDigit(name[1]);

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
    /// <summary>The probe pitches a GSG probe is commonly made in, µm.</summary>
    private static readonly int[] ProbePitchesUm = [100, 125, 150, 200, 250, 300, 400, 500, 650, 800, 1000, 1250, 1500];

    /// <summary>
    /// A GSGPAD's probe numbers on the placing technology: pads at least as wide as the technology's via pad
    /// (each ground pad has to hold one), the pitch the smallest common probe pitch from 150 µm up that leaves
    /// half a pad between pads, and the line at least one pad long. 80 µm pads at 150 µm on a die; on a board, whose via
    /// pad is far larger, a pitch a board probe comes in. The probe is the user's: these are starting values.
    /// </summary>
    private static void ApplyProbePadDefaults(IEnumerable<EditableParameter> parameters, Technology technology)
    {
        var (span, _, _) = SubstrateResolver.ResolveViaSpan(technology, null, null, toGround: true);
        double viaPadUm = (span?.PadMeters is > 0 and var p ? p : CircuitRF.Core.Devices.ComponentModelFactory.DefaultViaPadMeters) * 1e6;
        double padUm = Math.Ceiling(Math.Max(80, viaPadUm) / 10) * 10;
        double minPitchUm = Math.Max(150, 1.5 * padUm);   // 150 µm, the registry's own, unless the pads need more
        int pitchUm = ProbePitchesUm.FirstOrDefault(pp => pp >= minPitchUm, (int)Math.Ceiling(minPitchUm / 50) * 50);
        double lineUm = Math.Max(100, padUm);
        string Um(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        foreach (var prm in parameters)
            (prm.Expression, prm.Unit) = prm.Name switch
            {
                "PadW" or "PadL" => (Um(padUm), "µm"),
                "Pitch"          => (Um(pitchUm), "µm"),
                "L"              => (Um(lineUm), "µm"),
                _                => (prm.Expression, prm.Unit),
            };
    }

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
        if (kind == SymbolKind.GsgPad) ApplyProbePadDefaults(parameters, technology);
        string targetUnit = SchematicLengthUnit(technology.DefaultDisplayUnit);
        double step = RoundStepFor(targetUnit);

        // 50 Ω for every width; 100 Ω for a taper's narrow end, so MTaper still tapers (and matches
        // MKlopf's own 50→100 default rather than inventing a second convention).
        double? w50Mm = null, w100Mm = null;
        var (substrate, _, _) = SubstrateResolver.ResolveElectrical(technology, new PCellLayerSelection(null, null));
        if (kind is SymbolKind.Cpwg or SymbolKind.Slin)
        {
            // brief-artsch-1: CPWG and SLIN synthesise their 50 Ω width on their OWN model and binding — a
            // microstrip width would be the wrong line. CPWG's gap is converted and rounded first, so the width
            // is the one that gives 50 Ω with the gap the user will actually see.
            var quietLine = new MicrostripValidityReporter("(placement default width synthesis)");
            double? gM = null;
            foreach (var p in parameters)
                if (p.Name == "G" && p.Unit == DefaultParameterUnit
                    && double.TryParse(p.Expression, NumberStyles.Float, CultureInfo.InvariantCulture, out double gMm))
                    gM = ConvertToMm(targetUnit, RoundToStep(ConvertMmTo(targetUnit, gMm), step)) / 1000.0;
            if (kind == SymbolKind.Cpwg && substrate is not null && gM is { } g)
                w50Mm = PlanarLineSynthesis.CpwgWidth(50.0, g, substrate.HeightMeters, substrate.ThicknessMeters,
                                                      substrate.RelativePermittivity, quietLine) * 1000.0;
            if (kind == SymbolKind.Slin && SubstrateResolver.ResolveStripline(technology, null).Stripline is { } sl)
                w50Mm = PlanarLineSynthesis.SlinWidth(50.0, sl.H1Meters, sl.H2Meters, sl.ThicknessMeters,
                                                      sl.RelativePermittivity, quietLine) * 1000.0;
            substrate = null;   // the microstrip synthesis below is not this line's
        }
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
            else if (kind == SymbolKind.MCLine && p.Name == "S" && w50Mm is not null)
            {
                // The gap starts at the strip's own width (S = W) on THIS technology: a loose, ordinary
                // starting coupling rather than a millimetre baseline that is absurd on a thin substrate.
                value = ConvertMmTo(targetUnit, w50Mm.Value);
            }
            else if (kind == SymbolKind.MGap && p.Name == "S" && w50Mm is not null)
            {
                // Half the line's own width on THIS technology: the middle of the closed form's S/W range,
                // where a millimetre baseline carried over would be a gap wider than the line on a thin board.
                value = ConvertMmTo(targetUnit, w50Mm.Value / 2.0);
            }
            else if (kind == SymbolKind.MCurve && p.Name == "Radius" && w50Mm is not null)
            {
                // 3W, the low end of the effective-radius rule's published 3W-7W range: the tightest
                // curve whose model is still inside its claim (MicrostripCurve), on THIS technology's
                // own 50-ohm width rather than a radius carried over from the millimetre baseline.
                value = ConvertMmTo(targetUnit, 3.0 * w50Mm.Value);
            }
            else if (p.Name == "L" && NiceLengthFor(targetUnit, kind) is { } nice)
            {
                value = nice;
            }
            else if (IsJunctionArmLengthParam(kind, p.Name) && w50Mm is not null)
            {
                // L == W (owner's call, 2026-08-12): a square stub per arm. Synthesised, not carried
                // over from the millimetre baseline, for the same reason the widths are — 7.25 mm of
                // arm hanging off a 42 mil PCB line is arithmetically faithful and visually absurd.
                //
                // The registry's own L default is likewise its W default, so this stays consistent
                // with a no-technology placement rather than being a second, technology-only rule.
                value = ConvertMmTo(targetUnit, w50Mm.Value);
            }
            else
            {
                // Offset (0), or a width with no resolvable substrate: keep the physical magnitude.
                value = ConvertMmTo(targetUnit, mmValue);
            }

            p.Expression = FormatLength(RoundToStep(value, step));
            p.Unit = targetUnit;
        }

        // MMiter's M is dimensionless, so the loop above never touches it: start it at the
        // Douville-James optimum for THIS technology's 50-ohm line (capped at the deepest miter the
        // model accepts at 90 deg), rather than the registry's technology-free 0.5.
        if (kind == SymbolKind.MMiter && substrate is not null && w50Mm is not null
            && parameters.FirstOrDefault(p => p.Name == "M") is { } miter)
        {
            double m = Math.Min(MicrostripMiterBend.DouvilleJamesOptimum(w50Mm.Value / 1000.0, substrate.HeightMeters),
                                MicrostripMiterBend.MaxMiter(90.0));
            miter.Expression = Math.Round(m, 3).ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    private static double ConvertToMm(string unit, double value) => value / ConvertMmTo(unit, 1.0);

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
