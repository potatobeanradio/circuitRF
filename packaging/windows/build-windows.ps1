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

# == The harvester ==============================================================
#
# Defined once, above the loop, because PowerShell resolves a function only after its definition
# has been executed. It writes Files.wxs from the publish tree: everything except, for perMachine,
# the .exe that circuitRF.wxs names itself so the shortcuts and file associations can point at it.
#
# Harvesting rather than listing by hand is what keeps the installer correct when the published set
# changes - the app ships its user documentation as loose files, and the native SkiaSharp/HarfBuzz
# DLLs come and go with the single-file settings.

function Add-Directory($path, $parentId) {
    foreach ($file in Get-ChildItem -LiteralPath $path -File | Sort-Object Name) {
        if ($script:skipExe -and $file.Name -eq $script:exeName) { continue }
        $script:compId++
        [void]$script:components.AppendLine(
            "      <Component Id=`"cmp$($script:compId)`" Directory=`"$parentId`">")
        [void]$script:components.AppendLine(
            "        <File Id=`"fil$($script:compId)`" Source=`"$($file.FullName)`" KeyPath=`"yes`" />")
        [void]$script:components.AppendLine('      </Component>')
    }
    foreach ($sub in Get-ChildItem -LiteralPath $path -Directory | Sort-Object Name) {
        $script:dirId++
        $id = "dir$($script:dirId)"
        [void]$script:sb.AppendLine("      <Directory Id=`"$id`" Name=`"$($sub.Name)`">")
        Add-Directory $sub.FullName $id
        [void]$script:sb.AppendLine('      </Directory>')
    }
}

foreach ($Arch in $arches) {

    Write-Host ''
    Write-Host "=== $Arch ==================================================================="

    $rid = "win-$Arch"
    $publish = Join-Path $root "publish\$rid"

    # == The launcher stub (perUser only) ==========================================
    #
    # The one file in a per-user install that never changes. Built here rather than committed, for the
    # same reason the app icons are: a binary in the repository is a binary nobody can review.

    # A STUB FAILURE SKIPS THE PER-USER SCOPE - it does not abandon the whole run. It used to throw,
    # so a broken C toolchain produced ZERO artifacts from a build that could have produced the three
    # machine-wide .msi files (owner-reported, 2026-08-25: zig cc crashed with an access violation and
    # took the entire Windows release with it).
    #
    # Skipping quietly would be worse than throwing, though, because a release that is silently short
    # of the self-updating channel is the exact failure this script was rewritten to prevent. So it is
    # LOUD here, LOUD in the summary, and the script exits non-zero at the end.

    $archScopes = $scopes

    if ($scopes -contains 'perUser') {
        $stubExe = Join-Path $PSScriptRoot "stub\build\circuitRF-stub-$Arch.exe"
        try {
            & (Join-Path $PSScriptRoot 'stub\build-stub.ps1') -Arch $Arch -AppName 'circuitRF'
        }
        catch {
            # Indent EVERY line. The stub script reports one line per toolchain route it tried, and
            # a message that indents only its first line reads as though the rest is this script's
            # own output rather than the reason the stub is missing.
            $_.Exception.Message -split "`r?`n" | ForEach-Object { Write-Host "  $_" }
        }

        if (-not (Test-Path $stubExe)) {
            Write-Host "  No stub for $Arch, so its per-user installer is not built. Carrying on."
            $stubFailures += $Arch
            $archScopes = $scopes | Where-Object { $_ -ne 'perUser' }
        }
    }

    if (-not $archScopes) {
        Write-Host "Nothing left to package for $Arch; moving on."
        continue
    }


    # == Publish ===================================================================

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

            wix build circuitRF.wxs Files.wxs `
                -arch $Arch `
                -d "Version=$CrfMsiVersion" `
                -d "VersionText=$CrfVersion" `
                -d "Scope=$Scope" `
                -d "PublishDir=$publish" `
                -d "IconFile=$icon" `
                -d "StubFile=$stubFile" `
                -d "CurrentFile=$currentFile" `
                -ext $extensionRef `
                -o $msi
            if ($LASTEXITCODE -ne 0) { throw 'wix build failed.' }
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

Write-Host ''
Write-Host "=== Done. $($built.Count) artifact(s) in $dist"
foreach ($f in $built) { Write-Host "    $(Split-Path -Leaf $f)" }

# A full run ships nine files and a release needs every one of them. Stated here rather than left to
# be noticed, because the failure this guards against is silent: a missing .zip stops Windows
# updates with no error anywhere.
if ($Arch -eq 'all' -and $Scope -eq 'all' -and $built.Count -ne 9) {
    Write-Host ''
    Write-Host "WARNING: expected 9 artifacts for a full run, got $($built.Count)."
}

# Non-zero exit, so a short release cannot be mistaken for a complete one.
if ($stubFailures.Count -gt 0) {
    Write-Host ''
    Write-Host "Incomplete: no launcher stub for $($stubFailures -join ', '), so those"
    Write-Host '  architectures have no self-updating installer. Do not publish this build.'
    Write-Host ''
    Write-Host '  The stub needs a working C compiler. Either:'
    Write-Host '    - open a Developer PowerShell for VS, so cl.exe is used instead; or'
    Write-Host '    - install the zig build that matches this machine, from'
    Write-Host '      https://ziglang.org/download/ (on Windows on ARM you need the'
    Write-Host '      windows-aarch64 build; the x86_64 one runs emulated and crashes).'
    exit 1
}
