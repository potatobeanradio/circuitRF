using System.Globalization;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Systems;
using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Synthesis.Filters;

/// <summary>
/// A lumped LC ladder as an MLAD of sites alone (docs/design/filter-synthesis.md §1): each arm of
/// <see cref="LumpedLadder"/> a series or shunt site of its part's value, a second part in series another series site,
/// and a second part in parallel a <c>type=parallel</c> site on the same pins. The copper is the sites' pads; the parts
/// are ordinary ones placed on them. A lowpass starts on a shunt C and a highpass on a series C, the fewer inductors; a
/// ladder with finite zeros starts shunt, so its resonators are tanks in the line rather than traps to ground, which a
/// site cannot hold (one part from a pin to ground), and so neither can a bandstop's.
/// </summary>
public sealed class LumpedLadderRealiser : IFilterRealiser
{
    public string Name => "Lumped LC ladder (MLAD sites)";

    public bool Supports(FilterSpec spec, out string? why)
    {
        why = Try(spec).Why?.Render();
        return why is null;
    }

    public FilterRealisation Realise(FilterSpec spec, MicrostripSubstrate? substrate)
    {
        var (ladder, why) = Try(spec);
        if (why is not null) throw new ParameterRefusalException(why);

        var sections = new List<LadderSection>();
        var sites = new List<SiteValue>();
        foreach (var arm in ladder!.Arms)
            for (int i = 0; i < arm.Parts.Count; i++)
            {
                var (role, value) = (arm.Parts[i].Role, arm.Parts[i].Value);
                bool parallel = i > 0 && arm.Parallel;
                int a = LadderPlan.Grounds(sections) + 1;
                sites.Add(parallel ? sites[^1] with { Role = role.ToString(), Value = value }
                    : new SiteValue(role.ToString(), $"G{a}", arm.Shunt ? null : $"G{a + 1}", value));
                sections.Add(new LadderSite(role, value, arm.Shunt ? PlanSiteKind.Shunt : PlanSiteKind.Series,
                    Parallel: parallel));
            }

        var parameters = new Dictionary<string, string>
        {
            ["Sections"] = LadderPlan.Format(sections),
            ["Z0"] = string.Create(CultureInfo.InvariantCulture, $"{spec.Z0} Ohm"),
            ["F"] = string.Create(CultureInfo.InvariantCulture, $"{spec.CentreHz / 1e9:0.#########} GHz"),
        };
        List<string> notes =
        [
            $"{(ladder.Arms[0].Shunt ? "Shunt" : "Series")}-first, {sites.Count} parts at their ideal values; the sites' "
            + "pads are not in them and lower a 1 GHz cutoff about 5 % on a 20 mil board (docs/design/rf-passives.md §3.16).",
        ];
        if (Math.Abs(ladder.LoadOhms - spec.Z0) > 1e-6 * spec.Z0)
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"The ladder is designed into {ladder.LoadOhms:0.###} Ω at pin 2 (g(N+1) ≠ 1); into {spec.Z0} Ω its passband ripple grows."));
        return new FilterRealisation(ComponentModelFactory.LadderTypeName, parameters, sites, notes);
    }

    private static (LumpedLadder? Ladder, Diagnostic? Why) Try(FilterSpec spec)
    {
        if (spec.Band == FilterBand.Bandstop)
            return (null, Diagnostic.Create("synthesis.proto.bandstop", DiagnosticSeverity.Error,
                "Lumped ladder: a bandstop needs LC traps to ground, and a site holds one part from a pin to ground."));
        try
        {
            bool zeros = spec.Response is FilterResponse.Elliptic or FilterResponse.InvChebyshev;
            return (LumpedLadder.For(spec, seriesFirst: spec.Band == FilterBand.Highpass && !zeros), null);
        }
        catch (ParameterRefusalException e) when (e.Diagnostic is not null)
        {
            return (null, e.Diagnostic);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return (null, Diagnostic.Create("synthesis.proto.spec", DiagnosticSeverity.Error, "Lumped ladder: {why}",
                ("why", e.Message)));
        }
    }
}
