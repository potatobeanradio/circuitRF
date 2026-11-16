// ================================================================
//  Drops.cs  —  what one drop of a Propagation Channel draws (brief PCH-10 R-pch10-1, design note §8.1)
//
//  A drop is one realization of everything that is constant over a record and varies from place to place or from
//  one hour to the next: the shadowing, the line-of-sight state, the rain. Each is a stateless draw,
//  StatDraws.Uniform(Seed, Drop, Id("<channel>:<quantity>"), k), so drop N drawn alone is drop N in a batch, and a
//  quantity added later moves no other draw.
//
//    shadowing   X = σ·Φ⁻¹(u): the large-scale model's σ (dB), added to the loss (a positive X is more loss)
//    los         line of sight when u < P_LOS, the model's line-of-sight probability
//    rain        p = 100·u % of the time; the attenuation exceeded for p % (the method's own exceedance curve), zero
//                where p lies beyond the method's range (P.530 to 1 %, P.618 to 5 %), clamped at its lower end
// ================================================================

using System;
using RfCore.Statistics;

namespace RfCore.Propagation
{
    public static class DropDraws
    {
        /// <summary>The uniform draw (0, 1) of <paramref name="quantity"/> of <paramref name="channel"/> in drop <paramref name="drop"/>.</summary>
        public static double Uniform(ulong seed, long drop, string channel, string quantity, int k = 0)
            => StatDraws.Uniform(seed, drop, StatDraws.Id(channel + ":" + quantity), k);

        /// <summary>A standard normal draw, by the quantile of the uniform one (never infinite: the uniform is open).</summary>
        public static double Normal(ulong seed, long drop, string channel, string quantity, int k = 0)
            => Statistics.Normal.InverseCdf(Uniform(seed, drop, channel, quantity, k));

        /// <summary>The shadowing of one drop, dB: σ times a standard normal draw (0 when σ is 0).</summary>
        public static double Shadowing(double sigmaDb, ulong seed, long drop, string channel)
            => sigmaDb > 0 ? sigmaDb * Normal(seed, drop, channel, "shadowing") : 0;

        /// <summary>The line-of-sight state of one drop: true with probability <paramref name="pLos"/>.</summary>
        public static bool LineOfSight(double pLos, ulong seed, long drop, string channel)
            => Uniform(seed, drop, channel, "los") < pLos;

        /// <summary>The time percentage one drop's rain is taken at, uniform on (0, 100).</summary>
        public static double RainTimePercent(ulong seed, long drop, string channel)
            => 100 * Uniform(seed, drop, channel, "rain");
    }
}
