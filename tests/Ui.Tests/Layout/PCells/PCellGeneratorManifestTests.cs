using System;
using System.IO;
using CircuitRF.Ui.Layout.PCells.Wire;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// The entry-point declaration — how a kit says its cells' layouts are generated, and by what.
/// Run-time data beside the kit, never a list of kits inside circuitRF.
/// </summary>
public sealed class PCellGeneratorManifestTests : IDisposable
{
    private readonly string _dir;

    public PCellGeneratorManifestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crf-pcellmanifest-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string json) => File.WriteAllText(Path.Combine(_dir, PCellGeneratorManifest.FileName), json);

    [Fact]
    public void AKitDeclaresItsEntryScript_AndPathsResolveAgainstTheManifestsOwnFolder()
    {
        Write("""
        {
          "schemaVersion": 1,
          "entry": "pcells/main.py",
          "pythonPath": ["lib", "vendor/shared"]
        }
        """);

        var manifest = PCellGeneratorManifest.TryRead(_dir, out var problem);
        Assert.Null(problem);
        Assert.NotNull(manifest);

        // Relative to the MANIFEST's folder — which is what lets a kit be moved or copied whole and
        // still resolve, and is the same rule device-provider.json already follows.
        Assert.Equal(Path.GetFullPath(Path.Combine(_dir, "pcells/main.py")), manifest!.ResolveEntry(_dir));
        Assert.Equal(
            [Path.GetFullPath(Path.Combine(_dir, "lib")), Path.GetFullPath(Path.Combine(_dir, "vendor/shared"))],
            manifest.ResolvePythonPath(_dir));
    }

    /// <summary>A kit with no generated artwork simply has no manifest. Reporting that would be
    /// noise on nearly every kit, so absence is silent — and distinct from broken.</summary>
    [Fact]
    public void NoManifest_IsNotAProblem()
    {
        Assert.Null(PCellGeneratorManifest.TryRead(_dir, out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void AManifestThatIsPresentButBroken_IsReported_NotSilentlyTreatedAsAbsent()
    {
        Write("{ this is not json");
        Assert.Null(PCellGeneratorManifest.TryRead(_dir, out var problem));
        Assert.NotNull(problem);
        Assert.Contains(PCellGeneratorManifest.FileName, problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestNamingNoEntry_DeclaresNothing_AndSaysSo()
    {
        Write("""{ "schemaVersion": 1 }""");
        Assert.Null(PCellGeneratorManifest.TryRead(_dir, out var problem));
        Assert.Contains("no entry script", problem!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A newer schema may mean something different by the same field; guessing would launch
    /// the wrong thing. Refused with both numbers named.</summary>
    [Fact]
    public void ANewerSchemaVersion_IsRefused_NamingBothVersions()
    {
        Write("""{ "schemaVersion": 99, "entry": "main.py" }""");
        Assert.Null(PCellGeneratorManifest.TryRead(_dir, out var problem));
        Assert.Contains("99", problem!, StringComparison.Ordinal);
        Assert.Contains(PCellGeneratorManifest.CurrentSchemaVersion.ToString(), problem!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The manifest does NOT list the generators the kit offers, and that is deliberate — describe is
    /// the only source. A second one would be a cache that can silently disagree with the script.
    /// </summary>
    [Fact]
    public void TheManifestDoesNotListGenerators_DescribeIsTheOnlySource()
    {
        Write("""{ "schemaVersion": 1, "entry": "main.py", "generators": ["SPIRAL"] }""");

        var manifest = PCellGeneratorManifest.TryRead(_dir, out var problem);
        Assert.Null(problem); // an unknown field is ignored, not fatal — a kit may carry its own notes
        Assert.NotNull(manifest);

        Assert.DoesNotContain(nameof(PCellGeneratorManifest).GetType().GetProperties(),
                              p => p.Name.Equals("Generators", StringComparison.OrdinalIgnoreCase));
        Assert.Null(typeof(PCellGeneratorManifest).GetProperty("Generators"));
    }

    [Fact]
    public void AnInterpreterOverrideIsOptional_AndAbsentMeansCircuitRfFindsOne()
    {
        Write("""{ "schemaVersion": 1, "entry": "main.py" }""");
        Assert.Null(PCellGeneratorManifest.TryRead(_dir, out _)!.Interpreter);

        Write("""{ "schemaVersion": 1, "entry": "main.py", "interpreter": "/opt/kit/venv/bin/python" }""");
        Assert.Equal("/opt/kit/venv/bin/python", PCellGeneratorManifest.TryRead(_dir, out _)!.Interpreter);
    }
}
