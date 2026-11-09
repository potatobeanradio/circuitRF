#!/usr/bin/env python3
"""Reference data for the S-parameter noise analysis (testdata/noise/).

Synthetic circuits only. Every expected value comes from outside circuitRF: closed
forms, a small numpy nodal solver (noise by the adjoint method), scikit-rf and
ngspice. Needs numpy, scikit-rf (2.1.0 used) and ngspice (47 used) on PATH:

    python3 gen_noise_refs.py     # rewrites *.cnl, *.s2p and refs.json next to this file

Conventions: single-sided absolute spectral densities; Cs = <b b^H> of the outgoing
power waves with every port terminated in its own noiseless Z0 (W/Hz); a passive part
at temperature T has Cs = kT(I - S S^H), C_Y = 2kT(Y + Y^H); NF at T0 = 290 K with
the source at port 1's Z0, F = 1 + Cs22/(k T0 |S21|^2); Gamma_opt is referenced to
port 1's Z0; k = 1.380649e-23 J/K exactly.

Benches (keys of refs.json "benches"):
  pad6_290K, pad6_400K   matched 6 dB resistive pi pad (ambient 290 K / 400 K):
                         F = 1 + (T/T0)(L-1), Cs = kT(I-SS^H). Closed form; numpy nodal agrees.
  lossy_lc               unmatched R/L/C 2-port at the default 300 K ambient: Bosma Cs from
                         nodal S, equal to the adjoint nodal Cs; F = 1 + (T/T0)(1/G_A - 1).
  friis_matched          amp1.s2p - 3 dB pi pad (290 K) - amp2.s2p, all matched, Gamma_opt = 0:
                         F = F1 + (L-1)/G1 + (F2-1)L/G1. Closed form; numpy chain (C_A)
                         cascade; scikit-rf connect.
  friis_mismatched       ampA.s2p - lossy R/C interstage (290 K) - ampB.s2p, all mismatched:
                         scikit-rf connect with the interstage's Bosma C_A attached; numpy
                         nodal with the blocks' C_Y stamped.
  touchstone_r50, touchstone_r25, touchstone_r25_in_50
                         SnP noise block in -> NFmin/Rn/Gamma_opt out; the R 25 file between
                         50 ohm ports re-references Gamma_opt to 50 ohm. File values; scikit-rf.
  pozar_ex12_5           noise circle of Pozar, Microwave Engineering 4th ed., Ex. 12.5.
                         Closed form; textbook; scikit-rf nf_circle.
  ngspice_mixed_temp, ngspice_coupled_l, ngspice_vccs_amp
                         resistors at mixed temperatures, K-coupled lossy inductors, a VCCS
                         amplifier: ngspice .sp donoise against numpy nodal.
  osdi_crf_noise         the fake OSDI part crf_noise (conductance g, PSD 4kTg + 2q|g v|) as a
                         shunt at a node DC-fed through a choke, between two 50 ohm ports:
                         F = 1 + (T/T0) g Z0 + 2q|g Vdc| Z0/(4 k T0). Closed form; no .cnl.
"""
import json
import os
import re
import shutil
import subprocess
import tempfile

import numpy as np
import skrf as rf
from skrf.network import connect

K = 1.380649e-23          # J/K, SI exact
Q = 1.602176634e-19       # C, SI exact
T0 = 290.0
TDEF = 300.0             # circuitRF default ambient, 26.85 C
K_NGSPICE = 1.38064852e-23  # CODATA 2014, what ngspice 47 and scikit-rf 2.1.0 use
HERE = os.path.dirname(os.path.abspath(__file__))
TOL = {"db_abs": 1e-9, "rn_rel": 1e-9, "gamma_abs": 1e-9, "cs_rel": 1e-9}


def num(x):
    return repr(float(x))


def C2K(c):
    return c + 273.15


def K2C(t):
    return round(t - 273.15, 10)


# ---- circuit description: one list of parts drives numpy, ngspice and the .cnl ----------
# Values in the units the .cnl writes: R ohm, L nH, C pF, G mS. T in kelvin (None = ambient).

def R(name, a, b, r, T=None): return dict(t="R", name=name, n=(a, b), v=r, T=T)
def L(name, a, b, l_nh): return dict(t="L", name=name, n=(a, b), v=l_nh)
def C(name, a, b, c_pf): return dict(t="C", name=name, n=(a, b), v=c_pf)
def Kc(name, l1, l2, k): return dict(t="K", name=name, n=(), l1=l1, l2=l2, v=k)
def G(name, op, om, cp, cm, g_ms): return dict(t="G", name=name, n=(op, om, cp, cm), v=g_ms)
def NP(name, a, b, net): return dict(t="NP", name=name, n=(a, b), net=net)  # net: skrf Network with noise


def nodal(elems, ports, T_amb, f):
    """S and Cs (absolute, W/Hz) of the terminated network; Cs by the adjoint method:
    one transposed solve per port gives that port's row of Y^-1, and every noise current
    (4kT/R per resistor, C_Y per data block) is carried to the outgoing waves through it."""
    nodes = sorted(({n for e in elems for n in e["n"]} | {p for p, _ in ports}) - {"0"})
    ix = {n: i for i, n in enumerate(nodes)}
    N, P = len(nodes), len(ports)
    z0 = np.array([z for _, z in ports], float)

    def inc(*nn):  # incidence columns, ground dropped
        a = np.zeros((N, len(nn)))
        for j, n in enumerate(nn):
            if n != "0":
                a[ix[n], j] = 1.0
        return a

    Ls = [e for e in elems if e["t"] == "L"]
    Lm = np.diag([e["v"] * 1e-9 for e in Ls])
    li = {e["name"]: i for i, e in enumerate(Ls)}
    for e in elems:
        if e["t"] == "K":
            i, j = li[e["l1"]], li[e["l2"]]
            Lm[i, j] = Lm[j, i] = e["v"] * np.sqrt(Lm[i, i] * Lm[j, j])
    AL = sum((inc(e["n"][0])[:, [0]] - inc(e["n"][1])[:, [0]]) * np.eye(len(Ls))[i]
             for i, e in enumerate(Ls)) if Ls else None
    S = np.zeros((len(f), P, P), complex)
    Cs = np.zeros_like(S)
    for fi, fr in enumerate(f):
        w = 2 * np.pi * fr
        Y = np.zeros((N, N), complex)
        src = []  # (A: N x m incidence, C: m x m current PSD)
        for e in elems:
            if e["t"] in ("R", "C"):
                a = inc(e["n"][0]) - inc(e["n"][1])
                y = 1 / e["v"] if e["t"] == "R" else 1j * w * e["v"] * 1e-12
                Y += y * (a @ a.T)
                if e["t"] == "R":
                    src.append((a, np.array([[4 * K * (e["T"] or T_amb) / e["v"]]])))
            elif e["t"] == "G":
                o = inc(e["n"][0]) - inc(e["n"][1])
                c = inc(e["n"][2]) - inc(e["n"][3])
                Y += e["v"] * 1e-3 * (o @ c.T)
            elif e["t"] == "NP":
                a = inc(*e["n"])
                net = e["net"]
                Y += a @ block_y(net)[fi] @ a.T
                src.append((a, block_cy(net)[fi]))
        if Ls:
            Y += AL @ np.linalg.inv(1j * w * Lm) @ AL.T
        pi = [ix[p] for p, _ in ports]
        for k, z in zip(pi, z0):
            Y[k, k] += 1 / z
        rows = np.array([np.linalg.solve(Y.T, np.eye(N)[k]) for k in pi])  # rows of Y^-1
        S[fi] = 2 * rows[:, pi] / np.sqrt(np.outer(z0, z0)) - np.eye(P)
        for a, c in src:
            h = (rows @ a) / np.sqrt(z0)[:, None]
            Cs[fi] += h @ c @ h.conj().T
    return S, Cs


