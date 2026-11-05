#!/usr/bin/env python3
"""R' and L' of a rectangular strip over a perfect floor, by a 2D filament solve of its section over its image.

The section is cut into rectangles that each carry a uniform current. Each pair's mutual inductance per unit length is the exact
mean of ln r over the two rectangles, so a neighbouring pair of long thin cells is as good as a distant one. The grid is graded
towards the corners and refined until the answer settles. Numpy is the only dependency.

    python3 filament2d.py > reference.csv
"""
import numpy as np

MU0 = 4e-7 * np.pi
SIGMA = 3.77e7     # aluminium, S/m
THICK = 1e-6       # m
HEIGHT = 10e-6     # the strip's underside over the floor, m
FREQS_GHZ = (0.1, 0.447213595, 2.0, 8.94427191, 40.0)


def f4(x, z):
    """An antiderivative: d2/dx2 d2/dz2 of this is ln sqrt(x^2 + z^2), even in x and in z, and C3 where the axes are crossed."""
    x, z = np.abs(x), np.abs(z)
    w = x + 1j * z
    with np.errstate(divide="ignore", invalid="ignore"):
        h = w**4 * (np.log(w) / 24 - 25.0 / 288)
    h = np.where(w == 0, 0, h)
    return -np.real(h) + np.pi / 12 * x * z**3


def mean_log(ax0, ax1, az0, az1, bx0, bx1, bz0, bz1):
    """The mean of ln r between rectangle a and every rectangle b (arrays broadcast)."""
    total = 0.0
    for sx, dx in ((1, ax1 - bx1), (-1, ax1 - bx0), (-1, ax0 - bx1), (1, ax0 - bx0)):
        for sz, dz in ((1, az1 - bz1), (-1, az1 - bz0), (-1, az0 - bz1), (1, az0 - bz0)):
            total = total + sx * sz * f4(dx, dz)
    return total / ((ax1 - ax0) * (az1 - az0) * (bx1 - bx0) * (bz1 - bz0))


def graded(length, cells, ratio):
    """Edges of a grid on [0, length], symmetric, its middle cell `ratio` times its end cell."""
    half = (cells + 1) // 2
    sizes = ratio ** (np.arange(half) / max(half - 1, 1))
    sizes = np.concatenate([sizes, sizes[::-1][cells % 2:]])
    return np.concatenate([[0.0], np.cumsum(sizes)]) * length / sizes.sum()


def solve(width, freqs_hz, nx, nz, ratio_x, ratio_z=4.0):
    xe, ze = graded(width, nx, ratio_x), graded(THICK, nz, ratio_z) + HEIGHT
    x0, z0 = [a.ravel() for a in np.meshgrid(xe[:-1], ze[:-1], indexing="ij")]
    x1, z1 = [a.ravel() for a in np.meshgrid(xe[1:], ze[1:], indexing="ij")]
    area = (x1 - x0) * (z1 - z0)
    i, j = np.meshgrid(np.arange(x0.size), np.arange(x0.size), indexing="ij")
    own = mean_log(x0[i], x1[i], z0[i], z1[i], x0[j], x1[j], z0[j], z1[j])
    image = mean_log(x0[i], x1[i], z0[i], z1[i], x0[j], x1[j], -z1[j], -z0[j])
    inductance = MU0 / (2 * np.pi) * (image - own)
    rows = []
    for f in freqs_hz:
        z = 1j * 2 * np.pi * f * inductance
        z[np.diag_indices_from(z)] += 1 / (SIGMA * area)
        total = 1 / np.linalg.solve(z, np.ones(x0.size, complex)).sum()
        rows.append((total.real, total.imag / (2 * np.pi * f)))
    return rows


def main():
    freqs = [g * 1e9 for g in FREQS_GHZ]
    print("width_um,f_ghz,r_ohm_per_mm,l_ph_per_um,settled_r_percent")
    for width_um, (nx, nz, ratio_x) in ((5, (60, 14, 12)), (50, (120, 14, 60)), (200, (180, 10, 200)), (500, (240, 10, 400))):
        fine = solve(width_um * 1e-6, freqs, nx, nz, ratio_x)
        coarse = solve(width_um * 1e-6, freqs, nx * 2 // 3, nz * 2 // 3 + 1, ratio_x)
        for g, (r, l), (rc, _) in zip(FREQS_GHZ, fine, coarse):
            print(f"{width_um},{g:.4f},{r * 1e-3:.4f},{l * 1e6:.4f},{abs(r - rc) / r * 100:.2f}")


if __name__ == "__main__":
    main()
