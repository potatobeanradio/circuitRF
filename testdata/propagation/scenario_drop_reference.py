#!/usr/bin/env python3
"""Independent reference for the stochastic cluster-level channel drop.

Written from 3GPP TR 38.901 V18.0.0, clause 7.5 ("Fast fading model"),
Steps 1-11 at cluster and ray level, and Annex A.1, ALONE.  Every table value
used is transcribed below with the table it came from, so the transcription can
be checked line by line against the report.

Cases (forced propagation state, single link, no O2I, no spatial consistency,
no blockage, no oxygen absorption):

    case       fc [GHz]  d2D [m]  hBS [m]  hUT [m]  drops   seed
    UMi-LOS    28        100      10       1.5      10000   3890101
    UMi-NLOS   28        100      10       1.5      10000   3890102
    InH-LOS    28        20       3        1        10000   3890103
    InH-NLOS   28        20       3        1        10000   3890104

Random numbers: numpy.random.default_rng(seed), one generator per case.

Geometry: BS at the origin, UT at horizontal distance d2D along +x.
    AOD_LOS = 0 deg, AOA_LOS = 180 deg,
    ZOD_LOS = 90 deg + atan2(hBS - hUT, d2D) (degrees), ZOA_LOS = 180 deg - ZOD_LOS.

Square root of the cross-correlation matrix (Step 4): the lower-triangular
Cholesky factor L of the correlation matrix (numpy.linalg.cholesky), the report's
own choice in Step 4, over the vector order s = [SF, K, DS, ASD, ASA, ZSD, ZSA]
(LOS) or [SF, DS, ASD, ASA, ZSD, ZSA] (NLOS, K removed).  s = L xi with xi iid
N(0, 1); SF[dB] = sigma_SF s_SF, K[dB] = mu_K + sigma_K s_K,
lgX = mu_lgX + sigma_lgX s_X.

Circular angle spread: Annex A.1, equation (A-1), as V18.0.0 states it:
    AS = sqrt( -2 ln | sum_{n,m} P_{n,m} exp(j phi_{n,m}) / sum_{n,m} P_{n,m} | )
in radians, reported here in degrees.  Zenith spreads are the plain
power-weighted RMS about the power-weighted mean (no wrapping), as asked.

Choices where the report leaves room (each also listed in the run summary):
  * -25 dB cluster removal (Step 6): applied to the P_n of equation (7.5-8) in
    LOS (the P_n most recently defined where the sentence stands; for n >= 2
    these equal the powers entering the channel, and for n = 1 it is the first
    cluster's total including the LOS ray), to (7.5-6) in NLOS.  C_phi and
    C_theta keep the table N ("scaling factors need not be changed").  No
    renormalization after the removal (the report states none), so the kept
    power is slightly below 1; `power_sum` is the sum after the (7.5-6)
    normalization (exactly 1 up to rounding), `power_kept` the sum of the
    Step-11 powers of the clusters that survive.  Because the LOS-spiked maximum
    is large when K is large, this removes most NLOS clusters in high-K drops
    and pushes the realized K well above the drawn K.  The other reading
    (threshold on the (7.5-6) powers, i.e. the NLOS part alone) is run as well
    for the LOS cases, with seed + 100, and stored under
    `variant_removal_on_7_5_6_powers`.
  * Angles (Step 7) are generated for all N clusters and then the removed ones
    are dropped; a cluster's angle depends only on its own P_n and max(P_n),
    and (7.5-12)/(7.5-17) need cluster 1's draws even if cluster 1 were removed.
  * Delays entering Step 11: in LOS the SCALED delays tau_n^LOS = tau_n / C_tau
    of (7.5-3)/(7.5-4).  The report says the scaled delays are not to be used
    in cluster power generation (so (7.5-5) takes the unscaled tau_n), which
    leaves Step 11 as the place they are used; the LOS ray sits at the first
    cluster's delay, tau_1 / C_tau = 0.  `ds` is computed that way;
    `ds_unscaled` (the unscaled tau_n, for reference only) equals `ds` in NLOS.
    C_tau plays no part in the angle generation.
  * Zenith wrapping (7.5-18): theta is taken modulo 360 deg into [0, 360), then
    theta in [180, 360) becomes 360 - theta; applied to ZOD the same way.
    Azimuths are wrapped into [-180, 180) (the report states no azimuth wrap;
    none of the statistics depends on it).
  * mu_offset,ZOD enters only the NLOS equation (7.5-19); LOS ZOD uses (7.5-17)
    (and the LOS offset is 0 in every table used here anyway).
  * Coupling (Step 8): independent random permutations of the AOD, ZOA and ZOD
    ray indices against the AOA ray index, within each cluster, and within each
    sub-cluster of Table 7.5-5 for the two strongest clusters (strongest by the
    Step-6 P_n).  XPR (Step 9) and the four initial phases (Step 10) are drawn
    and discarded; no statistic below uses them.
  * Quantiles: numpy.quantile, default (linear) interpolation.
"""

