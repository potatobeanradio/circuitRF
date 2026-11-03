using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Every <c>Crf…</c> resource a view names is defined somewhere. Avalonia resolves a missing
/// <c>{DynamicResource}</c> to nothing and says nothing: the Import Schematic dialog named <c>CrfBorderBrush</c>,
/// which no file defined, and its three borders were invisible with no error anywhere. A source scan is the only
/// place that can see it.
/// </summary>
public sealed class CrfResourceKeysDefinedTests
{
    [Fact]
    public void EveryCrfResourceAViewNames_IsDefined()
    {
        string ui = Path.Combine(RepoRoot(), "src", "Ui");
        var axaml = Directory.EnumerateFiles(ui, "*.axaml", SearchOption.AllDirectories).Select(File.ReadAllText).ToList();
        var cs    = Directory.EnumerateFiles(ui, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText).ToList();

        var used = axaml.SelectMany(t => Regex.Matches(t, @"\{(?:Dynamic|Static)Resource (Crf[A-Za-z]+)\}").Select(m => m.Groups[1].Value))
                        .ToHashSet(StringComparer.Ordinal);
        var defined = axaml.SelectMany(t => Regex.Matches(t, @"x:Key=""(Crf[A-Za-z]+)""").Select(m => m.Groups[1].Value))
                           .Concat(cs.SelectMany(t => Regex.Matches(t, @"(?:\[|Resources\.Add\(\s*)""(Crf[A-Za-z]+)""").Select(m => m.Groups[1].Value)))
                           .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(used);
        Assert.Empty(used.Except(defined).Order(StringComparer.Ordinal));
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}
