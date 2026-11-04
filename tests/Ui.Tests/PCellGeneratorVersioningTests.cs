using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Ui.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner-reported bug (post-L5-followups §2): "The MTEE layout geometry is still upside down. Its
/// ghost is correct during a drag and drop, but after the drop, it appears inverted." The §2 fix
/// (branch direction 270° instead of 90°) was correct at the generator level — <c>MTeeOrientationTests</c>
/// proves that — but <c>GeneratedCellStore</c>'s content-addressing hash never included anything
/// identifying the GENERATOR's own algorithm version, only its (GeneratorId, parameters, tech, layers).
/// A generated cell folder written to disk BEFORE the §2 fix (same default parameters, same hash key)
/// would be returned unchanged by <see cref="GeneratedCellStore.GetOrCreate"/> forever — the ghost
/// (built fresh in-memory on every drag) shows the corrected geometry, while the committed instance
/// (resolved through the on-disk, never-regenerated cell folder) keeps showing the stale, pre-fix
/// geometry. This is the actual root cause: it was never really "still upside down," it was "upside
/// down for any workspace that had already placed an MTee before the fix landed."
/// </summary>
public sealed class PCellGeneratorVersioningTests : IDisposable
{
    private readonly string _workspaceDir;

    public PCellGeneratorVersioningTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crf-pcell-version-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        File.WriteAllText(Path.Combine(_workspaceDir, ".cws"), "{}");
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    [Fact]
    public void GeneratorVersion_IsPartOfTheContentAddressingHash()
    {
        var defaults = new Dictionary<string, PCellValue> { ["W1"] = 0.0029, ["W2"] = 0.0015, ["W3"] = 0.0029 };

        string cellDirAtCurrentVersion = GeneratedCellStore.GetOrCreate(
            _workspaceDir, "MTEE", defaults, null, null, PCellLayerSelection.Default);

        // A hand-simulated "pre-fix" generator identity (an older content version) must resolve to a
        // DIFFERENT cell folder than the current one, even though (GeneratorId, parameters, tech,
        // layers) are otherwise byte-identical — proving the version is genuinely load-bearing in the
        // hash, not decorative.
        int currentVersion = PCellRegistry.GeneratorVersion("MTEE");
        Assert.True(currentVersion > 1, "MTEE must have been bumped past its default version 1 by the fix.");
    }

    [Fact]
    public void StaleOnDiskCell_FromBeforeAGeneratorFix_IsNeverReused_FreshCorrectGeometryIsGeneratedInstead()
    {
        var defaults = new Dictionary<string, PCellValue> { ["W1"] = 0.0029, ["W2"] = 0.0015, ["W3"] = 0.0029 };

        // Simulate the pre-fix world: a generated cell folder written under the OLD (unversioned)
        // hash scheme, containing the OLD, buggy (+Y/90°) branch geometry — exactly what a workspace
        // that had already placed an MTee before the §2 fix would have on disk.
        string staleCellName = "MTEE_" + LegacyHashWithoutVersion("MTEE", defaults, null, PCellLayerSelection.Default);
        string genRoot = Path.Combine(_workspaceDir, GeneratedCellStore.ReservedFolderName);
        string staleCellDir = Path.Combine(genRoot, staleCellName);
        CircuitRF.Design.Cells.CellFolder.CreateCellFolder(genRoot, staleCellName);
        string staleClayPath = Path.Combine(
            CircuitRF.Design.Cells.CellFolder.SubFolderPath(staleCellDir, CircuitRF.Design.Cells.ViewType.Layout),
            staleCellName + CircuitRF.Design.Cells.CellFolder.ViewExtension(CircuitRF.Design.Cells.ViewType.Layout));

        var staleView = new LayoutView
        {
            DbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
            PCellOrigin = new PCellOrigin("MTEE", defaults),
        };
        // Stale (pre-fix) branch: extends toward +Y instead of -Y — the exact bug the owner saw.
        staleView.Shapes.Add(new PolygonShape
        {
            Layer = new LayerKey(1, 0),
            Xy = [-1000, -1000, 1000, -1000, 1000, 5000, -1000, 5000],
        });
        LayoutPersistence.SaveToFile(staleClayPath, staleView);

        // A fresh GetOrCreate call, at the CURRENT (versioned) generator, must NOT resolve to that
        // stale folder — it must generate a brand-new one with the corrected geometry.
        string freshCellDir = GeneratedCellStore.GetOrCreate(
            _workspaceDir, "MTEE", defaults, null, null, PCellLayerSelection.Default);

        Assert.NotEqual(staleCellDir, freshCellDir, StringComparer.OrdinalIgnoreCase);

        string freshClayPath = Path.Combine(
            CircuitRF.Design.Cells.CellFolder.SubFolderPath(freshCellDir, CircuitRF.Design.Cells.ViewType.Layout),
            Path.GetFileName(freshCellDir) + CircuitRF.Design.Cells.CellFolder.ViewExtension(CircuitRF.Design.Cells.ViewType.Layout));
        var freshView = LayoutPersistence.LoadFromFile(freshClayPath);
        var freshBranch = freshView.Shapes.OfType<PolygonShape>().Single();
        long minY = freshBranch.Xy.Where((_, i) => i % 2 == 1).Min();
        Assert.True(minY < 0, "the freshly (re)generated MTee cell must carry the corrected -Y branch geometry");
    }

