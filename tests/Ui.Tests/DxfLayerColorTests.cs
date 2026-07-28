using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Tests;

// ── docs/sonnet-briefs/brief-dxf-layer-colors.md ─────────────────────────────────────────────────
//
// §1: colours were never written at all (every LAYER record hardcoded ACI 7); §2: the LAYER table was
// never read on import, so an unmatched layer could only ever get a generated colour. Both are fixed
// here: DxfWriter now writes 62 (nearest ACI, always) and 420 (exact 24-bit RGB, on the two versions
// that support it) per layer; DxfReader parses the LAYER table; DxfLayerReconciliation carries the
// parsed colour into the source LayerDef a DXF import's "Add to technology" choice installs verbatim.

public sealed class DxfLayerColorTests : IDisposable
{
    private static readonly LayerKey RedKey = new(1, 0);
    private static readonly LayerKey GreenKey = new(2, 0);
    private static readonly LayerKey BlueKey = new(3, 0);

    private static Technology ThreeColorTech() => new()
    {
        Name = "T",
        DefaultDisplayUnit = LayoutUnit.Um,
        DefaultSnapDbu = 1000,
        Layers =
        [
            new LayerDef { Key = RedKey, Name = "Red", Color = new Rgba(255, 0, 0), FillOpacity = 1.0, Visible = true, Selectable = true },
            new LayerDef { Key = GreenKey, Name = "Green", Color = new Rgba(0, 255, 0), FillOpacity = 1.0, Visible = true, Selectable = true },
            new LayerDef { Key = BlueKey, Name = "Blue", Color = new Rgba(0, 0, 255), FillOpacity = 1.0, Visible = true, Selectable = true },
        ],
    };

    private static InterchangeStructure ThreeColorStructure() => new(
        "TOP",
        [
            new RectShape { Layer = RedKey, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 },
            new RectShape { Layer = GreenKey, X1 = 200, Y1 = 0, X2 = 300, Y2 = 100 },
            new RectShape { Layer = BlueKey, X1 = 400, Y1 = 0, X2 = 500, Y2 = 100 },
        ],
        []);

