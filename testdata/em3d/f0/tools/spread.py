# SPIKE MATERIAL: largest difference between two runs per frequency band, in |S21| (dB), arg S21 (deg),
# |S11| (dB), and -- for case A -- the pi-model series L = Im(-1/Y21)/omega (pH).
#   python spread.py <a.s2p> <b.s2p> <band edges GHz ...>
import sys
sys.path.insert(0, __import__('os').path.dirname(__file__))
from summarize import read, y_of_s
import numpy as np
a, b = sys.argv[1], sys.argv[2]; edges = [float(x) for x in sys.argv[3:]]
f, A = read(a); f2, B = read(b)
assert np.allclose(f, f2), 'different frequency grids'
db = lambda x: 20*np.log10(abs(x))
LA = (-1/y_of_s(A)[:, 1, 0]).imag/(2*np.pi*f)*1e12; LB = (-1/y_of_s(B)[:, 1, 0]).imag/(2*np.pi*f)*1e12
for lo, hi in zip(edges[:-1], edges[1:]):
    k = (f >= lo*1e9 - 1) & (f <= hi*1e9 + 1)
    d21 = np.max(abs(db(A[k,1,0]) - db(B[k,1,0])))
    dph = np.max(abs((np.degrees(np.angle(A[k,1,0]) - np.angle(B[k,1,0])) + 180) % 360 - 180))
    d11 = np.max(abs(db(A[k,0,0]) - db(B[k,0,0])))
    dL = np.max(abs(LA[k] - LB[k]))
    print(f'  {lo:g}-{hi:g} GHz: max |d|S21|| {d21:.4f} dB, max |d arg S21| {dph:.2f} deg, max |d|S11|| {d11:.2f} dB, max |dL| {dL:.1f} pH')
