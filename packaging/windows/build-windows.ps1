<#
  == circuitRF Windows installer builder ========================================

    .\packaging\windows\build-windows.ps1

  With no arguments it builds EVERYTHING this platform ships - all three architectures, both
  install scopes, and the update payload:

    dist\circuitRF-<VERSION>-x64.msi              perMachine, %ProgramFiles%       notify-only
    dist\circuitRF-<VERSION>-arm64.msi
    dist\circuitRF-<VERSION>-x86.msi
    dist\circuitRF-<VERSION>-win-x64-user.msi     perUser, %LOCALAPPDATA%          updates itself
    dist\circuitRF-<VERSION>-win-arm64-user.msi
    dist\circuitRF-<VERSION>-win-x86-user.msi
    dist\circuitRF-<VERSION>-win-x64.zip          the update payload
    dist\circuitRF-<VERSION>-win-arm64.zip
    dist\circuitRF-<VERSION>-win-x86.zip

  Narrow it only when you mean to:

    .\packaging\windows\build-windows.ps1 -Arch x64
    .\packaging\windows\build-windows.ps1 -Scope perUser
    .\packaging\windows\build-windows.ps1 -Arch arm64 -Scope perMachine

  WHY THE DEFAULT IS EVERYTHING. This script used to build ONE architecture in ONE scope per run,
  which meant a complete Windows release was six invocations and looked like three. A release cut
  that way ships the notify-only .msi files and silently omits the .zip the updater fetches - so
  nobody on Windows is offered the next version, and, because UpdateSelector needs a matching asset
  before it will even post the notify-only line, nobody is TOLD about it either. That happened
  (1.0.0-beta.2). A release script whose obvious invocation produces an incomplete release is the
  script's bug, not the operator's, so the obvious invocation now produces all of it.

  ONE PUBLISH SERVES BOTH SCOPES. The two differ in where the files go and what the shortcuts point
  at, never in the files themselves, so an architecture is published once and packaged twice. That
  is also why -Scope narrows the packaging and not the build.

  <VERSION> is the contents of the repo-root VERSION file; see BUILDING.md.

  Requires: .NET 10 SDK and the WiX CLI -

      dotnet tool install --global wix

  The WixToolset.UI.wixext extension is installed BY THIS SCRIPT if it is missing, pinned to the
  wix version actually on PATH. Adding it by hand is what produced "error WIX0144: The extension
  'WixToolset.UI.wixext' could not be found": the extension cache is keyed by wix version, so an
  extension added under one wix and a `dotnet tool update` later do not see each other.

  Run it from the repository root, in PowerShell.

  ------------------------------------------------------------------------------
  THIS FILE MUST STAY PURE ASCII. Windows PowerShell 5.1 reads a .ps1 with no byte-order mark as
  ANSI (cp1252), not UTF-8. A UTF-8 emoji or box-drawing character then decodes to bytes 0x93/0x94,
  which are the CURLY QUOTES U+201C/U+201D - and PowerShell honours those as string delimiters. The
  script does not fail: it silently reinterprets as a string everything from the stray quote to the
  next one, PRINTS that block instead of running it, and carries on. That is what made an entire
  publish step vanish while the build appeared to continue. tests/Ui.Tests/PackagingScriptTests.cs
  holds this shut.
  ------------------------------------------------------------------------------
#>

[CmdletBinding()]
param(
    # 'all' is the default and is what a release uses. Naming one narrows the run.
    [ValidateSet('all', 'x64', 'arm64', 'x86')]
    [string]$Arch = 'all',

    # perMachine is %ProgramFiles% and notify-only; perUser is %LOCALAPPDATA% and updates itself.
    # 'all' builds both, which is what a release ships.
    [ValidateSet('all', 'perMachine', 'perUser')]
    [string]$Scope = 'all',

    # Defaults to the repo-root VERSION file - the one place the version is written. Override only
    # for a one-off build; nothing is written back.
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')

# An explicit -Version wins; otherwise version.ps1 reads the repo-root VERSION file. Either way the
# numeric $CrfMsiVersion is derived in one place.
if ($Version) { $CrfVersion = $Version }
. (Join-Path $PSScriptRoot '..\version.ps1')

$dist = Join-Path $root 'dist'

# The order matters only for the log: perMachine first so the notify-only artifacts appear before
# the self-updating ones, which is the order BUILDING.md's table lists them in.
$arches = if ($Arch  -eq 'all') { @('x64', 'arm64', 'x86') }   else { @($Arch) }
$scopes = if ($Scope -eq 'all') { @('perMachine', 'perUser') } else { @($Scope) }

# The shipped executable is circuitRF.exe, NOT CircuitRF.Ui.exe. The assembly is still called
# CircuitRF.Ui (RfCore grants it InternalsVisibleTo, so the assembly name cannot change), but
# src/Ui/CircuitRF.Ui.csproj renames the native host after publish - see its CrfRenameApphost
# target. Keep this in step with that target.
$exeName = 'circuitRF.exe'

$built = @()
$stubFailures = @()
$consoleFailures = @()
$unsmoked = @()

# == Tool checks ===============================================================
#
# Both of these used to fail deep inside the build with a message that named the symptom rather
# than the cause. Check them up front, where the fix can be printed next to the failure.

foreach ($tool in 'dotnet', 'wix') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "'$tool' is not on PATH. See BUILDING.md; for wix: dotnet tool install --global wix"
    }
}

Write-Host 'Checking the WiX UI extension...'

# WIX0144 ("The extension 'WixToolset.UI.wixext' could not be found") is almost never a missing
# install - it is a VERSION MISMATCH. The extension cache is keyed by wix version, so the setup line
# people ran once, followed by any `dotnet tool update --global wix`, leaves an extension the new wix
# cannot see. Resolving the reference here, against the wix actually on PATH, is what stops it.

# EVERY ONE OF THESE CALLS CAPTURES STDERR, and doing that with the preference on 'Stop' is a trap
# rather than a capture: under Windows PowerShell 5.1, merging a NATIVE command's stderr into the
# success stream while $ErrorActionPreference is 'Stop' turns the first stderr line into a
# TERMINATING error. It never reaches $LASTEXITCODE, and the exit code can be 0. So one incidental
# line from wix - a NuGet notice, a first-run message - would end the entire Windows release at its
# first step, blaming a tool check. The same trap took out the x86 launcher stub for real
# (owner-reported, 2026-08-25; see build-stub.ps1's Invoke-Compiler and
# tests/Ui.Tests/PackagingScriptTests.cs, which now holds this shut).
#
# 'Continue' for the duration of the block, and decide from the EXIT CODE, which is what it is for.
$previousEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'

