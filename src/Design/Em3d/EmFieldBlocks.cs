// brief-cosim-pattern-2 R-cp2-6 / R-cp2-7 — what a schematic's EM Field Data blocks offer around a run: the paths a Fields analysis can
// name, including those inside sub-cells, and the run's one note when a block's setup has moved on since its result was made.

using System.Collections.Concurrent;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Em3d;

/// <summary>What a Fields analysis can ask of one block's record: the frequencies it stores, and whether it holds a far field and a field set.</summary>
public sealed record EmFieldBlockData(string Path, IReadOnlyList<double> StoredHz, bool HasFarField, bool HasFieldSet);

/// <summary>R-cp6-5 — an element directly joining the nodes of two gap ports of one block.</summary>
public sealed record GapPortBridge(string Part, string Block, string PortA, string PortB);

/// <summary>One setting of a result, with the setup's value now where they can be compared.</summary>
public sealed record EmRunSetting(string Field, string? Recorded, string? Now, bool Differs);

public static class EmFieldBlocks
{
    /// <summary>Every EM Field Data block under a test bench, as the elaborated path <c>block=</c> takes: <c>EM1</c>, <c>X1.EM1</c>.</summary>
    public static IReadOnlyList<string> Paths(Library lib, TestBench tb)
    {
        var found = new List<string>();
        void Walk(IEnumerable<Instance> instances, string prefix, HashSet<string> visiting)
        {
            foreach (var inst in instances)
            {
                string path = prefix + inst.InstanceName;
                if (inst.Reference.Equals(EmFieldDataRecord.TypeName, StringComparison.OrdinalIgnoreCase)) found.Add(path);
                else if (lib.Find(inst.Reference) is { } cell && visiting.Add(cell.Name))
                {
                    Walk(cell.Instances, path + ".", visiting);
                    visiting.Remove(cell.Name);
                }
            }
        }
        Walk(tb.Instances, "", []);
        return found;
    }

    /// <summary>The blocks of a schematic as it stands in the editor, extracted as Simulate extracts it. A schematic that does not extract
    /// lists none: the control still takes typed text.</summary>
    public static IReadOnlyList<string> Paths(SchematicEditModel model)
    {
        try
        {
            var (lib, tb) = SchematicCircuit.FromSchematic(model, "fields", model.SchematicDirectory);
            return Paths(lib, tb);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return []; }
    }

    /// <summary>R-cp6-4 — what each block of a schematic's record holds, read from the record alone, for the analysis editor. A block whose record
    /// cannot be read is left out, and so is every block of a schematic that does not extract: the controls still take typed text.</summary>
    public static IReadOnlyList<EmFieldBlockData> Data(SchematicEditModel model)
    {
        try
        {
            // A block's File is relative to what a run reads it from, the owning workspace, and not to the schematic's own folder.
            string? root = SchematicCircuit.ReferenceBaseOfDirectory(model.SchematicDirectory);
            var (lib, tb) = SchematicCircuit.FromSchematic(model, "fields", root);
            var found = new List<EmFieldBlockData>();
            void Walk(IEnumerable<Instance> instances, string prefix, HashSet<string> visiting)
            {
                foreach (var inst in instances)
                {
                    string path = prefix + inst.InstanceName;
                    if (inst.Reference.Equals(EmFieldDataRecord.TypeName, StringComparison.OrdinalIgnoreCase))
                    {
                        string? file = inst.Overrides.FirstOrDefault(o => o.Name.Equals("File", StringComparison.OrdinalIgnoreCase))?.Expression.Trim('"');
                        if (file is { Length: > 0 } && EmResultRecord.Load(root is null ? file : Path.Combine(root, file)).Record is { } r)
                            found.Add(new EmFieldBlockData(path, [.. r.Frequencies.FarFieldHz.Union(r.Frequencies.FieldSetHz).Order()],
                                                           r.Frequencies.FarFieldHz.Count > 0, r.Frequencies.FieldSetHz.Count > 0));
                    }
                    else if (lib.Find(inst.Reference) is { } cell && visiting.Add(cell.Name))
                    {
                        Walk(cell.Instances, path + ".", visiting);
                        visiting.Remove(cell.Name);
                    }
                }
            }
            Walk(tb.Instances, "", []);
            return found;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return []; }
    }

