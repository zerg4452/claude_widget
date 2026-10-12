# Builds UsageWidget.exe with the C# compiler that ships with .NET Framework 4.x.
$ErrorActionPreference = 'Stop'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
Push-Location $PSScriptRoot
try {
    & $csc /nologo /target:winexe /codepage:65001 /optimize+ /out:UsageWidget.exe `
        /r:System.Web.Extensions.dll /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll UsageWidget.cs
    if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
    'built UsageWidget.exe'
} finally { Pop-Location }
