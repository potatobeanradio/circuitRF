// A bond wire drawn in a 3D view lands on bare metal, and says so when it does not.
//
// The pad lookup reads conductors only, so a wire end on a pad that a placed layout's passivation still covers used to
// land with nothing said, and the solve connected it through the dielectric. The Passivated Pad example's bonded pad is
// the fixture: as shipped, the covers are opened over the pad and the wire's ball lands in the opening; with the
// opening taken out of the die's layout, the same wire lands under both covers.

using System.Text.Json.Nodes;
using CircuitRF.Design.ThreeD;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class BondOnCoveredPadTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-bondpad-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }

    /// <summary>The example copied out, so the fixture can be changed; results are never part of it.</summary>
    private string CopyExample()
    {
        string src = Path.Combine(RepoRoot(), "examples", "Passivated Pad");
        foreach (string f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, f);
            if (rel.StartsWith("results", StringComparison.Ordinal)) continue;
            string dst = Path.Combine(_tmp, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }
        return _tmp;
    }

    private static List<string> Warnings(string ws)
    {
        string c3d = Path.Combine(ws, "4 Bonded Pad", "3d", "4 Bonded Pad.c3d");
        var e = C3dElaborator.ElaborateOnce(C3dPersistence.LoadFromFile(c3d), c3d, null);
        Assert.True(e.Ok, string.Join(" | ", e.Refusals));
        return [.. e.Warnings];
    }

    [Fact]
    public void ABallInTheOpening_LandsOnBareMetal_AndOneOnACoveredPad_IsWarnedOfWithBothCovers()
    {
        string ws = CopyExample();
        Assert.DoesNotContain(Warnings(ws), w => w.Contains("Wire 'w1'", StringComparison.Ordinal));

        // Take the opening out of the die: the same wire now lands under the passivation and the polyimide.
        string clay = Path.Combine(ws, "2 Probe Pad", "layout", "2 Probe Pad.clay");
        var doc = JsonNode.Parse(File.ReadAllText(clay))!.AsObject();
        var shapes = doc["Shapes"]!.AsArray();
        foreach (var s in shapes.Where(s => (int?)s!["Layer"]?["Layer"] == 30).ToList()) shapes.Remove(s);
        File.WriteAllText(clay, doc.ToJsonString());

        string warning = Assert.Single(Warnings(ws), w => w.Contains("Wire 'w1'", StringComparison.Ordinal));
        Assert.Contains("its start lands on 'U1/Metal2/1' under 'Passivation', 'Polyimide'", warning, StringComparison.Ordinal);
    }
}
