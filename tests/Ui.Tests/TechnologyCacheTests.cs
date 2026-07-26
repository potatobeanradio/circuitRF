using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// ── Phase L0c gate 8: TechnologyCache — one load per file, explicit invalidation ──

public class TechnologyCacheTests : IDisposable
{
    private readonly string _root;

    public TechnologyCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "TechCache_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Get_MissingFile_ReturnsNull_NoException()
    {
        var cache = new TechnologyCache();
        var path = Path.Combine(_root, "missing.ctech");

        var ex = Record.Exception(() => Assert.Null(cache.Get(path)));
        Assert.Null(ex);
    }

    [Fact]
    public void Get_TwoResolutionsOfSamePath_LoadFileOnce()
    {
        var path = Path.Combine(_root, "pcb.ctech");
        TechPersistence.SaveToFile(path, StarterTechnologies.Pcb2Layer());

        var cache = new TechnologyCache();
        var first  = cache.Get(path);
        var second = cache.Get(path);

        Assert.NotNull(first);
        Assert.Same(first, second); // same cached instance — the file was loaded exactly once
    }

    [Fact]
    public void Get_IsCaseInsensitiveOnPath()
    {
        var path = Path.Combine(_root, "pcb.ctech");
        TechPersistence.SaveToFile(path, StarterTechnologies.Pcb2Layer());

        var cache = new TechnologyCache();
        var first  = cache.Get(path);
        var second = cache.Get(path.ToUpperInvariant());

        Assert.Same(first, second);
    }

    [Fact]
    public void Invalidate_ForcesReload()
    {
        var path = Path.Combine(_root, "pcb.ctech");
        TechPersistence.SaveToFile(path, StarterTechnologies.Pcb2Layer());

        var cache = new TechnologyCache();
        var first = cache.Get(path);

        cache.Invalidate(path);

        // Rewrite the file with different content, then confirm the reload picks it up
        // (not a stale cached instance).
        var mmic = StarterTechnologies.MmicGaAs();
        TechPersistence.SaveToFile(path, mmic);
        var second = cache.Get(path);

        Assert.NotSame(first, second);
        Assert.Equal("MMIC GaAs", second!.Name);
    }

    [Fact]
    public void Invalidate_FiresTechnologyChanged_WithThePath()
    {
        var path = Path.Combine(_root, "pcb.ctech");
        TechPersistence.SaveToFile(path, StarterTechnologies.Pcb2Layer());

        var cache = new TechnologyCache();
        cache.Get(path);

        string? changed = null;
        cache.TechnologyChanged += p => changed = p;

        cache.Invalidate(path);

        Assert.Equal(Path.GetFullPath(path), changed);
    }

    [Fact]
    public void InvalidateAll_FiresTechnologyChanged_ForEveryCachedPath()
    {
        var path1 = Path.Combine(_root, "a.ctech");
        var path2 = Path.Combine(_root, "b.ctech");
        TechPersistence.SaveToFile(path1, StarterTechnologies.Pcb2Layer());
        TechPersistence.SaveToFile(path2, StarterTechnologies.MmicGaAs());

        var cache = new TechnologyCache();
        cache.Get(path1);
        cache.Get(path2);

        var changed = new List<string>();
        cache.TechnologyChanged += p => changed.Add(p);

        cache.InvalidateAll();

        Assert.Contains(Path.GetFullPath(path1), changed);
        Assert.Contains(Path.GetFullPath(path2), changed);
    }
}
