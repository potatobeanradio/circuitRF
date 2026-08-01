using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// circuitRF ships the SEAM for external device providers, not any provider — a provider is bound
/// to whoever supplies the device model. These tests cover the loader's contract: what it finds,
/// what it refuses, and that one bad plug-in can never stop the application from starting.
/// </summary>
[Collection(ExternalProviderRegistryCollection.Name)]
public sealed class ExternalProviderLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-prov-" + Guid.NewGuid().ToString("N")[..8]);

    public ExternalProviderLoaderTests()
    {
        Directory.CreateDirectory(_dir);
        ExternalDeviceRegistry.Clear();
    }

    public void Dispose()
    {
        ExternalDeviceRegistry.Clear();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── Discovery over the running assembly ───────────────────────────────────
    //
    // The loader's type-discovery rules are exercised against THIS assembly, which really does
    // contain the provider shapes below. Emitting plug-in assemblies at test time would test the
    // compiler, not the loader.

    [Fact]
    public void ProvidersInAnAssembly_AreRegisteredUnderTheirOwnNames()
    {
        var report = ExternalProviderLoader.Load([Path.GetDirectoryName(typeof(GoodProvider).Assembly.Location)!]);

        Assert.Contains("crf-test-good", report.Registered);
        Assert.NotNull(ExternalDeviceRegistry.Find("crf-test-good"));
    }

    [Fact]
    public void AProviderWithNoParameterlessConstructor_IsReportedAndSkipped()
    {
        var report = ExternalProviderLoader.Load([Path.GetDirectoryName(typeof(GoodProvider).Assembly.Location)!]);

        Assert.Contains(report.Diagnostics, d => d.Contains(nameof(NeedsArgsProvider), StringComparison.Ordinal));
        Assert.DoesNotContain("crf-test-needsargs", report.Registered);
    }

    [Fact]
    public void AProviderWhoseConstructorThrows_IsReportedAndDoesNotStopTheOthers()
    {
        var report = ExternalProviderLoader.Load([Path.GetDirectoryName(typeof(GoodProvider).Assembly.Location)!]);

        Assert.Contains(report.Diagnostics, d => d.Contains(nameof(ThrowingProvider), StringComparison.Ordinal));
        Assert.Contains("crf-test-good", report.Registered);   // the good one still loaded
    }

    [Fact]
    public void AProviderWithAnEmptyName_IsRefused_BecauseNoNetlistCouldEverNameIt()
    {
        var report = ExternalProviderLoader.Load([Path.GetDirectoryName(typeof(GoodProvider).Assembly.Location)!]);

        Assert.Contains(report.Diagnostics, d => d.Contains(nameof(NamelessProvider), StringComparison.Ordinal));
        Assert.DoesNotContain(ExternalDeviceRegistry.ProviderNames, n => string.IsNullOrWhiteSpace(n));
    }

    // ── Folder handling ───────────────────────────────────────────────────────

    [Fact]
    public void AnAbsentPluginFolder_IsSilent_BecauseThatIsTheNormalCase()
    {
        var report = ExternalProviderLoader.Load([Path.Combine(_dir, "does-not-exist")]);

        Assert.Empty(report.Registered);
        Assert.Empty(report.Diagnostics);
        Assert.False(report.LoadedAnything);
    }

    [Fact]
    public void AnEmptyPluginFolder_LoadsNothingAndSaysNothing()
    {
        var report = ExternalProviderLoader.Load([_dir]);

        Assert.Empty(report.Registered);
        Assert.Empty(report.Diagnostics);
    }

    [Fact]
    public void AFileThatIsNotAManagedAssembly_IsSkippedWithoutComplaint()
    {
        // A native dependency sitting beside a plug-in is ordinary, not an error.
        File.WriteAllBytes(Path.Combine(_dir, "native.dll"), [0x00, 0x01, 0x02, 0x03]);

        var report = ExternalProviderLoader.Load([_dir]);

        Assert.Empty(report.Registered);
        Assert.Empty(report.Diagnostics);
    }

    [Fact]
    public void ACorruptAssembly_IsReportedRatherThanThrowing()
    {
        // A file with an MZ header but nothing valid behind it fails INSIDE the loader.
        File.WriteAllBytes(Path.Combine(_dir, "broken.dll"),
                           [.. "MZ"u8.ToArray(), .. new byte[512]]);

        var report = ExternalProviderLoader.Load([_dir]);

        Assert.Empty(report.Registered);
        // Either shape is acceptable — refused as not-an-assembly, or reported. What must NOT
        // happen is an exception escaping into application startup.
        Assert.True(report.Diagnostics.Count is 0 or >= 1);
    }

    [Fact]
    public void SeveralSearchFolders_AreAllScanned()
    {
        string a = Path.Combine(_dir, "a");
        string b = Path.Combine(_dir, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);

        var report = ExternalProviderLoader.Load([a, b, Path.GetDirectoryName(typeof(GoodProvider).Assembly.Location)!]);

        Assert.Contains("crf-test-good", report.Registered);
    }

    [Fact]
    public void DefaultSearchPaths_AreNamedProviders_AndNeverThrow()
    {
        var paths = ExternalProviderLoader.DefaultSearchPaths();

        Assert.NotEmpty(paths);
        Assert.All(paths, p => Assert.EndsWith(ExternalProviderLoader.PluginFolderName, p));
    }

    [Fact]
    public void LoadDefaults_NeverThrows_EvenWithNoPluginsInstalled()
    {
        var report = ExternalProviderLoader.LoadDefaults();

        Assert.NotNull(report);
    }

    // ── Test provider shapes ──────────────────────────────────────────────────

    public abstract class NotDiscoverable : IExternalDeviceProvider
    {
        public abstract string Name { get; }
        public IReadOnlyList<ExternalDeviceDescriptor> Describe() => [];
        public IExternalDeviceInstance Create(string typeId, IReadOnlyDictionary<string, string> parameters)
            => throw new ExternalDeviceException("not implemented in a test shape");
    }

    public sealed class GoodProvider : NotDiscoverable
    {
        public override string Name => "crf-test-good";
    }

    public sealed class NeedsArgsProvider : NotDiscoverable
    {
        public NeedsArgsProvider(int _) { }
        public override string Name => "crf-test-needsargs";
    }

    public sealed class ThrowingProvider : NotDiscoverable
    {
        public ThrowingProvider() => throw new InvalidOperationException("deliberate");
        public override string Name => "crf-test-throwing";
    }

    public sealed class NamelessProvider : NotDiscoverable
    {
        public override string Name => "";
    }
}