    private readonly string _dir = Directory.CreateTempSubdirectory("dxf-layer-color-test-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Counts whole-line occurrences of <paramref name="code"/> as a bare group-code line —
    /// the same dependency-free counting technique <c>DxfR2000ConformanceTests.CountGroupOccurrences</c>
    /// already established for this file family.</summary>
    private static int CountGroupOccurrences(string text, string code) =>
        text.Split('\n').Count(l => l == code);

    // ── Gate 2: colours are written ──────────────────────────────────────────────────────────────

    [Fact]
    public void Export_DistinctLayerColors_WritesPerLayer62And420_NoTwoDifferingLayersShareAnIndex()
    {
        var tech = ThreeColorTech();
        using var sw = new StringWriter();
        DxfWriter.Write(sw, [ThreeColorStructure()], "TOP", tech, 1000, new DxfExportOptions());
        string text = sw.ToString();

        int redIdx = DxfAciPalette.NearestIndex(new Rgba(255, 0, 0));
        int greenIdx = DxfAciPalette.NearestIndex(new Rgba(0, 255, 0));
        int blueIdx = DxfAciPalette.NearestIndex(new Rgba(0, 0, 255));

        Assert.Contains("62\n" + redIdx, text);
        Assert.Contains("62\n" + greenIdx, text);
        Assert.Contains("62\n" + blueIdx, text);
        // Well-separated primaries must not collide on the nearest-ACI approximation.
        Assert.True(redIdx != greenIdx && greenIdx != blueIdx && redIdx != blueIdx,
            $"expected distinct ACI indices, got red={redIdx} green={greenIdx} blue={blueIdx}");

        int packedRed = (255 << 16) | (0 << 8) | 0;
        int packedGreen = (0 << 16) | (255 << 8) | 0;
        int packedBlue = (0 << 16) | (0 << 8) | 255;
        Assert.Contains("420\n" + packedRed, text);
        Assert.Contains("420\n" + packedGreen, text);
        Assert.Contains("420\n" + packedBlue, text);
    }

    [Fact]
    public void Export_EntitiesNeverWriteTheirOwnColor_OnlyTheLayerTableDoes()
    {
        // Confirms §1.1's "verify entities don't write their own colour" directly, not just by
        // inspection: the ONLY group-62 occurrences in the whole file are the 4 layer-table records
        // (the 3 real layers plus the synthetic "0" layer) — none inside BLOCKS/ENTITIES.
        var tech = ThreeColorTech();
        using var sw = new StringWriter();
        DxfWriter.Write(sw, [ThreeColorStructure()], "TOP", tech, 1000, new DxfExportOptions());
        string text = sw.ToString();

        int tablesStart = text.IndexOf("2\nTABLES", StringComparison.Ordinal);
        int blocksStart = text.IndexOf("2\nBLOCKS", StringComparison.Ordinal);
        Assert.True(tablesStart >= 0 && blocksStart > tablesStart);

        string tablesAndAfter = text[tablesStart..];
        string blocksAndAfter = text[blocksStart..];

        Assert.Equal(4, CountGroupOccurrences(tablesAndAfter[..(blocksStart - tablesStart)], "62"));
        Assert.Equal(0, CountGroupOccurrences(blocksAndAfter, "62"));
        Assert.Equal(0, CountGroupOccurrences(blocksAndAfter, "420"));
    }

    // ── Gate 2a: all three versions ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DxfAcadVersion.R2000, "AC1015", false)]
    [InlineData(DxfAcadVersion.R2004, "AC1018", true)]
    [InlineData(DxfAcadVersion.R2018, "AC1032", true)]
    public void Export_EachVersion_WritesCorrectAcadverAndTrueColorPresence(DxfAcadVersion version, string expectedCode, bool expectTrueColor)
    {
        var tech = ThreeColorTech();
        using var sw = new StringWriter();
        DxfWriter.Write(sw, [ThreeColorStructure()], "TOP", tech, 1000, new DxfExportOptions(AcadVersion: version));
        string text = sw.ToString();

        Assert.Contains("$ACADVER\n1\n" + expectedCode, text);
        Assert.Equal(expectTrueColor ? 4 : 0, CountGroupOccurrences(text, "420"));
        // 62 is ALWAYS present regardless of version (R2000's only option; the other two keep it as
        // the ACI fallback for a reader that doesn't understand 420).
        Assert.Equal(4, CountGroupOccurrences(text, "62"));
    }

    [Fact]
    public void Export_R2004AndR2018_WriteIdenticalColorBytes_DifferOnlyInAcadver()
    {
        var tech = ThreeColorTech();
        using var sw2004 = new StringWriter();
        DxfWriter.Write(sw2004, [ThreeColorStructure()], "TOP", tech, 1000, new DxfExportOptions(AcadVersion: DxfAcadVersion.R2004));
        using var sw2018 = new StringWriter();
        DxfWriter.Write(sw2018, [ThreeColorStructure()], "TOP", tech, 1000, new DxfExportOptions(AcadVersion: DxfAcadVersion.R2018));

        string text2004 = sw2004.ToString();
        string text2018 = sw2018.ToString();

        // Replacing each file's own $ACADVER value with a placeholder should make them byte-identical —
        // proving the ONLY difference between the two versions is the header code, never the colour.
        string norm2004 = text2004.Replace("AC1018", "ACXXXX");
        string norm2018 = text2018.Replace("AC1032", "ACXXXX");
        Assert.Equal(norm2004, norm2018);
    }

    /// <summary>Gate 2a's own "verify each version opens in a real reader" — ezdxf (a real,
    /// independent, spec-compliant Python DXF library, already used by this project's prior DXF briefs
    /// as the non-QCAD real-parser check) opens all three versions with no error. Skips gracefully if
    /// ezdxf isn't installed in whatever environment runs this suite, rather than failing the whole
    /// gate on an environment gap.</summary>
    [Theory]
    [InlineData(DxfAcadVersion.R2000)]
    [InlineData(DxfAcadVersion.R2004)]
    [InlineData(DxfAcadVersion.R2018)]
    public void Export_EachVersion_OpensInEzdxf_ARealIndependentParser(DxfAcadVersion version)
    {
        var python = FindPython();
        if (python is null) return; // no Python/ezdxf in this environment — nothing to verify against

        var tech = ThreeColorTech();
        var path = Path.Combine(_dir, $"colors_{version}.dxf");
        using (var sw = new StreamWriter(path, append: false))
            DxfWriter.Write(sw, [ThreeColorStructure()], "TOP", tech, 1000, new DxfExportOptions(AcadVersion: version));

        var psi = new System.Diagnostics.ProcessStartInfo(python)
        {
            ArgumentList = { "-c", "import sys, ezdxf; ezdxf.readfile(sys.argv[1])", path },
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(10_000);

        Assert.True(proc.ExitCode == 0, $"ezdxf failed to open {version} export: {stderr}");
    }

    private static string? FindPython()
    {
        foreach (var name in new[] { "python3", "python" })
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(name)
                {
                    ArgumentList = { "-c", "import ezdxf" },
                    RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p is null) continue;
                p.WaitForExit(5_000);
                if (p.ExitCode == 0) return name;
            }
            catch { /* this name isn't on PATH — try the next */ }
        }
        return null;
    }

