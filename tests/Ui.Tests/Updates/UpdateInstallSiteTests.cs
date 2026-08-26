using System;
using System.IO;
using System.Runtime.InteropServices;
using CircuitRF.Ui.Updates;
using Xunit;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>
/// R-AU-14 — one runtime check, against temp-directory fixtures for every layout in design §2.
/// No real installation is involved and no platform API is consulted for POLICY: the shape is read
/// off the filesystem and writability is PROBED by attempting a write.
/// </summary>
public class UpdateInstallSiteTests : IDisposable
{
    private readonly string _tmp =
        Path.Combine(Path.GetTempPath(), "crf-installsite-" + Guid.NewGuid().ToString("N")[..8]);

    public UpdateInstallSiteTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { MakeWritable(_tmp); Directory.Delete(_tmp, true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string Make(params string[] parts)
    {
        string p = Path.Combine([_tmp, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    // ── the four layouts ──────────────────────────────────────────────────────────────────────

    /// <summary>macOS: /Applications/circuitRF.app — the bundle IS the launch path.</summary>
    private string MacBundleBaseDir(string container = "Applications")
    {
        string baseDir = Make(container, "circuitRF.app", "Contents", "MacOS");
        return baseDir;
    }

    /// <summary>The user-local channel: versioned directories behind a `current` pointer.</summary>
    private string VersionedBaseDir(string root = "Programs")
    {
        string r = Make(root, "circuitRF");
        string baseDir = Make(root, "circuitRF", "app-1.0.0-beta.1");
        File.WriteAllText(Path.Combine(r, UpdateInstallSite.CurrentPointerName), "app-1.0.0-beta.1");
        return baseDir;
    }

    /// <summary>The .msi and the .deb: a flat directory of files, nowhere to put a second version.</summary>
    private string FlatBaseDir(string name) => Make(name, "circuitRF");

    [Fact]
    public void MacBundle_IsDetectedByShape_AndTheProbedDirectoryIsTheBundlesPARENT()
    {
        InstallSite s = UpdateInstallSite.DetectFrom(MacBundleBaseDir());

        Assert.Equal(InstallShape.MacOsBundle, s.Shape);
        Assert.EndsWith("circuitRF.app", s.Root);

        // The bundle is REPLACED, not written into, so what must be writable is what HOLDS it —
        // /Applications for an admin user and not for a standard one. Probing inside the bundle
        // would answer a different question and answer it wrongly.
        Assert.Equal(Path.GetDirectoryName(s.Root), s.ProbeDirectory);
        Assert.True(s.IsWritable);
        Assert.True(s.CanSelfUpdate);
    }

    [Fact]
    public void VersionedPointer_IsDetected_AndItsRootIsThePointersDirectory()
    {
        InstallSite s = UpdateInstallSite.DetectFrom(VersionedBaseDir());

        Assert.Equal(InstallShape.VersionedPointer, s.Shape);
        Assert.True(UpdateInstallSite.PointerExists(s.Root));
        Assert.True(s.CanSelfUpdate);
    }

    [Fact]
    public void FlatInstall_IsNeverSelfUpdating_EvenWhenItHappensToBeWritable()
    {
        InstallSite s = UpdateInstallSite.DetectFrom(FlatBaseDir("opt"));

        Assert.Equal(InstallShape.Flat, s.Shape);
        Assert.True(s.IsWritable);          // a temp dir is writable...
        Assert.False(s.CanSelfUpdate);      // ...and it is STILL notify-only: there is nowhere to
                                            // put a second version, so writability is not the point.
    }

    [Fact]
    public void AnAppDirectoryWithoutAPointer_IsFlat_NotVersioned()
    {
        // The marker is what distinguishes the user-local channel; a bare `app-x` directory beside
        // no `current` is somebody else's layout.
        string baseDir = Make("Programs", "circuitRF", "app-1.0.0");
        Assert.Equal(InstallShape.Flat, UpdateInstallSite.DetectFrom(baseDir).Shape);
    }

    [Fact]
    public void ADanglingSymlinkPointer_StillCounts()
    {
        // A `current` symlink whose target has gone is still a pointer. File.Exists answers false for
        // one, which would silently demote a working Linux install to notify-only.
        if (OperatingSystem.IsWindows()) return;   // symlink creation needs privilege there

        string root = Make("share", "circuitRF");
        string baseDir = Make("share", "circuitRF", "app-2.0.0");
        File.CreateSymbolicLink(Path.Combine(root, UpdateInstallSite.CurrentPointerName),
                                Path.Combine(root, "app-does-not-exist"));

        Assert.True(UpdateInstallSite.PointerExists(root));
        Assert.Equal(InstallShape.VersionedPointer, UpdateInstallSite.DetectFrom(baseDir).Shape);
    }

    // ── read-only: the notify-only half ──────────────────────────────────────────────────────

    [Fact]
    public void AReadOnlyMacBundleContainer_IsNotifyOnly_AndTheProbeWritesNothing()
    {
        if (OperatingSystem.IsWindows()) return;   // chmod is not the access model there

        string baseDir = MacBundleBaseDir("ReadOnlyApplications");
        string container = Path.Combine(_tmp, "ReadOnlyApplications");

        string[] before = Directory.GetFileSystemEntries(container);
        MakeReadOnly(container);
        try
        {
            InstallSite s = UpdateInstallSite.DetectFrom(baseDir);

            Assert.False(s.IsWritable);
            Assert.False(s.CanSelfUpdate);
        }
        finally { MakeWritable(container); }

        // "Assert the writes-nothing property directly" — the probe leaves the directory byte-identical.
        Assert.Equal(before, Directory.GetFileSystemEntries(container));
    }

    [Fact]
    public void AReadOnlyVersionedRoot_IsNotifyOnly()
    {
        if (OperatingSystem.IsWindows()) return;

        string baseDir = VersionedBaseDir("ReadOnlyPrograms");
        string root = Path.Combine(_tmp, "ReadOnlyPrograms", "circuitRF");
        MakeReadOnly(root);
        try   { Assert.False(UpdateInstallSite.DetectFrom(baseDir).CanSelfUpdate); }
        finally { MakeWritable(root); }
    }

    [Fact]
    public void TheWritableProbe_LeavesNothingBehind_WhenItSucceeds()
    {
        string d = Make("probe-target");
        Assert.True(UpdateInstallSite.IsDirectoryWritable(d));
        Assert.Empty(Directory.GetFileSystemEntries(d));
    }

    [Fact]
    public void AMissingDirectory_IsNotWritable_RatherThanThrowing()
        => Assert.False(UpdateInstallSite.IsDirectoryWritable(Path.Combine(_tmp, "does-not-exist")));

    /// <summary>
    /// R-AU-1, asserted as a source property: policy has ONE predicate, and platform branches live
    /// only in the primitives that move bytes.
    /// </summary>
    [Fact]
    public void DetectionDoesNotSwitchOnTheOperatingSystem()
    {
        string src = SourceFile("src/Ui/Updates/UpdateInstallSite.cs");
        string code = StripComments(src);

        Assert.DoesNotContain("OperatingSystem.Is", code);
        Assert.DoesNotContain("RuntimeInformation.IsOSPlatform", code);
        Assert.DoesNotContain("OSPlatform.", code);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static void MakeReadOnly(string dir) => Chmod(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

    private static void MakeWritable(string dir) => Chmod(dir,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    private static void Chmod(string dir, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(dir, mode);
    }

    internal static string SourceFile(string repoRelative)
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, repoRelative);
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        throw new FileNotFoundException($"Could not find {repoRelative} above {AppContext.BaseDirectory}.");
    }

    /// <summary>
    /// Strips comments and string literals before a source scan. The H8 lesson: a scan that matches
    /// prose finds the requirement's own description and calls it a violation.
    /// </summary>
    internal static string StripComments(string source)
    {
        var sb = new System.Text.StringBuilder(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                sb.Append('\n');
            }
            else if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i++;
            }
            else if (source[i] == '"')
            {
                i++;
                while (i < source.Length && source[i] != '"')
                {
                    if (source[i] == '\\') i++;
                    i++;
                }
            }
            else sb.Append(source[i]);
        }
        return sb.ToString();
    }
}