import json
import math
import os
import sys

import numpy as np

# ---------------------------------------------------------------------------
# Transcribed tables
# ---------------------------------------------------------------------------

# Table 7.5-2: Scaling factors for AOA, AOD generation (C_phi^NLOS by # clusters)
C_PHI_NLOS = {4: 0.779, 5: 0.860, 8: 1.018, 10: 1.090, 11: 1.123, 12: 1.146,
              14: 1.190, 15: 1.211, 16: 1.226, 19: 1.273, 20: 1.289, 25: 1.358}

# Table 7.5-4: Scaling factors for ZOA, ZOD generation (C_theta^NLOS by # clusters)
C_THETA_NLOS = {8: 0.889, 10: 0.957, 11: 1.031, 12: 1.104, 15: 1.1088,
                19: 1.184, 20: 1.178, 25: 1.282}

# Table 7.5-3: Ray offset angles within a cluster, rms angle spread normalized to 1
#   m = 1,2: +-0.0447   3,4: +-0.1413   5,6: +-0.2492   7,8: +-0.3715
#   9,10: +-0.5129   11,12: +-0.6797   13,14: +-0.8844   15,16: +-1.1481
#   17,18: +-1.5195   19,20: +-2.1551
_ALPHA_PAIRS = [0.0447, 0.1413, 0.2492, 0.3715, 0.5129, 0.6797, 0.8844,
                1.1481, 1.5195, 2.1551]
ALPHA_M = np.array([s * a for a in _ALPHA_PAIRS for s in (+1.0, -1.0)])

# Table 7.5-5: Sub-cluster ray mapping for the two strongest clusters (1-based)
SUBCLUSTERS = [[1, 2, 3, 4, 5, 6, 7, 8, 19, 20],
               [9, 10, 11, 12, 17, 18],
               [13, 14, 15, 16]]

# Equation (7.5-3): C_tau = 0.7705 - 0.0433 K + 0.0002 K^2 + 0.000017 K^3
# Equation (7.5-10), LOS: C_phi = C_phi^NLOS (1.1035 - 0.028 K - 0.002 K^2 + 0.0001 K^3)
# Equation (7.5-15), LOS: C_theta = C_theta^NLOS (1.3086 + 0.0339 K - 0.0077 K^2 + 0.0002 K^3)
# (K in dB, as generated in Step 4)


def c_tau(k_db):
    return 0.7705 - 0.0433 * k_db + 0.0002 * k_db ** 2 + 0.000017 * k_db ** 3


def c_phi(n_clusters, los, k_db):
    c = C_PHI_NLOS[n_clusters]
    if los:
        c *= 1.1035 - 0.028 * k_db - 0.002 * k_db ** 2 + 0.0001 * k_db ** 3
    return c


def c_theta(n_clusters, los, k_db):
    c = C_THETA_NLOS[n_clusters]
    if los:
        c *= 1.3086 + 0.0339 * k_db - 0.0077 * k_db ** 2 + 0.0002 * k_db ** 3
    return c


