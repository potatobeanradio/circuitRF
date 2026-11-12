#!/usr/bin/env python3
"""An independent calculation of the RF chain budget's link, capacity and DC power, for the test suite to compare with.

    python3 link_reference.py            # writes link_reference.json beside this file

Standard library only. The chain below is written out as a .cchain (the "design" of the JSON, which the test reads
back through circuitRF); this script reads the same design with its own arithmetic, from the textbook forms:

    Pout = Psat - Backoff;  EIRP = Pout - Loss + G_tx + 20 log10(Mtx);  Lfs = 20 log10(4 pi d f / c)
    Prx = EIRP - Lfs - alpha(f) d / 1000 + G_rx;  Pin = min(Prx, IP1dB - RxBackoff)
    Friis on the noise gains; 1/IP1dB = sum of G_(i-1)/IP1dB_i on the signal gains; A = array gain
    Nir = 10 log10(k (T + Te) B) + 30 - 10 log10(A);  SNR = Pin - Nir;  rate = S B log2(1 + SNR)
    P_PA = (pout - pout/g)/PAE;  P = S (Mtx P_PA + Mrx P_branch + P_after);  E_b = P / rate
    NF allowed after stage k = 10 log10(1 + F_k (10^(X/10) - 1) Gn_k)

Every expression in the design is one Python evaluates as written, with freq in Hz and f_GHz in GHz.
"""
import json
import math
import os

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
            "NFarray_dB": nf_tot - db(a), "Nir_dBm": nir, "SNRtot_dB": snr, "Margin_dB": snr - d["RequiredSnrDb"],
            "Rate_bps": rate, "PdcPA_W": pa, "PdcTx_W": mtx * pa, "PdcRx_W": mrx * branch + after, "Pdctot_W": total,
            "Ebit_J": total / rate, "Nstg_LNA": nstg, "NFallow_dB": allow,
        },
    }


if __name__ == "__main__":
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "link_reference.json")
    with open(out, "w") as fh:
        json.dump({"design": DESIGN, "cases": [case(*c) for c in CASES]}, fh, indent=1)
        fh.write("\n")
    print(f"wrote {out}")
