#!/usr/bin/env python3
"""Comparison figures for the S-parameter noise analysis: circuitRF against independent references.

Two steps, because circuitRF and the references usually live in different environments:

    python3 plot_noise_compare.py run  CLI RESULTS   # circuitRF: every bench here, the example's, the OSDI part
    python3 plot_noise_compare.py plot RESULTS OUT   # the figures, against refs.json, scikit-rf and ngspice

`run` needs only the standard library and `dotnet`. CLI is the built CircuitRF.Cli.dll of the build under test;
the OSDI figure also needs tools/osdi-worker and tools/fake-osdi-model built (their build.sh). `plot` needs numpy,
matplotlib, scikit-rf (2.1.0 used) and ngspice (47 used) on PATH, as gen_noise_refs.py does. The PNGs it writes
are docs/images/sparam-noise/*.png.

On every figure circuitRF is drawn as markers and a reference as lines; a lower panel holds |circuitRF - reference|.
"""
import glob
import json
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
EXAMPLE = os.path.join(REPO, "examples", "S-Parameter Noise")
NG_GRID = "start=0.1 stop=5 npts=50 Unit=GHz"          # the ngspice benches, densely
CIRCLE_DB = (1.8, 2.0, 2.5, 3.0)                        # Pozar Ex. 12.5's circles
CIRCLE_PTS = 37
OSDI = [(1e-3, 300.0), (1e-3, 350.0), (4e-3, 300.0), (4e-3, 350.0)]   # (g, T) per curve


# ---- run: circuitRF ------------------------------------------------------------------------------

def run(cli, results):
    os.makedirs(results, exist_ok=True)
    for f in glob.glob(os.path.join(HERE, "*.s2p")):
        shutil.copy(f, results)

    def sparam(src, out, *extra):
        print(f"circuitRF: {os.path.basename(out)}", flush=True)
        subprocess.run(["dotnet", cli, "sparam", src, "-o", os.path.join(results, out), *extra],
                       check=True, stdout=subprocess.DEVNULL)

    def with_noise(name, out, grid=None, extra_lines=()):
        lines = []
        for line in open(os.path.join(HERE, name + ".cnl")).read().splitlines():
            if line.startswith("analysis SP1"):
                line = f"analysis SP1 type=sparam {grid} Noise=yes" if grid else line + " Noise=yes"
            lines.append(line)
        path = os.path.join(results, out + ".cnl")
        open(path, "w").write("\n".join([*lines, *extra_lines]) + "\n")
        sparam(path, out + ".npy")

    refs = json.load(open(os.path.join(HERE, "refs.json")))["benches"]
    for name, bench in refs.items():
        if bench["netlist"]:
            extra = [f"measure C{round(10 * db)} = ns_circle(SP1.S, {db}, {CIRCLE_PTS})" for db in CIRCLE_DB] \
                if name == "pozar_ex12_5" else []
            if "to_port" in bench["expected"]:   # a three-port: the noise figure to each output port
                extra = [f"measure NF{k} = nf(SP1.S, 0, {k})" for k in bench["expected"]["to_port"]]
            with_noise(name, name, extra_lines=extra)
        if name.startswith("ngspice_"):
            with_noise(name, name + "_dense", grid=NG_GRID)

    for bench in ("1 Attenuator", "2 Friis Cascade"):
        sparam(os.path.join(EXAMPLE, bench, "schematic", bench + ".csch"), bench.split()[1].lower() + ".npy")

    # The fake OSDI part, as OsdiNoiseTests builds it, through a kit folder the CLI's --kits finds.
    kit = os.path.join(results, "kits", "OsdiNoise")
    os.makedirs(kit, exist_ok=True)
    for rel in ("tools/osdi-worker/osdi-worker", "tools/fake-osdi-model/fake_osdi.osdi"):
        shutil.copy(os.path.join(REPO, rel), kit)
    rid = "linux-x64" if sys.platform.startswith("linux") else ("osx-arm64" if sys.platform == "darwin" else "win-x64")
    json.dump({"provider": "OsdiNoise",
               "workers": [{"platform": rid, "command": "osdi-worker", "arguments": ["fake_osdi.osdi"]}]},
              open(os.path.join(kit, "device-provider.json"), "w"))
    path = os.path.join(results, "osdi.cnl")
    open(path, "w").write("\n".join([
        "temp = 26.85", "G = 0.001", "Vb = 0.5",
        "Port:P1  p1 0  Num=1 Z=50 Ohm", "Port:P2  p2 0  Num=2 Z=50 Ohm",
        "C:C1  p1 x  C=1 F", "C:C2  x p2  C=1 F", "L:L1  v x  L=1 H", "Vdc:V1  v 0  Vdc=Vb",
        "ExtDevice:X1  x 0  Provider=OsdiNoise Type=crf_noise g=G",
        "analysis SP1 type=sparam start=1 stop=1 npts=1 Unit=GHz Noise=yes",
        "analysis SW1 type=parametric_sweep Var=Vb Start=0 Stop=1.5 Step=0.1 Inner=SP1"]) + "\n")
    for g, t in OSDI:
        sparam(path, f"osdi_g{g * 1e3:g}m_T{t:g}.npy", "--kits", os.path.dirname(kit),
               "--set", f"G={g!r}", "--set", f"temp={t - 273.15!r}")


