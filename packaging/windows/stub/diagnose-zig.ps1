<#
  == Why does zig crash on this machine? =======================================

    .\packaging\windows\stub\diagnose-zig.ps1

  Writes a report to the console AND to build\zig-diagnostic.txt. Send that file back.

  This exists because build-stub.ps1 currently WORKS AROUND a crash it cannot explain: zig 0.16.0
  on the Windows-on-ARM release box exits -1073741819 (0xC0000005, an access violation) having
  printed nothing at all, at random, roughly six times in seven, on every target - so the stub is
  built by retrying until a roll succeeds. That is a workaround, and a workaround nobody
  understands is a workaround that will fail differently one day. See packaging/RESOLVED.md.

  It measures rather than guesses, because guessing is what cost two release runs already. Every
  section isolates ONE variable and REPEATS, since a fault that appears one time in seven cannot be
  characterised by a single invocation - which is precisely how the first explanation
  ("aarch64 is special because it is also the host") survived a whole run before being refuted.

  Takes a few minutes. Nothing is installed and nothing outside build\ is written to.

  ------------------------------------------------------------------------------
  THIS FILE MUST STAY PURE ASCII - see the note at the top of build-windows.ps1. Windows PowerShell
  5.1 reads a BOM-less .ps1 as cp1252, and a UTF-8 emoji decodes to the curly-quote bytes
  0x93/0x94, which PowerShell honours as string delimiters. Nothing errors; the block is PRINTED
  instead of run. tests/Ui.Tests/PackagingScriptTests.cs holds this shut.
  ------------------------------------------------------------------------------
#>

[CmdletBinding()]
param(
    # How many times each measurement is repeated. The default is sized so that a one-in-seven
    # fault is very unlikely to be missed entirely (0.86^10 = 21% for a single run, but every
    # section runs several), while keeping the whole thing to a few minutes.
    [int]$Repeat = 10,

    # A shorter pass, for when you only want to confirm something changed.
    [switch]$Quick
)

$ErrorActionPreference = 'Stop'
if ($Quick) { $Repeat = 4 }

$here   = $PSScriptRoot
$out    = Join-Path $here 'build'
$scratch = Join-Path $out 'diag'
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$report = Join-Path $out 'zig-diagnostic.txt'

# THE REPORT IS WRITTEN AS IT GOES, not assembled and saved at the end. The first real run of this
# file died part way through section C, and because the save was the last statement it left NO FILE
# AT ALL - so everything sections A and B had already established was lost and the run had to be
# repeated (owner-reported, 2026-08-25). A diagnostic is the one kind of program that must assume it
# will not reach its own last line.
$lines = New-Object System.Collections.Generic.List[string]
New-Item -ItemType File -Force -Path $report | Out-Null
function Say {
    param([string]$Text = '')
    Write-Host $Text
    $script:lines.Add($Text)
    Add-Content -Path $script:report -Value $Text -Encoding Ascii
}
function Head { param([string]$Text)
    Say ''
    Say ("== $Text " + ('=' * [Math]::Max(0, 74 - $Text.Length)))
}

# The same stderr discipline as build-stub.ps1: under Windows PowerShell 5.1, capturing a native
# command's stderr with 2>&1 while $ErrorActionPreference is 'Stop' turns the first warning into a
# terminating error and the exit code is never read. A diagnostic that dies on the first warning
# from the thing it is diagnosing would be worse than useless.
function Run {
    param([string]$Exe, [string[]]$Arguments, [hashtable]$Environment)

    $saved = @{}
    if ($Environment) {
        foreach ($k in $Environment.Keys) {
            $saved[$k] = [Environment]::GetEnvironmentVariable($k)
            [Environment]::SetEnvironmentVariable($k, $Environment[$k])
        }
    }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $raw  = & $Exe @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    catch { $raw = @($_.Exception.Message); $code = -999 }
    finally {
        $ErrorActionPreference = $previous
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
        $sw.Stop()
    }
    return [pscustomobject]@{
        ExitCode = $code
        Output   = @($raw | ForEach-Object { $_.ToString() })
        Ms       = [int]$sw.Elapsed.TotalMilliseconds
    }
}

