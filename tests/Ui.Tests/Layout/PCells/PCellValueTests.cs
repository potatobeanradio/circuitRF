using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// Contract version 2's kinded parameter value: what it converts to, what it refuses to convert, and
/// the two places its encoding is a compatibility surface rather than an implementation detail — the
/// content hash that names a generated cell folder, and the <c>.clay</c> it is persisted in.
/// </summary>
public sealed class PCellValueTests
{
    // ── Conversions ───────────────────────────────────────────────────────────

    [Fact]
    public void IntAndBool_ConvertToNumbers_SoAGeneratorReadingRealGetsTheSameAnswerEitherWay()
    {
        // The reason MBend's Miter and MKlopf's SmoothSteps can be read through Real/Bool without
        // caring which kind the caller used — a pre-contract-v2 parameter set carries Reals.
        Assert.Equal(4.0, PCellValue.Int(4).AsReal());
        Assert.Equal(1.0, PCellValue.Bool(true).AsReal());
        Assert.Equal(0.0, PCellValue.Bool(false).AsReal());
        Assert.True(PCellValue.Real(2.0).AsBool());
        Assert.False(PCellValue.Real(0.0).AsBool());
        Assert.Equal(2L, PCellValue.Real(2.0).AsInt());
    }

    [Fact]
    public void AString_NeverConvertsToANumber_ItFallsBackInstead()
    {
        // The mistake this type exists to make impossible: a model name silently read as a dimension.
        var name = PCellValue.Text("nch_lvt");
        Assert.Equal(-1.0, name.AsReal(-1.0));
        Assert.Equal(-1L, name.AsInt(-1));
        Assert.True(name.AsBool(true));
        Assert.False(name.AsBool(false));
    }

    [Fact]
    public void EqualityIsKinded_SoOneTheIntAndOneTheRealAreDifferentInputs()
    {
        Assert.NotEqual(PCellValue.Int(1), PCellValue.Real(1.0));
        Assert.NotEqual(PCellValue.Bool(true), PCellValue.Real(1.0));
        Assert.Equal(PCellValue.Int(1), PCellValue.Int(1));
        Assert.Equal(PCellValue.Text("a"), PCellValue.Text("a"));
    }

    // ── Content encoding ──────────────────────────────────────────────────────

    /// <summary>
    /// <b>The compatibility gate for every workspace written before contract version 2.</b> A
    /// generated cell's folder name is a hash over these strings and a placed instance's
    /// <c>CellRef</c> names that folder — so a Real encoding one byte different from what the
    /// pre-kinded code wrote would rename every generated cell while every instance still pointed at
    /// the old name, and each one would render as Not Found. Compared against the literal old
    /// expression, not against a constant copied out of the new code.
    /// </summary>
    [Theory]
    [InlineData(0.0003)]
    [InlineData(2e-3)]
    [InlineData(0.0)]
    [InlineData(-1.5)]
    [InlineData(50.0)]
    [InlineData(1.0 / 3.0)]
    [InlineData(1e300)]
    public void ARealEncodesExactlyAsThePreKindedCodeWroteADouble(double value)
        => Assert.Equal(value.ToString("R", CultureInfo.InvariantCulture), PCellValue.Real(value).ToString());

    [Fact]
    public void EveryOtherKindIsTagged_AndNoTaggedFormCanCollideWithAnother()
    {
        Assert.Equal("Int:4", PCellValue.Int(4).ToString());
        Assert.Equal("Bool:true", PCellValue.Bool(true).ToString());
        Assert.Equal("String:nch", PCellValue.Text("nch").ToString());

        // A string that LOOKS like another kind's encoding still encodes distinctly — otherwise two
        // different parameter sets would hash to one generated cell.
        Assert.NotEqual(PCellValue.Int(4).ToString(), PCellValue.Text("Int:4").ToString());
        Assert.NotEqual(PCellValue.Real(3.5).ToString(), PCellValue.Text("3.5").ToString());
        Assert.NotEqual(PCellValue.Bool(true).ToString(), PCellValue.Text("true").ToString());
    }

