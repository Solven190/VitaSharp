# Lists the HLE functions whose argument registers differ from their VitaSDK prototype (vita-headers include/psp2):
# argument order, count, 64-bit pairs, stack arguments, floats/doubles in VFP registers (VitaSharp.Desktop hle-abi-check).
# A report, not a gate: the headers can be wrong too, so check a mismatch against the firmware module before changing
# the HLE. Exit 1 when a MISMATCH is listed.
# Usage: build/hle-abi-check.ps1 [-Headers <vita-headers folder>] [-Fewer] [-Exe <VitaSharp.Desktop>] [-Out <folder>]
#   -Headers: a checkout of github.com/vitasdk/vita-headers; default: a shallow clone into a temp folder.
#   -Fewer: also list bindings that read only a prefix of the prototype's arguments.
# Linux/macOS: build/hle-abi-check.sh (same steps).
param([string]$Headers = '', [switch]$Fewer, [string]$Exe = '', [string]$Out = '', [switch]$Help)
$ErrorActionPreference = 'Stop'
if ($Help) {
    Get-Content $PSCommandPath -TotalCount 8 | ForEach-Object { $_ -replace '^# ?', '' }
    exit 0
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Headers) {
    $Headers = Join-Path ([IO.Path]::GetTempPath()) 'vitasharp-vita-headers'
    if (-not (Test-Path (Join-Path $Headers 'include/psp2'))) {
        git clone --depth 1 https://github.com/vitasdk/vita-headers.git $Headers
        if ($LASTEXITCODE -ne 0) { throw 'git clone failed' }
    }
}
if (-not $Exe) {
    if (-not $Out) { $Out = Join-Path ([IO.Path]::GetTempPath()) 'vitasharp-hle-abi-check' }
    dotnet build (Join-Path $root 'src/VitaSharp.Desktop') -c Release -m:2 -p:UseSharedCompilation=false -v q -o $Out
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
    $Exe = Join-Path $Out 'VitaSharp.Desktop'
    if (Test-Path "$Exe.exe") { $Exe = "$Exe.exe" }
}
$cmd = @('hle-abi-check', "--headers=$Headers")
if ($Fewer) { $cmd += '--fewer' }
& $Exe @cmd
exit $LASTEXITCODE
