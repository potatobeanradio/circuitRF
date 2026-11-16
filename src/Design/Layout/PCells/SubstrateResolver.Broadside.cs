using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;

namespace CircuitRF.Design.Layout.PCells;

/// <summary>
/// What a broadside-coupled pair (BCLIN, MBCLIN) resolves to from a stackup: its two conductor layers
/// and every cross-section dimension the model takes, in SI.
/// </summary>
/// <param name="A">The upper strip's conductor.</param>
/// <param name="B">The lower strip's conductor (the next conductor below A).</param>
/// <param name="UpperGround">The ground-designated conductor above A — null for MBCLIN, which has none.</param>
/// <param name="LowerGround">The ground-designated conductor below B.</param>
/// <param name="Hs">Between A's bottom and B's top.</param>
/// <param name="Ha">Between A's top and the upper ground (zero without one).</param>
/// <param name="Hb">Between B's bottom and the lower ground.</param>
/// <param name="Hd">The dielectric's top over the lower ground (MBCLIN): B, A and every layer up to the
/// last dielectric above A.</param>
/// <param name="Ta">A's thickness.</param>
/// <param name="Tb">B's thickness.</param>
/// <param name="EpsR">Thickness-weighted εr of the dielectrics in the span (the model takes one).</param>
/// <param name="TanD">Thickness-weighted tan δ of the same.</param>
/// <param name="Sigma">The mean of the two strips' conductivities.</param>
public sealed record ResolvedBroadsidePair(
    StackupLayer A, StackupLayer B, StackupLayer? UpperGround, StackupLayer LowerGround,
    double Hs, double Ha, double Hb, double Hd, double Ta, double Tb, double EpsR, double TanD, double Sigma);

