---
title: The Signal Designer
slug: reference/signal-designer.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Signal Designer
lede: The signal a MOD_Source plays - LTE, NR, CW, a pulsed-CW train, FSK, Bluetooth LE, LoRa, Sigfox, a single PSK, QAM or APSK carrier or your own I/Q file - with its spectrum, CCDF and EVM before you simulate.
keywords: Signal Designer, MOD_Source, LFM, linear FM, chirped pulse, FR2, 120 kHz, mmWave, two-tone, N-tone, multitone, intermodulation, IMD, intercept, IP3, Newman phases, pulsed CW, pCW, pulse, radar, PRI, PRF, duty, pulse-top power, FSK, CPFSK, GFSK, GMSK, MSK, PN9, PN15, wireless M-Bus, metering, LoRa, LoRaWAN, Sigfox, Bluetooth LE, BLE, LE 1M, LE 2M, LE Coded, LPWAN, DBPSK, BPSK, UNB, ultra-narrowband, CSS, chirp, chirp spread spectrum, spreading factor, SF, IoT, single carrier, SC, root raised cosine, RRC, roll-off, matched filter, 8PSK, APSK, 16APSK, 32APSK, satellite, PN23, preamble, pilot block, gated CCDF, Gaussian reference, modulated source, LTE, NR, OFDM, I/Q, IQ file, custom waveform, Load Custom, sample rate, PAPR, CCDF, clipping, ACLR, ACPR, EVM, constellation, envelope, export
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

One click replaces the carriers with a common case. The presets are in one row per family:

- **Cellular** — **LTE 20 MHz**, **NR 20 MHz (30 kHz)**, **NR 100 MHz (30 kHz)**, **NR 100 MHz (120 kHz, FR2)**
  (64QAM, [FR2](#nr-fr2)), **4 x LTE 20 contiguous**,
  or **2C NR 20 at spacing**: two NR 20 MHz carriers the number of MHz apart in the field beside it, centre
  to centre.
- **Radar** — **Pulsed CW**, one [pulse train](#pulse).
- **IoT** — each sets one carrier and moves **Centre Fc** to the mode's channel; if Fc is an expression, it
  is left as you typed it.
  - **Wireless M-Bus** — a menu of the [FSK](#fsk) metering modes.
  - **Bluetooth LE** — a menu: **LE 1M / LE Coded** and **LE 2M**, as [GFSK](#fsk) at 2.44 GHz.
  - **LoRaWAN** — a menu: a [LoRa](#css) packet at **SF7** or **SF12**, 125 kHz at 868.1 MHz (EU868).
  - **Sigfox** — the RC1 uplink: [DBPSK](#dbpsk) at 100 b/s, 868.13 MHz.
- **Tones** — **N tones**: the number of equal CW tones in the field beside it (2 by default), the spacing in
  the next field apart, centred on `Fc`, for intermodulation and intercept tests: see [N tones](#tones).

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

Each card is one carrier: its standard (**LTE**, **NR**, **CW**, **PCW**, **FSK**, **DBPSK**, **CSS**, **SC** or **FILE**), channel bandwidth, NR
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

Not modelled: phase codes and nonlinear FM inside the pulse, and pulsing the gate or drain bias with the RF,
which is a pulsed bias source rather than a signal.

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

Not modelled: transmitting in bursts with a duty cycle (a metering transmitter sends a few milliseconds
every several seconds), and AM or PM.

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

## The spectrum and the CCDF {#preview}

The **Spectrum** is the composite power spectral density against the offset from `Fc`, framed on the
occupied channels; for a pulsed-CW source it is the train's. The **CCDF** is the probability that the instantaneous power exceeds the average by
more than a given margin.

- **The Gaussian reference.** For a noise-like signal (LTE, NR, several carriers, an I/Q file) the CCDF is
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
0.01 % and the peak, and each carrier's ideal ACPR.

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