# POWERSHELL'S [uint32] CAST IS CHECKED, NOT A REINTERPRET. `[uint32]-1073741819` THROWS
# "Value was either too large or too small for a UInt32" rather than giving 0xC0000005, and under
# $ErrorActionPreference = 'Stop' that ends the script - which is exactly what happened on the first
# real run of this file, at the first crash it was written to measure (owner-reported, 2026-08-25).
# Masking to 32 bits through a LONG first is what actually reinterprets the sign bit.
#
# Note what a dry run could NOT catch here: the machine it was rehearsed on never crashed, so this
# line never executed. A diagnostic's error path only runs when the fault it exists for occurs.
function ToNtStatus {
    param([int]$Code)
    return [uint32]($Code -band 0xFFFFFFFFL)
}

function CodeName {
    param([int]$Code)
    if ($Code -eq 0)    { return 'ok' }
    if ($Code -eq -999) { return 'could not start' }
    if ($Code -lt 0)    { return ('CRASH 0x{0:X8}' -f (ToNtStatus $Code)) }
    return "exit $Code"
}

# Repeat one command and summarise. The point of every measurement in this file: a rate, not a
# verdict from one roll of the dice.
function Probe {
    param([string]$Label, [string]$Exe, [string[]]$Arguments, [hashtable]$Environment,
          [int]$Times = $Repeat, [switch]$FreshOut)

    $ok = 0; $codes = @{}; $ms = @(); $sample = @()
    $freeOk = @(); $freeBad = @()
    for ($i = 1; $i -le $Times; $i++) {
        if ($FreshOut) { Get-ChildItem $scratch -Filter 'probe*' -ErrorAction SilentlyContinue | Remove-Item -Force -Recurse -ErrorAction SilentlyContinue }
        $freeBefore = FreeMb
        $r = Run -Exe $Exe -Arguments $Arguments -Environment $Environment
        $n = CodeName $r.ExitCode
        if ($null -ne $freeBefore) {
            if ($r.ExitCode -eq 0) { $freeOk += $freeBefore } else { $freeBad += $freeBefore }
        }
        if ($r.ExitCode -eq 0) { $ok++ }
        if (-not $codes.ContainsKey($n)) { $codes[$n] = 0 }
        $codes[$n]++
        $ms += $r.Ms
        if ($sample.Count -eq 0 -and $r.Output.Count -gt 0) { $sample = $r.Output }
    }
    $spread = ($codes.Keys | Sort-Object | ForEach-Object { "$_ x$($codes[$_])" }) -join ', '
    $avg = 0; if ($ms.Count -gt 0) { $avg = [int](($ms | Measure-Object -Average).Average) }
    Say ("  {0,-46} {1,2}/{2,-2} ok  {3,6} ms  {4}" -f $Label, $ok, $Times, $avg, $spread)
    if ($sample.Count -gt 0) {
        Say ("      first output: " + (($sample | Select-Object -First 3) -join ' | '))
    }
    # CRASHES ARE COUNTED SEPARATELY FROM REFUSALS, because "0 of 4 succeeded" reads the same for a
    # compiler that fell over and one that correctly rejected the code, and telling those two apart
    # is the entire question this file exists to answer.
    $crashed = 0
    foreach ($k in $codes.Keys) { if ($k -like 'CRASH*') { $crashed += $codes[$k] } }

    $mOk = $null; $mBad = $null
    if ($freeOk.Count  -gt 0) { $mOk  = [int](($freeOk  | Measure-Object -Average).Average) }
    if ($freeBad.Count -gt 0) { $mBad = [int](($freeBad | Measure-Object -Average).Average) }
    if ($null -ne $mOk -and $null -ne $mBad) {
        Say ("      free MB before: {0} when it worked, {1} when it did not" -f $mOk, $mBad)
    }
    return [pscustomobject]@{ Label = $Label; Ok = $ok; Times = $Times; Codes = $codes
                              AvgMs = $avg; Crashed = $crashed
                              FreeOk = $freeOk; FreeBad = $freeBad }
}

# FREE MEMORY, SAMPLED BEFORE EVERY SINGLE INVOCATION.
#
# The release box has 8 GB total and was showing 3.5 GB free. An allocation that fails and is not
# checked becomes a null dereference, and a null dereference on Windows is 0xC0000005 - the exact
# code seen, silent, at random, on a machine under memory pressure and not on one with room. That
# makes memory the leading hypothesis, and it is testable for free: record what was available before
# each attempt and compare the attempts that crashed with the ones that did not.
$script:memWorks = $true
function FreeMb {
    if (-not $script:memWorks) { return $null }
    try { return [int]((Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).FreePhysicalMemory / 1KB) }
    catch { $script:memWorks = $false; return $null }
}

