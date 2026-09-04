using System.Text;

namespace CircuitRF.Ui.Schematic;

// ──────────────────────────────────────────────────────────────────────────────
//  TM2 — a cell this design references was MOVED by whoever owns it, the
//  reference still spells the old place, and the forwarding record is what found
//  it. brief-tree-move-2-moves-across-a-shared-library.md §5.
//
//  This is SL3's shape, deliberately: one report per affected CELL, three
//  surfaces that already exist, and one explicit gesture that adopts the change.
//  The difference is that SL3's fact is "the cell changed shape" and this one is
//  "the cell is somewhere else" — and this one is INFORMATIONAL, not a warning
//  (R-tm2-14), because everything about the design is currently correct.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One CELL that resolved only through a forwarding record — one per cell, never one per instance
/// (R-tm2-12: forty instances of one moved cell is one problem, which is SL3 R-sl3-9 verbatim).
/// </summary>
/// <param name="CellRef">The reference as stored — the thing "Update references" rewrites.</param>
/// <param name="CellName">The cell's own last path segment: what the user calls it.</param>
/// <param name="Redirect">The record that fired, and the folder it led to.</param>
/// <param name="InstanceNames">Every affected instance in this document, in document order.</param>
/// <param name="NewCellRef">
/// What the reference WOULD be after adoption, produced by the one rule that produces a cell
/// reference (<see cref="ExternalCellRef.MakeCellRef"/>) — so the sentence the user reads and the
/// string the gesture writes cannot drift apart. Null when the document has no directory to store a
/// relative reference against, which is an unsaved scratch schematic.
/// </param>
public sealed record MovedCellReport(
    string                CellRef,
    string                CellName,
    MoveRedirectHit       Redirect,
    IReadOnlyList<string> InstanceNames,
    string?               NewCellRef)
{
    /// <summary>The Messages line — one per affected cell, on open. <b>Posted at Info, never at
    /// Warning</b> (R-tm2-14): an expected, correct state that happens to be worth mentioning must
    /// not be coloured like a problem, or users learn to ignore the colour that also marks real
    /// breakage. <c>NotFound</c> stays the warning.</summary>
    public string Message
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append('"').Append(CellName).Append("\" moved to \"").Append(Redirect.To)
              .Append("\" in \"").Append(Redirect.RootName).Append('"');
            if (Redirect.WhenDate.Length > 0) sb.Append(" on ").Append(Redirect.WhenDate);
            sb.Append("; ")
              .Append(InstanceNames.Count == 1
                          ? "1 instance"
                          : $"{InstanceNames.Count} instances")
              .Append(InstanceNames.Count == 1 ? " here still references" : " here still reference")
              .Append(" the old location (")
              .Append(string.Join(", ", InstanceNames))
              .Append("). It resolves and draws correctly — use \"Update references\" on the instance "
                    + "to adopt the new location.");
            return sb.ToString();
        }
    }

    /// <summary>The one-line notice the Properties inspector shows for one affected instance.</summary>
    public string InstanceNotice =>
        $"\"{CellName}\" is no longer at \"{Redirect.From}\" in \"{Redirect.RootName}\" — it moved to "
      + $"\"{Redirect.To}\""
      + (Redirect.WhenDate.Length > 0 ? $" on {Redirect.WhenDate}" : "")
      + ". This instance still references the old location and resolves through the forwarding record.";
}