    [Fact]
    public void TheGeneratedCellName_IsUnchangedForAnAllRealParameterSet()
    {
        // End to end through the real store rather than through the encoding alone: an all-Real
        // parameter set — everything an existing workspace can contain — must resolve to the folder
        // it already resolved to. The expected name is computed from the pre-widening encoding.
        string root = Path.Combine(Path.GetTempPath(), "crf-pcellvalue-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            var parameters = new Dictionary<string, PCellValue> { ["W"] = 300e-6, ["L"] = 2e-3 };
            string cellDir = GeneratedCellStore.GetOrCreate(
                root, "MLIN", parameters, null, null, PCellLayerSelection.Default);

            Assert.Equal(LegacyCellName("MLIN", new Dictionary<string, double> { ["W"] = 300e-6, ["L"] = 2e-3 }),
                         Path.GetFileName(cellDir));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best effort */ } }
    }

    [Fact]
    public void TheKindIsPartOfTheContentHash_SoAnIntAndARealResolveToDifferentCells()
    {
        string root = Path.Combine(Path.GetTempPath(), "crf-pcellvalue-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            string asReal = GeneratedCellStore.GetOrCreate(root, "MLIN",
                new Dictionary<string, PCellValue> { ["W"] = PCellValue.Real(1.0) }, null, null, PCellLayerSelection.Default);
            string asInt = GeneratedCellStore.GetOrCreate(root, "MLIN",
                new Dictionary<string, PCellValue> { ["W"] = PCellValue.Int(1) }, null, null, PCellLayerSelection.Default);

            Assert.NotEqual(Path.GetFileName(asReal), Path.GetFileName(asInt));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* best effort */ } }
    }

    /// <summary>The pre-widening <c>BuildCellName</c>, transcribed. Deliberately a second copy rather
    /// than a call into the production one — a test that asks the code under test how it encodes
    /// proves nothing about whether that matches what is already on disk.</summary>
    private static string LegacyCellName(string generatorId, Dictionary<string, double> parameters)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(generatorId).Append('|');
        foreach (var kv in parameters.OrderBy(kv => kv.Key, System.StringComparer.Ordinal))
            sb.Append(kv.Key).Append('=').Append(kv.Value.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        sb.Append('|').Append("");
        sb.Append('|').Append("").Append(',').Append("");
        sb.Append('|').Append(PCellRegistry.GeneratorVersion(generatorId));
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return $"{generatorId}_{System.Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    [Fact]
    public void EveryKindRoundTripsThroughAClay()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-pcellvalue-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "cell.clay");
            var view = new LayoutView
            {
                PCellOrigin = new PCellOrigin("VENDOR", new Dictionary<string, PCellValue>
                {
                    ["W"]       = PCellValue.Real(300e-6),
                    ["Fingers"] = PCellValue.Int(4),
                    ["Guard"]   = PCellValue.Bool(true),
                    ["Model"]   = PCellValue.Text("nch_lvt"),
                }),
            };
            LayoutPersistence.SaveToFile(path, view);

            var reloaded = LayoutPersistence.LoadFromFile(path).PCellOrigin!.Parameters;
            Assert.Equal(PCellValue.Real(300e-6), reloaded["W"]);
            Assert.Equal(PCellValue.Int(4),       reloaded["Fingers"]);
            Assert.Equal(PCellValue.Bool(true),   reloaded["Guard"]);
            Assert.Equal(PCellValue.Text("nch_lvt"), reloaded["Model"]);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    [Fact]
    public void AnIntSurvivesTheRoundTripAsAnInt_NotCollapsedToAReal()
    {
        // JSON has one number token for both, so an Int written bare would reload as a Real, hash to
        // a different generated cell, and leave every instance naming the old one dangling. This is
        // the assertion that keeps the tagged form from being "simplified" away.
        string dir = Path.Combine(Path.GetTempPath(), "crf-pcellvalue-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "cell.clay");
            LayoutPersistence.SaveToFile(path, new LayoutView
            {
                PCellOrigin = new PCellOrigin("VENDOR",
                    new Dictionary<string, PCellValue> { ["Fingers"] = PCellValue.Int(4) }),
            });

            var value = LayoutPersistence.LoadFromFile(path).PCellOrigin!.Parameters["Fingers"];
            Assert.Equal(PCellValueKind.Int, value.Kind);
            Assert.NotEqual(PCellValue.Real(4.0), value);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    [Fact]
    public void AClayWrittenBeforeTheWidening_LoadsItsBareNumbersAsReals()
    {
        // Hand-authored in the pre-contract-v2 shape — the point is that no existing file was
        // rewritten, so this is what is actually sitting in users' workspaces.
        string dir = Path.Combine(Path.GetTempPath(), "crf-pcellvalue-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "cell.clay");
            File.WriteAllText(path, """
            {
              "FormatVersion": 1,
              "DbuPerMicron": 1000,
              "DisplayUnit": "Um",
              "SnapDbu": 1000,
              "AngleMode": "AnyAngle",
              "PCellOrigin": { "GeneratorId": "MLIN", "Parameters": { "W": 0.0003, "L": 0.002 } },
              "Shapes": [],
              "Instances": []
            }
            """);

            var origin = LayoutPersistence.LoadFromFile(path).PCellOrigin!;
            Assert.Equal("MLIN", origin.GeneratorId);
            Assert.Equal(PCellValue.Real(0.0003), origin.Parameters["W"]);
            Assert.Equal(PCellValue.Real(0.002),  origin.Parameters["L"]);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    [Fact]
    public void AnAllRealLayoutReSerializesToTheSameBytes()
    {
        // No FormatVersion bump is honest only if a file holding nothing but Reals — every file that
        // can exist today — comes back byte for byte.
        string dir = Path.Combine(Path.GetTempPath(), "crf-pcellvalue-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "cell.clay");
            LayoutPersistence.SaveToFile(path, new LayoutView
            {
                PCellOrigin = new PCellOrigin("MLIN",
                    new Dictionary<string, PCellValue> { ["W"] = 300e-6, ["L"] = 2e-3 }),
            });
            string first = File.ReadAllText(path);

            LayoutPersistence.SaveToFile(path, LayoutPersistence.LoadFromFile(path));
            Assert.Equal(first, File.ReadAllText(path));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    [Fact]
    public void AnUnrecognisedTaggedValue_IsRefused_NotGuessedAt()
    {
        // Guessing a kind would put the wrong value into the content hash — which surfaces as a cell
        // that silently regenerated, not as a bad file. Failing loudly is the lesser harm.
        var opts = new JsonSerializerOptions { Converters = { new PCellValueJsonConverter() } };
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<PCellValue>("""{"complex":{"re":1,"im":2}}""", opts));
    }
}
