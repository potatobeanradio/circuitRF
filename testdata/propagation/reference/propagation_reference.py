#!/usr/bin/env python3
"""Independent reference values for the large-scale propagation models (brief PCH-1).

Written from the source documents alone, standard library only, and deliberately not from circuitRF's code:
  ITU-R P.525-5, P.1238-13, P.1411-13, P.2108-1, P.2109-2, P.833-10, P.526-16; 3GPP TR 38.901 V18.0.0.
The two-ray, log-distance and multi-slope forms are the textbook ones the design note states.

Run:  python3 propagation_reference.py      (writes reference.csv and spreads_38901.csv beside this file)

reference.csv: one case per row - model, frequency (Hz), horizontal distance (m), transmit and receive heights (m),
the model's parameters as name=value pairs separated by ';', and the loss (dB), the shadowing sigma (dB) and the
line-of-sight probability (blank where the model gives none). Added losses are cases on a 'free' model, so the
expected value is free space plus the added term.
"""
import cmath
import csv
import math
import os
from statistics import NormalDist

C = 299792458.0
ND = NormalDist()


def log10(x):
    return math.log10(x)


def fspl(f, d):
    return 20 * log10(4 * math.pi * d * f / C)


def d3(d, h1, h2):
    return math.hypot(d, h1 - h2)


# ---------------------------------------------------------------- simple models

def logdist(f, d, n, d0):
    return fspl(f, d) if d <= d0 else fspl(f, d0) + 10 * n * log10(d / d0)


def multislope(f, d, d0, breaks, ns):
    if d <= d0:
        return fspl(f, d)
    edges = [d0] + list(breaks) + [math.inf]
    loss = fspl(f, d0)
    for k, n in enumerate(ns):
        a, b = edges[k], min(edges[k + 1], d)
        if b <= a:
            break
        loss += 10 * n * log10(b / a)
    return loss


def tworay(f, d, ht, hr, er, sigma, pol):
    lam = C / f
    k = 2 * math.pi / lam
    r1 = math.sqrt(d * d + (ht - hr) ** 2)
    r2 = math.sqrt(d * d + (ht + hr) ** 2)
    psi = math.atan((ht + hr) / d)
    epsc = complex(er, -sigma / (2 * math.pi * f * 8.8541878128e-12))
    root = cmath.sqrt(epsc - math.cos(psi) ** 2)
    if pol == 'horizontal':
        g = (math.sin(psi) - root) / (math.sin(psi) + root)
    else:
        g = (epsc * math.sin(psi) - root) / (epsc * math.sin(psi) + root)
    e = cmath.exp(-1j * k * r1) / r1 + g * cmath.exp(-1j * k * r2) / r2
    return -20 * log10(lam / (4 * math.pi) * abs(e))


# ---------------------------------------------------------------- TR 38.901

def pl_38901(sc, los, f, d2, hbs, hut, W=20.0, h=5.0):
    fc = f / 1e9
    dd = math.sqrt(d2 ** 2 + (hbs - hut) ** 2)
    if sc == 'RMa':
        dbp = 2 * math.pi * hbs * hut * f / 3e8

        def pl1(x):
            return (20 * log10(40 * math.pi * x * fc / 3) + min(0.03 * h ** 1.72, 10) * log10(x)
                    - min(0.044 * h ** 1.72, 14.77) + 0.002 * log10(h) * x)
        if d2 <= dbp:
            lo, slo = pl1(dd), 4.0
        else:
            lo, slo = pl1(dbp) + 40 * log10(dd / dbp), 6.0
        if los:
            return lo, slo
        n = (161.04 - 7.1 * log10(W) + 7.5 * log10(h) - (24.37 - 3.7 * (h / hbs) ** 2) * log10(hbs)
             + (43.42 - 3.1 * log10(hbs)) * (log10(dd) - 3) + 20 * log10(fc)
             - (3.2 * (log10(11.75 * hut)) ** 2 - 4.97))
        return max(lo, n), 8.0
    if sc in ('UMa', 'UMi'):
        dbp = 4 * (hbs - 1) * (hut - 1) * f / 3e8
        if sc == 'UMa':
            if d2 <= dbp:
                lo = 28.0 + 22 * log10(dd) + 20 * log10(fc)
            else:
                lo = 28.0 + 40 * log10(dd) + 20 * log10(fc) - 9 * log10(dbp ** 2 + (hbs - hut) ** 2)
            if los:
                return lo, 4.0
            return max(lo, 13.54 + 39.08 * log10(dd) + 20 * log10(fc) - 0.6 * (hut - 1.5)), 6.0
        if d2 <= dbp:
            lo = 32.4 + 21 * log10(dd) + 20 * log10(fc)
        else:
            lo = 32.4 + 40 * log10(dd) + 20 * log10(fc) - 9.5 * log10(dbp ** 2 + (hbs - hut) ** 2)
        if los:
            return lo, 4.0
        return max(lo, 35.3 * log10(dd) + 22.4 + 21.3 * log10(fc) - 0.3 * (hut - 1.5)), 7.82
    if sc.startswith('InH'):
        lo = 32.4 + 17.3 * log10(dd) + 20 * log10(fc)
        if los:
            return lo, 3.0
        return max(lo, 38.3 * log10(dd) + 17.30 + 24.9 * log10(fc)), 8.03
    lo = 31.84 + 21.50 * log10(dd) + 19.00 * log10(fc)
    if los or sc == 'InF-HH':
        return lo, 4.3
    sl = 33 + 25.5 * log10(dd) + 20 * log10(fc)
    if sc == 'InF-SL':
        return max(sl, lo), 5.7
    if sc == 'InF-DL':
        return max(18.6 + 35.7 * log10(dd) + 20 * log10(fc), lo, sl), 7.2
    if sc == 'InF-SH':
        return max(32.4 + 23.0 * log10(dd) + 20 * log10(fc), lo), 5.9
    return max(33.63 + 21.9 * log10(dd) + 20 * log10(fc), lo), 4.0


