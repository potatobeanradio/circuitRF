using System;
using System.IO;
using CircuitRF.Core.Devices.External;
using CircuitRF.Ui.Security;
using CircuitRF.Ui.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests.Security;

/// <summary>
/// The consent path for external device workers (Settings ▸ Security &amp; Permissions).
///
/// <para>A kit declares two kinds of program: generator scripts in <c>pcell-generators.json</c>,
/// gated behind an explicit prompt since B6, and a worker in <c>device-provider.json</c>, whose
/// <c>command</c> resolves against the kit's own folder — so a kit can ship an executable and
/// circuitRF starts it. Only the first of the two asked until the security review of 2026-08-25.</para>
///
/// <para><b>ON by default, and the tests say so first</b>, because the whole risk in adding a gate to
/// a path that has always just run is shipping it shut.</para>
/// </summary>
public sealed class ExternalWorkerConsentTests
{
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine([RepoRoot().FullName, .. parts]));

    private static string EmptyRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-worker-policy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── the default ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A machine with no <c>preferences.json</c> at all reads ON, with no first-run seeding anywhere
    /// — the nullable-with-default idiom, the same one automatic updates uses.
    /// </summary>
    [Fact]
    public void AbsenceIsTheDefault_AndTheDefaultIsOn()
    {
        string root = EmptyRoot();
        try
        {
            ExternalWorkerPolicyState policy = ExternalWorkerPolicy.Resolve(root, new AppPreferences());

            Assert.True(policy.Allowed);
            Assert.False(policy.IsOverridden);
            Assert.Equal("", policy.Reason);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void TheUserCanTurnItOff()
    {
        string root = EmptyRoot();
        try
        {
            var prefs = new AppPreferences { ExternalDeviceWorkers = false };
            Assert.False(ExternalWorkerPolicy.Resolve(root, prefs).Allowed);

            prefs.ExternalDeviceWorkers = true;
            Assert.True(ExternalWorkerPolicy.Resolve(root, prefs).Allowed);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ── the overrides, in precedence order ──────────────────────────────────────────────────

    /// <summary>
    /// The administrator's lock beats the preference — that is the whole point of it. An installation
    /// deployed with the marker file cannot be re-enabled by the person using it.
    /// </summary>
    [Fact]
    public void ThePolicyFileBeatsThePreference()
    {
        string root = EmptyRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, ExternalWorkerPolicy.PolicyFileName), "");

            var policy = ExternalWorkerPolicy.Resolve(root, new AppPreferences { ExternalDeviceWorkers = true });

            Assert.False(policy.Allowed);
            Assert.True(policy.IsOverridden);
            Assert.Equal(ExternalWorkerOverride.PolicyFile, policy.Override);
            Assert.Contains("administrator", policy.Reason, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>
    /// On macOS the install root IS the <c>.app</c> bundle, and a file put inside a bundle breaks its
    /// code signature. The marker therefore goes NEXT TO it, and both locations are looked in.
    /// </summary>
    [Fact]
    public void ThePolicyFileIsFoundBesideTheInstallAsWellAsInIt()
    {
        string parent = EmptyRoot();
        try
        {
            string bundle = Path.Combine(parent, "circuitRF.app");
            Directory.CreateDirectory(bundle);
            File.WriteAllText(Path.Combine(parent, ExternalWorkerPolicy.PolicyFileName), "");

            Assert.False(ExternalWorkerPolicy.Resolve(bundle, new AppPreferences()).Allowed);
        }
        finally { try { Directory.Delete(parent, true); } catch { } }
    }

    [Fact]
    public void TheEnvironmentKillSwitchBeatsThePreference()
    {
        string root = EmptyRoot();
        string? saved = Environment.GetEnvironmentVariable(ExternalWorkerPolicy.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ExternalWorkerPolicy.EnvironmentVariable, "1");

            var policy = ExternalWorkerPolicy.Resolve(root, new AppPreferences { ExternalDeviceWorkers = true });

            Assert.False(policy.Allowed);
            Assert.Equal(ExternalWorkerOverride.Environment, policy.Override);
            Assert.Contains(ExternalWorkerPolicy.EnvironmentVariable, policy.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExternalWorkerPolicy.EnvironmentVariable, saved);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>Anything but the literal <c>1</c> is not the kill switch — an empty or stray value
    /// must not silently disable a kit's devices.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    public void AStrayEnvironmentValueIsNotTheKillSwitch(string value)
    {
        string root = EmptyRoot();
        string? saved = Environment.GetEnvironmentVariable(ExternalWorkerPolicy.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ExternalWorkerPolicy.EnvironmentVariable, value);
            Assert.True(ExternalWorkerPolicy.Resolve(root, new AppPreferences()).Allowed);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExternalWorkerPolicy.EnvironmentVariable, saved);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ── the refusal a user actually reads ───────────────────────────────────────────────────

    /// <summary>
    /// This surfaces as a run error, so it has to name what stopped it and where to change that.
    /// "The kit could not be started" would send the reader to look at the kit.
    /// </summary>
    [Fact]
    public void TheRefusalNamesTheKitAndWhereToTurnItBackOn()
    {
        string? saved = Environment.GetEnvironmentVariable(ExternalWorkerPolicy.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ExternalWorkerPolicy.EnvironmentVariable, "1");

            string? why = ExternalWorkerPolicy.RefusalFor("some-kit");

            Assert.NotNull(why);
            Assert.Contains("some-kit", why!, StringComparison.Ordinal);
            Assert.Contains(ExternalWorkerPolicy.EnvironmentVariable, why!, StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable(ExternalWorkerPolicy.EnvironmentVariable, saved); }
    }

    /// <summary>
    /// The discovery worker's provider name is <c>osdi</c>, which is circuitRF's own spelling and not
    /// a kit anyone recognises — quoting it back would read as a kit that does not exist.
    /// </summary>
    [Fact]
    public void TheRefusalDoesNotQuoteOurOwnInternalProviderName()
    {
        string? saved = Environment.GetEnvironmentVariable(ExternalWorkerPolicy.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ExternalWorkerPolicy.EnvironmentVariable, "1");

            Assert.DoesNotContain("'osdi'", ExternalWorkerPolicy.RefusalFor("osdi")!, StringComparison.Ordinal);
            Assert.DoesNotContain("''", ExternalWorkerPolicy.RefusalFor("")!, StringComparison.Ordinal);
        }
        finally { Environment.SetEnvironmentVariable(ExternalWorkerPolicy.EnvironmentVariable, saved); }
    }

    // ── the wiring ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// src/Core cannot read AppPreferences, so the policy is a hook the application installs — and an
    /// entry point that forgets the call runs workers. That is the stated default rather than a hole,
    /// but it is exactly the omission a NEW entry point makes silently. Same shape as
    /// <c>PCellTrustGateTests.ResetPCellGenerators_AlwaysPassesATrustPolicy</c>.
    /// </summary>
    [Theory]
    [InlineData("Program.cs")]
    [InlineData("ProgramHarmonica.cs")]
    [InlineData("ProgramWBond.cs")]
    public void EveryEntryPointInstallsTheGate(string program)
        => Assert.Contains("ExternalWorkerPolicy.Install()", Read("src", "Ui", program), StringComparison.Ordinal);

    /// <summary>
    /// SettingsView is shared by circuitRF AND wBond; harmonicaRF has its own window and does not use
    /// it at all — and harmonicaRF resolves kit providers too (<c>HarmonicaDutCatalog</c>). Without
    /// its own reachable copy, a user who has only harmonicaRF installed could never refuse a kit its
    /// worker.
    /// </summary>
    [Fact]
    public void BothDialogsHostTheSameControl()
    {
        Assert.Contains("ExternalWorkerSettingsView",
                        Read("src", "Ui", "Views", "Dialogs", "SettingsView.axaml"));
        Assert.Contains("ExternalWorkerSettingsView",
                        Read("src", "Ui", "Views", "Dialogs", "HarmonicaSettingsDialog.axaml"));
    }

    /// <summary>
    /// ...and it is the SAME implementation, not a second copy: only one file writes the preference.
    /// Two copies would drift, and the one that drifted would be the one nobody opened.
    /// </summary>
    [Fact]
    public void OnlyTheOneControlWritesThePreference()
    {
        int writers = 0;
        foreach (string file in Directory.GetFiles(
                     Path.Combine(RepoRoot().FullName, "src", "Ui"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                              StringComparison.Ordinal)) continue;
            if (File.ReadAllText(file).Contains("p.ExternalDeviceWorkers =", StringComparison.Ordinal))
                writers++;
        }

        Assert.Equal(1, writers);
    }

    /// <summary>
    /// It lives in Security &amp; Permissions, with the other question about what this binary is
    /// allowed to RUN or to FETCH — not in the tab it happened to arrive in.
    /// </summary>
    [Fact]
    public void TheControlIsInTheSecurityAndPermissionsTab()
    {
        string xaml = Read("src", "Ui", "Views", "Dialogs", "SettingsView.axaml");

        int security = xaml.IndexOf("<TabItem Header=\"Security &amp; Permissions\">", StringComparison.Ordinal);
        int control  = xaml.IndexOf("<dlg:ExternalWorkerSettingsView", StringComparison.Ordinal);
        int colour   = xaml.IndexOf("<TabItem Header=\"Color Theme\">", StringComparison.Ordinal);

        Assert.True(security >= 0 && control > security && control < colour,
                    "ExternalWorkerSettingsView must sit inside the Security & Permissions tab.");
    }

    /// <summary>
    /// A settings change that only mutates JSON is incomplete. A worker started before the box was
    /// unchecked is still running and still answering, so the change would appear not to take effect
    /// until the workspace was reopened — and the user would have every reason to believe it had.
    /// </summary>
    [Fact]
    public void UncheckingTheBoxAlsoEndsWorkersAlreadyRunning()
        => Assert.Contains("ExternalDeviceRegistry.EndResolvedProviders()",
                           Read("src", "Ui", "Views", "Dialogs", "ExternalWorkerSettingsView.axaml.cs"),
                           StringComparison.Ordinal);

    /// <summary>
    /// The gate itself is enforced at the line that starts a process, not at the callers — so the
    /// three launch paths cannot each carry their own copy, and a fourth is covered by construction.
    /// </summary>
    [Fact]
    public void TheGateIsEnforcedAtTheProcessStart()
        => Assert.Contains("DeviceWorkerPolicy.RefusalReason(",
                           Read("src", "Core", "Devices", "External", "DeviceWorkerTransport.cs"),
                           StringComparison.Ordinal);

    /// <summary>
    /// The precedence lives in ONE accessor. A second reader of the preference is a second place the
    /// administrator's lock can be forgotten — and the place it is forgotten is the one that starts a
    /// vendor's executable on a machine whose administrator forbade it.
    /// </summary>
    [Fact]
    public void NothingElseReadsThePreferenceDirectly()
    {
        int readers = 0;
        foreach (string file in Directory.GetFiles(
                     Path.Combine(RepoRoot().FullName, "src", "Ui"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                              StringComparison.Ordinal)) continue;

            string name = Path.GetFileName(file);
            if (name is "AppPreferences.cs" or "ExternalWorkerPolicy.cs"
                     or "ExternalWorkerSettingsView.axaml.cs") continue;

            if (File.ReadAllText(file).Contains(".ExternalDeviceWorkers", StringComparison.Ordinal))
                readers++;
        }

        Assert.Equal(0, readers);
    }
}
