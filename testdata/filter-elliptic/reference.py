"""Independent reference for circuitRF's elliptic / inverse-Chebyshev LC ladders.

Nothing here reads, imports or calls circuitRF. It is a second implementation of the same
mathematics by a DIFFERENT route, in 120-digit arithmetic:

* the selectivity modulus from the nome with mpmath's own theta functions;
* the transmission / reflection zeros from mpmath's own Jacobi `cd`;
* types b and c by the Moebius map in x = Omega^2 (the definition of those types);
* the poles by a polynomial root finder on D(x)^2 + eps^2 c^2 x^r N(x)^2 -- not by the
  closed-form complex-argument Jacobi formula the C# uses;
* the ladder by classical Darlington zero shifting on POLYNOMIALS (exact divisions by
  s^2 + w^2) -- not by the point evaluation the C# uses.

Output: one CSV row per specification, normalised to 1 rad/s and a 1 ohm source. The element
list is in ladder order from the source, `L` or `C`, the order circuitRF's lowpass
denormalisation places them (a tank or a stub is its inductor then its capacitor).

    python -I reference.py > elliptic-ladders.csv
"""

import itertools
import sys

import mpmath as mp

mp.mp.dps = 120

ORDERS = range(3, 22)
RIPPLES = ["0.01", "0.1", "0.5", "1"]
ASTOPS = ["20", "40", "60", "80"]
MIN_TRANSITION = mp.mpf("1e-3")   # skip a mask whose stop edge is within 0.1 % of the pass edge
TINY = mp.mpf("1e-45")


def eps_db(db):
    return mp.sqrt(mp.power(10, mp.mpf(db) / 10) - 1)


# ---- polynomials: lists of mpf, DESCENDING powers -------------------------------------------

def chop(p):
    """Drops leading coefficients that are cancellation residue, relative to the largest."""
    big = max(abs(c) for c in p) if p else 0
    i = 0
    while i < len(p) - 1 and abs(p[i]) <= TINY * big:
        i += 1
    return p[i:]


def padd(a, b):
    n = max(len(a), len(b))
    a = [mp.mpf(0)] * (n - len(a)) + list(a)
    b = [mp.mpf(0)] * (n - len(b)) + list(b)
    return [x + y for x, y in zip(a, b)]


def pscale(a, k):
    return [x * k for x in a]


def pmul(a, b):
    r = [mp.mpf(0)] * (len(a) + len(b) - 1)
    for i, x in enumerate(a):
        for j, y in enumerate(b):
            r[i + j] += x * y
    return r


def pmuls(a):
    return list(a) + [mp.mpf(0)]


def peval(p, s):
    v = mp.mpc(0)
    for c in p:
        v = v * s + c
    return v


def pdiv_quad(p, w2):
    """p / (s^2 + w2): the quotient, and the remainder's size relative to p."""
    scale = max(abs(c) for c in p) or 1
    p = list(p)
    q = []
    for i in range(len(p) - 2):
        c = p[i]
        q.append(c)
        p[i + 2] -= c * w2
    rem = p[-2:]
    return q, max(abs(c) for c in rem) / scale


def poly_x(roots):
    p = [mp.mpf(1)]
    for x in roots:
        p = pmul(p, [mp.mpf(1), -x])
    return p


def from_x_roots(xs):
    """prod (s^2 + x): each x a root of a polynomial in x = Omega^2 = -s^2."""
    p = [mp.mpf(1)]
    for x in xs:
        p = pmul(p, [mp.mpf(1), mp.mpf(0), x])
    return p


# ---- the prototype ---------------------------------------------------------------------------

