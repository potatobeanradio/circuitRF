using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Matching;

/// <summary>Why a Probe button is disabled (match.md §10.4 / MN-4 §5).</summary>
public enum MatchProbeBlock
{
    /// <summary>Nothing is wrong: the probe can run.</summary>
    None,

    /// <summary>The Designer is not bound to a schematic at all.</summary>
    NoSchematic,

    /// <summary>The instance is not in the extracted testbench — it was skipped or is not placed.</summary>
    NotPlaced,

    /// <summary>The schematic is a cell definition, not a testbench.</summary>
    InsideCell,

    /// <summary>Extraction reported problems; the circuit is not in a state worth measuring.</summary>
    SchematicErrors,

    /// <summary>
    /// The pin's net resolved to ground. A pin wired to ground reads this way, and so does one the
    /// extractor could give no net at all — the two are not distinguishable from a testbench, and
    /// they mean the same thing here: there is no external network on that pin.
    /// </summary>
    PinUnconnected,

    /// <summary>The pin's net carries nothing but the <c>Match</c> itself.</summary>
    NetIsBare,
}

/// <summary>
/// Answers "can this pin be probed, and if not, which of match.md §10.4's reasons is it?" — from the
/// live schematic, with no engine run.
/// </summary>
/// <remarks>
/// <b>It answers from the extracted testbench, not from the canvas.</b> The extraction is the same one
/// a Run does, so the connectivity the button reasons about is the connectivity the probe will
/// measure. Asking the canvas directly would mean a second implementation of what a net is, and the
/// two would eventually disagree on exactly the cases this exists to catch.
///
/// <para><b>The bench it built is kept</b> rather than thrown away and re-extracted a moment later by
/// whatever runs the probe: on a hierarchical schematic an extraction reads every referenced cell off
/// disk, and doing that twice per click is the kind of cost nobody notices until the design is large.</para>
/// </remarks>
public sealed record MatchProbeAvailability(
    MatchProbeBlock Block,
    string Reason,
    TestBench? Bench,
    Library? Library,
    string InstanceName,
    int PinIndex)
{
    /// <summary>True when the probe may run.</summary>
    public bool CanProbe => Block == MatchProbeBlock.None;

    /// <summary>The net the probed pin sits on, when one resolved.</summary>
    public string Net { get; init; } = "";

    /// <summary>
    /// Evaluates one pin. <paramref name="pinIndex"/> is 0 for the Term1 side, 1 for the Term2 side.
    /// </summary>
    public static MatchProbeAvailability Evaluate(
        SchematicEditModel? model, ICellResolver? cells, string instanceName, int pinIndex)
    {
        if (model is null || string.IsNullOrEmpty(instanceName))
            return Blocked(MatchProbeBlock.NoSchematic,
                "This Match is not open in a schematic, so there is no circuit to look outward into.",
                instanceName, pinIndex);

        NetExtractor.ExtractionResult extraction;
        try
        {
            extraction = NetExtractor.Extract(model, "probe", cells);
        }
        catch (Exception ex)
        {
            return Blocked(MatchProbeBlock.SchematicErrors,
                $"This schematic could not be read: {ex.Message}", instanceName, pinIndex);
        }

        // A cell DEFINITION has Pins, and its ports are an interface rather than a circuit. There is
        // no external network to look at from inside one — what is outside is whatever the cell is
        // instantiated into, which is a different schematic and possibly several of them.
        if (extraction.CellPorts.Count > 0)
            return Blocked(MatchProbeBlock.InsideCell,
                "This Match is inside a cell definition rather than a testbench. A cell's pins are an "
                + "interface, not a circuit, so there is no external network to look at from here — "
                + "open the testbench that instantiates this cell and probe there.",
                instanceName, pinIndex);

        if (extraction.Conflicts.Count > 0)
            return Blocked(MatchProbeBlock.SchematicErrors,
                "This schematic has unresolved problems, so what the probe measured would not be the "
                + $"circuit you think you drew. First of {extraction.Conflicts.Count}: "
                + extraction.Conflicts[0],
                instanceName, pinIndex);

        var bench = extraction.TestBench;
        var match = bench.Instances.FirstOrDefault(
            i => string.Equals(i.InstanceName, instanceName, StringComparison.Ordinal));
        if (match is null)
            return Blocked(MatchProbeBlock.NotPlaced,
                $"'{instanceName}' did not survive extraction, so there is nothing to probe from.",
                instanceName, pinIndex);

        if (pinIndex < 0 || pinIndex >= match.NetBindings.Count)
            return Blocked(MatchProbeBlock.NotPlaced,
                $"'{instanceName}' has {match.NetBindings.Count} pins; pin {pinIndex + 1} is not one of them.",
                instanceName, pinIndex);

        string net = match.NetBindings[pinIndex];
        if (string.IsNullOrWhiteSpace(net) || net == "0")
            return Blocked(MatchProbeBlock.PinUnconnected,
                $"Pin {pinIndex + 1} of {instanceName} sits on ground — either wired there, or not "
                + "wired to anything, which reads the same way once the schematic is extracted. There "
                + "is no external network on that pin to measure.",
                instanceName, pinIndex) with { Net = net ?? "" };

        bool anythingElse = bench.Instances.Any(
            i => !ReferenceEquals(i, match) && i.NetBindings.Contains(net, StringComparer.Ordinal));
        if (!anythingElse)
            return Blocked(MatchProbeBlock.NetIsBare,
                $"Net '{net}' carries nothing but {instanceName} itself. With the Match removed — which "
                + "is what the probe does — that node is open, and an open circuit has no impedance to "
                + "fit.",
                instanceName, pinIndex) with { Net = net };

        return new MatchProbeAvailability(
            MatchProbeBlock.None,
            $"Look outward from pin {pinIndex + 1} into net '{net}', with {instanceName} removed, and "
            + "fill in this termination from what is there.",
            bench, extraction.Library, instanceName, pinIndex) { Net = net };
    }

    private static MatchProbeAvailability Blocked(
        MatchProbeBlock block, string reason, string instanceName, int pinIndex)
        => new(block, reason, null, null, instanceName, pinIndex);
}
