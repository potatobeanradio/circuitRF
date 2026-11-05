# A conducting silicon substrate: reference

A 1 mm line, 90 µm wide, on 2 µm of oxide (εr 3.9) on 100 µm of silicon (εr 11.9, tanδ 0, the stated σ) over a perfect backside ground, with copper
metal (5.8e7 S/m). `reference.csv` is the line's S-parameters at 50 Ω, with its γ and Z_c, at 0.1, 10 and 1e4 S/m.

Made once and committed; no solver runs in a test.

- **γ** is Palace 0.18.1, with the silicon drawn as a body. At 0.1 S/m, and at 10 S/m from 5 GHz, it is the wave port's modal k_n, on a 5 µm edge mesh sized at
  40 GHz, with PMC walls and lid 1.5 mm out. On that line at 20 GHz, 5, 2 and 1 µm read 1130, 1130 and 1129 rad/m. Where a wave port takes a wrong mode, at 10 S/m
  and 1 GHz and throughout at 1e4 S/m, it is lumped ports on 0.5 and 1 mm lines, from the eigenvalues of T₂T₁⁻¹, with absorbing sides and lid 800 µm out, and at
  1e4 S/m a 15 µm mesh region over the top 50 µm of silicon.
- **Z_c** is γ/(jωC*), with C* from a 2D finite-element solve of the cross-section in complex permittivity, independent of the kernel, walls 3 mm out.
- **S** is the uniform line those two make, so the ports' own parasitics are not in it.

A wave port's driven S is not used anywhere: on conducting silicon its Z_PV is real where Z_c is not, and a uniform line read S11 of −0.76 dB at 5 GHz.
Pairs of lumped lines shorter than 0.5 mm are not used either: 0.25 and 0.5 mm read β 1.6 % above the modal value.
