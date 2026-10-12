# Claude Code statusLine wrapper: saves rate_limits for the taskbar widget,
# then runs the previous status line script unchanged.
$ErrorActionPreference = 'SilentlyContinue'
[Console]::InputEncoding = [Text.Encoding]::UTF8
$raw = [Console]::In.ReadToEnd()
try {
    $j = $raw | ConvertFrom-Json
    if ($j.rate_limits) {
        $out = Join-Path $PSScriptRoot 'claude-usage.json'
        $tmp = "$out.$PID.tmp"
        [IO.File]::WriteAllText($tmp, ($j.rate_limits | ConvertTo-Json -Compress -Depth 5))
        Move-Item -LiteralPath $tmp -Destination $out -Force
    }
} catch {}

$next = Join-Path $env:USERPROFILE '.claude/hooks/caveman-statusline.ps1'
if (Test-Path -LiteralPath $next) { $raw | & $next }