INF = {  # density, clutter height, clutter size, hall L, W, H
    'InF-SL': (0.2, 2, 10, 120, 60, 10), 'InF-DL': (0.6, 6, 2, 300, 150, 10), 'InF-SH': (0.2, 2, 10, 300, 150, 10),
    'InF-DH': (0.6, 6, 2, 120, 60, 10), 'InF-HH': (0.2, 2, 10, 120, 60, 10),
}


def plos_38901(sc, d, hbs, hut):
    if sc == 'RMa':
        return 1.0 if d <= 10 else math.exp(-(d - 10) / 1000)
    if sc == 'UMi':
        return 1.0 if d <= 18 else 18 / d + math.exp(-d / 36) * (1 - 18 / d)
    if sc == 'UMa':
        if d <= 18:
            return 1.0
        cp = 0.0 if hut <= 13 else ((hut - 13) / 10) ** 1.5
        return (18 / d + math.exp(-d / 63) * (1 - 18 / d)) * (1 + cp * 1.25 * (d / 100) ** 3 * math.exp(-d / 150))
    if sc == 'InH-Mixed':
        if d <= 1.2:
            return 1.0
        return math.exp(-(d - 1.2) / 4.7) if d < 6.5 else math.exp(-(d - 6.5) / 32.6) * 0.32
    if sc == 'InH-Open':
        if d <= 5:
            return 1.0
        return math.exp(-(d - 5) / 70.8) if d <= 49 else math.exp(-(d - 49) / 211.7) * 0.54
    if sc == 'InF-HH':
        return 1.0
    r, hc, dc = INF[sc][0], INF[sc][1], INF[sc][2]
    k = -dc / math.log(1 - r)
    if sc in ('InF-SH', 'InF-DH'):
        k *= (hbs - hut) / (hc - hut)
    return math.exp(-d / k)


