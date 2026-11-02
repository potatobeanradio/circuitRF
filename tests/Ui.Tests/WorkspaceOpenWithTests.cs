// ================================================================
//  WorkspaceOpenWithTests.cs — a workspace's dot-named `.cws` and `.cwsuser` have no extension as far as macOS is
//  concerned, so Finder opened them in a text editor. Saving marks both files with Finder's per-file Open With choice
//  (WorkspaceOpenWith); these hold the mark's application to the bundle's own identifier and its value to a property list
//  macOS itself reads back.
// ================================================================

using System.Diagnostics;
using System.Text.RegularExpressions;
using CircuitRF.Design.Workspace;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class WorkspaceOpenWithTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate the repository root.");
        return Path.Combine([dir!.FullName, .. parts]);
    }

    /// <summary>The mark names an application by bundle identifier; one that drifted from the bundle's names nothing.</summary>
    [Fact]
    public void The_mark_names_the_bundles_own_identifier()
    {
        string plist = File.ReadAllText(RepoFile("src", "Ui", "Assets", "macOS", "Info.plist"));
        var m = Regex.Match(plist, @"<key>CFBundleIdentifier</key>\s*<string>([^<]*)</string>");
        Assert.True(m.Success);
        Assert.Equal(m.Groups[1].Value, WorkspaceOpenWith.BundleIdentifier);
    }

    /// <summary>A saved workspace's two files both carry the mark, and macOS's own reader parses it to that identifier.</summary>
    [Fact]
    public void Saving_a_workspace_marks_both_files_on_macOS()
    {
        if (!OperatingSystem.IsMacOS()) return;
        string dir = Directory.CreateTempSubdirectory("crf-openwith-").FullName;
        try
        {
            string cws = Path.Combine(dir, ".cws");
            Assert.True(WorkspacePersistence.SaveToFileAtomic(cws, new CwsFile { DockLayout = System.Text.Json.Nodes.JsonNode.Parse("{\"a\":1}") }));
            foreach (string file in new[] { cws, WorkspaceUserPersistence.PathFor(cws) })
            {
                Assert.True(File.Exists(file), file);
                string hex = Run("/usr/bin/xattr", "-px", WorkspaceOpenWith.AttributeName, file);
                byte[] bytes = Convert.FromHexString(Regex.Replace(hex, @"\s", ""));
                string value = Path.Combine(dir, "value.plist");
                File.WriteAllBytes(value, bytes);
                Assert.Equal(WorkspaceOpenWith.BundleIdentifier, Run("/usr/bin/plutil", "-extract", "bundleidentifier", "raw", "-o", "-", value).Trim());
                Assert.Equal("0", Run("/usr/bin/plutil", "-extract", "version", "raw", "-o", "-", value).Trim());
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{exe} {string.Join(' ', args)}: {error}");
        return output;
    }
}