    // ── Gate 5: AC1015 option ────────────────────────────────────────────────────────────────────

    [Fact]
    public void R2000_NeverWrites420_AndDialogDescriptionSaysApproximate()
    {
        var tech = ThreeColorTech();
        using var sw = new StringWriter();
        DxfWriter.Write(sw, [ThreeColorStructure()], "TOP", tech, 1000, new DxfExportOptions(AcadVersion: DxfAcadVersion.R2000));
        Assert.DoesNotContain("420", sw.ToString());

        Assert.Contains("approximate", DxfWriter.FormatDescription(DxfAcadVersion.R2000), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultAcadVersion_IsR2018()
    {
        Assert.Equal(DxfAcadVersion.R2018, new DxfExportOptions().AcadVersion);
        Assert.Contains("default", DxfWriter.FormatDescription(DxfAcadVersion.R2018), StringComparison.OrdinalIgnoreCase);
    }

    // ── Gate 4: round-trip via 420 ───────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_ExactColorNotOnAnAciSlot_SurvivesExactlyViaGroup420()
    {
        // (37, 142, 201) is deliberately not any low-index ACI primary/secondary — the nearest-ACI
        // approximation alone would NOT reproduce it exactly; only group 420 can.
        var exact = new Rgba(37, 142, 201);
        var tech = new Technology
        {
            Name = "T", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
            Layers = [new LayerDef { Key = RedKey, Name = "Odd", Color = exact, FillOpacity = 1.0, Visible = true, Selectable = true }],
        };
        var structure = new InterchangeStructure("TOP", [new RectShape { Layer = RedKey, X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 }], []);

        using var sw = new StringWriter();
        DxfWriter.Write(sw, [structure], "TOP", tech, 1000, new DxfExportOptions(AcadVersion: DxfAcadVersion.R2018));

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sw.ToString()));
        var reader = DxfReader.Read(new StreamReader(stream));
        var layerEntry = reader.LayerTable.First(l => l.Name == "Odd");

        Assert.NotNull(layerEntry.TrueColor);
        Assert.Equal(exact, layerEntry.TrueColor!.Value);

        // Confirm the ACI-only approximation would NOT have been exact — proving 420 is load-bearing,
        // not redundant with 62, for this specific colour.
        Assert.NotEqual(exact, DxfAciPalette.ToRgb(layerEntry.AciIndex));
    }

    // ── Gate 6: the LAYER table is read ──────────────────────────────────────────────────────────

    [Fact]
    public void Reader_ParsesLayerTable_NameColorTrueColorAndFlags()
    {
        using var sw = new StringWriter();
        var w = new DxfGroupWriter(sw);
        w.WriteString(0, "SECTION"); w.WriteString(2, "HEADER"); w.WriteString(0, "ENDSEC");
        w.WriteString(0, "SECTION"); w.WriteString(2, "TABLES");
        w.WriteString(0, "TABLE"); w.WriteString(2, "LAYER"); w.WriteInt(70, 2);
        w.WriteString(0, "LAYER"); w.WriteString(2, "Copper");
        w.WriteInt(70, 1); // frozen
        w.WriteInt(62, 3); // ACI green
        w.WriteInt(420, (10 << 16) | (20 << 8) | 30); // exact true colour
        w.WriteString(0, "LAYER"); w.WriteString(2, "OffLayer");
        w.WriteInt(70, 0);
        w.WriteInt(62, -5); // negative = layer OFF, ACI 5
        w.WriteString(0, "ENDTAB");
        w.WriteString(0, "ENDSEC");
        w.WriteString(0, "SECTION"); w.WriteString(2, "ENTITIES"); w.WriteString(0, "ENDSEC");
        w.WriteString(0, "EOF");

        var reader = DxfReader.Read(new StringReader(sw.ToString()));

        var copper = Assert.Single(reader.LayerTable, l => l.Name == "Copper");
        Assert.True(copper.Frozen);
        Assert.False(copper.Off);
        Assert.Equal(3, copper.AciIndex);
        Assert.Equal(new Rgba(10, 20, 30), copper.TrueColor);

        var off = Assert.Single(reader.LayerTable, l => l.Name == "OffLayer");
        Assert.True(off.Off);
        Assert.False(off.Frozen);
        Assert.Equal(5, off.AciIndex);
        Assert.Null(off.TrueColor);
    }

