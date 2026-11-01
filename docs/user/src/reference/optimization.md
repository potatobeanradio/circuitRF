---
title: Optimization
slug: reference/optimization.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Optimization
lede: State what the design must do, say which values may move, and let circuitRF find values that do it.
keywords: optimization, optimizer, optimize, goal, goals, cost, least squares, minimax, Levenberg-Marquardt, gradient, simplex, differential evolution, genetic, CMA-ES, particle swarm, Bayesian, pattern search, railed, widen, snap, polish, preferred values, sensitivity, stability, mu, opt
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What the optimizer does</a></li>
<li><a href="#variables">Variables</a></li>
<li><a href="#goals">Goals</a></li>
<li><a href="#add-as-goal">Add as Goal, from a plot</a></li>
<li><a href="#cost">How the goals become one number</a></li>
<li><a href="#algorithms">Algorithms, and when to use each</a></li>
<li><a href="#algorithm-details">Each algorithm in detail</a></li>
<li><a href="#settings">Settings</a></li>
<li><a href="#running">Running, pausing, and reading the progress</a></li>
<li><a href="#railed">Railed variables</a></li>
<li><a href="#complex">Complex values</a></li>
<li><a href="#snap">Integers, steps and preferred values: snap and polish</a></li>
<li><a href="#keeping">Keeping the result</a></li>
<li><a href="#headless">From the command line, and from an agent</a></li>
</ol>
</nav>

## What the optimizer does {#what}

You give the **Optimizer** panel three things: the values it may move (**variables**), each within a
range; what the design must do (**goals**), each a limit on an expression over one analysis; and an
**algorithm**. Press ▶ and it simulates the schematic at one set of values after another, keeping the
best set found so far, until every goal is met or it runs out of iterations, simulations or time.
Every open Data Display follows the best point as it improves.

The panel is tabbed behind [Tuning](tuning.html) (View ▸ Panels ▸ Optimizer) and follows the focused
schematic the same way. Everything you set in it — variables, goals, algorithm, settings — is saved
with the schematic, marks it changed and can be undone. **The optimizer never changes the
schematic's values on its own**: the best point is yours to Push, lock in as a preset, or throw away.

{{ui: optimizer-panel}}

**Start with the example.** File ▸ New ▸ Examples ▸ *Tuning and Optimization* has three benches that each
optimize in under a second: a two-value L-section match, a four-value bandpass filter set up for
Minimax, and an amplifier stabilized for μ ≥ 1.05 while its source impedance is tuned by magnitude
and phase — with one range drawn too narrow on purpose, to show what a railed variable looks like.

## Variables {#variables}

