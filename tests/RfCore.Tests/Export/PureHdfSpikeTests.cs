// PureHDF spike — Condition 1 of the Phase 5-7 approval.
//
// Proves that PureHDF 2.1.2 can:
//   (A) Write a v7.3/HDF5 file (it always writes OHDR version 2 / superblock 2, which
//       is what MATLAB calls "v7.3").
//   (B) Write a compound {real:f64, imag:f64} complex dataset and read it back correctly.
//   (C) Write a variable-length string dataset and read it back correctly.
//   (D) Write a multi-dimensional double dataset and read it back correctly.
//
// CI check: C# round-trip only (no MATLAB/Octave in CI).
// Manual check for MATLAB: load the file produced by
//   PureHdfSpike_WritesFile_Manual() — set WRITE_FILE=true — and run in MATLAB:
//     f = load('spike.mat','-mat');   % .h5 renamed .mat works too
//     f = h5read('spike.h5','/complex_data');  % returns struct with .real/.imag
//
// If any assertion fails below, STOP and report — the .mat exporter design depends on
// PureHDF being able to write these two dataset kinds.

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using PureHDF;
using Xunit;

namespace RfCore.Tests.Export;

/// <summary>
/// PureHDF write/round-trip spike — Condition 1 of the Phase 5-7 approval.
/// </summary>
public class PureHdfSpikeTests
{
    // ── Compound type exactly as MATLAB expects (lowercase field names) ──────

    /// <summary>
    /// HDF5 compound type that MATLAB reads as complex double.
    /// Field names must be lowercase "real" and "imag" for MATLAB compatibility.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ComplexEntry
    {
        public double real;
        public double imag;

        public ComplexEntry(Complex c) { real = c.Real; imag = c.Imaginary; }
        public Complex ToComplex() => new(real, imag);
    }

    // ── (A+B) Complex compound dataset — write & read-back ───────────────────

    [Fact]
    public void Spike_A_B_ComplexCompound_RoundTrips()
    {
        // Arrange
        var source = new Complex[]
        {
            new(1.0,  2.0),
            new(3.5, -4.5),
            new(0.0,  1e9),
            new(-7.0, 0.0),
        };

        var entries = source.Select(c => new ComplexEntry(c)).ToArray();

        using var tmp = new TempHdf5File("spike_complex");

        // Act — write
        var file = new H5File
        {
            ["complex_data"] = entries    // PureHDF reflects struct fields → compound type
        };
        file.Write(tmp.Path);

        // Act — read back
        using var readFile = H5File.OpenRead(tmp.Path);
        var readBack = readFile.Dataset("complex_data").Read<ComplexEntry[]>();

        // Assert
        Assert.Equal(source.Length, readBack.Length);
        for (int i = 0; i < source.Length; i++)
        {
            Assert.Equal(source[i].Real,      readBack[i].real,      precision: 15);
            Assert.Equal(source[i].Imaginary, readBack[i].imag,      precision: 15);
        }
    }

    // ── (A+C) Variable-length string dataset — write & read-back ────────────

    [Fact]
    public void Spike_A_C_StringDataset_RoundTrips()
    {
        // Arrange — node names with special characters that appear in circuitRF
        var names = new string[]
        {
            "X1.drain",
            "X1.gate",
            "I:X1.M1:d",
            "__tuner_T1_bias",
            "n_supply",
        };

        using var tmp = new TempHdf5File("spike_strings");

        // Act — write
        var file = new H5File
        {
            ["node_names"] = names        // PureHDF writes as variable-length string dataset
        };
        file.Write(tmp.Path);

        // Act — read back
        using var readFile = H5File.OpenRead(tmp.Path);
        var readBack = readFile.Dataset("node_names").Read<string[]>();

        // Assert
        Assert.Equal(names.Length, readBack.Length);
        for (int i = 0; i < names.Length; i++)
            Assert.Equal(names[i], readBack[i]);
    }

    // ── (D) Multi-dimensional double dataset — write & read-back ────────────
    //   Verifies that shape information survives the round-trip.
    //   In PureHDF: use H5Dataset to declare shape; Read<double[,]> gives it back.

