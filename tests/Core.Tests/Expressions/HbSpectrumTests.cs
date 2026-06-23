// ================================================================
//  HbSpectrumTests.cs
//  Gate tests for brief-hb-spectrum-1-tone-metadata — Part B
//
//  4. HbSpectrum_Reconstruct
// ================================================================

using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Expressions;

public class HbSpectrumTests
{
    // ── 4. HbSpectrum_Reconstruct ─────────────────────────────────────────────
    // The static helper must compute harmonic frequencies and mixing products
    // correctly; it is the single source of truth for the index→frequency rule.

    [Fact]
    public void HbSpectrum_Reconstruct()
    {
        // Single-tone: 2nd harmonic at 5.5 GHz fundamental = 11 GHz.
        Assert.Equal(11e9, HbSpectrum.HarmonicFreqHz(2, 5.5e9));

        // DC (order 0): always 0 Hz.
        Assert.Equal(0.0, HbSpectrum.HarmonicFreqHz(0, 5.5e9));

        // Single-tone fundamental (order 1): same as f0.
        Assert.Equal(5.5e9, HbSpectrum.HarmonicFreqHz(1, 5.5e9));

        // Two-tone: (k1=1, k2=-1) at (2 GHz, 2.1 GHz) → 2e9 - 2.1e9 = -0.1 GHz = -1e8 Hz.
        Assert.Equal(-1e8, HbSpectrum.MixFreqHz(1, -1, 2e9, 2.1e9), 1.0);

        // Two-tone: (k1=1, k2=1) sum → 4.1 GHz.
        Assert.Equal(4.1e9, HbSpectrum.MixFreqHz(1, 1, 2e9, 2.1e9), 1.0);
    }
}
