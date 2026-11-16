#!/usr/bin/env python3
"""
ici_reference.py - the prediction brief PCH-7's gate 6 holds circuitRF's envelope run to.

An OFDM symbol through a flat channel whose gain h(t) turns during the symbol: the FFT of the received useful part
y[n] = h(t_n) x[n] gives, at subcarrier k, Y_k = sum_l X_l C_(k-l) with C_m the m-th DFT coefficient of h over the
window. A genie that divides by the channel at the window's middle, h_c, leaves at each subcarrier

    e_k = (C_0 - h_c)/h_c X_k + sum_(l != k) C_(k-l) X_l / h_c

and with unit-power independent data its mean power is, by Parseval,

    E = ( var_W(h) + |mean_W(h) - h_c|^2 ) / |h_c|^2

var_W and mean_W over the useful part W = [t_c - T_u/2, t_c + T_u/2], T_u = 1/SCS. EVM^2 is the mean of E over the
symbols measured. None of this reads circuitRF's demodulator, equalizer or envelope engine.

The drop's gain h(t) is the generator's DEFINITION (src/RfCore/Propagation/Fading.cs header, design note 4.3, 8.1),
written again here from that text: a classical spectrum of N sinusoids, f_k = f_D cos(2 pi (k + u)/N) with u one
uniform draw, phases phi_k uniform draws, h = (1/sqrt N) sum_k exp(j(2 pi f_k t + phi_k)); every draw
Hash(Seed, Drop, stream, k) of the yield engine (SplitMix64's mix over FNV-1a stream ids), the streams named
"<instance>:tap1:angle" and "<instance>:tap1:phase".

Output: ici_reference.json - for each Doppler, E(t) on a 2 us grid over the first 1.2 ms of channel time; the
test reads E at each measured symbol's middle. The seed is the first whose |h| stays above 0.4 over the window at the
fastest Doppler, so no deep fade makes the mean of E hang on one symbol. Standard library only.
"""
import cmath
import json
import math
import os

M64 = (1 << 64) - 1
GOLDEN = 0x9E3779B97F4A7C15


def mix(z):
    z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & M64
    z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & M64
    return z ^ (z >> 31)


def stream_id(text):
    h = 0xCBF29CE484222325
    for b in text.encode("utf-8"):
        h ^= b
        h = (h * 0x100000001B3) & M64
    return mix((h + GOLDEN) & M64)


def draw(seed, trial, stream, k):
    h = mix((seed + GOLDEN) & M64)
    h = mix(h ^ ((trial + 2 * GOLDEN) & M64))
    h = mix(h ^ ((stream + 3 * GOLDEN) & M64))
    return mix(h ^ (((k & 0xFFFFFFFF) + 4 * GOLDEN) & M64))


def uniform(seed, trial, stream, k):
    return ((draw(seed, trial, stream, k) >> 11) + 0.5) / float(1 << 53)


def gain_fn(seed, drop, instance, fd, n):
    u = uniform(seed, drop, stream_id(instance + ":tap1:angle"), 0)
    ph = stream_id(instance + ":tap1:phase")
    w = [2 * math.pi * fd * math.cos(2 * math.pi * (k + u) / n) for k in range(n)]
    phi = [2 * math.pi * uniform(seed, drop, ph, k) for k in range(n)]
    s = 1 / math.sqrt(n)
    return lambda t: s * sum(cmath.exp(1j * (w[k] * t + phi[k])) for k in range(n))


def error_power(h, tc, tu, samples=128):
    win = [h(tc + tu * ((i + 0.5) / samples - 0.5)) for i in range(samples)]
    mean = sum(win) / samples
    var = sum(abs(v - mean) ** 2 for v in win) / samples
    hc = h(tc)
    return (var + abs(mean - hc) ** 2) / abs(hc) ** 2


def main():
    instance, drop, n, scs = "CH1", 1, 64, 30e3
    dopplers = [300.0, 600.0, 1200.0]                    # f_D / SCS = 0.01, 0.02, 0.04
    span, dt = 1.2e-3, 2e-6
    seed = 1
    while True:
        h = gain_fn(seed, drop, instance, max(dopplers), n)
        if min(abs(h(i * 5e-6)) for i in range(int(1.0e-3 / 5e-6) + 1)) >= 0.4:
            break
        seed += 1
    out = {"script": "ici_reference.py", "instance": instance, "seed": seed, "drop": drop, "sinusoids": n, "scs_hz": scs,
           "t0_s": 0.0, "dt_s": dt, "dopplers": []}
    tu = 1 / scs
    for fd in dopplers:
        h = gain_fn(seed, drop, instance, fd, n)
        e = [float("%.6g" % error_power(h, i * dt, tu)) for i in range(int(span / dt) + 1)]
        out["dopplers"].append({"fd_hz": fd, "error_power": e})
        print("f_D %5.0f Hz: E from %.3g to %.3g" % (fd, min(e), max(e)))
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ici_reference.json")
    with open(path, "w") as f:
        json.dump(out, f, separators=(",", ":"))
    print("seed", seed, "->", path)


if __name__ == "__main__":
    main()