# ---- two-port noise algebra ---------------------------------------------------------------

def H(m):
    return np.conj(np.swapaxes(m, -1, -2))


def s2y(s, z0):
    rih = np.diag(1 / np.sqrt(z0))
    i = np.eye(s.shape[-1])
    return rih @ (i - s) @ np.linalg.inv(i + s) @ rih


def y2s(y, z0):
    rh = np.diag(np.sqrt(z0))
    i = np.eye(y.shape[-1])
    return np.linalg.solve(i + rh @ y @ rh, i - rh @ y @ rh)


def cs_to_cy(s, cs, z0):
    """C_Y = (I + Y R) R^-1/2 Cs R^-1/2 (I + Y R)^H, R = diag(Z0)."""
    y = s2y(s, z0)
    m = (np.eye(2) + y @ np.diag(z0)) @ np.diag(1 / np.sqrt(z0))
    return m @ cs @ H(m)


def cy_to_cs(s, cy, z0):
    y = s2y(s, z0)
    m = (np.eye(2) + y @ np.diag(z0)) @ np.diag(1 / np.sqrt(z0))
    mi = np.linalg.inv(m)
    return mi @ cy @ H(mi)


def ty(y):  # C_A = T C_Y T^H
    t = np.zeros(y.shape, complex)
    t[..., 0, 1] = -1 / y[..., 1, 0]
    t[..., 1, 0] = 1
    t[..., 1, 1] = -y[..., 0, 0] / y[..., 1, 0]
    return t


def ca_from_params(nfmin_db, gopt, rn_ohm, zref):
    """Absolute chain correlation matrix from noise parameters (Gamma_opt referenced to zref)."""
    fmin = 10 ** (np.asarray(nfmin_db) / 10)
    yopt = (1 - gopt) / (zref * (1 + gopt))
    ca = np.empty(np.shape(fmin) + (2, 2), complex)
    ca[..., 0, 0] = rn_ohm
    ca[..., 0, 1] = (fmin - 1) / 2 - rn_ohm * np.conj(yopt)
    ca[..., 1, 0] = (fmin - 1) / 2 - rn_ohm * yopt
    ca[..., 1, 1] = rn_ohm * abs(yopt) ** 2
    return 4 * K * T0 * ca


def params_from_ca(ca, z1):
    c11, c22, c12 = ca[..., 0, 0].real, ca[..., 1, 1].real, ca[..., 0, 1]
    gopt = np.sqrt(np.maximum(c22 / c11 - (c12.imag / c11) ** 2, 0.0))
    yopt = gopt + 1j * c12.imag / c11
    fmin = 1 + (c12.real + c11 * gopt) / (2 * K * T0)
    return dict(nfmin_db=10 * np.log10(fmin), rn_ohm=c11 / (4 * K * T0),
                gamma_opt=(1 - z1 * yopt) / (1 + z1 * yopt))


def f_at(ca, ys):
    c11, c22, c12 = ca[..., 0, 0].real, ca[..., 1, 1].real, ca[..., 0, 1]
    return 1 + (c11 * abs(ys) ** 2 + 2 * np.real(ys * c12) + c22) / (4 * K * T0 * np.real(ys))


def two_port(s, cs, z0):
    """NF (source = port 1 at its Z0), NFmin, Rn, Gamma_opt from S and Cs."""
    ca = ty(s2y(s, z0)) @ cs_to_cy(s, cs, z0) @ H(ty(s2y(s, z0)))
    out = params_from_ca(ca, z0[0])
    out["nf_db"] = 10 * np.log10(1 + cs[:, 1, 1].real / (K * T0 * abs(s[:, 1, 0]) ** 2))
    # the same NF through the chain matrix: an independent formula, must agree
    assert np.allclose(out["nf_db"], 10 * np.log10(f_at(ca, 1 / z0[0])), rtol=0, atol=1e-11)
    return out


def bosma(s, T):
    return K * T * (np.eye(s.shape[-1]) - s @ H(s))


# ---- data blocks (Touchstone 1.1 with a noise block) ----------------------------------------

def block_y(net):
    return s2y(net.s, np.full(2, net.z0[0, 0].real))


def block_cy(net):
    """C_Y of a data block from its C_A (absolute, exact k)."""
    y = block_y(net)
    t = np.zeros(y.shape, complex)
    t[..., 0, 0], t[..., 0, 1], t[..., 1, 0] = -y[..., 0, 0], 1, -y[..., 1, 0]
    return t @ net.ca @ H(t)


def g15(x):
    """15 significant digits: readable (no -44.99999999999999) and far below every tolerance."""
    return f"{float(x):.15g}"


