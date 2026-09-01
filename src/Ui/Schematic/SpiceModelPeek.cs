using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// One thing in a SPICE file a <see cref="SymbolKind.SpiceModel"/> instance can be pointed at.
/// </summary>
/// <param name="Name">What the file calls it — the card's or the subcircuit's own name.</param>
/// <param name="TypeLabel">How it is written: <c>.SUBCKT</c>, <c>.NMOS</c>, <c>.D</c>.</param>
/// <param name="IsSubcircuit">True for a <c>.subckt</c>, false for a <c>.model</c> card.</param>
/// <param name="PortNames">
/// The terminals, in the order an instance binds them — the subcircuit's own port names, or the
/// device's terminal letters. <b>Empty when <see cref="Refusal"/> is set</b>: a definition
/// circuitRF cannot build has no terminal order to promise.
/// </param>
/// <param name="DeviceSymbol">
/// The palette component a supported card draws as. Null for a subcircuit and for a refusal.
/// </param>
/// <param name="Detail">One line describing what would be built — shown in the parameter dialog.</param>
/// <param name="Refusal">Why this definition cannot be run, or null. Shown verbatim.</param>
/// <param name="IsTopLevel">
/// True when nothing else in the file calls this definition — the PART, as opposed to the pieces it
/// is built from. Always false for a <c>.model</c> card: a card in a file that also defines
/// subcircuits is there to support one of them.
/// </param>
/// <param name="Candidate">The scan entry this came from — carries the translation the extractor needs.</param>
public sealed record SpiceModelDefinition(
    string                Name,
    string                TypeLabel,
    bool                  IsSubcircuit,
    IReadOnlyList<string> PortNames,
    SymbolKind?           DeviceSymbol,
    string                Detail,
    string?               Refusal,
    bool                  IsTopLevel,
    SpiceCellCandidate    Candidate)
{
    public bool IsSupported => Refusal is null;

    /// <summary>What the Name combo shows for this definition — the name, then what it is.</summary>
    public string DisplayLabel => $"{Name}  ({TypeLabel})";
}

/// <summary>What one SPICE file holds, as a placed component needs to see it.</summary>
/// <param name="Definitions">Everything in it, supported or not, in the scan's own order.</param>
/// <param name="Scan">The scan behind it — the extractor reads the translated subcircuits from here.</param>
/// <param name="Error">Why nothing could be read at all. Null when the file was read.</param>
public sealed record SpiceModelFile(
    IReadOnlyList<SpiceModelDefinition>  Definitions,
    SpiceCellScan                        Scan,
    string?                              Error)
{
    public static readonly SpiceModelFile Empty =
        new([], new SpiceCellScan([], [], [], null), "no file");
}

/// <summary>
/// Reads a SPICE file on behalf of a PLACED <see cref="SymbolKind.SpiceModel"/> component, and
/// caches the answer by file mtime.
///
/// <para><b>Why a cache and not just a call.</b> Three separate paths ask this question, and two of
/// them ask it constantly: the symbol resolver runs on every schematic model rebuild, the parameter
/// dialog runs on every selection change, and the extractor runs once per simulate. Parsing a
/// vendor <c>.lib</c> on each of those is a re-read of the same bytes to get the same answer —
/// exactly what <see cref="CellSymbolResolver"/>'s own mtime cache exists to avoid.</para>
///
/// <para><b>It reads through <see cref="SpiceCellImport"/> and nothing else.</b> The import gesture
/// (Copy to Workspace as Cell…) and this one are the same question asked about the same file, so a
/// definition that imports as a cell and the same definition placed as a component must never
/// disagree about what it is — which they would, immediately, if this restated the classification
/// instead of reusing it.</para>
/// </summary>
public static class SpiceModelPeek
{
    private sealed record CacheKey(string Path);
    private sealed record CacheEntry(DateTime Mtime, long Length, SpiceModelFile File);

    private static readonly Dictionary<CacheKey, CacheEntry> _cache = new();
    private static readonly Lock _gate = new();

    /// <summary>Extensions the SpiceModel file picker offers first. Wider than the project tree's
    /// own list (<see cref="ModelCardCellBuilder.IsSpiceCellFile"/>) because here the user has
    /// already said what the file is by choosing it.</summary>
    public static string[] FileExtensions { get; } =
        [".model", ".mod", ".subckt", ".sub", ".ckt", ".sp", ".spi", ".cir", ".lib", ".inc", ".txt"];