def spreads_38901(sc, los, f, d2, hbs, hut):
    fc = f / 1e9
    if sc == 'UMa':
        g = log10(max(fc, 6))
        if los:
            return [-6.955 - 0.0963 * g, 0.66, 1.06 + 0.1114 * g, 0.28, 1.81, 0.20, 0.95, 0.16,
                    max(-0.5, -2.1 * d2 / 1000 - 0.01 * (hut - 1.5) + 0.75), 0.40, 9, 3.5]
        return [-6.28 - 0.204 * g, 0.39, 1.5 - 0.1144 * g, 0.28, 2.08 - 0.27 * g, 0.11, -0.3236 * g + 1.512, 0.16,
                max(-0.5, -2.1 * d2 / 1000 - 0.01 * (hut - 1.5) + 0.9), 0.49, '', '']
    if sc == 'UMi':
        g = log10(1 + max(fc, 2))
        if los:
            return [-0.24 * g - 7.14, 0.38, -0.05 * g + 1.21, 0.41, -0.08 * g + 1.73, 0.014 * g + 0.28,
                    -0.1 * g + 0.73, -0.04 * g + 0.34, max(-0.21, -14.8 * d2 / 1000 + 0.01 * abs(hut - hbs) + 0.83), 0.35, 9, 5]
        return [-0.24 * g - 6.83, 0.16 * g + 0.28, -0.23 * g + 1.53, 0.11 * g + 0.33, -0.08 * g + 1.81, 0.05 * g + 0.3,
                -0.04 * g + 0.92, -0.07 * g + 0.41, max(-0.5, -3.1 * d2 / 1000 + 0.01 * max(hut - hbs, 0) + 0.2), 0.35, '', '']
    if sc == 'RMa':
        if los:
            return [-7.49, 0.55, 0.90, 0.38, 1.52, 0.24, 0.47, 0.40, max(-1, -0.17 * d2 / 1000 - 0.01 * (hut - 1.5) + 0.22), 0.34, 7, 4]
        return [-7.43, 0.48, 0.95, 0.45, 1.52, 0.13, 0.58, 0.37, max(-1, -0.19 * d2 / 1000 - 0.01 * (hut - 1.5) + 0.28), 0.30, '', '']
    if sc.startswith('InH'):
        g = log10(1 + max(fc, 6))
        if los:
            return [-0.01 * g - 7.692, 0.18, 1.60, 0.18, -0.19 * g + 1.781, 0.12 * g + 0.119, -0.26 * g + 1.44, -0.04 * g + 0.264,
                    -1.43 * g + 2.228, 0.13 * g + 0.30, 7, 4]
        return [-0.28 * g - 7.173, 0.10 * g + 0.055, 1.62, 0.25, -0.11 * g + 1.863, 0.12 * g + 0.059, -0.15 * g + 1.387,
                -0.09 * g + 0.746, 1.08, 0.36, '', '']
    L, W, H = INF[sc][3:]
    vs = L * W * H / (2 * (L * W + L * H + W * H))
    g = log10(1 + fc)
    if los or sc == 'InF-HH':
        return [log10(26 * vs + 14) - 9.35, 0.15, 1.56, 0.25, -0.18 * g + 1.78, 0.12 * g + 0.2, -0.2 * g + 1.5, 0.35, 1.35, 0.35, 7, 8]
    return [log10(30 * vs + 32) - 9.44, 0.19, 1.57, 0.2, 1.72, 0.3, -0.13 * g + 1.45, 0.45, 1.2, 0.55, '', '']


# ---------------------------------------------------------------- P.1238

P1238 = {('office', 1): (1.47, 34.17, 2.08, 3.68), ('office', 0): (2.39, 30.13, 2.40, 5.01),
         ('corridor', 1): (1.57, 29.46, 2.24, 3.77), ('corridor', 0): (2.78, 28.62, 2.54, 7.58),
         ('industrial', 1): (2.27, 24.79, 2.10, 2.62), ('industrial', 0): (2.80, 23.55, 2.16, 5.70),
         ('conference', 1): (1.56, 30.47, 2.23, 2.92), ('conference', 0): (1.40, 39.53, 2.37, 3.33)}


def p1238(env, los, f, d, floor_loss=0.0):
    a, b, g, s = P1238[(env, los)]
    return 10 * a * log10(d) + b + 10 * g * log10(f / 1e9) + floor_loss, s


# ---------------------------------------------------------------- P.1411

def p1411_site(table, f, d):
    a, b, g, s = table
    return 10 * a * log10(d) + b + 10 * g * log10(f / 1e9), s


def p1411_low(f, d, p, lurban):
    fm = f / 1e6
    los = lambda x: 32.45 + 20 * log10(fm) + 20 * log10(x / 1000) + 1.5624 * 7 * (math.sqrt(-2 * math.log(1 - p / 100)) - 1.1774)
    nlos = lambda x: 9.5 + 45 * log10(fm) + 40 * log10(x / 1000) + lurban + 7 * ND.inv_cdf(p / 100)
    q = p / 100
    dlos = 212 * log10(q) ** 2 - 64 * log10(q) if p < 45 else 79.2 - 70 * q
    w = 20
    if d < dlos:
        return los(d)
    if d > dlos + w:
        return nlos(d)
    return los(dlos) + (nlos(dlos + w) - los(dlos)) * (d - dlos) / w


