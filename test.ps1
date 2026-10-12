param([string]$CodexSessions)
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$testExe = Join-Path $env:TEMP ('UsageWidgetTests-' + [guid]::NewGuid().ToString('N') + '.exe')
Push-Location $PSScriptRoot
try {
    & $csc /nologo /target:exe /main:CodexMetricsTests /codepage:65001 "/out:$testExe" `
        /r:System.Web.Extensions.dll /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll UsageWidget.cs CodexMetricsTests.cs
    if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
    if ($CodexSessions) { & $testExe $CodexSessions } else { & $testExe }
    if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE)" }
} finally {
    Pop-Location
    if (Test-Path -LiteralPath $testExe) { Remove-Item -LiteralPath $testExe }
}
