using System.Text;

namespace RfCore.Statistics;

/// <summary>
/// Counter-based random draws (yield overview D5): a draw is a pure function <c>Hash(seed, trial, stream, k) → uniform</c>, not
/// the next number of a generator. So trial N is the same drawn alone or in a batch, in any order and on any thread, and a
/// stream's draws do not depend on which other streams exist.
///
/// <para><b>Here, in RfCore, because the propagation channel's fading generator needs it</b> (brief PCH-7, design note §8.1:
/// one stream per channel instance, tap and quantity) and RfCore cannot reach the yield engine.
/// <c>CircuitRF.Engine.Statistics.StatStreams</c> and <c>SplitMix64.Mix</c> forward here, so there is one implementation and
/// every draw the yield engine made before the move is the same draw after it.</para>
/// </summary>
public static class StatDraws
{
    /// <summary>The 64-bit golden ratio, the SplitMix64 increment.</summary>
    public const ulong Golden = 0x9E3779B97F4A7C15UL;

    /// <summary>The SplitMix64 output mix: a bijection of 64-bit words that scatters every input bit over every output bit,
    /// fixed on every platform and runtime.</summary>
    public static ulong Mix(ulong z)
    {
        unchecked
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>A stream's id: FNV-1a over the UTF-8 bytes of <paramref name="text"/>, then mixed, so similar keys land far
    /// apart.</summary>
    public static ulong Id(string text)
    {
        unchecked
        {
            ulong h = 0xCBF29CE484222325UL;
            foreach (byte b in Encoding.UTF8.GetBytes(text))
            {
                h ^= b;
                h *= 0x100000001B3UL;
            }
            return Mix(h + Golden);
        }
    }

    /// <summary>The 64 bits of draw <paramref name="k"/> of <paramref name="stream"/> in <paramref name="trial"/> under
    /// <paramref name="seed"/>. Each input is folded in through its own mix, so no two input tuples are related by a simple
    /// shift.</summary>
    public static ulong Hash(ulong seed, long trial, ulong stream, int k)
    {
        unchecked
        {
            ulong h = Mix(seed + Golden);
            h = Mix(h ^ ((ulong)trial + 2 * Golden));
            h = Mix(h ^ (stream + 3 * Golden));
            return Mix(h ^ ((ulong)(uint)k + 4 * Golden));
        }
    }

    /// <summary>Uniform on the OPEN interval (0, 1) — the top 53 bits, offset by half a step — so its normal quantile is always
    /// finite.</summary>
    public static double Uniform(ulong seed, long trial, ulong stream, int k = 0)
        => ((Hash(seed, trial, stream, k) >> 11) + 0.5) * (1.0 / (1UL << 53));
}
