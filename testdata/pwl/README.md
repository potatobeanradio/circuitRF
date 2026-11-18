# Wall-port fixtures (PWL-2)

Hand-built `.c3d` problems for the wall port: a line between two PEC end walls of the air box, fed at each end by a
lumped port in a gap from the wall (−) to the line (+), in the line's plane. Each set is the line itself and the line
standards its calibration reads, all the same cross-section. They are what brief 3's Palace gate and briefs 5 and 6's
EMerge and openEMS gates solve. No solver runs in a routine test.

| Files | Structure | Gap | Lengths, µm (wall to wall) |
|---|---|---|---|
| `stripline_*` | 50 Ω stripline: a zero-thickness perfect strip, 1717.866 µm wide, centred in 2 mm of PTFE (εr 2.1, lossless); PEC top and bottom faces, PMC sides 10 strip widths out | 171.8 µm (w/10) | short 4000, long 10131 and 23389, line 20000 |
| `coupled_*` | edge-coupled microstrip pair: two zero-thickness perfect strips 1200 µm wide, 300 µm apart, on 508 µm of laminate (εr 2.2, lossless) over a PEC floor; absorbing sides and lid 10 substrate heights out | 100 µm | short 4000, long 10476 and 24486, line 20000 |

Ports: the stripline's are P1 (xmin) and P2 (xmax); the pair's are P1, P2 at xmin (strip a, strip b) and P3, P4 at xmax,
which is the order a calibration group's 2N-port standard takes. "Perfect" is σ = 1e14 S/m, which a sheet needs to be a
conductor and whose surface resistance at 10 GHz is about 2e-5 Ω. The setup is Palace at 1, 5 and 10 GHz, Standard
quality with no refinement passes.

## The oracles and what was measured on Palace (2026-11-18)

- **Stripline:** Z_c = 50.000 Ω and ε_eff = 2.1 exactly (zero-thickness closed form). Calibrated with Z_c = γ/(jωC′), C′
  = √εr/(c·Z_c): ε_eff 2.116, worst calibrated VSWR against the exact line 1.0047, third-line residual (|S11| of the
  de-embedded line in its own Z_c) ≤ 1.8e-3, worst |ΔS| 2.3e-2 at 10 GHz, which is the ε_eff error alone (0.76 % over
  20 mm). Raw, before calibration: VSWR up to 1.36, |ΔS| up to 0.21.
- **Coupled pair:** kernel A's quasi-static modes, ε_eff 1.7155 (odd) and 1.9387 (even). Calibrated as one group:
  ε_eff 1.749–1.758 / 1.985–2.003, modal third-line residual ≤ 8.1e-3, worst calibrated VSWR against kernel A's
  quasi-static coupled line 1.017 (raw 1.096), modes 2.3–6.5° apart over the separation read.

Detail, and every other table, in `src/Design/Em3d/RESOLVED.md` §PWL-2.