function Get-PeMachine {
    param([string]$Path)
    try {
        $b = [System.IO.File]::ReadAllBytes($Path)
        $pe = [BitConverter]::ToInt32($b, 0x3C)
        if ($b[$pe] -ne 0x50 -or $b[$pe+1] -ne 0x45) { return 'not a PE' }
        $m = [BitConverter]::ToUInt16($b, $pe + 4)
        switch ($m) {
            0x8664  { return 'x64' }
            0xAA64  { return 'arm64' }
            0x014C  { return 'x86' }
            default { return ('0x{0:X4}' -f $m) }
        }
    } catch { return 'unreadable' }
}


# == A. What this machine is ===================================================

Head 'A. Environment'

$hostRaw = $env:PROCESSOR_ARCHITECTURE
if ($env:PROCESSOR_ARCHITEW6432) { $hostRaw = $env:PROCESSOR_ARCHITEW6432 }
Say "  host architecture      : $hostRaw"
Say "  PowerShell             : $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))"
Say "  OS                     : $([Environment]::OSVersion.VersionString)"
try {
    $os = Get-CimInstance Win32_OperatingSystem
    Say ("  memory free / total    : {0:N0} MB / {1:N0} MB" -f ($os.FreePhysicalMemory/1KB), ($os.TotalVisibleMemorySize/1KB))
} catch { Say '  memory                 : unavailable' }

# CRF_ZIG first, so this can measure a REPLACEMENT zig rather than only the broken one - which is
# the whole point of having found that the fault is in zig itself.
$zigs = @()
if ($env:CRF_ZIG) {
    if (Test-Path $env:CRF_ZIG) { $zigs = @(Get-Item $env:CRF_ZIG) }
    else { $zigs = @(Get-Command $env:CRF_ZIG -ErrorAction SilentlyContinue) }
    Say "  CRF_ZIG                : $($env:CRF_ZIG)"
}
if ($zigs.Count -eq 0) { $zigs = @(Get-Command zig -All -ErrorAction SilentlyContinue) }
if ($zigs.Count -eq 0) {
    Say '  zig                    : NOT ON PATH - nothing further can be measured.'
    Set-Content -Path $report -Value $lines -Encoding Ascii
    return
}
Say "  zig on PATH            : $($zigs.Count) found"
foreach ($z in $zigs) {
    # Get-Command yields .Source, Get-Item yields .FullName; take whichever is present.
    if (-not $z.PSObject.Properties['Source'] -or -not $z.Source) {
        $z | Add-Member -NotePropertyName Source -NotePropertyValue $z.FullName -Force
    }
    # ZIG'S OWN ARCHITECTURE, read out of the PE rather than taken from `zig env`. An x86_64 zig on
    # an ARM64 host runs under emulation, which is a known way to get exactly this crash - and it is
    # the one thing `zig env`'s self-reported target does not distinguish from a native build.
    Say ("    {0}  [{1}]" -f $z.Source, (Get-PeMachine $z.Source))
}
$zigExe = $zigs[0].Source

foreach ($v in 'ZIG_GLOBAL_CACHE_DIR', 'ZIG_LOCAL_CACHE_DIR', 'ZIG_LIB_DIR') {
    Say ("  {0,-22} : {1}" -f $v, [Environment]::GetEnvironmentVariable($v))
}
$cacheDirs = @()
foreach ($base in $env:LOCALAPPDATA, $env:APPDATA) {
    if ($base) { $cacheDirs += (Join-Path $base 'zig') }
}
foreach ($c in $cacheDirs) {
    if (Test-Path $c) {
        $n = @(Get-ChildItem $c -Recurse -File -ErrorAction SilentlyContinue)
        Say ("  cache {0,-16} : {1} files, {2:N0} MB" -f (Split-Path $c -Leaf), $n.Count, (($n | Measure-Object Length -Sum).Sum/1MB))
    }
}

