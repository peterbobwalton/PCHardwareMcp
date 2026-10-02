<#
.SYNOPSIS
    Builds the PCHardwareMcp setup exe for deployment to other PCs.

.DESCRIPTION
    1. Publishes PCHardwareMcp.Server and PCHardwareMcp.Service self-contained single-file (target PCs need no .NET).
    2. Compiles installer\PCHardwareMcp.iss with Inno Setup -> installer\Output\PCHardwareMcpSetup-<version>.exe

    Needs on THIS PC: .NET 10 SDK, Inno Setup 6.

.PARAMETER Version
    Installer version string. Default: <Version> from the server .csproj.

.EXAMPLE
    .\Build-Installer.ps1
.EXAMPLE
    .\Build-Installer.ps1 -Version 0.1.0-rc2
#>
[CmdletBinding()]
param(
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

function Write-Step([string]$Text) { Write-Host "`n=== $Text ===" -ForegroundColor Cyan }

function Invoke-Native {
    param([string]$File, [string[]]$Arguments, [string]$What)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}

function Find-Iscc {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $keys = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
            'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
    foreach ($k in $keys) {
        $loc = (Get-ItemProperty $k -ErrorAction SilentlyContinue).InstallLocation
        if ($loc -and (Test-Path (Join-Path $loc 'ISCC.exe'))) { return (Join-Path $loc 'ISCC.exe') }
    }
    return $null
}

function Publish-Project([string]$Project, [string]$Out) {
    if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
    Invoke-Native -File 'dotnet' -What "dotnet publish $Project" -Arguments @(
        'publish', (Join-Path $root "src\$Project"), '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true', '-p:DebugType=None', '-o', $Out, '--nologo')
    $exe = Join-Path $Out "$Project.exe"
    if (-not (Test-Path $exe)) { throw "Publish finished but $exe is missing." }
}

# ---------------------------------------------------------------- prerequisites
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK not found (dotnet). Install the .NET 10 SDK.' }
$iscc = Find-Iscc
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with:  winget install JRSoftware.InnoSetup' }
if (-not $Version) {
    $csproj = Join-Path $root 'src\PCHardwareMcp.Server\PCHardwareMcp.Server.csproj'
    $Version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { throw "No <Version> in $csproj" }
}
$sw = [Diagnostics.Stopwatch]::StartNew()

# ---------------------------------------------------------------- 1. publish
Write-Step '[1/2] Publishing server and service (win-x64, self-contained, single file)'
Publish-Project 'PCHardwareMcp.Server'  (Join-Path $root 'publish\server')
Publish-Project 'PCHardwareMcp.Service' (Join-Path $root 'publish\service')

# ---------------------------------------------------------------- 2. installer
Write-Step '[2/2] Building installer'
Invoke-Native -File $iscc -What 'Inno Setup' -Arguments @(
    '/Qp', "/DAppVersion=$Version", (Join-Path $root 'installer\PCHardwareMcp.iss'))

$setup = Get-ChildItem (Join-Path $root 'installer\Output') -Filter "PCHardwareMcpSetup-$Version.exe" | Select-Object -First 1
if (-not $setup) { throw 'Inno Setup reported success but no setup exe was found.' }

Write-Host ''
Write-Host ("Done in {0:mm\:ss}" -f $sw.Elapsed) -ForegroundColor Green
Write-Host ("  Setup: {0}  ({1:N1} MB)" -f $setup.FullName, ($setup.Length / 1MB))
Write-Host  '  Run it on each PC, accept the administrator prompt for the sensor service, then restart Claude Desktop.'
