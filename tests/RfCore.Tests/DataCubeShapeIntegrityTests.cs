// ================================================================
//  DataCubeShapeIntegrityTests.cs
//  A cube that cannot describe itself consistently must say so at the READ, not walk off its buffer.
//
//  The field report these exist for (Windows, five rounds) is a bare IndexOutOfRangeException out of
//  the gather, on a cube every diagnostic prints as well formed and a slice that is in range on its
//  face. The checks below are the ones that would have to fail first for that state to be reachable
//  at all — so if a later report still shows the gather throwing, these having passed is itself the
//  finding.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using RfCore.Data;
using Xunit;

namespace RfCore.Tests;

public sealed class DataCubeShapeIntegrityTests
{
    private static Axis Ax(string name, int len)
        => new(name, Enumerable.Range(0, len).Select(i => (double)i).ToArray());

    private static DataCube SParamShaped(int nFreq = 101, int nPorts = 2)
        => new(new[] { Ax("freq", nFreq), Ax("i", nPorts), Ax("j", nPorts) },
               new Complex[nFreq * nPorts * nPorts]);

    private static void Poke(DataCube c, string field, object value)
        => typeof(DataCube).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
                           .SetValue(c, value);

    [Fact]
    public void TheReportedShapeAndSlice_ResolveCleanly()
    {
        // The exact state five field trails describe. It must not throw — which is what makes the
        // report a contradiction rather than a shape bug, and is worth pinning so it stays one.
        var cube = SParamShaped();
        var r = cube[new object[] { Range.All, 0, 0 }];
        Assert.Equal(1, r.Cube!.Rank);
        Assert.Equal(101, r.Cube.BufferLength);
    }

    [Fact]
    public void AShortBuffer_IsNamedAtTheRead_NotWalkedOff()
    {
        var cube = SParamShaped();
        Poke(cube, "_complexData", new Complex[400]);   // axes claim 404

        var ex = Assert.Throws<InvalidOperationException>(
            () => cube[new object[] { Range.All, 0, 0 }]);
        Assert.Contains("404", ex.Message);
        Assert.Contains("400", ex.Message);
        Assert.Contains("freq[101]", ex.Message);
    }

    [Fact]
    public void StridesThatDisagreeWithTheAxes_AreCaught_EvenWhenTheElementCountIsRight()
    {
        // The hole the leading-stride check could not see: the buffer length is exactly right, so
        // every count-based test passes, while the gather walks a shape no diagnostic prints.
        var cube = SParamShaped();
        Poke(cube, "_strides", new[] { 4, 4, 1 });      // correct would be 4,2,1

        var ex = Assert.Throws<InvalidOperationException>(
            () => cube[new object[] { Range.All, 0, 0 }]);
        Assert.Contains("strides", ex.Message);
        Assert.Contains("4,2,1", ex.Message);           // what the axes imply
        Assert.Contains("4,4,1", ex.Message);           // what the cube stored
    }

