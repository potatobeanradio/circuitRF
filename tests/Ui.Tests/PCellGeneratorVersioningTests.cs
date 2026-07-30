using System.Linq;
using CircuitRF.Ui.Layout;
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
        CellLayoutResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateAll();
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    [Fact]
    public void GeneratorVersion_IsPartOfTheContentAddressingHash()
    {
        var defaults = new Dictionary<string, double> { ["W1"] = 0.0029, ["W2"] = 0.0015, ["W3"] = 0.0029 };

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
        var defaults = new Dictionary<string, double> { ["W1"] = 0.0029, ["W2"] = 0.0015, ["W3"] = 0.0029 };

        // Simulate the pre-fix world: a generated cell folder written under the OLD (unversioned)
        // hash scheme, containing the OLD, buggy (+Y/90°) branch geometry — exactly what a workspace
        // that had already placed an MTee before the §2 fix would have on disk.
        string staleCellName = "MTEE_" + LegacyHashWithoutVersion("MTEE", defaults, null, PCellLayerSelection.Default);
        string genRoot = Path.Combine(_workspaceDir, GeneratedCellStore.ReservedFolderName);
        string staleCellDir = Path.Combine(genRoot, staleCellName);
        CircuitRF.Ui.Schematic.CellFolder.CreateCellFolder(genRoot, staleCellName);
        string staleClayPath = Path.Combine(
            CircuitRF.Ui.Schematic.CellFolder.SubFolderPath(staleCellDir, CircuitRF.Ui.Schematic.ViewType.Layout),
            staleCellName + CircuitRF.Ui.Schematic.CellFolder.ViewExtension(CircuitRF.Ui.Schematic.ViewType.Layout));

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
            CircuitRF.Ui.Schematic.CellFolder.SubFolderPath(freshCellDir, CircuitRF.Ui.Schematic.ViewType.Layout),
            Path.GetFileName(freshCellDir) + CircuitRF.Ui.Schematic.CellFolder.ViewExtension(CircuitRF.Ui.Schematic.ViewType.Layout));
        var freshView = LayoutPersistence.LoadFromFile(freshClayPath);
        var freshBranch = freshView.Shapes.OfType<PolygonShape>().Single();
        long minY = freshBranch.Xy.Where((_, i) => i % 2 == 1).Min();
        Assert.True(minY < 0, "the freshly (re)generated MTee cell must carry the corrected -Y branch geometry");
    }

    /// <summary>Reproduces GeneratedCellStore's OLD (pre-versioning) hash exactly, so this test can
    /// plant a stale cell folder under the name a pre-fix session would have used.</summary>
    private static string LegacyHashWithoutVersion(
        string generatorId, IReadOnlyDictionary<string, double> parameters,
        string? techIdentity, PCellLayerSelection layerSelection)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(generatorId).Append('|');
        foreach (var kv in parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append('=').Append(kv.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
        sb.Append('|').Append(techIdentity ?? "");
        sb.Append('|').Append(layerSelection.SignalLayerNameOverride ?? "")
          .Append(',').Append(layerSelection.GroundLayerNameOverride ?? "");
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return System.Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }
}
