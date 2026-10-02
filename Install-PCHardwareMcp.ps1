<#
.SYNOPSIS
    Developer install of PCHardwareMcp on this PC (no setup wizard).

.DESCRIPTION
    1. Publishes PCHardwareMcp.Server to .\publish\dev-server (framework-dependent, fast) and registers it in
       Claude Desktop's config, keeping every other MCP server entry.
    2. Unless -NoService: publishes PCHardwareMcp.Service self-contained and runs '--install' elevated (one UAC prompt),
       which copies it to %ProgramFiles%\PCHardwareMcp and (re)starts the LocalSystem service.

    Restart Claude Desktop afterwards so it starts the new server.

.PARAMETER NoService
    Only publish and register the MCP server (it then reads sensors in-process, unelevated).

.PARAMETER WithPawnIo
    Also install the PawnIO driver with winget during the elevated step.

.PARAMETER Uninstall
    Remove the Claude Desktop entry and the service.

.EXAMPLE
    .\Install-PCHardwareMcp.ps1 -WithPawnIo
.EXAMPLE
    .\Install-PCHardwareMcp.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch]$NoService,
    [switch]$WithPawnIo,
    [switch]$Uninstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$serverOut = Join-Path $root 'publish\dev-server'
$serviceOut = Join-Path $root 'publish\dev-service'
$serverExe = Join-Path $serverOut 'PCHardwareMcp.Server.exe'
$serviceExe = Join-Path $serviceOut 'PCHardwareMcp.Service.exe'

function Write-Step([string]$Text) { Write-Host "`n=== $Text ===" -ForegroundColor Cyan }

function Invoke-Elevated([string]$Exe, [string]$Arguments) {
    $p = Start-Process -FilePath $Exe -ArgumentList $Arguments -Verb RunAs -Wait -PassThru -WindowStyle Hidden
    $log = Join-Path $env:ProgramData 'PCHardwareMcp\install.log'
    if (Test-Path $log) { Get-Content $log -Tail 8 | ForEach-Object { Write-Host "  $_" } }
    if ($p.ExitCode -ne 0) { throw "$Exe $Arguments failed ($($p.ExitCode))." }
}

if ($Uninstall) {
    Write-Step 'Removing Claude Desktop registration'
    if (Test-Path $serverExe) { & $serverExe --unregister } else { Write-Warning "$serverExe not found; remove 'pc-hardware' from claude_desktop_config.json by hand." }
    $installed = Join-Path $env:ProgramFiles 'PCHardwareMcp\PCHardwareMcp.Service.exe'
    $exe = @($serviceExe, $installed) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($exe) {
        Write-Step 'Removing the sensor service (administrator)'
        Invoke-Elevated $exe '--uninstall'
    }
    Write-Host "`nUninstalled. Restart Claude Desktop." -ForegroundColor Green
    return
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK not found (dotnet).' }

Write-Step 'Publishing PCHardwareMcp.Server'
$running = Get-Process PCHardwareMcp.Server -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path -like "$serverOut*" }
if ($running) { throw 'PCHardwareMcp.Server.exe from .\publish\dev-server is running. Quit Claude Desktop (tray icon > Quit) and retry.' }
& dotnet publish (Join-Path $root 'src\PCHardwareMcp.Server') -c Release -r win-x64 --self-contained false -o $serverOut --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

Write-Step 'Registering with Claude Desktop'
& $serverExe --register
if ($LASTEXITCODE -ne 0) { throw "Registration failed ($LASTEXITCODE)." }

if (-not $NoService) {
    Write-Step 'Publishing PCHardwareMcp.Service (self-contained: it runs as LocalSystem from Program Files)'
    if (Test-Path $serviceOut) { Remove-Item $serviceOut -Recurse -Force }
    & dotnet publish (Join-Path $root 'src\PCHardwareMcp.Service') -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $serviceOut --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

    Write-Step 'Installing the sensor service (administrator prompt)'
    $svcArgs = '--install'
    if ($WithPawnIo) { $svcArgs += ' --with-pawnio' }
    Invoke-Elevated $serviceExe $svcArgs
}

Write-Host "`nDone. Restart Claude Desktop to load 'pc-hardware'." -ForegroundColor Green
Write-Host "Check it with:  & '$serverExe' --selftest"
