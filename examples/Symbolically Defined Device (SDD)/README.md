# Symbolically Defined Device (SDD)

A nonlinear GaN FET model written as equations, built up in five steps from its drain current to its charges, each step a
bench of its own under the same loadpull, so you can see what each equation does to the device. A sixth bench reads
another part's current inside an SDD.

The FET is the one every harmonic-balance reference in circuitRF is checked against, at the *Loadpull and Pursuit*
example's 1.25 mm periphery, 48 V and −3.05 V bias, 2 GHz and polar load grid (`load_grid.gam`), with more load points
where the optima fall.

## What an SDD is

A **Symbolically Defined Device** describes a part by the currents at its ports as expressions of the voltages across them.
There is no compiled model behind it: what you write is the model.

- **Ports.** An SDD with N ports takes 2N nets, a `+` and a `−` for each. The FET here is a 2-port, `Vin 0 Vout 0`: port 1
  is gate to source (ground), port 2 drain to source. Inside the equations **`_v1` is Vgs and `_v2` is Vds**.
- **Equations.** **`I[p,w]`** is a contribution to the current into port p's `+` net, out of its `−` net. Every
  contribution to one port is summed.
- **Weights.** The second index, **`w`**, says how the expression becomes a current. circuitRF evaluates `I[p,w]` in the time
  domain, takes its spectrum, multiplies it by a weighting function `H[w](f)`, and adds the result to port p's current:
  - **`w = 0`**, `H[0] = 1`: the expression **is** a current. A resistor, a diode, a transconductance.
  - **`w = 1`**, `H[1] = j2πf`: the expression is a **charge**, and the current is its time derivative, dQ/dt. A
    capacitor. At DC it passes nothing, as a capacitor should.
  - **`w ≥ 2`**: weights you define, as expressions of `freq` (`H[2]=1/(1+j*2*pi*freq*tau)` is a low-pass, for one).
- **Everything else on the line** is a parameter the equations read (`B`, `TV0`, `Cgs0` …). Edit one on the schematic and the
  model changes; sweep it, tune it or optimize it like any other value.

circuitRF differentiates every expression exactly (automatic differentiation), so the conductances `∂I/∂V` and capacitances
`∂Q/∂V` the solver needs come from the equations you wrote, with nothing else to supply. Write charges, not capacitances:
a charge `Q(V)` whose derivative is the capacitance you want conserves charge in harmonic balance; a capacitance written
directly does not.

Open each bench's schematic and double-click the SDD to read its equations.

## 1 Drain Current

The least an FET model can be: the drain current as a function of both port voltages.

```
I[1,0] = _v1/50
I[2,0] = Periphery_mm*(B*TC*tanh(_v2*a*(tanh(g*(TV0-_v1+_v2*th+Sc*ln(exp(-(Sv-_v1)/Sc)+1)))+1))
         *ln(exp(-(2*TV0-2*_v1+2*_v2*th+2*Sc*ln(exp(-(Sv-_v1)/Sc)+1))/TC)+1)*(_v2*lam+1))/2
```

`I[2,0]` is the drain current Ids(Vgs, Vds): the soft pinch-off (`Sv`, `Sc`), the transconductance's shape (`TV0`, `TC`),
the knee (`tanh(_v2*a…)`), output conductance (`lam`) and scale (`B`, times the periphery). `I[1,0] = _v1/50` gives the gate
50 Ω to ground, which is how this reference model sets its input impedance. Nothing here stores charge, so the device is
instantaneous: its loadpull optimum sits on the real axis.

## 2 Gate Current

The gate is a Schottky diode to the channel, and a high enough drain voltage breaks down to the gate. Both join the
equations as further `w = 0` currents:

```
Igs = Periphery_mm*Isg*(if(_v1/Vtg<40, exp(_v1/Vtg), exp(40)*(1+_v1/Vtg-40)) - 1)          forward gate conduction
Ibr = Periphery_mm*Ibk*if((_v2-_v1-Vbr)/Vbk<40, exp(…), exp(40)*(1+…-40))                  gate-drain breakdown past Vbr
I[1,0] = _v1/50 + Igs - Ibr
I[2,0] = Ids + Ibr
```

**The breakdown current leaves the drain and enters the gate, so it appears in both ports with opposite signs**: a current
between two terminals of a multi-port SDD is always written that way. The `if()` continues each exponential along its
tangent past an argument of 40. The model is the same where the device works, and a Newton iterate that overshoots can't
overflow. Near compression the gate swings positive and conducts, which clamps the drive: maximum power falls by 0.2 dB.

