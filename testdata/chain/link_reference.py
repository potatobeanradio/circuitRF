#!/usr/bin/env python3
"""An independent calculation of the RF chain budget's link, capacity and DC power, for the test suite to compare with.

    python3 link_reference.py            # writes link_reference.json beside this file

Standard library only. The chain below is written out as a .cchain (the "design" of the JSON, which the test reads
back through circuitRF); this script reads the same design with its own arithmetic, from the textbook forms:

    Pout = Psat - Backoff;  EIRP = Pout - Loss + G_tx + 20 log10(Mtx);  Lfs = 20 log10(4 pi d f / c)
    Prx = EIRP - Lfs - alpha(f) d / 1000 + G_rx;  Pin = min(Prx, IP1dB - RxBackoff)
    Friis on the noise gains; 1/IP1dB = sum of G_(i-1)/IP1dB_i on the signal gains; A = array gain
    MDS = 10 log10(k (T + Te) B) + 30 - 10 log10(A);  SNR = Pin - MDS;  rate = S B log2(1 + SNR)
    P_PA = (pout - pout/g)/PAE;  P = S (Mtx P_PA + Mrx P_branch + P_after);  E_b = P / rate
    NF allowed after stage k = 10 log10(1 + F_k (10^(X/10) - 1) Gn_k), for every stage but the last

Every expression in the design is one Python evaluates as written, with freq in Hz and f_GHz in GHz.

The link's propagation (brief PCH-3), from the textbook forms and the recommendations, not from circuitRF's code:

    margins at availability p:  M_shadow = sigma Q^-1(1 - p);  M_fade = -10 log10(x) with F(x) = 1 - p;
    M_comp the same with F replaced by the lognormal-shadowed outage, integral of phi(z) F(x 10^(-sigma z / 10)) dz,
    by the trapezoid rule at h = 0.05 and a regula falsi on the margin. F at mean power 1: Rayleigh 1 - e^-x; Nakagami
    the incomplete gamma by its own power series; Rician 1 - Q1(sqrt(2K), sqrt(2(K+1)x)) by the Bessel series of the
    Marcum Q function.
    required SNR: Eb/N0 = Q^-1(BER)^2 / 2 (QPSK), (M-1)/(3k) Q^-1(BER k / (4(1 - 1/sqrt M)))^2 (square M-QAM);
    SNR = Eb/N0 + 10 log10(k).
    a geostationary downlink: the slant range, ITU-R P.676 gas on the slant path and P.618 rain, clouds and
    scintillation from testdata/propagation/reference/propagation_reference.py, itself written from the recommendations.
"""
import json
import math
import os
import sys
from statistics import NormalDist

K, T0, C = 1.380649e-23, 290.0, 299792458.0
TANH_P1DB_BELOW_IIP3 = 8.9625
NOISELESS_MIXER_SSB = 10 * math.log10(2)


def st(name, part, **p):
    return {"Name": name, "Part": part, "Parameters": {k: str(v) for k, v in p.items()}}


DESIGN = {
    "FormatVersion": 2, "FrequencyHz": 26e9, "BandwidthHz": 400e6, "InputPowerDbm": -70, "TemperatureK": 290,
    "RequiredSnrDb": 10, "Streams": 2,
    "Stages": [
        st("RXSW", "Switch", IL="0.012*f_GHz + 0.4"),
        st("LNA", "Amp", Gain=20, NF="0.045*f_GHz + 1.29", IP3=-10, IP3Ref="Input", GainPerStage="33 - 15*log10(f_GHz)", Pdc=0.008),
        st("PS", "PhaseShifter", Phase=0, IL=6, IIP3=10),
        st("CMB", "Combiner", N=8, Topology="Levels", IL=0.8),
        st("DRV", "Amp", Gain=10, NF="0.045*f_GHz + 1.29", IP3=15, IP3Ref="Output", Pdc=0.015),
        st("MIX", "Mixer", ConvGain=-7, NF=9, NFType="ssb", IIP3=3, Plo=7, Flo="freq - 3e9", Pdc=0.025),
    ],
    "Link": {
        "Tx": {"Psat": "min(32.5 - 6*log10(f_GHz), 45.79 - 15*log10(f_GHz))", "Backoff": "8", "Loss": "0.012*f_GHz + 0.4",
               "AntennaGain": "5", "Elements": "16", "PaGain": "20", "PAE": "max(32 - 12*log10(f_GHz), 5)"},
        "Path": {"Distance": "200 m", "Attenuation": "0.004*f_GHz", "RxAntennaGain": "3"},
    },
}

