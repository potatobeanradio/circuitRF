// ================================================================
//  FskPresets.cs  —  constant-envelope standards as FSK settings
//
//  Each row is one FskShape: rate, deviation, levels and mapping,
//  frequency pulse and, where the standard sends one, the burst. The
//  data is the card's test pattern (PN9); a sync word the standard
//  derives from an address is a stand-in of the right length.
// ================================================================

using System.Linq;

namespace RfCore.Signals
{
    /// <summary>One standard's FSK settings: its menu, name, the clause it comes from, the centre it moves Fc to
    /// (null = Fc as it is) and the shape.</summary>
    public sealed record FskPreset(string Family, string Name, string Reference, double? CentreHz, FskShape Shape);

    public static class FskPresets
    {
        // dibit (MSB first) 00 -> +1, 01 -> +3, 10 -> -1, 11 -> -3: TIA-102.BAAA-A, ETSI TS 102 361-1 cl. 10.2.1,
        // ETSI TS 102 490 (the 4FSK default, so the LMR rows leave Mapping null)
        // 802.15.4 SUN 4-FSK: 00 -> -1, 01 -> -3, 10 -> +1, 11 -> +3 (IEEE 802.15.4-2020 cl. 19.3)
        private static readonly int[] Sun4Fsk = [-1, -3, 1, 3];

        // stand-in sync word: a Bluetooth sync word is coded from the device address (Core Vol 2 Part B cl. 6.3.3)
        private static readonly string BtSync = string.Concat(Fsk.Prbs(9, 5).Take(64));

        private static FskBurst BtSlot(int payloadBits, double guard, FskSwitch? then = null)
            => new(Preamble: "0101", Sync: BtSync + "1010", PayloadBits: payloadBits, RampSymbols: 2, GuardSymbols: guard, Then: then);

        private static FskShape Bt(FskBurst b) => new(1e6, 160e3, 0.5, "PN9") { Burst = b };

        private static FskShape Sun(double rb, double h, int levels = 2)
            => new(rb, h * rb / (levels == 2 ? 2 : 4) * (levels - 1), 0.5, "PN9")
               { Levels = levels, Mapping = levels == 4 ? Sun4Fsk : null };

        private static FskShape Lmr(double rb, double dev, FskPulse pulse) => new(rb, dev, 0, "PN9") { Levels = 4, Pulse = pulse };

        private static FskShape Pocsag(double rb) => new(rb, 4.5e3, 0, "PN9")
        {
            Mapping = [1, -1],                                                  // a 0 is the upper frequency
            Burst = new(Preamble: "10", PreambleBits: 576, Sync: "01111100110100100001010111011000", PayloadBits: 512),
        };

        private const double GsmRate = 1625e3 / 6;

