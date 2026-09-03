using System.Text;

namespace CircuitRF.Ui.Schematic;

// ──────────────────────────────────────────────────────────────────────────────
//  SL3 — comparing the recorded interface hash against the cell as it is now, and
//  saying what that means for THIS document.
//
//  brief-shared-library-3-interface-change.md. This is a REPORT, never a refusal
//  and never an automatic repair (R-sl3-1): the librarian's new symbol is the
//  truth and must render; auto-rewiring is symbol-editor.md §6's deferred Option
//  B and stays deferred. What was missing is only that the user is not told.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One CELL whose published interface no longer matches what the instances referencing it were
/// placed against — one per cell, never one per instance (R-sl3-9: forty instances of one changed
/// cell is one problem).
/// </summary>
/// <param name="CellRef">The reference as stored, so a repair can be pointed at it.</param>
/// <param name="CellName">The cell's own last path segment — what the user calls it.</param>
/// <param name="SourceAlias">The workspace alias, when the reference is a <c>ws://</c> one.</param>
/// <param name="InstanceNames">Every affected instance in this document, in document order.</param>
/// <param name="UnconnectedPorts">
/// <c>Instance.port</c> for every port of an affected instance that nothing currently meets — the
/// ELECTRICAL consequence, and the reason the feature exists (R-sl3-8). Empty for a layout, whose
/// connectivity is not positional in this sense.
/// </param>
/// <param name="NoLongerDeclared">Parameter names the instances carry that the cell no longer declares.</param>
/// <param name="NewlyDeclared">Parameter names the cell declares that the instances do not carry.</param>
/// <param name="PinCount">Pins the cell publishes NOW.</param>
/// <param name="PortCount">The symbol's <c>PortCount</c> NOW.</param>
/// <param name="ParameterCount">Declared parameters NOW.</param>
public sealed record CellInterfaceChange(
    string                CellRef,
    string                CellName,
    string?               SourceAlias,
    IReadOnlyList<string> InstanceNames,
    IReadOnlyList<string> UnconnectedPorts,
    IReadOnlyList<string> NoLongerDeclared,
    IReadOnlyList<string> NewlyDeclared,
    int                   PinCount,
    int                   PortCount,
    int                   ParameterCount)
{
    /// <summary>The Messages line — one per affected cell, on open.</summary>
    public string Message
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append('"').Append(CellName).Append('"');
            if (SourceAlias is { Length: > 0 } a) sb.Append(" (from [").Append(a).Append("])");
            sb.Append(" has changed since it was placed here: it now publishes ")
              .Append(Plural(PinCount, "pin"));
            if (PortCount != PinCount) sb.Append(" over ").Append(Plural(PortCount, "port"));
            sb.Append(" and ").Append(Plural(ParameterCount, "declared parameter")).Append(". ");

            sb.Append(InstanceNames.Count == 1 ? "1 instance is affected: " : $"{InstanceNames.Count} instances are affected: ")
              .Append(string.Join(", ", InstanceNames)).Append('.');

            if (UnconnectedPorts.Count > 0)
                sb.Append(" Not connected now: ").Append(string.Join(", ", UnconnectedPorts)).Append('.');
            if (NoLongerDeclared.Count > 0)
                sb.Append(" No longer declared by the cell: ").Append(string.Join(", ", NoLongerDeclared)).Append('.');
            if (NewlyDeclared.Count > 0)
                sb.Append(" Newly declared: ").Append(string.Join(", ", NewlyDeclared)).Append('.');

            sb.Append(" The drawing is correct — check the connections, then Accept the new interface "
                    + "from the instance's properties.");
            return sb.ToString();
        }
    }

    /// <summary>The one-line notice the Properties inspector shows for one affected instance.</summary>
    public string InstanceNotice =>
        $"\"{CellName}\" has changed since this instance was placed — it now publishes "
      + $"{Plural(PinCount, "pin")} and {Plural(ParameterCount, "declared parameter")}. "
      + "Check the connections; Accept the new interface when the design is right.";

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}

/// <summary>
/// Compares each instance's recorded interface hash against the cell as it is now, and groups what
/// differs into one report per cell.
///
/// <para><b>Not on the render path.</b> Computing a hash reads the cell's <c>.ccell</c> from disk and
/// <c>CellSymbolResolver.ResolveCcell</c> is deliberately uncached, so this runs at document open and
/// on an explicit re-check — never per frame. What the renderer reads is the boolean this leaves
/// behind (<see cref="EditableComponent.InterfaceChanged"/>).</para>
///
/// <para><b>Applies to every cell reference, not only <c>ws://</c> ones</b> (R-sl3-11). The same
/// failure exists for a cell in your own workspace, with a smaller blast radius; conditioning the
/// check on the reference form would make it fire only sometimes, which is a rule nobody learns.</para>
/// </summary>
public static class CellInterfaceWatch
{
    /// <summary>
    /// How many times the last <see cref="Scan(SchematicEditModel, string?)"/> or
    /// <see cref="Scan(LayoutView, string, string?)"/> computed a hash — the cost of the check,
    /// counted rather than timed, so a regression shows up as a number instead of as a machine
    /// measurement. One per instance carrying a recorded hash, and zero for a document that carries
    /// none (which is every document written before SL3).
    /// </summary>
    public static int LastScanHashCount { get; private set; }

