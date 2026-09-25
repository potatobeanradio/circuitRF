# SPIKE MATERIAL. Read a 2-port RI Touchstone written above; print |S11|, |S21|, arg S21 and the
# series element of the pi-model, Z_series = -1/Y21 (R = Re, L = Im/omega), at chosen frequencies.
import sys, numpy as np
def read(p):
    rows = [l.split() for l in open(p) if l.strip() and l[0] not in '!#']
    a = np.array(rows, float); f = a[:, 0]*1e9
    S = np.zeros((len(f), 2, 2), complex)
    S[:, 0, 0] = a[:, 1] + 1j*a[:, 2]; S[:, 1, 0] = a[:, 3] + 1j*a[:, 4]
    S[:, 0, 1] = a[:, 5] + 1j*a[:, 6]; S[:, 1, 1] = a[:, 7] + 1j*a[:, 8]
    return f, S
def y_of_s(S, z0=50.0):
    I = np.eye(2)
    return np.array([np.linalg.solve(z0*(I + s), (I - s)) for s in S])  # Y = (I+S)^-1 (I-S) / z0
if __name__ == '__main__':
    p = sys.argv[1]; fs = [float(x) for x in sys.argv[2:]]
    f, S = read(p); Y = y_of_s(S)
    for fq in fs:
        k = int(np.argmin(abs(f - fq*1e9)))
        zs = -1/Y[k, 1, 0]
        print(f'{f[k]/1e9:6.2f} GHz |S11| {20*np.log10(abs(S[k,0,0])):8.3f}  |S21| {20*np.log10(abs(S[k,1,0])):9.5f}  '
              f'argS21 {np.degrees(np.angle(S[k,1,0])):9.3f}  Rser {zs.real*1e3:9.2f} mOhm  Lser {zs.imag/(2*np.pi*f[k])*1e12:8.2f} pH')
