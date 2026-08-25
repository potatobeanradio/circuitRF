// Every Markdown table in the user docs must have the same number of cells in every row.
//
// Owner-reported, 2026-08-24: a table in the MoM engine page rendered with "many extra empty cells".
// The cause is the one way a Markdown table goes wrong silently — an unescaped `|` INSIDE a cell.
// `| Frequency | |S₁₁| of a section that should be zero |` is not a two-column row; the bars around
// the magnitude are column separators, so the header parsed as four cells against a two-column
// separator and the renderer filled the difference with blanks.
//
// It fails silently in both directions that matter: the Markdown is valid, so nothing errors, and the
// prose reads correctly in the SOURCE — which is where an author checks it. Only the rendered page is
// wrong. This page already writes `Σ\|S\|²` correctly elsewhere, so it is not that the convention was
// unknown; it is that one row missed it and nothing said so.

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace CircuitRF.Ui.Tests;

public class DocTableIntegrityTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md walking up from this test file).");
        return dir!;
    }

    /// <summary>The PROSE source, not the generated HTML — this is an authoring mistake, and the
    /// source is where it can be pointed at by file and line.</summary>
    private static string DocSource() => Path.Combine(RepoRoot(), "docs", "user", "src");

    /// <summary>A table row's cells. Splits on `|` that is NOT backslash-escaped, which is exactly the
    /// distinction the renderer makes and therefore the only one worth reproducing.</summary>
    private static string[] Cells(string line)
    {
        string s = line.Trim();
        if (s.StartsWith('|')) s = s[1..];
        if (s.EndsWith('|'))   s = s[..^1];
        return Regex.Split(s, @"(?<!\\)\|");
    }

    private static bool IsSeparator(string line)
        => Regex.IsMatch(line, @"^\s*\|[\s:|\-]+\|\s*$") && line.Contains('-');

    [Fact]
    public void EveryTableRowHasTheSameCellCountAsItsHeader()
    {
        var offences = new List<string>();
        int tables = 0;

        foreach (string path in Directory.EnumerateFiles(DocSource(), "*.md", SearchOption.AllDirectories)
                                         .OrderBy(p => p, StringComparer.Ordinal))
        {
            var lines = File.ReadAllLines(path);
            for (int i = 1; i < lines.Length; i++)
            {
                if (!IsSeparator(lines[i]) || !lines[i - 1].TrimStart().StartsWith('|')) continue;

                tables++;
                int want = Cells(lines[i]).Length;

                // The header, then every row until the table ends.
                var rows = new List<int> { i - 1 };
                for (int k = i + 1; k < lines.Length && lines[k].TrimStart().StartsWith('|'); k++)
                    rows.Add(k);

                foreach (int r in rows)
                {
                    int got = Cells(lines[r]).Length;
                    if (got == want) continue;
                    offences.Add(
                        $"{Path.GetRelativePath(RepoRoot(), path)}:{r + 1} has {got} cells in a " +
                        $"{want}-column table — an unescaped '|' inside a cell splits it. Write it as " +
                        $@"\| (this page already does elsewhere). Row: {lines[r].Trim()}");
                }

                i = rows[^1];
            }
        }

        // Guards the guard: a scanner that found no tables would pass this file for the wrong reason.
        Assert.True(tables > 20, $"only {tables} tables were scanned; the table detector has drifted");
        Assert.True(offences.Count == 0, string.Join("\n", offences));
    }
}