def write_s2p(name, f_ghz, s, z0, nfmin_db, gopt, rn_norm, comment):
    lines = [f"! {l}" for l in comment.splitlines()] + [f"# GHz S MA R {z0:g}"]
    for i, fg in enumerate(f_ghz):
        vals = [s[i, 0, 0], s[i, 1, 0], s[i, 0, 1], s[i, 1, 1]]
        lines.append(" ".join([g15(fg)] + [f"{g15(abs(v))} {g15(np.degrees(np.angle(v)))}" for v in vals]))
    lines.append("! noise: f  NFmin(dB)  |Gamma_opt|  angle(Gamma_opt) deg  Rn/R")
    for i, fg in enumerate(f_ghz):
        lines.append(" ".join(g15(x) for x in
                              (fg, nfmin_db[i], abs(gopt[i]), np.degrees(np.angle(gopt[i])), rn_norm[i])))
    with open(os.path.join(HERE, name), "w") as fh:
        fh.write("\n".join(lines) + "\n")
    return read_s2p(name)


def read_s2p(name):
    """scikit-rf reads it back: S, and noise as set_noise_a builds it (Gamma_opt and rn
    referenced to the option-line R). The exact-k C_A used here is rebuilt from the parsed
    parameters, so scikit-rf's own k (CODATA 2014) never enters an expected value."""
    net = rf.Network(os.path.join(HERE, name))
    z = net.z0[0, 0].real
    nfmin = net.nfmin_db
    net.ca = ca_from_params(nfmin, net.g_opt, net.rn, z)
    assert net.noise_freq == net.frequency
    return net


def mag_ang(m, a_deg):
    return m * np.exp(1j * np.radians(a_deg))


def check_realizable(nfmin_db, gopt, rn_ohm, zref):
    gopt_s = np.real((1 - gopt) / (zref * (1 + gopt)))
    assert np.all(10 ** (np.asarray(nfmin_db) / 10) - 1 <= 4 * rn_ohm * gopt_s), "not realizable"


# ---- writers -------------------------------------------------------------------------------

def write_cnl(name, title, ports, elems, T_amb, f_ghz):
    """A bench without the noise key; the tests add Noise=yes to the sparam line."""
    out = [f"; {l}" for l in title.splitlines()]
    if T_amb != TDEF:
        out.append(f"temp = {num(K2C(T_amb))}")
    for i, (n, z) in enumerate(ports, 1):
        out.append(f"Port:P{i}  {n} 0  Num={i} Z={num(z)} Ohm")
    for e in elems:
        nets = "  ".join(e["n"])
        t = e["t"]
        if t == "R":
            extra = f"  Temp={num(K2C(e['T']))}" if e["T"] else ""
            out.append(f"R:{e['name']}  {nets}  R={num(e['v'])} Ohm{extra}")
        elif t == "L":
            out.append(f"L:{e['name']}  {nets}  L={num(e['v'])} nH")
        elif t == "C":
            out.append(f"C:{e['name']}  {nets}  C={num(e['v'])} pF")
        elif t == "G":
            out.append(f"VCCS:{e['name']}  {nets}  G={num(e['v'])} mS")
        elif t == "K":
            l1 = next(x["v"] for x in elems if x.get("name") == e["l1"])
            l2 = next(x["v"] for x in elems if x.get("name") == e["l2"])
            m_ph = e["v"] * np.sqrt(l1 * l2) * 1e3
            out.append(f"Mutual:{e['name']}  Inductor1=\"{e['l1']}\" Inductor2=\"{e['l2']}\" "
                       f"M={num(float(m_ph))} pH   ; k = {num(e['v'])}")
        elif t == "NP":
            out.append(f"SnP:{e['name']}  {nets}  NumPorts=2 File=\"{e['file']}\"")
    n = len(f_ghz)
    out.append(f"analysis SP1 type=sparam start={num(f_ghz[0])} stop={num(f_ghz[-1])} npts={n} Unit=GHz")
    with open(os.path.join(HERE, name), "w") as fh:
        fh.write("\n".join(out) + "\n")


def ngspice(title, ports, elems, T_amb, f_hz):
    """Run an ngspice .sp with donoise; return its vectors (complex arrays)."""
    if shutil.which("ngspice") is None:
        raise SystemExit("ngspice not on PATH")
    d = [f"* {title}", f".options temp={num(K2C(T_amb))}"]
    for i, (n, z) in enumerate(ports, 1):
        d.append(f"Vp{i} {n} 0 dc 0 ac {1 if i == 1 else 0} portnum {i} z0 {num(z)}")
    for e in elems:
        n = " ".join(e["n"])
        if e["t"] == "R":
            d.append(f"{e['name']} {n} {num(e['v'])}" + (f" temp={num(K2C(e['T']))}" if e["T"] else ""))
        elif e["t"] == "L":
            d.append(f"{e['name']} {n} {num(e['v'])}n")
        elif e["t"] == "C":
            d.append(f"{e['name']} {n} {num(e['v'])}p")
        elif e["t"] == "G":
            d.append(f"{e['name']} {n} {num(e['v'])}m")
        elif e["t"] == "K":
            d.append(f"{e['name']} {e['l1']} {e['l2']} {num(e['v'])}")
    d += [".control", f"sp lin {len(f_hz)} {num(f_hz[0])} {num(f_hz[-1])} 1", "set filetype=ascii",
          "write out.raw all", ".endc", ".end"]
    with tempfile.TemporaryDirectory() as tmp:
        with open(os.path.join(tmp, "deck.cir"), "w") as fh:
            fh.write("\n".join(d) + "\n")
        subprocess.run(["ngspice", "-b", "deck.cir"], cwd=tmp, check=True, capture_output=True)
        txt = open(os.path.join(tmp, "out.raw")).read()
    names = re.findall(r"^\t\d+\t(\S+)\t", txt.split("Values:")[0], re.M)
    nums = [complex(float(a), float(b)) for a, b in re.findall(r"(\S+),(\S+)", txt.split("Values:")[1])]
    v = np.array(nums).reshape(-1, len(names)).T
    out = {nm.lower(): v[i] for i, nm in enumerate(names)}
    assert np.allclose(out["frequency"].real, f_hz, rtol=1e-12), "ngspice grid differs"
    return out


# ---- helpers for refs.json -----------------------------------------------------------------

def lst(a):
    return [float(x) for x in np.ravel(a)] if np.ndim(a) else float(a)


def expected(p, cs=None):
    e = {"nf_db": lst(p["nf_db"]), "nfmin_db": lst(p["nfmin_db"]), "rn_ohm": lst(p["rn_ohm"]),
         "gamma_opt_re": lst(np.real(p["gamma_opt"])), "gamma_opt_im": lst(np.imag(p["gamma_opt"]))}
    if cs is not None:
        e["cs_re"] = np.real(cs).tolist()
        e["cs_im"] = np.imag(cs).tolist()
    return e


def dmax(a, b):
    return float(np.max(np.abs(np.asarray(a) - np.asarray(b))))


