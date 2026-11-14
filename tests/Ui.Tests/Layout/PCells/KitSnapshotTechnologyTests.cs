// brief-icp-10 — a kit PCell's snapshot that names no technology is rebuilt on the technology of the layout placing it. A kit cell
// resolves every layer it draws by name against the technology it is handed, so with none it drew nothing: an open SiGe kit's own
// example layout opened, rendered and simulated empty. A built-in generator draws without one and keeps its cell's name.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

public sealed class KitSnapshotTechnologyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("icp10-tech-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void AKitSnapshot_WithNoTechnology_TakesThePlacingLayouts()
    {
        Directory.CreateDirectory(Path.Combine(_root, "tech"));
        TechPersistence.SaveToFile(Path.Combine(_root, "tech", "kit.ctech"), ShippedTechnologies.Load("rfic-Si_3LM_TFR_100um"));
        string clay = Path.Combine(_root, "Top", "layout", "Top.clay");
        var placing = new LayoutView { TechRef = "../../tech/kit.ctech" };
        var none = new Dictionary<string, PCellValue>();

        var kit = GeneratedCellsLifecycle.Canonical(_root, new PCellSnapshot("npn13G2", none, null, null, null), clay, placing);
        Assert.Equal("tech/kit.ctech", kit.TechIdentity);

        var builtIn = GeneratedCellsLifecycle.Canonical(_root, new PCellSnapshot("MLIN", none, null, null, null), clay, placing);
        Assert.Null(builtIn.TechIdentity);
    }
}
