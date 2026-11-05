using System.Globalization;

namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// R-pc-9/10: everything a microstrip component's electrical model needs, resolved from a
/// technology's stackup between the chosen Signal Layer and Ground Reference.
/// </summary>
/// <param name="SignalLayer">The drawing <see cref="LayerKey"/> the artwork is painted on.</param>
/// <param name="SignalConductorName">The stackup conductor entry's own name (for diagnostics).</param>
/// <param name="GroundConductorName">The stackup conductor entry serving as ground reference.</param>
/// <param name="HeightMeters">h — the dielectric separation between signal and ground.</param>
/// <param name="RelativePermittivity">εr of the dielectric(s) between signal and ground. When more
/// than one dielectric layer intervenes, this is a thickness-weighted average — a stated
/// simplification (§9 open item of pcell-contract.md: exact multi-dielectric handling is
/// deferred).</param>
/// <param name="ThicknessMeters">t — the signal conductor's own thickness.</param>
/// <param name="ConductivitySPerM">σ — the signal conductor's conductivity.</param>
/// <param name="LossTangent">tanδ of the dielectric(s) between signal and ground (same
/// thickness-weighting note as <see cref="RelativePermittivity"/>).</param>
/// <param name="IsStripline">R-pc-10: true when the signal layer has a ground-designated
/// conductor both above and below it — a microstrip closed form is the wrong model.</param>
/// <param name="RoughnessRmsMeters">The signal conductor's RMS surface roughness
/// (<c>StackupLayer.RoughnessRmsM</c>), zero for a smooth conductor.</param>
public sealed record ResolvedSubstrate(
    LayerKey SignalLayer,
    string SignalConductorName,
    string GroundConductorName,
    double HeightMeters,
    double RelativePermittivity,
    double ThicknessMeters,
    double ConductivitySPerM,
    double LossTangent,
    bool IsStripline,
    double RoughnessRmsMeters = 0.0,
    double DielectricSigmaSPerM = 0.0);

/// <summary>Why <see cref="SubstrateResolver.ResolveElectrical"/> could not produce a
/// <see cref="ResolvedSubstrate"/> — always a human-readable sentence naming what is missing, per
/// §2 of brief-L5a-pcell-contract-and-microstrip.md ("refuse to stamp with a clear message naming
/// the missing technology").</summary>
public sealed record SubstrateResolutionFailure(string Reason);

/// <summary>
/// Resolves Signal Layer + Ground Reference from a workspace technology's stackup (R-pc-9), and
/// the electrical substrate parameters between them. Framework-free, pure — this is resolution
/// LOGIC only; the caller (a UI-layer piece for the schematic side, or the PCell artwork
/// generators directly) supplies the already-loaded <see cref="Technology"/>.
/// </summary>
public static partial class SubstrateResolver
{
    private const long FallbackDbuPerMicron = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>
    /// The drawing layer artwork should paint on — never fails. R-pc-9's default (topmost
    /// conductor) with an optional name override; falls back to a fixed <c>(1,0)</c> key when
    /// there is no technology, or no conductor at all, so a PCell's GEOMETRY is always
    /// generatable per §2 of the brief even with nothing resolved.
    /// </summary>
    public static LayerKey ResolveSignalLayerKey(Technology? technology, PCellLayerSelection selection, out IReadOnlyList<string> warnings)
    {
        var warn = new List<string>();
        warnings = warn;

        var signal = FindSignalConductor(technology, selection.SignalLayerNameOverride, warn);
        if (signal is null || signal.DrawingLayers.Count == 0)
            return new LayerKey(1, 0);

        return signal.DrawingLayers[0];
    }