$wixVersion = $null
$versionText = (& wix --version 2>&1) -join ' '
if ($versionText -match '(\d+\.\d+\.\d+)') { $wixVersion = $Matches[1] }

$installed = (& wix extension list --global 2>&1) -join "`n"
$extensionRef = $null

if ($wixVersion -and $installed -match "WixToolset\.UI\.wixext[\s/]+$([regex]::Escape($wixVersion))") {
    $extensionRef = "WixToolset.UI.wixext/$wixVersion"          # exactly what this wix wants
}
elseif ($wixVersion) {
    $extensionRef = "WixToolset.UI.wixext/$wixVersion"
    Write-Host "  installing $extensionRef ..."
    & wix extension add --global $extensionRef 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { $extensionRef = $null }          # e.g. a preview wix with no matching package
}

$wixInstallFailed = $false
if (-not $extensionRef) {
    $extensionRef = 'WixToolset.UI.wixext'
    if ($installed -notmatch 'WixToolset\.UI\.wixext') {
        Write-Host "  installing $extensionRef ..."
        & wix extension add --global $extensionRef 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { $wixInstallFailed = $true }
    }
}

# Put it back before anything else runs. The rest of this script relies on 'Stop' - a failed publish
# or a failed wix build must end the run rather than be carried past - so the relaxation has to be
# exactly as wide as the block that needs it. The throw is deferred to here for the same reason:
# thrown above, it would escape while the preference was still relaxed.
$ErrorActionPreference = $previousEap

if ($wixInstallFailed) {
    throw "Could not install the WiX UI extension. Run it by hand: wix extension add --global $extensionRef"
}

# == Icons =====================================================================
#
# The icon must exist BEFORE the publish: the .csproj embeds Assets\circuitRFIcon.ico into the
# executable only if it is on disk, and that embedded icon is what Explorer draws for the .exe.

Write-Host 'Building icons...'
dotnet run --project (Join-Path $root 'tools\IconGen') -- circuitrf
if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }

# == The packaging smoke check ==================================================
#
# Built once, run once per architecture against what that architecture's installers will contain -
# see "The command line, run out of THIS publish tree" below. Release, so it shares the libraries
# the publishes build rather than compiling a Debug copy of the whole stack beside them.
Write-Host 'Building the packaging smoke check (tools\CliSmoke)...'
dotnet build (Join-Path $root 'tools\CliSmoke') -c Release -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Could not build tools\CliSmoke.' }
$smokeDll = Join-Path $root 'tools\CliSmoke\bin\Release\net10.0\CliSmoke.dll'

# == The geometry kernel (brief-em3d-62 R-em3d62-5) ==============================
#
# OpenCASCADE behind tools\geometry-worker, in <publish>\geometry-kernel\. 'dotnet build' only ever
# COPIES it out of the per-user cache and warns when the cache is empty - right for a build, wrong
# for a release - so this script BUILDS it per architecture with --strict, and fails at the end when
# an architecture that ships it (recipe.env's KERNEL_RIDS, decision D2) came out without it. The
# first run on a machine builds OCCT itself: minutes per architecture, once, with llvm-mingw, CMake
# and Ninja - no Visual Studio. An architecture D2 leaves out gets no kernel even if the cache has one.
#
# Set CRF_ALLOW_NO_KERNEL=1 to package without it on purpose (nothing is fetched or built then).
$kernelRecipe = Join-Path $root 'tools\geometry-worker\occt\recipe.env'
$recipeLines  = Get-Content -LiteralPath $kernelRecipe
$occtVersion  = (($recipeLines | Where-Object { $_ -match '^OCCT_VERSION=' }) -replace '^OCCT_VERSION=', '')
$kernelRids   = (($recipeLines | Where-Object { $_ -match '^KERNEL_RIDS=' }) -replace '^KERNEL_RIDS=', '') -split ' '
$noKernel     = @()
$kernelLeftOut = @()
$kernelFail   = $false

# == The gdstk worker (brief-oasis-gdstk.md 4e) ===================================
#
# gdstk, qhull and zlib in ONE statically linked program, <publish>\gdstk-kernel\gdstk-worker.exe: the
# OASIS import and export and the second GDSII route. The geometry kernel's rule exactly: built per
# architecture with --strict (well under a minute, once, with the same llvm-mingw, CMake and Ninja), and
# the run fails at the end when an architecture in tools\gdstk-worker\recipe.env's KERNEL_RIDS came out
# without it. Set CRF_ALLOW_NO_GDSTK=1 to package without it on purpose.
$gdstkRecipeLines = Get-Content -LiteralPath (Join-Path $root 'tools\gdstk-worker\recipe.env')
$gdstkVersion = (($gdstkRecipeLines | Where-Object { $_ -match '^GDSTK_VERSION=' }) -replace '^GDSTK_VERSION=', '')
$gdstkRids    = (($gdstkRecipeLines | Where-Object { $_ -match '^KERNEL_RIDS=' }) -replace '^KERNEL_RIDS=', '') -split ' '
$noGdstk      = @()
$gdstkFail    = $false

