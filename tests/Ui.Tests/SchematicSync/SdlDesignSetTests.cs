using Xunit;

namespace CircuitRF.Ui.Tests.SchematicSync;

/// <summary>
/// brief-sdl-0 R-sdl0-3c: every design in <c>testdata/schematic-to-layout/designs.txt</c> through
/// <see cref="SdlChecker"/>. Each schematic-driven layout brief adds its own designs to the manifest, and this
/// theory is the gate they pass (series rule S6).
/// </summary>
public sealed class SdlDesignSetTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "crf-sdl-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best-effort */ }
    }

    public static TheoryData<string, string> Designs()
    {
        var data = new TheoryData<string, string>();
        foreach (var (folder, brief) in SdlChecker.Manifest()) data.Add(folder, brief);
        return data;
    }

    [Theory]
    [MemberData(nameof(Designs))]
    public void Design_PassesTheChecker(string design, string brief)
    {
        var result = SdlChecker.Check(design, _scratch);
        Assert.True(result.Problems.Count == 0,
                    $"{design} ({brief}):\n  " + string.Join("\n  ", result.Problems));
    }
}
