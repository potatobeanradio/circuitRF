using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// ── Phase L0c gates: TechnologyResolver resolution order + non-fatal failure modes ──

public class TechnologyResolverTests : IDisposable
{
    private readonly string _root;

    public TechnologyResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "TechResolver_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteTech(string relPath, Technology tech)
    {
        var abs = Path.Combine(_root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        TechPersistence.SaveToFile(abs, tech);
        return abs;
    }

    // ── Gate 3: resolution order ──────────────────────────────────────────────

    [Fact]
    public void LayoutRef_ResolvesRelativeToClayDir_AndWinsOverWorkspaceDefault()
    {
        var clayDir = Path.Combine(_root, "cells", "Amp", "layout");
        Directory.CreateDirectory(clayDir);
        WriteTech("cells/Amp/layout/local.ctech", StarterTechnologies.MmicGaAs());
        WriteTech("tech/pcb-2layer.ctech", StarterTechnologies.Pcb2Layer());

        var cache = new TechnologyCache();
        var result = TechnologyResolver.Resolve("local.ctech", clayDir, _root, "tech/pcb-2layer.ctech", cache);

        Assert.Equal(TechResolutionSource.LayoutRef, result.Source);
        Assert.NotNull(result.Tech);
        Assert.Equal("MMIC GaAs", result.Tech!.Name);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void NullTechRef_ResolvesToWorkspaceDefault()
    {
        var clayDir = Path.Combine(_root, "cells", "Amp", "layout");
        Directory.CreateDirectory(clayDir);
        WriteTech("tech/pcb-2layer.ctech", StarterTechnologies.Pcb2Layer());

        var cache = new TechnologyCache();
        var result = TechnologyResolver.Resolve(null, clayDir, _root, "tech/pcb-2layer.ctech", cache);

        Assert.Equal(TechResolutionSource.WorkspaceDefault, result.Source);
        Assert.NotNull(result.Tech);
        Assert.Equal("PCB 2-Layer", result.Tech!.Name);
    }

    [Fact]
    public void NeitherPresent_ResolvesToNone_NoException()
    {
        var cache = new TechnologyCache();
        var ex = Record.Exception(() =>
        {
            var result = TechnologyResolver.Resolve(null, null, null, null, cache);
            Assert.Null(result.Tech);
            Assert.Null(result.ResolvedPath);
            Assert.Equal(TechResolutionSource.None, result.Source);
            Assert.Empty(result.Diagnostics);
        });
        Assert.Null(ex);
    }

    // ── Gate 4: every failure is non-fatal ────────────────────────────────────

    [Fact]
    public void MissingFile_ReturnsNullTech_WithDiagnostic_NoException()
    {
        var clayDir = Path.Combine(_root, "cells", "Amp", "layout");
        Directory.CreateDirectory(clayDir);
        var cache = new TechnologyCache();

        var result = TechnologyResolver.Resolve("missing.ctech", clayDir, null, null, cache);

        Assert.Null(result.Tech);
        Assert.Equal(TechResolutionSource.LayoutRef, result.Source);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void CorruptJson_ReturnsNullTech_WithDiagnostic_NoException()
    {
        var clayDir = Path.Combine(_root, "cells", "Amp", "layout");
        Directory.CreateDirectory(clayDir);
        File.WriteAllText(Path.Combine(clayDir, "bad.ctech"), "{ not valid json");
        var cache = new TechnologyCache();

        var result = TechnologyResolver.Resolve("bad.ctech", clayDir, null, null, cache);

        Assert.Null(result.Tech);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void NewerFormatVersion_ReturnsNullTech_WithDiagnostic_NoException()
    {
        var clayDir = Path.Combine(_root, "cells", "Amp", "layout");
        Directory.CreateDirectory(clayDir);
        var json = TechPersistence.Serialize(StarterTechnologies.Pcb2Layer())
            .Replace("\"FormatVersion\": 1", "\"FormatVersion\": 999");
        File.WriteAllText(Path.Combine(clayDir, "future.ctech"), json);
        var cache = new TechnologyCache();

        var result = TechnologyResolver.Resolve("future.ctech", clayDir, null, null, cache);

        Assert.Null(result.Tech);
        Assert.NotEmpty(result.Diagnostics);
    }

    // ── Gate 5: a technology that fails validation still resolves ────────────

    [Fact]
    public void InvalidTechnology_StillResolves_DiagnosticsCarryValidationProblems()
    {
        var clayDir = Path.Combine(_root, "cells", "Amp", "layout");
        Directory.CreateDirectory(clayDir);
        var bad = new Technology
        {
            Name = "Broken",
            Layers =
            [
                new LayerDef { Key = new LayerKey(1, 0), Name = "A" },
                new LayerDef { Key = new LayerKey(1, 0), Name = "B" }, // duplicate key
            ],
        };
        WriteTech("cells/Amp/layout/broken.ctech", bad);
        var cache = new TechnologyCache();

        var result = TechnologyResolver.Resolve("broken.ctech", clayDir, null, null, cache);

        Assert.NotNull(result.Tech);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Contains(result.Diagnostics, d => d.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidTechnology_ResolvesWithNoDiagnostics()
    {
        var clayDir = Path.Combine(_root, "cells", "Amp", "layout");
        Directory.CreateDirectory(clayDir);
        WriteTech("cells/Amp/layout/good.ctech", StarterTechnologies.Pcb2Layer());
        var cache = new TechnologyCache();

        var result = TechnologyResolver.Resolve("good.ctech", clayDir, null, null, cache);

        Assert.NotNull(result.Tech);
        Assert.Empty(result.Diagnostics);
    }
}