    // ── Gate 7: import prompt pre-fill + R-col-4's default divergence ──────────────────────────────

    private static MemoryStream ExportToStream(InterchangeStructure structure, Technology sourceTechForColor)
    {
        var tech = new Technology
        {
            Name = "Src", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
            Layers = [.. sourceTechForColor.Layers],
        };
        using var sw = new StringWriter();
        DxfWriter.Write(sw, [structure], "TOP", tech, 1000, new DxfExportOptions(AcadVersion: DxfAcadVersion.R2018));
        return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sw.ToString()));
    }

    [Fact]
    public void Import_UnmatchedLayer_DefaultChoiceIsAddToTechnology_PreFilledWithNameAndColor()
    {
        var exact = new Rgba(200, 30, 40);
        var sourceTech = new Technology { Name = "Src", Layers = [new LayerDef { Key = new LayerKey(1, 0), Name = "Silkscreen", Color = exact }] };
        var structure = new InterchangeStructure("TOP", [new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }], []);
        using var stream = ExportToStream(structure, sourceTech);

        var destTech = new Technology
        {
            Name = "Dest", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
            Layers = [new LayerDef { Key = new LayerKey(9, 0), Name = "Unrelated", Color = new Rgba(1, 2, 3) }],
        };

        IReadOnlyList<LayerMappingRow>? capturedRows = null;
        var result = DxfImport.Import(stream, _dir, destTech, 1000,
            resolveLayerMapping: rows =>
            {
                capturedRows = rows;
                return LayoutLayerMapping.BuildChoices(rows); // simulates "user accepts the pre-filled defaults as-is"
            });

        Assert.False(result.Cancelled);
        Assert.NotNull(capturedRows);
        var row = Assert.Single(capturedRows!);
        Assert.Equal(LayerMatchKind.NoMatch, row.Match);
        // R-col-4: pre-selected "Add to technology", NOT L1g's own paste-path default (Keep as unknown).
        Assert.Equal(LayoutFragment.LayerReconciliationAction.AddToTechnology, row.Choice.Action);

        var added = Assert.Single(result.LayersToAdd);
        Assert.Equal("Silkscreen", added.Name);
        Assert.Equal(exact, added.Color);
    }

    [Fact]
    public void Import_OverridingARow_IsHonoured_NotForcedToAddToTechnology()
    {
        var sourceTech = new Technology { Name = "Src", Layers = [new LayerDef { Key = new LayerKey(1, 0), Name = "Silkscreen", Color = new Rgba(200, 30, 40) }] };
        var structure = new InterchangeStructure("TOP", [new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }], []);
        using var stream = ExportToStream(structure, sourceTech);

        var existingTarget = new LayerKey(9, 0);
        var destTech = new Technology
        {
            Name = "Dest", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
            Layers = [new LayerDef { Key = existingTarget, Name = "Unrelated", Color = new Rgba(1, 2, 3) }],
        };

        var result = DxfImport.Import(stream, _dir, destTech, 1000,
            resolveLayerMapping: rows =>
            {
                var row = Assert.Single(rows);
                // The user overrides the pre-filled "Add to technology" default to "Map to existing".
                var overridden = row with { Choice = new LayoutFragment.LayerReconciliationChoice(LayoutFragment.LayerReconciliationAction.MapToExisting, existingTarget) };
                return LayoutLayerMapping.BuildChoices([overridden]);
            });

        Assert.False(result.Cancelled);
        Assert.Empty(result.LayersToAdd); // nothing added — the override mapped to the EXISTING layer instead
    }

    [Fact]
    public void Import_CrossTechPaste_DefaultStaysKeepAsUnknown_NotChangedByThisBrief()
    {
        // The divergence (R-col-4) is a DxfImport-only decision — LayoutLayerMapping.Propose's own
        // shared default (used by cross-technology paste/retarget) must be completely unaffected.
        var destTech = new Technology { Name = "Dest", Layers = [new LayerDef { Key = new LayerKey(9, 0), Name = "Unrelated" }] };
        var shapes = new List<LayoutShape> { new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 } };
        var sourceLayers = new List<LayerDef> { new() { Key = new LayerKey(1, 0), Name = "Foo" } };

        var rows = LayoutLayerMapping.Propose(shapes, sourceLayers, destTech);
        var row = Assert.Single(rows);
        Assert.Equal(LayerMatchKind.NoMatch, row.Match);
        Assert.Equal(LayoutFragment.LayerReconciliationAction.KeepUnknown, row.Choice.Action);
    }

    // ── Gate 8: ACI 7 (and missing-from-table) fallback — never literal black ──────────────────────

    [Fact]
    public void Import_LayerColorSeven_FallsBackToFallbackPalette_NeverBlack()
    {
        // Matches exactly what THIS application's writer emitted for every layer before §1 was fixed
        // (hardcoded "62 7" on every LAYER record, per the brief's own diagnosis) — a real regression
        // fixture, not a synthetic one.
        using var sw = new StringWriter();
        var w = new DxfGroupWriter(sw);
        w.WriteString(0, "SECTION"); w.WriteString(2, "HEADER"); w.WriteString(0, "ENDSEC");
        w.WriteString(0, "SECTION"); w.WriteString(2, "TABLES");
        w.WriteString(0, "TABLE"); w.WriteString(2, "LAYER"); w.WriteInt(70, 1);
        w.WriteString(0, "LAYER"); w.WriteString(2, "TopCopper"); w.WriteInt(70, 0); w.WriteInt(62, 7);
        w.WriteString(0, "ENDTAB");
        w.WriteString(0, "ENDSEC");
        w.WriteString(0, "SECTION"); w.WriteString(2, "ENTITIES");
        w.WriteString(0, "LWPOLYLINE"); w.WriteString(8, "TopCopper"); w.WriteInt(90, 2); w.WriteInt(70, 1);
        w.WriteDouble(10, 0); w.WriteDouble(20, 0);
        w.WriteDouble(10, 10); w.WriteDouble(20, 10);
        w.WriteString(0, "ENDSEC");
        w.WriteString(0, "EOF");

        var destTech = new Technology { Name = "Dest", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000, Layers = [] };

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sw.ToString()));
        var result = DxfImport.Import(stream, _dir, destTech, 1000,
            resolveLayerMapping: rows => LayoutLayerMapping.BuildChoices(rows));

        Assert.False(result.Cancelled);
        var added = Assert.Single(result.LayersToAdd);
        Assert.NotEqual(new Rgba(0, 0, 0), added.Color);
        Assert.Equal(FallbackPalette.For(added.Key).Color, added.Color);
    }

    [Fact]
    public void Import_LayerMissingFromTable_SameFallbackAsColorSeven()
    {
        // A shape can legally reference a layer name never declared in the LAYER table at all — DXF
        // permits this; the layer table can even be absent entirely. Both must fall back exactly like
        // an explicit ACI 7 would, never a naive black default.
        using var sw = new StringWriter();
        var w = new DxfGroupWriter(sw);
        w.WriteString(0, "SECTION"); w.WriteString(2, "HEADER"); w.WriteString(0, "ENDSEC");
        w.WriteString(0, "SECTION"); w.WriteString(2, "ENTITIES");
        w.WriteString(0, "LWPOLYLINE"); w.WriteString(8, "Undeclared"); w.WriteInt(90, 2); w.WriteInt(70, 1);
        w.WriteDouble(10, 0); w.WriteDouble(20, 0);
        w.WriteDouble(10, 10); w.WriteDouble(20, 10);
        w.WriteString(0, "ENDSEC");
        w.WriteString(0, "EOF");

        var destTech = new Technology { Name = "Dest", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000, Layers = [] };

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sw.ToString()));
        var result = DxfImport.Import(stream, _dir, destTech, 1000,
            resolveLayerMapping: rows => LayoutLayerMapping.BuildChoices(rows));

        Assert.False(result.Cancelled);
        var added = Assert.Single(result.LayersToAdd);
        Assert.NotEqual(new Rgba(0, 0, 0), added.Color);
        Assert.Equal(FallbackPalette.For(added.Key).Color, added.Color);
    }
}
