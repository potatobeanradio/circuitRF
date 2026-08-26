<#
  == Build the circuitRF per-user launcher stub (Windows) ======================

    .\packaging\windows\stub\build-stub.ps1 -Arch x64

  Writes build\<AppName>-stub-<Arch>.exe. Called by build-windows.ps1 when -Scope perUser.

  The stub is ~150 lines of plain Win32, so any C compiler produces the same thing. Several
  routes are tried in order and the FIRST ONE THAT PRODUCES A USABLE BINARY wins:

    1. zig cc, -mcpu=baseline            cross-compiles every architecture from any host
    2. zig cc, -mcpu=baseline -O0        same, with the optimiser out of the picture
    3. zig cc, native CPU                what this script used to do, kept as a last resort
    4. cl.exe already on PATH            a Developer PowerShell
    5. cl.exe found via vswhere          any plain PowerShell on a machine with VS installed

  ------------------------------------------------------------------------------
  THIS FILE MUST STAY PURE ASCII - see the note at the top of build-windows.ps1. Windows PowerShell
  5.1 reads a BOM-less .ps1 as cp1252, and a UTF-8 emoji decodes to the curly-quote bytes
  0x93/0x94, which PowerShell honours as string delimiters. Nothing errors; the block is PRINTED
  instead of run. tests/Ui.Tests/PackagingScriptTests.cs holds this shut.
  ------------------------------------------------------------------------------
#>

[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64', 'x86')]
    [string]$Arch = 'x64',

    [string]$AppName = 'circuitRF'
)

$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'circuitrf-stub.c'
$out = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$exe = Join-Path $out "$AppName-stub-$Arch.exe"

# Removed before anything runs: every guard below is `-not (Test-Path $exe)`, so a stub left by an
# earlier run would skip both compilers and be packaged as though it had just been built.
if (Test-Path $exe) { Remove-Item $exe -Force }


# == Running a compiler without PowerShell deciding it failed ==================
#
# A WARNING ON STDERR MUST NOT FAIL THE BUILD, and making sure of that needs its own function.
#
# `$log = & zig cc ... 2>&1` looks like plain output capture and is not. Under Windows PowerShell
# 5.1, merging a NATIVE command's stderr into the success stream while $ErrorActionPreference is
# 'Stop' turns the first stderr line into a TERMINATING error: PowerShell wraps it in a
# NativeCommandError, throws it, and $LASTEXITCODE is never reached. The compiler's exit code is
# irrelevant - it can be 0.
#
# That is not hypothetical. It cost the x86 stub of a whole release (owner-reported, 2026-08-25):
# zig printed one harmless line -
#
#     '-macrofusio' is not a recognized feature for this target (ignoring feature)
#
# - which is an LLVM warning that says, in its own text, that it is ignoring the feature and
# carrying on. The compile succeeded. PowerShell threw anyway, build-windows.ps1 caught it, and the
# release shipped without the x86 per-user installer or its update payload. The clue that it was
# never a compiler failure: that line is the caught EXCEPTION MESSAGE, not something this script
# printed - a NativeCommandError's message IS the offending stderr text.
#
# So: run the command with the preference set to 'Continue' for exactly its duration, and decide
# what happened from the EXIT CODE and the artifact, which is what those things are for.

function Invoke-Compiler {
    param(
        [Parameter(Mandatory)] [string]   $Exe,
        [Parameter(Mandatory)] [string[]] $Arguments,
        [hashtable] $Environment
    )

    # Environment variables are set for the duration of the call and put back afterwards, so a route
    # that needs one (a private zig cache, below) cannot leak it into the next route or into the
    # caller. $null restores 'was not set', which is not the same as 'was empty'.
    $saved = @{}
    if ($Environment) {
        foreach ($k in $Environment.Keys) {
            $saved[$k] = [Environment]::GetEnvironmentVariable($k)
            [Environment]::SetEnvironmentVariable($k, $Environment[$k])
        }
    }

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $raw  = & $Exe @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }

    $text = @($raw | ForEach-Object { $_.ToString() })
    return [pscustomobject]@{ ExitCode = $code; Output = $text }
}