def p1411_corner(f, x1, x2, w1, w2, alpha_deg, residential=False, chamfer=False, h1=1.5, h2=1.5, hs=0.0, n=2.0):
    fm = f / 1e6
    lam = C / f
    if fm <= 2000:
        a = math.radians(alpha_deg)
        lr = 20 * log10(x1 + x2) + x1 * x2 * (3.86 / a ** 3.5) / (w1 * w2) + 20 * log10(4 * math.pi / lam)
        da = (40 / (2 * math.pi)) * (math.atan(x2 / w2) + math.atan(x1 / w1) - math.pi / 2)
        ld = 10 * log10(x1 * x2 * (x1 + x2)) + 2 * da - 0.1 * (90 - alpha_deg) + 20 * log10(4 * math.pi / lam)
        return -10 * log10(10 ** (-lr / 10) + 10 ** (-ld / 10))
    # line-of-sight street, P.1411 sec. 4.1.2 median
    if f > 15e9:
        llos = 20 * log10(fm) - 28 + 10 * n * log10(x1)
    else:
        rbp = 4 * (h1 - hs) * (h2 - hs) / lam
        lbp = abs(20 * log10(lam ** 2 / (8 * math.pi * (h1 - hs) * (h2 - hs))))
        llos = lbp + 6 + (20 * log10(x1 / rbp) if x1 <= rbp else 40 * log10(x1 / rbp))
    dc = 30.0
    lcorner = 30.0 if residential else 20.0
    beta = 4.2 + (1.4 * log10(fm) - 7.8) * (0.8 * log10(x1) - 1.0) if chamfer and not residential else 6.0
    if x2 <= w1 / 2 + 1 + dc:
        lc = lcorner / log10(1 + dc) * log10(x2 - w1 / 2)
        latt = 0.0
    else:
        lc = lcorner
        latt = 10 * beta * log10((x1 + x2) / (x1 + w1 / 2 + dc))
    return llos + lc + latt


# ---------------------------------------------------------------- P.2108, P.2109, P.833

def qinv(x):
    return -ND.inv_cdf(x)


def jnu(v):
    return 0.0 if v <= -0.78 else 6.9 + 20 * log10(math.sqrt((v - 0.1) ** 2 + 1) + v - 0.1)


def p2108_terminal(f, h, ctype, R=None, ws=27.0):
    fg = f / 1e9
    if R is None:
        R = {'urban': 15.0, 'denseurban': 20.0}.get(ctype, 10.0)
    if h >= R:
        return 0.0
    if ctype in ('water', 'open'):
        return -(21.8 + 6.2 * log10(fg)) * log10(h / R)
    hdif = R - h
    theta = math.degrees(math.atan(hdif / ws))
    return jnu(0.342 * math.sqrt(fg) * math.sqrt(hdif * theta)) - 6.03


def p2108_terrestrial(f, dkm, p):
    fg = f / 1e9

    def lctt(dk):
        ll = -2 * log10(10 ** (-5 * log10(fg) - 12.5) + 10 ** (-16.5))
        ls = 32.98 + 23.9 * log10(dk) + 3 * log10(fg)
        sl, ss = 4.0, 6.0
        a, b = 10 ** (-0.2 * ll), 10 ** (-0.2 * ls)
        scb = math.sqrt((sl ** 2 * a + ss ** 2 * b) / (a + b))
        return -5 * log10(a + b) - scb * qinv(p / 100)
    return min(lctt(dkm), lctt(2.0))


def p2108_earthspace(f, theta, p):
    fg = f / 1e9
    k1, a1 = 93 * fg ** 0.175, 0.05
    inner = -k1 * math.log(1 - p / 100) / math.tan(a1 * (1 - theta / 90) + math.pi * theta / 180)
    return inner ** (0.5 * (90 - theta) / 90) - 1 - 0.6 * qinv(p / 100)


def p2109(f, P, building, theta):
    fg = f / 1e9
    if building == 'traditional':
        r, s, t, u, v, w, x, y, z = 12.64, 3.72, 0.96, 9.6, 2.0, 9.1, -3.0, 4.5, -2.0
    else:
        r, s, t, u, v, w, x, y, z = 28.19, -3.00, 8.48, 13.5, 3.8, 27.8, -2.9, 9.4, -2.1
    lf = log10(fg)
    mu1 = r + s * lf + t * lf ** 2 + 0.212 * abs(theta)
    mu2 = w + x * lf
    s1, s2 = u + v * lf, y + z * lf
    fi = ND.inv_cdf(P)
    A, B = fi * s1 + mu1, fi * s2 + mu2
    return 10 * log10(10 ** (0.1 * A) + 10 ** (0.1 * B) + 10 ** (0.1 * -3.0))


def p833(f, depth, gamma=None, am=None):
    fm = f / 1e6
    if gamma is None:
        tf = [105.9, 466.475, 949.0, 1852.2, 2117.5]
        tg = [0.04, 0.12, 0.17, 0.30, 0.34]
        i = max(k for k in range(len(tf) - 1) if tf[k] <= fm)
        t = math.log(fm / tf[i]) / math.log(tf[i + 1] / tf[i])
        gamma = math.exp(math.log(tg[i]) + t * math.log(tg[i + 1] / tg[i]))
    if am is None:
        am = 1.15 * fm ** 0.43
    return am * (1 - math.exp(-depth * gamma / am))


