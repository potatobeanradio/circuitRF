---
title: Pins, Ports & Terms
slug: reference/pins-ports-terms.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Pins, Ports &amp; Terms
lede: Three concepts that sound interchangeable but aren't. Getting them right is the difference between a circuit that simulates what you intended and one that doesn't.
---

## The one-paragraph version {#summary}

A **Port** is the abstract idea of an external connection. circuitRF realizes a port two different
ways depending on context: inside a reusable **cell**, a port is a **Pin** (a connectivity-only
interface terminal); on an S-parameter **test bench**, a port is a **Term** (a numbered,
reference-impedance termination the simulator excites and measures). So: *Pin = a cell's
connection point; Term = an S-parameter measurement port; Port = the general concept either one
realizes.*

<table>
    <thead><tr><th></th><th>Pin</th><th>Term</th></tr></thead>
    <tbody>
      <tr><td><strong>Role</strong></td><td>Cell interface terminal</td><td>S-parameter port termination</td></tr>
      <tr><td><strong>Electrical model</strong></td><td>None (connectivity only)</td><td>Reference impedance + excitation/measurement</td></tr>
      <tr><td><strong>Lives on</strong></td><td>A cell's symbol</td><td>A test-bench schematic</td></tr>
      <tr><td><strong>Key parameter</strong></td><td><code>Num</code> (+ optional <code>Name</code>, polarity)</td><td><code>Num</code> + <code>Z</code> (reference impedance)</td></tr>
      <tr><td><strong>Use it to…</strong></td><td>Expose a reusable cell's connections to its parent</td><td>Define where S-parameters are measured, and the port impedance</td></tr>
    </tbody>
  </table>

## Pin — the cell interface terminal {#pin}

<figure class="symbol"><span class="frame">
    <img class="sym-light" src="../assets/symbols/pin.svg" alt="Pin symbol">
    <img class="sym-dark"  src="../assets/symbols/pin-dark.svg" alt="">
  </span><figcaption>Pin</figcaption></figure>

A Pin marks a point where a **cell** connects to the world outside it. When you build a reusable
cell (say, an amplifier), you place Pins where its gate-in, drain-out, and bias connections should
be; those Pins become the cell's **symbol pins**, and the parent schematic wires to them. A Pin
carries no electrical model — it is pure connectivity. Its `Num` orders the cell's ports; `Name`
labels them; an optional `Plus`/`Minus` polarity pairs two Pins into one differential port.

<div class="callout note">
    <span class="label">Why "no electrical model" matters</span>
    <p>A Pin does not source, load, or terminate anything — it just says "this net is exposed." If you want a
    50 Ω measurement port, a Pin alone won't give you one; that's a Term's job.</p>
  </div>

## Term — the S-parameter port {#term}

<figure class="symbol"><span class="frame">
    <img class="sym-light" src="../assets/symbols/term.svg" alt="Term symbol">
    <img class="sym-dark"  src="../assets/symbols/term-dark.svg" alt="">
  </span><figcaption>Term</figcaption></figure>

A Term is an active S-parameter port: it presents a reference impedance (`Z`, default 50 Ω) and is
the point at which an S-parameter analysis injects a wave and measures the scattered result. Place
two Terms on a network and run S-parameters to get the 2-port S-matrix; their `Num` values
(auto-assigned 1, 2, …) become the port indices you read as `S(2,1)`, etc. The
[P1Tone](components.html#p1tone) source can also act as a numbered port for power-driven work.

## A note on Ground {#ground}

<figure class="symbol"><span class="frame">
    <img class="sym-light" src="../assets/symbols/ground.svg" alt="Ground symbol">
    <img class="sym-dark"  src="../assets/symbols/ground-dark.svg" alt="">
  </span><figcaption>Ground</figcaption></figure>

Ground (net `0`) is the global reference — not a port at all. A Term's impedance is referenced to
it; a Pin may connect to it like any net. Every port voltage is ultimately measured against ground
unless a component declares its own floating reference (see the SnP floating-reference option in
[Dynamic symbols](dynamic-symbols.html#snp)).

## Choosing the right one {#choosing}

- Building a reusable cell and need to expose its connections? → **Pin**.

- Setting up an S-parameter measurement on a test bench? → **Term** (one per port).

- Driving a power amplifier and also want it to be a numbered port? → **P1Tone** with a `Num`.

- Just need a reference node? → **Ground**.

---

<p class="small">See also: <a href="components.html#pin">Components › Pin</a> /
  <a href="components.html#term">Term</a> · <a href="dynamic-symbols.html">Dynamic symbols</a> ·
  <a href="../new-user-guide/index.html#ppt">New User's Guide</a> for a gentler take.</p>
