// WHICH of a process's deck files are one deck, and which are a different deck entirely.
//
// ── Why this exists ──────────────────────────────────────────────────────────────────────────────
//
// A scan finds every file that reads as a rule deck. That is the right way to FIND them and the
// wrong way to READ them, because a process routinely ships more than one deck: a main one split
// across dozens of files, plus separate optional ones (density, antenna) and — the case that forced
// this — a self-contained alternative that reimplements the whole rule set through helper methods of
// its own.
//
// Reading them all as one program is wrong twice over:
//
//   * It merges two independent symbol namespaces. A derived layer named the same thing in both
//     resolves against whichever was read last, and a rule then measures a region its own deck never
//     meant — the silent-wrong-answer failure this whole area exists to avoid.
//
//   * A helper LIBRARY is not a rule set. Measured on a real process, its alternative deck states
//     zero reporting calls and defines its vocabulary as methods; every "rule" read from it came from
//     a line inside a method body. 117 phantom rules, attributed to whatever layer their symbols
//     happened to resolve to, and every one of them would then have been ENFORCED on the user's
//     artwork.
//
// ── What decides it ──────────────────────────────────────────────────────────────────────────────
//
// The deck's own include graph, which is a fact stated by the process rather than a convention
// circuitRF invents. A deck that pulls its rule files in is the ROOT of a deck; the files it pulls in
// are that deck's own. Anything left over is a DIFFERENT deck, reported rather than merged.
//
// Checked against a real process's own runner script: the set this selects — the root plus its
// includes — is exactly the set that runner calls the main rule set, and the three files it leaves
// out are exactly the three that runner launches as separate runs. Nothing here reads that script, or
// knows it exists; the agreement is the evidence that the include graph is the right signal.
//
// <b>The open framework standard does NOT settle this.</b> It defines where a process's per-tool
// files live and says nothing about which deck file is canonical or how a tool should discover one.
// So there is no standard to follow here, and this deliberately follows the PROCESS's own statement
// instead of inventing a naming convention that the next process would not share.

using System.Text.RegularExpressions;

namespace CircuitRF.Ui.Layout.TechImport;

/// <summary>Which deck files form one deck, and which belong to another.</summary>
/// <param name="MainSet">
/// The files to read together, in include order where one is known. Every file when the process
/// states no include graph at all — a flat deck is one deck, and refusing to read it because nothing
/// said so would be worse than the problem this solves.
/// </param>
/// <param name="Alternates">
/// Deck files belonging to no selected deck: a separate optional deck, or a self-contained
/// alternative. Reported, never merged.
/// </param>
/// <param name="RootPath">The file the main set was rooted at, or null when none was found.</param>
public sealed record RuleDeckSelection(
    IReadOnlyList<string> MainSet,
    IReadOnlyList<string> Alternates,
    string?               RootPath);

public static partial class RuleDeckSelector
{
    /// <summary>
    /// Splits the scanned deck files into the one deck to read and the ones to report.
    /// </summary>
    /// <param name="files">Absolute path and full text of every file the scan classified as a deck.</param>
    public static RuleDeckSelection Select(IReadOnlyList<(string Path, string Text)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) return new RuleDeckSelection([], [], null);

        var byPath = new Dictionary<string, string>(PathComparer);
        foreach (var (path, text) in files) byPath[Normalize(path)] = text;

        // Who includes whom. An include names a path relative to the INCLUDING file's own folder,
        // which is the only reading that survives the deck being checked out anywhere.
        var includes = new Dictionary<string, List<string>>(PathComparer);
        var included = new HashSet<string>(PathComparer);

        foreach (var (path, text) in files)
        {
            string self = Normalize(path);
            string dir  = Path.GetDirectoryName(self) ?? "";
            var targets = new List<string>();

            foreach (Match m in IncludeRegex().Matches(text))
            {
                string rel = m.Groups[1].Value.Trim();
                if (rel.Length == 0) continue;

                string target;
                try { target = Normalize(Path.Combine(dir, rel)); }
                catch (ArgumentException) { continue; }

                // Only an include that names a file the scan actually found counts. A deck may include
                // something this reader never classified as a deck at all (a shared helper), and
                // treating that as a missing member would make the graph unreadable.
                if (!byPath.ContainsKey(target) || PathComparer.Equals(target, self)) continue;

                targets.Add(target);
                included.Add(target);
            }

            includes[self] = targets;
        }

        // A ROOT pulls files in and is pulled in by nothing. A process with several is stating several
        // decks; the largest is the main one, which is the same "most complete" tie-break the stack
        // and value-table readers already use, and the rest are reported.
        var roots = includes
            .Where(kv => kv.Value.Count > 0 && !included.Contains(kv.Key))
            .Select(kv => kv.Key)
            .OrderByDescending(p => Reachable(p, includes).Count)
            .ThenBy(p => p, StringComparer.Ordinal)
            .ToList();

        if (roots.Count == 0)
        {
            // No include graph at all: a flat deck, or a process using a mechanism this does not read.
            // Everything is one deck — exactly the behaviour before this existed.
            return new RuleDeckSelection([.. files.Select(f => Normalize(f.Path))], [], null);
        }

        string root = roots[0];
        var main = Reachable(root, includes);

        // Include order first, so a derived layer is bound before the file that measures it — the
        // fixed-point passes make the read order-independent anyway, but presenting the deck in its
        // own order is what makes a diagnostic about it readable.
        var mainOrdered = new List<string> { root };
        mainOrdered.AddRange(main.Where(p => !PathComparer.Equals(p, root))
                                 .OrderBy(p => p, StringComparer.Ordinal));

        var alternates = byPath.Keys
            .Where(p => !main.Contains(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        return new RuleDeckSelection(mainOrdered, alternates, root);
    }

    /// <summary>Every file reachable from <paramref name="from"/>, including itself. Cycle-safe.</summary>
    private static HashSet<string> Reachable(string from, Dictionary<string, List<string>> includes)
    {
        var seen  = new HashSet<string>(PathComparer);
        var stack = new Stack<string>();
        stack.Push(from);

        while (stack.Count > 0)
        {
            string p = stack.Pop();
            if (!seen.Add(p)) continue;
            if (includes.TryGetValue(p, out var next))
                foreach (var t in next) stack.Push(t);
        }

        return seen;
    }

    private static string Normalize(string path) => Path.GetFullPath(path);

    /// <summary>
    /// Windows and macOS compare paths case-insensitively; Linux does not. The same rule the manifest
    /// re-pointing already applies, for the same reason — a deck checked out on a case-insensitive
    /// filesystem must not fail to match its own include.
    /// </summary>
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>
    /// The include forms a deck states. Both are the deck language's own, not a file-name convention:
    /// a preprocessor directive written as a comment (so the file stays loadable unprocessed), and the
    /// plain script-level load a deck uses when it has no preprocessor.
    /// </summary>
    [GeneratedRegex(@"^\s*(?:#\s*%include\s+|load\s+['""])([^\s'""]+)",
                    RegexOptions.Multiline)]
    private static partial Regex IncludeRegex();
}