    [Fact]
    public void AGatherThatWalksOffAnInRangeSlice_SaysSoInThoseWords()
    {
        // The message the next field report will carry if the gather throws again. It cannot be
        // provoked through the public API any more — the shape check above catches every reachable
        // way to get there — which is precisely why the wording is pinned here: this branch exists to
        // be read in a crash trail, and it must state that the read was in range rather than blame a
        // shape that is fine.
        var cube = SParamShaped();
        var ranges = new (bool, int, int, int)[] { (false, 0, 0, 101), (true, 0, 0, 1), (true, 0, 0, 1) };
        var surviving = new[] { Ax("freq", 101) };

        var m = typeof(DataCube).GetMethod("GatherWalkedOff",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var ex = (Exception)m.Invoke(cube, new object[]
        {
            ranges, surviving, 101, new IndexOutOfRangeException("Index was outside the bounds of the array.")
        })!;

        Assert.Contains("BOTH INDICES ARE IN RANGE", ex.Message);
        Assert.Contains("max source index 400 of 404", ex.Message);
        Assert.Contains("max destination index 100 of 101", ex.Message);
        Assert.Contains("strides [4,2,1]", ex.Message);
        Assert.IsType<IndexOutOfRangeException>(ex.InnerException);   // the real stack survives

        // The two facts the closed form above cannot supply, and the reason a sixth field report
        // will be readable: every index pair the walk visits, checked one at a time against the live
        // buffers, and the outcome of running the identical gather again.
        Assert.Contains("checked walk: every one of 101 index pairs is in range", ex.Message);
        Assert.Contains("src<=400 of 404", ex.Message);
        Assert.Contains("dst<=100 of 101", ex.Message);
        Assert.Contains("replay: SUCCEEDED on a fresh buffer", ex.Message);
        Assert.Contains("NOT reproducible from this state", ex.Message);

        // Object identity and the buffer's real runtime type. S, Z and Y in one group have the same
        // shape and the same strides, so up to here every message this branch produced was equally
        // consistent with all three; the caller records the identity of the cube it handed to the
        // indexer, and the pair is what settles which one threw.
        Assert.Matches(@"id=0x[0-9a-f]{8}", ex.Message);
        Assert.Contains("buffer Complex[404]", ex.Message);
    }

    [Fact]
    public void AGatherThatGenuinelyOverruns_IsNotReportedAsImpossible()
    {
        var cube = SParamShaped();
        var ranges = new (bool, int, int, int)[] { (false, 0, 0, 200), (true, 0, 0, 1), (true, 0, 0, 1) };
        var surviving = new[] { Ax("freq", 200) };

        var m = typeof(DataCube).GetMethod("GatherWalkedOff",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var ex = (Exception)m.Invoke(cube, new object[]
        {
            ranges, surviving, 200, new IndexOutOfRangeException()
        })!;

        Assert.DoesNotContain("BOTH INDICES ARE IN RANGE", ex.Message);
        Assert.Contains("reaches past the buffer", ex.Message);

        // A real overrun names the pair that faulted, not just the maximum: the 102nd of 200 pairs
        // is the first to leave the 404-element buffer, and the fault repeats.
        Assert.Contains("FIRST OFFENDING PAIR at walk position [101] (pair 102 of 200)", ex.Message);
        Assert.Contains("src=404 of 404", ex.Message);
        Assert.Contains("dst=101 of 200", ex.Message);
        Assert.Contains("replay: threw IndexOutOfRangeException again", ex.Message);
        Assert.Contains("deterministic on this state", ex.Message);
    }

    [Fact]
    public void BufferLength_ReportsTheRealBuffer_WithoutCopyingIt()
    {
        var cube = SParamShaped();
        Assert.Equal(404, cube.BufferLength);
        Poke(cube, "_complexData", new Complex[7]);
        Assert.Equal(7, cube.BufferLength);
    }

    [Fact]
    public void MutatingTheCallersAxisArray_CannotDesyncAxesFromStrides()
    {
        var axes = new[] { Ax("freq", 4), Ax("i", 2) };
        var cube = new DataCube(axes, new Complex[8]);

        axes[1] = Ax("i", 99);                          // the caller keeps its array and edits it

        Assert.Equal(new[] { "freq", "i" }, cube.Axes.Select(a => a.Name));
        Assert.Equal(2, cube.Axes[1].Length);
        var r = cube[new object[] { Range.All, 0 }];     // still consistent, still readable
        Assert.Equal(4, r.Cube!.BufferLength);
    }

    // ---- The walk itself ------------------------------------------------------------------

    /// <summary>
    /// Every slice of every shape, against index arithmetic the test derives ITSELF from the axis
    /// lengths — not from <c>DataCube</c>'s own strides. The gather stopped being a recursive
    /// dimension walk and became a planned one with block copies, so "it agrees with the old
    /// arithmetic" is not a check worth having; agreeing with an independent oracle is.
    /// </summary>
    [Theory]
    [InlineData(601)]                 // the shapes five field trails name
    [InlineData(601, 1, 1)]
    [InlineData(101, 2, 2)]
    [InlineData(4, 3, 2)]             // every stride distinct, so a transposed walk cannot pass
    [InlineData(1, 1, 1)]
    [InlineData(3, 1, 4, 2)]          // rank 4, with a degenerate axis in the middle
    public void TheGather_MatchesAnIndependentIndexOracle(params int[] shape)
    {
        int rank    = shape.Length;
        int total   = shape.Aggregate(1, (a, b) => a * b);
        var strides = new int[rank];
        for (int d = rank - 1, s = 1; d >= 0; s *= shape[d], d--) strides[d] = s;

        // Each element carries its own flat index, so a misplaced element is identifiable rather
        // than merely unequal.
        var data = new Complex[total];
        for (int i = 0; i < total; i++) data[i] = new Complex(i, -i);
        var cube = new DataCube(
            Enumerable.Range(0, rank).Select(d => Ax($"d{d}", shape[d])).ToArray(), data);

        foreach (var combo in Combinations(shape))
        {
            var args = combo.Select(c => c.arg).ToArray();

            // The expected destination order: dense row-major over the surviving axes. Walking ALL
            // dimensions in order, with a pinned one contributing its single index, produces exactly
            // that sequence.
            var expected = new List<int>();
            var idx = new int[rank];
            while (true)
            {
                int flat = 0;
                for (int d = 0; d < rank; d++) flat += combo[d].idx[idx[d]] * strides[d];
                expected.Add(flat);

                int k = rank - 1;
                for (; k >= 0; k--)
                {
                    if (++idx[k] < combo[k].idx.Length) break;
                    idx[k] = 0;
                }
                if (k < 0) break;
            }

            var result = cube[args];
            string what = $"[{string.Join(", ", args.Select(a => a is Range r ? $"{r}" : a.ToString()))}]"
                        + $" on {string.Join("x", shape)}";

            if (args.All(a => a is int))
            {
                Assert.Single(expected);
                Assert.Equal(new Complex(expected[0], -expected[0]), result.ComplexValue);
                continue;
            }

            var got = result.Cube!.ComplexValues;
            Assert.Equal(expected.Count, got.Length);
            for (int i = 0; i < got.Length; i++)
                Assert.Equal(new Complex(expected[i], -expected[i]), got[i]);

            // The result's own axes must describe what was gathered, not merely be the right length.
            Assert.Equal(args.Count(a => a is Range), result.Cube.Rank);
            Assert.Equal(expected.Count, result.Cube.BufferLength);
            Assert.NotEqual("", what);   // keeps `what` meaningful if an assertion above is edited
        }
    }

    /// <summary>
    /// Per-dimension slice arguments paired with the source indices each one selects: whole axis,
    /// first, last, and an interior narrowing where the axis is long enough to have one.
    /// </summary>
    private static IEnumerable<(object arg, int[] idx)[]> Combinations(int[] shape)
    {
        var perDim = shape.Select(len =>
        {
            var list = new List<(object arg, int[] idx)>
            {
                (DataCube.All, Enumerable.Range(0, len).ToArray()),
                (0,            new[] { 0 }),
                (len - 1,      new[] { len - 1 }),
            };
            if (len >= 3)
                list.Add((new Range(1, len - 1), Enumerable.Range(1, len - 2).ToArray()));
            return list;
        }).ToArray();

        var pick = new int[shape.Length];
        while (true)
        {
            yield return Enumerable.Range(0, shape.Length).Select(d => perDim[d][pick[d]]).ToArray();

            int k = shape.Length - 1;
            for (; k >= 0; k--)
            {
                if (++pick[k] < perDim[k].Count) break;
                pick[k] = 0;
            }
            if (k < 0) yield break;
        }
    }

    [Fact]
    public void AnAxisOfLengthZero_GathersNothing_RatherThanCopyingARun()
    {
        // The empty result the recursive walk got for free from a for-loop that ran no times. The
        // planned walk has to say it: the INNER axis here is 3 long, so a plan that copied its run
        // before noticing the outer axis is empty would block-copy 3 elements into a 0-length buffer.
        var cube = new DataCube(new[] { Ax("freq", 0), Ax("i", 3) }, Array.Empty<Complex>());

        var r = cube[new object[] { Range.All, Range.All }];

        Assert.Equal(2, r.Cube!.Rank);
        Assert.Equal(0, r.Cube.BufferLength);
    }
}