# (carrier, Mtx, Mrx): three carriers, a transmitter strong enough to meet the receiver's ceiling, and an array of a
# size that is not a power of 2.
CASES = [(22e9, 16, 8), (26e9, 16, 8), (38e9, 16, 8), (26e9, 1024, 8), (26e9, 16, 6)]


def value(text, f):
    return float(eval(str(text), {"__builtins__": {}}, {"freq": f, "f_GHz": f / 1e9, "log10": math.log10, "min": min, "max": max}))


def db(x):
    return 10 * math.log10(x)


def lin(x):
    return 10 ** (x / 10)


def stage_budget(s, f):
    """(signal gain, noise gain, NF, input IP1dB) of one stage at its input frequency f, dB and dBm."""
    p = {k: value(v, f) for k, v in s["Parameters"].items() if k not in ("IP3Ref", "NFType", "Topology")}
    part = s["Part"]
    if part == "Switch":
        return -p["IL"], -p["IL"], p["IL"], math.inf
    if part == "Amp":
        iip3 = p["IP3"] if s["Parameters"].get("IP3Ref") == "Input" else p["IP3"] - p["Gain"]
        return p["Gain"], p["Gain"], p["NF"], iip3 - TANH_P1DB_BELOW_IIP3
    if part == "PhaseShifter":
        return -p["IL"], -p["IL"], p["IL"], p["IIP3"] - TANH_P1DB_BELOW_IIP3
    if part == "Combiner":
        n = int(p["N"])
        loss = math.ceil(math.log2(n)) * p["IL"]
        return db(n) - loss, -loss, loss, math.inf
    if part == "Mixer":
        return p["ConvGain"], p["ConvGain"], max(p["NF"], NOISELESS_MIXER_SSB), p["IIP3"] - TANH_P1DB_BELOW_IIP3
    raise ValueError(part)


def metres(text):
    t = text.strip()
    return float(t[:-2]) * 1000 if t.endswith("km") else float(t.rstrip("m"))


def case(f, mtx, mrx):
    d = json.loads(json.dumps(DESIGN))
    d["Link"]["Tx"]["Elements"] = str(mtx)
    d["Stages"][3]["Parameters"]["N"] = str(mrx)
    # The stages at their own input frequencies: the mixer moves the rest to |f - Flo|.
    rows, fs = [], f
    for s in d["Stages"]:
        rows.append(stage_budget(s, fs))
        if s["Part"] == "Mixer":
            fs = abs(fs - value(s["Parameters"]["Flo"], fs))
    gs = gn = 1.0
    te = inv1 = 0.0
    allow = []
    for g_s, g_n, nf, ip1 in rows:
        te += T0 * (lin(nf) - 1) / gn
        if math.isfinite(ip1):
            inv1 += gs / lin(ip1)
        gs *= lin(g_s)
        gn *= lin(g_n)
        allow.append(db(1 + (1 + te / T0) * (lin(1) - 1) * gn))
    a = gs / gn
    nf_tot, ip1_tot = db(1 + te / T0), -db(inv1)

    tx, path = d["Link"]["Tx"], d["Link"]["Path"]
    pout = value(tx["Psat"], f) - value(tx["Backoff"], f)
    eirp = pout - value(tx["Loss"], f) + value(tx["AntennaGain"], f) + 20 * math.log10(mtx)
    dist = metres(path["Distance"])
    lfs = 20 * math.log10(4 * math.pi * dist * f / C)
    atm = value(path["Attenuation"], f) * dist / 1000
    prx = eirp - lfs - atm + value(path["RxAntennaGain"], f)
    ceiling = ip1_tot - 10
    pin = min(prx, ceiling)

    b, streams = d["BandwidthHz"], d["Streams"]
    nir = db(K * (d["TemperatureK"] + te) * b) + 30 - db(a)
    snr = pin - nir
    rate = streams * b * math.log2(1 + lin(snr))

    lna = d["Stages"][1]["Parameters"]
    nstg = math.ceil(value(lna["Gain"], f) / value(lna["GainPerStage"], f) - 1e-9)
    branch = nstg * value(lna["Pdc"], f)
    after = sum(value(s["Parameters"].get("Pdc", 0), f) for s in d["Stages"][3:])
    pw = lin(pout - 30)
    pa = (pw - pw / lin(value(tx["PaGain"], f))) / (value(tx["PAE"], f) / 100)
    total = streams * (mtx * pa + mrx * branch + after)
    return {
        "FrequencyHz": f, "Mtx": mtx, "Mrx": mrx,
        "expected": {
            "Pout_PA_dBm": pout, "EIRP_dBm": eirp, "Lfs_dB": lfs, "Atm_dB": atm, "Prx_dBm": prx, "Ceiling_dBm": ceiling,
            "Pin_dBm": pin, "Gain_dB": db(gs), "NFtot_dB": nf_tot, "IP1dBtot_dBm": ip1_tot, "ArrayGain_dB": db(a),
            "NFarray_dB": nf_tot - db(a), "MDS_dBm": nir, "SNRtot_dB": snr, "Margin_dB": snr - d["RequiredSnrDb"],
            "Rate_bps": rate, "PdcPA_W": pa, "PdcTx_W": mtx * pa, "PdcRx_W": mrx * branch + after, "Pdctot_W": total,
            "Ebit_J": total / rate, "Nstg_LNA": nstg, "NFallow_dB": allow[:-1],
        },
    }


