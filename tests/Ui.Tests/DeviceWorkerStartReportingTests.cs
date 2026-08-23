using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Reported: opening a workspace printed "Starting the worker that evaluates 'osdi' (osdi-worker).
/// The first device waits for it to load its models; the rest of the run does not." twice, with no
/// run in sight.
///
/// <para>Both lines were true starts and neither was a run. <c>OsdiModelDiscovery.Find</c> starts one
/// worker PER compiled model in the kit, asks what it implements, and disposes it — so the count
/// follows how many artefacts the kit ships, and every one of them borrowed a sentence written for
/// the worker a run actually waits on. The event now says which kind it is, and the workspace says
/// nothing for a scan.</para>
/// </summary>
public sealed class DeviceWorkerStartReportingTests
{
    [Fact]
    public void AStartRaisedForAScan_IsMarkedAsOne()
    {
        var scan = new DeviceWorkerStart("osdi", "osdi-worker", ForDiscovery: true);
        var run  = new DeviceWorkerStart("osdi", "osdi-worker");

        Assert.True(scan.ForDiscovery);

        // The default is the run, so every existing caller keeps meaning what it meant.
        Assert.False(run.ForDiscovery);
    }

    [Fact]
    public void TheScanAndTheRun_AreOtherwiseIndistinguishable()
    {
        // Why the flag had to exist at all: the provider name and the program are identical, so
        // nothing a host could inspect would have told the two apart.
        var scan = new DeviceWorkerStart("osdi", "osdi-worker", ForDiscovery: true);
        var run  = new DeviceWorkerStart("osdi", "osdi-worker");

        Assert.Equal(run.Provider, scan.Provider);
        Assert.Equal(run.Command,  scan.Command);
        Assert.NotEqual(run, scan);
    }
}
