---
title: The Signal Designer
slug: reference/signal-designer.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Signal Designer
lede: The signal a MOD_Source plays - LTE, NR and their uplinks and test models, Wi-Fi, WCDMA, GNSS, 802.11b, 802.15.4, Bluetooth, FSK, LoRa, Sigfox, a single PSK, QAM or APSK carrier, broadcast OFDM, analog and avionics carriers, radar pulses and FMCW, tones and noise, or your own I/Q file - with its spectrum, CCDF and quality reading before you simulate.
keywords: Signal Designer, MOD_Source, Wi-Fi, WLAN, 802.11a, 802.11n, 802.11ac, 802.11ax, 802.11be, MCS, spectral mask, GNSS, GPS, L1 C/A, L5, Galileo E1, BeiDou B1I, C/N0, code phase, WCDMA, UMTS, test model, PCDE, code-domain power, 802.11b, CCK, DSSS, 802.15.4, O-QPSK, Zigbee, EDGE, TETRA, DVB-S2, DVB-S2X, linearised GMSK, burst, Bluetooth BR, EDR, DECT, Z-Wave, Wi-SUN, P25, C4FM, DMR, dPMR, POCSAG, 4FSK, AM, FM, SSB, VOR, ILS, localizer, glide path, DDM, DME, NPR, noise power ratio, AWGN, multitone, Schroeder, Barker, Frank, P3, P4, Zadoff-Chu, PSL, ISL, NLFM, staggered PRI, DFS, FMCW, uplink, DFT-s-OFDM, SC-FDMA, PUSCH, pi/2-BPSK, E-TM, NR-FR1-TM, TDD, NB-IoT, LTE-M, DVB-T, DVB-T2, ISDB-T, DAB, WiMAX, LFM, linear FM, chirped pulse, FR2, 120 kHz, mmWave, two-tone, N-tone, multitone, intermodulation, IMD, intercept, IP3, Newman phases, pulsed CW, pCW, pulse, radar, PRI, PRF, duty, pulse-top power, FSK, CPFSK, GFSK, GMSK, MSK, PN9, PN15, wireless M-Bus, metering, LoRa, LoRaWAN, Sigfox, Bluetooth LE, BLE, LE 1M, LE 2M, LE Coded, LPWAN, DBPSK, BPSK, UNB, ultra-narrowband, CSS, chirp, chirp spread spectrum, spreading factor, SF, IoT, single carrier, SC, root raised cosine, RRC, roll-off, matched filter, 8PSK, APSK, 16APSK, 32APSK, satellite, PN23, preamble, pilot block, gated CCDF, Gaussian reference, modulated source, LTE, NR, OFDM, I/Q, IQ file, custom waveform, Load Custom, sample rate, PAPR, CCDF, clipping, ACLR, ACPR, EVM, constellation, envelope, export
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#opening">Opening the Designer</a></li>
<li><a href="#source">The Source box</a></li>
<li><a href="#presets">Presets</a></li>
<li><a href="#custom">A custom I/Q file</a></li>
<li><a href="#waveform">Clipping and spectral shaping</a></li>
<li><a href="#carriers">Carriers</a></li>
<li><a href="#pulse">Pulsed CW</a></li>
<li><a href="#fsk">FSK</a></li>
<li><a href="#dbpsk">DBPSK (Sigfox)</a></li>
<li><a href="#css">Chirp spread spectrum (LoRa)</a></li>
<li><a href="#sc">A single carrier (PSK, QAM, APSK)</a></li>
<li><a href="#family-cards">The family cards</a></li>
<li><a href="#ofdm-options">LTE and NR options, OFDM numerologies</a></li>
<li><a href="#wlan">Wi-Fi (IEEE 802.11)</a></li>
<li><a href="#dsss">WCDMA, 802.11b and 802.15.4</a></li>
<li><a href="#sc-options">SC pulses, rotation and bursts</a></li>
<li><a href="#fsk-options">FSK levels, pulses and packets</a></li>
<li><a href="#gnss">GNSS</a></li>
<li><a href="#analog">Analog and avionics</a></li>
<li><a href="#tones-noise">Tones and noise</a></li>
<li><a href="#radar">Radar waveforms and FMCW</a></li>
<li><a href="#preview">The spectrum and the CCDF</a></li>
<li><a href="#evm">The EVM tab</a></li>
<li><a href="#export">Export signal</a></li>
</ol>
</nav>

## Opening the Designer {#opening}

