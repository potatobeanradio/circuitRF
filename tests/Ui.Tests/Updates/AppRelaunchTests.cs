using CircuitRF.Ui.Updates;

namespace CircuitRF.Ui.Tests.Updates;

/// <summary>
/// How an applied update starts the version it just installed (owner report, 2026-09-04).
///
/// <para>After an automatic update on macOS every workspace under <c>~/Documents</c> was refused with
/// the protected-folder diagnostic, and quitting and relaunching fixed it with nothing else changed.
/// The kernel recorded <c>System Policy: circuitRF(61022) deny(1) file-read-data
/// …/Documents/&lt;workspace&gt;</c>, and the session file — <c>session-…-194158-61022</c> against a
/// process <c>ps</c> reported as starting at 19:41:52 — carries the <c>execv</c> signature
/// <c>CrashReporter.IsOwnExecPredecessor</c> documents: same pid, later stamp. The updated session was
/// the exec'd image, and it kept a launch-time application attribution pointing at the bundle the
/// update had just exchanged.</para>
///
/// <para>These pin the parts that can be checked without launching an application: which executables
/// resolve to a bundle, and that the hand-over prefers Launch Services on macOS while falling back
/// rather than failing.</para>
/// </summary>
public sealed class AppRelaunchTests : IDisposable
{
    public void Dispose() => AppRelaunch.Launcher = null;

    [Fact]
    public void AMainExecutableInsideABundle_ResolvesToTheBundleRoot()
    {
        string exe = Path.Combine("/Applications", "circuitRF.app", "Contents", "MacOS", "circuitRF");

        Assert.Equal(Path.Combine("/Applications", "circuitRF.app"), AppRelaunch.BundleRootOf(exe));
    }

    /// <summary>
    /// The shape is matched structurally, not by looking for ".app" anywhere in the string. A
    /// versioned-pointer install is the live case: handing <c>open</c> something that is not a bundle
    /// produces a refusal the caller would then have to interpret, and there is a correct mechanism
    /// for that layout already.
    /// </summary>
    [Theory]
    [InlineData("/opt/circuitrf/app-1.0.0/circuitRF")]
    [InlineData("/Applications/circuitRF.app/Contents/Resources/circuitRF")]
    [InlineData("/Users/x/build/thing.app/circuitRF")]
    [InlineData("/usr/local/bin/circuitRF")]
    [InlineData("")]
    public void AnythingThatIsNotABundlesMainExecutable_ResolvesToNull(string exe)
        => Assert.Null(AppRelaunch.BundleRootOf(exe));

    /// <summary>
    /// The whole point: on macOS the successor is asked for by BUNDLE, so launchd spawns it and the
    /// new process is attributed to the application that is actually installed.
    /// </summary>
    [Fact]
    public void OnMacOs_TheRelaunchAsksLaunchServicesForTheBundle()
    {
        if (!OperatingSystem.IsMacOS()) return;

        string? asked = null;
        IReadOnlyList<string>? gotArgs = null;
        AppRelaunch.Launcher = (bundle, args) => { asked = bundle; gotArgs = args; return true; };

        bool ok = AppRelaunch.TryRelaunchBundle(
            "/Applications/circuitRF.app/Contents/MacOS/circuitRF", ["a.cws"]);

        Assert.True(ok);
        Assert.Equal("/Applications/circuitRF.app", asked);
        Assert.Equal(["a.cws"], gotArgs);
    }

    /// <summary>
    /// Linux keeps <c>execv</c>: it has no TCC to go stale, and the exec keeps the pid, the process
    /// clock and the parent's handle on this process. A relaunch route that quietly turned itself on
    /// there would change behaviour nothing asked to change.
    /// </summary>
    [Fact]
    public void OffMacOs_ThereIsNoLaunchServicesRoute()
    {
        if (OperatingSystem.IsMacOS()) return;

        AppRelaunch.Launcher = (_, _) => true;

        Assert.False(AppRelaunch.TryRelaunchBundle(
            "/Applications/circuitRF.app/Contents/MacOS/circuitRF", []));
    }

    /// <summary>
    /// A refused or unavailable Launch Services request must report false so the caller falls through
    /// to <c>execv</c>. An update that leaves a stale privacy attribution for one session is bad; one
    /// that leaves the user with no application at all is very much worse.
    /// </summary>
    [Fact]
    public void ALaunchThatIsRefused_ReportsFalseRatherThanClaimingSuccess()
    {
        if (!OperatingSystem.IsMacOS()) return;

        AppRelaunch.Launcher = (_, _) => false;
        Assert.False(AppRelaunch.TryRelaunchBundle(
            "/Applications/circuitRF.app/Contents/MacOS/circuitRF", []));

        AppRelaunch.Launcher = (_, _) => throw new InvalidOperationException("no Launch Services");
        Assert.False(AppRelaunch.TryRelaunchBundle(
            "/Applications/circuitRF.app/Contents/MacOS/circuitRF", []));
    }

    /// <summary>An executable that is not in a bundle never reaches the launcher at all.</summary>
    [Fact]
    public void ANonBundleExecutable_NeverReachesLaunchServices()
    {
        bool called = false;
        AppRelaunch.Launcher = (_, _) => { called = true; return true; };

        Assert.False(AppRelaunch.TryRelaunchBundle("/opt/circuitrf/app-1.0.0/circuitRF", []));
        Assert.False(called);
    }
}