    /// <summary>
    /// The full electrical substrate (R-pc-9's layer selection + the numbers the microstrip
    /// models need). Returns a <see cref="SubstrateResolutionFailure"/> — never throws, never
    /// returns a silently-wrong default — when the technology is missing, has no signal conductor,
    /// or has no ground reference on either side of it (§2 of the brief: "refuse to stamp with a clear
    /// message naming the missing technology").
    /// </summary>
    public static (ResolvedSubstrate? Substrate, SubstrateResolutionFailure? Failure, IReadOnlyList<string> Warnings) ResolveElectrical(
        Technology? technology, PCellLayerSelection selection)
    {
        var warn = new List<string>();

        if (technology is null)
            return (null, new SubstrateResolutionFailure("no technology resolved for this document"), warn);

        var layers = technology.Stackup.Layers;
        var signal = FindSignalConductor(technology, selection.SignalLayerNameOverride, warn);
        if (signal is null)
        {
            var reason = selection.SignalLayerNameOverride is { } name
                ? $"technology '{technology.Name}' has no conductor named '{name}'"
                : $"technology '{technology.Name}' stackup has no conductor layer";
            return (null, new SubstrateResolutionFailure(reason), warn);
        }

        int signalIndex = layers.IndexOf(signal);

        StackupLayer? ground = null;
        if (selection.GroundLayerNameOverride is { Length: > 0 } gName)
        {
            ground = layers.FirstOrDefault(l => l.Kind == StackupKind.Conductor &&
                                                 string.Equals(l.Name, gName, StringComparison.OrdinalIgnoreCase));
            if (ground is null)
            {
                // R-tec-9: a named ground override that no longer exists reports and falls back to
                // the inferred default (nearest ground-designated conductor beneath), exactly like
                // the signal-layer case above — a stale reference must not fail the component.
                warn.Add($"no conductor named '{gName}' in technology '{technology.Name}' — falling back to the default ground reference");
            }
        }
        // Nearest ground beneath first (the conventional microstrip, and every pre-existing answer
        // unchanged); failing that, the nearest ground-designated conductor ABOVE. A line routed on the
        // bottom of a board, or on an inner layer under its plane, is the same microstrip mirrored.
        ground ??= FindNearestGroundBeneath(technology.Stackup, signalIndex)
                   ?? FindNearestGroundAbove(technology.Stackup, signalIndex);

        if (ground is null)
        {
            var reason = $"technology '{technology.Name}' has no ground-designated conductor above or beneath '{signal.Name}' " +
                         "(mark a conductor StackupLayer.IsGroundReference, or supply an explicit override)";
            return (null, new SubstrateResolutionFailure(reason), warn);
        }

        int groundIndex = layers.IndexOf(ground);
        if (groundIndex == signalIndex)
        {
            return (null, new SubstrateResolutionFailure(
                $"ground reference '{ground.Name}' is the signal layer itself in technology '{technology.Name}'"), warn);
        }

        // A microstrip referenced to a plane ABOVE it is the same line mirrored: the substrate is the
        // dielectric between the two conductors whichever side the plane is on, and the closed forms
        // (which see only h, t, er, sigma, tan d) give the identical answer. Nothing about the
        // below-case changes — its span and its stripline test are exactly what they were.
        bool groundIsAbove = groundIndex < signalIndex;
        int spanFrom = Math.Min(signalIndex, groundIndex), spanTo = Math.Max(signalIndex, groundIndex);

        // R-pc-10: stripline check — a ground-designated conductor on the OTHER side of the signal too.
        bool groundOnOtherSide = groundIsAbove
            ? layers.Skip(signalIndex + 1).Any(l => l.Kind == StackupKind.Conductor && l.IsGroundReference)
            : layers.Take(signalIndex).Any(l => l.Kind == StackupKind.Conductor && l.IsGroundReference);
        if (groundOnOtherSide)
        {
            warn.Add($"'{signal.Name}' has a ground-designated conductor both above and below it — this is a " +
                     "stripline, not a microstrip; the Hammerstad-Jensen microstrip model is the wrong model here.");
        }

        // Sum the intervening dielectric thickness; thickness-weight εr/tanδ across it (exact for
        // the common single-dielectric case, an averaging simplification for a multi-dielectric span).
        long hDbu = 0;
        double weightedEpsr = 0, weightedTanD = 0;
        for (int i = spanFrom + 1; i < spanTo; i++)
        {
            var layer = layers[i];
            if (layer.Kind != StackupKind.Dielectric) continue;
            hDbu += layer.ThicknessDbu;
            weightedEpsr += layer.Epsr * layer.ThicknessDbu;
            weightedTanD += layer.TanD * layer.ThicknessDbu;
        }

        if (hDbu <= 0)
        {
            return (null, new SubstrateResolutionFailure(
                $"no positive-thickness dielectric found between '{signal.Name}' and '{ground.Name}' in technology '{technology.Name}'"), warn);
        }

        double h = DbuToMeters(hDbu, FallbackDbuPerMicron);
        double er = weightedEpsr / hDbu;

        // GI2 R-gi2-4's rule, on the third consumer of a stackup. Both EM extractors already refuse a
        // dielectric with er < 1; this path did not, because until GI2 nothing could HAND it one — an
        // unset Epsr read as its C# default of 1.0, which is air and computes. A Gerber import with no
        // job file now writes Epsr = 0 deliberately, so a stackup that has had its thicknesses filled
        // in but not its permittivities can reach here, and Kirschning-Jansen on er = 0 returns a
        // number rather than failing. Refuse it where it is still attributable to a layer.
        if (!(er >= 1))
        {
            return (null, new SubstrateResolutionFailure(
                $"the dielectric between '{signal.Name}' and '{ground.Name}' in technology '{technology.Name}' " +
                $"has a relative permittivity of {er:G4}; er is >= 1. Set it on the technology editor's " +
                "Stackup tab (an imported Gerber set carries no permittivity, so it is left unset)"), warn);
        }
        double tanD = weightedTanD / hDbu;
        double t = DbuToMeters(signal.ThicknessDbu, FallbackDbuPerMicron);

        var between = layers.Skip(spanFrom + 1).Take(spanTo - spanFrom - 1).Where(l => l.Kind == StackupKind.Dielectric);
        double dielectricSigma = between.Select(l => l.SigmaSm).DefaultIfEmpty(0).Max();
        if (ClosedFormConductionWarning(dielectricSigma, between) is { } conduction) warn.Add(conduction);

        var substrate = new ResolvedSubstrate(
            SignalLayer: signal.DrawingLayers.Count > 0 ? signal.DrawingLayers[0] : new LayerKey(1, 0),
            SignalConductorName: signal.Name,
            GroundConductorName: ground.Name,
            HeightMeters: h,
            RelativePermittivity: er,
            ThicknessMeters: t,
            ConductivitySPerM: signal.SigmaSm,
            LossTangent: tanD,
            IsStripline: groundOnOtherSide,
            RoughnessRmsMeters: signal.RoughnessRmsM ?? 0.0,
            DielectricSigmaSPerM: dielectricSigma);

        return (substrate, null, warn);
    }

