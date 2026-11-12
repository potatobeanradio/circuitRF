using System.Text;
using CircuitRF.Design.Interchange.Hzn;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// Like <see cref="FactAttribute"/>, but Skipped with a reason when <c>CIRCUITRF_HERTZON_HOME</c> does not name a
/// folder. Hertzon's own files are never committed (brief-hzn-0 D2), so a test that reads them finds them there.
/// </summary>
public sealed class HertzonHomeFactAttribute : FactAttribute
{
    public const string Variable = "CIRCUITRF_HERTZON_HOME";

    public HertzonHomeFactAttribute()
    {
        if (Home is null)
            Skip = $"{Variable} is not set to a folder — point it at a Hertzon checkout or installation to read its own .hzn files.";
    }

    public static string? Home =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } dir && Directory.Exists(dir) ? dir : null;
}

/// <summary>HZN-1 — the <c>.hzn</c> file model: lossless round trip, typed edits, the version gate, change on disk,
/// and the backup.</summary>
public sealed class HznFileTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-hzn1-" + Guid.NewGuid().ToString("N")[..10]);

    public HznFileTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string Fixture(string name) => Path.Combine(FixturePaths.Require("tests/Ui.Tests/Hzn/Fixtures"), name);

    /// <summary>A copy in the scratch folder, so a save never touches the committed fixture.</summary>
    private string Copy(string name)
    {
        string path = Path.Combine(_tmp, name);
        File.Copy(Fixture(name), path);
        return path;
    }

    private string WriteText(string name, string text)
    {
        string path = Path.Combine(_tmp, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private static HznFile Open(string path)
    {
        var read = HznFile.Read(path);
        Assert.True(read.Ok, read.Refusal?.Render());
        return read.File!;
    }

    public static TheoryData<string> Fixtures =>
    [
        "box-and-lumped-port.hzn",
        "two-designs.hzn",
        "circuit-and-3d.hzn",
        "unknown-keys.hzn",
        "number-spellings.hzn",
        "non-ascii-names.hzn",
    ];

    // ── R-hzn1-1: round trip ─────────────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(Fixtures))]
    public void EveryFixture_RoundTripsByteForByte(string name)
    {
        string path = Fixture(name);
        Assert.Equal(File.ReadAllBytes(path), Open(path).ToBytes());
    }

    /// <summary>Layouts the committed fixtures cannot carry through git unchanged: a byte-order mark, CRLF line ends
    /// with tab indentation, and a file on one line.</summary>
    [Theory]
    [InlineData(true, "{\r\n\t\"name\": \"P\",\r\n\t\"designs\": [ ],\r\n\t\"format\": \"hertzon-project\",\r\n\t\"version\": 2\r\n}\r\n")]
    [InlineData(false, "{\"name\":\"P\",\"designs\":[{\"name\":\"D\",\"variables\":{\"a\":\"1mm\"}}],\"format\":\"hertzon-project\",\"version\":2}")]
    public void OtherLayouts_RoundTripByteForByte(bool bom, string text)
    {
        byte[] bytes = [.. (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : []), .. Encoding.UTF8.GetBytes(text)];
        string path = Path.Combine(_tmp, "layout.hzn");
        File.WriteAllBytes(path, bytes);
        Assert.Equal(bytes, Open(path).ToBytes());
    }

    /// <summary>Hertzon's own files (D2: never committed). Gate 1 covers every file that opens; one that is refused is
    /// reported in the message, not failed here.</summary>
    [HertzonHomeFact]
    public void EveryFileUnderHertzonHome_RoundTripsByteForByte()
    {
        var opened = new List<string>();
        var refused = new List<string>();
        foreach (string path in Directory.EnumerateFiles(HertzonHomeFactAttribute.Home!, "*.hzn", SearchOption.AllDirectories))
        {
            var read = HznFile.Read(path);
            if (!read.Ok) { refused.Add(read.Refusal!.Render()); continue; }
            Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(read.File!.ToBytes()), $"Not byte-identical: {path}");
            opened.Add(path);
        }
        Assert.True(opened.Count > 0, "No .hzn opened under the folder. Refused:\n" + string.Join("\n", refused));
    }

    // ── R-hzn1-1: typed edits ────────────────────────────────────────────────────────────────

    [Fact]
    public void ATypedEdit_ChangesExactlyThatString()
    {
        string path = Copy("box-and-lumped-port.hzn");
        string before = File.ReadAllText(path);
        var file = Open(path);

        var design = Assert.Single(file.Project.Designs);
        Assert.Equal("3mm", design.Variables["w"]);
        design.Variables["w"] = "3.2mm";
        var saved = file.Save();

        Assert.True(saved.Saved, saved.Refusal?.Render());
        Assert.Equal(before.Replace("\"w\": \"3mm\"", "\"w\": \"3.2mm\"", StringComparison.Ordinal), File.ReadAllText(path));
    }

    [Fact]
    public void ANewVariable_TakesItsSiblingsLayout()
    {
        string path = Copy("two-designs.hzn");
        string before = File.ReadAllText(path);
        var file = Open(path);

        file.Project.Designs[1].Variables["h"] = "1mm";

        string expected = before.Replace("\"r\": \"250um\"\n   },", "\"r\": \"250um\",\n    \"h\": \"1mm\"\n   },", StringComparison.Ordinal);
        Assert.NotEqual(before, expected);
        Assert.Equal(expected, Encoding.UTF8.GetString(file.ToBytes()));
    }

    /// <summary>A new object placed beside others takes their key order and the file's indentation unit at its depth.</summary>
    [Fact]
    public void ANewObject_TakesItsSiblingsKeyOrderAndIndentation()
    {
        string path = Copy("two-designs.hzn");
        string before = File.ReadAllText(path);
        var file = Open(path);

        var sphere = new HznJsonObject();
        sphere.SetString("group", "");
        sphere.SetString("material", "pec");
        sphere.SetString("name", "Sphere");
        sphere.Set("history", new HznJsonArray());
        sphere.SetString("consumed_by", null);
        file.Project.Designs[0].Json.GetArray("objects")!.Add(sphere);

        const string after = "\"visible\": true\n    }\n   ],\n   \"boundaries\": [],\n   \"excitations\": [],\n   \"setups\": [],\n   \"coord_systems\": []\n  },";
        const string added = "\"visible\": true\n    },\n    {\n     \"name\": \"Sphere\",\n     \"history\": [],\n     \"material\": \"pec\",\n" +
                             "     \"consumed_by\": null,\n     \"group\": \"\"\n    }\n   ],\n   \"boundaries\": [],\n   \"excitations\": [],\n" +
                             "   \"setups\": [],\n   \"coord_systems\": []\n  },";
        int at = before.IndexOf(after, StringComparison.Ordinal);
        Assert.True(at >= 0);
        string expected = before[..at] + added + before[(at + after.Length)..];
        Assert.Equal(expected, Encoding.UTF8.GetString(file.ToBytes()));
    }

    // ── R-hzn1-2: the version gate ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{\"format\": \"other-project\", \"version\": 2}", "hzn.format.not-hertzon",
                "'{0}' is not a Hertzon project: its format is 'other-project', not 'hertzon-project'.")]
    [InlineData("{\"format\": \"hertzon-project\", \"version\": 1}", "hzn.version.older",
                "'{0}' is a Hertzon format version 1 project; circuitRF reads version 2. Open it in Hertzon and save it once, which converts it.")]
    [InlineData("{\"format\": \"hertzon-project\", \"version\": 3}", "hzn.version.newer",
                "'{0}' was written by a newer Hertzon, format version 3; circuitRF reads version 2.")]
    public void TheVersionGate_RefusesByName(string text, string id, string sentence)
    {
        string path = WriteText("gate.hzn", text);
        var read = HznFile.Read(path);

        Assert.False(read.Ok);
        Assert.Equal(id, read.Refusal!.Id);
        Assert.Equal(string.Format(sentence, path), read.Refusal.Render());
    }

    // ── R-hzn1-3: change on disk ─────────────────────────────────────────────────────────────

    /// <summary>The hardest case to see: same length and same modification time, different bytes. Only the hash
    /// catches it.</summary>
    [Fact]
    public void AFileChangedOnDisk_IsRefused_AndLeftUntouched()
    {
        string path = Copy("box-and-lumped-port.hzn");
        var file = Open(path);
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        file.Project.Designs[0].Variables["h"] = "2mm";

        byte[] theirs = File.ReadAllBytes(path);
        int i = Encoding.UTF8.GetString(theirs).IndexOf("\"20mm\"", StringComparison.Ordinal);
        theirs[i + 1] = (byte)'3';
        File.WriteAllBytes(path, theirs);
        File.SetLastWriteTimeUtc(path, stamp);

        var saved = file.Save();

        Assert.False(saved.Saved);
        Assert.Equal("hzn.save.changed-on-disk", saved.Refusal!.Id);
        Assert.Equal(theirs, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    // ── R-hzn1-4: the backup ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TheBackup_IsOff_UntilAsked_AndThenKeepsTheFileAsFirstRead()
    {
        string path = Copy("box-and-lumped-port.hzn");
        byte[] original = File.ReadAllBytes(path);
        var file = Open(path);
        Assert.False(file.BackupOnFirstSave);

        file.BackupOnFirstSave = true;
        file.Project.Designs[0].Variables["h"] = "2mm";
        Assert.Equal(file.BackupPath, file.Save().BackupPath);
        file.Project.Designs[0].Variables["h"] = "2.5mm";
        var second = file.Save();

        Assert.True(second.Saved, second.Refusal?.Render());
        Assert.Null(second.BackupPath);
        Assert.Equal(path + ".bak", file.BackupPath);
        Assert.Equal(original, File.ReadAllBytes(file.BackupPath));
    }
}