$vsRoot = ${env:ProgramFiles(x86)}
$vswhere = $null
if ($vsRoot) { $vswhere = Join-Path $vsRoot 'Microsoft Visual Studio\Installer\vswhere.exe' }
if ($vswhere -and (Test-Path $vswhere)) {
    $vs = (Run $vswhere @('-latest','-products','*','-prerelease','-property','installationPath')).Output |
          Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($vs) {
        $hasCxx = Test-Path (Join-Path $vs 'VC\Auxiliary\Build\vcvarsall.bat')
        Say "  Visual Studio          : $vs (C++ tools: $hasCxx)"
    } else { Say '  Visual Studio          : vswhere found no installation' }
} else { Say '  Visual Studio          : not installed (no vswhere.exe)' }


# == B. Can zig start at all? ==================================================
#
# If `zig version` crashes, the fault is in process start-up and has nothing to do with compiling,
# with a target, or with anything this repository does. That single result would redirect the whole
# investigation, so it is measured first and it is measured REPEATEDLY.

Head 'B. Does zig start reliably? (no compiling at all)'
$bVersion = Probe 'zig version'      $zigExe @('version')
$bEnv     = Probe 'zig env'          $zigExe @('env')
$bTargets = Probe 'zig targets'      $zigExe @('targets') -Times ([Math]::Min($Repeat,4))


# == C. Is it target-specific? =================================================
#
# The claim to test is that all three targets fail at the SAME rate. If they do, no amount of
# per-target flag work will help. A trivial main() is used so that nothing about circuitRF's stub
# is in the picture.

Head 'C. Trivial C program, one section per target'
$trivialC = Join-Path $scratch 'trivial.c'
Set-Content -Path $trivialC -Value 'int main(void){return 0;}' -Encoding Ascii

$cResults = @()
foreach ($t in 'x86_64-windows-gnu', 'aarch64-windows-gnu', 'x86-windows-gnu') {
    $exe = Join-Path $scratch "probe-$t.exe"
    $cResults += Probe $t $zigExe @('cc','-target',$t,'-O2',$trivialC,'-o',$exe)
}


# == D. Which STAGE crashes? ===================================================
#
# Three points on the same pipeline, so a crash can be placed rather than described:
#   -c            compile only. No link, no mingw CRT, no import libraries.
#   full link     the same plus the link, which is where zig builds and links libc for the target.
#   build-exe     zig's own language, no C, no clang. Isolates the C path from zig's pipeline.

Head 'D. Which stage? (worst target from section C)'
$worst = ($cResults | Sort-Object { $_.Ok / [double]$_.Times } | Select-Object -First 1)
$wt = $worst.Label
Say "  using target $wt (lowest success rate above)"

$obj = Join-Path $scratch 'probe.obj'
$dExe = Join-Path $scratch 'probe-stage.exe'
$dCompile = Probe 'compile only (-c), no link' $zigExe @('cc','-target',$wt,'-O2','-c',$trivialC,'-o',$obj)
$dLink    = Probe 'compile and link'           $zigExe @('cc','-target',$wt,'-O2',$trivialC,'-o',$dExe)

$trivialZig = Join-Path $scratch 'trivial.zig'
Set-Content -Path $trivialZig -Value 'pub fn main() void {}' -Encoding Ascii
# -fno-emit-bin: this probe is asking whether zig SURVIVES, not what it produces, and 0.16 has no
# -femit-bin= to redirect the output with (it lands beside the source otherwise). Verified against
# zig 0.16.0 rather than assumed - the first spelling here was rejected outright.
$dZig = Probe 'zig build-exe (no C, no clang)' $zigExe @('build-exe',$trivialZig,'-target',$wt,'-fno-emit-bin')


# == E. Does any FLAG bring it on? =============================================
#
# Added one at a time, on the real stub source, so the answer is about this build and not a
# hypothetical one. If the rate is flat across the whole ladder, no flag is implicated - which is
# itself the finding, and it is the one that stops the next person shuffling flags for a day.

