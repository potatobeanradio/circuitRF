// ================================================================
//  TouchstonePortInferenceTests.cs
//
//  A Touchstone row for N > 2 is split across several physical lines, so port count CANNOT be
//  inferred from the data alone — it must come from the file's own extension. A caller reading
//  through a TextReader (a storage-API stream, where the filename never reaches the reader) has to
//  supply it. This is not theoretical: the Data Display's file-picker / drag-drop path read a
//  stream WITHOUT it, so every .s3p/.s4p failed to parse and was swallowed by a catch — while the
//  path-based loader beside it, which does pass it, worked. These tests pin the underlying fact.
// ================================================================

using System;
using System.IO;
using Xunit;
using RfCore;

namespace RfCore.Tests;

public sealed class TouchstonePortInferenceTests
{
    /// <summary>A 4-port MA file in canonical layout: one frequency per record, 4 lines of 4 pairs.</summary>
    private static string FourPortText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# GHz S MA R 50");
        for (int i = 0; i < 3; i++)
        {
            double f = 1.0 + i;
            for (int r = 0; r < 4; r++)
            {
                sb.Append(r == 0 ? $"{f:F2}  " : "        ");
                for (int c = 0; c < 4; c++) sb.Append("0.50000    -10.000  ");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    [Theory]
    [InlineData("dut.s1p", 1)]
    [InlineData("dut.s2p", 2)]
    [InlineData("dut.s3p", 3)]
    [InlineData("dut.s4p", 4)]
    [InlineData("dut.s16p", 16)]
    [InlineData("dut.npy", null)]
    [InlineData("dut", null)]
    public void ParsePortsFromExtension_DeclaresTheCount(string path, int? expected)
        => Assert.Equal(expected, TouchstoneIO.ParsePortsFromExtension(path));

    [Fact]
    public void Read_FourPortStream_WithoutKnownPorts_Fails()
    {
        // The regression this guards: inference from the data alone cannot see past the first line.
        using var rdr = new StringReader(FourPortText());
        Assert.ThrowsAny<Exception>(() => TouchstoneIO.Read(rdr));
    }

    [Fact]
    public void Read_FourPortStream_WithPortsFromExtension_Succeeds()
    {
        using var rdr = new StringReader(FourPortText());
        var snp = TouchstoneIO.Read(rdr, TouchstoneIO.ParsePortsFromExtension("dut.s4p"));

        Assert.Equal(4, snp.Ports);
        Assert.Equal(3, snp.Frequencies.Length);
    }

    [Fact]
    public void Read_TwoPortStream_WithoutKnownPorts_StillWorks()
    {
        // The 2-port case always worked, which is exactly why the N > 2 gap went unnoticed.
        string text = "# GHz S MA R 50\n"
                    + "1.00  0.5 -10  2.0 90  0.05 20  0.4 -30\n"
                    + "2.00  0.5 -20  1.8 80  0.05 10  0.4 -40\n";
        using var rdr = new StringReader(text);
        var snp = TouchstoneIO.Read(rdr);

        Assert.Equal(2, snp.Ports);
        Assert.Equal(2, snp.Frequencies.Length);
    }
}