public static partial class SubstrateResolver
{
    /// <summary>
    /// Resolves a broadside pair's two strip layers and its cross-section (BCLIN when
    /// <paramref name="stripline"/>, MBCLIN otherwise). <paramref name="layerA"/>/<paramref name="layerB"/>
    /// are conductor NAMES or null for the default: B is always the next non-ground conductor below A,
    /// and A defaults to the first non-ground conductor that has such a B — with a ground-designated
    /// conductor above it too, for a stripline. Every stackup layer between two surfaces counts toward
    /// their spacing, each conductor slab at its full thickness (copper adds height). Never throws: a
    /// stackup that cannot hold the pair is a <see cref="SubstrateResolutionFailure"/> naming why.
    /// </summary>
    public static (ResolvedBroadsidePair? Pair, SubstrateResolutionFailure? Failure, IReadOnlyList<string> Warnings) ResolveBroadsidePair(
        Technology? technology, string? layerA, string? layerB, bool stripline)
    {
        var warn = new List<string>();
        string part = stripline ? "BCLIN" : "MBCLIN";
        if (technology is null)
            return (null, new SubstrateResolutionFailure("no technology resolved for this document"), warn);

        var layers = technology.Stackup.Layers;
        bool IsSignal(StackupLayer l) => l.Kind == StackupKind.Conductor && !l.IsGroundReference;
        StackupLayer? Named(string? name)
        {
            if (name is not { Length: > 0 }) return null;
            var found = layers.FirstOrDefault(l => l.Kind == StackupKind.Conductor &&
                                                   string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
            if (found is null)
                warn.Add($"no conductor named '{name}' in technology '{technology.Name}' — falling back to the default layer");
            return found;
        }
        StackupLayer? NextSignalBelow(int index)
        {
            for (int i = index + 1; i < layers.Count; i++)
                if (layers[i].Kind == StackupKind.Conductor)
                    return IsSignal(layers[i]) ? layers[i] : null;   // a plane in between: no broadside pair
            return null;
        }
        StackupLayer? GroundAbove(int index)
        {
            for (int i = index - 1; i >= 0; i--)
                if (layers[i].Kind == StackupKind.Conductor && layers[i].IsGroundReference) return layers[i];
            return null;
        }
        StackupLayer? GroundBelow(int index)
        {
            for (int i = index + 1; i < layers.Count; i++)
                if (layers[i].Kind == StackupKind.Conductor && layers[i].IsGroundReference) return layers[i];
            return null;
        }

        var a = Named(layerA);
        var b = Named(layerB);
        if (a is null)
        {
            if (b is not null)
            {
                // B named: A is the signal conductor directly above it.
                int ib = layers.IndexOf(b);
                for (int i = ib - 1; i >= 0 && a is null; i--)
                    if (layers[i].Kind == StackupKind.Conductor) a = IsSignal(layers[i]) ? layers[i] : null;
                if (a is null)
                    return (null, new SubstrateResolutionFailure(
                        $"{part}: there is no signal conductor directly above '{b.Name}' in technology '{technology.Name}'"), warn);
            }
            else
            {
                for (int i = 0; i < layers.Count && a is null; i++)
                {
                    var l = layers[i];
                    if (!IsSignal(l) || NextSignalBelow(i) is not { } below) continue;
                    if (GroundBelow(layers.IndexOf(below)) is null) continue;
                    if (stripline && GroundAbove(i) is null) continue;
                    a = l;
                }
                if (a is null)
                    return (null, new SubstrateResolutionFailure(stripline
                        ? $"technology '{technology.Name}' has no two adjacent signal conductors between two ground-designated conductors"
                        : $"technology '{technology.Name}' has no two adjacent signal conductors over a ground-designated conductor"), warn);
            }
        }
        int ia = layers.IndexOf(a);
        b ??= NextSignalBelow(ia);
        if (b is null)
            return (null, new SubstrateResolutionFailure(
                $"{part}: '{a.Name}' has no signal conductor directly below it in technology '{technology.Name}'"), warn);
        int ibb = layers.IndexOf(b);
        if (ibb <= ia)
            return (null, new SubstrateResolutionFailure($"{part}: layer B '{b.Name}' is not below layer A '{a.Name}'"), warn);

        var lower = GroundBelow(ibb);
        if (lower is null)
            return (null, new SubstrateResolutionFailure(
                $"{part}: technology '{technology.Name}' has no ground-designated conductor below '{b.Name}'"), warn);
        var upper = GroundAbove(ia);
        if (stripline && upper is null)
            return (null, new SubstrateResolutionFailure(
                $"BCLIN: technology '{technology.Name}' has no ground-designated conductor above '{a.Name}', so this is not a " +
                "stripline; MBCLIN is the broadside pair with one ground"), warn);
        if (!stripline && upper is not null)
            warn.Add($"MBCLIN: '{upper.Name}' is a ground above '{a.Name}' — this is a stripline, and BCLIN is the model for it");
        if (!stripline) upper = null;

        double M(long dbu) => DbuToMeters(dbu, FallbackDbuPerMicron);
        double Span(int from, int to)   // every layer strictly between two indices
        {
            long sum = 0;
            for (int i = from + 1; i < to; i++) sum += layers[i].ThicknessDbu;
            return M(sum);
        }
        for (int i = ia + 1; i < ibb; i++)
            if (layers[i].Kind == StackupKind.Conductor)
                warn.Add($"{part}: conductor '{layers[i].Name}' lies between the strips and is counted as dielectric thickness");

        int il = layers.IndexOf(lower);
        int iu = upper is null ? -1 : layers.IndexOf(upper);
        // MBCLIN: the dielectric reaches the last dielectric layer above A, if any.
        int topDielectric = ia;
        if (upper is null)
            for (int i = ia - 1; i >= 0 && layers[i].Kind != StackupKind.Conductor; i--)
                if (layers[i].Kind == StackupKind.Dielectric) topDielectric = i;

        long eDbu = 0;
        double eWeighted = 0, tWeighted = 0;
        int spanFrom = upper is null ? topDielectric - 1 : iu;
        for (int i = spanFrom + 1; i < il; i++)
        {
            var l = layers[i];
            if (l.Kind != StackupKind.Dielectric) continue;
            eDbu += l.ThicknessDbu;
            eWeighted += l.Epsr * l.ThicknessDbu;
            tWeighted += l.TanD * l.ThicknessDbu;
        }
        if (eDbu <= 0)
            return (null, new SubstrateResolutionFailure($"{part}: no dielectric between the grounds in technology '{technology.Name}'"), warn);
        double er = eWeighted / eDbu;
        if (!(er >= 1))
            return (null, new SubstrateResolutionFailure(
                $"{part}: the dielectric in technology '{technology.Name}' has a relative permittivity of {er:G4}; er is >= 1. " +
                "Set it on the technology editor's Stackup tab"), warn);
        var distinct = layers.Skip(spanFrom + 1).Take(il - spanFrom - 1)
                             .Where(l => l.Kind == StackupKind.Dielectric).Select(l => l.Epsr).Distinct().Count();
        if (distinct > 1)
            warn.Add($"{part}: the dielectrics between the grounds differ; their thickness-weighted εr {er:G4} is used");

        double hb = Span(ibb, il), hs = Span(ia, ibb), ta = M(a.ThicknessDbu), tb = M(b.ThicknessDbu);
        double ha = upper is null ? 0.0 : Span(iu, ia);
        double hd = hb + tb + hs + (topDielectric < ia ? ta + Span(topDielectric - 1, ia) : 0.0);
        double sigma = (a.SigmaSm + b.SigmaSm) / 2.0;
        return (new ResolvedBroadsidePair(a, b, upper, lower, hs, ha, hb, hd, ta, tb, er, tWeighted / eDbu, sigma), null, warn);
    }

    /// <summary>
    /// The stack a broadside coupler (MCLC with Broadside on) is built on: BCLIN's when the stackup has a ground above
    /// and below the pair, MBCLIN's otherwise, strip A on <paramref name="signalLayer"/> (null: the stackup's default
    /// pair). Null, with the reasons, when it has no second signal conductor next to the first.
    /// </summary>
    public static (BroadsideStack? Stack, string? Reason) ResolveBroadsideStack(Technology? technology, string? signalLayer)
    {
        var (pair, failure, _) = ResolveBroadsidePair(technology, signalLayer, null, stripline: true);
        bool stripline = pair is not null;
        if (pair is null) (pair, failure, _) = ResolveBroadsidePair(technology, signalLayer, null, stripline: false);
        if (pair is null) return (null, failure?.Reason ?? "no stackup resolved");
        double hd = !stripline && pair.Hd > pair.Hb + pair.Tb + pair.Hs * (1 + 1e-9) ? pair.Hd : double.NaN;
        return (new BroadsideStack(pair.Hs, pair.Hb, stripline ? pair.Ha : double.NaN, hd, pair.Ta, pair.Tb, pair.EpsR,
            pair.Sigma > 0 ? pair.Sigma : ComponentModelFactory.DefaultSubstrateSigmaSPerM, pair.TanD, stripline,
            pair.A.Name, pair.B.Name), null);
    }
}
