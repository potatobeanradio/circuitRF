using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CircuitRF.Ui.Schematic;

// ──────────────────────────────────────────────────────────────────────────────
//  SL3 — the cell interface, reduced to a short content hash.
//
//  A design records the hash it was placed against; the hash is recomputed at
//  resolve and compared. Equal means the cell still has the shape the design was
//  drawn for; different means it does not, and the user is told (never repaired
//  for, never refused — brief-shared-library-3-interface-change.md R-sl3-1).
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The content hash of a cell's published INTERFACE — what an instance in a parent depends on,
/// and nothing else (R-sl3-2).
///
/// <para><b>What is in it:</b> the pins in <c>PortIndex</c> order (index, local x, local y, name),
/// the symbol's own <c>PortCount</c>, and the parameter NAMES the <c>.ccell</c> declares, in
/// declaration order.</para>
///
/// <para><b>What is deliberately NOT in it,</b> because none of it can break a referencing design:
/// the drawing primitives (a redrawn glyph that keeps its pins breaks nothing), the parameter
/// DEFAULTS (an instance that overrides one is unaffected, and one that does not is <i>meant</i> to
/// follow the library), the cell's schematic, and its layout view. Reporting a change that cannot
/// break anything trains the user to dismiss the report, which costs more than the report is
/// worth.</para>
///
/// <para><b>The HASH is stored, never the interface</b> (R-sl3-3). Writing the whole signature into
/// every referencing document would put a copy of the library's interface in every file that names
/// it — a second source of truth, which is the thing every reference form in this codebase exists to
/// avoid. The cost is that a report can say WHAT the cell's interface is now and what this instance
/// no longer fits, but not what the interface used to be; see <see cref="CellInterfaceChange"/> for
/// what is said instead.</para>
/// </summary>
public static class CellInterfaceHash
{
    /// <summary>Bumped only if the canonical form below changes shape. Recorded IN the hash input,
    /// so an old recorded hash cannot silently compare equal to a new-scheme one.</summary>
    private const string SchemeVersion = "crf-cell-iface-1";

    /// <summary>Hex characters kept. Twelve is what <c>GeneratedCellStore</c> already uses for a
    /// content key and is far past collision relevance for a per-instance equality check.</summary>
    private const int Length = 12;

    /// <summary>
    /// The hash of the interface <paramref name="symbol"/> and <paramref name="ccell"/> publish.
    /// Deterministic across processes and machines: every number is written with the invariant
    /// round-trip format and every string is length-prefixed, so no field can be confused for the
    /// next one.
    /// </summary>
    public static string Of(Symbol? symbol, CcellFile? ccell)
    {
        var sb = new StringBuilder(SchemeVersion).Append('\n');

        // Pins, in PortIndex order. The .csym's own list order is a drawing-order artifact — moving
        // a pin up the list in the editor does not change what an instance depends on — so the order
        // is imposed here rather than taken from the file. Position and name break the tie, so two
        // pins sharing a PortIndex still hash deterministically.
        var pins = (symbol?.Pins ?? [])
            .OrderBy(p => p.PortIndex)
            .ThenBy(p => p.LocalX)
            .ThenBy(p => p.LocalY)
            .ThenBy(p => p.Name, StringComparer.Ordinal);

        sb.Append("pins\n");
        foreach (var pin in pins)
        {
            sb.Append(pin.PortIndex.ToString(CultureInfo.InvariantCulture)).Append('\t');
            Num(sb, pin.LocalX).Append('\t');
            Num(sb, pin.LocalY).Append('\t');
            Str(sb, pin.Name).Append('\n');
        }

        // PortCount is NOT the pin count — a symbol may map pins to a larger port set — so it is its
        // own field. Symbol's constructor already defaults it to Pins.Count, which is what a .csym
        // written before the field existed reads back as.
        sb.Append("ports\t").Append((symbol?.PortCount ?? 0).ToString(CultureInfo.InvariantCulture)).Append('\n');

        sb.Append("params\n");
        foreach (var p in ccell?.Parameters ?? [])
            Str(sb, p.Name).Append('\n');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..Length];
    }

    /// <summary>
    /// The hash of the interface <paramref name="cellRef"/> currently resolves to, or <b>null when
    /// there is nothing to hash</b> — the cell does not resolve, or it resolves with no usable
    /// primary symbol.
    ///
    /// <para>Null is never recorded and never compared (R-sl3-5): an absent recorded hash means
    /// "never recorded", which is exactly what every file written before this feature has and what
    /// every hand-placed instance has. A cell that cannot be read is already a reported, repairable
    /// state of its own (§4.2's NotFound / PrimaryMissing) and must not also become an interface
    /// report.</para>
    /// </summary>
    public static string? For(string? cellRef, string? baseDir, string? workspaceRoot = null)
    {
        if (string.IsNullOrEmpty(cellRef)) return null;
        if (baseDir is null && !CellSymbolResolver.NeedsNoBaseDirectory(cellRef)) return null;

        var res = CellSymbolResolver.Resolve(cellRef, baseDir, workspaceRoot);
        if (res is not { State: CellSymbolState.Resolved, Symbol: { } symbol }) return null;

        return Of(symbol, CellSymbolResolver.ResolveCcell(cellRef, baseDir ?? "", workspaceRoot));
    }

    // ── Canonical field writers ───────────────────────────────────────────────

    /// <summary>Invariant round-trip, so the same coordinate hashes the same on every machine and in
    /// every locale — a comma decimal separator would otherwise make a design authored in one place
    /// report a changed interface everywhere else.</summary>
    private static StringBuilder Num(StringBuilder sb, double v) =>
        sb.Append(v.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>Length-prefixed, and null distinguished from empty. Without the prefix a pin named
    /// <c>"a\tb"</c> would hash identically to two fields, which is the one way a hash comparison
    /// can report "unchanged" about something that changed.</summary>
    private static StringBuilder Str(StringBuilder sb, string? s) =>
        s is null ? sb.Append('-') : sb.Append(s.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s);
}