## 3 Charges (1D)

Now the device stores charge. Two charges on the `w = 1` path, each a function of **one** voltage:

```
I[1,1] = Periphery_mm*(Cgsp*_v1 + Cgs0/2*(_v1 + ln(cosh(Kgs*(_v1-Vgs0)))/Kgs))      Qgs(Vgs)
I[2,1] = Periphery_mm*(Cdsm*_v2 + (Cds0-Cdsm)*Vds0*(1-exp(-_v2/Vds0)))               Qds(Vds)
```

Differentiate each and you get the capacitance you meant: Cgs(Vgs) = `Cgsp + Cgs0/2·(1 + tanh(Kgs·(Vgs − Vgs0)))`, rising
from 0.3 to 1.3 pF/mm as the channel opens; Cds(Vds) = `Cdsm + (Cds0 − Cdsm)·exp(−Vds/Vds0)`, falling from 0.5 to 0.2 pF/mm as
the drain voltage rises. Each is a **1D nonlinear capacitor**: its charge depends on its own voltage only. The output
capacitance must be resonated by the load, so the optimum loads move up into the inductive half of the chart.

## 4 Charges (2D)

Real FET charges depend on both voltages. Two changes make them two-dimensional:

```
Qgs  = Periphery_mm*(Cgsp*_v1 + Cgs0/2*(_v1 + ln(cosh(Kgs*(_v1-Vgs0)))/Kgs)*(1 + Dgs*tanh(-_v2/Vgsd)))
Qgd  = Periphery_mm*(Cgdm*(_v1-_v2) - (Cgd0-Cgdm)*Vgd0*(1-exp(-(_v2-_v1)/Vgd0)))
I[1,1] = Qgs + Qgd
I[2,1] = Qds - Qgd
```

- **Qgs now also falls as Vds rises** (the channel pinches off at the drain end). The gate charge depends on Vds, so
  ∂Qg/∂Vds is a transcapacitance: a change of drain voltage moves charge at the gate.
- **Qgd is the gate-drain charge**, a function of the voltage *between* the two ports, Vgd = `_v1 − _v2`, with
  Cgd(Vdg) falling from 0.15 to 0.04 pF/mm as the drain rises above the gate. Like the breakdown current, a charge between two
  terminals is added at one port and subtracted at the other.

Cgd is feedback from output to input, and it costs gain: Gt at maximum power falls from 15.3 to 14.9 dB.

## 5 Cgd as an SDD Block

The same model with Qgd taken out of the FET and drawn as **its own one-port SDD**, `CGD`, connected from gate to drain:

```
SDD:CGD  Vin  Vout     I[1,1] = Periphery_mm*(Cgdm*_v1 - (Cgd0-Cgdm)*Vgd0*(1-exp(_v1/Vgd0)))
```

Its one port's `+` net is the gate and its `−` net the drain, so its `_v1` is Vgd. That is a 1D nonlinear capacitor anywhere
in a schematic, between any two nets, written in one line. The loadpull reads **exactly** what bench 4 reads. Equations
inside one SDD and equations split across several are the same circuit, which is how a large model can be assembled from
blocks you can check one at a time. (A **NonlinearC** component does the same job as a polynomial C(V); an SDD takes any
expression.)

## The loadpull

Each of benches 1 to 5 runs the *Loadpull and Pursuit* example's loadpull: source and load tuners with their bias tees,
the load swept over 97 points (the example's polar grid at 30° steps, and rings every 0.05 in |Γ| and 15° in angle where
the optima fall, so the interpolated optimum lands where the contours say), the drive raised at each load to 3 dB gain
compression. Its display, which Simulate
opens, draws the **Pout contours with the maximum-power point MXP (P)** and the **PAE contours with the maximum-efficiency
point MXE (E)** on one Smith chart, and a summary table at each optimum, read between grid points:

