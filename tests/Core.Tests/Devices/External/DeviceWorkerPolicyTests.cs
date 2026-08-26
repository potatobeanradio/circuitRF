using System;
using System.Collections.Generic;
using System.IO;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// The consent gate for external device workers.
///
/// <para>A kit's <c>device-provider.json</c> names a <c>command</c> that resolves against the kit's
/// own folder, so a kit can ship an executable and circuitRF starts it. Its PCell generator scripts
/// have been gated behind a prompt since B6; the worker half was not gated at all until this
/// (security review, 2026-08-25).</para>
///
/// <para><b>The gate is process-wide static state</b>, exactly like <c>ExternalDeviceRegistry</c>, so
/// this class joins that collection rather than racing whichever other class is starting a worker.
/// Every case restores it in a <c>finally</c> — a test that leaves the gate refusing would fail the
/// end-to-end worker tests in a way that points nowhere near here.</para>
/// </summary>
[Collection(ExternalProviderRegistryCollection.Name)]
public sealed class DeviceWorkerPolicyTests : IDisposable
{
    public void Dispose() => DeviceWorkerPolicy.Gate = null;

    // ── the default ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>No policy installed means workers run.</b> src/Core cannot read AppPreferences, so the
    /// policy is a hook the application installs — and the CLI, the tools and every test never
    /// install one. If this ever answers otherwise, headless simulation of a kit part stops working
    /// with no setting anywhere that explains it.
    /// </summary>
    [Fact]
    public void WithNoPolicyInstalled_AWorkerMayStart()
    {
        DeviceWorkerPolicy.Gate = null;

        Assert.Null(DeviceWorkerPolicy.RefusalReason("a-kit"));
        Assert.True(DeviceWorkerPolicy.MayStart("a-kit"));
    }

    /// <summary>A gate that returns null or blank is an allow, not a refusal with an empty reason.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AGateThatSaysNothing_Allows(string? answer)
    {
        DeviceWorkerPolicy.Gate = _ => answer;

        Assert.True(DeviceWorkerPolicy.MayStart("a-kit"));
    }

    /// <summary>
    /// A policy that throws ALLOWS, deliberately. This is a user preference, not a trust anchor: the
    /// stated default is on, and turning a bug in the policy into a simulation that cannot run would
    /// be a worse failure than the one it guards against.
    /// </summary>
    [Fact]
    public void AGateThatThrows_Allows()
    {
        DeviceWorkerPolicy.Gate = _ => throw new InvalidOperationException("policy is broken");

        Assert.True(DeviceWorkerPolicy.MayStart("a-kit"));
    }

    // ── the refusal ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The gate is consulted at <c>ProcessDeviceWorkerTransport.Start</c> — the line that actually
    /// starts a process — so no launch path can route around it, and <b>nothing is started</b> when
    /// it refuses. The executable named here exists and would run; the refusal is what stops it.
    /// </summary>
    [Fact]
    public void ARefusedWorker_IsNotStarted_AndSaysWhy()
    {
        DeviceWorkerPolicy.Gate = name => $"not allowed, and '{name}' is who asked";

        // The Starting notification is raised by Start() just before Process.Start. The gate is
        // checked ahead of it, so a refused worker is never even announced — which is the readable
        // form of "nothing was started".
        var announced = new List<DeviceWorkerStart>();
        void OnStarting(DeviceWorkerStart s) => announced.Add(s);
        ProcessDeviceWorkerTransport.Starting += OnStarting;

        try
        {
            var ex = Assert.Throws<ExternalDeviceException>(
                () => ProcessDeviceWorkerTransport.Start(
                          RealExecutable(), [], forProvider: "some-kit"));

            Assert.Contains("not allowed", ex.Message, StringComparison.Ordinal);
            Assert.Contains("some-kit", ex.Message, StringComparison.Ordinal);
            Assert.Empty(announced);
        }
        finally { ProcessDeviceWorkerTransport.Starting -= OnStarting; }
    }

    /// <summary>The provider name reaches the gate, so the refusal can name the kit rather than
    /// leaving the reader to work out which one stopped.</summary>
    [Fact]
    public void TheGateIsToldWhichProviderAsked()
    {
        string? asked = null;
        DeviceWorkerPolicy.Gate = name => { asked = name; return "no"; };

        Assert.Throws<ExternalDeviceException>(
            () => ProcessDeviceWorkerTransport.Start(RealExecutable(), [], forProvider: "kit-b"));

        Assert.Equal("kit-b", asked);
    }

    /// <summary>A caller with no name to give hands over an empty string, never null.</summary>
    [Fact]
    public void AGateAskedWithNoProviderName_GetsEmptyRatherThanNull()
    {
        string? asked = "unset";
        DeviceWorkerPolicy.Gate = name => { asked = name; return "no"; };

        Assert.Throws<ExternalDeviceException>(
            () => ProcessDeviceWorkerTransport.Start(RealExecutable(), []));

        Assert.Equal("", asked);
    }

    // ── discovery refuses ONCE, not once per artefact ───────────────────────────────────────

    /// <summary>
    /// <c>OsdiModelDiscovery.Find</c> starts one worker per <c>.osdi</c> file, and each would be
    /// refused with the same sentence. It asks the gate once, before the loop, so a kit holding a
    /// dozen compiled models produces one line rather than a dozen identical ones.
    /// </summary>
    [Fact]
    public void OsdiDiscovery_RefusesOnce_ForAKitOfManyArtefacts()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-osdi-gate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string n in new[] { "a", "b", "c", "d" })
                File.WriteAllText(Path.Combine(dir, n + ".osdi"), "not really a model");

            DeviceWorkerPolicy.Gate = _ => "workers are off on this machine.";

            var problems = new List<string>();
            IReadOnlyList<OsdiModel> found =
                OsdiModelDiscovery.Find([dir], RealExecutable(), problems);

            Assert.Empty(found);
            Assert.Single(problems);
            Assert.Contains("workers are off", problems[0], StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// A path that exists and is startable, so the test measures the GATE rather than a file that was
    /// not there — the refusal has to happen before anything looks at the executable.
    /// </summary>
    private static string RealExecutable()
        => OperatingSystem.IsWindows()
            ? Path.Combine(Environment.SystemDirectory, "cmd.exe")
            : "/bin/echo";
}
