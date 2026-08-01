using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices.External;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.External;

/// <summary>
/// Choosing a model library for a kit whose worker runs inside circuitRF's Linux VM.
///
/// <para>The VM is what makes this its own case: a path on this Mac means nothing inside the guest,
/// so a chosen library has to reach it the same way the kit's own does — through a share. Writing
/// the host path in verbatim starts the VM perfectly and then fails deep inside it with "no such
/// file", naming a file that plainly exists on the Mac. That is the failure these tests pin.</para>
/// </summary>
public sealed class VmHostLibraryOverrideTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-vm-" + Guid.NewGuid().ToString("N")[..8]);

    private string KitDir   => Path.Combine(_root, "SampleKit");
    private string ModelDir => Path.Combine(_root, "models");
    private string OtherDir => Path.Combine(_root, "elsewhere");

    public VmHostLibraryOverrideTests()
    {
        Directory.CreateDirectory(KitDir);
        Directory.CreateDirectory(ModelDir);
        Directory.CreateDirectory(OtherDir);
        File.WriteAllText(Path.Combine(ModelDir, "models.so"), "");
        File.WriteAllText(Path.Combine(ModelDir, "newer.so"),  "");
        File.WriteAllText(Path.Combine(OtherDir, "other.so"),  "");
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>Writes the kit's manifest with the shares given, in the shape the importer writes.</summary>
    private void WriteManifest(params string[] shares)
    {
        var arguments = new List<string>();
        foreach (string s in shares) { arguments.Add("--share"); arguments.Add(s); }
        arguments.Add("--");
        arguments.Add(VmHostArguments.GuestPath("crfw", "senior_worker"));
        arguments.Add(VmHostArguments.GuestPath("kit",  "models.so"));

        string json = string.Join(", ", arguments.Select(a => "\"" + a.Replace("\\", "\\\\") + "\""));

        File.WriteAllText(Path.Combine(KitDir, DeviceWorkerManifest.FileName),
            $$"""
              { "workers": [ { "platform": "any", "command": "{{VmHostArguments.Command}}",
                               "arguments": [{{json}}] } ] }
              """);
    }

    private string KitShare   => VmHostArguments.ShareValue("kit", ModelDir);
    private string WorkerShare => VmHostArguments.ShareValue("crfw", KitDir);

    private IReadOnlyList<string> Launched(string? library)
    {
        IReadOnlyList<string> captured = [];

        var resolver = new DeviceWorkerProviderResolver([_root],
            (_, _, arguments) => { captured = arguments; return null!; });

        resolver.Resolve(DeviceWorkerProviderResolver.ComposeOverride("SampleKit", library));
        return captured;
    }

    [Fact]
    public void ALibraryInsideAnAlreadySharedFolder_IsNamedByItsGuestPathAndAddsNoShare()
    {
        // The common case rather than a nicety: another revision of a library normally sits beside
        // the kit's own, which the kit's share already carries.
        WriteManifest(WorkerShare, KitShare);

        var args = Launched(Path.Combine(ModelDir, "newer.so"));

        Assert.Contains(VmHostArguments.GuestPath("kit", "newer.so"), args);
        Assert.DoesNotContain(VmHostArguments.GuestPath("kit", "models.so"), args);
        Assert.Equal(2, args.Count(a => a == "--share"));   // nothing added
    }

    [Fact]
    public void ALibraryOutsideEveryShare_GetsAShareOfItsOwnAndIsNamedInsideIt()
    {
        WriteManifest(WorkerShare, KitShare);

        var args = Launched(Path.Combine(OtherDir, "other.so"));

        Assert.Contains(VmHostArguments.ShareValue("lib", OtherDir), args);
        Assert.Contains(VmHostArguments.GuestPath("lib", "other.so"), args);
    }

    [Fact]
    public void TheHostPathNeverReachesTheGuest()
    {
        // The whole point. A path on this Mac is not a path in the VM, and handing one over fails
        // inside the guest rather than here — the report then names a file that visibly exists.
        WriteManifest(WorkerShare, KitShare);
        string chosen = Path.Combine(OtherDir, "other.so");

        Assert.DoesNotContain(chosen, Launched(chosen));
    }

    [Fact]
    public void AnAddedShareStaysAnOption_AheadOfTheArgvSeparator()
    {
        // Past the separator it would be an argument to the worker instead, which would both lose
        // the share and hand the worker a flag it never asked for.
        WriteManifest(WorkerShare, KitShare);

        var args = Launched(Path.Combine(OtherDir, "other.so"));

        Assert.All(args.Select((a, i) => (a, i)).Where(x => x.a == "--share"),
                   x => Assert.True(x.i < args.ToList().IndexOf("--")));
    }

    [Fact]
    public void AFreshTagDoesNotCollideWithOneTheKitAlreadyUses()
    {
        WriteManifest(WorkerShare, KitShare, VmHostArguments.ShareValue("lib", KitDir));

        var args = Launched(Path.Combine(OtherDir, "other.so"));

        Assert.Contains(VmHostArguments.ShareValue("lib2", OtherDir), args);
        Assert.Contains(VmHostArguments.GuestPath("lib2", "other.so"), args);
    }

    [Fact]
    public void ALibraryUnderAShareMountedWhereItLives_IsLeftExactlyAsItIs()
    {
        // Such a share makes the host path true inside the guest, so there is nothing to translate —
        // and rewriting it to /mnt/<tag>/… would name a place nothing was mounted.
        WriteManifest(WorkerShare, KitShare, VmHostArguments.ShareValue("kitdata", OtherDir));

        // Put the kitdata share on the --share-at flag rather than --share.
        string path = Path.Combine(KitDir, DeviceWorkerManifest.FileName);
        File.WriteAllText(path, File.ReadAllText(path)
            .Replace($"\"--share\", \"{Json(VmHostArguments.ShareValue("kitdata", OtherDir))}\"",
                     $"\"{VmHostArguments.ShareAtFlag}\", \"{Json(VmHostArguments.ShareValue("kitdata", OtherDir))}\""));

        string chosen = Path.Combine(OtherDir, "other.so");

        var args = Launched(chosen);

        Assert.Contains(chosen, args);                                   // untouched
        Assert.DoesNotContain(VmHostArguments.GuestPath("kitdata", "other.so"), args);
        Assert.Equal(2, args.Count(a => a == VmHostArguments.ShareFlag)); // and nothing added
    }

    private static string Json(string value) => value.Replace("\\", "\\\\");

    [Fact]
    public void WithNoLibraryChosen_TheKitsCommandIsUntouched()
    {
        WriteManifest(WorkerShare, KitShare);

        var args = Launched(null);

        Assert.Contains(VmHostArguments.GuestPath("kit", "models.so"), args);
        Assert.Equal(2, args.Count(a => a == "--share"));
    }

    [Fact]
    public void TheVmHostsOwnOptionsAreNotCandidatesForTheLibraryToReplace()
    {
        // Only the argv run INSIDE the guest can name a model library; the options describe the
        // machine. A manifest whose guest argv names none must say so rather than rewriting a share.
        File.WriteAllText(Path.Combine(KitDir, DeviceWorkerManifest.FileName),
            $$"""
              { "workers": [ { "platform": "any", "command": "{{VmHostArguments.Command}}",
                               "arguments": ["--share", "{{VmHostArguments.ShareValue("kit", "/x/models.so")}}",
                                             "--", "/mnt/crfw/senior_worker"] } ] }
              """);

        var ex = Assert.Throws<ExternalDeviceException>(() => Launched("/elsewhere/other.so"));

        Assert.Contains("no model library", ex.Message);
    }

    [Theory]
    [InlineData("crf-vmhost", true)]
    [InlineData("/opt/circuitrf/crf-vmhost", true)]
    [InlineData("crf-vmhost.exe", true)]
    [InlineData("senior_worker", false)]
    [InlineData("", false)]
    public void TheVmHostIsRecognisedByName_BareOrPathed(string command, bool expected)
        // A manifest may name it bare (resolved out of circuitRF's tools folder) or as a full path
        // (a kit shipping its own build), and both are the same program.
        => Assert.Equal(expected, VmHostArguments.IsVmHost(command));
}