def agree(p, q, cs_p=None, cs_q=None):
    """Largest differences between two results, in the units of each quantity."""
    d = {"nf_db": dmax(p["nf_db"], q["nf_db"]), "nfmin_db": dmax(p["nfmin_db"], q["nfmin_db"]),
         "rn_rel": dmax(p["rn_ohm"], q["rn_ohm"]) / float(np.max(np.abs(p["rn_ohm"]))),
         "gamma_opt": dmax(p["gamma_opt"], q["gamma_opt"])}
    if cs_p is not None:
        d["cs_rel"] = dmax(cs_p, cs_q) / float(np.max(np.abs(cs_p)))
    return d


def within(d):
    lim = {"nf_db": TOL["db_abs"], "nfmin_db": TOL["db_abs"], "rn_rel": TOL["rn_rel"],
           "gamma_opt": TOL["gamma_abs"], "cs_rel": TOL["cs_rel"]}
    bad = {k: v for k, v in d.items() if v > lim[k] * 1e-2}
    assert not bad, f"references disagree: {bad}"
    return d


def pi_pad(prefix, a, b, loss_db, z0=50.0, T=None):
    kk = 10 ** (loss_db / 20)
    rsh = z0 * (kk + 1) / (kk - 1)
    rse = z0 * (kk * kk - 1) / (2 * kk)
    return [R(f"{prefix}sh1", a, "0", rsh, T), R(f"{prefix}se", a, b, rse, T), R(f"{prefix}sh2", b, "0", rsh, T)]


def bench(name, ports, elems, T_amb, f_ghz, title):
    write_cnl(f"{name}.cnl", title, ports, elems, T_amb, f_ghz)
    z0 = np.array([z for _, z in ports], float)
    return z0, np.asarray(f_ghz) * 1e9


# ---- the cases -----------------------------------------------------------------------------

def case_pads(refs):
    loss_db = 6.0
    Lin = 10 ** (loss_db / 10)
    f_ghz = [1.0, 2.0, 3.0]
    for T in (290.0, 400.0):
        name = f"pad6_{T:.0f}K"
        ports = [("p1", 50.0), ("p2", 50.0)]
        elems = pi_pad("R", "p1", "p2", loss_db)
        z0, f = bench(name, ports, elems, T, f_ghz,
                      f"Matched 6 dB resistive pi pad, 50 ohm, ambient {T:g} K.\n"
                      f"Expected: F = 1 + (T/T0)(L-1), Cs = kT(1 - 1/L) I.")
        F = 1 + (T / T0) * (Lin - 1)
        s_cf = np.zeros((len(f), 2, 2), complex)
        s_cf[:, 0, 1] = s_cf[:, 1, 0] = 1 / np.sqrt(Lin)
        cs_cf = bosma(s_cf, T)
        p_cf = two_port(s_cf, cs_cf, z0)
        assert np.allclose(p_cf["nf_db"], 10 * np.log10(F), atol=1e-12)
        s_n, cs_n = nodal(elems, ports, T, f)
        p_n = two_port(s_n, cs_n, z0)
        refs[name] = dict(
            netlist=f"{name}.cnl", ambient_K=T, ports_z0_ohm=lst(z0), freq_hz=lst(f),
            description=f"matched 6 dB pi pad at {T:g} K",
            expected=expected(p_cf, cs_cf), tolerance=TOL,
            sources={"nf_db": "closed form F = 1 + (T/T0)(L-1)",
                     "cs": "Bosma kT(I - S S^H), S of the ideal matched pad",
                     "nfmin_db,rn_ohm,gamma_opt": "C_A from the closed-form S and Cs (Fmin = F, Gamma_opt = 0)"},
            agreement={"closed_form_vs_numpy_nodal": within(agree(p_cf, p_n, cs_cf, cs_n))})


def case_lossy_lc(refs):
    name = "lossy_lc"
    T = TDEF  # the .cnl states no temp
    ports = [("p1", 50.0), ("p2", 50.0)]
    elems = [L("L1", "p1", "a", 3.0), R("R1", "a", "b", 15.0), C("C1", "b", "0", 1.2),
             R("R2", "b", "0", 400.0), C("C2", "b", "c", 0.8), R("R3", "c", "p2", 6.0),
             L("L2", "p2", "0", 6.0)]
    f_ghz = [0.5 * i for i in range(1, 11)]
    z0, f = bench(name, ports, elems, T, f_ghz,
                  "Unmatched lossy R/L/C 2-port at the default ambient (300 K).\n"
                  "Expected: Cs = kT(I - S S^H) entry by entry; F = 1 + (T/T0)(1/G_A - 1).")
    s, cs_adj = nodal(elems, ports, T, f)
    cs_b = bosma(s, T)
    p = two_port(s, cs_b, z0)
    ga = abs(s[:, 1, 0]) ** 2 / (1 - abs(s[:, 1, 1]) ** 2)
    F = 1 + (T / T0) * (1 / ga - 1)
    d_cf = dmax(p["nf_db"], 10 * np.log10(F))
    assert d_cf < 1e-11
    refs[name] = dict(
        netlist=f"{name}.cnl", ambient_K=T, ports_z0_ohm=lst(z0), freq_hz=lst(f),
        description="unmatched lossy R/L/C 2-port at the default ambient",
        expected=expected(p, cs_b), tolerance=TOL,
        sources={"cs": "Bosma kT(I - S S^H), S from numpy nodal",
                 "nf_db": "closed form 1 + (T/T0)(1/G_A - 1), G_A = |S21|^2/(1-|S22|^2)",
                 "nfmin_db,rn_ohm,gamma_opt": "C_A from S and Bosma Cs"},
        agreement={"bosma_vs_adjoint_nodal": within(agree(p, two_port(s, cs_adj, z0), cs_b, cs_adj)),
                   "nf_db_closed_form_vs_cs22": d_cf})


def amp_file(name, f_ghz, s, nfmin_db, gopt, rn_norm, z, comment):
    check_realizable(nfmin_db, gopt, rn_norm * z, z)
    net = write_s2p(name, f_ghz, s, z, nfmin_db, gopt, rn_norm, comment)
    net.file = name
    return net


def chain(*blocks):
    """Cascade (ABCD, C_A) pairs: C = C1 + A1 C2 A1^H."""
    a, ca = blocks[0]
    for a2, ca2 in blocks[1:]:
        ca = ca + a @ ca2 @ H(a)
        a = a @ a2
    return a, ca