    // ── Schematic ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans <paramref name="model"/>, sets <see cref="EditableComponent.InterfaceChanged"/> on every
    /// affected component, and returns one report per affected CELL.
    ///
    /// <para>Reads nothing and reports nothing for a component whose recorded hash is absent
    /// (R-sl3-5), or whose cell does not currently resolve to a usable symbol — that is §4.2's own
    /// NotFound/PrimaryMissing, already reported, and must not become a second report saying
    /// something different about the same fact.</para>
    /// </summary>
    public static IReadOnlyList<CellInterfaceChange> Scan(SchematicEditModel model, string? workspaceRoot = null)
    {
        LastScanHashCount = 0;
        string? baseDir = model.SchematicDirectory;

        // cellRef → the affected components referencing it, in document order.
        var affected = new Dictionary<string, List<EditableComponent>>(StringComparer.Ordinal);
        var order    = new List<string>();

        foreach (var comp in model.Components)
        {
            comp.InterfaceChanged = false;
            if (comp.CellRef is not { Length: > 0 } cellRef) continue;
            if (comp.CellInterfaceHash is not { Length: > 0 } recorded) continue;

            LastScanHashCount++;
            if (CellInterfaceHash.For(cellRef, baseDir, workspaceRoot) is not { } now) continue;
            if (string.Equals(now, recorded, StringComparison.Ordinal)) continue;

            comp.InterfaceChanged = true;
            if (!affected.TryGetValue(cellRef, out var list))
            {
                affected[cellRef] = list = [];
                order.Add(cellRef);
            }
            list.Add(comp);
        }

        if (order.Count == 0) return [];

        // The connectivity pass is already the thing that decides what is connected — asked once,
        // here, rather than reimplemented (R-sl3-8: the electrical consequence is the point of the
        // feature). Built only when something actually changed, so an unaffected open pays nothing.
        var (render, _) = model.BuildRenderModel();
        var portsById = render.Components.ToDictionary(c => c.Id, c => c.Ports, StringComparer.Ordinal);

        var results = new List<CellInterfaceChange>(order.Count);
        foreach (string cellRef in order)
        {
            var comps = affected[cellRef];
            var res   = CellSymbolResolver.Resolve(cellRef, baseDir, workspaceRoot);
            var ccell = CellSymbolResolver.ResolveCcell(cellRef, baseDir ?? "", workspaceRoot);

            var unconnected = new List<string>();
            foreach (var c in comps)
            {
                if (!portsById.TryGetValue(c.Id, out var ports)) continue;
                foreach (var p in ports)
                    if (p.State == PortConnectionState.Unconnected)
                        unconnected.Add($"{NameOf(c)}.{p.Name}");
            }

            var declared = (ccell?.Parameters ?? [])
                .Select(p => p.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();
            var carried = comps
                .SelectMany(c => c.Parameters.Select(p => p.Name))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            results.Add(new CellInterfaceChange(
                CellRef:          cellRef,
                CellName:         CellNameOf(cellRef),
                SourceAlias:      ExternalCellRef.TryParse(cellRef, out string alias, out _) ? alias : null,
                InstanceNames:    comps.Select(NameOf).ToList(),
                UnconnectedPorts: unconnected,
                NoLongerDeclared: carried.Where(n => !declared.Contains(n, StringComparer.Ordinal)).ToList(),
                NewlyDeclared:    declared.Where(n => !carried.Contains(n, StringComparer.Ordinal)).ToList(),
                PinCount:         res.Symbol?.Pins.Count ?? 0,
                PortCount:        res.Symbol?.PortCount  ?? 0,
                ParameterCount:   declared.Count));
        }
        return results;
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The same comparison for a layout's instances. There is no per-instance flag to leave behind —
    /// a <see cref="LayoutInstance"/> is a persisted model with no runtime state — so the caller keeps
    /// the returned set and the renderer marks by <c>CellRef</c>.
    ///
    /// <para><b>The pins half of the interface is the symbol's, and a layout instance does not draw
    /// through it</b> — its own connection points come from the cell's <c>.clay</c>. It is compared
    /// anyway, because R-sl3-4 records the hash on a <c>LayoutInstance</c> and a hash that is recorded
    /// and never checked is worse than no hash at all. What is genuinely load-bearing here is the
    /// declared-parameter half, which a placed PCell instance depends on directly.</para>
    /// </summary>
    public static IReadOnlyList<CellInterfaceChange> Scan(LayoutView view, string baseDir, string? workspaceRoot = null)
    {
        LastScanHashCount = 0;

        var affected = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var order    = new List<string>();

        for (int i = 0; i < view.Instances.Count; i++)
        {
            var inst = view.Instances[i];
            if (inst.CellRef is not { Length: > 0 } cellRef) continue;
            if (inst.CellInterfaceHash is not { Length: > 0 } recorded) continue;

            LastScanHashCount++;
            if (CellInterfaceHash.For(cellRef, baseDir, workspaceRoot) is not { } now) continue;
            if (string.Equals(now, recorded, StringComparison.Ordinal)) continue;

            if (!affected.TryGetValue(cellRef, out var list))
            {
                affected[cellRef] = list = [];
                order.Add(cellRef);
            }
            list.Add(i);
        }

        var results = new List<CellInterfaceChange>(order.Count);
        foreach (string cellRef in order)
        {
            var res      = CellSymbolResolver.Resolve(cellRef, baseDir, workspaceRoot);
            var ccell    = CellSymbolResolver.ResolveCcell(cellRef, baseDir, workspaceRoot);
            var declared = (ccell?.Parameters ?? []).Select(p => p.Name)
                             .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

            results.Add(new CellInterfaceChange(
                CellRef:          cellRef,
                CellName:         CellNameOf(cellRef),
                SourceAlias:      ExternalCellRef.TryParse(cellRef, out string alias, out _) ? alias : null,
                InstanceNames:    affected[cellRef].Select(i => $"instance #{i + 1}").ToList(),
                UnconnectedPorts: [],
                NoLongerDeclared: [],
                NewlyDeclared:    [],
                PinCount:         res.Symbol?.Pins.Count ?? 0,
                PortCount:        res.Symbol?.PortCount  ?? 0,
                ParameterCount:   declared.Count));
        }
        return results;
    }

    // ── Accepting the change (R-sl3-10) ───────────────────────────────────────

    /// <summary>
    /// Rewrites the recorded hash for <paramref name="components"/> to the cell's interface as it is
    /// now, and clears the mark. Returns how many were rewritten.
    ///
    /// <para><b>This is the ONLY thing that rewrites a recorded hash, and it is never automatic</b>
    /// (R-sl3-10). It must not happen on open, on save, or as a side effect of any edit: the recorded
    /// hash is the only evidence that the design was authored against a different interface, and a
    /// product that erases that evidence on open has implemented nothing.</para>
    /// </summary>
    public static int Accept(IEnumerable<EditableComponent> components, string? baseDir, string? workspaceRoot = null)
    {
        int n = 0;
        foreach (var comp in components)
        {
            if (comp.CellRef is not { Length: > 0 } cellRef) continue;
            if (CellInterfaceHash.For(cellRef, baseDir, workspaceRoot) is not { } now) continue;
            if (string.Equals(comp.CellInterfaceHash, now, StringComparison.Ordinal) && !comp.InterfaceChanged) continue;
            comp.CellInterfaceHash = now;
            comp.InterfaceChanged  = false;
            n++;
        }
        return n;
    }

    /// <summary>Every component in <paramref name="model"/> referencing <paramref name="cellRef"/> —
    /// what "accept for every instance of this cell in this document" enumerates.</summary>
    public static IEnumerable<EditableComponent> InstancesOf(SchematicEditModel model, string cellRef) =>
        model.Components.Where(c => string.Equals(c.CellRef, cellRef, StringComparison.Ordinal));

    /// <summary>The layout half of <see cref="Accept(IEnumerable{EditableComponent}, string?, string?)"/>.</summary>
    public static int Accept(IEnumerable<LayoutInstance> instances, string baseDir, string? workspaceRoot = null)
    {
        int n = 0;
        foreach (var inst in instances)
        {
            if (inst.CellRef is not { Length: > 0 } cellRef) continue;
            if (CellInterfaceHash.For(cellRef, baseDir, workspaceRoot) is not { } now) continue;
            if (string.Equals(inst.CellInterfaceHash, now, StringComparison.Ordinal)) continue;
            inst.CellInterfaceHash = now;
            n++;
        }
        return n;
    }

    // ── Naming ────────────────────────────────────────────────────────────────

    private static string NameOf(EditableComponent c) =>
        string.IsNullOrWhiteSpace(c.InstanceName) ? "(unnamed)" : c.InstanceName;

    /// <summary>The cell's own name — a <c>CellRef</c>'s last path segment, for every one of its four
    /// forms (§4's taxonomy), since all four end in the thing being named.</summary>
    internal static string CellNameOf(string cellRef)
    {
        string trimmed = cellRef.Replace('\\', '/').TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        string name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        return name.Length == 0 ? cellRef : name;
    }
}
