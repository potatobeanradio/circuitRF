using System;
using System.Linq;
using RfCore.Propagation;
using Xunit;
using Xunit.Abstractions;

namespace RfCore.Tests.Propagation;

/// <summary>
/// Brief PCH-8's gate 4 on the clustered-delay-line tables, at a fixed seed: every profile's rays sum to 1 and have the stated RMS delay
/// spread; stated angle spreads are met by TR 38.901 Annex A's circular definition; translation puts CDL-D's line-of-sight ray on the
/// requested angles; and a realization is a function of (seed, drop, stream) alone.
/// </summary>
public class CdlTests(ITestOutputHelper output)
{
    private const ulong Seed = 5;

    // a line of sight that is not the tables' own: departing at 37° azimuth, 96° zenith; arriving from −143°, 84°
    private static CdlRealization Resolve(CdlProfile p, double ds, CdlAngleSpreads? s = null, double? k = null, long drop = 1)
        => CdlProfiles.Resolve(p, ds, s, k, 37, -143, 96, 84, Seed, drop, "CH1");

    [Fact]
    public void EveryProfile_SumsToOne_AndHasTheStatedDelaySpread()
    {
        foreach (var p in CdlProfiles.All)
            foreach (double ds in new[] { 30e-9, 300e-9 })
            {
                var r = Resolve(p, ds);
                double sum = r.Rays.Sum(x => x.Power), rms = CdlProfiles.RmsDelay(r.Rays);
                output.WriteLine($"{p.Name} at {ds * 1e9:0} ns: {r.Rays.Count} rays, Σp − 1 = {sum - 1:G3}, rms/DS − 1 = {rms / ds - 1:G3}, rescale {r.DelayRescale:G6}");
                Assert.True(Math.Abs(sum - 1) < 1e-12, $"{p.Name}: {sum}");
                Assert.True(Math.Abs(rms / ds - 1) < 1e-9, $"{p.Name}: {rms} against {ds}");
            }
    }

    [Fact]
    public void StatedAngleSpreads_AreMetByAnnexA()
    {
        // a line-of-sight profile holds most of its power at one angle, so its circular spread cannot reach the NLOS values (CDL-D's
        // specular ray is 93 % of the power: |Σ P e^(jφ)| ≥ 0.86, a spread of at most 31°)
        foreach (var name in new[] { "CDL-A", "CDL-B", "CDL-C", "CDL-D", "CDL-E" })
        {
            bool los = CdlProfiles.Find(name)!.HasLos;
            var want = los ? new CdlAngleSpreads(AsdDeg: 8, AsaDeg: 20, ZsdDeg: 2, ZsaDeg: 6) : new CdlAngleSpreads(AsdDeg: 15, AsaDeg: 45, ZsdDeg: 3, ZsaDeg: 10);
            var r = Resolve(CdlProfiles.Find(name)!, 100e-9, want);
            double asd = CdlProfiles.RmsAngle(r.Rays.Select(x => (x.AodDeg, x.Power)));
            double asa = CdlProfiles.RmsAngle(r.Rays.Select(x => (x.AoaDeg, x.Power)));
            double zsd = CdlProfiles.RmsAngle(r.Rays.Select(x => (x.ZodDeg, x.Power)));
            double zsa = CdlProfiles.RmsAngle(r.Rays.Select(x => (x.ZoaDeg, x.Power)));
            output.WriteLine($"{name}: ASD {asd:G12}°, ASA {asa:G12}°, ZSD {zsd:G12}°, ZSA {zsa:G12}°");
            Assert.True(Math.Abs(asd / want.AsdDeg!.Value - 1) < 1e-9, $"{name} ASD {asd}");
            Assert.True(Math.Abs(asa / want.AsaDeg!.Value - 1) < 1e-9, $"{name} ASA {asa}");
            Assert.True(Math.Abs(zsd / want.ZsdDeg!.Value - 1) < 1e-9, $"{name} ZSD {zsd}");
            Assert.True(Math.Abs(zsa / want.ZsaDeg!.Value - 1) < 1e-9, $"{name} ZSA {zsa}");
        }
        // NTN-CDL's departure spreads are zero: an arrival spread scales, a departure spread is refused
        var ntn = Resolve(CdlProfiles.Find("NTN-CDL-B")!, 100e-9, new CdlAngleSpreads(null, 30, null, null));
        Assert.True(Math.Abs(CdlProfiles.RmsAngle(ntn.Rays.Select(x => (x.AoaDeg, x.Power))) / 30 - 1) < 1e-9);
        Assert.Throws<PropagationRefusal>(() => Resolve(CdlProfiles.Find("NTN-CDL-B")!, 100e-9, new CdlAngleSpreads(5, null, null, null)));
        Assert.Throws<PropagationRefusal>(() => Resolve(CdlProfiles.Find("CDL-D")!, 100e-9, new CdlAngleSpreads(null, 45, null, null)));
    }

