#!/usr/bin/env python3
"""
array_link_reference.py - the predictions brief PCH-8's gates 5 and 6 hold circuitRF's array-ended Propagation Channel to.

Input: array_link_rays.json, written by the gate test (CRF_WRITE_ARRAY_LINK=1) from the channel's own realization: for each case
the carrier, the ends' distance, the transmit power, the receive array's lattice (uniform weights, Feed mode, no channel cell, an
isolated element: one pattern on every element, no coupling), and per ray its power share, its gain phase, its excess delay, its
2x2 polarization matrix M, the transmit end's realized-gain field toward it F_tx and the element's realized-gain field from it
F_el (both in the global (theta, phi) of the ray's direction), and its arrival (theta, phi) in the array's own frame.

What this computes, from those and nothing of circuitRF's: the wave each element receives (PAA-7's eq. (4) through the channel)

    c_n = j * lambda/(4 pi d) * sum_r sqrt(P_r) e^{j phi_r} e^{-j 2 pi f tau_r} (F_el,r . M_r F_tx,r) e^{j k (u_r x_n + v_r y_n)} * a

with a the transmit pin's incident wave (1/2|a|^2 = the available power), the element positions from the lattice (row-major from
the top left, centred), and the matched, isolating beamformer's output at the feed, b_F = sum_n c_n / sqrt(N); the power the feed
delivers is 1/2|b_F|^2. Output: array_link_reference.json, each case's feed power in dBm. Standard library only.
"""
import cmath
import json
import math
import os

C = 299792458.0


def feed_power_dbm(case):
    f, d = case["freq_hz"], case["distance_m"]
    lam, k = C / f, 2 * math.pi * f / C
    a = math.sqrt(2 * 10 ** ((case["pavl_dbm"] - 30) / 10))
    rows, cols, dx, dy = case["rows"], case["cols"], case["dx_m"], case["dy_m"]
    pos = [((c - (cols - 1) / 2) * dx, ((rows - 1) / 2 - r) * dy) for r in range(rows) for c in range(cols)]
    n = len(pos)
    bf = 0j
    for ray in case["rays"]:
        m = [complex(ray["m"][2 * i], ray["m"][2 * i + 1]) for i in range(4)]
        ft = (complex(ray["tx"][0], ray["tx"][1]), complex(ray["tx"][2], ray["tx"][3]))
        fe = (complex(ray["rx"][0], ray["rx"][1]), complex(ray["rx"][2], ray["rx"][3]))
        mf = (m[0] * ft[0] + m[1] * ft[1], m[2] * ft[0] + m[3] * ft[1])
        coupling = fe[0] * mf[0] + fe[1] * mf[1]
        amp = math.sqrt(ray["power"]) * cmath.exp(1j * ray["phase_rad"]) * cmath.exp(-2j * math.pi * f * ray["delay_s"])
        th, ph = ray["theta_rad"], ray["phi_rad"]
        u, v = math.sin(th) * math.cos(ph), math.sin(th) * math.sin(ph)
        af = sum(cmath.exp(1j * k * (u * x + v * y)) for x, y in pos) / math.sqrt(n)
        bf += 1j * lam / (4 * math.pi * d) * amp * coupling * af * a
    return 10 * math.log10(0.5 * abs(bf) ** 2 * 1000)


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    with open(os.path.join(here, "array_link_rays.json")) as fh:
        cases = json.load(fh)["cases"]
    out = {"script": "array_link_reference.py", "feed_power_dbm": {}}
    for name, case in cases.items():
        p = feed_power_dbm(case)
        out["feed_power_dbm"][name] = p
        print("%-12s %3d rays: feed %.6f dBm" % (name, len(case["rays"]), p))
    with open(os.path.join(here, "array_link_reference.json"), "w") as fh:
        json.dump(out, fh, indent=1)


if __name__ == "__main__":
    main()
