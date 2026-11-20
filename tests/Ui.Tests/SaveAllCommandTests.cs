// ================================================================
//  SaveAllCommandTests.cs — File ▸ Save All (GitHub #79).
//
//  Save All sweeps every dirty document whichever one is active. The claim held here is the one a
//  sweep can break silently: every document kind the dirty test counts is a kind the sweep saves.
//  A kind counted and not saved is a "Save All" that reports success and leaves work unsaved.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class SaveAllCommandTests
{
    private static string Workspace()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string raw = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Ui", "ViewModels", "WorkspaceViewModel.cs"));
        return Regex.Replace(raw, @"//[^\n]*", "");
    }

    private static string Body(string src, string signature)
    {
        int i = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{signature}' not found");
        int open = src.IndexOf('{', i), depth = 0;
        for (int k = open; k < src.Length; k++)
        {
            if (src[k] == '{') depth++;
            else if (src[k] == '}' && --depth == 0) return src[open..(k + 1)];
        }
        Assert.Fail($"'{signature}' has no closing brace");
        return "";
    }

    [Fact]
    public void EveryKindTheDirtyTestCounts_IsSavedBySaveAll()
    {
        string src = Workspace();
        string counted = Body(src, "public bool HasAnyDirtyWork(");
        string sweep = Body(src, "private async Task SaveEveryDirtyDocumentAsync(");

        var kinds = Regex.Matches(counted, @"OfType<([\w.]+)>\(\)|(_scratch\w+)\b|(HasOrphanedDirty\w*Session)")
            .Select(m => m.Value).Distinct().ToList();
        Assert.True(kinds.Count >= 15, $"the dirty test's kinds were not found ({kinds.Count})");

        foreach (var kind in kinds)
        {
            string needle = kind switch
            {
                "HasOrphanedDirtySession()" or "HasOrphanedDirtySession" => "_registry.GetOrphanedDirtyPaths",
                "HasOrphanedDirtyLayoutSession" => "_layoutRegistry.GetOrphanedDirtyPaths",
                _ => kind,
            };
            Assert.True(sweep.Contains(needle, StringComparison.Ordinal), $"Save All does not save {kind}");
        }

        // The command reaches the sweep and writes the workspace file after it.
        string command = Body(src, "private async Task SaveAll(Window? owner)");
        Assert.Contains("await SaveEveryDirtyDocumentAsync(window);", command);
        Assert.Contains("WriteWorkspaceFile(CurrentWorkspacePath", command);
    }
}