    /// <summary>
    /// <b>SI-SIG — the conductivity above which a line's closed form does not apply</b>, S/m. These
    /// models take a low-loss substrate and read tanδ alone. At 1e-3 S/m (100 kΩ·cm) silicon's conduction
    /// tanδ is 0.0015 at 1 GHz, as small as a good laminate's; every resistivity anyone uses is above it.
    /// </summary>
    public const double ClosedFormConductivityLimitSm = 1e-3;

    /// <summary>The sentence a line model says about a conducting substrate, or null when it has none.</summary>
    internal static string? ClosedFormConductionWarning(double sigmaSm, IEnumerable<StackupLayer> between)
    {
        if (!(sigmaSm > ClosedFormConductivityLimitSm)) return null;
        string name = between.First(l => l.SigmaSm == sigmaSm).Name;
        return $"'{name}' conducts, σ {sigmaSm.ToString("G3", CultureInfo.InvariantCulture)} S/m. The line's closed form " +
               "assumes a low-loss substrate and does not apply here: its loss and its phase velocity leave the " +
               "conductivity out. Use an EM run on this line.";
    }

    private static StackupLayer? FindSignalConductor(Technology? technology, string? nameOverride, List<string> warn)
    {
        if (technology is null) return null;
        var conductors = technology.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        if (conductors.Count == 0) return null;

        if (nameOverride is { Length: > 0 } name)
        {
            var found = conductors.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (found is not null) return found;

            // R-tec-9 (brief-technology-editor-units-and-layers.md): a named override that no
            // longer exists (a rename, or a technology swap) reports and falls back to the
            // inferred default — a rename/retarget should not silently break the component, and
            // failing it outright (the OLD behavior here) is the wrong response to a stale
            // reference. Falls through to the default below.
            warn.Add($"no conductor named '{name}' in technology '{technology.Name}' — falling back to the default signal layer (topmost conductor)");
        }

        // R-pc-9 default: the topmost conductor (Stackup.Layers is ordered top to bottom).
        return conductors[0];
    }

    private static StackupLayer? FindNearestGroundBeneath(Stackup stackup, int fromIndexExclusive)
    {
        var layers = stackup.Layers;
        for (int i = fromIndexExclusive + 1; i < layers.Count; i++)
        {
            if (layers[i].Kind == StackupKind.Conductor && layers[i].IsGroundReference)
                return layers[i];
        }

        // Backward-compatible fallback: a .ctech saved before StackupLayer.IsGroundReference
        // existed (or hand-authored without it) has NO conductor marked at all. Fires ONLY when
        // the whole stack has EXACTLY two conductors — unambiguous by construction, since there is
        // only one possible (signal, ground) pairing regardless of which is "on top." A 3+-
        // conductor stack (e.g. MMIC's Metal2/Metal1/Backside Metal) must stay ambiguous-by-
        // default so an explicit marker/override keeps meaning something (R-pc-9's own "genuinely
        // ambiguous on a 4-layer board" case) — checking only "nothing marked + Bottom==Ground"
        // would have silently guessed wrong there too. For the plain 2-conductor board, the
        // bottom-most conductor is the only sensible ground reference, matching the "drop an MLIN,
        // it already knows FR-4" zero-config promise (R-pc-8) regardless of when the .ctech file
        // on disk was written.
        var allConductors = layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        bool anyExplicitGround = allConductors.Any(l => l.IsGroundReference);
        if (!anyExplicitGround && allConductors.Count == 2 && stackup.Bottom == BoundaryCondition.Ground)
        {
            var bottomMost = allConductors[^1];
            int bottomMostIndex = layers.IndexOf(bottomMost);
            if (bottomMostIndex > fromIndexExclusive)
                return bottomMost;
        }

        return null;
    }

    /// <summary>The nearest ground-designated conductor above the signal. Deliberately no legacy
    /// two-conductor fallback here: that fallback exists for a board whose bottom is ground, and
    /// guessing a plane ABOVE from an unmarked stackup would be a guess nothing in the file states.</summary>
    private static StackupLayer? FindNearestGroundAbove(Stackup stackup, int fromIndexExclusive)
    {
        var layers = stackup.Layers;
        for (int i = fromIndexExclusive - 1; i >= 0; i--)
        {
            if (layers[i].Kind == StackupKind.Conductor && layers[i].IsGroundReference)
                return layers[i];
        }
        return null;
    }

    private static double DbuToMeters(long dbu, long dbuPerMicron)
        => (double)LayoutUnits.FromDbu(dbu, LayoutUnit.Mm, (int)dbuPerMicron) / 1000.0;
}
