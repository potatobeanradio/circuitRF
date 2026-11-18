// PWL-4 — the document operations behind the 3D editor's wall-port gestures: what Make Port ▸ Wall adds on a face
// (C3dWallPorts.Propose) and a face switched between wave and wall ports (C3dWallPorts.SwitchFace), round trips byte for byte.
// The editor calls exactly these; no view is built here.

using System.Text.Json.Nodes;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.ThreeD;

public sealed class WallPortEditingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-pwl4-" + Guid.NewGuid().ToString("N")[..12]);
    private const long Len = 20000000;

    public WallPortEditingTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    /// <summary>A testdata/pwl fixture with its lines run to both x faces and no ports, saved beside its technology.</summary>
    private (C3dDocument Doc, string Path) Fixture(string file, bool oneLine = false)
    {
        string dir = FixturePaths.Require("testdata/pwl");
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, file)))!.AsObject();
        foreach (var o in doc["Objects"]!.AsArray())
            if (((string?)o!["Name"])!.StartsWith("strip", StringComparison.Ordinal)) { o["Rect"]!["Min"]![0] = 0; o["Rect"]!["Size"]![0] = Len; }
        doc["Ports"] = new JsonArray();
        if (oneLine)    // the pair's strip_a alone: a microstrip
        {
            var objects = doc["Objects"]!.AsArray();
            objects.Remove(objects.First(o => (string?)o!["Name"] == "strip_b"));
        }
        File.Copy(Path.Combine(dir, "pwl.ctech"), Path.Combine(_root, "pwl.ctech"), overwrite: true);
        string path = Path.Combine(_root, (oneLine ? "one_" : "") + file);
        File.WriteAllText(path, doc.ToJsonString());
        return (C3dPersistence.LoadFromFile(path), path);
    }

    /// <summary>The port context the editor judges a document's ports in: the "Palace" setup's box, forced for wall faces.</summary>
    private static Func<C3dDocument, C3dPortContext> Context(C3dDocument doc, string path)
    {
        var e = new C3dElaborator().Elaborate(doc, path, null);
        var setup = C3dSetups.Read(doc).Single(s => s.Name == "Palace").Setup!;
        return d => C3dProblemAssembly.PortContext(setup, d, e, C3dProblemAssembly.AirBox(setup, e, out _, d));
    }

    /// <summary>Make Port ▸ Wall's ports on both x faces, numbered left to right on xmin first.</summary>
    private static List<C3dPort> MakeWall(C3dDocument doc, Func<C3dDocument, C3dPortContext> ctx)
    {
        int n = 1;
        var ports = new List<C3dPort>();
        foreach (double at in new[] { 0.0, Len * 1e-9 })
        {
            var (made, _, why) = C3dWallPorts.Propose(ctx(doc), doc.DbuPerMicron, 0, at);
            Assert.Null(why);
            foreach (var p in made) { p.Number = n; p.Name = $"P{n}"; n++; ports.Add(p); }
        }
        return ports;
    }

    [Fact]
    public void MakePortWall_OnTheStriplineFace_IsOneRectangleRoundTheStripsEnd()
    {
        var (doc, path) = Fixture("stripline_line_L20000um.c3d");
        var (made, skipped, why) = C3dWallPorts.Propose(Context(doc, path)(doc), doc.DbuPerMicron, 0, 0);
        Assert.Null(why);
        Assert.Empty(skipped);
        var p = Assert.Single(made);
        Assert.Equal(Em3dPortKind.Wall, p.Kind);
        Assert.Equal(C3dPlane.YZ, p.Plane);
        Assert.Equal(0, p.Offset);
        Assert.Equal(-858933, p.Rect.Min.U);
        Assert.Equal(1717866, p.Rect.Size.U);
        Assert.Null(p.Positive);

        var (_, skippedAgain, refused) = C3dWallPorts.Propose(Context(doc, path)(doc), doc.DbuPerMicron, 0, 0, new HashSet<string> { "strip" });
        Assert.Equal(["strip"], skippedAgain);
        Assert.Contains("has a port there already", refused);
    }

    [Fact]
    public void MakePortWall_OnThePairsFace_IsOnePortALine_LeftToRight()
    {
        var (doc, path) = Fixture("coupled_line_L20000um.c3d");
        var (made, _, why) = C3dWallPorts.Propose(Context(doc, path)(doc), doc.DbuPerMicron, 0, 0);
        Assert.Null(why);
        Assert.Equal(2, made.Count);
        Assert.Equal(-1350000, made[0].Rect.Min.U);     // strip_a, then strip_b
        Assert.Equal(150000, made[1].Rect.Min.U);
    }

    /// <summary>R-pwl4-3 — a face switched to Wave and back is the file it started from, in both directions.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFaceSwitchedToWaveAndBack_IsTheFileItStartedFrom(bool oneLine)
    {
        var (doc, path) = Fixture("coupled_line_L20000um.c3d", oneLine);
        var ctx = Context(doc, path);
        doc.Ports = MakeWall(doc, ctx);
        string walls = C3dPersistence.Serialize(doc);

        var (asWave, switched, why) = C3dWallPorts.SwitchFace(doc, doc.Ports[0], Em3dPortKind.Wave, ctx);
        Assert.Null(why);
        Assert.NotEmpty(switched);
        doc.Ports = asWave;
        Assert.Contains(doc.Ports, p => p.Kind == Em3dPortKind.Wave);
        Assert.Contains(doc.Ports, p => p.Kind == Em3dPortKind.Wall);      // the other face is untouched
        string waves = C3dPersistence.Serialize(doc);

        var (back, _, why2) = C3dWallPorts.SwitchFace(doc, doc.Ports.First(p => p.Kind == Em3dPortKind.Wave), Em3dPortKind.Wall, ctx);
        Assert.Null(why2);
        doc.Ports = back;
        Assert.Equal(walls, C3dPersistence.Serialize(doc));

        var (again, _, why3) = C3dWallPorts.SwitchFace(doc, doc.Ports[0], Em3dPortKind.Wave, ctx);
        Assert.Null(why3);
        doc.Ports = again;
        Assert.Equal(waves, C3dPersistence.Serialize(doc));
    }

    /// <summary>R-pwl4-1 — a line that stops short of the face gives the item nothing to feed: Make Port ▸ Wall is offered greyed,
    /// with this refusal as its tip (the menu asks exactly this call).</summary>
    [Fact]
    public void MakePortWall_OnAFaceNoLineReaches_IsRefusedSayingSo()
    {
        var (doc, path) = Fixture("stripline_line_L20000um.c3d");
        foreach (var o in doc.Objects.OfType<C3dSheet>().Where(o => o.Name == "strip"))
            o.Rect = new C3dRect { Min = new C3dPoint2(171800, o.Rect.Min.V), Size = new C3dPoint2(Len - 2 * 171800, o.Rect.Size.V) };
        var (made, _, why) = C3dWallPorts.Propose(Context(doc, path)(doc), doc.DbuPerMicron, 0, 0);
        Assert.Empty(made);
        Assert.Equal("No line reaches the xmin face. A wall port feeds a line that runs to the face of the air box.", why);
    }

    /// <summary>A face that cannot be a wave port is not switched: the stripline's strip runs between the box's own PEC planes, and
    /// a wave port over that face meets only the strip. The resolver's refusal is the answer, and the document is untouched.</summary>
    [Fact]
    public void AFaceThatCannotBeAWavePort_IsNotSwitched_AndSaysWhy()
    {
        var (doc, path) = Fixture("stripline_line_L20000um.c3d");
        var ctx = Context(doc, path);
        doc.Ports = MakeWall(doc, ctx);
        string before = C3dPersistence.Serialize(doc);
        var (ports, switched, why) = C3dWallPorts.SwitchFace(doc, doc.Ports[0], Em3dPortKind.Wave, ctx);
        Assert.NotNull(why);
        Assert.Contains("wave port on the air box's xmin face", why);
        Assert.Empty(switched);
        doc.Ports = ports;
        Assert.Equal(before, C3dPersistence.Serialize(doc));
    }
}