def scenario_parameters(scen, los, fc, d2d, hbs, hut):
    """Every parameter of Table 7.5-6 / 7.4.1-1 / 7.5-8 / 7.5-10 for one case.

    fc in GHz, distances and heights in metres.
    """
    p = {}
    if scen == "UMi":
        # Table 7.5-6 Part-1 NOTE 7: for UMi and fc < 2 GHz use fc = 2.
        f = max(fc, 2.0)
        lf = math.log10(1.0 + f)
        if los:
            # Table 7.5-6 Part-1, UMi-Street Canyon, LOS
            p["mu_lgDS"] = -0.24 * lf - 7.14
            p["sigma_lgDS"] = 0.38
            p["mu_lgASD"] = -0.05 * lf + 1.21
            p["sigma_lgASD"] = 0.41
            p["mu_lgASA"] = -0.08 * lf + 1.73
            p["sigma_lgASA"] = 0.014 * lf + 0.28
            p["mu_lgZSA"] = -0.1 * lf + 0.73
            p["sigma_lgZSA"] = -0.04 * lf + 0.34
            p["mu_K"] = 9.0
            p["sigma_K"] = 5.0
            corr = {  # Cross-Correlations, UMi LOS
                ("ASD", "DS"): 0.5, ("ASA", "DS"): 0.8, ("ASA", "SF"): -0.4,
                ("ASD", "SF"): -0.5, ("DS", "SF"): -0.4, ("ASD", "ASA"): 0.4,
                ("ASD", "K"): -0.2, ("ASA", "K"): -0.3, ("DS", "K"): -0.7,
                ("SF", "K"): 0.5,
                ("ZSD", "SF"): 0.0, ("ZSA", "SF"): 0.0, ("ZSD", "K"): 0.0,
                ("ZSA", "K"): 0.0, ("ZSD", "DS"): 0.0, ("ZSA", "DS"): 0.2,
                ("ZSD", "ASD"): 0.5, ("ZSA", "ASD"): 0.3, ("ZSD", "ASA"): 0.0,
                ("ZSA", "ASA"): 0.0, ("ZSD", "ZSA"): 0.0}
            p["r_tau"] = 3.0
            p["mu_XPR"] = 9.0
            p["sigma_XPR"] = 3.0
            p["N"] = 12
            p["M"] = 20
            p["c_DS_ns"] = 5.0
            p["c_ASD"] = 3.0
            p["c_ASA"] = 17.0
            p["c_ZSA"] = 7.0
            p["zeta"] = 3.0
            # Table 7.4.1-1, UMi-Street Canyon LOS: sigma_SF = 4 dB
            p["sigma_SF"] = 4.0
            # Table 7.5-8, UMi-Street Canyon LOS (d2D in m)
            p["mu_lgZSD"] = max(-0.21, -14.8 * (d2d / 1000.0)
                                + 0.01 * abs(hut - hbs) + 0.83)
            p["sigma_lgZSD"] = 0.35
            p["mu_offset_ZOD"] = 0.0
        else:
            # Table 7.5-6 Part-1, UMi-Street Canyon, NLOS
            p["mu_lgDS"] = -0.24 * lf - 6.83
            p["sigma_lgDS"] = 0.16 * lf + 0.28
            p["mu_lgASD"] = -0.23 * lf + 1.53
            p["sigma_lgASD"] = 0.11 * lf + 0.33
            p["mu_lgASA"] = -0.08 * lf + 1.81
            p["sigma_lgASA"] = 0.05 * lf + 0.3
            p["mu_lgZSA"] = -0.04 * lf + 0.92
            p["sigma_lgZSA"] = -0.07 * lf + 0.41
            corr = {  # Cross-Correlations, UMi NLOS
                ("ASD", "DS"): 0.0, ("ASA", "DS"): 0.4, ("ASA", "SF"): -0.4,
                ("ASD", "SF"): 0.0, ("DS", "SF"): -0.7, ("ASD", "ASA"): 0.0,
                ("ZSD", "SF"): 0.0, ("ZSA", "SF"): 0.0, ("ZSD", "DS"): -0.5,
                ("ZSA", "DS"): 0.0, ("ZSD", "ASD"): 0.5, ("ZSA", "ASD"): 0.5,
                ("ZSD", "ASA"): 0.0, ("ZSA", "ASA"): 0.2, ("ZSD", "ZSA"): 0.0}
            p["r_tau"] = 2.1
            p["mu_XPR"] = 8.0
            p["sigma_XPR"] = 3.0
            p["N"] = 19
            p["M"] = 20
            p["c_DS_ns"] = 11.0
            p["c_ASD"] = 10.0
            p["c_ASA"] = 22.0
            p["c_ZSA"] = 7.0
            p["zeta"] = 3.0
            # Table 7.4.1-1, UMi-Street Canyon NLOS: sigma_SF = 7.82 dB
            p["sigma_SF"] = 7.82
            # Table 7.5-8, UMi-Street Canyon NLOS (d2D in m)
            p["mu_lgZSD"] = max(-0.5, -3.1 * (d2d / 1000.0)
                                + 0.01 * max(hut - hbs, 0.0) + 0.2)
            p["sigma_lgZSD"] = 0.35
            p["mu_offset_ZOD"] = -10.0 ** (-1.5 * math.log10(max(10.0, d2d)) + 3.3)
    elif scen == "InH":
        # Table 7.5-6 Part-2 NOTE 6 and Table 7.5-10 NOTE 4: fc < 6 GHz -> fc = 6.
        f = max(fc, 6.0)
        lf = math.log10(1.0 + f)
        if los:
            # Table 7.5-6 Part-2, Indoor-Office, LOS
            p["mu_lgDS"] = -0.01 * lf - 7.692
            p["sigma_lgDS"] = 0.18
            p["mu_lgASD"] = 1.60
            p["sigma_lgASD"] = 0.18
            p["mu_lgASA"] = -0.19 * lf + 1.781
            p["sigma_lgASA"] = 0.12 * lf + 0.119
            p["mu_lgZSA"] = -0.26 * lf + 1.44
            p["sigma_lgZSA"] = -0.04 * lf + 0.264
            p["mu_K"] = 7.0
            p["sigma_K"] = 4.0
            corr = {  # Cross-Correlations, Indoor-Office LOS
                ("ASD", "DS"): 0.6, ("ASA", "DS"): 0.8, ("ASA", "SF"): -0.5,
                ("ASD", "SF"): -0.4, ("DS", "SF"): -0.8, ("ASD", "ASA"): 0.4,
                ("ASD", "K"): 0.0, ("ASA", "K"): 0.0, ("DS", "K"): -0.5,
                ("SF", "K"): 0.5,
                ("ZSD", "SF"): 0.2, ("ZSA", "SF"): 0.3, ("ZSD", "K"): 0.0,
                ("ZSA", "K"): 0.1, ("ZSD", "DS"): 0.1, ("ZSA", "DS"): 0.2,
                ("ZSD", "ASD"): 0.5, ("ZSA", "ASD"): 0.0, ("ZSD", "ASA"): 0.0,
                ("ZSA", "ASA"): 0.5, ("ZSD", "ZSA"): 0.0}
            p["r_tau"] = 3.6
            p["mu_XPR"] = 11.0
            p["sigma_XPR"] = 4.0
            p["N"] = 15
            p["M"] = 20
            p["c_DS_ns"] = None  # N/A
            p["c_ASD"] = 5.0
            p["c_ASA"] = 8.0
            p["c_ZSA"] = 9.0
            p["zeta"] = 6.0
            # Table 7.4.1-1, InH-Office LOS: sigma_SF = 3 dB
            p["sigma_SF"] = 3.0
            # Table 7.5-10, Indoor-Office LOS
            p["mu_lgZSD"] = -1.43 * lf + 2.228
            p["sigma_lgZSD"] = 0.13 * lf + 0.30
            p["mu_offset_ZOD"] = 0.0
        else:
            # Table 7.5-6 Part-2, Indoor-Office, NLOS
            p["mu_lgDS"] = -0.28 * lf - 7.173
            p["sigma_lgDS"] = 0.10 * lf + 0.055
            p["mu_lgASD"] = 1.62
            p["sigma_lgASD"] = 0.25
            p["mu_lgASA"] = -0.11 * lf + 1.863
            p["sigma_lgASA"] = 0.12 * lf + 0.059
            p["mu_lgZSA"] = -0.15 * lf + 1.387
            p["sigma_lgZSA"] = -0.09 * lf + 0.746
            corr = {  # Cross-Correlations, Indoor-Office NLOS
                ("ASD", "DS"): 0.4, ("ASA", "DS"): 0.0, ("ASA", "SF"): -0.4,
                ("ASD", "SF"): 0.0, ("DS", "SF"): -0.5, ("ASD", "ASA"): 0.0,
                ("ZSD", "SF"): 0.0, ("ZSA", "SF"): 0.0, ("ZSD", "DS"): -0.27,
                ("ZSA", "DS"): -0.06, ("ZSD", "ASD"): 0.35, ("ZSA", "ASD"): 0.23,
                ("ZSD", "ASA"): -0.08, ("ZSA", "ASA"): 0.43, ("ZSD", "ZSA"): 0.42}
            p["r_tau"] = 3.0
            p["mu_XPR"] = 10.0
            p["sigma_XPR"] = 4.0
            p["N"] = 19
            p["M"] = 20
            p["c_DS_ns"] = None  # N/A
            p["c_ASD"] = 5.0
            p["c_ASA"] = 11.0
            p["c_ZSA"] = 9.0
            p["zeta"] = 3.0
            # Table 7.4.1-1, InH-Office NLOS: sigma_SF = 8.03 dB
            p["sigma_SF"] = 8.03
            # Table 7.5-10, Indoor-Office NLOS
            p["mu_lgZSD"] = 1.08
            p["sigma_lgZSD"] = 0.36
            p["mu_offset_ZOD"] = 0.0
    else:
        raise ValueError(scen)

    order = ["SF", "K", "DS", "ASD", "ASA", "ZSD", "ZSA"] if los else \
            ["SF", "DS", "ASD", "ASA", "ZSD", "ZSA"]
    n = len(order)
    R = np.eye(n)
    for (a, b), v in corr.items():
        i, j = order.index(a), order.index(b)
        R[i, j] = R[j, i] = v
    p["order"] = order
    p["R"] = R
    return p


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def wrap_azimuth(deg):
    return (np.asarray(deg) + 180.0) % 360.0 - 180.0


