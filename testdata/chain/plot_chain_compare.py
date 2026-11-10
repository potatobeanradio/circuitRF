#!/usr/bin/env python3
"""The RF chain budget against circuitRF's own simulation of the benches it transfers to, stage by stage.

Two steps, because circuitRF and the plotting usually live in different environments:

    python3 plot_chain_compare.py run  CLI RESULTS.json   # budget and benches, through the CLI
    python3 plot_chain_compare.py plot RESULTS.json OUT   # the figures

`run` needs only the standard library and `dotnet`; CLI is the built CircuitRF.Cli.dll of the build under test. It
transfers each chain into a temporary workspace (nothing in the repository is written), budgets it with `circuitrf
chain --json`, and runs its benches: the 1-Tone bench's noise analysis (`sparam`, or `hb -a HBN` with a mixer) and its
compression sweep (`hb -a SW1`), the 2-Tone bench (`hb`) and the Envelope bench (`env`); for the array also its
Steering bench (the pattern, `sparam` in two sweeps) and its Combiners bench (`sparam`). `plot` needs numpy and
matplotlib; the PNGs it writes are docs/images/rf-chain-budget/*.png.

The chains are the "RF Chain Budget" example's two (a superhet and a four-element steered array with a mixer) and one
more, a four-element array at RF, whose noise comes from the S-parameter analysis rather than harmonic balance.
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
EXAMPLE = os.path.join(REPO, "examples", "RF Chain Budget")


def st(name, part, **p):
    return {"Name": name, "Part": part, "Parameters": {k: str(v) for k, v in p.items()}}


# The test suite's RF array (tests/Ui.Tests/Chain/ChainBudgetTests.cs, RfArray): no mixer, so S-parameter noise.
RF_ARRAY = {
    "FormatVersion": 1, "FrequencyHz": 2.45e9, "BandwidthHz": 4.5e6, "InputPowerDbm": -80, "TemperatureK": 290,
    "Stages": [st("LNA", "Amp", Gain=18, NF=1.2, IP3=10), st("PS", "PhaseShifter", Phase=30, IL=5, NF=5, IIP3=15),
               st("CMB", "Combiner", N=4, IL=0.5, Steer=45),
               st("BPF", "Filter", Response="Butterworth", F1="2.35 GHz", F2="2.55 GHz", IL=1.5),
               st("A2", "Amp", Gain=14, NF=2, IP3=20)],
}


def cli(dll, *args):
    p = subprocess.run(["dotnet", dll, *args, "--json"], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                       universal_newlines=True)
    try:
        return json.loads(p.stdout)
    except ValueError:
        sys.exit(f"circuitrf {' '.join(args)} gave no document:\n{p.stderr[-2000:]}")


def groups(doc):
    return doc.get("result", {}).get("groups", {})


def scalars(doc, group="measurements"):
    out = {}
    for name, cube in groups(doc).get(group, {}).items():
        v = cube.get("values")
        if isinstance(v, list) and len(v) >= 1 and isinstance(v[0], (int, float)):
            out[name] = v[0]
    return out


def run(dll, out_path):
    ws = tempfile.mkdtemp(prefix="crf-chain-figs-")
    try:
        shutil.copy(os.path.join(EXAMPLE, ".cws"), ws)
        chains = {}
        for name in ("Superhet", "Array"):
            shutil.copy(os.path.join(EXAMPLE, name + ".cchain"), ws)
        with open(os.path.join(ws, "RfArray.cchain"), "w") as f:
            json.dump(RF_ARRAY, f, indent=2)
        for name in ("Superhet", "Array", "RfArray"):
            path = os.path.join(ws, name + ".cchain")
            budget = cli(dll, "chain", path, "--to-schematic")
            g = groups(budget)[""]
            stages = g["G_dB"]["axes"][0]["labels"]
            per = {k: g[k]["values"] for k in ("G_dB", "NF_dB", "OIP3_dBm", "IIP3_dBm")}
            with open(path) as f:
                mixer = any(s["Part"] == "Mixer" for s in json.load(f)["Stages"])

            def bench(suffix):
                return os.path.join(ws, f"{name} {suffix}", "schematic", f"{name} {suffix}.csch")

            one = scalars(cli(dll, *(["hb", bench("1-Tone"), "-a", "HBN"] if mixer else ["sparam", bench("1-Tone")])))
            one.update({k: v for k, v in scalars(cli(dll, "hb", bench("1-Tone"), "-a", "SW1")).items() if "IP1dB" in k or "OP1dB" in k})
            two = scalars(cli(dll, "hb", bench("2-Tone")))
            env = groups(cli(dll, "env", bench("Envelope")))[""]
            with open(bench("Envelope")) as f:
                snr_calc = next(float(p["Expression"]) for c in json.load(f)["Components"] if c["Symbol"] == "Meas"
                                for p in c["Parameters"] if p["Name"] == "SNR_calc")
            chains[name] = {
                "stages": stages, "budget": per, "one": one, "two": two,
                "env": {"SNR_sim": env["SNR_dB"]["values"][0], "SNR_calc": snr_calc,
                        "Pout_sim": env["Pavg_dBm"]["values"][0]},
            }
            if name == "Array":
                steer = groups(cli(dll, "sparam", bench("Steering"), "-o", os.path.join(ws, "steer.npy")))["measurements"]
                chains[name]["steering"] = {k: {"axes": [a["name"] for a in steer[k]["axes"]], "values": steer[k]["values"]}
                                            for k in ("Pattern_sim", "AF_calc", "Pattern_err")}
                chains[name]["steering"].update({k: v["values"][0] for k, v in steer.items() if not v.get("axes")})
                chains[name]["combiners"] = scalars(cli(dll, "sparam", bench("Combiners"), "-o", os.path.join(ws, "comb.npy")))
            print(f"{name}: {len(stages)} stages, NF_err {one.get('NF_err'):.3g}, IIP3_err {two.get('IIP3_err'):.3g}, "
                  f"SNR_err {env['SNR_dB']['values'][0] - snr_calc:.3g}")
        with open(out_path, "w") as f:
            json.dump(chains, f, indent=1)
    finally:
        shutil.rmtree(ws, ignore_errors=True)


BUDGET, SIM, INK, MUTED, SURFACE = "#2a78d6", "#eb6834", "#0b0b0b", "#52514e", "#fcfcfb"
TITLES = {"Superhet": "2.45 GHz superhet", "Array": "4-element steered array, then the superhet's receiver",
          "RfArray": "4-element steered array at RF (S-parameter noise)"}


def plot(results, out_dir):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    import numpy as np

    plt.rcParams.update({"font.size": 9, "axes.edgecolor": MUTED, "axes.labelcolor": INK, "xtick.color": MUTED,
                         "ytick.color": MUTED, "axes.grid": True, "grid.color": "#e4e3df", "grid.linewidth": 0.6,
                         "figure.facecolor": SURFACE, "axes.facecolor": SURFACE, "savefig.facecolor": SURFACE})
    os.makedirs(out_dir, exist_ok=True)
    with open(results) as f:
        data = json.load(f)

    worst = {}
    for name, c in data.items():
        s = c["stages"]
        x = np.arange(len(s))
        rows = [("Cumulative gain, dB", c["budget"]["G_dB"], [c["two"][f"G_{n}_sim"] for n in s], "2-Tone harmonic balance"),
                ("Cumulative NF, dB", c["budget"]["NF_dB"], [c["one"][f"NF_{n}_sim"] for n in s],
                 "1-Tone noise: Friis term by term from the run's own shares"),
                ("Cumulative OIP3, dBm", c["budget"]["OIP3_dBm"], [c["two"][f"OIP3_{n}_sim"] for n in s], "2-Tone harmonic balance")]
        fig, axes = plt.subplots(2, 3, figsize=(12, 5.6), gridspec_kw={"height_ratios": [2.2, 1]}, sharex=True)
        for k, (label, calc, sim, how) in enumerate(rows):
            calc = np.array([np.nan if v is None else v for v in calc], float)
            sim = np.array(sim, float)
            top, bottom = axes[0][k], axes[1][k]
            top.plot(x, calc, color=BUDGET, lw=2, label="budget (closed form)")
            top.plot(x, sim, "o", ms=8, mfc="none", mec=SIM, mew=2, label="simulated")
            top.set_title(label, color=INK, fontsize=10, loc="left")
            top.text(0.0, -0.08, how, transform=top.transAxes, fontsize=7.5, color=MUTED, va="top")
            d = np.abs(sim - calc)
            d = np.where(d > 0, d, 1e-16)
            bottom.semilogy(x, d, "s", ms=6, color=SIM)
            bottom.set_ylabel("|sim − budget|, dB", fontsize=8)
            bottom.set_ylim(1e-13, 1)
            bottom.set_xticks(x)
            bottom.set_xticklabels(s, rotation=30, ha="right")
            if k == 0:
                top.legend(frameon=False, loc="best")
        fig.suptitle(TITLES[name], x=0.01, ha="left", color=INK, fontsize=12)
        fig.tight_layout()
        fig.savefig(os.path.join(out_dir, f"per_stage_{name.lower()}.png"), dpi=130)
        plt.close(fig)

        q = {"Gain": abs(c["one"]["G_err"]), "NF": abs(c["one"]["NF_err"]), "IIP3": abs(c["two"]["IIP3_err"]),
             "OIP3": abs(c["two"]["OIP3_err"]), "IP1dB": abs(c["one"]["IP1dB_err"]),
             "SNR (envelope)": abs(c["env"]["SNR_sim"] - c["env"]["SNR_calc"])}
        q["each stage"] = max(max(abs(c["two"][f"G_{n}_sim"] - c["budget"]["G_dB"][i]) for i, n in enumerate(s)),
                              max(abs(c["one"][f"NF_{n}_sim"] - c["budget"]["NF_dB"][i]) for i, n in enumerate(s)))
        worst[name] = q

    if "steering" in data.get("Array", {}):
        plot_steering(data["Array"], out_dir, plt, np)
        plot_combiners(data["Array"]["combiners"], out_dir, plt, np)

    names = list(worst)
    quantities = list(worst[names[0]])
    fig, ax = plt.subplots(figsize=(10, 4.2))
    w = 0.8 / len(names)
    colors = [BUDGET, SIM, "#1baf7a"]
    for i, name in enumerate(names):
        vals = [max(worst[name][k], 1e-16) for k in quantities]
        ax.bar(np.arange(len(quantities)) + (i - (len(names) - 1) / 2) * w, vals, w * 0.92, color=colors[i],
               label=TITLES[name])
    ax.set_yscale("log")
    ax.set_ylim(1e-13, 3)
    ax.set_xticks(np.arange(len(quantities)))
    ax.set_xticklabels(quantities)
    ax.set_ylabel("|simulated − budget|, dB")
    ax.set_title("Largest deviation of the simulation from the budget", loc="left", color=INK)
    ax.text(0.0, -0.13, "IP1dB: the cascaded-P1dB formula's own approximation. SNR: one envelope noise record, "
            "about ±0.12 dB from seed to seed. The rest: the benches' drive levels.",
            transform=ax.transAxes, fontsize=7.5, color=MUTED, va="top")
    ax.legend(frameon=False, fontsize=8, loc="upper left")
    fig.tight_layout()
    fig.savefig(os.path.join(out_dir, "deviation_summary.png"), dpi=130)
    plt.close(fig)
    print(json.dumps(worst, indent=1))


def grid(cube, np):
    """A measurement cube as an array indexed [theta_steer, theta_in], whichever order its axes came out in."""
    v = np.array([np.nan if x is None else x for x in cube["values"]], float)
    if cube["axes"] == ["theta_steer", "theta_in"]:
        return v.reshape(-1, 181)
    return v.reshape(181, -1).T


def plot_steering(c, out_dir, plt, np):
    sim, af, err = (grid(c["steering"][k], np) for k in ("Pattern_sim", "AF_calc", "Pattern_err"))
    th = np.arange(-90, 91)
    colors = ["#2a78d6", "#eb6834", "#1baf7a"]
    fig, (top, bottom) = plt.subplots(2, 1, figsize=(11, 6.4), gridspec_kw={"height_ratios": [2.4, 1]}, sharex=True)
    for i, a in enumerate((-30, 0, 30)):
        top.plot(th, sim[i], color=colors[i], lw=2.2, label=f"steered to {a}°: simulated")
        top.plot(th, af[i], color=INK, lw=0.9, ls="--", label="closed-form array factor" if i == 0 else None)
        main = af[i] > -40
        bottom.semilogy(th[main], np.maximum(np.abs(err[i][main]), 1e-16), ".", ms=3, color=colors[i])
    top.set_ylim(-40, 3)
    top.set_ylabel("gain over the budget's peak, dB")
    top.set_title("Four elements, d = λ/2, steered by their phase shifters: the pattern against the arriving angle",
                  loc="left", color=INK, fontsize=10)
    top.legend(frameon=False, fontsize=8, loc="center left", bbox_to_anchor=(1.01, 0.5))
    bottom.set_ylim(1e-13, 1e-5)
    bottom.set_ylabel("|sim − closed form|, dB", fontsize=8)
    bottom.set_xlabel("theta_in, degrees")
    bottom.text(0.0, -0.32, "Where the closed form is above −40 dB. At each peak the gain, NF and array gain are the "
                f"budget's: array gain {c['steering']['A_0_sim']:.4f} dB at 0°,\n"
                f"NF {c['steering']['NF_m30_sim']:.4f} / {c['steering']['NF_0_sim']:.4f} / {c['steering']['NF_p30_sim']:.4f} dB "
                f"at −30°, 0°, 30° against the budget's {c['steering']['NFw_calc']:.4f} dB.",
                transform=bottom.transAxes, fontsize=7.5, color=MUTED, va="top")
    fig.tight_layout()
    fig.savefig(os.path.join(out_dir, "steering_pattern.png"), dpi=130)
    plt.close(fig)


def plot_combiners(c, out_dir, plt, np):
    variants = [("DP", "Direct,\npassive"), ("TP", "Tree,\npassive"), ("DA", "Direct,\nactive"), ("TA", "Tree,\nactive")]
    qs = [("G", "Gain, dB", True), ("NF", "NF, dB", True), ("IIP3", "IIP3, dBm", False), ("OIP3", "OIP3, dBm", False)]
    fig, axes = plt.subplots(1, 4, figsize=(12, 3.6))
    x = np.arange(len(variants))
    for ax, (q, label, simulated) in zip(axes, qs):
        calc = [c[f"{q}_{v}_calc"] for v, _ in variants]
        ax.bar(x, calc, 0.6, color=BUDGET, label="budget")
        if simulated:
            ax.plot(x, [c[f"{q}_{v}_sim"] for v, _ in variants], "o", ms=9, mfc="none", mec=SIM, mew=2, label="simulated")
        for xi, v in zip(x, calc):
            ax.text(xi, v / 2, f"{v:.2f}", ha="center", va="center", fontsize=8, color="white", fontweight="bold")
        ax.set_xticks(x)
        ax.set_xticklabels([n for _, n in variants], fontsize=8)
        ax.set_title(label, loc="left", color=INK, fontsize=10)
        ax.axhline(0, color=MUTED, lw=0.6)
    axes[0].legend(frameon=False, fontsize=8, loc="upper left")
    fig.suptitle("The same four elements into each combiner: gain and NF simulated in one S-parameter run "
                 f"(largest |sim − budget| {max(abs(c[k]) for k in c if k.endswith('_err')):.1e} dB)",
                 x=0.01, ha="left", color=INK, fontsize=11)
    fig.tight_layout()
    fig.savefig(os.path.join(out_dir, "combiner_variants.png"), dpi=130)
    plt.close(fig)


if __name__ == "__main__":
    if len(sys.argv) == 4 and sys.argv[1] == "run":
        run(sys.argv[2], sys.argv[3])
    elif len(sys.argv) == 4 and sys.argv[1] == "plot":
        plot(sys.argv[2], sys.argv[3])
    else:
        sys.exit(__doc__)