def elliptic_roots(n, ripple, astop, etype):
    ep, es = eps_db(ripple), eps_db(astop)
    k1 = ep / es
    K1, K1p = mp.ellipk(k1 ** 2), mp.ellipk(1 - k1 ** 2)
    q = mp.exp(-mp.pi * K1p / (n * K1))
    k = (mp.jtheta(2, 0, q) / mp.jtheta(3, 0, q)) ** 2
    K = mp.ellipk(k ** 2)
    zeta = [mp.ellipfun("cd", (2 * i - 1) * K / n, m=k ** 2) for i in range(1, n // 2 + 1)]
    xr = [z ** 2 for z in zeta]
    xz = [1 / (k * z) ** 2 for z in zeta]
    ws = 1 / k
    if etype in ("b", "c"):
        xp = max(xz)
        b = min(xr) if etype == "c" else mp.mpf(0)
        c = (1 - b) / (xp - 1)
        a = xp * c

        def inv(x):
            return (x - b) / (a - c * x)

        xz = [inv(x) for x in xz if x != xp]
        xr = [inv(x) for x in xr]
        ws = mp.sqrt(inv(ws ** 2))
    return ep, xz, xr, n % 2, ws


def inv_cheby_roots(n, astop):
    es = eps_db(astop)
    xz = [1 / mp.cos((2 * kk - 1) * mp.pi / (2 * n)) ** 2 for kk in range(1, n // 2 + 1)]
    return es, xz, [], n, None


def poles(n, eps, xz, xr, r):
    """LHP roots of E: |E(j Omega)|^2 is proportional to D(x)^2 + eps^2 c^2 x^r N(x)^2."""
    d = poly_x(xz)
    nx = poly_x(xr)
    c = peval(d, 1) / peval(nx, 1)
    nn2 = pmul(pmul(nx, nx), [mp.mpf(1)] + [mp.mpf(0)] * r)
    g = chop(padd(pmul(d, d), pscale(nn2, eps ** 2 * mp.re(c) ** 2)))
    xs = mp.polyroots(g, maxsteps=800, extraprec=800)
    out = []
    for x in xs:
        s = mp.sqrt(-x)
        if mp.re(s) > 0:
            s = -s
        out.append(s)
    assert len(out) == n, (len(out), n)
    return out


def prototype(n, eps, xz, xr, r):
    e = [mp.mpc(1)]
    for p in poles(n, eps, xz, xr, r):
        e = pmul(e, [mp.mpc(1), -p])
    e = [mp.re(c) for c in e]
    fhat = pmul(from_x_roots(xr), [mp.mpf(1)] + [mp.mpf(0)] * r)
    j = mp.mpc(0, 1)
    kf = eps / mp.sqrt(1 + eps ** 2) * abs(peval(e, j)) / abs(peval(fhat, j))
    return e, pscale(fhat, kf)


# ---- zero shifting on polynomials ------------------------------------------------------------

def orderings(m):
    """Tank orders tried, source end first, as indices into the ascending zeros."""
    centre = (m - 1) / 2
    pos = sorted(range(m), key=lambda p: (abs(p - centre), p))
    centre_out = [0] * m
    for zi, p in enumerate(pos):
        centre_out[p] = zi
    seen = []
    for o in (centre_out, list(range(m)), list(range(m - 1, -1, -1))):
        if o not in seen:
            seen.append(o)
    yield from seen
    if m <= 6:
        for o in itertools.permutations(range(m)):
            o = list(o)
            if o not in seen:
                yield o


def synthesize(e, f, zeros, shunt_first):
    """(arms, load ratio), or None when a removal is not positive."""
    # (E+F)/(E-F) has the pole at infinity. Read as an impedance it is the series-first ladder;
    # read as an admittance it is the dual network (F's sign flipped), the shunt-first ladder.
    num = chop(padd(e, f))
    den = chop(padd(e, pscale(f, -1)))
    impedance = not shunt_first
    arms = []
    j = mp.mpc(0, 1)
    for w in zeros:
        w2 = w ** 2
        val = peval(num, j * w) / peval(den, j * w)
        part = mp.im(val) / w
        if part < -TINY:
            return None
        if part > TINY:
            num = padd(num, pscale(pmuls(den), -part))
            arms.append(("L" if impedance else "C", part))
        qn, rel = pdiv_quad(num, w2)
        if rel > mp.mpf("1e-30"):
            return None
        res = mp.re(peval(den, j * w) / (j * w * peval(qn, j * w)))
        if not res > 0:
            return None
        if impedance:
            lb = 1 / res
            arms.append(("stub", lb, 1 / (w2 * lb)))
        else:
            ct = 1 / res
            arms.append(("tank", 1 / (w2 * ct), ct))
        dq, rel = pdiv_quad(padd(den, pscale(pmuls(qn), -res)), w2)
        if rel > mp.mpf("1e-30"):
            return None
        num, den = chop(qn), chop(dq)
    # Cauer at infinity on what is left: each removal drops the numerator's degree by one.
    while len(num) > 1 or len(den) > 1:
        if len(num) != len(den) + 1:
            return None
        v = num[0] / den[0]
        if not v > 0:
            return None
        arms.append(("L" if impedance else "C", v))
        rest = padd(num, pscale(pmuls(den), -v))[1:]
        num, den = den, chop(rest) if rest else [mp.mpf(0)]
        impedance = not impedance
    load = num[0] / den[0]
    if not load > 0:
        return None
    if not impedance:
        load = 1 / load
    return arms, load


def flat(arms):
    out = []
    for a in arms:
        if a[0] in ("L", "C"):
            out.append((a[0], a[1]))
        else:
            out.append(("L", a[1]))
            out.append(("C", a[2]))
    return out


def ladder_s21_sq(arms, load, w):
    """|S21|^2 of the normalised ladder by its ABCD chain, 1 ohm source."""
    s = mp.mpc(0, w)
    a, b, c, d = mp.mpc(1), mp.mpc(0), mp.mpc(0), mp.mpc(1)
    for arm in arms:
        kind = arm[0]
        if kind in ("L", "tank"):
            z = s * arm[1] if kind == "L" else 1 / (1 / (s * arm[1]) + s * arm[2])
            b, d = b + a * z, d + c * z
        else:
            y = s * arm[1] if kind == "C" else 1 / (s * arm[1] + 1 / (s * arm[2]))
            a, c = a + b * y, c + d * y
    t = 2 / mp.sqrt(load) / (a + b / load + c + d / load)
    return abs(t) ** 2


def check(arms, load, e, f):
    """The ladder IS the prototype: |S21|^2 = 1 - |F/E|^2 across the band, to 1e-40."""
    j = mp.mpc(0, 1)
    for w in ("0.05", "0.3", "0.7", "0.95", "1", "1.02", "1.3", "2", "5", "40"):
        w = mp.mpf(w)
        want = 1 - abs(peval(f, j * w) / peval(e, j * w)) ** 2
        assert abs(ladder_s21_sq(arms, load, w) - want) < mp.mpf("1e-40"), w


def row(family, n, ripple, astop, etype, shunt_first):
    if family == "elliptic":
        eps, xz, xr, r, ws = elliptic_roots(n, ripple, astop, etype)
    else:
        eps, xz, xr, r, ws = inv_cheby_roots(n, astop)
    if ws is not None and ws - 1 < MIN_TRANSITION:
        return None
    first = "parallel" if shunt_first else "series"
    e, f = prototype(n, eps, xz, xr, r)
    zeros = sorted(mp.sqrt(x) for x in xz)
    for o in orderings(len(zeros)):
        got = synthesize(e, f, [zeros[i] for i in o], shunt_first)
        if got is not None:
            arms, load = got
            check(arms, load, e, f)
            els = ";".join(f"{t}:{mp.nstr(v, 25)}" for t, v in flat(arms))
            return (f"{family},{n},{ripple},{astop},{etype},{first},"
                    f"{mp.nstr(load, 25)},{'-'.join(map(str, o))},{els}")
    return f"{family},{n},{ripple},{astop},{etype},{first},none,,"


def main():
    only = sys.argv[1:]
    print("family,order,ripple_db,astop_db,type,first,load_ratio,ordering,elements")
    if not only or "elliptic" in only:
        for n in ORDERS:
            for ripple in RIPPLES:
                for astop in ASTOPS:
                    for t in (["a"] if n % 2 else ["b", "c"]):
                        for shunt_first in (False, True):
                            line = row("elliptic", n, ripple, astop, t, shunt_first)
                            if line:
                                print(line, flush=True)
    if not only or "invcheby" in only:
        for n in range(3, 22, 2):
            for astop in ASTOPS:
                for shunt_first in (False, True):
                    print(row("invcheby", n, "0", astop, "a", shunt_first), flush=True)


if __name__ == "__main__":
    main()
