namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// What a stripline's model needs from the stackup (brief-artsch-1 R-as1-3): the signal conductor, the
/// nearest reference plane above and below it, and the dielectric between each.
/// </summary>
/// <param name="H1Meters">Dielectric from the signal conductor to the plane above.</param>
/// <param name="H2Meters">Dielectric from the signal conductor to the plane below.</param>
/// <param name="RelativePermittivity">Thickness-weighted over every dielectric between the two planes.</param>
/// <param name="LossTangent">Weighted the same way.</param>
public sealed record ResolvedStripline(
    string SignalConductorName,
    string PlaneAboveName,
    string PlaneBelowName,
    double H1Meters,
    double H2Meters,
    double RelativePermittivity,
    double ThicknessMeters,
    double ConductivitySPerM,
    double LossTangent,
    double DielectricSigmaSPerM = 0.0);

public static partial class SubstrateResolver
{
    /// <summary>A permittivity spread between the planes above this fraction of the weighted mean is
    /// worth saying: the model is homogeneous, so it is answering for a mixture.</summary>
    public const double StriplineEpsrSpreadWarning = 0.10;

    /// <summary>
    /// The stripline between the planes either side of the signal conductor. The signal conductor is
    /// <paramref name="signalLayerName"/> when one is named (a name the stackup does not have falls back to
    /// the default, with a warning, as every layer choice does); the default is the topmost conductor that
    /// HAS a plane on both sides, because the technology's default signal layer is its top copper, which
    /// never does. A conductor without a plane on each side is a FAILURE naming the missing plane: a
    /// stripline with one plane is a microstrip, and answering it as one would be a silent MLIN.
    /// </summary>
    public static (ResolvedStripline? Stripline, SubstrateResolutionFailure? Failure, IReadOnlyList<string> Warnings) ResolveStripline(
        Technology? technology, string? signalLayerName)
    {
        var warn = new List<string>();
        if (technology is null)
            return (null, new SubstrateResolutionFailure("no technology resolved for this document"), warn);

        var layers = technology.Stackup.Layers;
        var conductors = layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        if (conductors.Count == 0)
            return (null, new SubstrateResolutionFailure($"technology '{technology.Name}' stackup has no conductor layer"), warn);

        StackupLayer? signal = null;
        if (signalLayerName is { Length: > 0 } name)
        {
            signal = conductors.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (signal is null)
                warn.Add($"no conductor named '{name}' in technology '{technology.Name}' — falling back to the default " +
                         "stripline layer (the topmost conductor with a plane on both sides)");
        }
        if (signal is null)
        {
            signal = conductors.FirstOrDefault(c =>
            {
                int i = layers.IndexOf(c);
                return PlaneAbove(layers, i) is not null && PlaneBelow(layers, i) is not null;
            });
            if (signal is null)
                return (null, new SubstrateResolutionFailure(
                    $"technology '{technology.Name}' has no conductor with a ground-designated plane both above and below it, " +
                    "so no layer of it carries a stripline"), warn);
        }

        int si = layers.IndexOf(signal);
        var above = PlaneAbove(layers, si);
        var below = PlaneBelow(layers, si);
        if (above is null || below is null)
        {
            string missing = above is null && below is null ? "above or below it"
                           : above is null ? $"above it (the plane below is '{below!.Name}')"
                           : $"below it (the plane above is '{above.Name}')";
            return (null, new SubstrateResolutionFailure(
                $"'{signal.Name}' in technology '{technology.Name}' has no ground-designated plane {missing}; a stripline " +
                "needs one on each side. Use MLIN or CPWG for a line with one plane, or mark the plane " +
                "StackupLayer.IsGroundReference"), warn);
        }

        int ai = layers.IndexOf(above), bi = layers.IndexOf(below);
        var dielectrics = new List<StackupLayer>();
        long h1Dbu = 0, h2Dbu = 0;
        for (int i = ai + 1; i < bi; i++)
        {
            var l = layers[i];
            if (l.Kind != StackupKind.Dielectric) continue;
            dielectrics.Add(l);
            if (i < si) h1Dbu += l.ThicknessDbu; else h2Dbu += l.ThicknessDbu;
        }
        if (h1Dbu <= 0 || h2Dbu <= 0)
            return (null, new SubstrateResolutionFailure(
                $"no positive-thickness dielectric between '{signal.Name}' and '{(h1Dbu <= 0 ? above.Name : below.Name)}' " +
                $"in technology '{technology.Name}'"), warn);

        long total = h1Dbu + h2Dbu;
        double er = dielectrics.Sum(l => l.Epsr * l.ThicknessDbu) / total;
        double tanD = dielectrics.Sum(l => l.TanD * l.ThicknessDbu) / total;
        if (!(er >= 1))
            return (null, new SubstrateResolutionFailure(
                $"the dielectric between '{above.Name}' and '{below.Name}' in technology '{technology.Name}' has a relative " +
                $"permittivity of {er:G4}; er is >= 1. Set it on the technology editor's Stackup tab"), warn);

        double erMin = dielectrics.Min(l => l.Epsr), erMax = dielectrics.Max(l => l.Epsr);
        if ((erMax - erMin) / er > StriplineEpsrSpreadWarning)
        {
            string each = string.Join(", ", dielectrics.Select(l => $"{l.Name} {l.Epsr:0.###}"));
            warn.Add($"the dielectric between '{above.Name}' and '{below.Name}' is not uniform ({each}): a stripline model is " +
                     $"homogeneous, so it uses the thickness-weighted er {er:0.###}, a spread of {(erMax - erMin) / er * 100:0} %");
        }

        double dielectricSigma = dielectrics.Select(l => l.SigmaSm).DefaultIfEmpty(0).Max();
        if (ClosedFormConductionWarning(dielectricSigma, dielectrics) is { } conduction) warn.Add(conduction);

        return (new ResolvedStripline(
            signal.Name, above.Name, below.Name,
            DbuToMeters(h1Dbu, FallbackDbuPerMicron), DbuToMeters(h2Dbu, FallbackDbuPerMicron),
            er, DbuToMeters(signal.ThicknessDbu, FallbackDbuPerMicron), signal.SigmaSm, tanD, dielectricSigma), null, warn);
    }

    private static StackupLayer? PlaneAbove(IReadOnlyList<StackupLayer> layers, int index)
    {
        for (int i = index - 1; i >= 0; i--)
            if (layers[i].Kind == StackupKind.Conductor && layers[i].IsGroundReference) return layers[i];
        return null;
    }

    private static StackupLayer? PlaneBelow(IReadOnlyList<StackupLayer> layers, int index)
    {
        for (int i = index + 1; i < layers.Count; i++)
            if (layers[i].Kind == StackupKind.Conductor && layers[i].IsGroundReference) return layers[i];
        return null;
    }
}