    [Fact]
    public void Translation_PutsCdlD_LineOfSightOnTheEndsLine()
    {
        var r = Resolve(CdlProfiles.Find("CDL-D")!, 100e-9, new CdlAngleSpreads(8, 20, 2, 6), k: 9);
        var los = Assert.Single(r.Rays, x => x.Los);
        double rad = Math.PI / 180;
        double worst = new[] { los.AodDeg - 37, los.AoaDeg + 143, los.ZodDeg - 96, los.ZoaDeg - 84 }.Max(d => Math.Abs(d)) * rad;
        output.WriteLine($"LOS ray at AOD {los.AodDeg:R}, AOA {los.AoaDeg:R}, ZOD {los.ZodDeg:R}, ZOA {los.ZoaDeg:R}: worst {worst:G3} rad");
        Assert.True(worst < 1e-12, $"{worst} rad");
        Assert.Equal(System.Numerics.Complex.One, los.Mtt);
        Assert.Equal(-System.Numerics.Complex.One, los.Mpp);
        Assert.Throws<PropagationRefusal>(() => Resolve(CdlProfiles.Find("CDL-A")!, 100e-9, k: 9));
        // every other ray moves with it: requesting the table's own line of sight instead shifts each AOD back by 37° and each ZOD by
        // 96° − 98.5°, ray for ray
        var own = CdlProfiles.Resolve(CdlProfiles.Find("CDL-D")!, 100e-9, new CdlAngleSpreads(8, 20, 2, 6), 9, 0, -180, 98.5, 81.5, Seed, 1, "CH1");
        for (int i = 0; i < r.Rays.Count; i++)
        {
            double dAod = Math.IEEERemainder(r.Rays[i].AodDeg - own.Rays[i].AodDeg - 37, 360);
            Assert.True(Math.Abs(dAod) < 1e-9, $"ray {i}: AOD moved by {dAod + 37}");
            Assert.True(Math.Abs(r.Rays[i].ZodDeg - own.Rays[i].ZodDeg - (96 - 98.5)) < 1e-9, $"ray {i}: ZOD");
        }
    }

    [Fact]
    public void ARealization_IsItsDraws()
    {
        var p = CdlProfiles.Find("CDL-C")!;
        var a = Resolve(p, 100e-9, drop: 37);
        var b = Resolve(p, 100e-9, drop: 37);
        var c = Resolve(p, 100e-9, drop: 38);
        Assert.Equal(a.Rays, b.Rays);
        Assert.NotEqual(a.Rays.Select(x => x.Mtt), c.Rays.Select(x => x.Mtt));
        // XPR: CDL-C's 7 dB as the cross terms' magnitude
        var diffuse = a.Rays.First(x => !x.Los);
        Assert.Equal(Math.Pow(10, -7 / 20.0), diffuse.Mtp.Magnitude, 12);
    }
}