def abcd(y):
    y11, y12, y21, y22 = y[..., 0, 0], y[..., 0, 1], y[..., 1, 0], y[..., 1, 1]
    a = np.empty(y.shape, complex)
    a[..., 0, 0], a[..., 0, 1] = -y22 / y21, -1 / y21
    a[..., 1, 0], a[..., 1, 1] = -(y11 * y22 - y12 * y21) / y21, -y11 / y21
    return a


def ca_of(s, cs, z0):
    return ty(s2y(s, z0)) @ cs_to_cy(s, cs, z0) @ H(ty(s2y(s, z0)))


def skrf_twoport(s, ca, f, z0=50.0):
    """A scikit-rf Network carrying an exact-k C_A, rescaled to scikit-rf's k so that its
    noise parameters (which divide by its own k) come out exact."""
    n = rf.Network(frequency=rf.Frequency.from_f(f, unit="hz"), s=s, z0=z0)
    n.noise = ca * (K_NGSPICE / K)
    n.noise_freq = n.frequency
    return n


def skrf_params(net, z1=50.0):
    return dict(nf_db=10 * np.log10(net.nf(z1)), nfmin_db=net.nfmin_db, rn_ohm=net.rn, gamma_opt=net.g_opt)


def case_friis(refs):
    T = T0
    zz = np.array([50.0, 50.0])
    # -- matched, unilateral --
    f_ghz = [1.0, 2.0, 3.0, 4.0]
    f = np.array(f_ghz) * 1e9
    n = len(f)
    g1_db, nf1_db, g2_db, nf2_db, pad_db = 15.0, 1.2, 12.0, 2.5, 3.0
    blocks = []
    for name, g_db, nf_db, ph, rn in (("amp1.s2p", g1_db, nf1_db, -45.0, 0.3), ("amp2.s2p", g2_db, nf2_db, 120.0, 0.5)):
        s = np.zeros((n, 2, 2), complex)
        s[:, 1, 0] = mag_ang(10 ** (g_db / 20), ph)
        blocks.append(amp_file(name, f_ghz, s, np.full(n, nf_db), np.zeros(n), np.full(n, rn), 50.0,
                               f"Synthetic matched unilateral amplifier: S11 = S22 = S12 = 0, |S21|^2 = {g_db:g} dB,\n"
                               f"NFmin = {nf_db:g} dB at Gamma_opt = 0, so NF(50 ohm) = NFmin."))
    a1, a2 = blocks
    elems = [NP("X1", "p1", "a", a1), *pi_pad("Rp", "a", "b", pad_db), NP("X2", "b", "p2", a2)]
    for e, fn in ((elems[0], "amp1.s2p"), (elems[-1], "amp2.s2p")):
        e["file"] = fn
    ports = [("p1", 50.0), ("p2", 50.0)]
    write_cnl("friis_matched.cnl", "Friis: amp1.s2p - matched 3 dB pi pad at T0 = 290 K - amp2.s2p, 50 ohm.\n"
              "Expected: F = F1 + (L-1)/G1 + (F2-1) L/G1.", ports, elems, T, f_ghz)
    F1, G1, F2, Lp = 10 ** (nf1_db / 10), 10 ** (g1_db / 10), 10 ** (nf2_db / 10), 10 ** (pad_db / 10)
    F = F1 + (Lp - 1) / G1 + (F2 - 1) * Lp / G1
    nf_cf = np.full(n, 10 * np.log10(F))
    s_n, cs_n = nodal(elems, ports, T, f)
    p_n = two_port(s_n, cs_n, zz)
    pad_s, pad_cs = nodal(pi_pad("Rp", "a", "b", pad_db), [("a", 50.0), ("b", 50.0)], T, f)
    pad_ca = ca_of(pad_s, pad_cs, zz)
    _, ca_chain = chain((abcd(block_y(a1)), a1.ca), (abcd(s2y(pad_s, zz)), pad_ca), (abcd(block_y(a2)), a2.ca))
    nf_chain = 10 * np.log10(f_at(ca_chain, 1 / 50.0))
    casc = connect(connect(rf.Network(os.path.join(HERE, "amp1.s2p")), 1, skrf_twoport(pad_s, pad_ca, f), 0),
                      1, rf.Network(os.path.join(HERE, "amp2.s2p")), 0)
    p_sk = skrf_params(casc)
    refs["friis_matched"] = dict(
        netlist="friis_matched.cnl", ambient_K=T, ports_z0_ohm=lst(zz), freq_hz=lst(f),
        description="Friis cascade of two matched noisy amplifiers with a matched pad between",
        expected=dict(expected(p_n, cs_n), nf_db=lst(nf_cf)), tolerance=TOL,
        sources={"nf_db": "closed form Friis F1 + (L-1)/G1 + (F2-1)L/G1",
                 "nfmin_db,rn_ohm,gamma_opt,cs": "numpy nodal (amplifier C_Y from the files, pad 4kT/R)"},
        inputs=dict(G1_db=g1_db, NF1_db=nf1_db, G2_db=g2_db, NF2_db=nf2_db, pad_loss_db=pad_db),
        agreement={"closed_form_vs_numpy_nodal_nf_db": within({"nf_db": dmax(nf_cf, p_n["nf_db"])}),
                   "closed_form_vs_numpy_chain_nf_db": within({"nf_db": dmax(nf_cf, nf_chain)}),
                   "numpy_nodal_vs_scikit_rf_connect": within(agree(p_n, p_sk))})

    # -- mismatched, Gamma_opt != 0 --
    f_ghz = [2.0, 4.0, 6.0, 8.0]
    f = np.array(f_ghz) * 1e9
    fg = np.array(f_ghz)
    sA = np.empty((n, 2, 2), complex)
    sA[:, 0, 0], sA[:, 1, 0] = mag_ang(0.55, -50 - 8 * fg), mag_ang(4.0, 120 - 10 * fg)
    sA[:, 0, 1], sA[:, 1, 1] = mag_ang(0.05, 60 - 3 * fg), mag_ang(0.45, -30 - 6 * fg)
    sB = np.empty((n, 2, 2), complex)
    sB[:, 0, 0], sB[:, 1, 0] = mag_ang(0.35, -80 - 5 * fg), mag_ang(2.5, 70 - 8 * fg)
    sB[:, 0, 1], sB[:, 1, 1] = mag_ang(0.08, 30 - 2 * fg), mag_ang(0.4, -60 - 4 * fg)
    A = amp_file("ampA.s2p", f_ghz, sA, 0.6 + 0.08 * fg, mag_ang(0.5 - 0.02 * fg, 40 + 9 * fg), 0.2 + 0.01 * fg, 50.0,
                 "Synthetic mismatched amplifier A (bilateral, Gamma_opt != 0), R 50.")
    B = amp_file("ampB.s2p", f_ghz, sB, 1.0 + 0.1 * fg, mag_ang(np.full(n, 0.35), 70 + 6 * fg), np.full(n, 0.3), 50.0,
                 "Synthetic mismatched amplifier B (bilateral, Gamma_opt != 0), R 50.")
    inter = [R("Ri1", "a", "m", 22.0), C("Ci", "m", "0", 0.4), R("Ri2", "m", "0", 180.0), R("Ri3", "m", "b", 10.0)]
    elems = [dict(NP("XA", "p1", "a", A), file="ampA.s2p"), *inter, dict(NP("XB", "b", "p2", B), file="ampB.s2p")]
    write_cnl("friis_mismatched.cnl", "ampA.s2p - lossy R/C interstage at T0 = 290 K - ampB.s2p, 50 ohm; every\n"
              "block mismatched. Expected: scikit-rf connect (C_A cascade) = numpy nodal.", ports, elems, T, f_ghz)
    s_n, cs_n = nodal(elems, ports, T, f)
    p_n = two_port(s_n, cs_n, zz)
    i_s, i_cs = nodal(inter, [("a", 50.0), ("b", 50.0)], T, f)
    assert np.allclose(i_cs, bosma(i_s, T), rtol=0, atol=1e-12 * K * T)
    casc = connect(connect(rf.Network(os.path.join(HERE, "ampA.s2p")), 1, skrf_twoport(i_s, ca_of(i_s, i_cs, zz), f), 0),
                      1, rf.Network(os.path.join(HERE, "ampB.s2p")), 0)
    p_sk = skrf_params(casc)
    refs["friis_mismatched"] = dict(
        netlist="friis_mismatched.cnl", ambient_K=T, ports_z0_ohm=lst(zz), freq_hz=lst(f),
        description="mismatched cascade amplifier - lossy interstage - amplifier",
        expected=expected(p_sk, cs_n), tolerance=TOL,
        sources={"nf_db,nfmin_db,rn_ohm,gamma_opt": "scikit-rf connect (chain C_A cascade); interstage C_A from Bosma",
                 "cs": "numpy nodal (amplifier C_Y from the files, interstage 4kT/R)"},
        agreement={"scikit_rf_vs_numpy_nodal": within(agree(p_sk, p_n))})