    /// <summary>R-cp6-5 — every two-terminal element between the nodes of two gap ports of an EM Field Data block, by the record's port kinds. A part
    /// of several terminals is not asked about: an amplifier's signal pins stand on gap ports' nodes by design. A record with no port kinds gives nothing.</summary>
    public static IReadOnlyList<GapPortBridge> GapPortBridges(ElaboratedNetlist nl)
    {
        var found = new List<GapPortBridge>();
        foreach (var block in nl.Components)
        {
            if (block.Model is not CircuitRF.Core.Devices.SnpModel { RecordPath: { } path } || EmResultRecord.Load(path).Record is not { } record) continue;
            var ports = record.Ports.OrderBy(p => p.Number).ToList();
            var gaps = new Dictionary<int, string>();
            for (int k = 0; k < ports.Count && k < block.Nodes.Length; k++)
                if (ports[k].Kind == EmResultPort.Gap && block.Nodes[k] > 0) gaps.TryAdd(block.Nodes[k], ports[k].Name);
            foreach (var part in nl.Components)
                if (!ReferenceEquals(part, block) && part.Nodes is [var a, var b] && a != b && gaps.TryGetValue(a, out var portA) && gaps.TryGetValue(b, out var portB))
                    found.Add(new GapPortBridge(part.InstancePath, block.InstancePath, portA, portB));
        }
        return found;
    }

    /// <summary>
    /// R-cp2-6 — every setting of the record's setup beside the setup's value now, differences first. <c>WhyNot</c> says why nothing is
    /// compared: <see cref="EmResultRecord.SettingsNotRecorded"/>, or a setup that can no longer be read, whose recorded values still show.
    /// </summary>
    public static (IReadOnlyList<EmRunSetting> Settings, string? WhyNot) SettingsOf(EmResultRecord record, string recordPath)
    {
        if (record.Setup is not { } setup) return ([], EmResultRecord.SettingsNotRecorded);
        var then = EmResultRecord.Settings(setup);
        var (differences, whyNot) = record.DifferencesFromCurrent(recordPath);
        if (whyNot is not null) return ([.. then.Select(s => new EmRunSetting(s.Field, s.Value, null, false))], whyNot);
        var changed = differences.ToDictionary(d => d.Field, StringComparer.Ordinal);
        var same = then.Where(s => !changed.ContainsKey(s.Field)).Select(s => new EmRunSetting(s.Field, s.Value, s.Value, false));
        return ([.. differences.Select(d => new EmRunSetting(d.Field, d.Then, d.Now, true)), .. same], null);
    }

    private sealed record KeptNote(long RecordTicks, long RecordLength, string? SetupPath, long SetupTicks, long SetupLength, int Differing);

    private static readonly ConcurrentDictionary<string, KeptNote> _kept = new(StringComparer.Ordinal);

    /// <summary>
    /// R-cp2-6 — one note per block whose setup differs from what its result used. Running against older data is the point of the design,
    /// so this is never a warning and never stops anything. Asked once per evaluation, so an optimizer's thousandth asks only for two stats.
    /// </summary>
    public static void NoteSetupChanges(ElaboratedNetlist nl)
    {
        foreach (var c in nl.Components)
        {
            if (c.Model is not SnpModel { RecordPath: { } recordPath }) continue;
            int differing = DifferingSettings(recordPath);
            if (differing > 0)
                nl.AddNoteOnce($"em-field-data-setup:{c.InstancePath}",
                    $"{EmFieldDataRecord.Describe(c.InstancePath)}: its setup has changed since '{Path.GetFileName(recordPath)}' was made " +
                    $"({differing} setting{(differing == 1 ? "" : "s")} differ). This run uses the saved result; Run settings… shows the differences.");
        }
    }

    private static int DifferingSettings(string recordPath)
    {
        var info = new FileInfo(recordPath);
        if (_kept.TryGetValue(recordPath, out var kept) && kept.RecordTicks == info.LastWriteTimeUtc.Ticks && kept.RecordLength == info.Length && SetupStat(kept))
            return kept.Differing;
        var (record, _) = EmResultRecord.Load(recordPath);
        if (record is null) return 0;
        int differing = record.DifferencesFromCurrent(recordPath).Differences.Count;
        string? setupPath = record.SetupSource is { } source ? EmResultRecord.Resolve(recordPath, source.File) : null;
        var setup = setupPath is null ? null : new FileInfo(setupPath);
        _kept[recordPath] = new KeptNote(info.LastWriteTimeUtc.Ticks, info.Length, setupPath,
                                         setup is { Exists: true } ? setup.LastWriteTimeUtc.Ticks : 0, setup is { Exists: true } ? setup.Length : -1, differing);
        return differing;
    }

    private static bool SetupStat(KeptNote kept)
    {
        if (kept.SetupPath is null) return true;
        var setup = new FileInfo(kept.SetupPath);
        return setup.Exists ? setup.LastWriteTimeUtc.Ticks == kept.SetupTicks && setup.Length == kept.SetupLength : kept.SetupLength == -1;
    }
}