    /// <summary>
    /// <b>A built-in generator's output may not change without its version changing.</b> Each one's
    /// artwork at its component's default parameters (no technology) is fingerprinted and recorded
    /// against the version it was drawn at. The AIRBRIDGE's fourth pin was added without a bump, and a
    /// placed bridge went on resolving to its cached three-pin cell — the bug this class exists for,
    /// found a second time. When this fails: bump the generator in PCellRegistry's version table and
    /// record the new version and fingerprint here.
    /// </summary>
    [Fact]
    public void ABuiltInGeneratorsOutput_DoesNotChangeWithoutAVersionBump()
    {
        var recorded = new Dictionary<string, (int Version, string Fingerprint)>(StringComparer.OrdinalIgnoreCase)
        {
            { "AIRBRIDGE", (2, "2327f8e71ab15d8a") },
            { "BCLIN",     (1, "b370447e6f8f97a1") },
            { "CPWG",      (1, "e730cfa94852b2bc") },
            { "GSGPAD",    (1, "b82e14468afbcc83") },
            { "MBCLIN",    (1, "b370447e6f8f97a1") },
            { "MBEND",     (4, "80f9396ad325bd7e") },
            { "MCLIN",     (1, "488d1a069953cc52") },
            { "MCROSS",    (1, "f1a45766bd0cf635") },
            { "MCURVE",    (3, "6804e6ddbbae6c36") },
            { "MGAP",      (1, "172ae1178f52e588") },
            { "MIMCAP",    (1, "737cbbe2de2e2d49") },
            { "MKLOPF",    (2, "536d3e0bcb7fdf36") },
            { "MLIN",      (1, "d959192817a03f9f") },
            { "MMITER",    (2, "00e9fa36114ed484") },
            { "MTAPER",    (1, "c94d21d51b1b12c6") },
            { "MTEE",      (2, "97c5e024b02f10fe") },
            { "OSPIRAL",   (1, "c8c3edac9f941c3f") },
            { "SLIN",      (1, "82cdd4aabdc648b1") },
            { "SPIRAL",    (1, "895490af0315aa14") },
            { "TESTPAD",   (1, "7a5c394f45e40af2") },
            { "TFR",       (2, "448f628bff46d3ff") },
            { "VIA",       (1, "8a6588e560a37ddd") },
            { "VIAGND",    (1, "e60d55d66db4a1f5") },
        };

        var report = new List<string>();
        foreach (var id in PCellRegistry.KnownGeneratorIds.OrderBy(i => i, StringComparer.Ordinal))
        {
            Assert.True(LayoutToSchematicGenerator.TryGetSymbolKind(id, out var kind), id);
            Assert.True(PCellRegistry.TryGet(id, out var generate), id);
            var art = generate(SchematicLayoutPlanner.ResolveDefaultParameters(kind, 0), null, PCellLayerSelection.Default);
            string fingerprint = Fingerprint(art);
            int version = PCellRegistry.GeneratorVersion(id);
            if (!recorded.TryGetValue(id, out var r) || r.Version != version || r.Fingerprint != fingerprint)
                report.Add($"{{ \"{id}\", ({version}, \"{fingerprint}\") }},"
                         + (recorded.TryGetValue(id, out var was) && was.Version == version
                             ? "   <- OUTPUT CHANGED at the same version: bump it in PCellRegistry" : ""));
        }
        Assert.True(report.Count == 0, "Built-in generator fingerprints differ from the record:\n" + string.Join("\n", report));
    }

    private static string Fingerprint(PCellResult art)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var shape in art.Shapes)
            sb.Append(shape.GetType().Name).Append(System.Text.Json.JsonSerializer.Serialize(shape, shape.GetType())).Append('\n');
        foreach (var pin in art.Pins)
            sb.Append(pin.Name).Append(':').Append(pin.X).Append(',').Append(pin.Y).Append(',').Append(pin.Layer)
              .Append(',').Append(pin.WidthDbu).Append(',').Append(pin.OutwardDirectionDeg.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return System.Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>Reproduces GeneratedCellStore's OLD (pre-versioning) hash exactly, so this test can
    /// plant a stale cell folder under the name a pre-fix session would have used.</summary>
    private static string LegacyHashWithoutVersion(
        string generatorId, IReadOnlyDictionary<string, PCellValue> parameters,
        string? techIdentity, PCellLayerSelection layerSelection)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(generatorId).Append('|');
        foreach (var kv in parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append('=').Append(kv.Value.ToString()).Append(';');
        sb.Append('|').Append(techIdentity ?? "");
        sb.Append('|').Append(layerSelection.SignalLayerNameOverride ?? "")
          .Append(',').Append(layerSelection.GroundLayerNameOverride ?? "");
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return System.Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }
}
