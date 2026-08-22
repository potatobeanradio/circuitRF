<#
  == circuitRF Windows .msi builder ============================================

    .\packaging\windows\build-msi.ps1                  -> dist\circuitRF-<VERSION>-x64.msi
    .\packaging\windows\build-msi.ps1 -Arch arm64      -> dist\circuitRF-<VERSION>-arm64.msi
    .\packaging\windows\build-msi.ps1 -Arch x86        -> dist\circuitRF-<VERSION>-x86.msi

  <VERSION> is the contents of the repo-root VERSION file; see BUILDING.md.

  Requires: .NET 10 SDK and the WiX CLI -

      dotnet tool install --global wix

  The WixToolset.UI.wixext extension is installed BY THIS SCRIPT if it is missing, pinned to the
  wix version actually on PATH. Adding it by hand is what produced "error WIX0144: The extension
  'WixToolset.UI.wixext' could not be found": the extension cache is keyed by wix version, so an
  extension added under one wix and a `dotnet tool update` later do not see each other.

  Run it from the repository root, in PowerShell. Everything is derived from -Arch: the runtime
  identifier, the publish directory and the MSI platform.

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
    [ValidateSet('x64', 'arm64', 'x86')]
    [string]$Arch = 'x64',

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

$rid = "win-$Arch"
$publish = Join-Path $root "publish\$rid"
$dist = Join-Path $root 'dist'
$msi = Join-Path $dist "circuitRF-$CrfVersion-$Arch.msi"

# The shipped executable is circuitRF.exe, NOT CircuitRF.Ui.exe. The assembly is still called
# CircuitRF.Ui (RfCore grants it InternalsVisibleTo, so the assembly name cannot change), but
# src/Ui/CircuitRF.Ui.csproj renames the native host after publish - see its CrfRenameApphost
# target. Keep this in step with that target.
$exeName = 'circuitRF.exe'

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

if (-not $extensionRef) {
    $extensionRef = 'WixToolset.UI.wixext'
    if ($installed -notmatch 'WixToolset\.UI\.wixext') {
        Write-Host "  installing $extensionRef ..."
        & wix extension add --global $extensionRef 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Could not install the WiX UI extension. Run it by hand: wix extension add --global $extensionRef"
        }
    }
}

# == Icons =====================================================================
#
# The icon must exist BEFORE the publish: the .csproj embeds Assets\circuitRFIcon.ico into the
# executable only if it is on disk, and that embedded icon is what Explorer draws for the .exe.

Write-Host 'Building icons...'
dotnet run --project (Join-Path $root 'tools\IconGen') -- circuitrf
if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }

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

# == Harvest the publish tree ==================================================
#
# Everything except the .exe, which circuitRF.wxs names itself so the shortcuts and file
# associations can point at it. Harvesting rather than listing by hand is what keeps the installer
# correct when the published set changes - the app ships its user documentation as loose files, and
# the native SkiaSharp/HarfBuzz DLLs come and go with the single-file settings.

Write-Host 'Harvesting published files...'
$filesWxs = Join-Path $PSScriptRoot 'Files.wxs'
$dirId = 0
$compId = 0
$sb = [System.Text.StringBuilder]::new()
$components = [System.Text.StringBuilder]::new()

function Add-Directory($path, $parentId) {
    foreach ($file in Get-ChildItem -LiteralPath $path -File | Sort-Object Name) {
        if ($file.Name -eq $script:exeName) { continue }
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

Add-Directory $publish 'INSTALLFOLDER'

@"
<?xml version="1.0" encoding="utf-8"?>
<!-- GENERATED by build-msi.ps1 from the publish output. Do not edit; do not commit. -->
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Fragment>
    <DirectoryRef Id="INSTALLFOLDER">
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

    wix build circuitRF.wxs Files.wxs `
        -arch $Arch `
        -d "Version=$CrfMsiVersion" `
        -d "PublishDir=$publish" `
        -d "IconFile=$icon" `
        -ext $extensionRef `
        -o $msi
    if ($LASTEXITCODE -ne 0) { throw 'wix build failed.' }
}
finally { Pop-Location }

Write-Host ''
Write-Host "OK  $msi"
Write-Host '    Unsigned: SmartScreen will warn on first run. Sign with signtool for distribution - see BUILDING.md.'
