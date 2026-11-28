// ================================================================
//  MultitonePresets.cs  —  the tones-and-noise presets, as data
//
//  Each is a MultitoneShape the Signal Designer can offer as one
//  click; every value is stated here, none is computed elsewhere.
// ================================================================

using System.Collections.Generic;

namespace RfCore.Signals
{
    public sealed record MultitonePreset(string Name, string Description, MultitoneShape Shape);

    public static class MultitonePresets
    {
        public static IReadOnlyList<MultitonePreset> All { get; } =
        [
            new("Two-tone", "two equal tones 1 MHz apart, in phase: IMD3 and intercept",
                new MultitoneShape { Tones = 2, SpacingHz = 1e6, Phase = TonePhase.Zero }),
            new("Three-tone", "three equal tones 1 MHz apart, Newman phases",
                new MultitoneShape { Tones = 3, SpacingHz = 1e6, Phase = TonePhase.Newman }),
            new("10-tone", "ten equal tones 1 MHz apart, Newman phases",
                new MultitoneShape { Tones = 10, SpacingHz = 1e6, Phase = TonePhase.Newman }),
            new("100-tone NPR", "100 tones 100 kHz apart (10 MHz), random phases, the ten tones within ±0.5 MHz of the centre left out",
                new MultitoneShape { Tones = 100, SpacingHz = 100e3, Phase = TonePhase.Random, NotchWidthHz = 1e6 }),
            new("Noise NPR, 36 MHz", "Gaussian noise over a 36 MHz transponder with a 1 % (360 kHz) notch at the centre, 1 ms",
                new MultitoneShape { Tones = 0, NoiseBandwidthHz = 36e6, NotchWidthHz = 360e3, RecordSeconds = 1e-3 }),
            new("AWGN 20 MHz", "band-limited white Gaussian noise over 20 MHz, 1 ms",
                new MultitoneShape { Tones = 0, NoiseBandwidthHz = 20e6, RecordSeconds = 1e-3 }),
            new("CW + AWGN", "a CW tone with 20 MHz of Gaussian noise 20 dB below it (C/N 20 dB), 1 ms",
                new MultitoneShape { Tones = 1, SpacingHz = 1e6, Phase = TonePhase.Zero, NoiseBandwidthHz = 20e6, NoiseDb = -20, RecordSeconds = 1e-3 }),
        ];
    }
}
