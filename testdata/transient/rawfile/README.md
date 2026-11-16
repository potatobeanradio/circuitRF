# Rawfile reader gate (brief TRN-7 gate 1)

One ngspice run of each deck, written twice: `ngspice -b -n -r <name>.binary.raw <name>.cir`, and the same with
`SPICE_ASCIIRAWFILE=1` for `<name>.ascii.raw`. `rc_tran` is a transient run (real vectors); `rc_ac` a small-signal run
(complex vectors). `tests/RfCore.Tests/Spice/RawFileReaderTests.cs` reads both forms of each to the same plot. Written by
ngspice-47. ngspice leaves the AC scale's imaginary part unset, so only the vectors' imaginary parts are compared.