# == The kernel's toolchain, checked once, and offered for install ================
#
# tools\geometry-worker\find-toolchain.cmd is the one place that looks for llvm-mingw, CMake and Ninja
# (and says why those, not Visual Studio); this asks it rather than looking a second way. When any is
# missing and someone is at the keyboard, it offers to install exactly those with winget, instead of
# letting every architecture fail the same way minutes later. Declined, or with nobody to ask, the run
# carries on: build.cmd refuses per architecture, and the summary at the end says so.
# The gdstk worker builds with the same three tools, so one check covers both.
$kernelArches = @($arches | Where-Object { $kernelRids -contains "win-$_" -and $env:CRF_ALLOW_NO_KERNEL -ne '1' })
$gdstkArches  = @($arches | Where-Object { $gdstkRids -contains "win-$_" -and $env:CRF_ALLOW_NO_GDSTK -ne '1' })
$toolArches   = @($kernelArches + $gdstkArches | Select-Object -Unique)
if ($toolArches.Count -gt 0) {
    $findToolchain = Join-Path $root 'tools\geometry-worker\find-toolchain.cmd'
    $toolRid = "win-$($toolArches[0])"
    function Get-KernelToolGaps {
        $gaps = @{ missing = ''; winget = '' }
        foreach ($line in (& $findToolchain $toolRid --report)) {
            if ($line -match '^(missing|winget)=(.*)$') { $gaps[$Matches[1]] = $Matches[2].Trim() }
        }
        return $gaps
    }

    $gaps = Get-KernelToolGaps
    if ($gaps.missing) {
        $ids = @($gaps.winget -split ' ' | Where-Object { $_ })
        Write-Host ''
        Write-Host "The geometry kernel and the gdstk worker need tools this machine does not have: $($gaps.missing)."
        Write-Host '  It builds with llvm-mingw, CMake and Ninja - no Visual Studio.'
        $canAsk = [Environment]::UserInteractive -and -not [Console]::IsInputRedirected
        $haveWinget = [bool](Get-Command winget -ErrorAction SilentlyContinue)
        $answer = 'n'
        if ($canAsk -and $haveWinget) {
            $answer = Read-Host "  Install them now with winget ($($ids -join ', '))? [Y/n]"
        }
        if ($answer -eq '' -or $answer -match '^[Yy]') {
            # 'Continue' while winget runs: under Windows PowerShell 5.1 a native command's stderr line
            # is otherwise a TERMINATING error (the WiX section above says why).
            $previousEap = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            foreach ($id in $ids) {
                Write-Host "winget install -e --id $id"
                & winget install -e --id $id --accept-package-agreements --accept-source-agreements
                if ($LASTEXITCODE -ne 0) { Write-Host "  winget exited $LASTEXITCODE for $id (already installed is one such case)." }
            }
            $ErrorActionPreference = $previousEap
            # winget changes the STORED PATH, not this session's. Append it, so build.cmd sees the tools.
            $env:Path = $env:Path + ';' + [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
            $gaps = Get-KernelToolGaps
            if ($gaps.missing) {
                Write-Host "Still not found: $($gaps.missing). The kernel will not build; reported again at the end."
            } else {
                Write-Host 'The kernel toolchain is installed.'
            }
        } else {
            Write-Host '  Not installing. To install them yourself, then open a new terminal:'
            foreach ($id in $ids) { Write-Host "    winget install -e --id $id" }
            Write-Host '  Continuing; the kernel will not build, and that is reported again at the end.'
        }
        Write-Host ''
    }
}

# Which of the three this machine can EXECUTE. Windows on ARM runs all three (x64 and x86 under
# emulation); an x64 machine runs x64 and x86 but has no way to run arm64 at all.
$hostArch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }

# == The harvester ==============================================================
#
# Defined once, above the loop, because PowerShell resolves a function only after its definition
# has been executed. It writes Files.wxs from the publish tree: everything except, for perMachine,
# the .exe that circuitRF.wxs names itself so the shortcuts and file associations can point at it.
#
# Harvesting rather than listing by hand is what keeps the installer correct when the published set
# changes - the app ships its user documentation as loose files, and the native SkiaSharp/HarfBuzz
# DLLs come and go with the single-file settings.

