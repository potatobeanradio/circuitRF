# `testdata/em3d/wsl/` — the Linux subsystem's own bytes

Fixtures for `tests/Ui.Tests/Em3d/WslLocationTests.cs` (brief-em3d-26). Nothing here runs WSL.

## `wsl-l-v.utf16le.bin`

What `wsl.exe -l -v` writes to a **redirected** stdout: UTF-16LE, no byte-order mark, CRLF line ends. It
lists three distributions: `Ubuntu` (the default, running, WSL 2), `Debian` (stopped, WSL 1) and
`Ubuntu-24.04` (stopped, WSL 2).

**Provenance: constructed, not yet captured.** It was written on macOS in the format wsl.exe documents
for redirected output, because no Windows machine was available to the brief. Gate 1's control
assertion (decoding it as UTF-8 finds no distribution name) holds for any UTF-16LE listing. When the
owner makes the Windows runs in brief-em3d-26 §6, replace this file with a real capture and keep its
name:

```
wsl.exe -l -v > wsl-l-v.utf16le.bin
```

If the real capture carries a byte-order mark or a different line ending, the parser already accepts
both. A capture that the parser rejects is a finding, and it goes in `src/Design/RESOLVED.md`.
