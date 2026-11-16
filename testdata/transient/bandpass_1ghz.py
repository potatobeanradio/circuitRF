# brief-trn-4 gate 4 — a third-order maximally flat band-pass (1 GHz centre, 20 % bandwidth, 50 ohm), written as a
# Touchstone file from its lumped prototype: shunt resonator, series resonator, shunt resonator. Independent of circuitRF:
# plain complex arithmetic on ABCD matrices. 20 MHz to 10 GHz in 20 MHz steps, real/imaginary.
import math, cmath

Z0, F0, FBW = 50.0, 1e9, 0.2
G = [1.0, 2.0, 1.0]
W0 = 2 * math.pi * F0

def mul(a, b):
    return [[a[0][0]*b[0][0] + a[0][1]*b[1][0], a[0][0]*b[0][1] + a[0][1]*b[1][1]],
            [a[1][0]*b[0][0] + a[1][1]*b[1][0], a[1][0]*b[0][1] + a[1][1]*b[1][1]]]

def shunt(y): return [[1, 0], [y, 1]]
def series(z): return [[1, z], [0, 1]]

def s_at(f):
    s = 2j * math.pi * f
    # shunt parallel LC (g1, g3), series LC (g2)
    def shunt_res(g):
        L = FBW * Z0 / (W0 * g); C = g / (W0 * FBW * Z0)
        return shunt(1 / (s * L) + s * C)
    def series_res(g):
        L = g * Z0 / (W0 * FBW); C = FBW / (W0 * g * Z0)
        return series(s * L + 1 / (s * C))
    m = mul(mul(shunt_res(G[0]), series_res(G[1])), shunt_res(G[2]))
    A, B, C, D = m[0][0], m[0][1], m[1][0], m[1][1]
    den = A + B / Z0 + C * Z0 + D
    return ((A + B / Z0 - C * Z0 - D) / den, 2 * (A * D - B * C) / den, 2 / den, (-A + B / Z0 - C * Z0 + D) / den)

with open("bandpass_1ghz.s2p", "w") as out:
    out.write("! third-order maximally flat band-pass, 1 GHz, 20 % bandwidth (bandpass_1ghz.py)\n")
    out.write("# Hz S RI R 50\n")
    for k in range(1, 501):
        f = 20e6 * k
        s11, s12, s21, s22 = s_at(f)
        out.write("%d %.12e %.12e %.12e %.12e %.12e %.12e %.12e %.12e\n" %
                  (int(f), s11.real, s11.imag, s21.real, s21.imag, s12.real, s12.imag, s22.real, s22.imag))