/// <summary>
/// Collects the redirects that fired while resolving a document's cell references, and groups them
/// into one report per cell.
///
/// <para><b>The detection is free.</b> Unlike SL3's hash, nothing extra is read: the redirect is
/// produced by the resolution the document was going to do anyway
/// (<see cref="ExternalCellRef.ResolveCellDir"/>), and it only ever fires on a reference that
/// resolved to nothing directly — so a design with no moved references pays for exactly the
/// filesystem calls it paid for before (R-tm2-8 step 2 short-circuits; TM2 gate 8).</para>
///
/// <para><b>Applies to every cell reference, not only <c>ws://</c> ones</b>, for SL3 R-sl3-11's
/// reason and because a cell in a referenced LIBRARY is stored as an ordinary relative path — which
/// is the case the whole brief is about.</para>
/// </summary>
public static class CellMoveWatch
{
    // ── Schematic ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans <paramref name="model"/>, sets <see cref="EditableComponent.MovedRedirect"/> on every
    /// affected component, and returns one report per affected CELL.
    /// </summary>
    public static IReadOnlyList<MovedCellReport> Scan(SchematicEditModel model, string? workspaceRoot = null)
    {
        string? baseDir = model.SchematicDirectory;

        var affected = new Dictionary<string, List<EditableComponent>>(StringComparer.Ordinal);
        var hits     = new Dictionary<string, MoveRedirectHit>(StringComparer.Ordinal);
        var order    = new List<string>();

        foreach (var comp in model.Components)
        {
            comp.MovedRedirect = null;
            if (comp.CellRef is not { Length: > 0 } cellRef) continue;

            var res = CellSymbolResolver.Resolve(cellRef, baseDir, workspaceRoot);
            if (res.Redirect is not { } hit) continue;

            comp.MovedRedirect = hit;
            if (!affected.TryGetValue(cellRef, out var list))
            {
                affected[cellRef] = list = [];
                hits[cellRef]     = hit;
                order.Add(cellRef);
            }
            list.Add(comp);
        }

        return order
            .Select(cellRef => new MovedCellReport(
                CellRef:       cellRef,
                CellName:      CellInterfaceWatch.CellNameOf(cellRef),
                Redirect:      hits[cellRef],
                InstanceNames: affected[cellRef].Select(NameOf).ToList(),
                NewCellRef:    AdoptedRefFor(cellRef, baseDir, hits[cellRef])))
            .ToList();
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The same scan for a layout's instances. There is no per-instance flag to leave behind — a
    /// <see cref="LayoutInstance"/> is a persisted model with no runtime state — so the caller keeps
    /// the returned set and marks by <c>CellRef</c>, exactly as SL3's layout half does.
    /// </summary>
    public static IReadOnlyList<MovedCellReport> Scan(LayoutView view, string baseDir, string? workspaceRoot = null)
    {
        var affected = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var hits     = new Dictionary<string, MoveRedirectHit>(StringComparer.Ordinal);
        var order    = new List<string>();

        for (int i = 0; i < view.Instances.Count; i++)
        {
            if (view.Instances[i].CellRef is not { Length: > 0 } cellRef) continue;

            var res = CellSymbolResolver.Resolve(cellRef, baseDir, workspaceRoot);
            if (res.Redirect is not { } hit) continue;

            if (!affected.TryGetValue(cellRef, out var list))
            {
                affected[cellRef] = list = [];
                hits[cellRef]     = hit;
                order.Add(cellRef);
            }
            list.Add(i);
        }

        return order
            .Select(cellRef => new MovedCellReport(
                CellRef:       cellRef,
                CellName:      CellInterfaceWatch.CellNameOf(cellRef),
                Redirect:      hits[cellRef],
                InstanceNames: affected[cellRef].Select(i => $"instance #{i + 1}").ToList(),
                NewCellRef:    AdoptedRefFor(cellRef, baseDir, hits[cellRef])))
            .ToList();
    }

    // ── Adopting it (R-tm2-13) ────────────────────────────────────────────────

    /// <summary>
    /// What the stored reference becomes when the move is adopted: the SAME producing rule every
    /// placement uses (<see cref="ExternalCellRef.MakeCellRef"/>), pointed at where the cell actually
    /// is now. A second spelling rule here would be the drift that rule exists to prevent — a
    /// <c>ws://</c> reference must stay a <c>ws://</c> reference, and a library-relative one must stay
    /// relative.
    ///
    /// <para>Null when nothing would change, so a caller can tell "already right" from "would be
    /// rewritten" without comparing strings itself.</para>
    /// </summary>
    public static string? AdoptedRefFor(string cellRef, string? baseDir, MoveRedirectHit hit)
    {
        if (string.IsNullOrEmpty(baseDir)) return null;
        string next = ExternalCellRef.MakeCellRef(baseDir, hit.ResolvedDir);
        return string.Equals(next, cellRef, StringComparison.Ordinal) ? null : next;
    }

    /// <summary>Every component in <paramref name="model"/> referencing <paramref name="cellRef"/> —
    /// what "update every instance of this cell in this document" enumerates.</summary>
    public static IEnumerable<EditableComponent> InstancesOf(SchematicEditModel model, string cellRef) =>
        model.Components.Where(c => string.Equals(c.CellRef, cellRef, StringComparison.Ordinal));

    /// <summary>
    /// The layout half of the adoption gesture. A <see cref="LayoutInstance"/> carries no runtime
    /// mark, so this writes the reference and the caller re-scans. Returns how many were rewritten.
    ///
    /// <para><b>Never automatic</b> (R-tm2-13): not on open, not on save, not as a side effect of an
    /// edit. The stored reference is the only evidence that the design was authored against a
    /// different library layout, and erasing it on open implements nothing.</para>
    /// </summary>
    public static int UpdateReferences(
        IEnumerable<LayoutInstance> instances, IReadOnlyList<MovedCellReport> reports)
    {
        var byRef = reports
            .Where(r => r.NewCellRef is { Length: > 0 })
            .ToDictionary(r => r.CellRef, r => r.NewCellRef!, StringComparer.Ordinal);

        int n = 0;
        foreach (var inst in instances)
        {
            if (inst.CellRef is not { Length: > 0 } cellRef) continue;
            if (!byRef.TryGetValue(cellRef, out string? next)) continue;
            inst.CellRef = next;
            n++;
        }
        return n;
    }

    private static string NameOf(EditableComponent c) =>
        string.IsNullOrWhiteSpace(c.InstanceName) ? "(unnamed)" : c.InstanceName;
}