# == What was actually built, read out of the PE ===============================
#
# Never trust a toolchain to have done what it was asked - the same discipline as build-macos.sh
# reading architecture back with lipo and build-linux.sh reading the device worker's ELF header.
# Two things this catches, both of which ship silently otherwise:
#
#   * THE SUBSYSTEM. -mwindows is a NO-OP under zig cc and yields a console stub, measured by
#     reading the field back out of the PE (zig 0.13.0):
#
#         -mwindows                      -> 3 (CONSOLE)   wrong, and no warning
#         -Wl,--subsystem,windows        -> 2 (GUI)       correct
#         both together                  -> 3 (CONSOLE)   -mwindows wins and undoes it
#
#     The stub exists to be invisible; a console window flashing up on every launch of every one of
#     the three applications is about as visible as a defect gets.
#
#   * THE ARCHITECTURE. The cl.exe-already-on-PATH route has no architecture switch of its own - it
#     builds for whatever the Developer PowerShell it was started from targets. On an x64 prompt
#     that yields an x64 binary named circuitRF-stub-arm64.exe, which the arm64 .msi would then
#     ship. Nothing about that fails until an ARM user double-clicks the shortcut.
#
# A binary that fails this is DISCARDED AND THE NEXT ROUTE IS TRIED, rather than ending the run:
# "the compiler on PATH targets the wrong architecture" is a reason to go and find the right one,
# which is exactly what the vswhere route below does.

function Test-StubBinary {
    param([string]$Path, [string]$WantArch)

    $want  = @{ 'x64' = 0x8664; 'arm64' = 0xAA64; 'x86' = 0x014C }[$WantArch]
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 512) { return "the file is $($bytes.Length) bytes; that is not a PE" }

    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($pe -le 0 -or $pe + 92 -ge $bytes.Length) { return 'the PE header offset is out of range' }
    if ($bytes[$pe] -ne 0x50 -or $bytes[$pe + 1] -ne 0x45) { return 'no PE signature' }

    $machine   = [BitConverter]::ToUInt16($bytes, $pe + 4)
    $subsystem = [BitConverter]::ToUInt16($bytes, $pe + 24 + 68)

    if ($machine -ne $want) {
        return ("it is machine 0x{0:X4}, not the 0x{1:X4} a {2} install needs" -f $machine, $want, $WantArch)
    }
    if ($subsystem -ne 2) {
        return "its subsystem is $subsystem (2 is GUI); a console window would open on every launch"
    }
    return $null
}


# == The routes ================================================================

$zigTarget = @{ 'x64' = 'x86_64-windows-gnu'; 'arm64' = 'aarch64-windows-gnu'; 'x86' = 'x86-windows-gnu' }[$Arch]

# NO QUOTES AROUND THE APP NAME. Windows PowerShell 5.1 strips a bare " when it builds a native
# command line, so -DCRF_APP_NAME="circuitRF" arrived as -DCRF_APP_NAME=circuitRF, L##circuitRF
# pasted into the undeclared identifier LcircuitRF, and the build failed at the first architecture
# (owner-reported, 2026-08-25). The stub now takes a BARE TOKEN and stringifies it itself, so there
# is nothing here to escape and nothing for a future route to get wrong. See circuitrf-stub.c.
$appDefine = "-DCRF_APP_NAME=$AppName"