def case_touchstone(refs):
    A = read_s2p("ampA.s2p")  # R 50 file of the Friis case
    f_ghz = [1.0, 2.0, 3.0, 4.0, 5.0]
    fg = np.array(f_ghz)
    n = len(fg)
    s = np.empty((n, 2, 2), complex)
    s[:, 0, 0], s[:, 1, 0] = mag_ang(0.7, -40 - 12 * fg), mag_ang(3.2, 140 - 15 * fg)
    s[:, 0, 1], s[:, 1, 1] = mag_ang(0.04, 50 - 4 * fg), mag_ang(0.6, -20 - 9 * fg)
    gopt = mag_ang(0.55 - 0.03 * fg, 20 + 15 * fg)
    B = amp_file("noise_r25.s2p", f_ghz, s, 0.5 + 0.12 * fg, gopt, 0.6 + 0.02 * fg, 25.0,
                 "Synthetic amplifier referenced to R 25: S, Gamma_opt and rn are all at 25 ohm.")
    for name, net, fn, zp, title in (
            ("touchstone_r50", A, "ampA.s2p", 50.0, "ampA.s2p (R 50) between 50 ohm ports"),
            ("touchstone_r25", B, "noise_r25.s2p", 25.0, "noise_r25.s2p (R 25) between 25 ohm ports"),
            ("touchstone_r25_in_50", B, "noise_r25.s2p", 50.0,
             "noise_r25.s2p (R 25) between 50 ohm ports: Gamma_opt re-referenced to 50 ohm")):
        f = net.f
        fgz = list(f / 1e9)
        ports = [("p1", zp), ("p2", zp)]
        elems = [dict(NP("X1", "p1", "p2", net), file=fn)]
        write_cnl(f"{name}.cnl", f"Touchstone noise round trip: {title}.\nExpected: NFmin, Rn, Gamma_opt as in "
                  "the file (Gamma_opt re-referenced to the port-1 Z0).\nDefault ambient: data-file noise is absolute, "
                  "independent of the ambient.", ports, elems, TDEF, fgz)
        z = net.z0[0, 0].real
        zopt = z * (1 + net.g_opt) / (1 - net.g_opt)
        file_p = dict(nfmin_db=net.nfmin_db, rn_ohm=net.rn, gamma_opt=(zopt - zp) / (zopt + zp))
        file_p["nf_db"] = 10 * np.log10(f_at(net.ca, 1 / zp))
        zz = np.array([zp, zp])
        s_n, cs_n = nodal(elems, ports, TDEF, f)
        p_n = two_port(s_n, cs_n, zz)
        sk = rf.Network(os.path.join(HERE, fn))
        sk.renormalize(zp)
        p_sk = skrf_params(sk, zp)
        refs[name] = dict(
            netlist=f"{name}.cnl", ambient_K=TDEF, ports_z0_ohm=lst(zz), freq_hz=lst(f), description=title,
            expected=expected(file_p, cs_n), tolerance=TOL,
            file_reference_ohm=z, file_gamma_opt_re=lst(net.g_opt.real), file_gamma_opt_im=lst(net.g_opt.imag),
            sources={"nfmin_db,rn_ohm": "the file (Rn = rn * R)",
                     "gamma_opt": "the file, re-referenced Z = R(1+G)/(1-G) -> (Z-Z0)/(Z+Z0)",
                     "nf_db": "F(Ys = 1/Z0) from the file's C_A", "cs": "numpy nodal with the file's C_Y"},
            agreement={"file_vs_numpy_nodal": within(agree(file_p, p_n)),
                       "file_vs_scikit_rf_renormalized": within(agree(file_p, p_sk))})


