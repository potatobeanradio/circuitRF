using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CircuitRF.Design;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Em3d.Install;
using CircuitRF.Design.Layout.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-25 §6 — uninstall. Every home here is a FAKE: a directory with an install record and a
//  stub program, written into this class's own temporary root, so nothing is installed or downloaded and
//  nothing outside the temporary directory is ever removed. Gate 6's packaging half is in
//  PackagingScriptTests.AnUpgradeCannotRemoveASolver_AndTheAppsEntryRunsCircuitRfsOwnUninstall.
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class SolverUninstallTests : IDisposable
{
    private static readonly bool Windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private const string Stub = "crf-fake-gmsh";
    private static string StubFile => Windows ? Stub + ".cmd" : Stub;

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-uninstall-" + Guid.NewGuid().ToString("N")[..10]);
    private string Root => Path.Combine(_tmp, "solvers");

    public SolverUninstallTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ── 1. by record only; the confirmation is measured and says what it costs ──────────────────

    [Fact]
    public void Gate1_TheRecordedHomeIsRemoved_ASiblingItDoesNotNameSurvives_AndTheConfirmationIsMeasured()
    {
        var record = FakeHome(SolverTool.Gmsh, "4.15.2", padBytes: 40_000, elapsedSeconds: 3120);
        string sibling = Path.Combine(Root, "gmsh", "notes");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "keep.txt"), "not circuitRF's to remove");
        var older = FakeHome(SolverTool.Gmsh, "4.13.1");

        var u = Uninstaller();
        // Two versions and none named: refused, listing both, rather than guessing.
        var ambiguous = u.PlanOne(SolverTool.Gmsh);
        Assert.Contains("4.15.2", ambiguous.Refusal);
        Assert.Contains("4.13.1", ambiguous.Refusal);
        // R-em3d25-3: the older one is listed, measured, never removed by itself.
        Assert.Equal(["4.13.1"], u.Superseded(SolverTool.Gmsh, "4.15.2").Select(o => o.Record.Version));

        var plan = u.PlanOne(SolverTool.Gmsh, "4.15.2");
        Assert.True(plan.CanProceed, plan.Refusal);
        Assert.True(plan.TotalBytes >= 40_000, "the size was not measured from the disk");
        Assert.Contains(SolverUninstaller.Size(plan.TotalBytes), plan.Confirmation);
        Assert.Contains("permanent", plan.Confirmation);
        Assert.Contains("52 minutes", plan.Confirmation);           // the install's own measured time
        Assert.Contains("documents are not touched", plan.Confirmation);

        var outcome = u.Remove(plan);
        Assert.Equal(RemovalStatus.Removed, outcome.Status);
        Assert.False(Directory.Exists(record.Home));
        Assert.False(Directory.Exists(record.Home + SolverHomes.RemovingSuffix));
        Assert.True(File.Exists(Path.Combine(sibling, "keep.txt")));
        Assert.True(Directory.Exists(older.Home));
    }

    // ── 2. not ours ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_AToolFoundOnPathHasNoUninstall_AndRemovingItIsRefusedNamingItsPath()
    {
        string bin = Path.Combine(_tmp, "user-bin");
        Directory.CreateDirectory(bin);
        string program = Script(Path.Combine(bin, StubFile), "4.15.2");
        var discovery = IsolatedDiscovery(path: bin);

        var found = discovery.Find(out _);
        Assert.Equal(SolverHowFound.Path, found!.HowFound);
        Assert.False(SolverStatus.Of(SolverTool.Gmsh, discovery).InstalledByCircuitRf);

        var u = Uninstaller(discovery);
        Assert.Empty(u.Installed(SolverTool.Gmsh));
        var plan = u.PlanOne(SolverTool.Gmsh);
        Assert.False(plan.CanProceed);
        Assert.Contains(program, plan.Refusal);
        Assert.Contains("did not install", plan.Refusal);
        Assert.Equal(RemovalStatus.Refused, u.Remove(plan).Status);
        Assert.True(File.Exists(program));
    }

    // ── 3. in use ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_AHeldHomeIsRefusedNamingItsHolder_AndADeadProcessesLockIsIgnored()
    {
        var record = FakeHome(SolverTool.Gmsh, "4.15.2");
        var u = Uninstaller();

        // Held the way Em3dRunService holds it: by the program's path, mapped to its home.
        using (SolverInUse.HoldPrograms([record.Program, "/usr/bin/not-ours"], "the 3D EM run of 'filter'"))
        {
            Assert.True(File.Exists(Path.Combine(record.Home, SolverInUse.LockPrefix + Environment.ProcessId)));
            var refused = u.Remove(u.PlanOne(SolverTool.Gmsh));
            Assert.Equal(RemovalStatus.Refused, refused.Status);
            Assert.Contains("the 3D EM run of 'filter'", refused.Report);
            Assert.True(File.Exists(record.Program));
        }
        Assert.False(File.Exists(Path.Combine(record.Home, SolverInUse.LockPrefix + Environment.ProcessId)));

        // Another process's lock, whose process is gone: stale, ignored and removed.
        File.WriteAllText(Path.Combine(record.Home, SolverInUse.LockPrefix + DeadPid()), "the 3D EM run of 'crashed'\n");
        Assert.Equal(RemovalStatus.Removed, u.Remove(u.PlanOne(SolverTool.Gmsh)).Status);
        Assert.False(Directory.Exists(record.Home));
    }

    // ── 4 + 9. remove all: all or nothing, and documents untouched ──────────────────────────────

    [Fact]
    public void Gate4And9_RemoveAllIsAllOrNothing_AndLeavesAWorkspacesRunDirectoryByteIdentical()
    {
        var gmsh   = FakeHome(SolverTool.Gmsh, "4.15.2");
        var palace = FakeHome(SolverTool.Palace, "0.18.1");
        string workspace = Path.Combine(_tmp, "ws");
        string run = Path.Combine(workspace, "results", "via.palace.run");
        Directory.CreateDirectory(Path.Combine(run, "mesh"));
        File.WriteAllText(Path.Combine(workspace, ".cws"), "{}");
        File.WriteAllText(Path.Combine(run, "palace.json"), "{\"Problem\":{}}");
        File.WriteAllBytes(Path.Combine(run, "mesh", "via.msh"), new byte[5000]);
        string before = Fingerprint(workspace);

        var u = Uninstaller();
        using (SolverInUse.HoldHome(palace.Home, "an install of Palace"))
        {
            var refused = u.Remove(u.PlanAll());
            Assert.Equal(RemovalStatus.Refused, refused.Status);
            Assert.Contains("an install of Palace", refused.Report);
            Assert.True(Directory.Exists(gmsh.Home), "remove-all removed one home while refusing another");
            Assert.True(Directory.Exists(palace.Home));
        }

        var plan = u.PlanAll();
        Assert.Equal(2, plan.Homes.Count);
        Assert.Equal(RemovalStatus.Removed, u.Remove(plan).Status);
        Assert.False(Directory.Exists(gmsh.Home));
        Assert.False(Directory.Exists(palace.Home));
        Assert.Equal(before, Fingerprint(workspace));
    }

    // ── 5. interrupted delete ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate5_ADeleteThatStopsPartWayLeavesNoDiscoveryLocation_AndTheNextAttemptFinishesIt()
    {
        var record = FakeHome(SolverTool.Gmsh, "4.15.2");
        string held = Path.Combine(record.Home, "lib", "held.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(held)!);
        File.WriteAllText(held, "open on Windows");
        var discovery = IsolatedDiscovery(path: null);
        Assert.Equal(SolverHowFound.Installed, discovery.Find(out _)!.HowFound);

        var failing = new SolverUninstaller([Root]) { Discovery = _ => discovery, DeleteFile = f =>
        {
            if (f.EndsWith("held.dll", StringComparison.Ordinal)) throw new IOException("in use");
            File.Delete(f);
        } };
        var first = failing.Remove(failing.PlanOne(SolverTool.Gmsh));
        Assert.Equal(RemovalStatus.Incomplete, first.Status);
        Assert.Contains(Path.Combine(record.Home + SolverHomes.RemovingSuffix, "lib", "held.dll"), first.NotRemoved);
        Assert.Contains("held.dll", first.Report);
        Assert.Null(discovery.Find(out _));                              // nothing a run would find
        Assert.Empty(failing.Installed(SolverTool.Gmsh));

        var u = Uninstaller(discovery);
        var next = u.PlanOne(SolverTool.Gmsh);
        Assert.True(next.CanProceed, next.Refusal);
        Assert.Single(next.Leftovers);
        Assert.Equal(RemovalStatus.Removed, u.Remove(next).Status);
        Assert.False(Directory.Exists(record.Home + SolverHomes.RemovingSuffix));
    }

    // ── 6. the upgrade paths never reach it (source half) ───────────────────────────────────────

    [Fact]
    public void Gate6_NothingInTheUpdaterOrTheBundleExchangeCallsTheUninstaller()
    {
        string updates = Path.Combine(RepoRoot(), "src", "Ui", "Updates");
        var files = Directory.EnumerateFiles(updates, "*.cs", SearchOption.AllDirectories).ToList();
        Assert.True(files.Count > 10, "src/Ui/Updates was not found");
        foreach (string file in files)
        {
            string code = StripComments(File.ReadAllText(file));
            foreach (string forbidden in new[] { "SolverUninstaller", "AppUninstall", "UninstallCircuitRf", "SolverHomes" })
                Assert.False(code.Contains(forbidden, StringComparison.Ordinal), $"{Path.GetFileName(file)} references {forbidden}");
        }
    }

    // ── 7. leftovers reused ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_AHomePresentBeforeStartupIsFoundAsInstalled_WithItsUninstall()
    {
        var record = FakeHome(SolverTool.Gmsh, "4.15.2");   // as a Trash or package-manager removal leaves it
        var discovery = IsolatedDiscovery(path: null);

        var status = SolverStatus.Of(SolverTool.Gmsh, discovery);
        Assert.True(status.InstalledByCircuitRf);
        Assert.Equal(record.Program, status.Found!.Path);
        Assert.False(status.OfferInstall);
        Assert.Equal(record.Home, Assert.Single(Uninstaller(discovery).Installed(SolverTool.Gmsh)).Home);
    }

    // ── 8. headless consent ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_SolverRemoveWithoutYesExitsOneAndRemovesNothing_AndWithYesRemovesIt()
    {
        string state = Path.Combine(_tmp, "state");
        var record = FakeHome(SolverTool.Gmsh, "4.15.2", root: Path.Combine(state, "solvers"));

        var declined = RunCli(state, "solver", "remove", "gmsh");
        Assert.Equal(1, declined.ExitCode);
        Assert.Contains(record.Home, declined.StdErr);
        Assert.Contains("nothing was removed", declined.StdErr);
        Assert.Contains("--yes", declined.StdErr);
        Assert.Equal("", declined.StdOut);
        Assert.True(File.Exists(record.Program));

        var removed = RunCli(state, "solver", "remove", "gmsh", "--yes");
        Assert.True(removed.ExitCode == 0, removed.StdErr);
        Assert.False(Directory.Exists(record.Home));
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    private SolverUninstaller Uninstaller(SolverDiscovery? discovery = null)
    {
        var d = discovery ?? IsolatedDiscovery(path: null);
        return new SolverUninstaller([Root]) { Discovery = _ => d };
    }

    /// <summary>A published home as the installer leaves one: a stub program and its record.</summary>
    private InstallRecord FakeHome(SolverTool tool, string version, int padBytes = 0, double elapsedSeconds = 0, string? root = null)
    {
        string home = SolverHomes.Home(root ?? Root, tool, version);
        string bin = Path.Combine(home, "bin");
        Directory.CreateDirectory(bin);
        string program = Script(Path.Combine(bin, tool == SolverTool.Gmsh ? StubFile : "palace"), version);
        if (padBytes > 0) File.WriteAllBytes(Path.Combine(home, "padding.bin"), new byte[padBytes]);
        var record = new InstallRecord
        {
            Tool = tool, Version = version, Recipe = $"{SolverHomes.ToolId(tool)}-{version}-test", Home = home, Program = program,
            Identified = version, InstalledAt = DateTimeOffset.Now, SizeBytes = padBytes, ElapsedSeconds = elapsedSeconds,
        };
        record.Write(Path.Combine(home, SolverHomes.RecordFile));
        return record;
    }

    /// <summary>A Gmsh discovery that sees this test's install root and, when given, one PATH directory.</summary>
    private SolverDiscovery IsolatedDiscovery(string? path)
    {
        var d = SolverDiscovery.Create(SolverTool.Gmsh);
        d.PreferredCommand  = () => null;
        d.ReadEnvironment   = name => name == "PATHEXT" ? ".cmd" : name == "PATH" ? path : null;
        d.CandidateCommands = [Stub];
        d.SearchDirectories = [];
        d.SpackRoots        = [];
        d.CondaBases        = [];
        d.InstallRoots      = [Root];
        d.CacheDirectory    = Path.Combine(_tmp, "cache");
        return d;
    }

    private static string Script(string path, string version)
    {
        string body = Windows ? $"@echo off\r\necho {version}\r\n" : $"#!/bin/sh\necho '{version}'\n";
        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>The id of a process that has exited.</summary>
    private static int DeadPid()
    {
        using var p = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true, UseShellExecute = false })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.Id;
    }

    private static string Fingerprint(string dir)
        => string.Join("\n", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                                      .Select(f => Path.GetRelativePath(dir, f) + " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))));

    private static string StripComments(string code)
    {
        code = Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(code, @"//[^\n]*", "");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(string stateDir, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.Environment[UserStateDirectory.EnvironmentVariable] = stateDir;
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(SolverUninstallTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }
}
