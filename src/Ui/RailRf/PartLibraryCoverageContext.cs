// Which design a part library is being read ON BEHALF OF
// (docs/sonnet-briefs/brief-railrf-24-part-library-editor.md R-rail24-2c).
//
// `PartLibrary.Coverage` has answered "how many of this board's parts does this library know, and
// which of them have a bias curve" since brief 2, and nothing had ever shown it. The number it needs
// is the DENOMINATOR — the part numbers a design actually asks about — and a library file cannot
// know that: it is the same table whether one board references it or twelve.
//
// So the walk lives here rather than on the editor: the editor is handed part numbers and counts
// nothing itself, which is what keeps it framework-free AND what stops a second coverage rule
// existing. Coverage with no design behind it is not reported at all, because without a denominator
// there is nothing honest to say.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Ui.RailRf;

/// <summary>What one <c>.crail</c> asks a part library about.</summary>
/// <param name="Subject">The design's file name, shown beside the counts.</param>
/// <param name="PartNumbers">Every part number the design's rails carry, in document order and
/// including repeats — <see cref="PartLibrary.Coverage"/> distinguishes them itself.</param>
public sealed record PartLibraryCoverageContext(string Subject, IReadOnlyList<string> PartNumbers)
{
    /// <summary>
    /// The first <c>.crail</c> under <paramref name="workspaceRoot"/> whose part-library reference
    /// lands on <paramref name="crlibPath"/>, or null where none does.
    /// </summary>
    /// <remarks>
    /// <b>The reference is resolved rather than compared as text.</b> A <c>.crail</c> carries
    /// <c>PartLibraryRef</c> document-relative, exactly like every other reference it holds, so two
    /// designs in different folders name one file with two different strings — and the same design
    /// opened through a symlinked path names it with a third.
    ///
    /// <para><b>Unreadable documents are skipped, not reported.</b> This runs on the way in to an
    /// editor for a different file; a broken <c>.crail</c> elsewhere in the workspace is that
    /// document's problem and <c>check</c>'s to state, and raising it here would attach someone
    /// else's error to the library they opened.</para>
    /// </remarks>
    public static PartLibraryCoverageContext? For(string? workspaceRoot, string crlibPath)
    {
        ArgumentNullException.ThrowIfNull(crlibPath);
        if (workspaceRoot is not { Length: > 0 } || !Directory.Exists(workspaceRoot)) return null;

        string wanted;
        try   { wanted = Path.GetFullPath(crlibPath); }
        catch { return null; }

        IEnumerable<string> crails;
        try   { crails = Directory.EnumerateFiles(workspaceRoot, "*.crail", SearchOption.AllDirectories); }
        catch { return null; }

        foreach (string crail in crails.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            RailDocument document;
            try   { document = RailDocumentIo.LoadFromFile(crail); }
            catch { continue; }

            // The path the reference LANDED on, which RailArtwork reports even when the read failed —
            // and the read is expected to fail here whenever the library is mid-edit (R-rail24-3a),
            // so "where did it point" is the only question that may be asked.
            _ = RailArtwork.ResolvePartLibrary(document, crail, out string? landed, out _);
            if (landed is null) continue;

            string resolved;
            try   { resolved = Path.GetFullPath(landed); }
            catch { continue; }
            if (!string.Equals(resolved, wanted, StringComparison.OrdinalIgnoreCase)) continue;

            return new PartLibraryCoverageContext(
                Path.GetFileName(crail),
                [.. document.Rails.SelectMany(r => r.Parts).Select(part => part.PartNumber)]);
        }

        return null;
    }
}
