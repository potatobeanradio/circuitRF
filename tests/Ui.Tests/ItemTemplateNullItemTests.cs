using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Every item template built in code must tolerate a NULL item. When a list shrinks, Avalonia
/// recycles the removed row's container and builds its template once more with no item while
/// clearing it — so a template that dereferences its row unguarded throws on the UI thread, which
/// is unhandled and closes the application. Manage Libraries ▸ Remove did exactly that.
///
/// <para>Pinned by source scan: these templates live in Windows, which this suite does not
/// construct headlessly.</para>
/// </summary>
public sealed class ItemTemplateNullItemTests
{
    [Fact]
    public void EveryCodeBuiltItemTemplateGuardsANullItem()
    {
        var uiDir = Path.Combine(RepoRoot(), "src", "Ui");
        int seen = 0;

        foreach (var file in Directory.EnumerateFiles(uiDir, "*.cs", SearchOption.AllDirectories))
        {
            string code = File.ReadAllText(file);
            if (!code.Contains("FuncDataTemplate<", StringComparison.Ordinal)) continue;

            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(code, @"FuncDataTemplate<[^>]+>(?:\s+\w+\(\)\s*=>\s*new|\s*)\s*\(\s*\(\s*(\w+)\s*,"))
            {
                seen++;
                string item = m.Groups[1].Value;
                // The guard has to come before the item is used, so look only at the template's opening lines.
                string head = code.Substring(m.Index, Math.Min(400, code.Length - m.Index));
                Assert.True(head.Contains($"{item} is null", StringComparison.Ordinal),
                    $"{Path.GetRelativePath(uiDir, file)}: the item template does not guard a null '{item}'.");
            }
        }

        // The scan must actually find the templates, or it passes by matching nothing.
        Assert.True(seen >= 2, $"expected at least two code-built item templates, found {seen}");
    }

    private static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VERSION"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
