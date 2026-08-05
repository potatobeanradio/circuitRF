using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout.PCells.Wire;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// Interpreter discovery and the zero-configuration path.
///
/// <para>The bar is the one a kit import already sets — reference, place, run, with nothing to
/// configure — and the rule when that cannot be met is <b>degrade, never deny</b>: a missing
/// interpreter costs the generated artwork, never the design.</para>
/// </summary>
public sealed class PythonInterpreterDiscoveryTests : IDisposable
{
    private readonly string _dir;

    public PythonInterpreterDiscoveryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crf-pydisco-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── Probing ───────────────────────────────────────────────────────────────

    [PythonFact]
    public void ARealInterpreterProbesAndReportsItsVersion()
    {
        Assert.True(PythonInterpreterDiscovery.TryProbe(
            PythonRunner.Interpreter!, [], "test", out var found, out var why));

        Assert.Null(why);
        Assert.NotNull(found);
        Assert.Matches(@"^\d+\.\d+\.\d+$", found!.Version);
        Assert.True(int.Parse(found.Version.Split('.')[0]) >= PythonInterpreterDiscovery.MinimumMajor);
    }

    [Fact]
    public void SomethingThatIsNotAnInterpreter_IsRejectedWithAReason_NotAnException()
    {
        Assert.False(PythonInterpreterDiscovery.TryProbe(
            "crf-definitely-not-a-real-command", [], "test", out var found, out var why));

        Assert.Null(found);
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    /// <summary>
    /// Probed by RUNNING code, not by asking for a version string. A shim that prints a plausible
    /// version and cannot execute anything would otherwise be accepted and then fail later, which
    /// reads as a broken kit rather than a broken interpreter.
    /// </summary>
    [Fact]
    public void AStubThatOnlyPrintsAVersion_IsRejected()
    {
        string stub = WriteExecutable("fake-python", "echo 'Python 3.99.0'");
        Assert.False(PythonInterpreterDiscovery.TryProbe(stub, [], "test", out _, out var why));
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    /// <summary>An interpreter too old to run the package is refused by VERSION, rather than allowed
    /// through to fail on syntax somewhere the user cannot connect to the cause.</summary>
    [Fact]
    public void AnInterpreterOlderThanTheMinimum_IsRefusedNamingTheVersion()
    {
        string old = WriteExecutable("old-python", "echo '3.6.9'");
        Assert.False(PythonInterpreterDiscovery.TryProbe(old, [], "test", out _, out var why));

        Assert.Contains("3.6", why!, StringComparison.Ordinal);
        Assert.Contains($"{PythonInterpreterDiscovery.MinimumMajor}.{PythonInterpreterDiscovery.MinimumMinor}",
                        why!, StringComparison.Ordinal);
    }

    // ── Order ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A kit that declares an environment is stating where its dependencies are, so its declaration
    /// outranks everything — including a working interpreter found elsewhere.
    /// </summary>
    [PythonFact]
    public void AKitsOwnDeclaration_OutranksEverythingElse()
    {
        var found = PythonInterpreterDiscovery.Find(
            declaredByKit: PythonRunner.Interpreter, recorded: "python3", out _);

        Assert.NotNull(found);
        Assert.Contains("kit", found!.HowFound, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And when the kit's declared interpreter does not work, discovery <b>stops</b> rather than
    /// quietly using another one: running the kit's cells against an environment missing what they
    /// import fails on an import, far from here and much harder to explain.
    /// </summary>
    [Fact]
    public void AKitsDeclarationThatDoesNotWork_StopsRatherThanFallingBack()
    {
        var found = PythonInterpreterDiscovery.Find(
            declaredByKit: Path.Combine(_dir, "no-such-python"), recorded: null, out var rejected);

        Assert.Null(found);
        Assert.Contains(rejected, r => r.Contains("declares", StringComparison.OrdinalIgnoreCase));
    }

    [PythonFact]
    public void ARecordedChoiceIsReplayed_RatherThanRederived()
    {
        var found = PythonInterpreterDiscovery.Find(
            declaredByKit: null, recorded: PythonRunner.Interpreter, out _);

        Assert.NotNull(found);
        Assert.Contains("recorded", found!.HowFound, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An interpreter can be upgraded or removed between sessions; the workspace heals
    /// rather than needing the user to know that is what happened.</summary>
    [PythonFact]
    public void ARecordedChoiceThatNoLongerWorks_IsRederivedAndTheReasonKept()
    {
        var found = PythonInterpreterDiscovery.Find(
            declaredByKit: null, recorded: Path.Combine(_dir, "gone-python"), out var rejected);

        Assert.NotNull(found);
        Assert.DoesNotContain("recorded", found!.HowFound, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(rejected, r => r.Contains("gone-python", StringComparison.Ordinal));
    }

    [PythonFact]
    public void WithNothingDeclaredOrRecorded_ItFindsOneOnItsOwn()
    {
        var found = PythonInterpreterDiscovery.Find(null, null, out var rejected);
        Assert.True(found is not null, PythonInterpreterDiscovery.DescribeFailure(rejected));
    }

    // ── The record, and correcting it by hand ─────────────────────────────────

    [Theory]
    [InlineData("python3", "python3")]
    [InlineData("py.exe -3", "py.exe")]
    [InlineData("/opt/homebrew/bin/python3", "/opt/homebrew/bin/python3")]
    public void ARecordRoundTrips(string record, string expectedCommand)
    {
        var parsed = PythonInterpreter.ParseRecord(record);
        Assert.NotNull(parsed);
        Assert.Equal(expectedCommand, parsed!.Value.Command);

        var rebuilt = new PythonInterpreter(parsed.Value.Command, parsed.Value.Arguments, "3.13.0", "x");
        Assert.Equal(record, rebuilt.ToRecord());
    }

    /// <summary>A path with a space in it is the common case on Windows, so a quoted command must
    /// survive the round trip rather than being split into a command and a stray argument.</summary>
    [Fact]
    public void AQuotedCommandWithASpace_SurvivesTheRoundTrip()
    {
        var parsed = PythonInterpreter.ParseRecord("\"C:\\Program Files\\Python\\python.exe\" -X utf8");
        Assert.NotNull(parsed);
        Assert.Equal(@"C:\Program Files\Python\python.exe", parsed!.Value.Command);
        Assert.Equal(["-X", "utf8"], parsed.Value.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingRecorded_ParsesAsNothing(string? recorded)
        => Assert.Null(PythonInterpreter.ParseRecord(recorded));

    /// <summary>The decision is recorded where a user can see and correct it: one line in the
    /// workspace's own <c>.cws</c>, additive, and absent on an older file.</summary>
    [Fact]
    public void TheChoiceIsRecordedInTheWorkspace_AndAnOlderFileLoadsWithout()
    {
        string cws = Path.Combine(_dir, ".cws");

        WorkspacePersistence.SaveToFileAtomic(cws, new CwsFile { PythonInterpreter = "py.exe -3" });
        Assert.Equal("py.exe -3", WorkspacePersistence.LoadFromFile(cws).PythonInterpreter);
        Assert.Contains("py.exe -3", File.ReadAllText(cws), StringComparison.Ordinal);

        // Absent on a file written before the field existed — no FormatVersion bump.
        File.WriteAllText(cws, """{ "FormatVersion": 2 }""");
        Assert.Null(WorkspacePersistence.LoadFromFile(cws).PythonInterpreter);

        // And a workspace that never settled on one writes nothing rather than a null entry.
        WorkspacePersistence.SaveToFileAtomic(cws, new CwsFile());
        Assert.DoesNotContain("PythonInterpreter", File.ReadAllText(cws), StringComparison.Ordinal);
    }

    // ── Degrade, never deny ───────────────────────────────────────────────────

    /// <summary>
    /// <b>The rule this phase exists to keep.</b> With no interpreter, a kit's generators are simply
    /// unavailable — reported, with cells drawing as the existing placeholder — and every OTHER
    /// generator, and the design itself, is untouched. Nothing throws.
    /// </summary>
    [Fact]
    public void NoInterpreter_CostsTheArtworkAndNothingElse()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "kit"));
        File.WriteAllText(Path.Combine(_dir, "kit", "main.py"), "pass\n");
        File.WriteAllText(Path.Combine(_dir, "kit", PCellGeneratorManifest.FileName),
            """{ "schemaVersion": 1, "entry": "main.py" }""");

        var reports = new List<string>();
        using var resolver = new PCellWorkerResolver(_dir, (_, _) => null, reports.Add);

        // No exception, no generators, and a reason the user can act on.
        Assert.Empty(resolver.KnownGeneratorIds);
        Assert.Null(resolver.Resolve("ANYTHING"));
        Assert.Contains(reports, r => r.Contains("placeholders", StringComparison.OrdinalIgnoreCase));

        // Built-ins are entirely unaffected — the design still draws.
        Assert.True(CircuitRF.Ui.Layout.PCells.PCellRegistry.TryGet("MLIN", out _));
    }

    [Fact]
    public void TheFailureMessageSaysWhatWasTried_NotJustThatNothingWasFound()
    {
        PythonInterpreterDiscovery.Find(
            declaredByKit: null, recorded: Path.Combine(_dir, "nope"), out var rejected);

        string message = PythonInterpreterDiscovery.DescribeFailure(rejected);
        Assert.Contains("placeholders", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(PCellGeneratorManifest.FileName, message, StringComparison.Ordinal);
        Assert.Contains("nope", message, StringComparison.Ordinal);
    }

    /// <summary>Every candidate is a plausible interpreter location, and the list is finite — a
    /// wider search costs a process launch per entry and eventually matches something that is not a
    /// Python at all.</summary>
    [Fact]
    public void TheCandidateListIsSmallAndOrderedPathFirst()
    {
        var candidates = PythonInterpreterDiscovery.Candidates().ToList();

        Assert.NotEmpty(candidates);
        Assert.True(candidates.Count <= 12, $"{candidates.Count} candidates is a search, not a list.");
        Assert.Contains("PATH", candidates[0].How, StringComparison.Ordinal);
        Assert.All(candidates, c => Assert.False(string.IsNullOrWhiteSpace(c.How)));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Writes a tiny executable shell script standing in for a broken or ancient
    /// interpreter. Skipped on Windows, where the equivalent needs a different mechanism entirely.</summary>
    private string WriteExecutable(string name, string body)
    {
        if (OperatingSystem.IsWindows())
        {
            string cmd = Path.Combine(_dir, name + ".cmd");
            File.WriteAllText(cmd, "@echo off\r\n" + body.Replace("echo '", "echo ").Replace("'", "") + "\r\n");
            return cmd;
        }

        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }
}