# ---------------------------------------------------------------- the link's propagation (PCH-3)

ND = NormalDist()


def qinv(x):
    return -ND.inv_cdf(x)


def lower_gamma_regularized(s, x):
    """P(s, x) by its power series, x^s e^-x / Gamma(s + 1) * sum x^k / ((s+1)...(s+k))."""
    if x <= 0:
        return 0.0
    term, total, k = 1.0, 1.0, 0
    while term > 1e-17 * total:
        k += 1
        term *= x / (s + k)
        total += term
        if k > 100000:
            break
    return min(1.0, math.exp(s * math.log(x) - x - math.lgamma(s + 1)) * total)


def bessel_i(k, z):
    term = (z / 2) ** k / math.factorial(k)
    total, m = term, 0
    while term > 1e-18 * total:
        m += 1
        term *= (z / 2) ** 2 / (m * (m + k))
        total += term
    return total


def rician_cdf(kdb):
    K = 10 ** (kdb / 10)
    a = math.sqrt(2 * K)

    def F(x):
        if x <= 0:
            return 0.0
        b = math.sqrt(2 * (K + 1) * x)
        if b > a + 40:
            return 1.0
        e = math.exp(-(a * a + b * b) / 2)
        if b < a:   # 1 - Q1 = e^-(a^2+b^2)/2 sum_{k>=1} (b/a)^k I_k(ab)
            total, k = 0.0, 1
            while True:
                t = (b / a) ** k * bessel_i(k, a * b)
                total += t
                if t < 1e-18 * total or k > 400:
                    break
                k += 1
            return e * total
        total, k = 0.0, 0   # Q1 = e^-(a^2+b^2)/2 sum_{k>=0} (a/b)^k I_k(ab)
        while True:
            t = (a / b) ** k * bessel_i(k, a * b)
            total += t
            if t < 1e-18 * total or k > 400:
                break
            k += 1
        return 1 - e * total
    return F


def fading_cdf(fading, kdb, m):
    if fading == "rayleigh":
        return lambda x: 0.0 if x <= 0 else 1 - math.exp(-x)
    if fading == "nakagami":
        return lambda x: lower_gamma_regularized(m, m * x)
    return rician_cdf(kdb)


def margin(F, sigma, p):
    """The margin in dB at which the (shadowed) outage is 1 - p."""
    q, h = 1 - p, 0.05

    def outage(mdb):
        x = 10 ** (-mdb / 10)
        if sigma == 0:
            return F(x)
        n = int(round(10 / h))
        total = 0.0
        for i in range(-n, n + 1):
            z = i * h
            w = 0.5 if abs(i) == n else 1.0
            total += w * math.exp(-z * z / 2) / math.sqrt(2 * math.pi) * F(x * 10 ** (-sigma * z / 10))
        return total * h

    # Regula falsi (Illinois) on log(outage) - log(q), between margins of -20 and 100 dB.
    g = lambda mdb: math.log(outage(mdb)) - math.log(q)
    lo, hi = -20.0, 100.0
    glo, ghi = g(lo), g(hi)
    side = 0
    for _ in range(200):
        mid = hi - ghi * (hi - lo) / (ghi - glo)
        gm = g(mid)
        if abs(gm) < 1e-14 or hi - lo < 1e-12:
            return mid
        if gm * ghi > 0:
            hi, ghi = mid, gm
            if side == -1:
                glo /= 2
            side = -1
        else:
            lo, glo = mid, gm
            if side == 1:
                ghi /= 2
            side = 1
    return mid