# -Force on BOTH enumerations below, and it is not belt-and-braces. A workspace's manifest is a
# dotfile named literally `.cws`, and so is `.ccell` beside every cell - Windows does not infer the
# hidden ATTRIBUTE from a leading dot, but anything that has ever touched these files with a tool
# that does (a git checkout under some shells, an extracted archive, a synced folder) leaves it set,
# and without -Force those files are skipped in silence. The installer would then carry the example
# workspaces' cells and none of their manifests, and File > New > Examples would be empty on the
# installed copy with nothing anywhere saying why. The examples are the reason it is here; every
# other published file is unaffected.
function Add-Directory($path, $parentId) {
    foreach ($file in Get-ChildItem -LiteralPath $path -File -Force | Sort-Object Name) {
        if ($script:skipExe -and $file.Name -eq $script:exeName) { continue }
        $script:compId++
        [void]$script:components.AppendLine(
            "      <Component Id=`"cmp$($script:compId)`" Directory=`"$parentId`">")
        [void]$script:components.AppendLine(
            "        <File Id=`"fil$($script:compId)`" Source=`"$($file.FullName)`" KeyPath=`"yes`" />")
        [void]$script:components.AppendLine('      </Component>')
    }
    foreach ($sub in Get-ChildItem -LiteralPath $path -Directory -Force | Sort-Object Name) {
        $script:dirId++
        $id = "dir$($script:dirId)"
        [void]$script:sb.AppendLine("      <Directory Id=`"$id`" Name=`"$($sub.Name)`">")
        Add-Directory $sub.FullName $id
        [void]$script:sb.AppendLine('      </Directory>')
    }
}

# The machine an .exe was built for, read out of its own PE header, or $null when the file is not a
# PE this can read. The Linux script does exactly this with the ELF header and for the same reason:
# a helper that quietly fell back to the BUILDING machine is present, plausible and the right size,
# and no listing of the publish tree shows the difference.
function Get-PeMachine($path) {
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $buf = New-Object byte[] 4
        if ($stream.Read($buf, 0, 2) -ne 2) { return $null }
        if ($buf[0] -ne 0x4D -or $buf[1] -ne 0x5A) { return $null }      # 'MZ'

        $stream.Position = 0x3C
        if ($stream.Read($buf, 0, 4) -ne 4) { return $null }
        $peOffset = [System.BitConverter]::ToInt32($buf, 0)
        if ($peOffset -le 0 -or $peOffset -gt ($stream.Length - 6)) { return $null }

        $stream.Position = $peOffset
        if ($stream.Read($buf, 0, 4) -ne 4) { return $null }
        if ($buf[0] -ne 0x50 -or $buf[1] -ne 0x45) { return $null }      # 'PE'

        if ($stream.Read($buf, 0, 2) -ne 2) { return $null }
        return [System.BitConverter]::ToUInt16($buf, 0)
    }
    finally { $stream.Dispose() }
}

foreach ($Arch in $arches) {

    Write-Host ''
    Write-Host "=== $Arch ==================================================================="

    $rid = "win-$Arch"
    $publish = Join-Path $root "publish\$rid"

    # == The launcher stub, and its console twin ===================================
    #
    # ONE SOURCE, TWO BUILDS (packaging\windows\stub\circuitrf-stub.c says why):
    #   circuitRF-stub-<arch>.exe     the per-user launcher - the one file in a per-user install
    #                                 that never changes. Also what the smoke check below drives,
    #                                 because it is the route an MCP client's pipes take.
    #   circuitRF-console-<arch>.com  installed as circuitRF.com beside circuitRF.exe in BOTH
    #                                 scopes, so a command TYPED in cmd or PowerShell gets a console
    #                                 and a shell that waits (brief-automation-13 R-aut13-2).
    # Built here rather than committed, for the same reason the app icons are: a binary in the
    # repository is a binary nobody can review.

    # A STUB FAILURE SKIPS THE PER-USER SCOPE - it does not abandon the whole run. It used to throw,
    # so a broken C toolchain produced ZERO artifacts from a build that could have produced the three
    # machine-wide .msi files (owner-reported, 2026-08-25: zig cc crashed with an access violation and
    # took the entire Windows release with it). A missing .com is treated the same way: the
    # installers are still built, without it, because a piped caller (every MCP client) never needed
    # it - only a person typing into a console does.
    #
    # Skipping quietly would be worse than throwing, though, because a release that is silently short
    # of the self-updating channel is the exact failure this script was rewritten to prevent. So it is
    # LOUD here, LOUD in the summary, and the script exits non-zero at the end.

    $archScopes = $scopes
    $stubExe = Join-Path $PSScriptRoot "stub\build\circuitRF-stub-$Arch.exe"
    $comExe  = Join-Path $PSScriptRoot "stub\build\circuitRF-console-$Arch.com"

    foreach ($consoleBuild in @($false, $true)) {
        try {
            & (Join-Path $PSScriptRoot 'stub\build-stub.ps1') -Arch $Arch -AppName 'circuitRF' -Console:$consoleBuild
        }
        catch {
            # Indent EVERY line. The stub script reports one line per toolchain route it tried, and
            # a message that indents only its first line reads as though the rest is this script's
            # own output rather than the reason the stub is missing.
            $_.Exception.Message -split "`r?`n" | ForEach-Object { Write-Host "  $_" }
        }
    }

    if (-not (Test-Path $stubExe) -and ($scopes -contains 'perUser')) {
        Write-Host "  No stub for $Arch, so its per-user installer is not built. Carrying on."
        $stubFailures += $Arch
        $archScopes = $scopes | Where-Object { $_ -ne 'perUser' }
    }

    if (-not (Test-Path $comExe)) {
        Write-Host "  No circuitRF.com for $Arch, so its installers are built WITHOUT it: a command typed"
        Write-Host '  in cmd or PowerShell would get no console and no shell would wait. Carrying on.'
        $consoleFailures += $Arch
    }

    if (-not $archScopes) {
        Write-Host "Nothing left to package for $Arch; moving on."
        continue
    }


    # == Publish ===================================================================

    $shipsKernel = $kernelRids -contains $rid
    if ($shipsKernel -and $env:CRF_ALLOW_NO_KERNEL -ne '1') {
        Write-Host "Building the geometry kernel ($rid) ..."
        & (Join-Path $root 'tools\geometry-worker\build.cmd') --strict --rid $rid
        if ($LASTEXITCODE -ne 0) {
            Write-Host "WARNING: the geometry kernel did not build for $rid (see above); reported again at the end."
        }
    }

    $shipsGdstk = $gdstkRids -contains $rid
    if ($shipsGdstk -and $env:CRF_ALLOW_NO_GDSTK -ne '1') {
        Write-Host "Building the gdstk worker ($rid) ..."
        & (Join-Path $root 'tools\gdstk-worker\build.cmd') --strict --rid $rid
        if ($LASTEXITCODE -ne 0) {
            Write-Host "WARNING: the gdstk worker did not build for $rid (see above); reported again at the end."
        }
    }

    Write-Host "Publishing $rid ..."
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    dotnet publish (Join-Path $root 'src\Ui\CircuitRF.Ui.csproj') `
        -c Release -r $rid --self-contained true -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    # Never harvest a tree that is not there. Without this the first symptom is a Get-ChildItem
    # "Cannot find path" from inside the harvester, which names the wrong step.
    if (-not (Test-Path (Join-Path $publish $exeName))) {
        throw "Publish produced no $exeName in $publish - nothing to package."
    }


    # == The device worker =========================================================
    #
    # The program that evaluates a kit's compiled device models. It is built by the .csproj (which runs
    # tools\senior-worker\ensure-built.cmd) and published by its CrfPublishHelperPrograms target - but
    # that build step is warn-only BY DESIGN, because nobody should be unable to build circuitRF for
    # want of a C compiler.
    #
    # That is right for a build and wrong for a RELEASE. An installer missing this ships an application
    # that opens a kit, describes it correctly, and then refuses at Run naming a program the user never
    # installed and had no way to install. So packaging checks what building only warns about.
    #
    # Both files, not just the stub: on Windows a model imports its host callbacks from a NAMED MODULE,
    # so senior_worker.exe without crf-model-host.dll beside it loads nothing.
    #
    # Set CRF_ALLOW_NO_DEVICE_WORKER=1 to package without it on purpose.

    $workerFiles = @('senior_worker.exe', 'crf-model-host.dll')
    $missing = $workerFiles | Where-Object { -not (Test-Path (Join-Path $publish $_)) }

    if ($missing) {
        $what = $missing -join ', '
        if ($env:CRF_ALLOW_NO_DEVICE_WORKER -eq '1') {
            Write-Host "WARNING: packaging without the device worker ($what). Compiled device models will not run."
        } else {
            Write-Host "Missing from the publish tree: $what"
            throw @'
The device worker is missing from the publish tree.

circuitRF builds it during 'dotnet build', but only warns when no C compiler is present - so this
machine has none that the build could use. Install one of these and run this script again:

    zig      (winget install zig.zig)   - one download, no daemon; the preferred route
    MSYS2/MinGW x86-64 gcc
    Docker or Podman, plus a bash (Git for Windows ships one)

The worker is built for x86-64 even on an ARM machine, deliberately: it exists to load vendor model
libraries, those are x64, and a process holds one instruction set. Windows runs it translated.

To package deliberately without it: set CRF_ALLOW_NO_DEVICE_WORKER=1
'@
        }
    }


    # == The OSDI worker ===========================================================
    #
    # The program that evaluates a compiled Verilog-A model a user placed on a schematic. A separate
    # helper from the one above, with a separate history: it had NO Windows build at all until
    # 2026-09, so every Windows installer circuitRF has released shipped without it, and placing a
    # compiled model refused naming a helper the user had no way to obtain.
    #
    # BOTH ARCHITECTURES, on every Windows package, and that is not a copy of the arch loop above.
    # This worker LoadLibrary()s the user's own compiled model, so it must match THE MODEL'S
    # architecture, not circuitRF's - and an arm64 Windows machine routinely runs a translated x64
    # Verilog-A compiler, whose output is x64. Shipping one of the pair leaves the other's users with
    # a helper that cannot load anything they can compile.
    #
    # Set CRF_ALLOW_NO_DEVICE_WORKER=1 to package without it on purpose; it covers both helpers.

    # osdi-worker.exe is the third of them and it is NOT a duplicate of the pair: it is the copy a
    # kit's device-provider.json reaches by bare command, which is the one route with no model file
    # to read an architecture out of.

    $osdiFiles = @('osdi-worker-x64.exe', 'osdi-worker-arm64.exe', 'osdi-worker.exe')
    $osdiMissing = $osdiFiles | Where-Object { -not (Test-Path (Join-Path $publish $_)) }

    if ($osdiMissing) {
        $what = $osdiMissing -join ', '
        if ($env:CRF_ALLOW_NO_DEVICE_WORKER -eq '1') {
            Write-Host "WARNING: packaging without the OSDI worker ($what). Compiled Verilog-A models will not run."
        } else {
            Write-Host "Missing from the publish tree: $what"
            throw @'
The OSDI worker is missing from the publish tree.

circuitRF builds it during 'dotnet build' (tools\osdi-worker\build.cmd), but only warns when no C
compiler is present. Install zig and run this script again:

    zig      (winget install zig.zig)

zig is named on its own here rather than as one of several: it targets both x86-64 and arm64 from
either kind of machine, and both are needed. A single-target compiler (an MSYS2/MinGW gcc, a clang
without cross targets) builds only the architecture it runs on, which is half of what ships.

To package deliberately without it: set CRF_ALLOW_NO_DEVICE_WORKER=1
'@
        }
    }

    # ...AND THE FLAT ONE IS OF THE RIGHT ARCHITECTURE, which is a separate question from being
    # present and is the one that got through. tools\osdi-worker\build.cmd used to pick that copy
    # from %PROCESSOR_ARCHITECTURE% - correct for a developer build, wrong for every release, since
    # ONE run of this script publishes all three architectures from a single machine. 1.0.0-beta.16
    # therefore shipped an arm64 flat worker in the x86 and x64 payloads as well, and nothing
    # downstream notices: VerilogAFileResolver reads each candidate's PE header and prefers the
    # arch-suffixed pair, so only the bare-command route fails, on a user's machine, launching a
    # binary the processor cannot execute.
    #
    # x86 expects the x64 worker. No 32-bit worker is built, and the worker is a separate process
    # whose architecture has to match the MODEL rather than circuitRF - see build.cmd's own note.

    $wantMachine = if ($Arch -eq 'arm64') { 0xAA64 } else { 0x8664 }
    $machineNames = @{ 0x8664 = 'x64'; 0xAA64 = 'arm64'; 0x014C = 'x86' }
    $flatWorker = Join-Path $publish 'osdi-worker.exe'

    if (Test-Path $flatWorker) {
        $gotMachine = Get-PeMachine $flatWorker
        if ($gotMachine -ne $wantMachine) {
            $gotName  = if ($machineNames.ContainsKey([int]$gotMachine)) { $machineNames[[int]$gotMachine] } else { "unreadable" }
            $wantName = $machineNames[[int]$wantMachine]
            if ($env:CRF_ALLOW_NO_DEVICE_WORKER -eq '1') {
                Write-Host "WARNING: osdi-worker.exe in publish\$rid is $gotName, not $wantName. The bare-command route will not run."
            } else {
                throw @"
osdi-worker.exe in publish\$rid is $gotName, but this package is $Arch and needs $wantName.

That copy is the one a kit's device-provider.json reaches by bare command, so it is the one route
that cannot recover by reading a model's header. It is built by tools\osdi-worker\build.cmd, which
takes --arch from the .csproj; a stale tools\osdi-worker\build directory is the other way to get
here. Delete it and build again.

To package deliberately without a working one: set CRF_ALLOW_NO_DEVICE_WORKER=1
"@
            }
        }
    }


    # == The geometry kernel, read back out of the publish tree ===================
    #
    # Like the OSDI worker's flat copy above: the PE header, not where the file came from, says which
    # architecture it is - the cache holds one OCCT build per RID and a stale build directory could
    # hold another. An architecture D2 leaves out loses the folder here, so the package matches the
    # decision whatever this machine's cache holds.
    $kernelDir    = Join-Path $publish 'geometry-kernel'
    $kernelWorker = Join-Path $kernelDir 'geometry-worker.exe'
    if (Test-Path $kernelWorker) {
        $wantKernel = switch ($Arch) { 'arm64' { 0xAA64 } 'x86' { 0x014C } default { 0x8664 } }
        if ((Get-PeMachine $kernelWorker) -ne $wantKernel) {
            Write-Host "WARNING: the geometry worker in publish\$rid is not a $Arch binary; leaving the kernel out."
            Remove-Item $kernelDir -Recurse -Force
        }
    }
    if (-not $shipsKernel -and (Test-Path $kernelDir)) {
        Write-Host "NOTE: $rid is not in recipe.env's KERNEL_RIDS; leaving the geometry kernel out."
        Remove-Item $kernelDir -Recurse -Force
    }
    if (-not $shipsKernel) { $kernelLeftOut += $Arch }
    if ($shipsKernel -and -not (Test-Path $kernelWorker)) { $noKernel += $Arch }
    $kernelSmoke = if (Test-Path $kernelWorker) { @('--kernel', $occtVersion) }
                   elseif ($shipsKernel)       { @('--no-kernel', 'built without it; reported at the end') }
                   else                        { @('--no-kernel', "not shipped on $rid") }

    # == The gdstk worker, read back the same way ====================================
    $gdstkDir    = Join-Path $publish 'gdstk-kernel'
    $gdstkWorker = Join-Path $gdstkDir 'gdstk-worker.exe'
    if (Test-Path $gdstkWorker) {
        $wantGdstk = switch ($Arch) { 'arm64' { 0xAA64 } 'x86' { 0x014C } default { 0x8664 } }
        if ((Get-PeMachine $gdstkWorker) -ne $wantGdstk) {
            Write-Host "WARNING: the gdstk worker in publish\$rid is not a $Arch binary; leaving it out."
            Remove-Item $gdstkDir -Recurse -Force
        }
    }
    if (-not $shipsGdstk -and (Test-Path $gdstkDir)) {
        Write-Host "NOTE: $rid is not in tools\gdstk-worker\recipe.env's KERNEL_RIDS; leaving the gdstk worker out."
        Remove-Item $gdstkDir -Recurse -Force
    }
    if ($shipsGdstk -and -not (Test-Path $gdstkWorker)) { $noGdstk += $Arch }
    $gdstkSmoke = if (Test-Path $gdstkWorker) { @('--gdstk', $gdstkVersion) }
                  elseif ($shipsGdstk)       { @('--no-gdstk', 'built without it; reported at the end') }
                  else                       { @('--no-gdstk', "not shipped on $rid") }

    # == The command line, run out of THIS publish tree =============================
    #
    # THE GATE THAT WAS MISSING FOR 32 RELEASES (brief-automation-13-installed-cli.md). Every
    # release through 1.0.0-beta.32 shipped a circuitRF with no command line at all, and every CLI
    # gate was green throughout, because every one of them launched src\Cli\bin - never the tree
    # that goes into an installer. This one launches exactly that tree, and the build fails unless
    # --version prints the VERSION file, `reference --json` parses, and `serve` answers initialize
    # and tools/list (compared with the tool CATALOGUE, not a count) and exits when stdin closes.
    #
    # THROUGH THE STUB, laid out as a per-user install lays it out (stub + `current` + app-<version>),
    # because that is the route every MCP client takes: a program spawning circuitRF.exe with
    # redirected pipes (R-aut13-2 route 1). If there is no stub the publish tree's own circuitRF.exe
    # is driven directly, which is what a per-machine install runs.
    #
    # The .com CANNOT be checked here: this drives it through a pipe, and a pipe is the one condition
    # it does not exist for. It is checked by hand from a real console at a phase boundary and the
    # result recorded in packaging\RESOLVED.md.
    #
    # A copy of the publish tree, not a junction: removing a junction with Remove-Item -Recurse under
    # Windows PowerShell 5.1 can delete what it points at, which here is the tree about to be packaged.
    $canRun = switch ($hostArch) {
        'ARM64' { $true }
        'AMD64' { $Arch -ne 'arm64' }
        default { $Arch -eq 'x86' }
    }

    if (-not $canRun) {
        Write-Host "WARNING: NOT SMOKE-TESTED - this $hostArch machine cannot execute a $Arch build."
        Write-Host '         Windows on ARM runs all three; build there to check every architecture.'
        $unsmoked += $Arch
        # The no-Python check reads files and runs nothing, so it holds for this tree all the same.
        & dotnet $smokeDll --no-python (Join-Path $publish $exeName)
        if ($LASTEXITCODE -ne 0) {
            throw "publish\$rid carries a Python environment (see above). This tree must not be packaged."
        }
    }
    else {
        $smokeRoot = Join-Path ([System.IO.Path]::GetTempPath()) "circuitrf-smoke-$Arch"
        if (Test-Path $smokeRoot) { Remove-Item $smokeRoot -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null

        if (Test-Path $stubExe) {
            Copy-Item -Path $publish -Destination (Join-Path $smokeRoot "app-$CrfVersion") -Recurse
            Copy-Item -Path $stubExe -Destination (Join-Path $smokeRoot $exeName)
            Set-Content -LiteralPath (Join-Path $smokeRoot 'current') -Value "app-$CrfVersion" -NoNewline -Encoding ASCII
            $smokeTarget = Join-Path $smokeRoot $exeName
            Write-Host "Smoke-testing the command line through the launcher stub ($Arch) ..."
        }
        else {
            $smokeTarget = Join-Path $publish $exeName
            Write-Host "Smoke-testing the command line in publish\$rid directly (no stub for $Arch) ..."
        }

        & dotnet $smokeDll $smokeTarget $CrfVersion @kernelSmoke @gdstkSmoke
        $smokeCode = $LASTEXITCODE
        Remove-Item $smokeRoot -Recurse -Force -ErrorAction SilentlyContinue
        if ($smokeCode -ne 0) {
            throw "The command line in publish\$rid does not answer (see above). This tree must not be packaged."
        }
    }


    foreach ($Scope in $archScopes) {

        $perUser = ($Scope -eq 'perUser')

        # THE ARTIFACT NAMES. The .zip spelling is a contract with the updater, not a preference -
        # see src\Ui\Updates\UpdateAssetNames.cs and PackagingScriptTests.cs.
        if ($perUser) {
            $msi = Join-Path $dist "circuitRF-$CrfVersion-win-$Arch-user.msi"
            $zip = Join-Path $dist "circuitRF-$CrfVersion-win-$Arch.zip"
        } else {
            $msi = Join-Path $dist "circuitRF-$CrfVersion-$Arch.msi"
            $zip = $null
        }

        # Reset per package: the harvester accumulates into script-scope variables.
        $dirId = 0
        $compId = 0
        $sb = [System.Text.StringBuilder]::new()
        $components = [System.Text.StringBuilder]::new()
        $filesWxs = Join-Path $PSScriptRoot 'Files.wxs'

        # For perUser the harvest target is app-<version>, and the application's OWN circuitRF.exe belongs
        # in it - what sits at the install root is the stub. For perMachine nothing changes: the .exe is
        # named by circuitRF.wxs so the shortcuts and file associations can point at it, so it is skipped.
        $harvestRoot = if ($perUser) { 'APPFOLDER' } else { 'INSTALLFOLDER' }
        $skipExe = -not $perUser

        Add-Directory $publish $harvestRoot

        @"
<?xml version="1.0" encoding="utf-8"?>
<!-- GENERATED by build-windows.ps1 from the publish output. Do not edit; do not commit. -->
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Fragment>
    <DirectoryRef Id="$harvestRoot">
$($sb.ToString().TrimEnd())
    </DirectoryRef>
    <ComponentGroup Id="PublishedFiles">
$($components.ToString().TrimEnd())
    </ComponentGroup>
  </Fragment>
</Wix>
"@ | Set-Content -LiteralPath $filesWxs -Encoding UTF8


        # == Build the installer =======================================================

        New-Item -ItemType Directory -Force -Path $dist | Out-Null

        Write-Host "Building $msi ..."
        Push-Location $PSScriptRoot
        try {
            # $CrfMsiVersion is the numeric four-field form: "0.9.0-beta.1" is not a valid ProductVersion.
            # No trailing backslash on PublishDir: a native command line eats it as a quote escape. The
            # .wxs supplies the separator itself.
            $icon = Join-Path $root 'src\Ui\Assets\circuitRFIcon.ico'

            # Both are always passed, because the WiX preprocessor evaluates $(var.X) references inside a
            # <?if?> branch it did NOT take - an undefined one is an error even where it is unreachable.
            $stubFile = Join-Path $PSScriptRoot "stub\build\circuitRF-stub-$Arch.exe"
            $currentFile = Join-Path $PSScriptRoot 'current.txt'

            # The pointer the stub reads: one line naming the directory to run. GENERATED, never committed -
            # it carries the version, and a committed copy would be a second place the version is written.
            Set-Content -LiteralPath $currentFile -Value "app-$CrfVersion" -NoNewline -Encoding ASCII

            # Both -d values are always supplied. The WiX preprocessor resolves $(var.X) references inside a
            # <?if?> branch it did NOT take, so an undefined one is an error even where it is unreachable.
            if (-not $perUser -and -not (Test-Path $stubFile)) { $stubFile = Join-Path $publish $exeName }

            # circuitRF.com, when the console build succeeded. ConsoleFile is ALWAYS passed, for the
            # reason above; HasConsole is what decides, and without the .com it names a file that
            # exists and is never installed.
            $hasConsole  = if (Test-Path $comExe) { 'yes' } else { 'no' }
            $consoleFile = if (Test-Path $comExe) { $comExe } else { $stubFile }

            # -d Arch is the SAME value as -arch, and it is passed twice on purpose: -arch tells wix
            # what to emit, and the .wxs preprocessor cannot read it back. It selects the UpgradeCode,
            # which is per scope AND per architecture - see the note at the head of circuitRF.wxs. An
            # architecture with no code of its own is a wix ERROR there, never a silently shared one.
            #
            # RETRIED, but only for one failure. Every wix build unpacks the UI extension's bitmaps
            # (bannrbmp.bmp, dlgbmp.bmp, ...) into a fresh %TEMP%\<random>\...\wix-ir\ folder and reads
            # them straight back; on Windows that read intermittently fails on a file named under
            # wix-ir\, and the identical command then succeeds. Nothing in the inputs differs between
            # the two, so a retry is the fix and a person re-running the whole script is the cost it
            # removes. Any other failure - a WIX0091, a missing PublishDir file - is deterministic and
            # still stops on the first attempt. The output is captured to make that distinction, so the
            # preference is relaxed for the capture (see the tool checks above for why that matters).
            $wixExit = 1
            for ($attempt = 1; $attempt -le 3; $attempt++) {
                $previousEap = $ErrorActionPreference
                $ErrorActionPreference = 'Continue'
                $wixOutput = wix build circuitRF.wxs Files.wxs `
                    -arch $Arch `
                    -d "Arch=$Arch" `
                    -d "Version=$CrfMsiVersion" `
                    -d "VersionText=$CrfVersion" `
                    -d "Scope=$Scope" `
                    -d "PublishDir=$publish" `
                    -d "IconFile=$icon" `
                    -d "StubFile=$stubFile" `
                    -d "CurrentFile=$currentFile" `
                    -d "HasConsole=$hasConsole" `
                    -d "ConsoleFile=$consoleFile" `
                    -ext $extensionRef `
                    -o $msi 2>&1 | ForEach-Object { $line = "$_"; Write-Host $line; $line }
                $wixExit = $LASTEXITCODE
                $ErrorActionPreference = $previousEap

                if ($wixExit -eq 0) { break }
                if ((($wixOutput -join "`n") -notmatch '\\wix-ir\\') -or $attempt -eq 3) { break }
                Write-Host "  wix failed reading a file it had just unpacked from the UI extension; retrying (attempt $($attempt + 1) of 3) ..."
                Start-Sleep -Seconds 2
            }
            if ($wixExit -ne 0) { throw 'wix build failed.' }
        }
        finally { Pop-Location }

        Write-Host ''
        Write-Host "OK  $msi"
        Write-Host '    Unsigned: SmartScreen will warn on first run. Sign with signtool for distribution - see BUILDING.md.'
        if ($perUser) {
            # Not cosmetic on this channel. R-AU-25 compares a staged payload's publisher against the RUNNING
            # application's, and an unsigned build has no publisher to compare against - so the updater
            # refuses and the install is notify-only, with no error and nothing for the user to notice.
            Write-Host '    NOTE: an UNSIGNED per-user install cannot auto-update. Sign circuitRF.exe before packaging.'
        }

        if ($perUser) {
            Write-Host ''
            Write-Host "Building the update payload $zip ..."
            if (Test-Path $zip) { Remove-Item $zip -Force }
            Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
            Write-Host "OK  $zip"
            Write-Host '    This is what the updater downloads. Its NAME is a contract - see UpdateAssetNames.cs.'
        }

        $built += $msi
        if ($zip) { $built += $zip }
    }
}