Head 'E. Flag bisect on the real stub source'
$src = Join-Path $here 'circuitrf-stub.c'
$eExe = Join-Path $scratch 'probe-stub.exe'
$base = @('cc','-target',$wt,'-O2','-DCRF_APP_NAME=circuitRF',$src,'-o',$eExe)
# THE FIRST ROW IS A CONTROL AND IS MEANT TO FAIL. Without -municode the mingw CRT looks for
# WinMain and the link refuses - loudly, on stderr, with a reason, exit 1. It is here precisely
# because it proves this harness can tell a refusal from a crash: if the control ever reports CRASH,
# the readings below it mean nothing.
$eSteps = @(
    @{ L = 'CONTROL: bare, must refuse'; A = $base },
    @{ L = '+ -municode';              A = $base + @('-municode') },
    @{ L = '+ -Wl,--subsystem,windows';A = $base + @('-municode','-Wl,--subsystem,windows') },
    @{ L = '+ -luser32';               A = $base + @('-municode','-Wl,--subsystem,windows','-luser32') },
    @{ L = '+ -mcpu=baseline (as ship)'; A = @('cc','-target',$wt,'-mcpu=baseline','-O2','-municode','-Wl,--subsystem,windows','-DCRF_APP_NAME=circuitRF',$src,'-o',$eExe,'-luser32') }
)
$eResults = @()
foreach ($step in $eSteps) {
    $eResults += Probe $step.L $zigExe $step.A -Times ([Math]::Min($Repeat,6))
}


# == F. Is the shared cache implicated? ========================================
#
# A crash part-way through writing %LOCALAPPDATA%\zig can leave a half-written entry, so the cache
# is both a possible cause and a known casualty. Same command, two caches.

Head 'F. Shared cache versus a private one'
$privateCache = Join-Path $scratch 'cache'
$fShared  = Probe 'shared cache (%LOCALAPPDATA%\zig)' $zigExe @('cc','-target',$wt,'-mcpu=baseline','-O2',$trivialC,'-o',(Join-Path $scratch 'probe-shared.exe')) -Times ([Math]::Min($Repeat,6))
$fPrivate = Probe 'private cache' $zigExe @('cc','-target',$wt,'-mcpu=baseline','-O2',$trivialC,'-o',(Join-Path $scratch 'probe-private.exe')) @{ ZIG_GLOBAL_CACHE_DIR = $privateCache; ZIG_LOCAL_CACHE_DIR = $privateCache } ([Math]::Min($Repeat,6))


# == G. What Windows recorded about the crash ==================================
#
# THE HIGHEST-VALUE SECTION IF IT HAS ANYTHING IN IT. A crash with no output from the process still
# leaves a Windows Error Reporting entry naming the FAULTING MODULE and offset. That distinguishes
# a fault inside zig.exe from one inside a DLL loaded into it - an anti-malware or EDR shim being
# the case worth ruling out, because no zig version would fix that and it would explain a fault
# that is random, silent, and specific to one machine.

Head 'G. Windows crash records for zig'
try {
    $events = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000, 1001 } -MaxEvents 250 -ErrorAction Stop |
              Where-Object { $_.Message -match 'zig' }
    if ($events) {
        foreach ($e in ($events | Select-Object -First 8)) {
            Say ("  {0}  Id={1}" -f $e.TimeCreated, $e.Id)
            foreach ($l in ($e.Message -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 6)) {
                Say "      $l"
            }
        }
    } else { Say '  no Application-log crash records mention zig' }
} catch { Say "  could not read the Application log: $($_.Exception.Message)" }


# == Summary ===================================================================
#
# Observations only. Every line below is a reading of the numbers above, and each names what it
# would mean - deliberately not a single verdict, because the last single verdict here was wrong.

Head 'Summary'

$startsOk = ($bVersion.Ok -eq $bVersion.Times) -and ($bEnv.Ok -eq $bEnv.Times)
if (-not $startsOk) {
    Say "  * zig CANNOT RELIABLY START: 'zig version' $($bVersion.Ok)/$($bVersion.Times), 'zig env' $($bEnv.Ok)/$($bEnv.Times)."
    Say '    The fault is in process start-up, not in compiling. Nothing about targets, flags,'
    Say '    caches or circuitRF is involved. Section G, and reinstalling zig, are the leads.'
} else {
    Say '  * zig starts reliably, so the fault is in compiling and not in start-up.'
}