def wrap_zenith(deg):
    """(7.5-18): within [0, 360); theta in [180, 360) -> 360 - theta."""
    t = np.asarray(deg) % 360.0
    return np.where(t >= 180.0, 360.0 - t, t)


def circular_spread_deg(phi_deg, w):
    """Annex A.1, (A-1), result in degrees."""
    z = np.sum(w * np.exp(1j * np.deg2rad(phi_deg))) / np.sum(w)
    r = min(abs(z), 1.0)
    return math.degrees(math.sqrt(-2.0 * math.log(r))) if r > 0 else math.inf


def linear_spread(x, w):
    w = w / np.sum(w)
    mu = np.sum(w * x)
    return math.sqrt(max(np.sum(w * (x - mu) ** 2), 0.0))


# ---------------------------------------------------------------------------
# One drop
# ---------------------------------------------------------------------------

def one_drop(rng, p, los, geo, L, removal="7.5-8"):
    order = p["order"]
    N, M = p["N"], p["M"]

    # Step 4: correlated large-scale parameters
    s = L @ rng.standard_normal(len(order))
    sv = dict(zip(order, s))
    sf_db = p["sigma_SF"] * sv["SF"]
    k_db = p["mu_K"] + p["sigma_K"] * sv["K"] if los else float("nan")
    lg = {x: p["mu_lg" + x] + p["sigma_lg" + x] * sv[x]
          for x in ("DS", "ASD", "ASA", "ZSD", "ZSA")}
    DS = 10.0 ** lg["DS"]
    ASD = min(10.0 ** lg["ASD"], 104.0)
    ASA = min(10.0 ** lg["ASA"], 104.0)
    ZSD = min(10.0 ** lg["ZSD"], 52.0)
    ZSA = min(10.0 ** lg["ZSA"], 52.0)
    KR = 10.0 ** (k_db / 10.0) if los else 0.0

    # Step 5: cluster delays (7.5-1), (7.5-2)
    r_tau = p["r_tau"]
    X = rng.uniform(0.0, 1.0, N)
    X = np.where(X == 0.0, np.finfo(float).tiny, X)
    tau_p = -r_tau * DS * np.log(X)
    tau = np.sort(tau_p - tau_p.min())
    ctau = c_tau(k_db) if los else 1.0

    # Step 6: cluster powers (7.5-5), (7.5-6), (7.5-8)
    Z = rng.normal(0.0, p["zeta"], N)
    Pp = np.exp(-tau * (r_tau - 1.0) / (r_tau * DS)) * 10.0 ** (-Z / 10.0)
    P_nlos = Pp / Pp.sum()                      # (7.5-6)
    power_sum = float(P_nlos.sum())
    if los:
        P = P_nlos / (KR + 1.0)
        P[0] += KR / (KR + 1.0)                 # (7.5-8)
    else:
        P = P_nlos.copy()
    # -25 dB removal, on the (7.5-8) powers (default) or on the (7.5-6) ones
    ref = P if removal == "7.5-8" else P_nlos
    keep = ref >= ref.max() * 10.0 ** (-25.0 / 10.0)

    # Step 7: angles, generated for all N clusters
    rel = P / P.max()
    lnrel = np.log(rel)
    cphi = c_phi(N, los, k_db)
    cth = c_theta(N, los, k_db)

    def azimuth(spread, phi_los):
        base = 2.0 * (spread / 1.4) * np.sqrt(-lnrel) / cphi    # (7.5-9)
        Xn = rng.integers(0, 2, N) * 2 - 1
        Yn = rng.normal(0.0, spread / 7.0, N)
        a = Xn * base + Yn
        if los:
            return a - (a[0] - phi_los)                          # (7.5-12)
        return a + phi_los                                        # (7.5-11)

    def zenith(spread, theta_los, offset):
        base = -spread * lnrel / cth                              # (7.5-14)
        Xn = rng.integers(0, 2, N) * 2 - 1
        Yn = rng.normal(0.0, spread / 7.0, N)
        a = Xn * base + Yn
        if los:
            return a - (a[0] - theta_los)                        # (7.5-17)
        return a + theta_los + offset                             # (7.5-16)/(7.5-19)

    phi_aoa_n = azimuth(ASA, geo["AOA"])
    phi_aod_n = azimuth(ASD, geo["AOD"])
    th_zoa_n = zenith(ZSA, geo["ZOA"], 0.0)
    th_zod_n = zenith(ZSD, geo["ZOD"], p["mu_offset_ZOD"])

    idx = np.nonzero(keep)[0]
    Nk = idx.size
    aoa = wrap_azimuth(phi_aoa_n[idx, None] + p["c_ASA"] * ALPHA_M[None, :])   # (7.5-13)
    aod = wrap_azimuth(phi_aod_n[idx, None] + p["c_ASD"] * ALPHA_M[None, :])
    zoa = wrap_zenith(th_zoa_n[idx, None] + p["c_ZSA"] * ALPHA_M[None, :])    # (7.5-18)
    zod = wrap_zenith(th_zod_n[idx, None]
                      + (3.0 / 8.0) * 10.0 ** p["mu_lgZSD"] * ALPHA_M[None, :])  # (7.5-20)

    # Step 8: random coupling (within sub-clusters for the two strongest)
    strongest = set(idx[np.argsort(-P[idx])[:2]].tolist())
    groups_sub = [np.array(g) - 1 for g in SUBCLUSTERS]
    group_all = [np.arange(M)]
    for r, n in enumerate(idx):
        groups = groups_sub if n in strongest else group_all
        for arr in (aod, zoa, zod):
            perm = np.arange(M)
            for g in groups:
                perm[g] = g[rng.permutation(g.size)]
            arr[r, :] = arr[r, perm]

    # Step 9: XPR, Step 10: initial phases (drawn; not used below)
    _xpr = 10.0 ** (rng.normal(p["mu_XPR"], p["sigma_XPR"], (Nk, M)) / 10.0)
    _phases = rng.uniform(-math.pi, math.pi, (Nk, M, 4))

    # Step 11 powers: NLOS powers (7.5-6) scaled by 1/(KR+1), plus the LOS ray
    w_cl = P_nlos[idx] / (KR + 1.0)
    p_los = KR / (KR + 1.0) if los else 0.0
    tau_k = tau[idx]

    if los:
        tw = np.concatenate([w_cl, [p_los]])
        tt = np.concatenate([tau_k, [tau[0]]])
        tts = np.concatenate([tau_k / ctau, [tau[0] / ctau]])
    else:
        tw, tt, tts = w_cl, tau_k, tau_k
    ds = linear_spread(tts, tw)            # Step 11 delays: tau_n / C_tau in LOS
    ds_unscaled = linear_spread(tt, tw)

    wr = np.repeat(w_cl / M, M)
    if los:
        wr_all = np.concatenate([wr, [p_los]])
        ext = lambda a, v: np.concatenate([a.ravel(), [v]])
    else:
        wr_all = wr
        ext = lambda a, v: a.ravel()
    asa = circular_spread_deg(ext(aoa, geo["AOA"]), wr_all)
    asd = circular_spread_deg(ext(aod, geo["AOD"]), wr_all)
    zsa = linear_spread(ext(zoa, geo["ZOA"]), wr_all)
    zsd = linear_spread(ext(zod, geo["ZOD"]), wr_all)
    k_real = 10.0 * math.log10(p_los / w_cl.sum()) if los else float("nan")

    return {
        "ds": ds, "ds_unscaled": ds_unscaled, "asd": asd, "asa": asa,
        "zsd": zsd, "zsa": zsa, "k_db": k_real,
        "power_sum": power_sum, "power_kept": float(w_cl.sum() + p_los),
        "clusters_kept": float(Nk),
        "lsp_ds": DS, "lsp_asd": ASD, "lsp_asa": ASA, "lsp_zsd": ZSD,
        "lsp_zsa": ZSA, "lsp_k_db": k_db, "lsp_sf_db": sf_db,
        "_s": s, "_lg": lg,
    }