# wix writes a .wixpdb (debug symbols for the installer) beside every .msi. Nothing ships or reads
# them, so they are removed rather than left in dist/ among the files a release is collected from.
# The whole folder is swept, so ones left behind by an earlier, failed run go too.
if (Test-Path $dist) {
    Get-ChildItem -Path $dist -Filter '*.wixpdb' -File | Remove-Item -Force
}

Write-Host ''
Write-Host "=== Done. $($built.Count) file(s) in $dist"
foreach ($f in $built) { Write-Host "    $(Split-Path -Leaf $f)" }

# A full run ships nine files and a release needs every one of them. Stated here rather than left to
# be noticed, because the failure this guards against is silent: a missing .zip stops Windows
# updates with no error anywhere.
if ($Arch -eq 'all' -and $Scope -eq 'all' -and $built.Count -ne 9) {
    Write-Host ''
    Write-Host "WARNING: expected 9 files for a full run, got $($built.Count)."
}

# Non-zero exit, so a short release cannot be mistaken for a complete one.
# NOT SMOKE-TESTED IS NOT PASSED. An architecture this machine cannot execute has not been shown to
# have a working command line, which is precisely what shipped broken for 32 releases. So it fails
# the run like a missing stub does, unless CRF_ALLOW_UNSMOKED=1 says that is understood.
# The sentence brief 61 wrote for an architecture D2 leaves out; it belongs in that installer's notes.
if ($kernelLeftOut.Count -gt 0) {
    Write-Host ''
    Write-Host "Geometry kernel not shipped on: $($kernelLeftOut -join ', ') (recipe.env KERNEL_RIDS). For the release notes:"
    Write-Host '  The 32-bit Windows edition of circuitRF does not include the geometry kernel, so booleans,'
    Write-Host '  fillets, chamfers and STEP import and export are unavailable in it; every other feature is'
    Write-Host '  the same. The 64-bit edition includes the kernel.'
}