The variables are **the same entries as the Tuning panel's**, with the same ranges: a range changed
in either panel is changed in both. A tick on a row means the optimizer may move it. **＋** adds
values, through the same search Tuning uses, and anything [Tuning can tune](tuning.html#choosing)
can be optimized.

Each row shows its range as a bar with the best value found marked on it. Click the minimum or
maximum to change it.

**Choose ranges you would accept.** The optimizer searches only inside them, and a range is also what
it scales each variable by: a log range (the default for a positive range spanning a factor of ten or
more) gives every factor of two the same weight, a linear one every equal step.

## Goals {#goals}

A goal is one expression, evaluated over one analysis, held to a limit. **＋** opens the goal editor.
On its left is a list of templates for the analyses the schematic declares:

- for an S-parameter analysis: |S<sub>ij</sub>| in dB or linear, phase, group delay, VSWR, μ, μ′, K
  and maximum gain, with the ports taken from the bench's ports;
- for an S-parameter analysis of a circuit with WSProbes: the probe's stability metrics;
- every `measure` row, by name — this is how to make a goal of an output power, an efficiency or a PAE
  from harmonic balance: write the `measure` row, then pick it;
- a custom expression.

A template fills in the fields; change any of them:

| Field | |
|---|---|
| **Name** | What the goal is called in the panel and in every report. |
| **Expression** | The same language as a `measure` row, so no measurement has to exist first: `dB(SP1.S(2,1))`, `mu(SP1.S)`, `phase(SP1.S(1,1))`. Checked as you type. |
| **Analysis** | The one analysis the expression reads. Each run simulates only the analyses the goals name, unless the settings say otherwise. |
| **Range** | The part of the swept axis that counts — 0.95 to 1.05 GHz, say — or *Whole range*. A range holding no point of the sweep is an error. |
| **Type** | **≤** (at or below), **≥** (at or above), **=** (at), **in** (inside a band) or **out** (outside a band). |
| **Limit** | One limit, or two for *in* and *out*. With **Sloped**, a limit at each end of the range, interpolated between them. |
| **Weight** | How much this goal counts against the others. Default 1. |
| **Scale** (Advanced) | What one unit of violation is worth ([below](#cost)). |
| **Enabled** | A disabled goal is kept but not scored. |

A goal also serves the [Yield](yield.html#specs) panel, as a spec. Its **Use** — set there — says
which: **Opt**, the optimizer aims for it and the yield ignores it; **Yield**, the yield counts it and
the optimizer ignores it; **Both**, the default. A design is usually centred against tight goals marked
*Opt*, and judged for yield against looser copies of them marked *Yield*. The goal list here shows only
the goals the optimizer aims for. In a `.cnl` it is `use=opt` or `use=yield` at the end of the goal line.

When results exist, the editor previews the expression over the range with the limit drawn on it.

Each goal row in the panel shows, at the best point so far, a bar (green when met), the worst value
in its range and ✓ or ✕. A goal whose expression reads only variables, such as `L1 - L2` ≥ 0, is a
constraint: it costs no simulation.

## Add as Goal, from a plot {#add-as-goal}

In a Data Display, right-click a trace ▸ **Add as Goal** (or a marker, or a table's trace heading) and
the goal editor opens already filled in from what the trace shows:

- **the expression** is the trace's own reading and its transform. A dB trace becomes `dB(…)`; a
  *power* dB trace becomes `dB10(…)`, so a power goal is not 3 dB wrong.
- **the range** is the plot's visible x range, clipped to the data — zoom the plot first to choose it.
- **the limit** is the value of the first visible marker, if there is one.

Some traces cannot be turned into a goal, and their menu row is greyed with the reason in its
tooltip: a complex trace with nothing reducing it to a number, stability circles and the passive
readouts, a trace plotted against another trace, a trace renormalized by the plot's own Z0, and a
trace whose source is not a simulation's results.

## How the goals become one number {#cost}

The optimizer compares points by one number, the **cost**. It is built the same way for every goal:

1. **At each point in the goal's range, the violation** — how far the value is on the wrong side of
   its limit, and 0 where it is met. For *in*, the distance to the band; for *out*, the distance to the
   nearer edge while inside it.
2. **Divided by the goal's scale**, so goals in dB, degrees and ohms can be added. Unless you set one,
   the scale is the band's width for *in* and *out*, and otherwise the larger of |limit| and 1 in the
   limit's own unit: missing −15 dB by 1.5 dB counts the same as missing a limit of 2 pF by 0.2 pF.
3. **Times the weight, divided by the square root of the number of points**, so a goal over 201
   frequencies counts no more than a goal at one.

The cost is the **sum of squares** of all of those (least squares, the default), or the **largest**
of them (minimax). A cost of 0 means every enabled goal is met. A run that ends above 0 names every
goal it missed, with its worst value and where on the axis it is.

**Least squares stops at the limit, not beyond it.** The cost is zero the moment every point meets
its limit, so a goal is usually met with its worst point right on the limit. If you want margin, ask
for it in the limit.

## Algorithms, and when to use each {#algorithms}

Choose the algorithm from the list in the panel; its tooltip is the *Use it* column below. The choice
is saved with the schematic. [Each algorithm in detail](#algorithm-details), after the table, says
what each one does, with its equations, and when to reach for it or avoid it.

{{table: optimizers}}

*Derivatives* means the algorithm estimates slopes by moving each variable a little: one extra
simulation per variable per step, and a response it assumes is smooth. **Minimax** always works on the
worst violation, whatever the cost setting says; **Gradient** works only on least squares and refuses a
minimax cost. **Auto** runs CMA-ES for a modest number of
simulations, then a local polish from its best point (Minimax under a minimax cost), then snap and
polish when any variable is discrete. The *Advanced* section of ⚙ lists each algorithm's own options
with their defaults.

### Each algorithm in detail {#algorithm-details}

**What every algorithm sees.** No algorithm sees your units. Each variable is mapped onto 0 … 1
across its range — linearly, or by its logarithm on a log range — so the algorithms search the unit
box u ∈ [0, 1]ⁿ, where n is the number of variables. A point u is turned back into values, simulated,
and scored by the goals as a vector of residuals r(u) (one per goal per point of its range,
[above](#cost)). The cost is

```
least squares   F(u) = Σᵢ rᵢ(u)²
minimax         F(u) = maxᵢ rᵢ(u)
```

In the equations below, **J** is the Jacobian, Jᵢⱼ = ∂rᵢ/∂uⱼ, and **g** the gradient of F. Neither
comes from the simulator: an algorithm that needs them estimates them by **forward differences**,
moving one variable at a time by `fdstep` (10⁻⁶ of its range), which is n extra simulations per
estimate:

```
Jᵢⱼ ≈ ( rᵢ(u + h·eⱼ) − rᵢ(u) ) / h          h = fdstep
```

Two families follow from that. **Local** methods (Gradient, Quasi-Newton, Minimax, Simplex,
Trust-region model, Pattern search) walk downhill from the starting values and finish at the nearest
minimum they reach — fast, and only as good as the start. **Global** methods (Differential evolution,
Particle swarm, CMA-ES, Bayesian, Random) spread their simulations over the whole box and can climb
out of a poor start, at the price of many more simulations.

#### Auto {#alg-auto}

**What it does.** Three stages, one after the other: CMA-ES for 50·(n + 1) simulations (or half of
*Max evaluations*, when that is fewer) to find the right region; then Gradient (Levenberg–Marquardt)
from the best point CMA-ES found — Minimax instead, under a minimax cost — to finish the job
precisely; then snap and polish if any variable is an integer, a step or a preferred value. A stage
that meets every goal skips straight to the snap. The header names the stage that is running.

**Use it** when you have no reason to choose another — it is the right default for most circuits of
a handful of variables whose simulations take well under a second.

**Avoid it** when each simulation is slow (seconds or more): its first stage alone is 50·(n + 1)
simulations, so choose **Bayesian**. And when the start is already close, **Gradient** alone gets
there in a fraction of the simulations.

#### Gradient (Levenberg–Marquardt) {#alg-lm}

**What it does.** Treats the goals as a least-squares problem. At each step it estimates J, then
solves for the step δ that a straight-line model of the residuals says is best, damped by λ:

```
(JᵀJ + λ·s·I) · δ = −Jᵀr          s = the largest diagonal entry of JᵀJ
u ← u + δ        if F(u + δ) < F(u),  then  λ ← λ/3
λ ← 4λ           otherwise (10λ after a failed simulation), and solve again
```

Small λ is the Gauss–Newton step — the jump straight to the bottom of the model, which converges in a
few steps near the answer. Large λ shortens the step and turns it toward steepest descent, which is
always downhill. A variable held at a bound whose gradient points out of the box is held there and
left out of the solve.

**Use it** when the start is reasonably close and the response is smooth — tuning a match, a filter
or a bias point from a sensible hand design. It is usually the fewest simulations to the nearest
answer, and it is what the *Tuning and Optimization* example's L-section uses (39 simulations).

**Avoid it** from a poor start or on a response with several valleys — it finds the nearest minimum,
not the best one. Avoid it on noisy or jumpy responses (a harmonic balance that converges
differently from point to point), where the difference estimates are garbage. It cannot use a minimax
cost.

#### Quasi-Newton (BFGS-B) {#alg-bfgsb}

**What it does.** Works on the cost F itself rather than on the residuals. It estimates the gradient
g by differences and builds up an approximation of the curvature from how g changes between steps
(the last `memory` = 5 pairs), so it never has to compute second derivatives:

```
sₖ = uₖ₊₁ − uₖ,   yₖ = gₖ₊₁ − gₖ
d  = −Hₖ·g                    Hₖ ≈ (∇²F)⁻¹, built from the stored (sₖ, yₖ) pairs
u ← P[u + α·d]                P = projection onto the box; α halved until
                              F(u + α·d) ≤ F(u) + 10⁻⁴·α·gᵀd   (Armijo)
```

A variable on a bound is held there while the gradient pushes it outward, which is what the **B**
(bounds) means.

**Use it** for smooth responses where an answer is expected to sit **on** a range limit — a
resistor wanting to go to its smallest allowed value, say — and when goals cannot all be met and you
want the least-squares compromise found precisely.

**Avoid it** on noisy or discontinuous responses, and from a poor start. With a minimax cost the
worst violation switches from one point to another, which leaves a kink in the cost that its
curvature estimate does not expect — use **Minimax** for that. With a least-squares cost on goals
that can all be met, Gradient usually gets there in fewer simulations.

#### Minimax {#alg-minimax}

**What it does.** Lowers the **largest** single violation rather than the sum of squares. At each
step it linearizes every residual and solves a small linear program for the step d that minimizes the
worst one within a trust region of size Δ:

```
minimize  t    subject to   rᵢ + Jᵢ·d ≤ t    for every residual i
                            |d|∞ ≤ Δ,  0 ≤ u + d ≤ 1
ρ = (actual fall in F) / (predicted fall)
Δ ← 2Δ  if ρ > 0.75 and the step reached the edge;   Δ ← ½|d|∞  if ρ < 0.25
```

Because it works on signed residuals, it keeps pushing the worst points down until several of them
are equally bad — which is what makes a response **equal-ripple**. It always uses the minimax cost,
whatever *Cost* says.

**Use it** for equal-ripple responses (filter passbands, flat gain), worst-case specifications, and
any goal set where one bad frequency matters more than many good ones. The example's bandpass
filter uses it.

**Avoid it** from a poor start (it is a local method) and on noisy responses. When goals cannot all
be met it balances the misses; if you would rather miss one goal badly and meet the rest, use a
least-squares method.

#### Simplex (Nelder–Mead) {#alg-simplex}

**What it does.** Keeps n + 1 points (a simplex) and repeatedly replaces the worst one by reflecting
it through the centroid c of the others, expanding the move if it worked well, contracting it if it
did not, and shrinking the whole simplex toward the best point when nothing works. It uses only cost
comparisons — no derivatives:

```
reflect    x_r = c + α·(c − x_worst)
expand     x_e = c + β·(x_r − c)
contract   x_c = c + γ·(x_worst − c)        (or toward x_r)
shrink     xᵢ ← x_best + δ·(xᵢ − x_best)

α = 1,  β = 1 + 2/n,  γ = 0.75 − 1/(2n),  δ = 1 − 1/n      (Gao–Han, adapted to n)
```

Points that leave the box are projected back onto it. A collapsed simplex is rebuilt around its best
point once (`restarts`).

**Use it** for a few variables (up to about 5–6) when the response is noisy, kinked or otherwise not
smooth and the start is reasonable. It costs about one simulation per iteration.

**Avoid it** with many variables — it slows down badly beyond about ten — and from a poor start. On a
smooth response, Gradient or the Trust-region model is far faster.

#### Trust-region model {#alg-trust-region}

**What it does.** Fits a quadratic model of the cost through the points it has simulated (2n + 1 of
them to start), steps to the model's minimum within a trust region, and judges the model by how
well it predicted the result:

```
m(d) = F(x_b) + gᵀd + ½·dᵀH·d          fitted through the stored points; each new fit
                                        changes H as little as possible (least-change)
d   = argmin m(d)   over |d|∞ ≤ Δ, inside the box
ρ   = (F(x_b) − F(x_b + d)) / (m(0) − m(d))
Δ ← larger when ρ > 0.7,  smaller when ρ < 0.1
```

A second radius, the model's resolution, only ever shrinks; when the model is poorly placed it adds a
point to improve its geometry rather than to lower the cost. No derivatives are estimated: every
simulation contributes to the model.

**Use it** for 2 to 20 variables when simulations are **expensive** and the response is **smooth** —
it typically needs far fewer simulations than the difference-based methods, because it never spends
n simulations on one gradient.

**Avoid it** on noisy or jumpy responses (the quadratic fit chases the noise), from a poor start, and
for more than about 20 variables.

#### Pattern search {#alg-pattern}

**What it does.** From the best point x, tries ("polls") 2n points in a set of directions that spans
every direction, scaled to a poll size Δp and rounded onto a mesh of size Δm:

```
poll       x ± Δp·dⱼ,   j = 1 … n      (OrthoMADS: a fresh orthogonal set every poll)
mesh       Δm = Δp²
success    x ← the best poll point,   Δp ← 2·Δp
failure    Δp ← Δp / 2;    stop when Δp < minpoll
```

It also tries one **speculative** point twice as far along a successful step, and the minimum of a
quadratic fitted through the points it has seen. A point that fails to simulate, or lies outside the
box, is simply a poll point that did not improve.

**Use it** when **some points fail to simulate** or the response **jumps** — a harmonic balance that
does not converge everywhere, a response with switching behaviour. It has a convergence guarantee
that does not depend on smoothness.

**Avoid it** when simulations are cheap and the response is smooth (Gradient is much faster), and
for many variables — each poll is 2n simulations.

#### Random {#alg-random}

**What it does.** Samples the box. Each batch is max(8, 2n) points, drawn as a **Latin hypercube**
by default: every variable's range is cut into as many equal slices as there are points, and each
slice is used exactly once, so even a small batch covers every range evenly. The first batch includes
the start.

**Use it** to **survey** the box — see what range of results is reachable — or to find a decent
starting point for a local method (Push or Send to Tuning the best point, then run Gradient).

**Avoid it** as a way to finish: it never converges, and only stops on a limit — set *Max
evaluations*, *Max iterations* or a *Time limit*.

#### Differential evolution {#alg-de}

**What it does.** Keeps a population of points (18n to start) and makes each new candidate from
differences between members, so its steps shrink automatically as the population closes in. This is
L-SHADE, a self-adapting form:

```
mutate     v = xᵢ + F·(x_pbest − xᵢ) + F·(x_r1 − x_r2)
           x_pbest: one of the best 11 %;  x_r2 may come from an archive of replaced members
crossover  uⱼ = vⱼ  if rand < CR  or  j = j_rand,   else  xᵢⱼ
select     xᵢ ← u   if F(u) ≤ F(xᵢ)
adapt      F ~ Cauchy(M_F, 0.1),  CR ~ Normal(M_CR, 0.1),  M_F and M_CR learned from the
           values that produced successful candidates
```

The population shrinks linearly to 4 over the planned budget (*Max evaluations*, or 1000·n), trading
exploration for convergence as the run goes on.

**Use it** from a **poor start**, on a response with **many local minima**, or when you do not trust
your starting values at all. It is the genetic-family method on the menu.

**Avoid it** when simulations are slow: it routinely needs hundreds to thousands of them. When the
start is close, a local method is many times cheaper. Set *Max evaluations*, which also sets its
schedule.

#### Particle swarm {#alg-pso}

**What it does.** A swarm of max(30, 10n) particles flies through the box. Each particle remembers
its own best point pᵢ and is pulled toward it and toward the best point of its neighbourhood lᵢ (its
two ring neighbours, or the whole swarm with `topology=global`):

```
vᵢ ← χ·( vᵢ + c₁·r₁·(pᵢ − xᵢ) + c₂·r₂·(lᵢ − xᵢ) )
xᵢ ← xᵢ + vᵢ
χ = 2 / |2 − φ − √(φ² − 4φ)|,   φ = c₁ + c₂       (0.7298 at c₁ = c₂ = 2.05)
r₁, r₂ uniform in [0, 1];  each velocity component limited to ±vmax
```

A particle that leaves the box is reflected back in.

**Use it**, like differential evolution, for a global search from a poor start; it is often faster
when the variables act fairly independently of one another.

**Avoid it** when simulations are slow, and do not expect it to stop on its own — it runs until the
swarm collapses or a limit ends it, so set one.

#### CMA-ES {#alg-cmaes}

**What it does.** Samples each generation from a multivariate normal distribution, then moves the
distribution's centre toward the best samples and reshapes its covariance to follow the directions
that worked — so it learns which combinations of variables matter, and how much each one does:

```
sample     xₖ = m + σ·N(0, C),          k = 1 … λ,   λ = 4 + ⌊3·ln n⌋
recombine  m ← Σ wᵢ·x_(i)               over the best μ = λ/2, weights wᵢ decreasing
adapt C    from the path the centre has travelled (rank-one) and from the
           spread of the best samples (rank-μ)
adapt σ    lengthen when successive steps line up, shorten when they cancel
```

A sample outside the box is drawn again rather than penalized. A run that stalls restarts with a
doubled population (IPOP), up to 9 times — larger populations see past more local minima.

**Use it** on **multi-modal** or **badly scaled** responses — variables of very different sensitivity,
or strongly interacting ones. It is the first stage of **Auto**.

**Avoid it** when simulations are slow, or when the start is close and the response smooth: a local
method is far cheaper there.

#### Bayesian (slow simulations) {#alg-bayes}

**What it does.** Spends computation, not simulations. It fits a statistical model — a Gaussian
process — to every point simulated so far, which predicts the cost anywhere in the box together with
how uncertain that prediction is. The next point is the one with the largest **expected
improvement** over the best cost so far, F*:

```
prediction   F(u) ~ Normal( μ(u), σ(u)² )        Matérn-5/2 Gaussian process,
                                                 one length scale per variable
z  = (F* − μ(u)) / σ(u)
EI(u) = (F* − μ(u))·Φ(z) + σ(u)·φ(z)            Φ, φ: the normal CDF and density
next point = argmax EI(u)
```

EI is large where the prediction is low (exploit) or very uncertain (explore). It starts from 2n + 1
Latin-hypercube points. Above 10 variables it switches to a trust-region form that models only a
box around the best point. Points that fail to simulate are avoided.

**Use it** when **each simulation takes seconds or more** — harmonic balance over a drive sweep,
loadpull, anything with an EM model — and the variables are few (up to about 10–20). It often
needs tens of simulations where the others need hundreds.

**Avoid it** when simulations are fast: choosing each point costs time that grows with the cube of
the number of points kept (up to 500), so beyond a few hundred points the bookkeeping is slower
than simulating. It is also a poor finisher — for many digits of precision, polish with Gradient.

#### Discrete {#alg-discrete}

**What it does.** Searches only the values the variables are allowed to take — integers, steps or
preferred values — and never anything between them. A grid of up to 2,000 combinations is searched
**exhaustively**; a larger one by coordinate descent: each variable alone is moved 1, 2, 4 and 8
allowed values either side of the best point, keeping any improvement, with 10 random restarts to get
out of a poor corner.

**Use it** when **every** variable is discrete — finger counts, a set of standard capacitor values —
and you want the best buildable combination rather than a continuous answer rounded afterwards.

**Avoid it** when any variable is continuous (it refuses to start, naming them): use another method
and then **Snap and polish**. And keep the grid small: the number of combinations is the product of
every variable's count of allowed values.

## Settings {#settings}

**⚙** in the panel:

| Setting | |
|---|---|
| **Max iterations** | Default 100. An iteration is one step of the algorithm, which may be many simulations. |
| **Max evaluations** | A limit on simulations. Empty: no limit. |
| **Time limit** | Empty: no limit. |
| **Cost** | Least squares, or Minimax (worst violation). |
| **Each evaluation runs** | *The goals' analyses only* (the default) or *every enabled analysis*. |
| **Corners** | Meet every goal at these [corners](yield.html#corners) too: `all`, or names separated by commas. Empty: the nominal only. |
| **Include the nominal** | With corners, whether the design as it stands is one of the points as well. On by default. |
| **Parallel evaluations** | How many points of one step are simulated at once. Default: one fewer than the machine's cores. |
| **Seed** | Fixes the random choices of the algorithms that make them, so a run can be repeated exactly. |
| **Advanced** | The chosen algorithm's own options. |

A run also stops when the best cost has not improved for 25 iterations.

### Optimizing across corners {#corners}

With **Corners** set, every point the optimizer tries is simulated once at each corner (and at the
nominal), and a goal is met only when it is met at all of them. The status line says how many
simulations one point costs. Each goal row then shows, after **@**, the corner that binds it — where it
is worst. In the `.cnl` it is `corners=` on the optimize line: `optimize algorithm=lm corners=all`, with
`nominal=0` to leave the nominal out.

Meeting a goal at a corner is meeting it with that corner's values at their nominal. Any tolerance on
top of that is a [yield](yield.html#corners) question: a goal met at its worst corner with no margin
fails at that corner for about half of the parts built. In the *Yield, Corners and Centering* example
the amplifier's gain goal, met with no margin hot at low supply, has a yield of 43 % there.

## Running, pausing, and reading the progress {#running}

**▶ Run** checks the setup first; if it cannot start — nothing ticked, no enabled goal, a goal that
cannot be scored at the starting values — the status line says why. While it runs:

- the header shows the iteration, the simulations so far and the time taken, and for **Auto** which
  stage it is in;
- the **sparkline** draws the best cost after each iteration, on a log scale;
- *Goals met 1/2* counts the goals met at the best point, and each goal and variable row updates;
- every open Data Display follows the best point, with a small *Optimizing* chip. When each
  evaluation runs only the goals' analyses, plots of the other analyses keep their old results and are
  drawn dimmed until the run ends.

**⏸ Pause** finishes the simulations in flight, then holds; press it again to resume. A paused run
continues exactly as if it had never paused. **■ Stop** ends the run and keeps its best point. With a
standard-value variable, Stop still snaps the best point onto its series. Under an algorithm that moves
it continuously, a paused best between two standard values cannot be locked in, pushed or sent to
Tuning until Stop has snapped it.

When the run ends, the best point is simulated once more with every enabled analysis and saved as the
schematic's results, so every plot is current.

**A simulation that fails** — a harmonic balance that does not converge, say — counts as worse than
every point that succeeded, and the run carries on. The header counts them.

## Railed variables {#railed}

A variable whose best value ends within half a percent of the edge of its range is **railed**: its row
is marked ⚠ at that end, with **Widen** beside it. It means the optimizer wanted to go further than the
range allows, so the goals may be reachable with a wider range.

**Widen** doubles the range on that side — by width on a linear range, by ratio on a log one — as one
undo step. Then run again. A railed variable is a hint, never a reason to stop: the run finishes either
way, and the range is yours.

## Complex values {#complex}

A complex value is optimized by its parts, as it is [tuned by them](tuning.html#complex). Tick the
parts the optimizer may move; each is one variable with its own range.

- **At most two parts of one value** may be optimized — a complex number has two degrees of freedom.
  Ticking a third is refused when you run, naming them. Parts you do not tick still limit it with their
  ranges.
- **One part** moves with its partner held at the starting value. **Real and imaginary**, or
  **magnitude and phase**, set the number directly. **A mixed pair** — real and magnitude, say — is
  solved for the number they describe.
- **A point that no complex number satisfies** — a real part larger than the magnitude — or that falls
  outside the range of any of that value's rows is **infeasible**: it is not simulated, it ranks behind
  every point that was, and the header counts it.

The best value is reported, pushed and locked in **whole**, in the form the schematic wrote it.

## Integers, steps and preferred values: snap and polish {#snap}

A variable can take only some values: an **integer** parameter (a finger count), a value with a
**step** (⋮ ▸ Step… in the Tuning panel), or a value restricted to **standard values**, chosen with
⋮ ▸ Standard values on its row in either panel. The variable list shows the choice beside the name.

- **E6, E12, E24, E48, E96** — that IEC 60063 series inside the variable's range, for any positive
  value. Written `discrete=e24` on its `tune` line.
- **Preferences ladder** — the value's own list from the Smith Chart's **Preferred Values** dialog:
  capacitors for F, inductors for H, resistors for Ω. Written `discrete=preferred`.

**Standard values are searched on their series** by every algorithm that needs no derivatives —
Simplex, Pattern search, Random, Differential evolution, Particle swarm, CMA-ES and Bayesian, and the
first stage of **Auto**: every point they simulate, and every value the rows show while they run, is on
the series. Two points that land on the same values are simulated once.

Gradient (Levenberg–Marquardt), Quasi-Newton, Minimax and the Trust-region model — and Auto's second
stage — move such a value continuously, because they need a smooth response to follow. **Snap and
polish** then:

1. tries the allowed values either side of where each one ended — every combination, for up to six of
   them; beyond six, the nearest — and keeps the best;
2. holds those values and optimizes the continuous ones again from there.

From then on the best point is the snapped one, because the continuous one is not a design anyone can
build. Every run with a standard-value variable ends with a snap and polish, whichever algorithm it
used and however it ended — a Stop included, which snaps without the polish — so its result is always
on the series; **Auto** does the same for integers and steps too. The toolbar button runs it on a
finished run — to polish after a Stop, or for an integer or a step under another algorithm. **Discrete** searches the allowed values directly when every
variable has them. A part of a complex value is always continuous.

**A vendor part** is a variable whose values are real parts, not numbers; see
[Letting the optimizer choose the part](pdk-integration.html#vendor-optimize). Its row shows how many
candidate parts it has and their value span, and its value is the chosen part number. A run of vendor
parts alone uses **Discrete**, and the continuous methods are greyed in the menu. With other variables
too, an ideal component stands in for each part while the values move continuously (the row reads
`ideal 2.3 pF`), and the snap then tries the real parts either side. The status line reports both
costs. At the end of the candidate list the row is marked railed. **Widen** removes the value bound on
that side, and is not offered when the part is at the end of what the library holds.

**Sensitivity** (in the toolbar) simulates once more per variable around the best point and shows, on
each row, how much of the cost that variable moves — which variables matter, and which could be left
alone. Before any run, the same button opens the [Sensitivity](sensitivity.html) panel, which ranks
every value of the design against each goal at the values as they stand — a good way to choose which
variables to optimize.

## Keeping the result {#keeping}

While paused or after a run:

- **Lock in** keeps the best values as a [preset](tuning.html#presets), recording the cost.
- **Push** writes them into the schematic — one undo step per document, inside sub-cells too.
- **Send to Tuning** loads them into the Tuning panel's sliders, to explore around the answer by hand.

All three use the digits chosen with the button at the right of the header — the same setting as the
[Tuning panel's](tuning.html#rows) — so what is kept is what the variable list shows.

**Reset** (beside Stop) clears the result and puts back every value this panel pushed, as one undo
step. The next run then starts from the design as drawn rather than from the last run's answer, which
is how to compare two algorithms from the same start. A run always starts from the schematic's values,
so after a Push without a Reset the next run starts where the last one ended. If that point already
meets every goal, it stops at once.

## From the command line, and from an agent {#headless}

`circuitrf opt <schematic.csch>` runs the schematic's saved setup headless and reports the same best
values, cost and simulation count the panel does; see
{{anchor: cli#opt|Optimizing from the command line}}. An AI agent runs it as `run analysis=optimize`
over [MCP](ai-agents.html), and learns how to write goals from `reference goals`, `reference tuning`
and `reference optimizers`. Neither changes the design's values. `--save-preset` is the one thing the
command line writes: the best point, as a preset in the schematic.

<p class="small">See also: <a href="tuning.html">Tuning</a> ·
  <a href="derived-metrics.html">Derived metrics</a> (μ, μ′, K, group delay and VSWR) ·
  <a href="measurements.html">Measurements</a> · <a href="expressions.html">Expressions</a> ·
  <a href="cli.html#opt">The command line</a>.</p>