def case_pozar(refs):
    f_ghz = [3.5, 4.0, 4.5]
    n = len(f_ghz)
    s = np.empty((n, 2, 2), complex)
    s[:, 0, 0], s[:, 1, 0], s[:, 0, 1], s[:, 1, 1] = (mag_ang(0.6, -60), mag_ang(1.9, 81),
                                                      mag_ang(0.05, 26), mag_ang(0.5, -60))
    net = amp_file("pozar_ex12_5.s2p", f_ghz, s, np.full(n, 1.6), np.full(n, mag_ang(0.62, 100)),
                   np.full(n, 20 / 50), 50.0,
                   "Pozar, Microwave Engineering 4th ed., Example 12.5 (the 4 GHz data, held over 3.5-4.5 GHz):\n"
                   "Fmin = 1.6 dB, Gamma_opt = 0.62 /100 deg, Rn = 20 ohm, Z0 = 50 ohm.")
    ports = [("p1", 50.0), ("p2", 50.0)]
    elems = [dict(NP("X1", "p1", "p2", net), file="pozar_ex12_5.s2p")]
    write_cnl("pozar_ex12_5.cnl", "Pozar Ex. 12.5 device between 50 ohm ports; noise circle for NF = 2 dB.\n"
              "Expected: N = 0.1019, centre 0.56 /100 deg, radius 0.24 (textbook rounding).", ports, elems, TDEF, f_ghz)
    z0, nf_db = 50.0, 2.0
    g, fmin, rn = net.g_opt, 10 ** (net.nfmin_db / 10), net.rn
    N = (10 ** (nf_db / 10) - fmin) / (4 * rn / z0) * abs(1 + g) ** 2
    ctr = g / (N + 1)
    rad = np.sqrt(N * (N + 1 - abs(g) ** 2)) / (N + 1)
    sk = rf.Network(os.path.join(HERE, "pozar_ex12_5.s2p"))
    pts = sk.nf_circle(nf_db, npoints=9).T  # scikit-rf returns [npoints, f]; here [f, npoints]: centre + radius e^{j theta}, theta = linspace(0, 2pi, 9)
    sk_ctr = (pts[:, 0] + pts[:, 4]) / 2
    sk_rad = abs(pts[:, 0] - pts[:, 4]) / 2
    # every circle point has NF = 2 dB exactly
    gs = pts[:, :8]
    ys = (1 - gs) / (z0 * (1 + gs))
    nf_on = 10 * np.log10(f_at(net.ca[:, None], ys))
    assert dmax(nf_on, nf_db) < 1e-12
    s_n, cs_n = nodal(elems, ports, TDEF, net.f)
    p_n = two_port(s_n, cs_n, np.array([z0, z0]))
    file_p = dict(nfmin_db=net.nfmin_db, rn_ohm=rn, gamma_opt=g, nf_db=10 * np.log10(f_at(net.ca, 1 / z0)))
    refs["pozar_ex12_5"] = dict(
        netlist="pozar_ex12_5.cnl", ambient_K=TDEF, ports_z0_ohm=[z0, z0], freq_hz=lst(net.f),
        description="noise circle, Pozar 4th ed. Ex. 12.5", expected=expected(file_p, cs_n), tolerance=TOL,
        circle=dict(nf_db=nf_db, N=lst(N), center_re=lst(ctr.real), center_im=lst(ctr.imag), radius=lst(rad),
                    textbook=dict(N=0.1019, center_mag=0.56, center_deg=100.0, radius=0.24, tol=1e-2),
                    scikit_rf_points_npoints9_re=np.real(pts).tolist(), scikit_rf_points_npoints9_im=np.imag(pts).tolist(),
                    parameterization="centre + radius*exp(j*theta), theta = linspace(0, 2*pi, npoints) (scikit-rf)"),
        sources={"nfmin_db,rn_ohm,gamma_opt": "the file", "cs": "numpy nodal with the file's C_Y", "circle": "closed form (Pozar eqs.), scikit-rf nf_circle"},
        agreement={"closed_form_vs_scikit_rf_center": dmax(ctr, sk_ctr), "closed_form_vs_scikit_rf_radius": dmax(rad, sk_rad),
                   "closed_form_vs_textbook": dict(N=float(abs(N[1] - 0.1019)), center_mag=float(abs(abs(ctr[1]) - 0.56)),
                                                   center_deg=float(abs(np.degrees(np.angle(ctr[1])) - 100)),
                                                   radius=float(abs(rad[1] - 0.24))),
                   "file_vs_numpy_nodal": within(agree(file_p, p_n))})
    assert abs(N[1] - 0.1019) < 1e-4 and abs(abs(ctr[1]) - 0.56) < 1e-2 and abs(rad[1] - 0.24) < 1e-2
    assert dmax(ctr, sk_ctr) < 1e-12 and dmax(rad, sk_rad) < 1e-12


def ng_params(ng):
    s = np.stack([[ng["s_1_1"], ng["s_1_2"]], [ng["s_2_1"], ng["s_2_2"]]]).transpose(2, 0, 1)
    cy = np.stack([[ng["i(cy_1_1)"], ng["i(cy_1_2)"]], [ng["i(cy_2_1)"], ng["i(cy_2_2)"]]]).transpose(2, 0, 1)
    return s, cy * (K / K_NGSPICE), dict(nf_db=ng["nf"].real, nfmin_db=ng["nfmin"].real,
                                         rn_ohm=ng["rn"].real, gamma_opt=ng["sopt"])


