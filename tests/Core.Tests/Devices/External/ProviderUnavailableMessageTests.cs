using System;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// The message a user meets when a kit part will not simulate. It is the ONLY thing standing between
/// them and a dead end, so its content is pinned rather than left to drift.
///
/// <para><b>The failure it describes.</b> A kit's devices are usually compiled models, and the library
/// implementing them routinely ships as a separate package BESIDE the kit rather than inside it. Move
/// the kit — into a workspace, say — and nothing on disk connects the two any more. The message used to
/// report only that no provider answered to the name, which sends the user looking for a missing
/// registration when what is actually missing is a file.</para>
///
/// <para>The usual cause is named AS the usual cause, not as a diagnosis: this layer knows only that
/// nothing answered. Asserting "the library was not found" as fact would be over-claiming.</para>
/// </summary>
[Collection(ExternalProviderRegistryCollection.Name)]
public sealed class ProviderUnavailableMessageTests : IDisposable
{
    public ProviderUnavailableMessageTests() => ExternalDeviceRegistry.Clear();
    public void Dispose()                    => ExternalDeviceRegistry.Clear();

    /// <summary>The shape a workspace registers: manifests in hand, none of them settled.</summary>
    private static void RegisterEmptyResolver()
        => ExternalDeviceRegistry.AddResolver(
               new DeviceWorkerProviderResolver(Array.Empty<(string, DeviceWorkerManifest)>()));

    [Fact]
    public void AnUnavailableProvider_NamesTheCompiledModelAndItsMissingLibrary()
    {
        RegisterEmptyResolver();

        var ex = Assert.Throws<ExternalDeviceException>(() => ExternalDeviceRegistry.Require("SampleKit"));

        Assert.Contains("SampleKit",      ex.Message, StringComparison.Ordinal);
        Assert.Contains("compiled model", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("library that implements it was not found",
                        ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ItSaysWhatToDoAboutIt_NotOnlyWhatWentWrong()
    {
        // A message naming a problem with no route out of it is a dead end. Both routes are offered:
        // reference the package, or supply the settings by hand.
        RegisterEmptyResolver();

        var ex = Assert.Throws<ExternalDeviceException>(() => ExternalDeviceRegistry.Require("SampleKit"));

        Assert.Contains("Manage PDKs",                  ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(DeviceWorkerManifest.FileName,  ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEmptySearchIsDescribedByWhatItMeans_NotByWhatItIs()
    {
        // "no kit folders" is literally true and tells a user nothing. Why there are none is the part
        // worth saying.
        RegisterEmptyResolver();

        var ex = Assert.Throws<ExternalDeviceException>(() => ExternalDeviceRegistry.Require("SampleKit"));

        Assert.DoesNotContain("no kit folders", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("settled on a way to evaluate its devices",
                        ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithNoResolverAtAll_ItSaysThatInstead()
    {
        // A different situation with a different fix — nothing was ever wired up, as opposed to a kit
        // that was and came up empty. Conflating the two would send the user to the wrong place.
        var ex = Assert.Throws<ExternalDeviceException>(() => ExternalDeviceRegistry.Require("SampleKit"));

        Assert.Contains("no providers are registered", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("compiled model",        ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