        public static readonly FskPreset[] All =
        [
            // Bluetooth Core v5.4 Vol 2 Part A cl. 3 (GFSK BT 0.5, h 0.28-0.35; 160 kHz is h 0.32) and Part B cl. 6.4 / 6.7:
            // access code 72 bits, header 54, a DH1 payload of 240; one 625 us slot
            new("Bluetooth", "BR", "Bluetooth Core v5.4 Vol 2 Part A cl. 3.1, Part B cl. 6.4", 2.441e9, Bt(BtSlot(294, 625 - 366))),
            // EDR: the GFSK access code and header, a 5 us guard, then 11 sync + 232 payload (2-DH1 / 3-DH1) + 2 trailer
            // DPSK symbols at 1 Msym/s, RRC 0.4 (Part A cl. 3.2, Part B cl. 6.7)
            new("Bluetooth", "EDR 2 Mb/s", "Bluetooth Core v5.4 Vol 2 Part A cl. 3.2, Part B cl. 6.7", 2.441e9,
                Bt(BtSlot(54, 625 - 126 - 5 - 245, new FskSwitch("PI/4-DQPSK", 1e6, 245)))),
            new("Bluetooth", "EDR 3 Mb/s", "Bluetooth Core v5.4 Vol 2 Part A cl. 3.2, Part B cl. 6.7", 2.441e9,
                Bt(BtSlot(54, 625 - 126 - 5 - 245, new FskSwitch("8DPSK", 1e6, 245)))),

            // ETSI EN 300 175-2 cl. 4 / 5: GFSK BT 0.5, 1.152 Mb/s, 288 kHz (h 0.5); a P32 packet (S 32, D 388, Z 4) in a 480-bit
            // slot; RFP preamble and sync word
            new("DECT", "DECT", "ETSI EN 300 175-2 cl. 4.4, 5.2", 1.897344e9, new FskShape(1.152e6, 288e3, 0.5, "PN9")
            {
                Burst = new(Preamble: "10", PreambleBits: 16, Sync: "1110100110001010", PayloadBits: 392, RampSymbols: 2, GuardSymbols: 56),
            }),

            // ITU-T G.9959 cl. 7.1: R1 9.6 kb/s Manchester (19.2 kchip/s) FSK +-20 kHz, R2 40 kb/s NRZ FSK +-20 kHz,
            // R3 100 kb/s GFSK BT 0.6 +-29 kHz
            new("Z-Wave", "R1 9.6 kb/s", "ITU-T G.9959 cl. 7.1", 868.42e6, new FskShape(19.2e3, 20e3, 0, "PN9") { Manchester = true }),
            new("Z-Wave", "R2 40 kb/s", "ITU-T G.9959 cl. 7.1", 868.40e6, new FskShape(40e3, 20e3, 0, "PN9")),
            new("Z-Wave", "R3 100 kb/s", "ITU-T G.9959 cl. 7.1", 869.85e6, new FskShape(100e3, 29e3, 0.6, "PN9")),

            // IEEE 802.15.4-2020 cl. 19 (SUN FSK) operating modes as the Wi-SUN PHY lists them, GFSK BT 0.5
            new("Wi-SUN", "Mode 1a 50 kb/s h 0.5", "IEEE 802.15.4-2020 cl. 19.3", null, Sun(50e3, 0.5)),
            new("Wi-SUN", "Mode 1b 50 kb/s h 1", "IEEE 802.15.4-2020 cl. 19.3", null, Sun(50e3, 1)),
            new("Wi-SUN", "Mode 2a 100 kb/s h 0.5", "IEEE 802.15.4-2020 cl. 19.3", null, Sun(100e3, 0.5)),
            new("Wi-SUN", "Mode 2b 100 kb/s h 1", "IEEE 802.15.4-2020 cl. 19.3", null, Sun(100e3, 1)),
            new("Wi-SUN", "Mode 3 150 kb/s h 0.5", "IEEE 802.15.4-2020 cl. 19.3", null, Sun(150e3, 0.5)),
            new("Wi-SUN", "Mode 4a 200 kb/s h 0.5", "IEEE 802.15.4-2020 cl. 19.3", null, Sun(200e3, 0.5)),
            new("Wi-SUN", "Mode 4b 200 kb/s h 1", "IEEE 802.15.4-2020 cl. 19.3", null, Sun(200e3, 1)),
            new("Wi-SUN", "Mode 5 300 kb/s h 0.5", "IEEE 802.15.4-2020 cl. 19.3", null, Sun(300e3, 0.5)),
            new("Wi-SUN", "4FSK 200 kb/s h 0.33", "IEEE 802.15.4-2020 cl. 19.3", null, Sun(200e3, 0.33, 4)),

            // 4FSK land mobile radio: +-3 symbols at the stated deviation, +-1 at a third of it
            new("LMR", "P25 Phase 1 C4FM", "TIA-102.BAAA-A (C4FM modulator)", null, Lmr(9600, 1800, FskPulse.C4fm)),
            new("LMR", "DMR", "ETSI TS 102 361-1 cl. 10.2", null, Lmr(9600, 1944, FskPulse.RootRaisedCosine)),
            new("LMR", "dPMR", "ETSI TS 102 490 cl. 6.2", null, Lmr(4800, 1050, FskPulse.RootRaisedCosine)),

            // ITU-R M.584-2: 2FSK +-4.5 kHz NRZ; 576-bit preamble, the sync codeword, one batch of 16 codewords
            new("Paging", "POCSAG 512 b/s", "ITU-R M.584-2 Annex 1", null, Pocsag(512)),
            new("Paging", "POCSAG 1200 b/s", "ITU-R M.584-2 Annex 1", null, Pocsag(1200)),
            new("Paging", "POCSAG 2400 b/s", "ITU-R M.584-2 Annex 1", null, Pocsag(2400)),

            // 3GPP TS 45.004 cl. 2 (GMSK BT 0.3) and TS 45.002 cl. 5.2.3: 3 tail, 58 data, 26 training (TSC 0), 58 data,
            // 3 tail, 8.25 guard bits
            new("GSM", "Normal burst", "3GPP TS 45.004 cl. 2, TS 45.002 cl. 5.2.3", null, new FskShape(GsmRate, GsmRate / 4, 0.3, "PN9")
            {
                Burst = new(Preamble: "000", Sync: "00100101110000100010010111", SyncAt: 58, PayloadBits: 116, Tail: "000",
                            RampSymbols: 2, GuardSymbols: 8.25),
            }),
        ];
    }
}