if ($noKernel.Count -gt 0) {
    Write-Host ''
    Write-Host "NO GEOMETRY KERNEL in: $($noKernel -join ', '). Those packages have no booleans, fillets,"
    Write-Host '  chamfers or STEP import and export, and a .c3d using any of them is refused on open.'
    if ($env:CRF_ALLOW_NO_KERNEL -eq '1') {
        Write-Host '  CRF_ALLOW_NO_KERNEL=1 says that is intended.'
    } else {
        Write-Host '  Build it and run this again - tools\geometry-worker\build.cmd --rid win-<arch> (needs'
        Write-Host '  llvm-mingw, CMake and Ninja; minutes per architecture, once) - or'
        Write-Host '  set CRF_ALLOW_NO_KERNEL=1 to ship without it knowingly.'
        $kernelFail = $true
    }
}

if ($noGdstk.Count -gt 0) {
    Write-Host ''
    Write-Host "NO GDSTK WORKER in: $($noGdstk -join ', '). Those packages have no OASIS import or export and no"
    Write-Host '  GDSII (gdstk) route; convert refuses the oasis format there.'
    if ($env:CRF_ALLOW_NO_GDSTK -eq '1') {
        Write-Host '  CRF_ALLOW_NO_GDSTK=1 says that is intended.'
    } else {
        Write-Host '  Build it and run this again - tools\gdstk-worker\build.cmd --rid win-<arch> (needs llvm-mingw,'
        Write-Host '  CMake and Ninja; well under a minute per architecture) - or'
        Write-Host '  set CRF_ALLOW_NO_GDSTK=1 to ship without it knowingly.'
        $gdstkFail = $true
    }
}

