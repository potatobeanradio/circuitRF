# TRL and De-embedding

**Run the three numbered benches in order, top of the Workspace tree first:**

- **`1 TRL Calibration`** solves the Thru, Reflect and Line for the two fixtures and writes them to `fixtures/`.
- **`2 Deembed DUT`** removes both fixtures from a measured filter and lays the result over the filter itself. It reads
  the files `1 TRL Calibration` writes, so run that first.
- **`3 FET Deembed`** removes a GaN FET's extrinsic network, a 6-port, to show the device at its intrinsic plane.

Each opens its Data Display when it finishes. The `Lab` benches made the files in `measurements/` and need not be run.

A part is rarely measured on its own. The instrument is calibrated at its connectors, and between those and the part
sit fixtures: launches, lines, probe pads. TRL finds both fixtures from three measurements made through them, and a
**Deembed** block then takes them off any measurement made the same way. The three benches above are the ones to
duplicate for your own data.

## What TRL is

The fixture between the instrument's port 1 and the part is fixture A, and the one between the part and port 2 is
fixture B. Neither is known. TRL measures three standards through the same two fixtures:

- **Thru**: A and B joined directly.
- **Reflect**: A into a reflection, and the same reflection into B. Its value need not be known, only whether it is
  nearer an open or a short.
- **Line**: A and B joined by a short extra length of uniform line. Its length and loss need not be known either.

From those three, both fixtures come out, and the part's own S-parameters follow from any measurement made through
them. Very little has to be known about the standards, which is why TRL is used where good standards are hard to make:
on a board, on a wafer, in a test fixture.

## The model and the equations

Each two-port is written as a cascade matrix T, so that networks in a chain multiply. From S:

```
T = 1/S21 · [ −det S    S11 ]        det S = S11·S22 − S12·S21
            [ −S22       1  ]
```

and back again:

```
S11 = T12/T22    S12 = det T/T22    S21 = 1/T22    S22 = −T21/T22
```

With A and B the fixtures' cascade matrices, the measured Thru and Line are

```
Mt = A·B
Ml = A·L·B        L = diag(e^−γℓ, e^+γℓ)
```

where L is the extra length of line, with propagation constant γ and length ℓ. Then

```
Ml·Mt⁻¹ = A·L·A⁻¹
```

so the columns of A are the eigenvectors of Ml·Mt⁻¹, and its eigenvalues are e^−γℓ and e^+γℓ: the Line's electrical
length and loss come out too. An eigenvector fixes a column only up to a scale, so one unknown is left over, and the
Reflect fixes it: the same reflection seen through A and through B must be the same reflection. Its sign is the one
choice the solve cannot make for itself, which is why `reflectType` says open or short. B follows from B = A⁻¹·Mt, and
any measurement M gives the part:

```
DUT = A⁻¹ · M · B⁻¹
```

## What the standards must be

- **Thru**: the reference plane is its middle. The de-embedded part is the part between the two halves of the Thru.
- **Reflect**: the same on both ports, highly reflecting, and known only as an open or a short. It need not be ideal;
  the one here is a short behind 1 mm of line and 0.5 Ω.
- **Line**: the same line as the Thru's, longer by an electrical length between **20° and 160°** at every frequency of
  the band. Near 0° or 180° the Line looks like the Thru and the two cannot be told apart. The Line's impedance is the
  impedance the part comes back referenced to.

The Line is best at 90° at the middle of the band. For a band f1 to f2, with fc = (f1 + f2)/2:

```
ℓ = c₀ / (4 · fc · √εeff)                    a quarter wave at fc
θ(f1) = 90° · f1/fc = 180° · f1/(f1 + f2)    ≥ 20° needs f2/f1 ≤ 8
```

So one Line covers an 8:1 band. For a wider one, use two Lines and two calibrations, each over its own band. The
`Line Standard` cell is cut this way for 1 to 8 GHz, which puts its edges at exactly 20° and 160°: the `1 TRL Calibration`
display plots the Line's length against both limits.

## Using your own measurements

Duplicate `1 TRL Calibration`, then point it at your files:

| Change | To |
|---|---|
| `THRU`, `REFLECT`, `LINE` | Each SnP's File: your Thru, Reflect and Line `.s2p`, all on one frequency grid |
| `CAL`'s Reflect | Open or Short, whichever your reflect is nearer |
| `CAL`'s fixture files | Where the two fixtures are written; the same strings go into the Deembeds' File |
| `SP1` | Your band, if you want to see the raw standards |

Run it. If the Line leaves 20° to 160° anywhere in the band, the run says where; the fixtures there are not to be
trusted. Then duplicate `2 Deembed DUT`:

| Change | To |
|---|---|
| `MEAS` | Your part's measured `.s2p` |
| `DA` and `DB` | The two fixture files, if you renamed them |
| `SP1` | Your band |
| `X1`, `RAW` and ports 3 to 6 | Delete them: they are this example's comparison |

## The Deembed component

