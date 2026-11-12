using System.Text.RegularExpressions;

namespace CircuitRF.Firewall.Tests;

/// <summary>
/// HZN-1 — <c>src/Design/Interchange/Hzn/</c>, where every <c>.hzn</c> is read, written and translated
/// (brief-hzn-0 D10), stays below the UI firewall. The assembly row in <see cref="UiFirewallTests"/> holds
/// <c>CircuitRF.Design.dll</c>; this holds the folder: every file is compiled into that assembly's own namespace and
/// names no UI framework or <c>src/Ui</c> type, so the CLI verbs can reach the format through their existing seams.
/// </summary>
public sealed class HznBoundaryTests
{
    private static readonly Regex Ui = new(@"\bAvalonia\b|\bCircuitRF\.Ui\b", RegexOptions.Compiled);

    private static string Folder => Path.Combine(SolverBoundaryTests.RepoRoot(), "src", "Design", "Interchange", "Hzn");

    internal static IReadOnlyList<string> Violations(IEnumerable<string> files) =>
    [
        .. files.SelectMany(f =>
        {
            string text = File.ReadAllText(f);
            var found = Ui.Matches(text).Select(m => $"{f}: {m.Value}").ToList();
            if (!text.Contains("namespace CircuitRF.Design.Interchange.Hzn;", StringComparison.Ordinal))
                found.Add($"{f}: not in namespace CircuitRF.Design.Interchange.Hzn");
            return found;
        }),
    ];

    [Fact]
    public void TheHznFolder_IsBelowTheFirewall()
    {
        var files = Directory.EnumerateFiles(Folder, "*.cs", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);
        var found = Violations(files);
        Assert.True(found.Count == 0, "src/Design/Interchange/Hzn crosses the UI firewall:\n" + string.Join("\n", found));
    }

    [Fact]
    public void Planted_AUiReferenceIsCaught()
    {
        string path = Path.Combine(Path.GetTempPath(), "crf-hznwall-" + Guid.NewGuid().ToString("N")[..10] + ".cs");
        File.WriteAllText(path, "using Avalonia.Threading;\nnamespace CircuitRF.Design.Interchange.Hzn;\n");
        try { Assert.NotEmpty(Violations([path])); }
        finally { File.Delete(path); }
    }
}