# ---------------------------------------------------------------------------
# Cases
# ---------------------------------------------------------------------------

CASES = {
    "UMi-LOS":  dict(scen="UMi", los=True,  fc=28.0, d2d=100.0, hbs=10.0, hut=1.5, seed=3890101),
    "UMi-NLOS": dict(scen="UMi", los=False, fc=28.0, d2d=100.0, hbs=10.0, hut=1.5, seed=3890102),
    "InH-LOS":  dict(scen="InH", los=True,  fc=28.0, d2d=20.0,  hbs=3.0,  hut=1.0, seed=3890103),
    "InH-NLOS": dict(scen="InH", los=False, fc=28.0, d2d=20.0,  hbs=3.0,  hut=1.0, seed=3890104),
}
N_DROPS = 10000
PROBS = np.arange(1, 200) * 0.005  # 0.5 %, 1.0 %, ..., 99.5 %

STATS = ["ds", "ds_unscaled", "asd", "asa", "zsd", "zsa", "k_db", "power_kept",
         "clusters_kept", "lsp_ds", "lsp_asd", "lsp_asa", "lsp_zsd", "lsp_zsa",
         "lsp_k_db", "lsp_sf_db"]


def g9(x):
    return float("%.9g" % x)


VARIANT_STATS = ["ds", "ds_unscaled", "asd", "asa", "zsd", "zsa", "k_db",
                 "power_kept", "clusters_kept"]
