// WHAT MAKES TWO PLACEMENTS THE SAME EXTRACTION — brief-lvs-9-hierarchy.md R-lvs9-1b,
// docs/design/lvs.md §4.5.
//
// ── THE KEY SHAPE THE REST OF THE APPLICATION ALREADY USES ────────────────────────────────────
//
// A content hash of (cell directory, primary `.clay` mtime, resolved technology identity, resolved
// PCell parameters). That is `GeneratedCellStore.BuildCellName`'s own recipe and
// `CellLayoutResolver`'s own cache key put together — deliberately, because a cache key that
// differs from the one the rest of the application uses is a cache that is stale in exactly the
// cases the others are not, and nothing anywhere reports the disagreement.
//
// `CellLayoutResolver` keys on (cell folder, primary name, mtime) and re-reads when the file moves
// underneath it. `BuildCellName` keys on (generator, parameters, technology identity, technology
// content) and is what makes editing a `.ctech` actually invalidate the artwork drawn against it.
// An LVS extraction depends on all of it: the same `.clay` read against a different technology is a
// different partition, and a PCell is its parameters (R-lvs9-1d).
//
// ── WHY THE RECIPE IS RE-STATED HERE RATHER THAN CALLED ───────────────────────────────────────
//
// `GeneratedCellStore` lives in `src/Ui` and `src/Design` may not reference it — the firewall runs
// the other way. What is reused is the SHAPE, not the function, and this file says so rather than
// pretending otherwise. The two are not required to produce the same STRING: they name different
// things (one names a folder on disk, one names an entry in a dictionary that lives for one run),
// and only the inputs have to agree.
//
// ── IT IS PER RUN AND IN MEMORY (R-lvs9-1c) ───────────────────────────────────────────────────
//
// Nothing is written. LVS is read-only on `check`'s terms (R-aut4-6), so it runs on a read-only
// tree and on a workspace another process has open, and a cache FILE would be the one thing in the
// feature that could not.

using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Layout.Lvs;

/// <summary>The identity of one cell extraction — R-lvs9-1b.</summary>
public static class LvsCellKey
{
    /// <summary>
    /// The content key for extracting <paramref name="cellDir"/> against
    /// <paramref name="tech"/>.
    /// </summary>
    /// <param name="cellDir">The resolved cell folder, absolute.</param>
    /// <param name="view">Its primary layout, as read — the <c>PCellOrigin</c> parameters come off
    /// it, so two placements of one generator with different values are two entries (R-lvs9-1d).</param>
    /// <param name="tech">The technology this extraction will read the artwork against.</param>
    /// <remarks>
    /// <b>The mtime is read from disk and a failure to read it is part of the key.</b> A cell whose
    /// file cannot be stat'd gets a key that will not match the next call's, which re-extracts — the
    /// safe direction. A key that silently collapsed to "no mtime" would make every such cell one
    /// entry, and the two would be compared against each other's copper.
    /// </remarks>
    public static string Of(string cellDir, LayoutView? view, Technology? tech)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cellDir);

        var sb = new StringBuilder();
        sb.Append(Path.GetFullPath(cellDir)).Append('|');
        sb.Append(MtimeOf(cellDir)).Append('|');

        // The technology's IDENTITY, which is its name and its layer/stackup content — not its
        // path. circuitRF ships the editor that edits a `.ctech`, and a path does not change when
        // the file behind it does.
        sb.Append(tech?.Name ?? "").Append('|').Append(TechContentOf(tech)).Append('|');

        // R-lvs9-1d. A PCell is its parameters, in a stable order.
        if (view?.PCellOrigin is { } origin)
        {
            sb.Append(origin.GeneratorId).Append('|');
            foreach (var (name, value) in origin.Parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                sb.Append(name).Append('=').Append(value.ToString()).Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16]
                      .ToLowerInvariant();
    }

    /// <summary>The primary layout's own last-write time, or a sentinel naming why there is
    /// none.</summary>
    private static string MtimeOf(string cellDir)
    {
        try
        {
            var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
            if (primary.ResolvedName is not { Length: > 0 } name) return "no-primary";

            string path = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), name);
            return File.Exists(path)
                ? File.GetLastWriteTimeUtc(path).Ticks.ToString()
                : "missing:" + name;
        }
        catch (Exception ex)
        {
            // Unreadable, and the key says so rather than pretending the cell has no mtime — which
            // would make every unreadable cell one entry.
            return "unreadable:" + ex.GetType().Name + ":" + Guid.NewGuid().ToString("N")[..8];
        }
    }

    /// <summary>
    /// The technology's own CONTENT, as its file spells it.
    /// </summary>
    /// <remarks>
    /// <b>The serialized document rather than a field-by-field digest</b>, which is
    /// <c>GeneratedCellStore.TechnologyContentKey</c>'s own choice and for its reason: circuitRF
    /// ships the editor that edits a <c>.ctech</c>, so a technology's PATH does not change when the
    /// process behind it does. A field-by-field digest would also have to name the stackup's span
    /// fields, and the via walk is <c>DrcConnectivity</c>'s alone — nothing under <c>Lvs/</c> may
    /// read one, which a source scan holds.
    ///
    /// <para>It keeps the presentational fields that <c>BuildCellName</c> strips. Those cannot
    /// matter here: this cache lives for ONE run and a run holds one technology object, so the only
    /// effect they could have is to invalidate an entry that was going to be rebuilt anyway.</para>
    /// </remarks>
    private static string TechContentOf(Technology? tech)
    {
        if (tech is null) return "";
        try { return TechPersistence.Serialize(tech); }
        catch (Exception ex) { return "unserializable:" + ex.GetType().Name; }
    }
}
