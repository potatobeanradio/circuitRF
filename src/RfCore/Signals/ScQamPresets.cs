// ================================================================
//  ScQamPresets.cs  —  standards' single carriers as SC shapes
//
//  Each preset is the modulation, pulse, rotation and framing a
//  standard sends, with PN data in place of its coding and its own
//  known sequences (training, headers, Golay guards) replaced by the
//  SC carrier's QPSK known symbols. Bits are mapped in circuitRF's
//  Gray order (first bit on I); a standard's own table may swap I and
//  Q or relabel the points, which changes no statistic of the
//  waveform. Where a standard sets a spectral mask rather than a pulse
//  the roll-off chosen is said.
// ================================================================

using System.Collections.Generic;

namespace RfCore.Signals
{
    /// <summary>A standard's single carrier: its name, the document it follows, and the shape.</summary>
    public sealed record ScPreset(string Name, string Source, ScShape Shape);

    public static class ScQamPresets
    {
        private static ScShape Sc(double rs, ScModulation mod, double rollOff = ScQam.DefaultRollOff, string? rings = null)
            => ScQam.Resolve(new CarrierSpec { Standard = CarrierStandard.Sc, SymbolRateHz = rs, ScModulation = mod, RollOff = rollOff, Rings = rings });

        /// <summary>An EDGE normal burst: 3 + 58 data, 26 training, 58 + 3 data symbols (the training put first here),
        /// 8.25 symbols of guard holding 4-symbol ramps; all eight timeslots on.</summary>
        private static ScShape Edge(ScModulation mod, double rotation)
            => ScQam.Close(Sc(1625e3 / 6, mod) with
            {
                Pulse = ScPulse.LinearGmsk, RotationDeg = rotation, Preamble = 26, FrameData = 122, Guard = 8.25, Ramp = 4,
            });

        /// <summary>A DVB-S2 PL frame: a 90-symbol header, the FECFRAME's 64,800 bits as symbols, 36-symbol pilot blocks
        /// after every 16 slots of 90.</summary>
        private static ScShape DvbS2(ScModulation mod, double rollOff, string? rings = null)
            => ScQam.Close(Sc(27.5e6, mod, rollOff, rings) with { Preamble = 90, PilotLength = 36, PilotEvery = 1440, FrameData = 64800 / ScQam.BitsPerSymbol(mod) });

        /// <summary>The IEEE 802.11 DMG single carrier: 1.76 Gsym/s, π/2 rotation, 512-symbol blocks of a 64-symbol guard
        /// interval and 448 data symbols; roll-off 0.2 (the standard sets a mask, not a pulse).</summary>
        private static ScShape Dmg(ScModulation mod)
            => ScQam.Close(Sc(1.76e9, mod, 0.2) with { RotationDeg = 90, Preamble = 64, FrameData = 448 });

        public static IReadOnlyList<ScPreset> All { get; } =
        [
            new("EDGE 8PSK", "3GPP TS 45.004 (8PSK: 3π/8 rotation, linearised GMSK pulse); TS 45.002 (normal burst)",
                Edge(ScModulation.Psk8, 67.5)),
            new("EDGE Evolution 16QAM", "3GPP TS 45.004 (16QAM: π/4 rotation, linearised GMSK pulse at 271 ksym/s)",
                Edge(ScModulation.Qam16, 45)),
            new("TETRA π/4-DQPSK", "ETSI EN 300 392-2, clause 5 (18 ksym/s, π/4-DQPSK, root raised cosine 0.35)",
                ScQam.Close(Sc(18e3, ScModulation.Qpsk, 0.35) with { Differential = true })),
            new("IEEE 802.15.4 O-QPSK 2.4 GHz", "IEEE 802.15.4-2020, clause 12 (2 Mchip/s O-QPSK, half-sine chips); chip pairs as symbols, no spreading",
                ScQam.Close(Sc(1e6, ScModulation.Qpsk) with { Pulse = ScPulse.HalfSine, Offset = true })),
            new("Bluetooth EDR 2 Mb/s", "Bluetooth Core Specification, Vol 2, Part A (EDR payload: π/4-DQPSK, 1 Msym/s, root raised cosine 0.4)",
                ScQam.Close(Sc(1e6, ScModulation.Qpsk, 0.4) with { Differential = true })),
            new("Bluetooth EDR 3 Mb/s", "Bluetooth Core Specification, Vol 2, Part A (EDR payload: 8DPSK, 1 Msym/s, root raised cosine 0.4)",
                ScQam.Close(Sc(1e6, ScModulation.Psk8, 0.4) with { Differential = true })),
            new("IEEE 802.11ad SC π/2-BPSK", "IEEE 802.11-2020, clause 20 (DMG single carrier)", Dmg(ScModulation.Bpsk)),
            new("IEEE 802.11ad SC π/2-QPSK", "IEEE 802.11-2020, clause 20 (DMG single carrier)", Dmg(ScModulation.Qpsk)),
            new("IEEE 802.11ad SC π/2-16QAM", "IEEE 802.11-2020, clause 20 (DMG single carrier)", Dmg(ScModulation.Qam16)),
            new("DVB-S2 QPSK, α 0.35", "ETSI EN 302 307-1, clauses 5.4-5.6", DvbS2(ScModulation.Qpsk, 0.35)),
            new("DVB-S2 8PSK, α 0.25", "ETSI EN 302 307-1, clauses 5.4-5.6", DvbS2(ScModulation.Psk8, 0.25)),
            new("DVB-S2 16APSK 3/4, α 0.20", "ETSI EN 302 307-1, clauses 5.4-5.6 (ring ratio 2.85)", DvbS2(ScModulation.Apsk16, 0.20, "2.85")),
            new("DVB-S2 32APSK 4/5, α 0.20", "ETSI EN 302 307-1, clauses 5.4-5.6 (ring ratios 2.72, 4.87)", DvbS2(ScModulation.Apsk32, 0.20, "2.72,4.87")),
            new("DVB-S2X 16APSK 2/3, α 0.15", "ETSI EN 302 307-2 (roll-off 0.15; ring ratio 3.15)", DvbS2(ScModulation.Apsk16, 0.15, "3.15")),
            new("DVB-S2X 8PSK, α 0.10", "ETSI EN 302 307-2 (roll-off 0.10)", DvbS2(ScModulation.Psk8, 0.10)),
            new("DVB-S2X QPSK, α 0.05", "ETSI EN 302 307-2 (roll-off 0.05)", DvbS2(ScModulation.Qpsk, 0.05)),
            new("π/2-BPSK", "π/2 rotation per symbol, root raised cosine 0.35", ScQam.Close(Sc(1e6, ScModulation.Bpsk) with { RotationDeg = 90 })),
            new("SOQPSK-TG 10 Mb/s", "IRIG 106, Chapter 2 (SOQPSK-TG)", Sc(5e6, ScModulation.SoqpskTg)),
        ];
    }
}