if ($unsmoked.Count -gt 0) {
    Write-Host ''
    Write-Host "NOT SMOKE-TESTED: $($unsmoked -join ', ') - this $hostArch machine cannot execute them, so"
    Write-Host '  nothing has shown that their command line answers. Build on Windows on ARM, which runs'
    Write-Host '  all three architectures, or set CRF_ALLOW_UNSMOKED=1 to accept that knowingly.'
    if ($env:CRF_ALLOW_UNSMOKED -ne '1') { exit 1 }
}

if ($consoleFailures.Count -gt 0 -and $stubFailures.Count -eq 0) {
    Write-Host ''
    Write-Host "Incomplete: no circuitRF.com for $($consoleFailures -join ', '), so on those architectures"
    Write-Host '  a command typed in cmd or PowerShell gets no console. Piped callers are unaffected.'
    Write-Host '  Do not publish this build. The .com needs the same C compiler as the launcher stub;'
    Write-Host '  packaging\windows\stub\diagnose-zig.ps1 measures what is wrong with it.'
    exit 1
}

if ($stubFailures.Count -gt 0) {
    Write-Host ''
    Write-Host "Incomplete: no launcher stub for $($stubFailures -join ', '), so those"
    Write-Host '  architectures have no self-updating installer. Do not publish this build.'
    Write-Host ''
    # THIS TEXT USED TO SAY "on Windows on ARM you need the windows-aarch64 build; the x86_64 one
    # runs emulated and crashes". That was measured to be wrong: the release box IS running the
    # native windows-aarch64 zig and it crashes anyway, inside zig.exe, at one code offset, roughly
    # eleven times in twelve. Advice that sends the operator to re-install what they already have
    # costs a round trip and teaches them to distrust the message.
    Write-Host '  The stub needs a working C compiler. Either:'
    Write-Host '    - point CRF_ZIG at a DIFFERENT zig version and run again. To see which'
    Write-Host '      zigs are installed and what to set it to:'
    Write-Host '        .\packaging\windows\stub\diagnose-zig.ps1 -ListZig'
    Write-Host '        $env:CRF_ZIG = "<one of the paths it prints>"'
    Write-Host '      zig 0.16.0 on Windows on ARM has a memory-safety fault of its own'
    Write-Host '      (see packaging/RESOLVED.md); an older build is the first thing to try; or'
    Write-Host '    - install Visual Studio with the C++ workload, which this script then finds'
    Write-Host '      by itself - no Developer PowerShell needed.'
    Write-Host ''
    Write-Host '  packaging\windows\stub\diagnose-zig.ps1 measures which of those is happening.'
    exit 1
}

