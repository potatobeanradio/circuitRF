using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// Serialises every test class that mutates <c>ExternalDeviceRegistry</c>.
///
/// <para>The registry is process-wide static state. xUnit runs test classes in parallel, so two
/// classes each calling <c>Clear()</c> around their own work will occasionally clear the other's —
/// a failure that appears at random, in a class that did nothing wrong, and only when the machine
/// is loaded enough to interleave them. Sharing one collection makes them run one at a time.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExternalProviderRegistryCollection
{
    public const string Name = "external-provider-registry";
}
