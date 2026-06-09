using System.IO;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

public class CellPersistenceTests
{
    // ── Round-trip helpers ────────────────────────────────────────────────────

    private static CcellFile BuildFull()
    {
        return new CcellFile
        {
            IsTestBench      = true,
            PrimarySchematic = "amp.csch",
            PrimarySymbol    = "amp.csym",
            PrimaryLayout    = "amp.clay",
            Parameters =
            {
                new CcellParameter
                {
                    Name              = "W",
                    DefaultExpression = "100e-6",
                    Unit              = "m",
                    Dimension         = UnitDimension.Length,
                    ShowOnSchematic   = true,
                },
                new CcellParameter
                {
                    Name              = "L",
                    DefaultExpression = "50e-9",
                    Unit              = "m",
                    Dimension         = UnitDimension.Length,
                    ShowOnSchematic   = false,
                },
                new CcellParameter
                {
                    Name              = "Ids",
                    DefaultExpression = "10e-3",
                    Unit              = "A",
                    Dimension         = UnitDimension.Current,
                    ShowOnSchematic   = true,
                },
            },
        };
    }

    // ── Round-trip: Serialize / Deserialize ───────────────────────────────────

    [Fact]
    public void Serialize_Deserialize_RoundTrips_AllFields()
    {
        var original = BuildFull();
        string json  = CellPersistence.Serialize(original);
        var restored = CellPersistence.Deserialize(json);

        Assert.Equal(CellPersistence.CurrentFormatVersion, restored.FormatVersion);
        Assert.True(restored.IsTestBench);
        Assert.Equal("amp.csch", restored.PrimarySchematic);
        Assert.Equal("amp.csym", restored.PrimarySymbol);
        Assert.Equal("amp.clay", restored.PrimaryLayout);

        Assert.Equal(3, restored.Parameters.Count);

        var w = restored.Parameters[0];
        Assert.Equal("W",              w.Name);
        Assert.Equal("100e-6",         w.DefaultExpression);
        Assert.Equal("m",              w.Unit);
        Assert.Equal(UnitDimension.Length, w.Dimension);
        Assert.True(w.ShowOnSchematic);

        var l = restored.Parameters[1];
        Assert.Equal("L",              l.Name);
        Assert.Equal("50e-9",          l.DefaultExpression);
        Assert.False(l.ShowOnSchematic);

        var ids = restored.Parameters[2];
        Assert.Equal(UnitDimension.Current, ids.Dimension);
    }

    [Fact]
    public void Serialize_EmptyCell_RoundTrips()
    {
        var empty    = new CcellFile();
        string json  = CellPersistence.Serialize(empty);
        var restored = CellPersistence.Deserialize(json);

        Assert.Empty(restored.Parameters);
        Assert.Null(restored.PrimarySchematic);
        Assert.Null(restored.PrimarySymbol);
        Assert.Null(restored.PrimaryLayout);
        Assert.False(restored.IsTestBench);
    }

    [Fact]
    public void Serialize_NullPrimaries_NotWrittenToJson()
    {
        var cell   = new CcellFile { PrimarySymbol = "x.csym" };
        string json = CellPersistence.Serialize(cell);

        Assert.DoesNotContain("PrimarySchematic", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PrimaryLayout",    json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PrimarySymbol",          json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_EnumAsString_Roundtrips()
    {
        var cell = new CcellFile
        {
            Parameters = { new CcellParameter { Dimension = UnitDimension.Resistance } },
        };
        string json  = CellPersistence.Serialize(cell);
        var restored = CellPersistence.Deserialize(json);

        Assert.Equal(UnitDimension.Resistance, restored.Parameters[0].Dimension);
        Assert.Contains("Resistance", json);   // enum written as string, not integer
    }

    [Fact]
    public void Deserialize_IsTestBench_Roundtrips()
    {
        var tb = new CcellFile { IsTestBench = true };
        var restored = CellPersistence.Deserialize(CellPersistence.Serialize(tb));
        Assert.True(restored.IsTestBench);

        var notTb = new CcellFile { IsTestBench = false };
        var restored2 = CellPersistence.Deserialize(CellPersistence.Serialize(notTb));
        Assert.False(restored2.IsTestBench);
    }

    // ── format_version mismatch ───────────────────────────────────────────────

    [Fact]
    public void Deserialize_WrongFormatVersion_ThrowsInvalidDataException()
    {
        var cell = new CcellFile();
        string json   = CellPersistence.Serialize(cell);
        string broken = json.Replace("\"FormatVersion\": 1", "\"FormatVersion\": 999");

        var ex = Assert.Throws<InvalidDataException>(() => CellPersistence.Deserialize(broken));
        Assert.Contains("999",            ex.Message);
        Assert.Contains("Regenerate",     ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deserialize_MalformedJson_ThrowsInvalidDataException()
    {
        Assert.ThrowsAny<Exception>(() => CellPersistence.Deserialize("not json at all"));
    }

    // ── File I/O ──────────────────────────────────────────────────────────────

    [Fact]
    public void SaveToFile_LoadFromFile_RoundTrips()
    {
        var original = BuildFull();
        var tmp      = Path.GetTempFileName();
        try
        {
            CellPersistence.SaveToFile(tmp, original);
            var restored = CellPersistence.LoadFromFile(tmp);

            Assert.True(restored.IsTestBench);
            Assert.Equal(3, restored.Parameters.Count);
            Assert.Equal("amp.csym", restored.PrimarySymbol);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // ── Id is not persisted ───────────────────────────────────────────────────

    [Fact]
    public void Serialize_DoesNotPersistId()
    {
        var cell = new CcellFile();
        string json = CellPersistence.Serialize(cell);
        // CcellFile has no Id field by design — just assert no "Id" key leaks in.
        Assert.DoesNotContain("\"Id\"", json, StringComparison.OrdinalIgnoreCase);
    }
}
