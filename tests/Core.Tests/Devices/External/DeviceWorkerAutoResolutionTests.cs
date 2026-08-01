using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// Getting from "the user imported a kit" to "pressing Run works", with nothing configured in
/// between.
///
/// <para>The path is: a netlist names a provider → the registry has never heard of it → a resolver
/// finds the manifest that came with the kit → its worker is started. These tests cover what the
/// resolver picks and what it refuses, and that a provider is started only when something asks for
/// it, since starting one is expensive and most kits in a workspace are unused by any one design.</para>
/// </summary>
[Collection(ExternalProviderRegistryCollection.Name)]
public sealed class DeviceWorkerAutoResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-kits-" + Guid.NewGuid().ToString("N")[..8]);

    public DeviceWorkerAutoResolutionTests()
    {
        Directory.CreateDirectory(_root);
        ExternalDeviceRegistry.Clear();
    }

    public void Dispose()
    {
        ExternalDeviceRegistry.Clear();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Records what it was asked to start instead of starting it.</summary>
    private sealed class RecordingLauncher
    {
        public readonly List<(string Name, string Command, string[] Arguments)> Launches = [];

        public IExternalDeviceProvider Launch(string name, string command, IReadOnlyList<string> arguments)
        {
            Launches.Add((name, command, arguments.ToArray()));
            return new DeviceWorkerProvider(name, new FakeDeviceWorker());
        }
    }

    private string WriteKit(string folder, string manifestJson, params string[] filesToCreate)
    {
        string dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, DeviceWorkerManifest.FileName), manifestJson);

        foreach (string file in filesToCreate)
        {
            string full = Path.Combine(dir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "");
        }

        return dir;
    }

    private static string ThisPlatform => DeviceWorkerManifest.CurrentRuntimeIdentifier();

    // ── the path that has to work ─────────────────────────────────────────────

    [Fact]
    public void AKitThatCameWithAManifest_NeedsNothingConfigured()
    {
        WriteKit("AcmeKit", $$"""
        { "provider": "AcmeKit",
          "workers": [ { "platform": "{{ThisPlatform}}", "command": "worker", "arguments": ["models/lib.bin"] } ] }
        """, "worker", "models/lib.bin");

        var launcher = new RecordingLauncher();
        ExternalDeviceRegistry.AddResolver(new DeviceWorkerProviderResolver([_root], launcher.Launch));

        var provider = ExternalDeviceRegistry.Require("AcmeKit");

        Assert.Equal("AcmeKit", provider.Name);
        Assert.Single(launcher.Launches);
    }

    [Fact]
    public void TheWorkerAndItsFilesAreFoundRelativeToTheKit()
    {
        // A kit must survive being copied elsewhere, and the same workspace being opened on another
        // machine. That only holds if nothing in the manifest is an absolute path.
        string dir = WriteKit("AcmeKit", $$"""
        { "workers": [ { "platform": "{{ThisPlatform}}", "command": "worker", "arguments": ["models/lib.bin", "--fast"] } ] }
        """, "worker", "models/lib.bin");

        var launcher = new RecordingLauncher();
        new DeviceWorkerProviderResolver([_root], launcher.Launch).Resolve("AcmeKit");

        var (_, command, arguments) = launcher.Launches.Single();
        Assert.Equal(Path.Combine(dir, "worker"), command);
        Assert.Equal(Path.Combine(dir, "models", "lib.bin"), arguments[0]);

        // An argument that is not a file in the kit is left exactly as written.
        Assert.Equal("--fast", arguments[1]);
    }

    [Fact]
    public void AWorkerOnTheSystemPath_IsLeftForTheSystemToFind()
    {
        WriteKit("AcmeKit", $$"""
        { "workers": [ { "platform": "{{ThisPlatform}}", "command": "some-installed-worker" } ] }
        """);

        var launcher = new RecordingLauncher();
        new DeviceWorkerProviderResolver([_root], launcher.Launch).Resolve("AcmeKit");

        Assert.Equal("some-installed-worker", launcher.Launches.Single().Command);
    }

    [Fact]
    public void AManifestNamingNoProvider_TakesTheNameOfItsKitFolder()
    {
        WriteKit("AcmeKit", $$"""
        { "workers": [ { "platform": "{{ThisPlatform}}", "command": "worker" } ] }
        """, "worker");

        var launcher = new RecordingLauncher();
        var resolved = new DeviceWorkerProviderResolver([_root], launcher.Launch).Resolve("AcmeKit");

        Assert.NotNull(resolved);
    }

    [Fact]
    public void AKitInstalledUnderADifferentFolderName_IsStillFoundByTheNameItDeclares()
    {
        // Folder names are sanitised on install, so the folder and the provider name can differ.
        WriteKit("acme_kit_v2", $$"""
        { "provider": "Acme Kit v2",
          "workers": [ { "platform": "{{ThisPlatform}}", "command": "worker" } ] }
        """, "worker");

        var launcher = new RecordingLauncher();
        var resolved = new DeviceWorkerProviderResolver([_root], launcher.Launch).Resolve("Acme Kit v2");

        Assert.NotNull(resolved);
    }

    [Fact]
    public void ProviderNamesAreMatchedWithoutRegardToCase()
    {
        WriteKit("AcmeKit", $$"""
        { "provider": "AcmeKit", "workers": [ { "platform": "{{ThisPlatform}}", "command": "worker" } ] }
        """, "worker");

        var launcher = new RecordingLauncher();

        Assert.NotNull(new DeviceWorkerProviderResolver([_root], launcher.Launch).Resolve("acmekit"));
    }

    // ── not starting things unnecessarily ─────────────────────────────────────

    [Fact]
    public void NoWorkerStarts_UntilADesignAsksForOne()
    {
        // A workspace can hold many kits. Starting a process for each one at open would cost far
        // more than every design that uses none of them.
        WriteKit("AcmeKit", $$"""
        { "workers": [ { "platform": "{{ThisPlatform}}", "command": "worker" } ] }
        """, "worker");

        var launcher = new RecordingLauncher();
        ExternalDeviceRegistry.AddResolver(new DeviceWorkerProviderResolver([_root], launcher.Launch));

        Assert.Empty(launcher.Launches);

        ExternalDeviceRegistry.Find("AcmeKit");

        Assert.Single(launcher.Launches);
    }

    [Fact]
    public void AResolvedProviderIsKept_SoOneWorkerServesEveryDeviceInADesign()
    {
        WriteKit("AcmeKit", $$"""
        { "workers": [ { "platform": "{{ThisPlatform}}", "command": "worker" } ] }
        """, "worker");

        var launcher = new RecordingLauncher();
        ExternalDeviceRegistry.AddResolver(new DeviceWorkerProviderResolver([_root], launcher.Launch));

        var first  = ExternalDeviceRegistry.Require("AcmeKit");
        var second = ExternalDeviceRegistry.Require("AcmeKit");

        Assert.Same(first, second);
        Assert.Single(launcher.Launches);
    }

    [Fact]
    public void AProviderTheRegistryStarted_IsEndedWhenTheRegistryIsCleared()
    {
        // Otherwise closing a workspace leaves worker processes running with nothing to talk to.
        var worker = new FakeDeviceWorker();
        ExternalDeviceRegistry.AddResolver(new StubResolver("AcmeKit", () => new DeviceWorkerProvider("AcmeKit", worker)));

        ExternalDeviceRegistry.Require("AcmeKit");
        Assert.False(worker.Disposed);

        ExternalDeviceRegistry.Clear();

        Assert.True(worker.Disposed);
    }

    [Fact]
    public void AProviderTheHostRegistered_IsLeftAloneOnClear()
    {
        // The host owns what the host created; ending it here would be taking something away from
        // whoever is still holding it.
        var worker = new FakeDeviceWorker();
        var provider = new DeviceWorkerProvider("host-owned", worker);
        ExternalDeviceRegistry.Register(provider);

        ExternalDeviceRegistry.Clear();

        Assert.False(worker.Disposed);
        provider.Dispose();
    }

    // ── refusals that have to be readable ─────────────────────────────────────

    [Fact]
    public void AKitBuiltForOtherPlatforms_SaysSoAndSaysWhichOnes()
    {
        // An ordinary situation, not a corruption: a kit for one operating system opened on another.
        WriteKit("AcmeKit", """
        { "workers": [ { "platform": "somethingelse-x64", "command": "worker" } ] }
        """, "worker");

        var ex = Assert.Throws<ExternalDeviceException>(
            () => new DeviceWorkerProviderResolver([_root]).Resolve("AcmeKit"));

        Assert.Contains("AcmeKit", ex.Message);
        Assert.Contains("somethingelse-x64", ex.Message);
        Assert.Contains(ThisPlatform, ex.Message);
    }

    [Fact]
    public void AKitWhoseManifestIsBroken_IsReportedAgainstThatKit()
    {
        WriteKit("AcmeKit", "{ this is not json");

        var ex = Assert.Throws<ExternalDeviceException>(
            () => new DeviceWorkerProviderResolver([_root]).Resolve("AcmeKit"));

        Assert.Contains("AcmeKit", ex.Message);
    }

    [Fact]
    public void ABrokenManifestBelongingToAnotherKit_DoesNotBreakThisLookup()
    {
        WriteKit("BrokenKit", "{ not json at all");
        WriteKit("AcmeKit", $$"""
        { "workers": [ { "platform": "{{ThisPlatform}}", "command": "worker" } ] }
        """, "worker");

        var launcher = new RecordingLauncher();

        Assert.NotNull(new DeviceWorkerProviderResolver([_root], launcher.Launch).Resolve("AcmeKit"));
    }

    [Fact]
    public void AnUnknownKit_IsAbsenceRatherThanFailure()
    {
        WriteKit("AcmeKit", $$"""
        { "workers": [ { "platform": "{{ThisPlatform}}", "command": "worker" } ] }
        """, "worker");

        Assert.Null(new DeviceWorkerProviderResolver([_root]).Resolve("SomeOtherKit"));
    }

    [Fact]
    public void AMissingKitFolder_IsNotAnError()
    {
        // A workspace with no kits at all is the ordinary case.
        Assert.Null(new DeviceWorkerProviderResolver([Path.Combine(_root, "nope")]).Resolve("AcmeKit"));
    }

    [Fact]
    public void WhenNothingIsFound_TheErrorSaysWhereItLooked()
    {
        // Without this the user is told only that something is unavailable, with no way to act.
        ExternalDeviceRegistry.AddResolver(new DeviceWorkerProviderResolver([_root]));

        var ex = Assert.Throws<ExternalDeviceException>(() => ExternalDeviceRegistry.Require("AcmeKit"));

        Assert.Contains(_root, ex.Message);
        Assert.Contains(DeviceWorkerManifest.FileName, ex.Message);
    }

    private sealed class StubResolver(string name, Func<IExternalDeviceProvider> make) : IExternalProviderResolver
    {
        public string Describe => "stub";
        public IExternalDeviceProvider? Resolve(string requested)
            => string.Equals(requested, name, StringComparison.OrdinalIgnoreCase) ? make() : null;
    }
}
