# Brief em3d-27 (F2 spike), route B: fetch wgpu-native for THIS Windows machine into native\<rid>\.
# Never committed (native/ is git-ignored). wgpu-native is MIT OR Apache-2.0.
# The MSVC build imports VCRUNTIME140.dll (the Visual C++ runtime). If the harness reports a
# DllNotFoundException for wgpu_native.dll on a machine without it, re-run with -Gnu (x64 only),
# which links its runtime statically.
param([switch]$Gnu)
$ErrorActionPreference = 'Stop'
$ver = 'v29.0.1.1'
Set-Location $PSScriptRoot
$arch = $env:PROCESSOR_ARCHITECTURE
if ($arch -eq 'ARM64') { $z = 'windows-aarch64-msvc'; $rid = 'win-arm64' }
elseif ($Gnu) { $z = 'windows-x86_64-gnu'; $rid = 'win-x64' }
else { $z = 'windows-x86_64-msvc'; $rid = 'win-x64' }
$dest = Join-Path 'native' $rid
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$zip = Join-Path $env:TEMP "wgpu-$z.zip"
Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/gfx-rs/wgpu-native/releases/download/$ver/wgpu-$z-release.zip" -OutFile $zip
Expand-Archive -Force -Path $zip -DestinationPath $dest
Remove-Item $zip
Get-ChildItem (Join-Path $dest 'lib') -Include *.lib, *.pdb, *.a -Recurse | Remove-Item
Get-ChildItem (Join-Path $dest 'lib')