VARIANT_SEED_OFFSET = 100


def summarize(drops, keys):
    res = {}
    for st in keys:
        x = np.array([d[st] for d in drops], dtype=float)
        res[st] = {"quantiles": [g9(v) for v in np.quantile(x, PROBS)],
                   "mean": g9(x.mean()), "std": g9(x.std(ddof=1))}
    return res


def run_case(name, c):
    los = c["los"]
    p = scenario_parameters(c["scen"], los, c["fc"], c["d2d"], c["hbs"], c["hut"])
    zod = 90.0 + math.degrees(math.atan2(c["hbs"] - c["hut"], c["d2d"]))
    geo = {"AOD": 0.0, "AOA": 180.0, "ZOD": zod, "ZOA": 180.0 - zod}
    L = np.linalg.cholesky(p["R"])
    rng = np.random.default_rng(c["seed"])
    drops = [one_drop(rng, p, los, geo, L) for _ in range(N_DROPS)]

    out = {
        "inputs": {"scenario": c["scen"], "state": "LOS" if los else "NLOS",
                   "fc_GHz": c["fc"], "d2D_m": c["d2d"], "hBS_m": c["hbs"],
                   "hUT_m": c["hut"], "drops": N_DROPS, "seed": c["seed"]},
        "N": p["N"], "M": p["M"],
        "los_directions_deg": {k: g9(v) for k, v in geo.items()},
        "table_parameters": {k: (g9(v) if isinstance(v, float) else v)
                             for k, v in p.items() if k not in ("R", "order")},
        "correlation_order": p["order"],
        "statistics": {},
    }
    for st in STATS:
        x = np.array([d[st] for d in drops], dtype=float)
        if st == "k_db" and not los or st == "lsp_k_db" and not los:
            out["statistics"][st] = None
            continue
        out["statistics"][st] = {
            "quantiles": [g9(v) for v in np.quantile(x, PROBS)],
            "mean": g9(x.mean()), "std": g9(x.std(ddof=1)),
        }
    ps = np.array([d["power_sum"] for d in drops])
    out["statistics"]["power_sum"] = {"min": g9(ps.min()), "max": g9(ps.max())}

    # ---- checks -------------------------------------------------------------
    print(f"== {name}: N={p['N']} M={p['M']} seed={c['seed']}  "
          f"LOS dirs AOD={geo['AOD']} AOA={geo['AOA']} ZOD={zod:.4f} ZOA={180 - zod:.4f}")
    check = {}
    for x in ("DS", "ASD", "ASA", "ZSD", "ZSA"):
        v = np.array([d["_lg"][x] for d in drops])
        mu, sg = p["mu_lg" + x], p["sigma_lg" + x]
        se = sg / math.sqrt(N_DROPS)
        print(f"   lg{x:3s} (before clip): mean {v.mean():+.4f} vs mu {mu:+.4f} "
              f"({(v.mean() - mu) / se:+.2f} se), std {v.std(ddof=1):.4f} vs sigma {sg:.4f}")
        check["lg" + x] = {"mean": g9(v.mean()), "mu": g9(mu),
                           "std": g9(v.std(ddof=1)), "sigma": g9(sg)}
    sf = np.array([d["lsp_sf_db"] for d in drops])
    print(f"   SF[dB]: mean {sf.mean():+.4f} vs 0, std {sf.std(ddof=1):.4f} vs {p['sigma_SF']}")
    check["SF_dB"] = {"mean": g9(sf.mean()), "std": g9(sf.std(ddof=1)), "sigma": p["sigma_SF"]}
    if los:
        kk = np.array([d["lsp_k_db"] for d in drops])
        print(f"   K[dB]:  mean {kk.mean():+.4f} vs {p['mu_K']}, std {kk.std(ddof=1):.4f} vs {p['sigma_K']}")
        check["K_dB"] = {"mean": g9(kk.mean()), "mu": p["mu_K"],
                         "std": g9(kk.std(ddof=1)), "sigma": p["sigma_K"]}
    S = np.array([d["_s"] for d in drops])
    dR = np.abs(np.corrcoef(S.T) - p["R"]).max()
    print(f"   realized LSP correlation vs table: max |diff| = {dR:.4f}")
    check["max_abs_correlation_error"] = g9(dR)
    out["lsp_check"] = check

    if los:
        # The other reading of the -25 dB removal: threshold on the (7.5-6)
        # powers (the NLOS part alone); angles still from the (7.5-8) powers.
        vseed = c["seed"] + VARIANT_SEED_OFFSET
        vrng = np.random.default_rng(vseed)
        vd = [one_drop(vrng, p, los, geo, L, removal="7.5-6") for _ in range(N_DROPS)]
        out["variant_removal_on_7_5_6_powers"] = {"seed": vseed,
                                                  "statistics": summarize(vd, VARIANT_STATS)}
        vs = out["variant_removal_on_7_5_6_powers"]["statistics"]
        vk = np.array([d["k_db"] - d["lsp_k_db"] for d in vd])
        vck = np.array([d["clusters_kept"] for d in vd])
        print(f"   variant (removal on 7.5-6 powers, seed {vseed}): medians ds "
              f"{vs['ds']['quantiles'][99] * 1e9:.3f} ns (unscaled {vs['ds_unscaled']['quantiles'][99] * 1e9:.3f} ns), "
              f"asd {vs['asd']['quantiles'][99]:.2f}, asa {vs['asa']['quantiles'][99]:.2f}, "
              f"zsd {vs['zsd']['quantiles'][99]:.2f}, zsa {vs['zsa']['quantiles'][99]:.2f} deg, "
              f"K {vs['k_db']['quantiles'][99]:.2f} dB; clusters kept median {np.median(vck):.0f}; "
              f"realized-drawn K max {vk.max():.3f} dB")

    st = out["statistics"]
    print(f"   power_sum in [{ps.min():.17g}, {ps.max():.17g}]  "
          f"max |1 - sum| = {np.abs(ps - 1).max():.2e}")
    pk = np.array([d["power_kept"] for d in drops])
    ck = np.array([d["clusters_kept"] for d in drops])
    print(f"   power_kept: min {pk.min():.6f} median {np.median(pk):.6f}; "
          f"clusters kept: min {ck.min():.0f} median {np.median(ck):.0f} max {ck.max():.0f}")
    med = lambda k: st[k]["quantiles"][99]
    print(f"   realized medians: ds {med('ds') * 1e9:.3f} ns (unscaled {med('ds_unscaled') * 1e9:.3f} ns), "
          f"asd {med('asd'):.2f}, asa {med('asa'):.2f}, zsd {med('zsd'):.2f}, zsa {med('zsa'):.2f} deg"
          + (f", K {med('k_db'):.2f} dB" if los else ""))
    print(f"   drawn medians:    ds {med('lsp_ds') * 1e9:.3f} ns, asd {med('lsp_asd'):.2f}, "
          f"asa {med('lsp_asa'):.2f}, zsd {med('lsp_zsd'):.2f}, zsa {med('lsp_zsa'):.2f} deg")
    for k, lim in (("asd", 104), ("asa", 104), ("zsd", 52), ("zsa", 52)):
        x = np.array([d[k] for d in drops])
        print(f"   {k}: max {x.max():.2f} deg, fraction above {lim}: {(x > lim).mean():.4f}")
    if los:
        kr = np.array([d["k_db"] - d["lsp_k_db"] for d in drops])
        print(f"   realized K - drawn K: min {kr.min():.2e} max {kr.max():.2e} dB "
              f"(= -10 log10(power kept in the NLOS part))")
    return out


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    result = {
        "source": "3GPP TR 38.901 V18.0.0 clause 7.5 Steps 1-11 (cluster level), Annex A.1",
        "generator": "scenario_drop_reference.py",
        "quantile_probabilities": [g9(v) for v in PROBS],
        "units": {"ds": "s", "ds_unscaled": "s", "asd": "deg", "asa": "deg",
                  "zsd": "deg", "zsa": "deg", "k_db": "dB", "lsp_ds": "s",
                  "lsp_asd": "deg", "lsp_asa": "deg", "lsp_zsd": "deg",
                  "lsp_zsa": "deg", "lsp_k_db": "dB", "lsp_sf_db": "dB"},
        "cases": {},
    }
    for name, c in CASES.items():
        result["cases"][name] = run_case(name, c)
    path = os.path.join(here, "scenario_drop_reference.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=1, allow_nan=False)
        f.write("\n")
    print("wrote", path)


if __name__ == "__main__":
    sys.exit(main())