# ---------------------------------------------------------------- P.526

def fresnel(v, n=20000):
    """C(v), S(v) by composite Simpson integration, independent of the recommendation's coefficient expansion."""
    if v == 0:
        return 0.0, 0.0
    h = v / n
    c = s = 0.0
    for k in range(n + 1):
        t = k * h
        wgt = 1 if k in (0, n) else (4 if k % 2 else 2)
        c += wgt * math.cos(math.pi * t * t / 2)
        s += wgt * math.sin(math.pi * t * t / 2)
    return c * h / 3, s * h / 3


def knife_edge(f, h, d1, d2):
    lam = 3e8 / f
    v = h * math.sqrt(2 / lam * (1 / d1 + 1 / d2))
    c, s = fresnel(v)
    j = -20 * log10(math.sqrt((1 - c - s) ** 2 + (c - s) ** 2) / 2)
    return fspl(f, d1 + d2) + j


def sphere_first(lam, d, h1, h2, a, er, sigma, vertical):
    kh = (2 * math.pi * a / lam) ** (-1 / 3) * ((er - 1) ** 2 + (60 * lam * sigma) ** 2) ** -0.25
    k = kh * math.sqrt(er ** 2 + (60 * lam * sigma) ** 2) if vertical else kh
    fmhz = 3e8 / lam / 1e6
    if (not vertical) or fmhz > (300 if sigma >= 1 else 20):
        beta = 1.0
    else:
        beta = (1 + 1.6 * k ** 2 + 0.67 * k ** 4) / (1 + 4.5 * k ** 2 + 1.53 * k ** 4)
    X = beta * (math.pi / (lam * a * a)) ** (1 / 3) * d
    ys = 2 * beta * (math.pi ** 2 / (lam ** 2 * a)) ** (1 / 3)
    F = 11 + 10 * log10(X) - 17.6 * X if X >= 1.6 else -20 * log10(X) - 5.6488 * X ** 1.425

    def G(y):
        B = beta * y
        g = 17.6 * math.sqrt(B - 1.1) - 5 * log10(B - 1.1) - 8 if B > 2 else 20 * log10(B + 0.1 * B ** 3)
        return max(g, 2 + 20 * log10(k))
    return -(F + G(ys * h1) + G(ys * h2))


def sphere(f, d, h1, h2, a, er, sigma, vertical):
    lam = 3e8 / f
    if d >= math.sqrt(2 * a) * (math.sqrt(h1) + math.sqrt(h2)):
        return sphere_first(lam, d, h1, h2, a, er, sigma, vertical)
    c = (h1 - h2) / (h1 + h2)
    m = d * d / (4 * a * (h1 + h2))
    b = 2 * math.sqrt((m + 1) / (3 * m)) * math.cos(math.pi / 3 + math.acos(1.5 * c * math.sqrt(3 * m / (m + 1) ** 3)) / 3)
    d1 = d / 2 * (1 + b)
    d2 = d - d1
    hc = ((h1 - d1 * d1 / (2 * a)) * d2 + (h2 - d2 * d2 / (2 * a)) * d1) / d
    hreq = 0.552 * math.sqrt(d1 * d2 * lam / d)
    if hc > hreq:
        return 0.0
    aem = 0.5 * (d / (math.sqrt(h1) + math.sqrt(h2))) ** 2
    ah = sphere_first(lam, d, h1, h2, aem, er, sigma, vertical)
    return 0.0 if ah < 0 else (1 - hc / hreq) * ah


# ---------------------------------------------------------------- the cases