    [Fact]
    public void Spike_D_MultiDimDouble_RoundTrips()
    {
        // Arrange: 3 sweep points × 4 harmonics, stored row-major
        const int S = 3, K = 4;
        var src = new double[S, K];
        for (int si = 0; si < S; si++)
        for (int k  = 0; k  < K; k++)
            src[si, k] = si * 10 + k;

        using var tmp = new TempHdf5File("spike_multidim");

        // Act — write
        var file = new H5File
        {
            ["bSrc"] = src
        };
        file.Write(tmp.Path);

        // Act — read back
        using var readFile = H5File.OpenRead(tmp.Path);
        var readBack = readFile.Dataset("bSrc").Read<double[,]>();

        // Assert
        Assert.Equal(S, readBack.GetLength(0));
        Assert.Equal(K, readBack.GetLength(1));
        for (int si = 0; si < S; si++)
        for (int k  = 0; k  < K; k++)
            Assert.Equal(src[si, k], readBack[si, k], precision: 15);
    }

    // ── (A+B+C) Combined — full-structure round-trip (mirrors the .mat layout) ─

    [Fact]
    public void Spike_Combined_GroupedLayout_RoundTrips()
    {
        // Arrange — mirror the /dataset/ group layout from data-export.md §2.2
        var complexCube = new ComplexEntry[]
        {
            new(new Complex(1.0, 0.5)), new(new Complex(2.0, 1.0)), new(new Complex(3.0, 1.5)),
            new(new Complex(4.0, 2.0)), new(new Complex(5.0, 2.5)), new(new Complex(6.0, 3.0)),
        };
        // shape [3 nodes × 2 harmonics] — PureHDF needs explicit shape for multidim struct
        var realCube  = new double[] { 0.1, 0.2, 0.3 };  // PAE [3 sweep points]
        var nodeNames = new[] { "n_gate", "n_drain", "n_bias" };
        var branchNames = new[] { "L:X1.Lchoke", "V:X1.Vbias" };

        using var tmp = new TempHdf5File("spike_combined");

        // Act — write
        var file = new H5File
        {
            ["dataset"] = new H5Group
            {
                ["V"]            = complexCube,
                ["PAE"]          = realCube,
                ["__axes__"] = new H5Group
                {
                    // axes stored as JSON string attribute on a dataset — mimic the design
                    ["V"] = new H5Group
                    {
                        Attributes = new()
                        {
                            ["axes_json"] = "[{\"name\":\"node\",\"unit\":\"\",\"values\":[1,2,3]},{\"name\":\"harmonic\",\"unit\":\"\",\"values\":[0,1]}]"
                        }
                    }
                },
                ["__linear_network__"] = new H5Group
                {
                    ["node_names"]   = nodeNames,
                    ["branch_names"] = branchNames,
                }
            }
        };
        file.Write(tmp.Path);

        // Act — read back
        using var readFile = H5File.OpenRead(tmp.Path);
        var readComplex  = readFile.Group("dataset").Dataset("V").Read<ComplexEntry[]>();
        var readReal     = readFile.Group("dataset").Dataset("PAE").Read<double[]>();
        var readNodes    = readFile.Group("dataset").Group("__linear_network__").Dataset("node_names").Read<string[]>();
        var readBranches = readFile.Group("dataset").Group("__linear_network__").Dataset("branch_names").Read<string[]>();
        var axesJson     = readFile.Group("dataset").Group("__axes__").Group("V").Attribute("axes_json").Read<string>();

        // Assert
        Assert.Equal(complexCube.Length, readComplex.Length);
        for (int i = 0; i < complexCube.Length; i++)
        {
            Assert.Equal(complexCube[i].real, readComplex[i].real, precision: 15);
            Assert.Equal(complexCube[i].imag, readComplex[i].imag, precision: 15);
        }
        Assert.Equal(realCube, readReal);
        Assert.Equal(nodeNames, readNodes);
        Assert.Equal(branchNames, readBranches);
        Assert.Contains("\"name\":\"node\"", axesJson);
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    /// <summary>Temp HDF5 file that deletes itself on dispose.</summary>
    private sealed class TempHdf5File(string prefix) : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}.h5");

        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}
