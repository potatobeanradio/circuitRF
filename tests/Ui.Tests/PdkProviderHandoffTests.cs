using System;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The hand-off that makes a kit part simulable with nothing configured: importing a kit settles how
/// its devices are evaluated, and provider resolution answers from that.
///
/// <para>Without this the user imports a kit, places a part, presses Run, and is told the provider
/// is unavailable — with the fix being to hand-write a file they have no reason to know about.</para>
///
/// <para>Nothing is written into the workspace: the settled settings are held in memory and recorded
/// in <c>.cws</c> by the caller. What is asserted here is what they RESOLVE TO, which is the property
/// that actually decides whether Run works.</para>
/// </summary>
[Collection(PdkToolsDirectoryCollection.Name)]
public sealed class PdkProviderHandoffTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-handoff-" + Guid.NewGuid().ToString("N")[..8]);

    private string KitDir       => Path.Combine(_root, "kit");
    private string WorkspaceDir => Path.Combine(_root, "ws");

    public PdkProviderHandoffTests()
    {
        Directory.CreateDirectory(KitDir);
        Directory.CreateDirectory(WorkspaceDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void WriteKitManifest(string json)
        => File.WriteAllText(Path.Combine(KitDir, DeviceWorkerManifest.FileName), json);

    private PdkPartInstaller.InstallOutcome Install()
        => PdkPartInstaller.Install(
               new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" });

    /// <summary>The settled settings as a manifest — what provider resolution is handed.</summary>
    private DeviceWorkerManifest SettledManifest(PdkPartInstaller.InstallOutcome outcome)
        => Assert.IsType<DeviceWorkerManifest>(
               PdkPartInstaller.ManifestFrom(outcome.Settings, KitDir, outcome.KitName));

    // ── the hand-off ──────────────────────────────────────────────────────────

    [Fact]
    public void AKitThatDescribesHowToSimulateIt_HasThatCarriedIntoTheWorkspace()
    {
        WriteKitManifest("""
        { "workers": [ { "platform": "any", "command": "worker", "arguments": ["models/lib.bin"] } ] }
        """);

        var outcome = Install();

        Assert.NotNull(outcome.Settings);
        Assert.Equal("SampleKit", SettledManifest(outcome).ProviderName);
        Assert.False(Directory.Exists(Path.Combine(WorkspaceDir, PdkPartInstaller.InstallFolderName)),
            "the import wrote into the workspace — a kit's files stay the kit's");
    }

    [Fact]
    public void TheSettledSettingsAnswerToTheKitName_WhateverTheOriginalCalledItself()
    {
        // Each installed cell records Provider = the kit name, so that is what a netlist asks for.
        // A copy keeping some other name leaves every step working and only Run failing.
        WriteKitManifest("""
        { "provider": "something-else", "workers": [ { "platform": "any", "command": "worker" } ] }
        """);

        Assert.Equal("SampleKit", SettledManifest(Install()).ProviderName);
    }

    [Fact]
    public void TheSettledSettingsStillReachTheWorkerAndModelFiles_WhichNeverLeftTheKit()
    {
        // Relative paths in a kit's own settings are relative to the KIT, and the settings no longer
        // sit in a folder of their own — so the kit is what they must resolve against.
        Directory.CreateDirectory(Path.Combine(KitDir, "models"));
        File.WriteAllText(Path.Combine(KitDir, "worker"), "");
        File.WriteAllText(Path.Combine(KitDir, "models", "lib.bin"), "");

        WriteKitManifest("""
        { "workers": [ { "platform": "any", "command": "worker", "arguments": ["models/lib.bin"] } ] }
        """);

        var manifest = SettledManifest(Install());
        var (command, arguments) = manifest.Resolve(manifest.Launches[0]);

        Assert.Equal(Path.Combine(KitDir, "worker"), command);
        Assert.Equal(Path.Combine(KitDir, "models", "lib.bin"), arguments[0]);
    }

    [Fact]
    public void SettingsTheWorkspaceRecorded_WinOverTheKitsOwn()
    {
        // The workspace's own record is the escape hatch: a kit's choice can be overridden for one
        // workspace without editing the kit, which is very often read-only.
        File.WriteAllText(Path.Combine(KitDir, "worker"), "");
        File.WriteAllText(Path.Combine(KitDir, "mine"), "");
        WriteKitManifest("""{ "workers": [ { "platform": "any", "command": "worker" } ] }""");

        var recorded = System.Text.Json.Nodes.JsonNode.Parse(
            """{ "provider": "SampleKit", "workers": [ { "platform": "any", "command": "mine" } ] }""");

        var outcome = PdkPartInstaller.Install(
            new PdkImportReport { RootPath = KitDir, KitName = "SampleKit" }, recorded);

        var manifest = SettledManifest(outcome);
        Assert.Equal(Path.Combine(KitDir, "mine"), manifest.Resolve(manifest.Launches[0]).Command);
    }

    [Fact]
    public void EveryPlatformTheKitCovers_SurvivesIntoTheSettledSettings()
    {
        // A workspace opened on another machine must still find that machine's worker.
        WriteKitManifest("""
        { "workers": [ { "platform": "linux-x64", "command": "w" },
                       { "platform": "win-x64",   "command": "w.exe" },
                       { "platform": "osx-arm64", "command": "w-vm" } ] }
        """);

        var platforms = SettledManifest(Install()).Launches.Select(l => l.Platform).ToArray();

        Assert.Equal(["linux-x64", "win-x64", "osx-arm64"], platforms);
    }

    // ── kits that describe nothing ────────────────────────────────────────────

    [Fact]
    public void AKitWithNoSuchDescription_ImportsSilently()
    {
        // The ordinary case. Its parts still place and draw; only simulating them needs a manifest,
        // so saying anything here would be noise on nearly every import.
        var outcome = Install();

        Assert.Null(outcome.Settings);
        Assert.DoesNotContain(outcome.Diagnostics, d => d.Contains("simulat", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AKitWhoseDescriptionIsBroken_SaysSoWithoutFailingTheImport()
    {
        // Everything else about the kit still works, so the import must not be lost over this.
        WriteKitManifest("{ not json at all");

        var outcome = Install();

        Assert.Contains(outcome.Diagnostics, d => d.Contains("simulate", StringComparison.OrdinalIgnoreCase));
    }

    // ── end to end ────────────────────────────────────────────────────────────

    [Fact]
    public void AfterImport_ResolutionFindsTheKitByTheNameItsPartsWereInstalledUnder()
    {
        // The netlist writes Provider=<kit name>. That name has to be the one resolution answers to,
        // or every step works and the last one fails.
        File.WriteAllText(Path.Combine(KitDir, "worker"), "");
        WriteKitManifest("""{ "workers": [ { "platform": "any", "command": "worker" } ] }""");

        var outcome = Install();

        string? started = null;

        // The shape the workspace registers: manifests already in hand, no folder to search.
        var resolver = new DeviceWorkerProviderResolver(
            [(outcome.KitName, SettledManifest(outcome))],
            (name, command, args) =>
            {
                started = command;
                return new DeviceWorkerProvider(name, new StubTransport());
            });

        var provider = resolver.Resolve("SampleKit");

        Assert.NotNull(provider);
        Assert.Equal("SampleKit", provider!.Name);
        Assert.Equal(Path.Combine(KitDir, "worker"), started);
    }

    /// <summary>A transport that is never spoken to — these tests stop at "which worker would start".</summary>
    private sealed class StubTransport : IDeviceWorkerTransport
    {
        public Stream Requests          => Stream.Null;
        public Stream Replies           => Stream.Null;
        public string Origin            => "stub";
        public bool   IsAlive           => true;
        public string RecentErrorOutput => "";
        public void   Dispose() { }
    }
}