The Signal Designer edits the signal a [MOD_Source](components.html#modsource) plays in an
[Envelope analysis](simulations.html#envelope). Select the source and press **Design signal…** in the
inspector. The left of the window defines the signal; the right previews it. Every edit previews by
itself after a short pause.

Generation runs in the background — shown in the window's footer and on a live row of the Messages pane —
and is cached. A simulation of the same signal reuses the cached waveform instead of generating it again.
**Apply** writes the signal to the source as one undoable step; **Apply & Close** does the same and closes
the window. At the top of the left pane, **Export signal** saves the signal as an I/Q file and **?**
opens this page.

**Tools ▸ Signal Designer** opens the Designer on its own, with no source behind it and no workspace
needed. It starts on one LTE 20 MHz carrier. Design a signal there, or read one in with
[Load custom](#custom) to see its spectrum and CCDF, and **Export signal** to save it as an I/Q file.
There is nothing to apply it to, so **Apply** and **Apply & Close** are not shown, and a file you load is
remembered by its full path. Several can be open at once, and each is listed in the **Window** menu.

{{ui: signal-designer}}

## The Source box {#source}

- **Centre Fc** and **Pavg (dBm)** — the centre frequency the carriers sit around, and the total average
  available power behind the source impedance. Both accept a number or an expression (`RFfreq`, `Pin`),
  which the Designer writes back exactly as typed, so a swept source stays swept. For a
  [pulsed-CW](#pulse) source the field reads **Pulse-top power (dBm)**: the power on the flat top of the
  pulse, with the record average and the power duty shown beneath it.
- **Record (ms)** — the periodic record length, in whole 0.5 ms slots. ACPR settles within about 0.1 dB
  from 0.5 ms; a longer record tightens the statistics.
- **Sample rate** — the rate of the preview and of the export. **Auto** picks the recommended rate for the
  signal (122.88 MS/s for LTE 20 MHz); the list holds the standard rates from 7.68 to 1966.08 MS/s, and,
  while a custom I/Q file is loaded, **File rate** — the file's own. The
  line under it shows the rate in use, the sample period and the samples per record. A rate that cannot
  hold the signal is refused, naming the lowest one that can. It is a Designer setting: it is not written
  to the source, and the simulation picks its own time step. It is not the
  [file sample rate](#custom) of a custom I/Q file, which says how the file was recorded.

## Presets {#presets}

One click replaces the carriers with a common case. The presets are in one row per family. A family of several
modes is one button with a menu, one item per mode. An item for a standard with a channel moves **Centre Fc** to
it; if Fc is an expression, it is left as you typed it.

- **Cellular** — **LTE 20 MHz**, **NR 20 MHz (30 kHz)**, **NR 100 MHz (30 kHz)**, **NR 100 MHz (120 kHz, FR2)**
  (64QAM, [FR2](#nr-fr2)), **4 x LTE 20 contiguous**,
  or **2C NR 20 at spacing**: two NR 20 MHz carriers the number of MHz apart in the field beside it, centre
  to centre. Then four menus:
  - **Uplink** — LTE SC-FDMA, NR CP-OFDM and DFT-s-OFDM (QPSK, π/2-BPSK, a partial allocation): see
    [LTE and NR options](#ofdm-options).
  - **Test models** — every NR-FR1-TM and E-TM on a 20 MHz carrier, and E-TM3.1 with its TDD pattern.
  - **NB-IoT / LTE-M** — the NB-IoT uplink at 12 tones or one, at 15 or 3.75 kHz, and LTE-M narrowbands.
  - **WCDMA** — the test models TM1 to TM6 at 2140 MHz and the 12.2 kb/s uplink at 1950 MHz: see [WCDMA](#dsss).
- **Connectivity** — **Wi-Fi** (802.11a, n, ac, ax and be: see [Wi-Fi](#wlan)), **802.11b** (1 to 11 Mb/s at
  2412 MHz), **Bluetooth BR / EDR**, **802.15.4** (2405 MHz), **Z-Wave**, **Wi-SUN**, **DECT**, **802.11ad** and
  **WiMAX**.
- **IoT** — each sets one carrier and moves **Centre Fc** to the mode's channel.
  - **Wireless M-Bus** — a menu of the [FSK](#fsk) metering modes.
  - **Bluetooth LE** — a menu: **LE 1M / LE Coded** and **LE 2M**, as [GFSK](#fsk) at 2.44 GHz.
  - **LoRaWAN** — a menu: a [LoRa](#css) packet at **SF7** or **SF12**, 125 kHz at 868.1 MHz (EU868).
  - **Sigfox** — the RC1 uplink: [DBPSK](#dbpsk) at 100 b/s, 868.13 MHz.
- **Navigation** — **GNSS** (GPS L1 C/A, GPS L5, Galileo E1, BeiDou B1I: see [GNSS](#gnss)), **VOR / ILS**
  and **DME**.
- **Broadcast** — **DVB-T / ISDB-T / DAB** (the [OFDM numerologies](#ofdm-options)), **DVB-S2 / S2X** and
  **Telemetry** (π/2-BPSK, SOQPSK-TG) as [SC](#sc-options) carriers.
- **Radio / LMR** — **P25 / DMR / dPMR** and **POCSAG** ([FSK](#fsk-options)), **TETRA** and the **GSM / EDGE**
  bursts, and **FM / AM / SSB** ([analog](#analog)).
- **Radar** — **Pulsed CW**, one [pulse train](#pulse), and the menus **Codes**, **FM**, **PRI**, **DFS ETSI**,
  **DFS FCC** and **FMCW**: see [Radar waveforms](#radar).
- **Tones, noise** — **N tones**: the number of equal CW tones in the field beside it (2 by default), the spacing in
  the next field apart, centred on `Fc`, for intermodulation and intercept tests: see [N tones](#tones). The
  **Multitone / NPR / AWGN** menu sets one [TONES](#tones-noise) carrier.

## A custom I/Q file {#custom}

The **Custom I/Q** card makes an I/Q file you supply the whole signal: a capture from an instrument, a
waveform you wrote yourself, or one you exported here earlier. Press the file button at the left of the
card and pick the file. It is played around `Fc` at the power `Pavg` sets, exactly as a generated signal
would be. While no file is loaded the card is empty and the carriers make the signal.

{{ui: signal-designer-custom}}

**The file.** Plain text, one sample per line: the in-phase and quadrature values, separated by spaces, a
tab or a comma. Lines starting with `#` or `%` are comments, and two of them are read:

- `# sample_rate_hz 200e6` — the rate the samples were taken at;
- `# channel_bw_hz 40e6` — the channel bandwidth. Without it, the bandwidth is measured from the file:
  twice the furthest edge of the band holding 99 % of the power, over 0.9.

The scale does not matter: the samples are normalised to unit mean power and `Pavg` sets the level. The
file is one periodic record, so its length is the record: 200,000 samples at 200 MS/s play a 1 ms
record, and the last sample wraps to the first.

**The Custom I/Q card** shows the file, its sample rate, and what was read: the samples, the record and the
channel bandwidth.

- **File sample rate (MHz)** is the rate the file's samples were recorded at. It is filled from the file's `# sample_rate_hz` line when it has one. A file with no
  such line cannot be played until you enter its rate here — the footer says so. A rate you enter wins
  over the header. Type a number in MHz (`122.88`) or a number with its own unit (`1.5 GHz`,
  `30720000 Hz`). An entry that is not a rate is outlined, its tooltip says why, and the signal keeps the
  last valid rate.
- The **×** at the right of the file removes it. The signal goes back to the carriers it had before you loaded the
  file (one LTE 20 MHz carrier if the source was opened on a file), and everything the file disabled is
  enabled again. **Apply** then takes the file's reference out of the schematic.

**What is disabled, and why.** While a custom file is loaded, the **Presets**, the **Carriers** and
**Record** are greyed: the file is the whole signal and its length is the record. **Spectral shaping** is
greyed too, as it shapes generated OFDM symbols. Everything else still applies:

- **Clip to PAPR** clips the file to a target peak-to-average ratio, by the same in-band peak cancellation
  a generated carrier gets;
- **Centre Fc**, **Pavg** and the preview's **Sample rate**;
- the spectrum, the CCDF and **Export signal**.

**Two sample rates, two jobs.** The **file sample rate** describes the file and is saved with the source.
The Source box's **Sample rate** is the rate the Designer previews and exports at, and is not saved. A
custom file is resampled to the Source rate for the preview and the export — band-limited and exact, since
the record is periodic — so a file recorded at 200 MS/s exported at 245.76 MS/s holds the same 1 ms
record in 245,760 samples. To preview and export the file at its own rate, with no resampling at all,
choose **File rate** in the Source box's list: it is offered, second after Auto, for as long as a custom
file is loaded, and follows the file sample rate when you change it. A Source rate that does not give a
whole number of samples over the file's record is refused.

The EVM tab has nothing to show for a custom file: it carries no reference symbols to compare against.

**Where the reference is kept.** The schematic stores the file the way it stores a Touchstone file:
relative to the workspace when the file is inside it, or at most two folders above it, and as a full path
otherwise, so a workspace you move or share keeps finding it. In the netlist the source's carrier reads:

```
Carrier[1]="FILE;File=iq/sc_64qam_40Msym.txt;Fs=200MHz;Off=0;P=0;Seed=1"
```

**Editing the file.** A file changed on disk is read again on the next preview or run; the cache never
serves the old one.

**In the Envelope analysis** the record is stepped at the usual LTE / NR rate (1.92 MHz × 2ᵏ) when that
divides it. When it does not — 12,345 samples at 100 MS/s, say — the step is the file's own rate × 2ᵏ,
which always gives a whole number of samples per record.

## Clipping and spectral shaping {#waveform}

- **Clip to PAPR (dB)** — off, the signal keeps its natural peak-to-average ratio (about 11–12 dB for
  OFDM). On, iterative in-band peak cancellation brings the peak down to the target, measured at 8×
  oversampling so peaks between samples count too. Clipping adds in-band error, which the EVM tab shows.
- **Spectral shaping floor (dBc)** — a ceiling outside the occupied band, so the source's own ACLR sits
  far below the amplifier's and what the simulation reports is the amplifier.

## Carriers {#carriers}

Each card is one carrier: its standard (**LTE**, **NR**, **CW**, **PCW**, **FSK**, **DBPSK**, **CSS**, **SC**, **WLAN**,
**WLAN11B**, **OQPSK154**, **WCDMA**, **GNSS**, **ANALOG**, **TONES**, **FMCW**, **OFDM** or **FILE**), channel bandwidth, NR
subcarrier spacing, modulation, offset from `Fc`, relative power and random-data seed. **+** adds a
carrier beside the last; **×** removes one. With a single carrier, **Power** is disabled — it is relative
to the others, and `Pavg` is the total. Carriers more than three channel bandwidths apart get their own
envelope fundamental in the simulation. Click a card to show that carrier on the EVM tab.

A **FILE** card puts an I/Q file beside generated carriers, at its own offset and relative power. Mixed
with LTE or NR carriers, the file must last a whole number of 0.5 ms slots.

{{ui: signal-designer-2c}}

### NR subcarrier spacing and FR2 {#nr-fr2}

An NR card offers 15, 30, 60 and 120 kHz, and its
bandwidth list follows the spacing. 15 to 60 kHz carry the FR1 bandwidths of 3GPP TS 38.101-1, up to 100 MHz.
120 kHz is FR2 (TS 38.101-2): 50, 100, 200 and 400 MHz, with 32, 66, 132 and 264 resource blocks. FR2 also
allows 60 kHz at 50, 100 and 200 MHz. Of those, only 200 MHz (264 RB) is added to the 60 kHz list: 50 and 100 MHz
are FR1 bandwidths at 60 kHz as well and keep FR1's resource blocks (65 and 135, where FR2 has 66 and 132). The
FFT is the smallest power of two holding the subcarriers over 0.85: 1024 points, 122.88 MS/s, for 100 MHz at
120 kHz, and 4096 points, 491.52 MS/s, for 400 MHz. At 120 kHz a slot lasts 0.125 ms, so each 0.5 ms of the
record holds four slots, 56 symbols, with the longer cyclic prefix on the first. The record stays in whole
0.5 ms, as for every OFDM carrier, and the EVM tab demodulates it the same way.

### N tones {#tones}

The **N tones** preset replaces the carriers with N **CW** cards of equal power, tone k (counting from 0) at
`Fc` + (k − (N − 1)/2) × spacing: two tones 1 MHz apart sit at ±0.5 MHz. They are ordinary CW cards, so each can
be moved or changed after the preset, and the envelope run plays them as it plays any CW carriers.

**The phases.** Two tones start in phase. Their relative phase only moves the beat in time, and the PAPR is
3.01 dB at any phase. N tones all in phase peak together once in every beat period, 10 log N dB above the
average: 9 dB for eight tones. So from three tones up, tone k starts at Newman's phase, 180 k² / N degrees.
That spreads the peaks and keeps the PAPR at 3.7 dB for three tones, 3.0 dB for four and 2.4 to 2.7 dB from five up.

In the netlist a tone's starting phase is the CW card's `Phase=`, in degrees, and is written only when it is not 0:

```
Carrier[1]="CW;Off=-1MHz;P=0;Seed=1"
Carrier[2]="CW;Phase=60;Off=0;P=0;Seed=1"
Carrier[3]="CW;Phase=240;Off=1MHz;P=0;Seed=1"
```

The card does not show the phase, and editing the card keeps it. A carrier's offset snaps to a multiple of
1 / Record (1 kHz at the default 1 ms), so the spacing is exact when every tone's offset is such a multiple.

## Pulsed CW {#pulse}

A **PCW** card is a pulse train: a CW carrier switched on and off, the stimulus a pulsed-radar amplifier is
specified with. The **Pulsed CW** preset replaces the carriers with one, or choose **PCW** in a card's standard
list. The record is one pulse repetition interval, which repeats, so the Envelope analysis's
**Stop** sets how many pulses are simulated. Its fields take a number in µs, or a number with its unit
(`50 us`, `0.5 ms`, `500 ns`):

| Field | Meaning | Default |
|---|---|---|
| **PRI** | pulse repetition interval. **PRF** is shown beside it | 100 µs |
| **Width** | pulse width, from the 50 % point of the rising edge to the 50 % point of the falling edge. The duty is shown beside it | 10 µs |
| **Rise**, **Fall** | the 10–90 % times of the raised-cosine edges | 100 ns |
| **Delay** | the off-time before the rising edge leaves zero. Empty centres the pulse in the PRI, with equal off-time either side | centred |
| **Chirp (MHz)** | a linear-FM sweep over the width, centred on the carrier: positive sweeps up, negative down. 0 is a plain pulse | 0 |

The envelope is exactly zero in the off-time and exactly one on the flat top. A pulse that cannot be
generated is refused on the field it names: a width as long as the PRI, an edge longer than half the width,
a pulse that ends past the PRI, a zero Delay, or a PRI that no 1.92 MHz × 2ᵏ sample rate divides into
whole samples. Delay must be greater than zero because an envelope run starts from harmonic balance at
the first sample of the record: in the off-time that is the quiescent point, so the first pulse starts
cold.

**Power is the pulse top.** A pulsed-CW record is normalised to unit power on the flat top, so the
source's `Pavg` is the pulse-top available power — the number a radar specification states — and
`Pin_avg_dBm` and each IProbe's `Pavg_dBm` in the results stay averages over the whole run, off-time included. Harmonic balance
and loadpull play a MOD_Source as a CW tone at `Pavg`, which for a pulse is the top: the steady state of
a pulse that never ends.

**A chirp.** With a chirp of B, the frequency moves along a straight line from B/2 below the carrier at the
rising edge's 50 % point to B/2 above it at the falling edge's, or the other way for a negative B. The phase is
π B / PW × (t − Tc)², a parabola about the pulse's centre Tc. Only the phase changes. |envelope| is the plain
pulse's, so the gated CCDF, the PAPR, the power duty and the **Pulse** tab, which plots |envelope|, read the
same with or without the chirp. In an envelope run the pulse measurements stay what they are. The top, droop,
rise and fall are read from the delivered power. The phase droop is the output's phase relative to the drive,
so the chirp itself drops out and what remains is the circuit's own phase across the sweep. The chirp shows in
the spectrum, which widens to the sweep. The carrier's bandwidth is the sweep plus the edges' band, over 0.9
(below), and the native rate holds at least twice it. A pulse has no symbols, so with or without a chirp there
is no EVM.

**Its bandwidth is its edges'.** The faster edge sets the carrier's bandwidth (twice the band holding
99 % of a raised-cosine edge's power, over 0.9, as an I/Q file's is measured), and the envelope rate, the
preview's span and the sample rate follow from it. The native rate is the smallest 1.92 MHz × 2ᵏ that
puts at least ten samples in the faster edge and a whole number in the PRI.

A pulsed carrier is the source's only carrier: a pulse beside a modulated carrier has no clear power or
record. With one on the card, **Record**, **Clip to PAPR**, **Spectral shaping** and **+** are disabled:
the record is the PRI, and a pulse is neither clipped nor spectrally shaped. A pulse draws no random data,
so its card has no **Seed**.

In the netlist the card is

```
Carrier[1]="PCW;PRI=100us;PW=50us;Rise=0.5us;Fall=0.5us;Delay=10us;Off=0;P=0"
```

with `Chirp=20MHz` (or `Chirp=-20MHz`, a frequency) for a chirp, written only when it is not 0. Two keys the
card does not show set the [droop window](simulations.html#envelope-pulse):
`MeasureStart` and `MeasureEnd`, fractions of the flat top (0.1 and 0.9 by default).

Phase codes, nonlinear FM, Gaussian pulses and pulse trains are the card's **Advanced** fields: see
[Radar waveforms](#radar). Not modelled: pulsing the gate or drain bias with the RF, which is a pulsed bias
source rather than a signal.

## FSK {#fsk}

An **FSK** card is binary frequency-shift keying with continuous phase: each bit holds the frequency at
`Fc + Dev` (a 1) or `Fc − Dev` (a 0) for one bit period, and the phase never jumps. Choose **FSK** in a
card's standard list, or a mode from the **Wireless M-Bus** or **Bluetooth LE** preset.

| Field | Meaning | Default |
|---|---|---|
| **Bit rate (kb/s)** | the bit (chip) rate, Rb | 100 |
| **Deviation (kHz)** | the peak frequency deviation, Dev | 45 |
| **Filter** | **None (CPFSK)**: the frequency steps at each bit edge. **Gaussian**: the steps are smoothed by a Gaussian filter of bandwidth-time product **BT** | None |
| **Data** | **PN9** (511 bits), **PN15** (32,767 bits), or **Fixed bits**, a pattern of 0s and 1s you type | PN9 |

The line under the fields gives the modulation index h = 2 Dev / Rb, what the settings make, the bits
and the record. With a Gaussian filter it is **GFSK**, and with h = 0.5 it is **GMSK**: GSM's is
270.833 kb/s, ±67.708 kHz, BT 0.3. Without the filter h = 0.5 is MSK.

The **Wireless M-Bus** menu (EN 13757-4) sets one FSK carrier on PN9 for a metering mode and moves
**Centre Fc** to its channel. If Fc is an expression, the menu leaves it as you typed it.

| Mode | Direction | Fc | Chip rate | Deviation | Filter |
|---|---|---|---|---|---|
| C1 | meter → other | 868.95 MHz | 100 kchip/s | ±45 kHz | none (2-FSK) |
| C2 | other → meter | 869.525 MHz | 50 kchip/s | ±25 kHz | Gaussian, BT 0.5 |
| T1 | meter → other | 868.95 MHz | 100 kchip/s | ±50 kHz | none |
| T2 | other → meter | 868.3 MHz | 32.768 kchip/s | ±50 kHz | none |

C1 meters only transmit; C2 meters also listen, on the other-to-meter link. Only that link is Gaussian
filtered. T mode is the same split.

The **Bluetooth LE** menu sets one GFSK carrier at 2.44 GHz, the middle of the 2.4 GHz band,
with h = 0.5 and BT 0.5:

| Mode | Symbol rate | Deviation | Filter |
|---|---|---|---|
| LE 1M / LE Coded | 1 Msym/s | ±250 kHz | Gaussian, BT 0.5 |
| LE 2M | 2 Msym/s | ±500 kHz | Gaussian, BT 0.5 |

LE Coded (125 kb/s at S = 8, 500 kb/s at S = 2) sends the LE 1M air interface: its coding is in the bits,
so an RF front end sees the same signal. Classic Bluetooth's basic rate is the same FSK card at 1 Mb/s,
±160 kHz, BT 0.5.

The data is a test pattern, not a telegram. There is no preamble, sync word or 3-out-of-6 coding,
because an RF front end responds to the modulation and does not depend on what the bits mean.

**The record is one period of the data**: 5.11 ms for PN9 at 100 kb/s, 80 µs for an eight-bit pattern at
the same rate. It repeats, so the Envelope analysis's **Stop** sets how much is simulated. Over a record
the phase gains π h for each 1 and loses it for each 0, and that rarely adds up to a whole turn: PN9 has one
more 1 than 0s. So the record is closed by moving the whole carrier by at most half of one over the record,
98 Hz for PN9 at 100 kb/s. The seam is then as smooth as any bit edge.

**Bandwidth and rate.** The carrier's channel bandwidth is Carson's, 2 (Dev + Rb): 290 kHz for C1. The
preview's span, the envelope rate and the ACPR bands follow from it. The record is sampled at Rb × 2ᵏ,
at least 32 samples per bit and eight times the bandwidth. A 511-bit record holds a whole number of
samples at none of the standard sample rates, so while an FSK carrier is on the card the Source box's
list offers **Native rate**, second after Auto, and Auto uses it.

FSK has a constant envelope: its PAPR is 0 dB, and its CCDF is a step at 0 dB. As with a pulse, it is
the source's only carrier, and **Record**, **Clip to PAPR**, **Spectral shaping** and **+** are
disabled. A PN sequence draws no random data, so the card has no **Seed**. The second preview tab reads
**Frequency** and shows the instantaneous frequency over the first 64 bits, where the Gaussian
filter's rounding of each bit edge can be seen.

In the netlist the card is

```
Carrier[1]="FSK;Rb=100kbps;Dev=45kHz;BT=0.5;Data=PN9;Off=0;P=0"
```

Four- and eight-level FSK, other frequency pulses and standards' packets (bursts with ramps, Bluetooth EDR) are
the card's **Advanced** fields: see [FSK levels, pulses and packets](#fsk-options). AM and PM are the
[ANALOG](#analog) card's.

## DBPSK (Sigfox) {#dbpsk}

A **DBPSK** card is differential binary phase-shift keying at a low bit rate: the Sigfox uplink, an
ultra-narrowband (UNB) signal at 100 b/s in the 868 MHz band (RC1). A **0** reverses the phase and a **1**
keeps it. Choose **DBPSK** in a card's standard list, or the **Sigfox** preset, which also moves
**Centre Fc** to 868.13 MHz.

| Field | Meaning | Default |
|---|---|---|
| **Bit rate (b/s)** | the bit rate, Rb: 100 in the 868 MHz band, 600 where the rules allow it | 100 |
| **Data** | **PN9**, **PN15** or **Fixed bits**, as on an [FSK](#fsk) card | PN9 |

**The reversals are smooth.** Each bit is a raised-cosine pulse two bits long, centred on its bit, so two
equal bits in a row hold a flat envelope and a reversal passes through zero along a cosine across the bit
edge. The envelope therefore dips at each 0 and nowhere else: the PAPR of PN9 is 1.25 dB. Phase flips
with no shaping would spread 99 % of the power over ±9 Rb; shaped, 99 % lies within ±0.7 Rb, and the
channel bandwidth is the pulse's main lobe, 2 Rb (200 Hz at 100 b/s). The record is real-valued.

**The record is one period of the data**, or two when one period holds an odd number of 0s: the phase
would then end reversed and the record would not repeat. PN9 has 255 0s, so its record is 1,022 bits,
10.22 s at 100 b/s. The record is sampled at 32 samples per bit (3.2 kS/s), which the Source box offers
as **Native rate** and **Auto** uses. A standard rate is refused for so long a record. The envelope analysis steps at that
native rate too, not at the 1.92 MHz the LTE and NR ladder starts from, so 50 ms (five bits) is a short run.

The second preview tab reads **Waveform** and shows the in-phase waveform over the first 32 bits. The
spectrum's offset axis reads in Hz. As with FSK, a DBPSK carrier is the source's only carrier, it is not
clipped, and the card has no **Seed**. The data is a test pattern, not a frame: a Sigfox uplink frame of
12 bytes lasts about 2 s, and a message sends it three times on three frequencies within its 192 kHz band,
with gaps between. For a power amplifier, one continuous record is the same signal.

```
Carrier[1]="DBPSK;Rb=100bps;Data=PN9;Off=0;P=0"
```

## Chirp spread spectrum (LoRa) {#css}

A **CSS** card is chirp spread spectrum, the LoRa modulation that LoRaWAN runs on; its EU868 channels are
125 kHz wide. Each symbol is one chirp: the frequency sweeps the bandwidth **BW** once, up from Fc − BW/2, in
2^SF / BW. A symbol's value, SF bits, is where on the sweep it starts; it wraps from the top to the bottom
when it gets there. The phase never jumps, and the envelope is constant: the PAPR is 0 dB. Choose **CSS** in a
card's standard list, or **SF7** or **SF12** from the **LoRaWAN** preset, which also moves **Centre Fc**
to 868.1 MHz.

| Field | Meaning | Default |
|---|---|---|
| **SF** | the spreading factor, 7 to 12: 2^SF chips per symbol. Each step up doubles the symbol | 7 |
| **BW (kHz)** | the chirp's sweep: 125 kHz on the 868 MHz channels, 250 or 500 kHz where allowed | 125 |

The line under the fields gives the symbol, the raw bit rate SF × BW / 2^SF (before coding) and the
packet. **The record is one packet**: 8 preamble up-chirps, two sync symbols, 2.25 down-chirps that mark
the start of the frame, and 32 payload symbols read SF bits at a time from PN9. That is 44.25 symbols:
45.3 ms at SF7 and 125 kHz, 1.45 s at SF12. There is no header, coding or CRC, as an RF front end does
not depend on what the bits mean.

The channel bandwidth is BW. The record is sampled at 8 BW (1 MS/s at 125 kHz), offered as **Native
rate**. The second preview tab reads **Frequency** and shows the instantaneous frequency over the
preamble, the sync symbols, the start of frame and the first payload symbols: a sawtooth that turns down
at the start of frame.

```
Carrier[1]="CSS;SF=7;BW=125kHz;Off=0;P=0"
```

`BW` takes Hz, kHz or MHz, and is 125 kHz when absent; `SF` may be 5 to 12 in the netlist.

## A single carrier (PSK, QAM, APSK) {#sc}

An **SC** card is one carrier of PSK, QAM or APSK symbols at a symbol rate Rs, shaped by a root-raised-cosine
filter: the waveform of most satellite and point-to-point links. Choose **SC** in a card's standard list.

| Field | Meaning | Default |
|---|---|---|
| **Rs (Msym/s)** | the symbol rate | 1 |
| **Modulation** | BPSK, QPSK, 8PSK, 16QAM, 64QAM, 256QAM, 16APSK or 32APSK | QPSK |
| **Roll-off** | the root-raised-cosine roll-off α, 0 to 1 | 0.35 |
| **Rings** | APSK only: each outer ring's radius over the inner ring's, separated by commas | 2.85 (16APSK); 2.84, 5.27 (32APSK) |
| **Phases (°)** | APSK only: each ring's phase offset, inner ring first | 45, 15 (16APSK); 45, 15, 0 (32APSK) |
| **Preamble** | known symbols at the start of the record | 0 |
| **Pilot block** | `L/P`: L known symbols after every P data symbols, such as `36/1440`; empty for none | none |
| **Data** | **PN9**, **PN15**, **PN23** or **Fixed bits** | PN9 |

**The constellations** have unit mean power. BPSK sends +1 for a 0 and −1 for a 1. QPSK, 8PSK and the square
QAMs are Gray-coded: QPSK puts the first bit on I and the second on Q, 8PSK puts k ^ (k >> 1) at the point at
kπ/4, and the QAMs use Gray code on each axis, the first half of the bits on I. 16APSK has 4 + 12 points on two
rings, and 32APSK 4 + 12 + 16 on three. APSK labels its points ring by ring, inner ring first, each ring
counter-clockwise from its phase offset. Its rings let an amplifier run closer to compression than square QAM's
corners allow. APSK's EVM is read against the constellation's mean power.

**The shaping covers the whole record.** The record is periodic, so the root raised cosine is applied to the
whole of it in the frequency domain. The spectrum ends exactly at Rs(1 + α)/2 either side, which is the channel
bandwidth, Rs(1 + α). The receiver's matched filter turns the shaping into a raised cosine, which leaves no
interference between symbols at the symbol instants. In the netlist, `Span=n` cuts the filter's impulse
response to n symbols instead, as a transmitter's FIR does. The receiver then uses the same truncated filter,
and some interference between symbols remains.

**The record holds the data's period in whole symbols.** The data is read one symbol's bits at a time, so the
record holds one whole period of it: lcm(period, k)/k symbols for k bits a symbol. For PN9 that is 511 symbols,
because 511 shares no factor with any k. The record holds at most 65,536 data symbols: PN23's period is 8.4
million bits, so its record is its first 65,536 symbols. `Symbols=` in the netlist sets the count. Known symbols
are QPSK points read from PN9: the preamble starts the record, and a pilot block follows every P data symbols
except the last group. The record is sampled at 8 Rs, offered as **Native rate**. The line under the fields
gives the symbol period, the data and known symbols, the record and the bandwidth.

An SC carrier is the source's only carrier, and **Record** and **Spectral shaping floor** do not apply to it.
**Clip to PAPR** applies as it does to OFDM, and the PAPR is under the plots. Its EVM tab shows the EVM and the
constellation, measured through the matched filter: 0 % unclipped, and the clipping's error otherwise. The card
has no **Seed**. The carrier is a modulation, with no standard's framing, coding or signalling.

```
Carrier[1]="SC;Rs=10Msps;Mod=16APSK;RollOff=0.2;Data=PN15;Preamble=64;PilotBlock=36/1440;Off=0;P=0"
```

`Rs` takes sps, ksps or Msps (MBd, kBd and Bd read the same). Every field but `Off` and `P` may be left out, and
then its default applies.

**Through a link.** A single carrier is the signal of most satellite links. Played through a
[Propagation Channel](propagation-channel.html#satellite) into an envelope run with `Equalizer=pilot`, the preamble and
pilot blocks train the receiver's equalizer, the pass's Doppler is measured and removed (`CFO_Hz`), and EVM, counted bit
errors and the estimated BER are read at the far end. The *LEO Satellite Downlink* example sends 16APSK this way through a
transponder.

## The family cards {#family-cards}

The standards below were added after the first cards, and their cards share one form. Each field is one key of
the carrier as the netlist writes it, typed with its unit (`80MHz`, `0.8us`, `1@1.8kHz@12@0dB`) or picked from
its list; an empty field leaves the key out, and its default, shown greyed in the field, applies. The carrier is
read back through the netlist's own parser, so the card, the netlist and the cache never disagree. A field that
does not parse is named under the card in the netlist's words, and the signal keeps the last carrier that did.
The line under the fields is the carrier's label, the same one the inspector shows.

Rarely used fields are behind **Advanced**. The **LTE**, **NR**, **SC**, **FSK** and **PCW** cards have an
**Advanced** expander of the same kind for their new options.

Every family below is the source's only carrier, with a record of its own: **Record**, **Spectral shaping** and
**+** are disabled. **Clip to PAPR** applies to WCDMA, tones and noise, and the OFDM numerologies, as it does to
SC. A Wi-Fi packet is gated, so a PAPR target against the record's mean would count its gap; GNSS, 802.11b,
802.15.4, analog and FMCW carriers are not clipped either. The quality reading is under the plots and on the
second tab.

## LTE and NR options, OFDM numerologies {#ofdm-options}

An **LTE** or **NR** card's **Advanced** fields take it past the full-load downlink. With none set, the carrier
is sample for sample what it was.

| Field | Key | Meaning | Default |
|---|---|---|---|
| **Link** | `Link` | `DL`, or `UL`: LTE's SC-FDMA, NR's PUSCH | DL |
| **DFT-s-OFDM** | `DFTs` | NR uplink transform precoding | 0 |
| **π/2-BPSK** | `Pi2BPSK` | NR DFT-s-OFDM only: π/2-BPSK in place of the modulation | 0 |
| **RB start**, **RB count** | `RbStart`, `RbCount` | a contiguous allocation. A DFT-s-OFDM count is 2^a·3^b·5^c | 0, all |
| **Test model** | `TM` | `NR-FR1-TM1.1` … `NR-FR1-TM3.3`, `E-TM1.1` … `E-TM3.3` (with 2a, 2b, 3.1a, 3.1b) | none |
| **TDD** | `TDD` | a slot pattern and the special slot's symbols, `DDDSU:10,2`, or `TM` for the test model's own | FDD |
| **Cell ID** | `CellId` | the physical cell identity | 1 |

**Standards.** NR uplink: 3GPP TS 38.211 V19.5.0, cl. 6.3.1.4 (transform precoding), cl. 5.1.1 (π/2-BPSK), the
DM-RS of cl. 6.4.1.1 and the low-PAPR sequences of cl. 5.2.2. LTE uplink: TS 36.211 V19.3.0, cl. 5.3.3 and 5.6
(SC-FDMA), cl. 5.5 (DM-RS). Test models: TS 38.141-1 V19.6.0 cl. 4.9.2.2 and 4.9.2.3; TS 36.141 V19.1.0 cl. 6.1.1
and 6.1.2. LTE-M narrowbands: TS 36.211 cl. 5.2.4.

An **OFDM** card is a numerology of its own, chosen in **Numerology** (`Preset=`):

| Numerology | Standard |
|---|---|
| DVB-T 2K, DVB-T 8K | ETSI EN 300 744 V1.6.2 cl. 4.4–4.6: 8 MHz, guard 1/4, scattered and continual pilots, TPS carriers |
| DVB-T2 8K PP2 | ETSI EN 302 755 V1.4.1 cl. 9.2: normal carrier mode, guard 1/8, PP2 |
| ISDB-T mode 3 | ARIB STD-B31: 6 MHz, guard 1/8, 13 segments (unverified) |
| DAB mode I | ETSI EN 300 401 V2.2.1 cl. 14: 1536 carriers, null and phase-reference symbols, one 96 ms frame |
| WiMAX 10 MHz PUSC | IEEE 802.16e OFDMA DL PUSC, 1024 points at 11.2 MS/s (from the published profile: unverified) |
| NB-IoT UL | TS 36.211 cl. 10.1: 1, 3, 6 or 12 tones at 15 kHz, or one at 3.75 kHz |

**Symbols** sets the OFDM symbols in the record (68). The **Advanced** fields are the NB-IoT uplink's: **NB-IoT
SCS** (`SCS`, 15 kHz), **NB-IoT tones** (`NbTones`, 12), **First tone** (0), **π/2-BPSK** (single tone, in place
of QPSK), **Slots** (2) and **Cell ID** (0), and **Seed**.

```
Carrier[1]="NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Link=UL;DFTs=1;Pi2BPSK=1;RbStart=10;RbCount=24;Off=0;P=0;Seed=1"
Carrier[1]="LTE;BW=20MHz;Mod=64QAM;TM=E-TM3.1;TDD=TM;CellId=1;Off=0;P=0;Seed=1"
Carrier[1]="OFDM;Preset=NB-IoT UL;SCS=3.75kHz;NbTones=1;Slots=2;Off=0;P=0;Seed=1"
```

**Quality.** An LTE or NR carrier with options, and a numerology, are demodulated against the grid they were made
from: pilot-aided where the grid has pilots, one gain on the data where it has none. The EVM tab shows the EVM
and the constellation; LTE and NR keep their modulation's limit, a numerology has none. A clean record reads
0.17 % or less.

**Simplified.** The data are seeded random constellation points, with no channel coding, scrambling or framing;
no PT-RS, SRS, PRACH or hopping. The E-TM control region spreads its QPSK REGs evenly at the table's levels
rather than placing them by the PCFICH, PHICH and CCE mapping. The DVB-T TPS carriers repeat their reference
symbol, and DVB-T2 leaves out the P1, P2 and frame-closing symbols and the continual pilots. ISDB-T uses one PRBS
from the lowest carrier in place of its per-segment initial values, with TMCC and AC carriers carrying data.

## Wi-Fi (IEEE 802.11) {#wlan}

A **WLAN** card is one IEEE 802.11 OFDM packet, then an idle gap, as a periodic record: the legacy (L-STF, L-LTF,
L-SIG) fields, the format's own preamble and the data symbols, one spatial stream. The **Wi-Fi** preset menu sets
one at 4× oversampling and moves **Centre Fc** to a channel.

| Field | Key | Meaning | Default |
|---|---|---|---|
| **Standard** | `Std` | `a` (11a/g), `n` (HT), `ac` (VHT), `ax` (HE) or `be` (EHT) | ac |
| **Bandwidth** | `BW` | 20 MHz (a), 20 or 40 (n), up to 160 (ac, ax), up to 320 MHz (be) | 20 MHz |
| **MCS** | `MCS` | 0 to 7 (a, n), 9 (ac), 11 (ax), 13 (be) | 7 |
| **Guard interval** | `GI` | 0.8 µs (a); 0.4 or 0.8 µs (n, ac); 0.8, 1.6 or 3.2 µs (ax, be) | 0.8 µs |
| **Data symbols** | `Symbols` | data symbols in the packet (Advanced) | 20 |
| **Idle** | `Idle` | the gap after the packet (Advanced) | 16 µs |
| **Oversample** | `Oversample` | the rate over the bandwidth (Advanced). A card chosen in the Designer starts at 4 | 1 |
| **Window** | `Window` | the raised-cosine overlap between symbols, inside each guard interval (Advanced) | 100 ns |
| **Seed** | `Seed` | the random data (Advanced) | 1 |

**Power is the packet's.** The packet is unit power over its own length and the gap is zeros, so `Pavg` is the
packet power, as a pulse's is its top: the Source box's field reads **Packet power (dBm)**, and the label gives the
duty that the record's mean is below it by.

**Standards.** IEEE Std 802.11-2020: Clause 17 (OFDM, 11a/g), Clause 19 (HT), Clause 21 (VHT); IEEE Std
802.11ax-2021 Clause 27 (HE SU); IEEE Std 802.11be-2024 Clause 36 (EHT). The 2020 subclause numbers are inferred
from the 2012 edition's, one clause number lower, and are unverified against the 2020 text.

**Quality.** The packet from the record's first sample, as a receiver reads it: the frequency offset from the
L-LTF's two copies taken out, the channel from the LTF, each data symbol equalised and its common phase from the
pilots taken out, each data tone decided to the nearest point. The rms error over the constellation's mean power
is read against the MCS's allowed relative constellation error (17.3.9.7.4, 19.3.18.7.3, 21.3.17.4.3 and
802.11ax-2021 27.3.19.4; HT 64QAM 5/6 −28 dB, VHT −27 dB; EHT 4096QAM −38 dB, unverified), with **PASS** or
**FAIL**. The spectrum carries the transmit **mask**, dashed and named by its clause: 0 dBr to just inside the
channel edge, −20 dBr just outside it, −28 dBr one bandwidth from the centre and −40 dBr at 1.5 bandwidths.

```
Carrier[1]="WLAN;Std=ax;BW=80MHz;MCS=11;GI=0.8us;Symbols=4;Idle=16us;Oversample=4;Window=100ns;Off=0;P=0;Seed=1"
```

**Limits.** At `Oversample=1` the record ends at the channel edge: the mask beyond it is not in the record, and an
envelope run reads about 0.36 % EVM from its resampling alone. Use 4. A Wi-Fi carrier refuses **Clip to PAPR**.

**Simplified.** There is no coding, scrambling or interleaving: every data tone carries a random point of the
MCS's constellation. The SIG fields carry random BPSK and QBPSK, not an encoded rate and length. Every STF is the
L-STF pattern per 20 MHz. The HE and EHT LTF is ±1 from the pilot polarity sequence on the tone plan, not the
tabulated HE-LTF, with one 4× LTF at the data's guard interval. Pilots other than legacy ones are the polarity
sequence shifted by one per pilot. There is no packet extension, LTF compression or puncturing, and the tone
rotation turns every field, where EHT turns the pre-EHT fields only.

## WCDMA, 802.11b and 802.15.4 {#dsss}

Three direct-sequence spread-spectrum standards, each with its own card. Chips are shaped by a root raised cosine
over the whole periodic record, and the receiver applies the same filter, so a clean record reads every chip back.

| Card | Field | Key | Meaning | Default |
|---|---|---|---|---|
| **WCDMA** | **Test model** | `Model` | `Tm1x64`, `Tm1x32`, `Tm1x16`, `Tm2`, `Tm3x32`, `Tm3x16`, `Tm4`, `Tm5x8`, `Tm5x4`, `Tm5x2`, `Tm6`, or `Uplink12k2` | Tm1x64 |
| | **Scrambling code** | `Code` | the primary scrambling code, 0 to 7 (group 0) | 0 |
| **WLAN11B** | **Rate** | `Rb` | 1 (DBPSK), 2 (DQPSK), 5.5 or 11 Mb/s (CCK) | 11 Mb/s |
| | **PSDU (bytes)** | `PSDU` | the frame's payload, 1 to 4095 | 1000 |
| **OQPSK154** | **PSDU (bytes)** | `PSDU` | the frame's payload, 1 to 127 | 127 |

**WCDMA** is one 10 ms frame at 3.84 Mcps, RRC α = 0.22, at 30.72 MS/s: the downlink test models of 3GPP TS 25.141
V19.0.0 §6.1.1 (channels per §6.1.1.5–6.1.1.9, scrambling per TS 25.213 V19.0.0 §5.2.2, P-SCH and S-SCH per
§5.2.3), or the 12.2 kb/s uplink reference channel (TS 25.213 §4.2.1.1, §4.3.2.2). The preset menu puts the
downlink at 2140 MHz and the uplink at 1950 MHz. **802.11b** (IEEE Std 802.11-2020 Clauses 15 and 16, checked
against IEEE Std 802.11b-1999 clause 18) is the long preamble and PLCP header at 1 Mb/s, then the PSDU, scrambled,
at 11 Mchip/s and 88 MS/s; frames follow back to back. **802.15.4** (IEEE Std 802.15.4-2020, the 2450 MHz O-QPSK
PHY; the chip sequences checked against the 2003 edition) is the preamble, SFD, PHR and PSDU as 32-chip sequences
at 2 Mchip/s, half-sine O-QPSK at 16 MS/s: constant envelope.

```
Carrier[1]="WCDMA;Model=Tm1x64;Code=0;Off=0;P=0"
Carrier[1]="WLAN11B;Rb=11Mbps;PSDU=1000;Off=0;P=0"
Carrier[1]="OQPSK154;PSDU=127;Off=0;P=0"
```

**Quality**, at the chip instants after the matched filter, one complex gain fitted:

- **WCDMA** — the composite EVM against its limit (17.5 % for QPSK, 12.5 % for TM5's 16QAM, 8 % for TM6's
  64QAM, TS 25.141 §6.7.1), the peak code-domain error, and on the second tab each channel's code-domain power
  (descrambled and despread, relative to the total). A test model's channels sum to peaks, so Clip applies.
- **802.11b** — the chip EVM and the worst margin against the transmit mask, positive inside it.
- **802.15.4** — the chip EVM against the PHY's 35 %.

**Simplified.** The WCDMA Home BS variants are not made, and the 16QAM and 64QAM maps are per-axis Gray (the same
waveform as TS 25.213's, labelled in another order). 802.11b sets a mask, not a filter: the chips are shaped by
RRC α = 0.5, which ends the band at ±8.25 MHz. That later 802.15.4 editions keep the 2003 chip sequences is
unverified.

## SC pulses, rotation and bursts {#sc-options}

The **SC** card's **Advanced** fields turn the plain root-raised-cosine carrier into a standard's own single
carrier. The **GSM / EDGE**, **TETRA**, **DVB-S2 / S2X**, **802.11ad**, **Telemetry** and Bluetooth EDR payload
presets set them.

| Field | Key | Meaning | Default |
|---|---|---|---|
| **Pulse** | `Pulse` | `RRC`, `RC`, `Gaussian`, `Rect`, `HalfSine` or `LinearGMSK` (EDGE's linearised GMSK) | RRC |
| **BT** | `BT` | a Gaussian pulse's bandwidth-time product | 0.3 |
| **Rotation (°)** | `Rotation` | each symbol turns by this from the one before: 90 for π/2-BPSK, 67.5 for EDGE 8PSK, 45 for 16QAM | 0 |
| **Differential** | `Differential` | each point is the previous one times the label's (π/4-DQPSK, 8DPSK) | 0 |
| **Offset Q** | `OffsetQ` | Q half a symbol after I (O-QPSK) | 0 |
| **Frame data** | `FrameData` | data symbols per frame, each with its own preamble and pilots | 0 (one frame) |
| **Guard (symbols)** | `Guard` | silence after each frame: a burst | 0 |
| **Ramp (symbols)** | `Ramp` | each raised-cosine ramp into and out of a frame, inside the guard | 0 |

`Mod=SOQPSK-TG` is the telemetry modulation (IRIG 106, Chapter 2). With a rotation or a differential chain the
record is lengthened, up to 16 times, until it comes back to its start.

```
Carrier[1]="SC;Rs=270.833ksps;Mod=8PSK;Preamble=26;Pulse=LinearGMSK;Rotation=67.5;FrameData=122;Guard=8.25;Ramp=4;Off=0;P=0"
```

**Standards** of the presets: EDGE 8PSK and EDGE Evolution 16QAM, 3GPP TS 45.004 and TS 45.002 (normal burst);
TETRA, ETSI EN 300 392-2 clause 5 (18 ksym/s π/4-DQPSK, RRC 0.35); IEEE 802.15.4-2020 clause 12 (O-QPSK chips);
Bluetooth EDR payloads, Bluetooth Core Specification Vol 2 Part A; the 802.11ad single carrier, IEEE Std 802.11-2020
clause 20; DVB-S2, ETSI EN 302 307-1 clauses 5.4–5.6; DVB-S2X, ETSI EN 302 307-2.

**Quality.** The EVM and the constellation, measured at each data symbol's instant through the matched filter, as
for any SC carrier. Bursts are read on their data symbols only.

**Limits and simplifications.** Any pulse but RRC rings into the next symbols in a window that is not whole
records: read an envelope run's EVM over whole records. The data is PN and the standards' own known sequences
(training, headers, Golay guards) are the SC carrier's QPSK known symbols; EDGE's 26 training symbols are put first
in the burst rather than in its middle. A standard that sets a mask rather than a pulse (802.11ad) gets the roll-off
stated in its preset.

## FSK levels, pulses and packets {#fsk-options}

The **FSK** card's **Advanced** fields:

| Field | Key | Meaning | Default |
|---|---|---|---|
| **Preset** | `Preset` | a standard's burst or packet, below; the card's fields override it | none |
| **Levels** | `Levels` | 2, 4 or 8 frequencies; **Deviation** is the outer level's | 2 |
| **Mapping** | `Mapping` | the odd level of each symbol value, comma-separated | 4FSK: 1,3,-1,-3 |
| **Pulse** | `Pulse` | `NRZ`, `RC`, `RRC`, `C4FM`, `LREC` or `LRC` | NRZ |
| **Roll-off** | `RollOff` | of the RC, RRC and C4FM pulses | 0.2 |
| **Pulse length** | `PulseLength` | L, symbols, of an LREC or LRC pulse | 1 |
| **Manchester** | `Manchester` | each bit as two chips; the bit rate is then the chip rate | 0 |

The presets, each from its menu, with its burst where the standard sends one:

| Preset | Standard |
|---|---|
| Bluetooth BR, EDR 2 Mb/s, EDR 3 Mb/s | Bluetooth Core v5.4 Vol 2 Part A cl. 3.1, 3.2 and Part B cl. 6.4, 6.7: one 625 µs slot, the GFSK access code and header, and for EDR a guard, then the π/4-DQPSK or 8DPSK payload at 1 Msym/s |
| DECT | ETSI EN 300 175-2 cl. 4.2, 4.6, 5.1, 5.4: 1.152 Mb/s GFSK, a P32 packet in its slot |
| Z-Wave R1, R2, R3 | ITU-T G.9959 cl. 7.1.2, 7.1.3: preamble and start of frame |
| Wi-SUN modes 1a to 5, 4FSK 400 kb/s | IEEE 802.15.4-2020 cl. 19.3 (SUN FSK) |
| P25 Phase 1 C4FM, DMR, dPMR | TIA-102.BAAA-A; ETSI TS 102 361-1; ETSI TS 102 490 Table 12.1 |
| POCSAG 512, 1200, 2400 b/s | ITU-R M.584-2 Annex 1: preamble, sync codeword, one batch |
| GSM normal burst | 3GPP TS 45.004 cl. 2, TS 45.002 cl. 5.2.3: GMSK BT 0.3, 26 training bits |

```
Carrier[1]="FSK;Rb=9.6kbps;Dev=1.944kHz;Data=PN9;Levels=4;Mapping=1,3,-1,-3;Pulse=RRC;RollOff=0.2;Off=0;P=0"
Carrier[1]="FSK;Preset=Bluetooth EDR 2 Mb/s;Off=0;P=0"
```

**Quality.** The frequency at each symbol's centre, fitted to the frequency sent there: the **deviation**, the
carrier **offset** and the **FSK error**, the rms of what is left over the nominal deviation. Ramps, guards and a
switched payload are not read by it; an EDR packet's DPSK payload is read as an SC carrier is, and its **EDR
payload EVM** follows.

**Simplified.** The data is PN9. A sync word a standard derives from an address (Bluetooth's) is a stand-in of the
right length. The Wi-SUN 4FSK mapping and BT, the Z-Wave bit order and regional centres, and the 920 MHz 4FSK mode
are from working-group copies and regional texts: unverified.

## GNSS {#gnss}

A **GNSS** card is the ranging signal of several satellites at once, each far below the thermal noise, as a
receiver front end sees it. The **GNSS** preset menu sets two satellites at C/N0 45 dB-Hz and moves **Centre Fc**
to the signal's carrier: 1575.42 MHz (GPS L1 C/A, Galileo E1), 1176.45 MHz (GPS L5) or 1561.098 MHz (BeiDou B1I).

| Field | Key | Meaning | Default |
|---|---|---|---|
| **Signal** | `Signal` | `L1CA`, `L5`, `E1` or `B1I` | L1CA |
| **Satellites** | `Sats` | `PRN@Doppler@code phase@power`, comma-separated: `1@1.8kHz@12@0dB,5@-2kHz@300@-3dB` | PRN 1 |
| **C/N0 (dB-Hz)** | `CN0` | the reference (0 dB) satellite's carrier to noise density; empty adds no noise | no noise |
| **Record** | `Record` | whole milliseconds of the codes (Advanced) | 20 ms |
| **Seed** | `Seed` | the data bits, carrier phases and noise (Advanced) | 1 |

**Standards.** GPS L1 C/A: IS-GPS-200N, Table 3-Ia (1023-chip Gold codes, 1.023 Mcps). GPS L5: IS-GPS-705J,
Table 3-Ia (I5 and Q5 at 10.23 Mcps with their Neuman-Hofman codes). Galileo E1: Galileo OS SIS ICD Issue 2.1
(CBOC(6,1,1/11) of E1-B and E1-C). BeiDou B1I: BDS-SIS-ICD-B1I-3.0, Table 4-1 (2046-chip codes, D1 and D2 data).
C/N0 is the reference satellite's carrier power over the noise density: white Gaussian noise over the native band
is added at the N0 that gives it, and the record, signal and noise together, is unit mean power.

**Quality.** A receiver's acquisition in place of an EVM: the Doppler wiped off, the record correlated against
each PRN's replica one code period at a time and the periods summed in power. Per PRN: the **code phase** (the
peak's lag, chips), the **peak over the floor** and the **C/N0** read from it. The spectrum carries the signal's
**expected PSD** (sinc² for BPSK, the CBOC sum for E1), dashed and named by its ICD. With noise on, the measured
PSD is the noise floor's, and the expected shape is what the noise hides. The second tab reads **Quality**.

```
Carrier[1]="GNSS;Signal=L1CA;Sats=1@1.8kHz@12@0dB,5@-2kHz@300@-3dB;CN0=45;Record=20ms;Off=0;P=0;Seed=1"
```

{{ui: signal-designer-gnss}}

**Simplified.** The data bits are random, with no frame structure. The Galileo E1 primary codes are stand-ins:
each is a fixed pseudo-random code of the ICD's length and rate drawn from the PRN, so the spectrum, the
correlation and the C/N0 are the real signal's and the chips are not. E1-A, L1 P(Y) and B1Q are not made. A
Doppler is rounded to a whole number of cycles per record (a 50 Hz grid at 20 ms), and the code is not stretched
by it. The record kinds need whole-record windows: an envelope run's correlation reads whole records.

## Analog and avionics {#analog}

An **ANALOG** card is an analog carrier on a message of tones (or noise): AM, DSB-SC, SSB, FM and PM, and the
VOR and ILS navigation composites. The **FM / AM / SSB** and **VOR / ILS** preset menus set one and move **Centre
Fc**: AM broadcast at 1 MHz, FM broadcast at 98 MHz (±75 kHz), NBFM at 446 and 156.8 MHz (±2.5 and ±5 kHz), SSB
two-tone at 14.2 MHz (700 and 1900 Hz), VOR at 113 MHz (bearing 45°), ILS localizer at 110.1 MHz and glide path at
334.4 MHz.

| Field | Key | Meaning | Default |
|---|---|---|---|
| **Kind** | `Kind` | `AM`, `DSBSC`, `USB`, `LSB`, `FM`, `PM`, `VOR`, `LOC` or `GS` | AM |
| **Tones** | `Tones` | the message, `frequency@level@phase`, comma-separated | 1 kHz |
| **AM depth** | `Depth` | the depth m, 0 to 1 | 0.3 |
| **FM deviation** | `Dev` | the peak deviation | 5 kHz |
| **Bearing (°)** | `Bearing` | VOR: the radial | 0 |
| **DDM** | `DDM` | ILS: the difference in depth of modulation, 90 Hz over 150 Hz | 0 |
| **Noise BW** | `NoiseBW` | Gaussian noise to this bandwidth as the message (Advanced) | none |
| **FM index** | `Index` | the deviation as index × the top message frequency (Advanced) | from Dev |
| **PM peak (rad)** | `PeakPhase` | the PM peak phase (Advanced) | 1 |
| **SDM** | `SDM` | ILS: the sum of the depths (Advanced) | 0.4 LOC, 0.8 GS |
| **Ident** | `Ident` | VOR, LOC: the Morse ident at 1020 Hz (Advanced) | none |
| **Record** | `Record` | whole 100 ms, lengthened to hold the ident (Advanced) | 100 ms |

**Standards.** VOR, ILS localizer and glide path, and their idents: ICAO Annex 10 Vol. I, 8th edition (2023),
3.3.5 and 3.3.6 (VOR: 30 Hz AM at 30 % and a 9960 Hz subcarrier FM ±480 Hz), 3.1.3.5 and 3.1.3.9 (LOC: 20 % by
each of 90 and 150 Hz), 3.1.5.5 (GS: 40 %). The clause numbers were checked against the 7th edition (2018), not
the 8th edition's own text: unverified for the 8th.

**Quality.** Demodulated as a receiver reads it: the message's strongest line, and the AM depth, FM deviation,
PM peak, VOR bearing, ILS DDM and SDM, each where the kind has it. The second tab reads **Quality**.

```
Carrier[1]="ANALOG;Kind=FM;Tones=1kHz@0@0;Dev=75kHz;Off=0;P=0;Seed=1"
```

**Simplified.** The VOR here is the Doppler-VOR form, the variable signal leading the reference; a receiver reads
the same phase difference for a conventional VOR, but lead or lag is taken from receiver practice (unverified in
the Annex text). That the LOC tones zero-cross together, as the GS clause states, is unverified for the LOC. An
analog carrier is not clipped: a clipped AM is a distorted message, not a PAPR target. DME pulse pairs are
[radar](#radar) presets.

## Tones and noise {#tones-noise}

A **TONES** card is a multitone, band-limited Gaussian noise, or both: N-tone with chosen phases, a notched
multitone or notched noise for the noise power ratio (NPR), white noise, and CW plus AWGN. It is built on the
record's 1 / record grid, so every tone sits on a bin and a notch is empty to the floor of one transform. The
**Multitone / NPR / AWGN** preset menu holds two-tone, three-tone, 10-tone, 100-tone NPR (100 tones 100 kHz
apart, random phases, a 1 MHz notch), noise NPR over 36 MHz with a 1 % notch, AWGN 20 MHz and CW + AWGN (C/N
20 dB). The CW-card [N tones](#tones) preset is still there, for tones as separate envelope carriers.

| Field | Key | Meaning | Default |
|---|---|---|---|
| **Tones** | `N` | equal tones centred on Fc, up to 100,000; 0 is noise only | 2 |
| **Spacing** | `Spacing` | tone to tone | 1 MHz |
| **Phases** | `Phase` | `Zero`, `Newman` (π k² / N), `Schroeder` or `Random` (from Seed) | Newman |
| **Notch** | `Notch` | `centre/width`, an empty band: `0/1MHz` | none |
| **Noise BW** | `NoiseBW` | Gaussian noise over this band, centred on Fc | none |
| **Noise (dB)** | `NoiseDb` | with tones: the noise's total over the tones' total (−20 is C/N 20 dB) | 0 |
| **Tone powers (dB)** | `Powers` | each tone's power, comma-separated (Advanced) | equal |
| **Notch tones** | `NotchTones` | the notch as tone indices, 0-based (Advanced) | none |
| **Record** | `Record` | the record (Advanced) | 2 / spacing; 1 ms for noise |

Schroeder's phases follow M. R. Schroeder, "Synthesis of low-peak-factor signals and binary sequences with low
autocorrelation", IEEE Trans. Inf. Theory 16(1):85–89, 1970, <https://doi.org/10.1109/TIT.1970.1054411>.

**Quality.** With a notch, the **NPR**: the band's density outside the notch over the density in the notch's
middle half, over whole records. The CCDF carries the Gaussian reference, which noise lies on. Clip applies:
noise has peaks. The second tab reads **Quality**.

```
Carrier[1]="TONES;N=100;Spacing=100kHz;Phase=Random;Notch=0/1MHz;Off=0;P=0;Seed=1"
```

## Radar waveforms and FMCW {#radar}

The **PCW** card's **Advanced** fields code, sweep and pattern the pulse; the **FMCW** card is a continuous ramp.

| Field | Key | Meaning | Default |
|---|---|---|---|
| **Phase code** | `Code` | `kind:length[:root]`: `Barker:13`, `Frank:4` (16 chips), `P1:4`, `P2:4`, `P3:64`, `P4:64`, `ZadoffChu:63:5`, `Mls:7` (127 chips). The chip is PW over the chips | none |
| **Chip smoothing** | `ChipSmoothing` | the fraction of a chip over which the phase moves to the next chip's | 0 |
| **Sweep law** | `Fm` | how **Chirp** is swept: `Linear`, `Tangent` or `Sine` (NLFM) | Linear |
| **Law shape** | `FmParam` | tangent β (rad, 0 to π/2) or sine k (0 to 1) | 1.2 / 0.5 |
| **Gaussian** | `Gaussian` | a Gaussian envelope of half-amplitude width PW in place of the flat top | 0 |
| **PRIs** | `PRIs` | a staggered train: PRIs cycled pulse by pulse, `100us,125us,150us` | PRI |
| **Jitter** | `Jitter` | ± fraction of each PRI, drawn from Seed | 0 |
| **Pulses**, **Dwell** | `Pulses`, `Dwell` | pulses before an off-time of Dwell | auto, 0 |
| **Step**, **Steps** | `Step`, `Steps` | stepped frequency: Δf between pulses and how many | 0, 1 |
| **Pair spacing** | `Pair` | a pulse pair, the second this long after the first, peak to peak (DME) | 0 |

| FMCW field | Key | Meaning | Default |
|---|---|---|---|
| **Sweep** | `BW` | the ramp's sweep, centred on Fc | 20 MHz |
| **Ramp** | `Ramp` | the up-ramp (each half of a triangle) | 50 µs |
| **Idle** | `Idle` | a sawtooth's return to the start (0 is a jump) | 0 |
| **Triangle** | `Triangle` | up and down ramps of equal length | 0 |

The preset menus: **Codes** (Barker 13 and 7, Frank, P1 to P4, Zadoff-Chu, an m-sequence), **FM** (tangent and
sine NLFM over 10 MHz, stepped frequency 8 × 1 MHz), **PRI** (stagger, jitter, burst and dwell), **DFS ETSI**
(ETSI EN 301 893 V2.1.1 Annex D, Tables D.3 and D.4: the reference and signals 1 to 6), **DFS FCC** (FCC 47 CFR
15.407(h) and KDB 905462 D02, Tables 5 to 7: types 0 to 6), **FMCW** (sawtooth and triangle over 250 MHz), and,
on the Navigation row, **DME** (ICAO Annex 10 Vol. I 3.5: X and Y pulse pairs of Gaussian 3.5 µs pulses). A DFS
preset is one member of each range the table gives, as a test lab draws its own; every PRI is rounded to whole
samples at 30.72 MHz.

```
Carrier[1]="PCW;PRI=100us;PW=13us;Rise=0.1us;Fall=0.1us;Code=Barker:13;Off=0;P=0"
Carrier[1]="FMCW;BW=250MHz;Ramp=50us;Idle=12.5us;Off=0;P=0"
```

**Quality.** A coded or chirped pulse is compressed: the record's first pulse through the matched filter of the
one sent, read as the **mainlobe** width at −3 dB, the null-to-null width, the peak sidelobe level (**PSL**) and the
integrated sidelobe level (**ISL**). Barker 13 reads −22.3 dB. An FMCW carrier's second tab reads **Frequency**:
the instantaneous frequency over one period.

**Sources** of the codes: R. H. Barker, "Group synchronizing of binary digital systems", Communication Theory,
Butterworth, 1953 (no DOI); R. L. Frank, IEEE Trans. Inf. Theory 9(1):43–45, 1963,
<https://doi.org/10.1109/TIT.1963.1057798>; B. L. Lewis and F. F. Kretschmer, IEEE Trans. AES 17(3):364–372, 1981,
<https://doi.org/10.1109/TAES.1981.309063>, and 18(5):637–641, 1982, <https://doi.org/10.1109/TAES.1982.309276>;
D. C. Chu, IEEE Trans. Inf. Theory 18(4):531–532, 1972, <https://doi.org/10.1109/TIT.1972.1054840>; S. W. Golomb,
Shift Register Sequences, 3rd ed., World Scientific, 2014, <https://doi.org/10.1142/9361>. The tangent NLFM:
T. Collins and P. Atkins, "Nonlinear frequency modulation chirps for active sonar", IEE Proc. Radar, Sonar and
Navigation 146(6):312–316, 1999, <https://doi.org/10.1049/ip-rsn:19990754>. The PSL and ISL readout: N. Levanon and
E. Mozeson, Radar Signals, Wiley, 2004, <https://doi.org/10.1002/0471663085>.

**Limits and unverified items.** The compression of a stepped train reads its first pulse and ignores the step
offset of the others. The FCC rows were checked against public reproductions of KDB 905462 D02 only, and the DME
pulse-code table and its 2700 pulse pairs per second against public reproductions of the Annex's 8th edition: both
are unverified. ETSI signal 6 is an 81 ms burst, about 5 million samples at 61.44 MS/s. A train's power duty is the
record average.

## The spectrum and the CCDF {#preview}

The **Spectrum** is the composite power spectral density against the offset from `Fc`, framed on the
occupied channels; for a pulsed-CW source it is the train's. A [Wi-Fi](#wlan) packet's spectrum carries its
transmit mask and a [GNSS](#gnss) signal's its expected PSD, both dashed and named in the legend. The **CCDF** is
the probability that the instantaneous power exceeds the average by more than a given margin.

- **The Gaussian reference.** For a noise-like signal (LTE, NR, several carriers, an I/Q file, a WCDMA test model,
  an OFDM numerology, tones and noise) the CCDF is
  drawn against the CCDF of Gaussian noise, dashed and named in the plot's legend. Unclipped OFDM lies almost
  on it, and a clipped signal's curve bends down well inside it. A pulse, FSK, Bluetooth LE, LoRa or Sigfox
  signal is no kind of noise and never approaches the reference, so it is drawn alone.
- **A constant envelope** (FSK, Bluetooth LE, LoRa, a gated pulse) is a step at 0 dB: its PAPR is 0 dB, and
  an amplifier can run it in saturation. A Sigfox signal's step is at 1.25 dB, from the dip at each phase
  reversal. A step this narrow is framed from 0 to 12 dB, so it reads as a step.
- **A pulse is gated by default.** With **CCDF over the pulse on-time only (gated)** ticked, beneath the
  plot, the CCDF and the PAPR are measured from the rising edge's 50 % point to the falling edge's, against
  the pulse's own average, as a signal analyser gates a burst. The plot reads **CCDF (pulse on-time)** and
  a flat-topped pulse is a step at 0 dB. Untick it to measure over the whole PRI, where the off-time is in
  the average: the step then sits at the peak-to-average, which is 1 / the power duty (10 dB at 10 %). The
  line beneath the plots gives both. The setting belongs to the Designer and is not written to the source.

Below the plots: the carrier count and instantaneous bandwidth, the record and the rate, the PAPR at
0.01 % and the peak, each carrier's ideal ACPR, and each carrier's quality reading: Wi-Fi's EVM against its MCS
limit, GNSS's code phase and C/N0 per PRN, WCDMA's EVM and peak code-domain error, 802.11b's EVM and mask
margin, 802.15.4's EVM, an SC or OFDM-numerology carrier's EVM, the NPR, a coded or chirped pulse's PSL, ISL and
mainlobe, an analog carrier's depth, deviation, bearing or DDM, and an FSK carrier's deviation and FSK error with
an EDR payload's EVM. Each is read on the record the source plays, as the family's receiver reads it.

## The EVM tab {#evm}

For each LTE or NR carrier, the EVM tab demodulates the record the source plays (after clipping and
shaping) and compares every resource element with its ideal QAM point. One complex gain is fitted over the
record, with no per-subcarrier equaliser. The tab shows:

- the rms EVM in % and dB, and the peak EVM;
- which signal was measured: carrier, standard, bandwidth, modulation, offset, seed, record and clipping.
  The tab has no picker of its own. Click a carrier card on the left to show that carrier, and press
  **Calculate** to generate the signal on the left and measure it again;
- the chosen carrier's constellation over its ideal points;
- the envelope power around the record's peak, with the average and the peak as flat lines. A black arrow
  between them is labelled with the peak-to-average ratio. The **Window** field sets how long a stretch is
  shown (2 µs by default). It only redraws the plot, and Apply never writes it to the source.

For an [SC](#sc) carrier it passes the record through the matched filter and compares each data symbol with
its ideal point. An SC carrier has no standard's EVM limit, so the tab shows no limit and no pass or fail.

For the [family cards](#family-cards) the tab shows the family's own reading under the measured signal. Wi-Fi,
an OFDM numerology, an LTE or NR carrier with options, WCDMA, 802.11b and 802.15.4 have an EVM, against their
limit where the standard states one; Wi-Fi and the OFDM grids draw their constellation. WCDMA lists each
channel's code-domain power. A GNSS, analog or tones-and-noise carrier has no EVM: the tab reads **Quality** and
holds the reading (per PRN, the demodulated message, the NPR) with the envelope plot.

An unclipped signal reads close to 0 %. Clipping is what moves it. CW and I/Q-file carriers carry no
reference symbols, so they have no EVM. A pulsed-CW signal has none either: for one, this tab reads
**Pulse** and holds the pulse itself, |envelope| against time over one PRI, which zooms onto an edge like
any plot. For an [FSK](#fsk) or [CSS](#css) signal it reads **Frequency** and holds the instantaneous
frequency, and for a [DBPSK](#dbpsk) signal **Waveform**, the in-phase waveform.

{{ui: signal-designer-evm}}

## Export signal {#export}

**Export signal** (the button at the top of the left pane) saves the signal as a two-column `I Q` text
file at a location you choose. It holds one record of the composite of all carriers, around `Fc`, at the
Source box's sample rate and unit mean power — a custom I/Q file is resampled to that rate, after
clipping. The header states the sample rate (in MS/s, Hz and sample period),
the sample count and record, Fc, Pavg, PAPR, clipping, shaping and each carrier, followed by the
`# sample_rate_hz`, `# channel_bw_hz`, `# samples` and `# columns I Q` lines. This is the format
**Load custom** reads, so an exported signal loads back unchanged.