# ---- plot: the references and the figures ---------------------------------------------------------

def load(path):
    """A circuitRF .npy result: {cube name: (array, [axis values...])}."""
    import numpy as np
    rec = np.load(path)[0]
    meta = json.loads(bytes(rec["__meta__"]).decode().rstrip("\0"))
    out = {}
    for name in rec.dtype.names:
        if name == "__meta__":
            continue
        m = meta.get(name) or next(v for v in meta.values() if isinstance(v, dict) and v.get("cube") == name)
        out[name] = (np.asarray(rec[name]), [np.asarray(a["values"], float) for a in m["axes"]])
    return out


def plot(results, out):
    import numpy as np
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    sys.path.insert(0, HERE)
    import gen_noise_refs as g
    import skrf as rf
    from skrf.network import connect

    os.makedirs(out, exist_ok=True)
    refs = json.load(open(os.path.join(HERE, "refs.json")))["benches"]
    blue, orange, aqua, yellow = "#2a78d6", "#eb6834", "#1baf7a", "#eda100"
    ink, muted = "#0b0b0b", "#52514e"
    plt.rcParams.update({"font.size": 9, "axes.titlesize": 10, "axes.edgecolor": muted, "axes.labelcolor": ink,
                         "xtick.color": muted, "ytick.color": muted, "axes.grid": True, "grid.color": "#e4e3df",
                         "grid.linewidth": 0.6, "legend.frameon": False, "lines.linewidth": 1.6,
                         "figure.facecolor": "#fcfcfb", "axes.facecolor": "#fcfcfb", "savefig.facecolor": "#fcfcfb"})
    DOT = dict(linestyle="none", marker="o", markersize=5, markerfacecolor="none", markeredgewidth=1.3)
    TINY = 1e-18   # where a deviation is exactly 0, it is drawn here on the log axis
    worst = {}     # bench -> {quantity: max |circuitRF - reference|}

    def fig(ncols, title):
        """One row of plots, each a (value, |deviation|) pair of axes sharing x."""
        f = plt.figure(figsize=(1200 / 110, 700 / 110), dpi=110)
        gs = f.add_gridspec(2, ncols, height_ratios=[3, 1], hspace=0.12, wspace=0.38)
        axes = [(f.add_subplot(gs[0, c]), f.add_subplot(gs[1, c])) for c in range(ncols)]
        for top, bot in axes:
            bot.sharex(top)
            top.tick_params(labelbottom=False)
            bot.set_yscale("log")
            bot.set_ylim(TINY / 2, 3e-9)
            bot.set_yticks([1e-18, 1e-15, 1e-12, 1e-9])
            bot.axhline(1e-9, color=muted, linestyle=":", linewidth=1)    # the tests' tolerance
        axes[0][1].set_ylabel("|Δ|", color=muted)   # one label for the row: the panels sit too close for one each
        axes[-1][1].annotate("test tolerance", (1.0, 1e-9), xycoords=("axes fraction", "data"), xytext=(-4, -9),
                             textcoords="offset points", ha="right", fontsize=7, color=muted)
        f.suptitle(title, fontsize=11, color=ink)
        return f, axes

    def dev(ax, x, d, color, label=None):
        ax.plot(x, np.maximum(np.abs(d), TINY), color=color, **{**DOT, "markersize": 3.5}, label=label)

    def save(f, name):
        f.savefig(os.path.join(out, name), dpi=110, bbox_inches="tight")
        plt.close(f)
        print(f"wrote {name}: {os.path.getsize(os.path.join(out, name)) // 1024} KB")

    def note(name, **q):
        worst.setdefault(name, {}).update({k: float(np.max(np.abs(v))) for k, v in q.items()})

    # -- every refs.json bench on its own grid: the summary's numbers -------------------------------
    for name, b in refs.items():
        if not b["netlist"]:
            continue
        r, e = load(os.path.join(results, name + ".npy")), b["expected"]
        if "to_port" in e:   # a three-port: nf(SP1.S, 0, k) to each output against the two-port reference
            note(name, nf_db=np.concatenate([r[f"NF{k}"][0] - np.asarray(x["nf_db"]) for k, x in e["to_port"].items()]))
            continue
        gopt = np.asarray(e["gamma_opt_re"]) + 1j * np.asarray(e["gamma_opt_im"])
        note(name, nf_db=r["NF"][0] - e["nf_db"], nfmin_db=r["NFmin"][0] - e["nfmin_db"],
             rn_rel=(r["Rn"][0] - e["rn_ohm"]) / np.asarray(e["rn_ohm"]), gamma=np.abs(r["Sopt"][0] - gopt))

    # -- a. the attenuator against its temperature ---------------------------------------------------
    r = load(os.path.join(results, "attenuator.npy"))
    tk = r["NF"][1][0] + 273.15
    nf = r["NF"][0][:, 0]
    L = 10 ** 0.6
    t_line = np.linspace(tk[0], tk[-1], 400)
    closed = 10 * np.log10(1 + tk / g.T0 * (L - 1))
    f, ((top, bot),) = fig(1, "A matched 6 dB pad: noise figure against the pad's temperature")
    top.plot(t_line, 10 * np.log10(1 + t_line / g.T0 * (L - 1)), color=orange, label="Closed form, 1 + (T/T0)(L − 1)")
    top.plot(tk, nf, color=blue, **DOT, label="circuitRF, Noise=yes")
    top.axhline(6.0, color=muted, linestyle=":", linewidth=1)
    top.axvline(g.T0, color=muted, linestyle=":", linewidth=1)
    top.annotate("NF = loss = 6 dB at T0 = 290 K", (g.T0, 6.0), xytext=(150, 6.6), color=ink, fontsize=8,
                 arrowprops=dict(arrowstyle="-", color=muted, linewidth=0.8))
    top.set_ylabel("NF, dB")
    top.legend(loc="lower right")
    dev(bot, tk, nf - closed, blue)
    bot.set_xlabel("Pad temperature, K (the bench's global temp, swept)")
    note("1 Attenuator (example)", nf_db=nf - closed)
    save(f, "attenuator_nf_vs_temperature.png")

    # -- b. Friis -------------------------------------------------------------------------------------
    def skrf_pad(loss_db, freq):
        ps, pcs = g.nodal(g.pi_pad("Rp", "a", "b", loss_db), [("a", 50.0), ("b", 50.0)], g.T0, freq)
        return g.skrf_twoport(ps, g.ca_of(ps, pcs, np.array([50.0, 50.0])), freq)

    f, ((a1, d1), (a2, d2), (a3, d3)) = fig(3, "Cascades: circuitRF against Friis' formula and scikit-rf's connect")
    # matched cascade, refs.json's bench, against frequency
    b, r = refs["friis_matched"], load(os.path.join(results, "friis_matched.npy"))
    fq = np.asarray(b["freq_hz"])
    sk = connect(connect(rf.Network(os.path.join(HERE, "amp1.s2p")), 1, skrf_pad(b["inputs"]["pad_loss_db"], fq), 0),
                 1, rf.Network(os.path.join(HERE, "amp2.s2p")), 0)
    sk_nf = 10 * np.log10(sk.nf(50.0))
    a1.plot(fq / 1e9, b["expected"]["nf_db"], color=orange, label="Friis, closed form")
    a1.plot(fq / 1e9, sk_nf, color=aqua, linestyle="--", label="scikit-rf connect")
    a1.plot(fq / 1e9, r["NF"][0], color=blue, **DOT, label="circuitRF")
    a1.set_title("Matched: amp1 – 3 dB pad – amp2", color=ink)
    a1.set_ylabel("dB")
    a1.set_ylim(1.0, 1.6)
    a1.legend(loc="upper right")
    dev(d1, fq / 1e9, r["NF"][0] - b["expected"]["nf_db"], orange)
    dev(d1, fq / 1e9, r["NF"][0] - sk_nf, aqua)
    d1.set_xlabel("Frequency, GHz")
    note("friis_matched", nf_db_vs_skrf=r["NF"][0] - sk_nf)
    # the example's cascade against the pad's loss
    r = load(os.path.join(results, "friis.npy"))
    loss, nf, nfmin = r["NF"][1][0], r["NF"][0][:, 0], r["NFmin"][0][:, 0]
    # Friis, from the stages as data/amp_a.s2p and amp_b.s2p state them (FA from a 50 ohm source, Gamma_s = 0)
    fa = 10 ** 0.08 + 4 * 0.25 * 0.25 ** 2 / abs(1 + 0.25 * np.exp(1j * np.pi / 4)) ** 2
    ga, fb = 10 ** 1.6, 10 ** 0.3
    lin = 10 ** (loss / 10)
    friis = 10 * np.log10(fa + (lin - 1) / ga + (fb - 1) * lin / ga)
    one = np.asarray([1e9])
    sk_nf, sk_min = [], []
    for ld in loss:
        n = connect(connect(rf.Network(os.path.join(EXAMPLE, "data", "amp_a.s2p"))["1ghz"], 1, skrf_pad(ld, one), 0),
                    1, rf.Network(os.path.join(EXAMPLE, "data", "amp_b.s2p"))["1ghz"], 0)
        sk_nf.append(10 * np.log10(n.nf(50.0)[0]))
        sk_min.append(n.nfmin_db[0])
    a2.plot(loss, friis, color=orange, label="NF, Friis")
    a2.plot(loss, sk_nf, color=aqua, linestyle="--", label="NF, scikit-rf")
    a2.plot(loss, sk_min, color=yellow, linestyle="--", label="NFmin, scikit-rf")
    a2.plot(loss, nf, color=blue, **DOT, label="NF, circuitRF")
    a2.plot(loss, nfmin, color=blue, **{**DOT, "marker": "s"}, label="NFmin, circuitRF")
    a2.set_title("The example: stage A – pad – stage B, 1 GHz", color=ink)
    a2.legend(loc="upper left")
    dev(d2, loss, nf - friis, orange)
    dev(d2, loss, nf - np.asarray(sk_nf), aqua)
    dev(d2, loss, nfmin - np.asarray(sk_min), yellow)
    d2.set_xlabel("Pad loss, dB")
    note("2 Friis Cascade (example)", nf_db=nf - friis, nf_db_vs_skrf=nf - np.asarray(sk_nf),
         nfmin_db=nfmin - np.asarray(sk_min))
    # the mismatched cascade, refs.json (scikit-rf connect)
    b, r = refs["friis_mismatched"], load(os.path.join(results, "friis_mismatched.npy"))
    fq = np.asarray(b["freq_hz"]) / 1e9
    a3.plot(fq, b["expected"]["nf_db"], color=aqua, label="NF, scikit-rf")
    a3.plot(fq, b["expected"]["nfmin_db"], color=yellow, label="NFmin, scikit-rf")
    a3.plot(fq, r["NF"][0], color=blue, **DOT, label="NF, circuitRF")
    a3.plot(fq, r["NFmin"][0], color=blue, **{**DOT, "marker": "s"}, label="NFmin, circuitRF")
    a3.set_title("Mismatched: ampA – lossy R/C – ampB", color=ink)
    a3.legend(loc="upper left")
    dev(d3, fq, r["NF"][0] - b["expected"]["nf_db"], aqua)
    dev(d3, fq, r["NFmin"][0] - b["expected"]["nfmin_db"], yellow)
    d3.set_xlabel("Frequency, GHz")
    save(f, "friis_cascades.png")

    # -- c. ngspice .sp donoise -------------------------------------------------------------------
    cases = g.ngspice_cases()
    f, row = fig(4, "Against ngspice 47 .sp donoise at 290 K: markers circuitRF, lines ngspice")
    labels = {"ngspice_mixed_temp": "Mixed temperatures", "ngspice_coupled_l": "Coupled lossy inductors",
              "ngspice_vccs_amp": "VCCS amplifier, 75/50 Ω ports"}
    for (name, (ports, elems, _)), color in zip(cases.items(), (blue, orange, aqua)):
        r = load(os.path.join(results, name + "_dense.npy"))
        fq = r["NF"][1][0]
        _, _, ng = g.ng_params(g.ngspice(name, ports, elems, g.T0, fq))
        ours = dict(nf_db=r["NF"][0], nfmin_db=r["NFmin"][0], rn_ohm=r["Rn"][0], gamma=np.abs(r["Sopt"][0]))
        theirs = dict(nf_db=ng["nf_db"], nfmin_db=ng["nfmin_db"], rn_ohm=ng["rn_ohm"], gamma=np.abs(ng["gamma_opt"]))
        for (top, bot), q in zip(row, ("nf_db", "nfmin_db", "rn_ohm", "gamma")):
            top.plot(fq / 1e9, theirs[q], color=color, label=labels[name])
            top.plot(fq / 1e9, ours[q], color=color, **{**DOT, "markersize": 3.5}, markevery=3)
            d = ours[q] - theirs[q]
            if q == "rn_ohm":
                d = d / theirs[q]
            elif q == "gamma":
                d = np.abs(r["Sopt"][0] - ng["gamma_opt"])
            dev(bot, fq / 1e9, d, color)
        note(name + " (ngspice, dense)", nf_db=ours["nf_db"] - theirs["nf_db"], nfmin_db=ours["nfmin_db"] - theirs["nfmin_db"],
             rn_rel=(ours["rn_ohm"] - theirs["rn_ohm"]) / theirs["rn_ohm"], gamma=np.abs(r["Sopt"][0] - ng["gamma_opt"]))
    for (top, bot), t, u in zip(row, ("NF", "NFmin", "Rn", "|Sopt|"), ("dB", "dB", "Ω", "")):
        top.set_title(t, color=ink)
        top.set_ylabel(u)
        bot.set_xlabel("Frequency, GHz")
    row[2][1].text(0.03, 0.9, "relative, |Δ|/Rn", transform=row[2][1].transAxes, va="top", fontsize=7, color=muted)
    f.legend(*row[0][0].get_legend_handles_labels(), loc="upper center", ncol=3, bbox_to_anchor=(0.5, 0.955))
    save(f, "ngspice_noise_parameters.png")

    # -- d. Pozar Ex. 12.5: noise circles on the source plane -------------------------------------
    b, r = refs["pozar_ex12_5"], load(os.path.join(results, "pozar_ex12_5.npy"))
    net = rf.Network(os.path.join(HERE, "pozar_ex12_5.s2p"))["4ghz"]
    f = plt.figure(figsize=(1200 / 110, 700 / 110), dpi=110)
    ax = f.add_subplot(1, 1, 1)
    th = np.linspace(0, 2 * np.pi, 400)
    ax.plot(np.cos(th), np.sin(th), color=muted, linewidth=1)
    ax.axhline(0, color="#e4e3df", linewidth=0.8)
    ax.axvline(0, color="#e4e3df", linewidth=0.8)
    gopt = net.g_opt[0]
    rn_n = 20 / 50
    worst_pt = 0.0
    for db, color in zip(CIRCLE_DB, (blue, orange, aqua, yellow)):
        dense = net.nf_circle(db, npoints=361)[:, 0]
        ax.plot(dense.real, dense.imag, color=color, label=f"{db:g} dB, scikit-rf nf_circle")
        ours = r[f"C{round(10 * db)}"][0][1]     # 4 GHz
        ax.plot(ours.real, ours.imag, color=color, **{**DOT, "markersize": 4})
        theirs = net.nf_circle(db, npoints=CIRCLE_PTS)[:, 0]
        worst_pt = max(worst_pt, float(np.max(np.abs(ours - theirs))))
        n = (10 ** (db / 10) - 10 ** 0.16) / (4 * rn_n) * abs(1 + gopt) ** 2      # closed form, Pozar (12.39)
        ax.plot((gopt / (1 + n)).real, (gopt / (1 + n)).imag, color=color, marker="+", markersize=8, linestyle="none")
    c2 = complex(b["circle"]["center_re"][1], b["circle"]["center_im"][1])
    ax.plot(gopt.real, gopt.imag, color=ink, marker="*", markersize=9, linestyle="none", label="Sopt = 0.62∠100° (the file)")
    ax.plot([], [], color=muted, **DOT, label="circuitRF, ns_circle(SP1.S, NF)")
    ax.plot([], [], color=muted, marker="+", linestyle="none", label="closed-form centre")
    ax.annotate(f"2 dB: centre {abs(c2):.4f}∠{np.degrees(np.angle(c2)):.1f}°, radius {b['circle']['radius'][1]:.4f}\n"
                f"(Pozar: 0.56∠100°, 0.24)\nmax |circuitRF − scikit-rf| over {len(CIRCLE_DB) * CIRCLE_PTS} points: "
                f"{worst_pt:.1e}", (1.03, 0.05), xycoords="axes fraction", fontsize=8, color=ink)
    ax.set_aspect("equal")
    ax.set_xlim(-1.05, 1.05)
    ax.set_ylim(-1.05, 1.05)
    ax.set_xlabel("Re Γs")
    ax.set_ylabel("Im Γs")
    ax.set_title("Pozar Ex. 12.5 at 4 GHz: constant-noise-figure circles on the source plane", color=ink)
    ax.legend(loc="upper left", bbox_to_anchor=(1.02, 1.0), fontsize=8)
    note("pozar_ex12_5 circles (vs scikit-rf)", gamma=np.array([worst_pt]))
    save(f, "pozar_noise_circles.png")

    # -- e. the OSDI part against its closed form ----------------------------------------------------
    f, ((top, bot),) = fig(1, "A compiled (OSDI) device's own noise: the test model crf_noise, shunt at a DC-fed node")
    for (gs, t), color in zip(OSDI, (blue, orange, aqua, yellow)):
        r = load(os.path.join(results, f"osdi_g{gs * 1e3:g}m_T{t:g}.npy"))
        v, nf = r["NF"][1][0], r["NF"][0][:, 0]
        vl = np.linspace(v[0], v[-1], 200)
        closed = lambda vv: 10 * np.log10(1 + t / g.T0 * gs * 50 + 2 * g.Q * np.abs(gs * vv) * 50 / (4 * g.K * g.T0))
        top.plot(vl, closed(vl), color=color, label=f"g = {gs * 1e3:g} mS, T = {t:g} K")
        top.plot(v, nf, color=color, **DOT)
        dev(bot, v, nf - closed(v), color)
        note(f"osdi_crf_noise g={gs * 1e3:g} mS T={t:g} K", nf_db=nf - closed(v))
    top.plot([], [], color=muted, **DOT, label="circuitRF (markers)")
    top.set_ylabel("NF, dB")
    top.legend(loc="upper left", title="Closed form 1 + (T/T0)gZ0 + 2q|gV|Z0/4kT0 (lines)", title_fontsize=8, fontsize=8)
    bot.set_xlabel("DC bias across the device, V (thermal 4kTg plus shot 2q|gV|)")
    save(f, "osdi_nf_vs_bias.png")

    # -- f. summary: the worst NF deviation of every bench --------------------------------------
    kind = {"pad6": "closed form", "lossy_lc": "Bosma, closed form", "friis_matched": "Friis",
            "friis_mismatched": "scikit-rf", "touchstone": "the file, scikit-rf", "pozar": "the file",
            "1 Attenuator": "closed form", "2 Friis": "Friis", "osdi": "closed form",
            "splitter3_passive": "3-port, Bosma", "splitter3_vccs": "3-port, numpy nodal two-port"}
    rows = [(k + "  ·  " + next((v for p, v in kind.items() if k.startswith(p)),
                                "ngspice" if "dense" in k else "numpy nodal = ngspice"), v)
            for k, v in worst.items() if "nf_db" in v or "nf_db_vs_skrf" in v]
    f = plt.figure(figsize=(1200 / 110, 700 / 110), dpi=110)
    ax = f.add_subplot(1, 1, 1)
    y = np.arange(len(rows))[::-1]
    for i, (k, v) in zip(y, rows):
        for q, color, mk in (("nf_db", blue, "o"), ("nfmin_db", orange, "s"), ("nf_db_vs_skrf", aqua, "D")):
            if q in v:
                ax.plot(max(v[q], TINY), i, color=color, marker=mk, markersize=6, markerfacecolor="none",
                        markeredgewidth=1.4, linestyle="none")
    for q, color, mk, lab in (("nf_db", blue, "o", "|ΔNF|, against the bench's reference"),
                              ("nfmin_db", orange, "s", "|ΔNFmin|"), ("nf_db_vs_skrf", aqua, "D", "|ΔNF|, against scikit-rf")):
        ax.plot([], [], color=color, marker=mk, markerfacecolor="none", linestyle="none", label=lab)
    ax.set_yticks(y, [k for k, _ in rows])
    ax.set_xscale("log")
    ax.set_xlim(TINY / 2, 1e-6)
    ax.axvline(1e-9, color=muted, linestyle=":", linewidth=1)
    ax.annotate("test tolerance, 1e-9 dB", (1e-9, len(rows) - 0.4), xytext=(1.3e-9, len(rows) - 0.4), fontsize=8, color=muted)
    ax.set_xlabel(f"Largest deviation over the bench, dB  (exactly 0 is drawn at {TINY:g})")
    ax.set_title("Every bench: circuitRF's largest deviation from its reference", color=ink)
    ax.legend(loc="lower right", fontsize=8)
    ax.grid(axis="y", visible=False)
    save(f, "deviation_summary.png")

    for k, v in worst.items():
        print(f"{k:42s} " + "  ".join(f"{q} {x:.1e}" for q, x in v.items()))


if __name__ == "__main__":
    if len(sys.argv) == 4 and sys.argv[1] == "run":
        run(os.path.abspath(sys.argv[2]), os.path.abspath(sys.argv[3]))
    elif len(sys.argv) == 4 and sys.argv[1] == "plot":
        plot(os.path.abspath(sys.argv[2]), os.path.abspath(sys.argv[3]))
    else:
        sys.exit(__doc__)
