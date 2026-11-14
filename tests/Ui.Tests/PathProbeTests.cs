using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Design;
using CircuitRF.Design.Workspace;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A path the file system cannot reach (a share whose server is down, a cloud folder whose service is
/// unreachable) makes every check on it WAIT rather than fail. <see cref="PathProbe"/> is what keeps
/// that wait off the UI thread and bounded.
/// </summary>
public class PathProbeTests
{
    private static string UniquePath() => Path.Combine(Path.GetTempPath(), "crf-probe-" + Guid.NewGuid().ToString("N"));

    /// <summary>A check that hangs is given up on at the timeout, and the path is then reported unresponsive
    /// at once, without a second wait, while the hung check is still stuck.</summary>
    [Fact]
    public async Task AHungCheck_IsGivenUpOnAtTheTimeout_AndRemembered()
    {
        using var release = new ManualResetEventSlim(false);
        string path = UniquePath();
        try
        {
            var watch = Stopwatch.StartNew();
            var state = await PathProbe.ProbeAsync(path, TimeSpan.FromMilliseconds(200), _ => { release.Wait(); return true; });
            Assert.Equal(PathState.NotResponding, state);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"waited {watch.Elapsed}");

            Assert.True(PathProbe.IsKnownUnresponsive(path));
            watch.Restart();
            Assert.Equal(PathState.NotResponding, await PathProbe.ProbeAsync(path, TimeSpan.FromSeconds(30)));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"asked again and waited {watch.Elapsed}");
        }
        finally { release.Set(); }
    }

    /// <summary>On a disk that answers, a workspace is Present or Missing — never refused as unresponsive.</summary>
    [Fact]
    public async Task AWorkspaceOnALocalDisk_IsPresentOrMissing()
    {
        string root = UniquePath();
        Directory.CreateDirectory(root);
        try
        {
            string cws = Path.Combine(root, ".cws");
            Assert.Equal(PathState.Missing, (await WorkspaceReachability.CheckAsync(cws)).State);

            File.WriteAllText(cws, "{}");
            var present = await WorkspaceReachability.CheckAsync(cws);
            Assert.True(present.Ok, present.Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
