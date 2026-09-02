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
}