# --- What these artifacts mean for automatic updates -------------------------------------------
#
# The same statement build-macos.sh and build-linux.sh end with. Whether these files can EVER be
# installed as an automatic update is decided by the release key compiled into the binary, not by
# anything this script did - so it is said here, while someone is reading the output, rather than
# discovered when a published release is offered to nobody.
#
# The constant is written as adjacent string literals across several lines, so every quoted run in
# the declaration is joined; reading only the first line would truncate the key silently.
$keysCs = Join-Path $root 'src\Ui\Updates\ReleaseKeys.cs'
$pub    = ''
if (Test-Path $keysCs) {
    $decl = (Get-Content $keysCs -Raw) -replace "(?s).*PublicKeySpkiBase64\s*=", ''
    $decl = ($decl -split ';')[0]
    $pub  = (([regex]::Matches($decl, '"([^"]*)"') | ForEach-Object { $_.Groups[1].Value }) -join '')
}

Write-Host ''
if ($pub.Length -gt 0) {
    Write-Host "Release key: COMPILED IN ($($pub.Length) chars)."
    Write-Host '   These artifacts can be installed as automatic updates - but ONLY once the release'
    Write-Host '   manifest is signed. Collect all 19 files from all three platforms into dist/, then'
    Write-Host '   run packaging/sign-release.sh on the machine holding the private key.'
    Write-Host '   Publishing without it means NO client is offered the release at all, silently.'
} else {
    Write-Host 'Release key: NONE compiled in.'
    Write-Host '   macOS and Linux clients will still update, anchored by the platform signature and'
    Write-Host '   the download hash. WINDOWS CLIENTS WILL NOT: an unsigned Windows build has no'
    Write-Host '   publisher for the updater to compare a payload against, so it stays notify-only.'
    Write-Host "   See BUILDING.md, 'The release signing key'."
}

# Reported with the rest above, and decided last, so nothing else the run has to say is lost.
if ($kernelFail) { exit 1 }
if ($gdstkFail) { exit 1 }
