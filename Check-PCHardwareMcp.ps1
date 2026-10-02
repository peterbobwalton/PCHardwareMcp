<#
.SYNOPSIS
    Checks a PCHardwareMcp install on this PC (run after the setup, no admin needed).
.DESCRIPTION
    Reports the installed server, the sensor service, the PawnIO driver, the Claude Desktop config entries, and runs the
    server's selftest through the service. Prints PASS/WARN/FAIL lines and writes the full selftest next to this script
    as selftest-<COMPUTERNAME>.txt (falls back to %TEMP% when this folder is read-only).
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

function Report([string]$Level, [string]$Text) {
    $color = @{ PASS = 'Green'; WARN = 'Yellow'; FAIL = 'Red' }[$Level]
    Write-Host ("[{0}] {1}" -f $Level, $Text) -ForegroundColor $color
}

Write-Host "PCHardwareMcp check on $env:COMPUTERNAME`n" -ForegroundColor Cyan

$exe = Join-Path $env:APPDATA 'PCHardwareMcp\server\PCHardwareMcp.Server.exe'
if (Test-Path $exe) { Report PASS "server installed: $exe ($((Get-Item $exe).VersionInfo.ProductVersion))" }
else { Report FAIL "server not found at $exe - run the setup"; return }

$svc = Get-Service PCHardwareMcp -ErrorAction SilentlyContinue
if (-not $svc) { Report WARN 'sensor service not installed (CPU temps/power, fans and voltages will be missing) - rerun setup and accept the admin prompt' }
elseif ($svc.Status -ne 'Running') { Report FAIL "sensor service is $($svc.Status) - start it in services.msc" }
else { Report PASS 'sensor service running' }

if (Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\PawnIO') { Report PASS 'PawnIO driver installed' }
else { Report WARN 'PawnIO driver missing - CPU temps, Super I/O fans/voltages need it (winget install namazso.PawnIO)' }

$configs = @(Get-ChildItem "$env:APPDATA\Claude\claude_desktop_config.json",
    "$env:LOCALAPPDATA\Packages\Claude_*\LocalCache\Roaming\Claude\claude_desktop_config.json" -ErrorAction SilentlyContinue)
if ($configs.Count -eq 0) { Report FAIL 'no claude_desktop_config.json found - is Claude Desktop installed for this user?' }
foreach ($c in $configs) {
    try {
        $entry = (Get-Content $c.FullName -Raw | ConvertFrom-Json).mcpServers.'pc-hardware'
        if ($entry -and $entry.command -eq $exe) { Report PASS "registered in $($c.FullName)" }
        elseif ($entry) { Report WARN "pc-hardware in $($c.FullName) points at $($entry.command)" }
        else { Report FAIL "pc-hardware missing from $($c.FullName) - run: & '$exe' --register" }
    } catch { Report FAIL "cannot parse $($c.FullName): $_" }
}

$out = Join-Path $PSScriptRoot "selftest-$env:COMPUTERNAME.txt"
try { [IO.File]::WriteAllText($out, '') } catch { $out = Join-Path $env:TEMP "selftest-$env:COMPUTERNAME.txt" }
& $exe --selftest --out $out | Out-Null
$lines = Get-Content $out -ErrorAction SilentlyContinue
if ($lines -and ($lines -contains 'SELFTEST OK')) {
    $status = $lines[1] | ConvertFrom-Json
    $summary = $lines[($lines.IndexOf(($lines | Where-Object { $_ -like 'summary via*' } | Select-Object -First 1))) + 1] | ConvertFrom-Json
    $via = if ($lines[0] -like '*via service*') { 'service' } else { 'in-process (no service)' }
    Report $(if ($via -eq 'service') { 'PASS' } else { 'WARN' }) "selftest OK via $via, $($status.sensorCount) sensors"
    Write-Host ("`n  CPU   {0}, {1}C/{2}T, {3} C, {4} W" -f $summary.cpu.name, $summary.cpu.cores, $summary.cpu.threads, $summary.cpu.temperatureC, $summary.cpu.powerW)
    Write-Host ("  RAM   {0}" -f $summary.memory.installed)
    Write-Host ("  Board {0}" -f $summary.board)
    foreach ($g in $summary.gpus) { Write-Host ("  GPU   {0}, {1} C" -f $g.name, $g.temperatureC) }
    Write-Host "`n  Full output: $out"
} else {
    Report FAIL "selftest failed - see $out"
}
Write-Host "`nIf everything passes, restart Claude Desktop (tray icon > Quit, then reopen) to load pc-hardware."
