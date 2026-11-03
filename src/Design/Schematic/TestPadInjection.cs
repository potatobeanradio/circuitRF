using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;

namespace CircuitRF.Design.Schematic;

/// <summary>
/// The test pad's side of the substrate seam (brief-dfr26-7 R-dfr26-7-4): what <see cref="ViaSubstrateInjection"/>
/// is to a via. At extraction a <c>TESTPAD</c> on a technology has the dielectric its pad faces, its via's length
/// and the planes the via passes resolved from the stackup (<see cref="SubstrateResolver.ResolveTestPad"/>) and
/// injected as plain SI overrides.
///
/// <para><b>What the user states</b> stays theirs: <c>D</c>, <c>Drill</c>, <c>ClearD</c>, <c>InnerPadD</c> and
/// <c>AntipadD</c> reach the engine as
/// typed, expressions and units included, so a swept diameter sweeps. The layer rows, <c>Feed</c>,
/// <c>FeedW</c>, <c>Centre</c> and <c>PortNum</c> are resolution or artwork inputs and never reach the
/// <c>.cnl</c>; an empty row never does either (<c>Key=</c> is the empty-value trap).</para>
/// </summary>
public static class TestPadInjection
{
    public const string PadLayerParam = "PadLayer";
    public const string ViaFromParam = "ViaFrom";
    public const string ClearLayersParam = "ClearLayers";

    /// <summary>The one kind this seam injects (the extractor's own test, named so <see cref="TechnologyBound"/> can
    /// take the union of every seam's).</summary>
    public static bool IsTestPadKind(SymbolKind kind) => kind == SymbolKind.TestPad;

    /// <summary>The rows the engine reads as typed — every other TESTPAD row is consumed here.</summary>
    public static readonly IReadOnlySet<string> EngineParams = new HashSet<string>(StringComparer.Ordinal) { "D", "Drill", "ClearD", "InnerPadD", "AntipadD" };

    /// <summary>The overrides to append, the sentences to post, and the resolved stack for a readout.</summary>
    public sealed record Injection(IReadOnlyList<ParameterAssignment> Overrides, IReadOnlyList<string> Messages, ResolvedTestPad? Stack);

    public static Injection Build(Technology? technology, IEnumerable<EditableParameter> parameters)
    {
        var list = parameters as IReadOnlyList<EditableParameter> ?? parameters.ToList();
        string? Text(string name) => list.FirstOrDefault(p => p.Name == name)?.Expression is { } e && e.Trim().Length > 0
            ? e.Trim() : null;

        bool viaFed = (Text("Feed") ?? TestPadPCell.ViaFeed).Equals(TestPadPCell.ViaFeed, StringComparison.OrdinalIgnoreCase);
        var clear = TestPadPCell.ParseClearLayers(Text(ClearLayersParam));
        var (stack, failure, warnings) = SubstrateResolver.ResolveTestPad(technology, Text(PadLayerParam), Text(ViaFromParam), viaFed, clear);
        var messages = new List<string>(warnings);
        var overrides = new List<ParameterAssignment>();
        void Add(string name, double value) => overrides.Add(new ParameterAssignment(name, value.ToString("R", CultureInfo.InvariantCulture)));

        if (stack is null)
        {
            messages.Add($"{failure?.Reason ?? "no stackup resolved"} — the test pad uses its standalone defaults " +
                         $"({ComponentModelFactory.DefaultSubstrateHMeters * 1e3:G4} mm of εr {ComponentModelFactory.DefaultSubstrateEpsR:G3} " +
                         "to one plane, and no via)");
            return new Injection(overrides, messages, null);
        }

        // The planes the pad faces, at most one a side. A pad facing none has no capacitance, which is worth saying.
        if (stack.Faces.Count == 0)
        {
            Add("Hpad", 0);
            messages.Add($"the pad on '{stack.Pad.Name}' faces no conductor on either side outside ClearLayers, so it is " +
                         "modelled with no capacitance");
        }
        for (int i = 0; i < stack.Faces.Count && i < 2; i++)
        {
            string suffix = i == 0 ? "" : "2";
            Add($"Hpad{suffix}", stack.Faces[i].HeightMeters);
            Add($"Erpad{suffix}", stack.Faces[i].EpsR);
            // The cleared plane in front of it (brief-dfr27-1): the pad faces it outside the ClearD hole.
            if (stack.Faces[i].Cut is { } cut)
            {
                Add($"Hcl{suffix}", cut.HeightMeters);
                Add($"Ercl{suffix}", cut.EpsR);
                Add($"Tcl{suffix}", cut.ThicknessMeters);
            }
        }
        // The cleared conductors by name, for the run's note when ClearD is too small to clear them.
        var clearNames = clear.Select(c => technology!.Stackup.Layers.FirstOrDefault(l =>
                                  l.Kind == StackupKind.Conductor && string.Equals(l.Name, c, StringComparison.OrdinalIgnoreCase))?.Name)
                              .OfType<string>().ToList();
        if (clearNames.Count > 0)
            overrides.Add(new ParameterAssignment("ClearNames", "\"" + string.Join(", ", clearNames) + "\""));

        if (stack.Via is { } via)
        {
            Add("Hvia", via.LengthMeters);
            Add("ErVia", via.MaxRelativePermittivity);
            if (Text("Drill") is null)
            {
                if (via.DrillMeters is { } dr) Add("Drill", dr);
                else messages.Add($"A default is used where the technology states none: Drill = {ComponentModelFactory.DefaultViaDrillMeters * 1e3:0.###} mm. " +
                                  "Set it on the component to use your own.");
            }
            // The technology's via antipad, unless the component states one. Every uncleared plane's barrel clearance
            // and every unused pad's antipad is AntipadD, so the model reads it from the one row.
            if (Text("AntipadD") is null)
                Add("AntipadD", (via.PadMeters ?? ComponentModelFactory.DefaultViaPadMeters) + ComponentModelFactory.DefaultViaAntipadRingMeters);
            if (stack.Planes.Count > 0)
            {
                Add("Planes", stack.Planes.Count);
                for (int i = 0; i < stack.Planes.Count; i++)
                {
                    var p = stack.Planes[i];
                    Add($"Zp{i + 1}", p.PositionMeters);
                    Add($"Tp{i + 1}", p.LengthMeters);
                    Add($"Erp{i + 1}", p.EpsR);
                    if (p.Cleared) Add($"Clp{i + 1}", 1);
                }
            }
            // The unused pads (brief-dfr26-8), only when the component says it has them: where each sits and what is
            // next to it. Their size stays the user's InnerPadD, so a swept diameter sweeps.
            if (Text("InnerPadD") is not null && stack.InnerPadSlots.Count > 0)
            {
                Add("InnerPads", stack.InnerPadSlots.Count);
                for (int i = 0; i < stack.InnerPadSlots.Count; i++)
                {
                    var q = stack.InnerPadSlots[i];
                    Add($"Zq{i + 1}", q.PositionMeters);
                    Add($"Tq{i + 1}", q.ThicknessMeters);
                    Add($"Erq{i + 1}", q.EpsR);
                    Add($"Hqa{i + 1}", q.GapAMeters);
                    Add($"Kqa{i + 1}", (int)q.SideA);
                    Add($"Hqb{i + 1}", q.GapBMeters);
                    Add($"Kqb{i + 1}", (int)q.SideB);
                }
            }
        }
        return new Injection(overrides, messages, stack);
    }
}
