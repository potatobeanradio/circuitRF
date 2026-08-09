using System;
using System.Collections.Generic;
using System.IO;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// Evaluating one instance with a model library other than the kit's own — which is what makes two
/// revisions of a library comparable side by side in one schematic.
/// </summary>
public sealed class ModelLibraryOverrideTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-lib-" + Guid.NewGuid().ToString("N")[..8]);
    private string KitDir => Path.Combine(_root, "SampleKit");

    public ModelLibraryOverrideTests()
    {
        Directory.CreateDirectory(KitDir);
        File.WriteAllText(Path.Combine(KitDir, "worker"), "");
        File.WriteAllText(Path.Combine(KitDir, "models.so"), "");
        File.WriteAllText(Path.Combine(KitDir, DeviceWorkerManifest.FileName), """
            { "workers": [ { "platform": "any", "command": "worker",
                             "arguments": ["--quiet", "models.so"] } ] }
            """);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private (string Name, IReadOnlyList<string> Arguments) Launched(string providerName)
    {
        string capturedName = "";
        IReadOnlyList<string> capturedArgs = [];

        var resolver = new DeviceWorkerProviderResolver([_root],
            (name, command, arguments) => { capturedName = name; capturedArgs = arguments; return null!; });

        resolver.Resolve(providerName);
        return (capturedName, capturedArgs);
    }

    [Fact]
    public void WithNoOverride_TheKitsOwnLibraryIsUsed()
    {
        var (name, args) = Launched("SampleKit");

        Assert.Equal("SampleKit", name);
        Assert.Contains(Path.Combine(KitDir, "models.so"), args);
    }

    [Fact]
    public void AChosenLibrary_ReplacesTheKitsOwnInTheCommand()
    {
        var (_, args) = Launched(
            DeviceWorkerProviderResolver.ComposeOverride("SampleKit", "/elsewhere/other.so"));

        Assert.Contains("/elsewhere/other.so", args);
        Assert.DoesNotContain(Path.Combine(KitDir, "models.so"), args);
        Assert.Contains("--quiet", args);          // every other argument is left as the kit wrote it
    }

    /// <summary>
    /// A COMPILED VERILOG-A ARTEFACT COUNTS AS A MODEL LIBRARY, which is what lets one provider serve
    /// a kit with a different <c>.osdi</c> per model without a second routing mechanism. It genuinely
    /// is one — the loader's own format under another extension — and it is exactly "which model
    /// library the worker should load", the case this substitution was built for.
    /// </summary>
    [Fact]
    public void AnOsdiArtefact_IsSubstitutedLikeAnyOtherModelLibrary()
    {
        File.WriteAllText(Path.Combine(KitDir, DeviceWorkerManifest.FileName), """
            { "workers": [ { "platform": "any", "command": "worker",
                             "arguments": ["default.osdi", "--quiet"] } ] }
            """);

        var (_, args) = Launched(
            DeviceWorkerProviderResolver.ComposeOverride("SampleKit", "/models/mdla.osdi"));

        Assert.Contains("/models/mdla.osdi", args);
        Assert.DoesNotContain("default.osdi", args);
        Assert.Contains("--quiet", args);
    }

    [Fact]
    public void TwoInstancesNamingDifferentLibraries_AreTwoProviders()
    {
        // They travel in the provider name because that is what the registry keys on: sharing one
        // provider would silently evaluate the second with the first's models.
        var a = Launched(DeviceWorkerProviderResolver.ComposeOverride("SampleKit", "/a.so")).Name;
        var b = Launched(DeviceWorkerProviderResolver.ComposeOverride("SampleKit", "/b.so")).Name;

        Assert.NotEqual(a, b);
        Assert.StartsWith("SampleKit", a);
    }

    [Fact]
    public void AKitWhoseCommandNamesNoLibrary_SaysSoRatherThanGuessingWhereItGoes()
    {
        // Appending would hand the worker two libraries and let it decide — the kind of guess that
        // produces an answer from the wrong models.
        File.WriteAllText(Path.Combine(KitDir, DeviceWorkerManifest.FileName), """
            { "workers": [ { "platform": "any", "command": "worker", "arguments": ["--quiet"] } ] }
            """);

        var ex = Assert.Throws<ExternalDeviceException>(() => Launched(
            DeviceWorkerProviderResolver.ComposeOverride("SampleKit", "/elsewhere/other.so")));

        Assert.Contains("no model library", ex.Message);
    }

    [Theory]
    [InlineData("SampleKit", "SampleKit", null)]
    [InlineData("SampleKit|/x.so", "SampleKit", "/x.so")]
    public void TheProviderNameSplitsBackIntoItsTwoParts(string composed, string kit, string? library)
    {
        var (k, l) = DeviceWorkerProviderResolver.SplitOverride(composed);

        Assert.Equal(kit, k);
        Assert.Equal(library, l);
    }

    [Fact]
    public void ComposingWithNoLibrary_LeavesTheNameAlone()
        => Assert.Equal("SampleKit", DeviceWorkerProviderResolver.ComposeOverride("SampleKit", ""));
}