# WHY EACH ROUTE IS RETRIED: ON THIS CLASS OF MACHINE ZIG CRASHES AT RANDOM.
#
# Two full release runs on the same Windows-on-ARM box, with the correct native windows-aarch64
# zig 0.16.0 (owner-reported, 2026-08-25). zig exits -1073741819 (0xC0000005, an access violation)
# having printed NOTHING AT ALL - a crash, not a refusal, so neither the stub source nor the flags
# are implicated:
#
#     run 1   x64   native CPU, shared cache    -> BUILT, first attempt
#             arm64 native CPU, shared cache    -> crashed
#             x86   native CPU, shared cache    -> ran far enough to emit an LLVM warning
#     run 2   x64   baseline x2 then private cache -> BUILT on the third attempt
#             arm64 all four routes             -> crashed
#             x86   all four routes             -> crashed
#
# THE SAME COMMAND GAVE A DIFFERENT ANSWER ON DIFFERENT RUNS. Route 4 here is character for
# character what built x64 on the first attempt in run 1, and in run 2 it crashed; the x86 native-CPU
# command emitted a warning and carried on in run 1 and crashed silently in run 2. That rules out a
# deterministic fault in a target, a flag or the source, and it is why the earlier theory recorded
# here - that the aarch64 target is special because it is also the HOST, so zig resolves the CPU
# natively down a different code path - was wrong. It is a good theory that predicts x86 works, and
# x86 does not. Across 15 attempts, 2 succeeded: roughly one in seven, spread over every target.
#
# At one in seven, four attempts fail more often than not, so a four-route ladder of DIFFERENT ideas
# was the wrong shape for this. Retrying the same route is the shape that fits, and it is cheap
# because a crash is instant - the run below is bounded at 20 attempts and a losing streak costs
# seconds, not minutes.
#
# The flags are kept anyway, on their own merits, and the ladder is now ordered by that merit rather
# than by suspicion:
#
#   -mcpu=baseline  The stub is shipped to other people's machines, so building it for whichever CPU
#                   happened to cut the release is wrong even when it works. Without an explicit
#                   -mcpu, zig resolves the CPU natively whenever the target's architecture and OS
#                   match the host's, which makes exactly one of the three architectures different.
#   private cache   zig caches compiled libc and CRT objects globally (%LOCALAPPDATA%\zig). A crash
#                   part-way through writing that cache can leave a half-written entry, and this run
#                   is producing crashes, so the next run reading it is a real hazard. A scratch
#                   directory tests it without deleting anything the operator has.
#   -municode       wWinMain is the Unicode entry point; without it the mingw CRT looks for WinMain.
#   -Wl,--subsystem,windows   NOT -mwindows, which is a no-op under zig cc and yields a CONSOLE
#                   stub. QUOTED, because unquoted PowerShell reads the commas as an array separator
#                   and the script does not even parse.
#
# HOW OFTEN IT CRASHED IS REPORTED ON SUCCESS, not swallowed. A toolchain that works one time in
# seven is a fact about the machine that the operator needs, and a script that hides it behind a
# tidy "OK" is how it stays unfixed.
$zigCache = Join-Path $out 'zig-cache-scratch'

# THE ATTEMPT BUDGET IS ARITHMETIC, not a round number. At the measured rate of roughly one success
# in seven, twenty attempts still leave (6/7)^20 = 4.6% of runs short - which across three
# architectures is a one-in-eight chance of an incomplete release, and an incomplete release is the
# whole thing this script was rewritten to prevent. Forty attempts take it to 0.2% per architecture.
# It costs nothing when it is not needed: a crash returns instantly, so a losing streak is seconds,
# and a machine with a healthy zig never sees attempt two.
$zigRoutes = @(
    @{ Label = "baseline CPU";                Retries = 14
       Flags = @('-mcpu=baseline', '-O2') },
    @{ Label = "baseline CPU, private cache"; Retries = 14
       Flags = @('-mcpu=baseline', '-O2')
       Env   = @{ ZIG_GLOBAL_CACHE_DIR = $zigCache; ZIG_LOCAL_CACHE_DIR = $zigCache } },
    @{ Label = "baseline CPU, -O0";           Retries = 6
       Flags = @('-mcpu=baseline', '-O0') },
    @{ Label = "native CPU";                  Retries = 6
       Flags = @('-O2') }
)

$tried        = @()
$attempts     = 0
$crashes      = 0
$lastOutput   = @()
$lastWasCrash = $false