def main():
    here = os.path.dirname(os.path.abspath(__file__))
    rows = []

    def add(case, model, f, d, ht, hr, params, loss, sigma=0.0, plos=''):
        rows.append([case, model, repr(f), repr(d), repr(ht), repr(hr), ';'.join(f'{k}={v}' for k, v in params.items()),
                     repr(loss), repr(sigma), '' if plos == '' else repr(plos)])

    add('free', 'free', 2.4e9, 100.0, 1.5, 1.5, {}, fspl(2.4e9, 100.0))
    add('logdist', 'logdist', 2.4e9, 50.0, 1.5, 1.5, {'n': 3.5, 'd0': 1, 'Sigma': 6}, logdist(2.4e9, 50.0, 3.5, 1.0), 6.0)
    for d in (50.0, 500.0):
        add(f'multislope {d:g} m', 'multislope', 2.4e9, d, 1.5, 1.5, {'Breaks': '10,100', 'Exponents': '2,3,4', 'd0': 1},
            multislope(2.4e9, d, 1.0, [10, 100], [2, 3, 4]))
    for pol in ('vertical', 'horizontal'):
        add(f'tworay {pol}', 'tworay', 900e6, 1000.0, 30.0, 1.5, {'GroundEr': 15, 'GroundSigma': 0.005, 'Polarization': pol},
            tworay(900e6, 1000.0, 30.0, 1.5, 15, 0.005, pol))

    def c38(name, sc, los, f, d, hb, hu, plos_word=None):
        word = plos_word or ('los' if los else 'nlos')
        pl, s = pl_38901(sc, los, f, d, hb, hu)
        add(f'38901 {name}', '38901', f, d, hb, hu, {'Scenario': sc, 'Los': word}, pl, s, plos_38901(sc, d, hb, hu))
    c38('UMa LOS before breakpoint', 'UMa', True, 3.5e9, 100.0, 25.0, 1.5)
    c38('UMa LOS beyond breakpoint', 'UMa', True, 3.5e9, 1500.0, 25.0, 1.5)
    c38('UMa NLOS', 'UMa', False, 28e9, 300.0, 25.0, 1.5)
    pa = plos_38901('UMa', 300.0, 25.0, 18.0)
    c38('UMa auto, high UT', 'UMa', pa >= 0.5, 3.5e9, 300.0, 25.0, 18.0, 'auto')
    c38('UMi LOS before breakpoint', 'UMi', True, 3.5e9, 50.0, 10.0, 1.5)
    c38('UMi LOS beyond breakpoint', 'UMi', True, 3.5e9, 900.0, 10.0, 1.5)
    c38('UMi NLOS', 'UMi', False, 28e9, 200.0, 10.0, 1.5)
    c38('RMa LOS before breakpoint', 'RMa', True, 3.5e9, 500.0, 35.0, 1.5)
    c38('RMa LOS beyond breakpoint', 'RMa', True, 3.5e9, 6000.0, 35.0, 1.5)
    c38('RMa NLOS', 'RMa', False, 3.5e9, 2000.0, 35.0, 1.5)
    c38('InH-Mixed LOS', 'InH-Mixed', True, 28e9, 10.0, 3.0, 1.0)
    c38('InH-Mixed NLOS', 'InH-Mixed', False, 60e9, 40.0, 3.0, 1.0)
    po = plos_38901('InH-Open', 30.0, 3.0, 1.0)
    c38('InH-Open auto', 'InH-Open', po >= 0.5, 28e9, 30.0, 3.0, 1.0, 'auto')
    c38('InF-SL NLOS', 'InF-SL', False, 3.5e9, 50.0, 1.5, 1.5)
    c38('InF-DL NLOS', 'InF-DL', False, 28e9, 50.0, 1.5, 1.5)
    c38('InF-SH NLOS', 'InF-SH', False, 3.5e9, 80.0, 8.0, 1.5)
    c38('InF-DH NLOS', 'InF-DH', False, 28e9, 80.0, 8.0, 1.5)
    c38('InF-HH auto', 'InF-HH', True, 28e9, 100.0, 8.0, 6.0, 'auto')

    def c1238(name, env, los, f, d, floors=0, building='office', lf=0.0):
        l, s = p1238(env, los, f, d3(d, 2.0, 1.5), lf)
        params = {'Environment': env, 'Los': 'los' if los else 'nlos'}
        if floors:
            params.update({'Floors': floors, 'Building': building})
        add(f'P1238 {name}', 'P1238', f, d, 2.0, 1.5, params, l, s)
    c1238('office LoS', 'office', 1, 2.4e9, 10.0)
    c1238('corridor NLoS', 'corridor', 0, 28e9, 50.0)
    c1238('industrial NLoS', 'industrial', 0, 5.8e9, 30.0)
    c1238('conference LoS', 'conference', 1, 60e9, 10.0)
    c1238('office, 2 floors at 1.9 GHz', 'office', 0, 1.9e9, 20.0, 2, 'office', 15 + 4 * (2 - 1))
    c1238('commercial, 3 floors at 1.8 GHz', 'office', 0, 1.8e9, 20.0, 3, 'commercial', 6 + 3 * (3 - 1))
    c1238('residential, 1 floor at 3.5 GHz (office value)', 'office', 0, 3.5e9, 20.0, 1, 'residential', 18.0)

    def c1411(name, mode, env, los, f, d, table, ht=1.5, hr=1.5):
        l, s = p1411_site(table, f, d3(d, ht, hr))
        add(f'P1411 {name}', 'P1411', f, d, ht, hr, {'Mode': mode, 'Environment': env, 'Los': 'los' if los else 'nlos'}, l, s)
    c1411('below roof LoS', 'belowroof', 'urbanlowrise', 1, 28e9, 200.0, (2.07, 31.23, 2.06, 4.91))
    c1411('below roof NLoS high-rise', 'belowroof', 'urbanhighrise', 0, 3.5e9, 300.0, (3.73, 16.02, 2.26, 7.62))
    c1411('below roof NLoS residential', 'belowroof', 'residential', 0, 5e9, 100.0, (3.01, 18.8, 2.07, 3.07))
    c1411('below roof NLoS suburban, 200 GHz', 'belowroof', 'suburban', 0, 200e9, 70.0, (4.52, 6.04, 2.14, 8.02))
    c1411('above roof LoS', 'aboveroof', 'urbanlowrise', 1, 28e9, 500.0, (2.29, 28.6, 1.96, 3.48), 25.0, 1.5)
    c1411('above roof NLoS high-rise', 'aboveroof', 'urbanhighrise', 0, 3.5e9, 500.0, (4.39, -6.27, 2.30, 6.89), 25.0, 1.5)
    for name, d, p, urban, lu in (('LoS region', 30.0, 50, 'urban', 6.8), ('corner transition', 50.0, 50, 'urban', 6.8),
                                  ('NLoS region', 200.0, 50, 'urban', 6.8), ('10 %, suburban', 200.0, 10, 'suburban', 0.0)):
        add(f'P1411 street level {name}', 'P1411', 400e6, d, 2.0, 2.0, {'Mode': 'lowheight', 'Urban': urban, 'Locations': p},
            p1411_low(400e6, d3(d, 2.0, 2.0), p, lu), 7.0)
    for name, f, x1, x2, w1, w2, res, ch in (('1.5 GHz', 1.5e9, 100.0, 50.0, 20.0, 20.0, False, False),
                                             ('10 GHz in the corner region', 10e9, 100.0, 40.0, 20.0, 10.0, False, False),
                                             ('28 GHz beyond', 28e9, 100.0, 60.0, 20.0, 10.0, False, False),
                                             ('5 GHz chamfered', 5e9, 100.0, 100.0, 20.0, 10.0, False, True),
                                             ('5 GHz residential', 5e9, 100.0, 100.0, 20.0, 10.0, True, False)):
        add(f'P1411 corner {name}', 'P1411', f, 120.0, 6.0, 1.5,
            {'Mode': 'corner', 'Environment': 'residential' if res else 'urbanlowrise', 'X1': x1, 'X2': x2, 'W1': w1, 'W2': w2,
             'Chamfered': 'yes' if ch else 'no'},
            p1411_corner(f, x1, x2, w1, w2, 90.0, res, ch, 6.0, 1.5))

    f0, dd, ht, hr = 1e9, 2000.0, 30.0, 5.0
    base = fspl(f0, d3(dd, ht, hr))
    add('P2108 terminal, urban', 'free', f0, dd, ht, hr, {'Clutter': 'terminal', 'ClutterType': 'urban'}, base + p2108_terminal(f0, hr, 'urban'))
    add('P2108 terminal, open', 'free', f0, dd, ht, hr, {'Clutter': 'terminal', 'ClutterType': 'open'}, base + p2108_terminal(f0, hr, 'open'))
    f3 = 3e9
    add('P2108 terrestrial, 2 km, median', 'free', f3, 2000.0, 30.0, 5.0, {'Clutter': 'terrestrial'},
        fspl(f3, d3(2000.0, 30.0, 5.0)) + p2108_terrestrial(f3, 2.0, 50))
    add('P2108 terrestrial, 5 km, 10 %', 'free', f3, 5000.0, 30.0, 5.0, {'Clutter': 'terrestrial', 'ClutterLocations': 10},
        fspl(f3, d3(5000.0, 30.0, 5.0)) + p2108_terrestrial(f3, 5.0, 10))
    f30 = 30e9
    add('P2108 Earth-space', 'free', f30, 1000.0, 30.0, 5.0, {'Clutter': 'earthspace', 'ClutterElevation': 30},
        fspl(f30, d3(1000.0, 30.0, 5.0)) + p2108_earthspace(f30, 30, 50))
    add('P2109 traditional', 'free', 3.5e9, 500.0, 30.0, 5.0, {'BuildingEntry': 'traditional'},
        fspl(3.5e9, d3(500.0, 30.0, 5.0)) + p2109(3.5e9, 0.5, 'traditional', 0))
    add('P2109 efficient', 'free', 28e9, 500.0, 30.0, 5.0, {'BuildingEntry': 'efficient', 'BuildingProbability': 0.9, 'FacadeElevation': 20},
        fspl(28e9, d3(500.0, 30.0, 5.0)) + p2109(28e9, 0.9, 'efficient', 20))
    add('P833 woodland, defaults', 'free', 950e6, 3000.0, 30.0, 2.0, {'Vegetation': 'on', 'VegDepth': 50},
        fspl(950e6, d3(3000.0, 30.0, 2.0)) + p833(950e6, 50))
    add('P833 woodland, stated', 'free', 3.6e9, 3000.0, 30.0, 2.0, {'Vegetation': 'on', 'VegDepth': 200, 'VegGamma': 0.5, 'VegMax': 46},
        fspl(3.6e9, d3(3000.0, 30.0, 2.0)) + p833(3.6e9, 200, 0.5, 46))
    add('partitions, polarization, pointing', 'free', 5e9, 20.0, 2.0, 2.0, {'Partitions': '3,5', 'PolLoss': 3, 'PointingLoss': 1},
        fspl(5e9, 20.0) + 3 + 5 + 3 + 1)

    for name, h in (('above the line', 20.0), ('below the line', -5.0)):
        add(f'P526 knife-edge {name}', 'P526', 1e9, 10000.0, 30.0, 30.0, {'Mode': 'knifeedge', 'EdgeHeight': h, 'EdgeDistance': 4000},
            knife_edge(1e9, h, 4000.0, 6000.0))
    k = 4.0 / 3.0
    for name, f, d, h1, h2, vert, er, sg in (('beyond the horizon', 1e9, 60000.0, 50.0, 20.0, True, 22.0, 0.003),
                                             ('within line of sight', 1e9, 30000.0, 50.0, 20.0, True, 22.0, 0.003),
                                             ('15 MHz vertical, beta from K', 15e6, 80000.0, 30.0, 10.0, True, 22.0, 0.003),
                                             ('horizontal over sea', 100e6, 70000.0, 30.0, 10.0, False, 80.0, 5.0)):
        add(f'P526 sphere {name}', 'P526', f, d, h1, h2,
            {'Mode': 'sphere', 'EarthRadiusFactor': repr(k), 'GroundEr': er, 'GroundSigma': sg, 'Polarization': 'vertical' if vert else 'horizontal'},
            fspl(f, d) + sphere(f, d, h1, h2, 6371e3 * k, er, sg, vert))

    with open(os.path.join(here, 'reference.csv'), 'w', newline='') as o:
        o.write('# Independent reference values for the propagation models; written by propagation_reference.py beside it.\n')
        w = csv.writer(o, lineterminator='\n')
        w.writerow(['case', 'model', 'f_Hz', 'd_m', 'htx_m', 'hrx_m', 'params', 'loss_dB', 'sigma_dB', 'plos'])
        w.writerows(rows)

    srows = []
    for sc, los, f, d, hb, hu in (('UMa', 1, 3.5e9, 200.0, 25.0, 1.5), ('UMa', 0, 28e9, 200.0, 25.0, 1.5),
                                  ('UMi', 1, 1.0e9, 100.0, 10.0, 1.5), ('UMi', 0, 28e9, 100.0, 10.0, 15.0),
                                  ('RMa', 1, 3.5e9, 1000.0, 35.0, 1.5), ('RMa', 0, 3.5e9, 1000.0, 35.0, 1.5),
                                  ('InH-Mixed', 1, 3.5e9, 20.0, 3.0, 1.0), ('InH-Open', 0, 60e9, 20.0, 3.0, 1.0),
                                  ('InF-SL', 1, 28e9, 50.0, 1.5, 1.5), ('InF-DH', 0, 3.5e9, 50.0, 8.0, 1.5)):
        srows.append([sc, 'los' if los else 'nlos', repr(f), repr(d), repr(hb), repr(hu)] +
                     [x if x == '' else repr(float(x)) for x in spreads_38901(sc, los, f, d, hb, hu)])
    with open(os.path.join(here, 'spreads_38901.csv'), 'w', newline='') as o:
        o.write('# TR 38.901 large-scale parameter statistics; written by propagation_reference.py beside it.\n')
        w = csv.writer(o, lineterminator='\n')
        w.writerow(['scenario', 'los', 'f_Hz', 'd2D_m', 'hBS_m', 'hUT_m', 'DS_mu', 'DS_sigma', 'ASD_mu', 'ASD_sigma', 'ASA_mu', 'ASA_sigma',
                    'ZSA_mu', 'ZSA_sigma', 'ZSD_mu', 'ZSD_sigma', 'K_mu', 'K_sigma'])
        w.writerows(srows)
    print(len(rows), 'cases,', len(srows), 'spread rows')


if __name__ == '__main__':
    main()