    /// <summary>
    /// Reads <paramref name="absolutePath"/>, or returns a file with <see cref="SpiceModelFile.Error"/>
    /// set. <b>Never throws</b> — this runs inside a render-path resolve and inside a dialog refresh.
    /// </summary>
    public static SpiceModelFile Read(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return SpiceModelFile.Empty with { Error = "No model file is set." };

        DateTime mtime;
        long     length;
        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists)
                return SpiceModelFile.Empty with
                    { Error = $"'{Path.GetFileName(absolutePath)}' was not found at {absolutePath}." };
            mtime  = info.LastWriteTimeUtc;
            length = info.Length;
        }
        catch (Exception ex)
        {
            return SpiceModelFile.Empty with { Error = $"{absolutePath} could not be read: {ex.Message}" };
        }

        var key = new CacheKey(absolutePath);
        lock (_gate)
            if (_cache.TryGetValue(key, out var hit) && hit.Mtime == mtime && hit.Length == length)
                return hit.File;

        var built = Build(absolutePath);

        lock (_gate) _cache[key] = new CacheEntry(mtime, length, built);
        return built;
    }

    /// <summary>Drops every cached read — called when a workspace is left, as the symbol caches are.</summary>
    public static void InvalidateAll()
    {
        lock (_gate) _cache.Clear();
    }

    /// <summary>
    /// The definition an instance's <c>Name</c> parameter picks.
    ///
    /// <para><b>A blank name means the HIGHEST-LEVEL supported definition</b> (owner, 2026-09-01) —
    /// the one nothing else in the file calls. A vendor file is a part plus the pieces it is built
    /// from, and every one of those pieces is a definition too: resolving to the first would place
    /// an internal transistor where the user asked for the transistor's package. Falls back, in
    /// order, to any supported definition and then to the first of any kind, so the refusal that IS
    /// shown names something real rather than nothing at all.</para>
    ///
    /// <para>A name that matches nothing returns null — the caller reports it. Substituting the
    /// first definition would silently run a different part.</para>
    /// </summary>
    public static SpiceModelDefinition? Select(SpiceModelFile file, string? wantedName)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (!string.IsNullOrWhiteSpace(wantedName))
        {
            string want = wantedName.Trim();
            return file.Definitions.FirstOrDefault(
                d => d.Name.Equals(want, StringComparison.OrdinalIgnoreCase));
        }

        return file.Definitions.FirstOrDefault(d => d.IsSupported && d.IsTopLevel)
            ?? file.Definitions.FirstOrDefault(d => d.IsSupported)
            ?? file.Definitions.FirstOrDefault();
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static SpiceModelFile Build(string absolutePath)
    {
        SpiceCellScan scan;
        try { scan = SpiceCellImport.Scan(absolutePath); }
        catch (Exception ex)
        {
            return SpiceModelFile.Empty with
                { Error = $"{Path.GetFileName(absolutePath)} could not be read: {ex.Message}" };
        }

        if (scan.Error is { } err && scan.Candidates.Count == 0)
            return new SpiceModelFile([], scan, err);

        // Every subcircuit that some OTHER subcircuit calls, transitively. What is left is the
        // part(s) the file exists to publish. `Dependencies` is already the transitive, leaf-first
        // list the translator resolved, so nothing is walked twice here.
        var called = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in scan.Subcircuits)
            foreach (var dep in s.Dependencies)
                called.Add(dep);

        var defs = new List<SpiceModelDefinition>(scan.Candidates.Count);
        foreach (var c in scan.Candidates)
            defs.Add(Describe(c, called));

        return new SpiceModelFile(defs, scan, null);
    }

    private static SpiceModelDefinition Describe(SpiceCellCandidate c, HashSet<string> called)
    {
        if (c.Subcircuit is { } sub)
        {
            return new SpiceModelDefinition(
                c.Name, c.TypeLabel, IsSubcircuit: true,
                PortNames:    sub.IsSupported ? [.. sub.Definition.Ports] : [],
                DeviceSymbol: null,
                Detail:       c.Detail,
                Refusal:      sub.Refusal,
                IsTopLevel:   !called.Contains(sub.Name),
                Candidate:    c);
        }

        var card = c.Card;
        if (card?.Binding is not { } binding)
            return new SpiceModelDefinition(
                c.Name, c.TypeLabel, IsSubcircuit: false, PortNames: [], DeviceSymbol: null,
                Detail: c.Detail, Refusal: card?.Refusal ?? "This definition could not be read.",
                IsTopLevel: false, Candidate: c);

        // A card whose engine component has no schematic symbol cannot be DRAWN, which for a placed
        // component is the same as not being runnable — there would be no pins to wire it by. The
        // refusal names the component so the sentence is actionable rather than "unsupported".
        if (ModelCardCellBuilder.SymbolFor(binding.EngineReference) is not { } kind)
            return new SpiceModelDefinition(
                c.Name, c.TypeLabel, IsSubcircuit: false, PortNames: [], DeviceSymbol: null,
                Detail: c.Detail,
                Refusal: $"circuitRF implements '{c.Name}' as '{binding.EngineReference}', which has "
                       + "no schematic symbol — there would be no pins to wire it by.",
                IsTopLevel: false, Candidate: c);

        var terminals = SymbolPortDefs.For(kind).Select(t => t.Name).ToList();

        return new SpiceModelDefinition(
            c.Name, c.TypeLabel, IsSubcircuit: false,
            PortNames:    terminals,
            DeviceSymbol: kind,
            Detail:       $"{ComponentTypeRegistry.Get(kind).DisplayName} — {c.Detail}",
            Refusal:      null,
            // A card is never "the part" while a subcircuit is present, and when the file holds
            // only cards the top-level test would pick out all of them equally — so it decides
            // nothing either way and is left false rather than made to mean two things.
            IsTopLevel:   false,
            Candidate:    c);
    }
}
