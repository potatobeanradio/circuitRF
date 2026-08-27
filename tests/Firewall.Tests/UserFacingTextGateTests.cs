using System.Text.RegularExpressions;

namespace CircuitRF.Firewall.Tests;

/// <summary>
/// Keeps the count of user-facing English authored BELOW the UI firewall going down rather than up
/// (brief-localization-groundwork.md R-loc-5 §8.3, item 4).
///
/// <para><b>The problem this guards.</b> <c>IMessageSink</c>'s own doc comment says the engine never
/// calls it — "it returns a DataSet; the UI layer reads the result and posts". That is true of the
/// INTERFACE and false of the TEXT: the sentence is authored in the numeric layer and laundered
/// across the wall through <c>ex.Message</c>, which <c>src/Ui</c> then interpolates into a
/// <c>Messages.Warning</c>/<c>Error</c> call in 118 places. Because a finished string is the only
/// thing that crosses, the Messages window cannot filter by kind, deduplicate a refusal repeated at
/// every sweep point, or attach an action to a diagnostic that knows what it is.</para>
///
/// <para><b>What this test does and does not claim.</b> It does not claim the listed messages are
/// wrong, and it does not require anyone to convert them — R-loc-5 converts exactly one family and
/// says to leave the other messages alone, converting opportunistically when one is touched for
/// another reason. What it does is freeze the population: a message already in
/// <c>user-facing-text-allowlist.txt</c> is fine forever, and a NEW one has to be a deliberate act
/// rather than a habit. The allow-list file IS the backlog, and it is meant to shrink.</para>
///
/// <para><b>On the count.</b> The brief estimated 131 such messages; this scanner's own definition —
/// any <c>throw new …Exception("…")</c> whose literal is three words or more, across the five
/// message-producing projects — finds 481. The discrepancy is definitional, not a contradiction:
/// the wider net catches internal invariant messages that no user will ever see alongside the ones
/// that reach the Messages window, and there is no reliable way to separate them by inspection. The
/// gate is written to be robust to that: it freezes whatever the scanner measures instead of
/// asserting a number, so an over-broad definition costs a longer backlog file and never a false
/// pass.</para>
/// </summary>
public class UserFacingTextGateTests
{
    private static readonly string[] NonUiProjects =
        ["src/Core", "src/RfCore", "src/Engine", "src/Design", "src/WBond"];

    /// <summary>
    /// Mirrors the generator that produced the allow-list. Deliberately simple and deliberately
    /// broad — a regex that tried to guess "is this one the user actually sees?" would be wrong in
    /// both directions and would make the gate untrustworthy.
    /// </summary>
    private static readonly Regex ThrownMessage = new(
        @"throw\s+new\s+\w*Exception\s*\(\s*\$?""((?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    [Fact]
    public void NoNewUserFacingTextIsAddedBelowTheUiFirewall()
    {
        string repoRoot  = FindRepoRoot();
        var    allowed   = ReadAllowList(Path.Combine(repoRoot, "tests", "Firewall.Tests", "user-facing-text-allowlist.txt"));
        var    found     = ScanForUserFacingText(repoRoot);

        var added = found.Where(e => !allowed.Contains(e)).OrderBy(e => e, StringComparer.Ordinal).ToList();

        Assert.True(added.Count == 0,
            $"{added.Count} user-facing message(s) were added below the UI firewall.\n\n" +
            "Prefer a CircuitRF.Diagnostics.Diagnostic — an id, typed arguments and an English\n" +
            "default template. That is what lets the Messages window group these by kind, collapse a\n" +
            "refusal repeated at every sweep point into one line, and attach an action to it; a bare\n" +
            "sentence supports none of that, whatever language it is in. See\n" +
            "src/Design/Layout/Em/EmDiagnostics.cs for the worked example.\n\n" +
            "If a plain exception really is right here (an internal invariant no user will read),\n" +
            "add the line below to tests/Firewall.Tests/user-facing-text-allowlist.txt.\n\n" +
            string.Join("\n", added.Select(a => "    " + a)));
    }

    /// <summary>
    /// The other half, and the one that makes the backlog honest: an allow-list entry whose message
    /// is gone should be DELETED, not left to accumulate. Without this the file only ever grows, and
    /// a file that only grows stops being a backlog and becomes wallpaper.
    /// </summary>
    [Fact]
    public void TheAllowListHasNoStaleEntries()
    {
        string repoRoot = FindRepoRoot();
        var    allowed  = ReadAllowList(Path.Combine(repoRoot, "tests", "Firewall.Tests", "user-facing-text-allowlist.txt"));
        var    found    = ScanForUserFacingText(repoRoot);

        var stale = allowed.Where(e => !found.Contains(e)).OrderBy(e => e, StringComparer.Ordinal).ToList();

        Assert.True(stale.Count == 0,
            $"{stale.Count} allow-list entr(ies) no longer match any message in the source — the " +
            $"message was converted, reworded or deleted.\n" +
            $"Remove them from tests/Firewall.Tests/user-facing-text-allowlist.txt; shrinking that " +
            $"file is the point of it.\n\n" +
            string.Join("\n", stale.Select(s => "    " + s)));
    }

    // ── Scanner ──────────────────────────────────────────────────────────────

    private static HashSet<string> ScanForUserFacingText(string repoRoot)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in NonUiProjects)
        {
            string root = Path.Combine(repoRoot, project.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(Directory.Exists(root), $"Project folder not found: {root}");

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file)) continue;

                string relative = Path.GetRelativePath(repoRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                foreach (Match m in ThrownMessage.Matches(File.ReadAllText(file)))
                {
                    string message = m.Groups[1].Value;
                    if (message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3)
                        found.Add($"{relative}|{message}");
                }
            }
        }
        return found;
    }

    private static bool IsBuildOutput(string path)
    {
        string p = path.Replace(Path.DirectorySeparatorChar, '/');
        return p.Contains("/bin/", StringComparison.Ordinal)
            || p.Contains("/obj/", StringComparison.Ordinal);
    }

    private static HashSet<string> ReadAllowList(string path)
    {
        Assert.True(File.Exists(path), $"Allow-list not found: {path}");
        return File.ReadAllLines(path)
                   .Where(l => l.Length > 0 && !l.StartsWith('#'))
                   .ToHashSet(StringComparer.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null,
            "Could not locate the repo root (the folder holding circuitRF.slnx) from " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