A Deembed is the opposite of an SnP. An SnP on a file adds that network to the circuit; a Deembed on the same file takes
it away. It takes any even number of ports: ports 1 to N/2 are one side of the network, and the rest the other, in the
file's order.

**Wire it the way you would wire the SnP that adds the network, then swap the SnP for the Deembed and the part for its
measurement.** Fixture A's port 1 is the instrument side, so `Port 1 — Deembed(A) — measurement`. Fixture B's port 1 is
the part's side, so `measurement — Deembed(B) — Port 2`. Neither needs flipping.

It works in every analysis an SnP works in: DC, S-parameters, harmonic balance, loadpull and sweeps. An SnP and a
Deembed on one file, side by side, are a through connection to rounding, at the file's points and between them.

The inverse of a lossy network has gain, so a Deembed is active: a stability analysis that needs every part to be
passive refuses it by name.

## The FET

`3 FET Deembed` places the 8-finger GaN device from the Model Composer example (`models/gan_4x125.cmodel`) twice, biased
as there: −0.8 V on the gate and 8 V on the drain. Its model carries an extrinsic network (pad capacitances, lead
inductances, access resistances) between its outer gate, drain and source and its intrinsic device. `GaN Extrinsic` is
that network as a cell, and `Lab Extrinsic` measures it as a 6-port: ports 1 to 3 the outer gate, drain and source, and
4 to 6 the inner ones.

On ports 1 and 2, a Deembed of that 6-port sits between the ports and the device, so the ports see the intrinsic device.
The bias tees are outside the Deembed, so the DC current flows through it. On ports 3 and 4, the same device is measured
as it is. Each copy's intrinsic elements are read from its Y-parameters:

```
Cgs = Im(Y11 + Y12)/ω      Cgd = −Im(Y12)/ω      gm = |Y21 − Y12|      Rds = 1/Re(Y22 + Y12)
```

The `3 FET Deembed` display plots all four against frequency. After de-embedding they are flat, as an intrinsic device's
are: Cgs within 0.4 % from 1 to 20 GHz, Rds within 0.01 %. As measured, the lead inductances and pad capacitances bend
them: Cgs by 17 % and Rds by 57 % over the same band. That is the point of de-embedding to the intrinsic plane. Below
1 GHz both copies' Cgs dip: the 10 µH gate chokes are outside the Deembed, and at 0.5 GHz theirs is the admittance
left in Im(Y11).

**The source needs a net of its own.** The Deembed's pin 3 (the outer source) goes to ground, and pin 6 (the inner
source) goes to the device's source, which is on nothing else. Pins 3 and 6 are the two ends of one path through the
network, the source lead. With both on ground, the current leaving one is not made equal to the current entering the
other, so that path is never removed, and the answer is wrong while looking plausible. A run with both ends of a path on
ground warns, naming the pins.

The HB analysis drives both copies at 10 dBm. `IG1` and `ID1` sit at the Deembed's pins 1 and 2, so `Vgs_intrinsic`,
`Ig_intrinsic`, `Vds_intrinsic` and `Id_intrinsic` are the voltages and currents at the intrinsic plane.

## The synthetic data

Every measurement here is made by circuitRF from cells whose values are stated, so the de-embedded answer can be checked
against the truth, which a real measurement never allows. The files in `measurements/` are inputs, and each Lab bench
writes one. To make them again, run the bench and export S as Touchstone, or from a terminal:

```
circuitrf sparam "Lab Thru/schematic/Lab Thru.csch" -o measurements/thru.s2p
```

| Cell | What it is |
|---|---|
| `Launch A` | Fixture A: 0.3 nH in series, 0.15 pF shunt, 9 mm of 40 Ω line. Port 1 is the instrument side |
| `Launch B` | Fixture B: 12 mm of 60 Ω line, 0.12 pF shunt, 0.35 nH in series. Port 1 is the part's side |
| `Line Standard` | 50 Ω line, a quarter wave at 4.5 GHz: 20° at 1 GHz and 160° at 8 GHz |
| `DUT` | A five-section stepped-impedance low-pass filter, 20 Ω and 110 Ω sections, cut off near 5 GHz |
| `GaN Extrinsic` | The FET's extrinsic network, its values the elaborated device's at 8 fingers |
| `Lab Thru` | A then B: `measurements/thru.s2p`, 1 to 8 GHz, 141 points |
| `Lab Reflect` | A into a short, and the same short into B: `measurements/reflect.s2p` |
| `Lab Line` | A, the Line, then B: `measurements/line.s2p` |
| `Lab DUT` | A, the filter, then B: `measurements/dut.s2p` |
| `Lab Extrinsic` | The 6-port network from 0 to 20 GHz, so DC through the Deembed uses its real DC values: `measurements/gan_extrinsic.s6p` |

The lines are lossy, with conductor and dielectric loss, and the two fixtures are deliberately different and
mismatched. On this data, `2 Deembed DUT` returns the filter to within 2e-10 in S, which is the precision the Touchstone
files are written to.