def case_ngspice(refs):
    f_ghz = [0.5 * i for i in range(1, 11)]
    Tamb = T0
    cases = {
        "ngspice_mixed_temp": ([("p1", 50.0), ("p2", 50.0)],
                               [R("R1", "p1", "m", 30.0), R("R2", "m", "0", 120.0, T=400.0),
                                R("R3", "m", "p2", 40.0, T=77.0), C("C1", "m", "0", 0.6)],
                               "Resistors at mixed temperatures: R2 at 400 K and R3 at 77 K (Temp=), the\n"
                               "rest at the 290 K ambient."),
        "ngspice_coupled_l": ([("p1", 50.0), ("p2", 50.0)],
                              [R("R1", "p1", "a", 3.0), L("L1", "a", "0", 5.0), R("R2", "p2", "b", 4.0),
                               L("L2", "b", "0", 8.0), Kc("K1", "L1", "L2", 0.7), C("C1", "p1", "p2", 0.25)],
                              "Coupled lossy inductors: L1 = 5 nH + 3 ohm, L2 = 8 nH + 4 ohm, k = 0.7\n"
                              "(Mutual M = k sqrt(L1 L2)), 290 K."),
        "ngspice_vccs_amp": ([("p1", 75.0), ("out", 50.0)],
                             [R("Rg", "p1", "in", 8.0), R("Rin", "in", "0", 150.0), C("Cin", "in", "0", 0.4),
                              R("Rf", "in", "out", 600.0), G("G1", "out", "0", "in", "0", 60.0),
                              R("RL", "out", "0", 250.0)],
                             "VCCS amplifier (60 mS, noiseless) with resistive input, load and feedback,\n"
                             "290 K; port 1 at 75 ohm, port 2 at 50 ohm."),
    }
    for name, (ports, elems, title) in cases.items():
        z0, f = bench(name, ports, elems, Tamb, f_ghz, title + "\nExpected: ngspice .sp donoise = numpy nodal.")
        s_n, cs_n = nodal(elems, ports, Tamb, f)
        p_n = two_port(s_n, cs_n, z0)
        s_g, cy_g, p_g = ng_params(ngspice(name, ports, elems, Tamb, f))
        cs_g = cy_to_cs(s_g, cy_g, z0)
        ag = {"numpy_nodal_vs_ngspice": within(agree(p_n, p_g, cs_n, cs_g)), "s_max_abs": dmax(s_n, s_g)}
        assert ag["s_max_abs"] < 1e-11
        if all(e["t"] in "RLCK" and e.get("T") is None for e in elems):  # passive, uniform T
            ag["bosma_vs_adjoint_cs_rel"] = within({"cs_rel": dmax(cs_n, bosma(s_n, Tamb)) / np.max(abs(cs_n))})["cs_rel"]
        refs[name] = dict(
            netlist=f"{name}.cnl", ambient_K=Tamb, ports_z0_ohm=lst(z0), freq_hz=lst(f), description=title,
            expected=expected(p_n, cs_n), tolerance=TOL,
            sources={"all": "numpy nodal (adjoint); ngspice 47 .sp donoise run at temp = 16.85 C agrees"},
            agreement=ag)

    # ngspice's own reference temperature: run the mixed-temperature bench at ngspice's default
    # 27 C. ngspice divides by k*T_circuit where this file divides by k*T0, so
    # F_ng - 1 = (F - 1) T0/T_circuit, Fmin likewise, Rn_ng = Rn T0/T_circuit, SOpt unchanged.
    ports, elems, _ = cases["ngspice_mixed_temp"]
    Tc = C2K(27.0)
    f = np.array(f_ghz) * 1e9
    p_n = two_port(*nodal(elems, ports, Tc, f), np.array([50.0, 50.0]))
    _, _, p_g = ng_params(ngspice("convention", ports, elems, Tc, f))
    fix = lambda db: 10 * np.log10(1 + (10 ** (db / 10) - 1) * T0 / Tc)
    conv = dict(nf_db=dmax(fix(p_n["nf_db"]), p_g["nf_db"]), nfmin_db=dmax(fix(p_n["nfmin_db"]), p_g["nfmin_db"]),
                rn_rel=dmax(p_n["rn_ohm"] * T0 / Tc, p_g["rn_ohm"]) / np.max(p_g["rn_ohm"]),
                gamma_opt=dmax(p_n["gamma_opt"], p_g["gamma_opt"]))
    within(conv)
    refs["ngspice_mixed_temp"]["agreement"]["ngspice_at_27C_after_T0_correction"] = conv


def case_osdi(refs):
    z0, f = 50.0, 1e9
    pts = []
    for g in (1e-3, 4e-3):
        for vdc in (0.5, 1.0):
            for T in (300.0, 350.0):
                th, sh = 4 * K * T * g, 2 * Q * abs(g * vdc)
                si = th + sh
                F = 1 + si * z0 / (4 * K * T0)
                assert abs(F - (1 + (T / T0) * g * z0 + 2 * Q * abs(g * vdc) * z0 / (4 * K * T0))) < 1e-15
                s21 = 2 / (2 + g * z0)
                cs = si * z0 / (2 + g * z0) ** 2  # every entry of the 2x2 Cs (b1 = b2 = V_X/sqrt(Z0))
                # NF through the general definition 1 + Cs22/(k T0 |S21|^2): the loading by g cancels
                assert abs(1 + cs / (K * T0 * s21 ** 2) - F) < 1e-14
                pts.append(dict(g_S=g, vdc_V=vdc, T_K=T, temp_C=K2C(T), freq_hz=f, psd_thermal=th, psd_shot=sh,
                                psd_total=si, s11=-g * z0 / (2 + g * z0), s21=s21, cs_all_entries=cs, F=F,
                                nf_db=10 * np.log10(F)))
    refs["osdi_crf_noise"] = dict(
        netlist=None, ports_z0_ohm=[z0, z0],
        description="fake OSDI crf_noise (conductance g, PSD 4kTg + 2q|g v|) shunt at node X; X between two "
                    "series 1 F DC blocks to the ports and DC-fed from Vdc through a 1 H choke",
        formula="F = 1 + (T/T0) g Z0 + 2q|g Vdc| Z0/(4 k T0); S21 = 2/(2+gZ0), S11 = -gZ0/(2+gZ0); "
                "Cs = S_i Z0/(2+gZ0)^2 [[1,1],[1,1]] with S_i = 4kTg + 2q|g Vdc|. The loading by g cancels in NF.",
        tolerance={"db_abs": 1e-9, "cs_rel": 1e-9},
        notes="White noise: frequency-independent. The 1 F blocks and 1 H choke move NF by ~1e-17 at 1 GHz. "
              "NFmin/Rn/Gamma_opt are degenerate (a shunt noise current at the input: Rn -> 0, "
              "Gamma_opt -> -1, Fmin -> 1) and are not references.",
        points=pts)


def main():
    refs = {}
    case_pads(refs)
    case_lossy_lc(refs)
    case_friis(refs)
    case_touchstone(refs)
    case_pozar(refs)
    case_ngspice(refs)
    case_osdi(refs)
    doc = dict(schema=1, generator="gen_noise_refs.py",
               constants=dict(k=K, q=Q, T0=T0),
               conventions="single-sided absolute PSDs; Cs = <b b^H> of outgoing power waves, ports in noiseless Z0 "
                           "(W/Hz); NF at T0 = 290 K, source = port 1 at its Z0; Gamma_opt referenced to port-1 Z0",
               benches=refs)
    with open(os.path.join(HERE, "refs.json"), "w") as fh:
        json.dump(doc, fh, indent=1)
        fh.write("\n")
    for k, v in refs.items():
        print(f"{k:24s} {json.dumps(v.get('agreement', {}), default=float)[:200]}")


if __name__ == "__main__":
    main()
