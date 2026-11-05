# A conducting silicon substrate: reference

A 1 mm line, 90 µm wide, on 2 µm of oxide (εr 3.9) on 100 µm of silicon (εr 11.9, tanδ 0, the stated σ) over a perfect backside ground, with copper
metal (5.8e7 S/m). `reference.csv` is the line's S-parameters at 50 Ω, with its γ and Z_c, at 0.1, 10, 100, 1000 and 1e4 S/m.

Made once and committed; no solver runs in a test.

- **γ** is Palace 0.18.1, with the silicon drawn as a body. At 0.1 S/m, and at 10 S/m from 5 GHz, it is the wave port's modal k_n, on a 5 µm edge mesh sized at
  40 GHz, with PMC walls and lid 1.5 mm out. On that line at 20 GHz, 5, 2 and 1 µm read 1130, 1130 and 1129 rad/m. Where a wave port takes a wrong mode, at 10 S/m
  and 1 GHz and throughout at 1e4 S/m, it is lumped ports on 0.5 and 1 mm lines, from the eigenvalues of T₂T₁⁻¹, with absorbing sides and lid 800 µm out, and at
  1e4 S/m a 15 µm mesh region over the top 50 µm of silicon.
- **At 100 and 1000 S/m** it is the same lumped pair, 0.5 and 1 mm. At 1000 S/m and 20 GHz, the pair of 0.5 and 1.5 mm and the pair of 1 and 1.5 mm read β within 0.15 % of it. The row at 1000 S/m and 30 GHz is the 0.5 and 1.5 mm pair.
- **At 1e4 S/m below 5 GHz, α is not Palace's.** Its lumped pairs agree on β within 2 % and disagree on α: 53.7, 27.0 and 0.6 Np/m at 1 GHz from the three pairs of 0.5, 1 and 1.5 mm. So β is the 0.5 and 1.5 mm pair, and α comes from √(Z·jωC*), with Z from the 2D solve below and C* as for Z_c.
- **Z_c** is γ/(jωC*), with C* from a 2D finite-element solve of the cross-section in complex permittivity, independent of the kernel, walls 3 mm out.
- **S** is the uniform line those two make, so the ports' own parasitics are not in it.

A wave port's driven S is not used anywhere: on conducting silicon its Z_PV is real where Z_c is not, and a uniform line read S11 of −0.76 dB at 5 GHz.
Pairs of lumped lines shorter than 0.5 mm are not used either: 0.25 and 0.5 mm read β 1.6 % above the modal value.

`series-term.csv` is what conductivity adds to a strip's series impedance, Z(σ) − Z(0), as ΔR and ΔL per metre at three widths. It is a 2D finite-element solve of the same cross-section for the longitudinal vector potential, with σ in the silicon, a perfect strip and no displacement current, walls 3 mm out. Halving every cell moves it by under 0.05 %.