Say ''
Say '  per-target success rate (trivial C):'
foreach ($r in $cResults) { Say ("    {0,-24} {1}/{2} ok, {3} crashed" -f $r.Label, $r.Ok, $r.Times, $r.Crashed) }
$rates = $cResults | ForEach-Object { $_.Ok / [double]$_.Times }
$spread = ($rates | Measure-Object -Maximum).Maximum - ($rates | Measure-Object -Minimum).Minimum
if ($spread -le 0.34) {
    Say '    -> comparable across targets: NOT target-specific. Per-target flag work cannot help.'
} else {
    Say '    -> the targets differ materially. Worth re-testing before believing it: at a low'
    Say '       success rate this spread also arises by chance.'
}

Say ''
Say ("  stage: -c only {0}/{1}, compile+link {2}/{3}, zig build-exe {4}/{5}" -f `
     $dCompile.Ok, $dCompile.Times, $dLink.Ok, $dLink.Times, $dZig.Ok, $dZig.Times)
if ($dCompile.Ok -eq $dCompile.Times -and $dLink.Ok -lt $dLink.Times) {
    Say '    -> compiling is clean and LINKING crashes: the fault is in the link stage, which is'
    Say '       where zig builds and links libc for the target.'
} elseif ($dCompile.Ok -lt $dCompile.Times) {
    Say '    -> even -c crashes, so the fault is before the link: it is not about libc or mingw.'
}
if ($dZig.Ok -lt $dZig.Times) {
    Say '    -> zig build-exe crashes too, with no C and no clang: the fault is in zig itself,'
    Say '       not in its clang front end.'
} elseif ($dZig.Ok -eq $dZig.Times -and $dLink.Ok -lt $dLink.Times) {
    Say '    -> zig build-exe is clean while zig cc is not: the fault is on the C path.'
}

Say ''
Say '  flags:'
foreach ($r in $eResults) { Say ("    {0,-30} {1}/{2} ok, {3} crashed" -f $r.Label, $r.Ok, $r.Times, $r.Crashed) }
Say ("  cache: shared {0}/{1}, private {2}/{3}" -f $fShared.Ok, $fShared.Times, $fPrivate.Ok, $fPrivate.Times)

Say ''
$total = ($cResults + @($dCompile,$dLink,$dZig) + $eResults + @($fShared,$fPrivate))

# The memory verdict, over EVERY attempt in the run rather than per section, because per section the
# sample is far too small to mean anything.
$allOk  = @(); $allBad = @()
foreach ($r in $total) { $allOk += $r.FreeOk; $allBad += $r.FreeBad }
if ($allOk.Count -gt 0 -and $allBad.Count -gt 0) {
    $avgOk  = [int](($allOk  | Measure-Object -Average).Average)
    $avgBad = [int](($allBad | Measure-Object -Average).Average)
    $minAny = [int]((($allOk + $allBad) | Measure-Object -Minimum).Minimum)
    Say ("  memory: {0} MB free on average before an attempt that worked, {1} MB before one that" -f $avgOk, $avgBad)
    Say ("          did not; lowest seen {2} MB. ({0} good samples, {1} bad.)" -f $allOk.Count, $allBad.Count, $minAny)
    if ($avgBad -lt ($avgOk * 0.8)) {
        Say '    -> failures happened with materially LESS memory free. Memory pressure is then the'
        Say '       best available explanation: an allocation that fails and is not checked becomes a'
        Say '       null dereference, and that is 0xC0000005 exactly. Worth re-running with more free.'
    } else {
        Say '    -> no memory difference between the attempts that worked and those that did not,'
        Say '       so memory pressure does not explain it.'
    }
}
$tOk = ($total | Measure-Object Ok -Sum).Sum
$tAll = ($total | Measure-Object Times -Sum).Sum
$tCrash = ($total | Measure-Object Crashed -Sum).Sum
Say ("  overall: $tOk of $tAll invocations succeeded; $tCrash CRASHED (the rest refused the code,")
Say '           which is a compiler working correctly and is what the CONTROL row above is for).'
if ($tCrash -eq 0) {
    Say '  -> NOTHING CRASHED during this run. That is a result, not a blank: either the machine'
    Say '     has changed since the release build, or the fault needs the load a real build puts'
    Say '     on it. Re-run the packaging script and, if it crashes there, run this immediately'
    Say '     afterwards so section G still has the crash records.'
}

Say ''
Say "Report written to $report"
Set-Content -Path $report -Value $lines -Encoding Ascii