function Complete-Route {
    param([string]$Label, [object]$Result)

    if ($Result.ExitCode -ne 0 -or -not (Test-Path $script:exe)) {

        # WHAT COUNTS AS A CRASH, and it is deliberately not one magic number. -1073741819 is
        # 0xC0000005, the access violation actually seen - but every NTSTATUS exception code has its
        # top bit set and therefore arrives here NEGATIVE, so stack overflow (0xC00000FD), illegal
        # instruction (0xC000001D) and heap corruption (0xC0000374) are the same event and want the
        # same answer. Testing for the one code that happened is how the next one gets treated as a
        # compiler refusal and retried zero times.
        #
        # NO OUTPUT AT ALL counts too. A compiler that REFUSES code says why, on stderr, always;
        # every crash observed on the release box printed nothing whatsoever. A silent non-zero exit
        # is not a refusal we can learn anything from, so it is worth another attempt - and the
        # attempt budget bounds it either way.
        $script:lastWasCrash = ($Result.ExitCode -lt 0) -or ($Result.Output.Count -eq 0)

        $why = if ($script:lastWasCrash) {
            $script:crashes++
            if ($Result.ExitCode -eq -1073741819) { 'crashed (access violation)' }
            elseif ($Result.ExitCode -lt 0)       { ('crashed (0x{0:X8})' -f [uint32]$Result.ExitCode) }
            else                                  { "exit $($Result.ExitCode), no output" }
        }
        else { "exit $($Result.ExitCode)" }

        $script:tried += "$Label - $why"
        if ($Result.Output.Count -gt 0) { $script:lastOutput = $Result.Output }
        if (Test-Path $script:exe) { Remove-Item $script:exe -Force -ErrorAction SilentlyContinue }
        return $false
    }

    $wrong = Test-StubBinary $script:exe $script:Arch
    if ($wrong) {
        # A binary that is the wrong shape is a deterministic outcome, not a dropped dice roll.
        $script:lastWasCrash = $false
        $script:tried += "$Label - built, but $wrong"
        if ($Result.Output.Count -gt 0) { $script:lastOutput = $Result.Output }
        Remove-Item $script:exe -Force -ErrorAction SilentlyContinue
        return $false
    }

    if (Test-Path $script:zigCache) { Remove-Item $script:zigCache -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host "OK  $script:exe"
    if ($script:crashes -gt 0) {
        Write-Host ("    Built by $Label on attempt $($script:attempts); zig crashed on " +
                    "$($script:crashes) of the attempts before it. That is this machine's zig, not " +
                    "the stub - see packaging/RESOLVED.md.")
    }
    else {
        Write-Host "    Built by $Label."
    }
    return $true
}

if (Get-Command zig -ErrorAction SilentlyContinue) {
    Write-Host "Building the launcher stub with zig cc ($zigTarget) ..."
    foreach ($route in $zigRoutes) {
        for ($try = 1; $try -le $route.Retries; $try++) {
            $attempts++
            $r = Invoke-Compiler -Exe zig -Environment $route.Env -Arguments (
                     @('cc', '-target', $zigTarget) + $route.Flags +
                     @('-municode', '-Wl,--subsystem,windows', $appDefine,
                       $src, '-o', $exe, '-luser32'))
            if (Complete-Route $route.Label $r) { return }

            # A route that FAILED FOR A REASON is not worth repeating: only a crash is random, and
            # retrying a compiler that refused the code just prints the same refusal eight times.
            if (-not $lastWasCrash) { break }
        }
    }
    Write-Host "  zig cc did not produce a stub in $attempts attempts ($crashes of them crashes)."
}
else {
    $tried += 'zig - not on PATH'
}

# cl.exe already on PATH: a Developer PowerShell. It builds for whatever that prompt targets, which
# the PE check above verifies rather than assumes.
if (Get-Command cl -ErrorAction SilentlyContinue) {
    Write-Host 'Building the launcher stub with cl.exe (on PATH) ...'
    Push-Location $out
    try {
        $r = Invoke-Compiler cl @('/nologo', '/O2', '/W3', '/DUNICODE', '/D_UNICODE', $appDefine, $src,
                                  '/link', '/SUBSYSTEM:WINDOWS', '/ENTRY:wWinMainCRTStartup',
                                  'user32.lib', "/OUT:$exe")
    }
    finally { Pop-Location }
    if (Complete-Route 'cl.exe (on PATH)' $r) { return }
}

# == cl.exe found for us =======================================================
#
# Visual Studio ships the cross tools for all three architectures; the only thing a plain PowerShell
# is missing is the environment that puts one of them on PATH. vswhere locates the install and
# vcvarsall sets up the exact host_target pair, so "open a Developer PowerShell instead" stops being
# something the operator has to know - which matters, because the alternative advice this script
# used to print was the only remaining option when zig fell over.
#
# The command line is written to a .cmd rather than passed through `cmd /c "..."`. PowerShell 5.1
# mangles embedded quotes when it builds a native command line - the same handling that ate the app
# name above - and these arguments contain paths that may have spaces in them.

# NO -requires FILTER ON THE VSWHERE QUERY. The obvious one to ask for is
# Microsoft.VisualStudio.Component.VC.Tools.x86.x64, and it EXCLUDES the very machine this route
# exists for: an ARM64 install carries ...VC.Tools.ARM64 instead, so requiring the x64 component
# hides a Visual Studio that can build all three targets. Take the latest install and let the
# presence of vcvarsall.bat, and then vcvarsall's own exit code, decide - those answer the real
# question, which is not "which components are registered" but "can this build arm64".
#
# WHY THIS ROUTE IS SKIPPED IS PRINTED. In the 2026-08-25 run it did not appear in the log at all,
# which left "no C compiler could build it" listing only zig and no way to tell whether Visual
# Studio was absent, unusable, or simply never looked for.

$vsRoot  = ${env:ProgramFiles(x86)}
$vswhere = if ($vsRoot) { Join-Path $vsRoot 'Microsoft Visual Studio\Installer\vswhere.exe' } else { $null }
if (-not (Test-Path $exe)) {

    $vsPath = $null
    if ($vswhere -and (Test-Path $vswhere)) {
        $vsPath = (Invoke-Compiler $vswhere @('-latest', '-products', '*', '-prerelease',
                                              '-property', 'installationPath')).Output |
                  Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    }

    $vcvars = if ($vsPath) { Join-Path $vsPath 'VC\Auxiliary\Build\vcvarsall.bat' } else { $null }

    if (-not ($vswhere -and (Test-Path $vswhere))) {
        $tried += 'Visual Studio - not installed (no vswhere.exe)'
    }
    elseif (-not $vsPath) {
        $tried += 'Visual Studio - vswhere found no installation'
    }
    elseif (-not (Test-Path $vcvars)) {
        $tried += "Visual Studio - found at $vsPath, but it has no C++ tools (no vcvarsall.bat)"
    }

    if ($vcvars -and (Test-Path $vcvars)) {

        # PROCESSOR_ARCHITECTURE reads 'x86' inside a 32-bit PowerShell on a 64-bit machine;
        # PROCESSOR_ARCHITEW6432 is what says so.
        $hostRaw  = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
        $hostVc   = @{ 'ARM64' = 'arm64'; 'AMD64' = 'amd64'; 'x86' = 'x86' }[$hostRaw]
        if (-not $hostVc) { $hostVc = 'amd64' }
        $targetVc = @{ 'x64' = 'amd64'; 'arm64' = 'arm64'; 'x86' = 'x86' }[$Arch]
        $pair     = if ($hostVc -eq $targetVc) { $targetVc } else { "${hostVc}_${targetVc}" }

        Write-Host "Building the launcher stub with cl.exe (Visual Studio, $pair) ..."

        $bat = Join-Path $out 'build-stub-msvc.cmd'
        $lines = @(
            '@echo off',
            "cd /d `"$out`"",
            "call `"$vcvars`" $pair > nul",
            'if errorlevel 1 exit /b 90',
            ("cl /nologo /O2 /W3 /DUNICODE /D_UNICODE $appDefine `"$src`" " +
             "/link /SUBSYSTEM:WINDOWS /ENTRY:wWinMainCRTStartup user32.lib `"/OUT:$exe`"")
        )
        Set-Content -Path $bat -Value $lines -Encoding Ascii

        try {
            $r = Invoke-Compiler $env:ComSpec @('/c', $bat)
            if ($r.ExitCode -eq 90) {
                $r = [pscustomobject]@{ ExitCode = 90; Output = @("vcvarsall.bat has no $pair tools installed") }
            }
        }
        finally { Remove-Item $bat -Force -ErrorAction SilentlyContinue }

        if (Complete-Route "cl.exe (Visual Studio, $pair)" $r) { return }
    }
}


# == Nothing worked ============================================================
#
# The compiler's own output is re-thrown with the failure. Without it a broken build says only
# "the stub could not be built", which is a sentence with no information in it - and this script
# runs on whichever machine cuts the Windows release, where a round trip to diagnose is expensive.

if (Test-Path $zigCache) { Remove-Item $zigCache -Recurse -Force -ErrorAction SilentlyContinue }

if ($tried.Count -gt 0) {
    $detail = ''
    if ($lastOutput.Count -gt 0) {
        $detail = "`n  last compiler output:`n    " + (($lastOutput | Select-Object -Last 12) -join "`n    ")
    }
    throw ("no C compiler could build it. Tried:`n  " + ($tried -join "`n  ") + $detail)
}

throw @'
No C compiler was found for the launcher stub.

Install one of these and run this script again:

    zig      (winget install zig.zig)   - one download, no daemon; the preferred route
    MSVC     (Visual Studio with "Desktop development with C++", or the Build Tools;
              this script finds it with vswhere, so no Developer PowerShell is needed)

The stub is the one file in a per-user install that never changes: shortcuts and file
associations point at it, and it starts the version named in `current`. Without it there is no
per-user install channel and therefore no automatic updates on Windows.
'@