# (fading, K dB, m, sigma dB, availability %)
MARGINS = [("rayleigh", None, None, 8.0, 99.0), ("rayleigh", None, None, 4.0, 99.9), ("nakagami", None, 2.5, 6.0, 99.0),
           ("rician", 6.0, None, 0.0, 99.0), ("rician", 9.0, None, 4.0, 99.0), ("none", None, None, 8.0, 90.0)]


def margins_case(fading, kdb, m, sigma, a):
    p = a / 100
    shadow = sigma * qinv(1 - p)
    if fading == "none":
        fade, comp = 0.0, shadow
    else:
        F = fading_cdf(fading, kdb, m)
        fade = margin(F, 0, p)
        comp = margin(F, sigma, p) if sigma > 0 else fade
    return {"Fading": fading, "KFactor": kdb, "m": m, "Sigma": sigma, "Availability": a,
            "expected": {"Mshadow_dB": shadow, "Mfade_dB": fade, "Mcomp_dB": comp}}


def required_snr_case(mod, ber):
    k, M = {"qpsk": (2, 4), "16qam": (4, 16), "64qam": (6, 64)}[mod]
    if k == 2:
        g = qinv(ber) ** 2 / 2
    else:
        g = (M - 1) / (3 * k) * qinv(ber * k / (4 * (1 - 1 / math.sqrt(M)))) ** 2
    ebn0 = 10 * math.log10(g)
    return {"Modulation": mod, "TargetBer": ber, "expected": {"EbN0req_dB": ebn0, "SNRreq_dB": ebn0 + 10 * math.log10(k)}}


def satellite_case():
    """A geostationary downlink at 20 GHz, 99.9 %: the design's transmitter and receiver, the path a satellite's at 40
    degrees with every tropospheric term, Rayleigh fading, a 40 dBi receive antenna, BER 1e-6 in QPSK."""
    sys.dont_write_bytecode = True
    sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "propagation", "reference"))
    import propagation_reference as pr
    f, el, h, a = 20e9, 40.0, 35786.0, 99.9
    p = 100 - a
    path = {"Model": "satellite", "Altitude": "35786", "Elevation": "40", "Gas": "on", "Rain": "fixed", "Clouds": "on",
            "Scintillation": "on", "Availability": "99.9", "RxAntennaGain": "40", "Fading": "rayleigh",
            "TargetBer": "1e-6", "Modulation": "qpsk"}
    t15 = 288.15
    e = 7.5 * t15 / 216.7
    ag = pr.gas_slant(20.0, 0.0, el)
    ar = pr.p618_rain(20.0, el, 45.0, p, 0.0, 3.4, 40.0, 45.0)
    ac = pr.p840_KL(20.0) * 0.5 / math.sin(math.radians(el))
    asc = pr.p618_scint(20.0, el, p, 72 * e / t15 + 3.75e5 * e / t15 ** 2, 1.0, 0.5)
    slant = pr.slant_range_km(h, el) * 1000
    loss = pr.fspl(f, slant) + ag + math.sqrt((ar + ac) ** 2 + asc ** 2)
    base = case(f, 16, 8)["expected"]
    prx = base["EIRP_dBm"] - loss + 40
    m = -10 * math.log10(-math.log(a / 100))
    snr = required_snr_case("qpsk", 1e-6)["expected"]["SNRreq_dB"]
    return {"FrequencyHz": f, "Path": path,
            "expected": {"PathLoss_dB": loss, "Prx_dBm": prx, "Margins_dB": m, "SlantRange_km": slant / 1000,
                         "Latency_ms": slant / C * 1e3, "LinkMargin_dB": prx - m - (base["MDS_dBm"] + snr)}}


if __name__ == "__main__":
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "link_reference.json")
    with open(out, "w") as fh:
        json.dump({"design": DESIGN, "cases": [case(*c) for c in CASES],
                   "margins": [margins_case(*c) for c in MARGINS],
                   "required_snr": [required_snr_case(*c) for c in [("qpsk", 1e-6), ("16qam", 1e-5), ("64qam", 1e-6)]],
                   "satellite": satellite_case()}, fh, indent=1)
        fh.write("\n")
    print(f"wrote {out}")