| Bench | MXP: Pout | Gt | PAE | ZL (Ω) | MXE: Pout | PAE | ZL (Ω) |
|---|---|---|---|---|---|---|---|
| 1 Drain Current | 41.6 dBm | 15.4 dB | 63.8 % | 63.3 + j0.2 | 40.4 dBm | 68.7 % | 101.3 − j1.2 |
| 2 Gate Current | 41.4 dBm | 15.7 dB | 65.9 % | 68.0 + j0.3 | 40.4 dBm | 68.7 % | 102.1 − j1.9 |
| 3 Charges (1D) | 41.4 dBm | 15.3 dB | 66.1 % | 65.2 + j14.7 | 40.6 dBm | 68.6 % | 85.6 + j27.8 |
| 4 Charges (2D) | 41.4 dBm | 14.9 dB | 66.0 % | 63.7 + j17.0 | 40.7 dBm | 68.5 % | 80.4 + j31.7 |
| 5 Cgd as an SDD Block | 41.4 dBm | 14.9 dB | 66.0 % | 63.7 + j17.0 | 40.7 dBm | 68.5 % | 80.4 + j31.7 |

Each runs in one or two seconds.

## 6 Bias Loop (control current)

An SDD equation can read the **current in another part** as `_cn`, bound by `C[n]=<instance>`. Here that builds an
**active bias controller**: an SDD that senses the drain supply's current and sets the gate bias so the quiescent current
stays at its target, whatever the transistor's threshold.

```
SDD:BIAS  vg 0    I[1,0] = (_v1 - VGS0 - Vspan*tanh(Kloop*(Iset - _c1)/Vspan))/Rc    C[1] = IDD
```

`C[1] = IDD` makes `_c1` the current through the IProbe `IDD` in the drain supply line. The equation is a source behind
`Rc` = 1 mΩ that holds its node at `VGS0 − Vspan·tanh(Kloop·(Idd − Iset)/Vspan)`: proportional control, `Kloop` volts of
gate per amp of error, with its output held within `Vspan` = ±1 V of the nominal bias by the tanh, as a real controller is
held by its supply rails. The gate is fed from that node through a choke. Bench 5's FET is swept over a threshold shift `dVt`
(added to `TV0`), with the loop open (`Kloop` = 0) and closed (200 V/A), at DC:

| dVt | Idq, open loop | Idq, closed loop | Gate bias, closed loop |
|---|---|---|---|
| −0.3 V | 91.4 mA | 62.9 mA | −3.34 V |
| 0 | 61.4 mA | 61.4 mA | −3.05 V |
| +0.3 V | 41.2 mA | 59.9 mA | −2.75 V |

Open loop, a 0.3 V threshold spread moves the quiescent current by ±50 %; closed, by ±2.5 %, the residual a proportional
loop of gain 16 (200 V/A times the device's 80 mA/V) leaves. A larger `Kloop` holds it tighter. The rails matter: a loop this
simple also has a second, unphysical solution, the gate driven so far negative that gate-drain breakdown carries the supply
current and the loop pushes the gate further still. With `Vspan=1000` the DC solve finds it at dVt = +0.3 V, the gate at
−238 V.

**Under drive.** The bench also runs a harmonic-balance drive sweep, 10 to 30 dBm at 2 GHz, into bench 5's maximum-power load
(the `LOAD` tuner: 63.7 + j17.0 Ω at the drain, its harmonics shorted), with the loop open and closed:

| Pin | Idd, open loop | Idd, closed loop | Gate bias, closed loop | Pout, open | Pout, closed |
|---|---|---|---|---|---|
| 10 dBm | 83.1 mA | 62.9 mA | −3.29 V | 23.7 dBm | 21.5 dBm |
| 14 dBm | 116.7 mA | 65.5 mA | −3.59 V | 28.9 dBm | 24.2 dBm |
| 18 dBm | 198.1 mA | 80.2 mA | −4.05 V | 34.7 dBm | 27.4 dBm |
| 22 dBm | 340.2 mA | 204.8 mA | −4.05 V | 39.7 dBm | 35.9 dBm |
| 30 dBm | 535.6 mA | 480.0 mA | −4.05 V | 41.8 dBm | 41.7 dBm |

The RF swing raises a class-AB stage's average current, and this controller answers it instantly: it pushes the gate negative to
hold the current near its 61.4 mA target, deeper towards class B, which costs gain (2.1 dB at 10 dBm, 7.4 dB at 18 dBm). At
18 dBm it reaches its rail, VGS0 − Vspan = −4.05 V, and from there the current rises as the open loop's does, and the output
meets it again at saturation. A loop filter, a weighting function `I[1,2]` with `H[2]=1/(1+j*2*pi*freq*Tloop)`, acts at DC and
not at the harmonics, so it keeps RF off the controller's output; harmonic balance solves the steady state, where even a slow
loop has settled on the average current, so the bias it reaches is the one above. A controller that holds the bias it set
before the drive arrives, as a sampled one does between bursts, is a transient question.
