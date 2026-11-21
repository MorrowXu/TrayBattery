# Build TrayBattery.exe with the in-box C# compiler. No NuGet, no network, no SDK.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) { throw 'csc.exe (NET Framework 4.x) not found' }

& $csc /nologo /codepage:65001 /target:winexe /optimize+ /platform:anycpu /warn:4 `
    /out:"$root\TrayBattery.exe" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    "$root\TrayBattery.cs"

if ($LASTEXITCODE -ne 0) { throw "csc failed: $LASTEXITCODE" }
Write-Host "OK -> $root\TrayBattery.exe"
