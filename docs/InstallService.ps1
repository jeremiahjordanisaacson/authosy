#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs the Authosy Windows Service.
.DESCRIPTION
    Publishes the .NET Worker Service and installs it as a Windows service.
    Requires Administrator privileges.
.PARAMETER ServicePath
    The path where the published service binaries will be placed.
    Defaults to C:\Authosy\Service
#>
param(
    [string]$ServicePath = "C:\Authosy\Service"
)

$ErrorActionPreference = "Stop"
$ServiceName = "Authosy News Service"
$ProjectPath = Join-Path $PSScriptRoot "..\service\Authosy.Service\Authosy.Service.csproj"

Write-Host "=== Authosy Service Installer ===" -ForegroundColor Cyan

# Check if service already exists
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service '$ServiceName' already exists. Stop and remove it first with UninstallService.ps1" -ForegroundColor Yellow
    exit 1
}

# Publish the project
Write-Host "Publishing service to $ServicePath..." -ForegroundColor Green
dotnet publish $ProjectPath -c Release -o $ServicePath --self-contained false
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to publish service" -ForegroundColor Red
    exit 1
}

# Copy configuration
$configSource = Join-Path $PSScriptRoot "..\service\Authosy.Service\appsettings.json"
$configDest = Join-Path $ServicePath "appsettings.json"
if (-not (Test-Path $configDest)) {
    Copy-Item $configSource $configDest
}

# Create the Windows service
$exePath = Join-Path $ServicePath "Authosy.Service.exe"
Write-Host "Creating Windows service..." -ForegroundColor Green
sc.exe create $ServiceName binPath= "`"$exePath`"" start= delayed-auto displayname= "Authosy News Service"
sc.exe description $ServiceName "Automated uplifting news aggregation and publishing service"
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/120000/restart/300000

Write-Host ""
Write-Host "Service installed successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "Before starting the service, configure:" -ForegroundColor Yellow
Write-Host "  1. Edit $configDest" -ForegroundColor Yellow
Write-Host "     - Set RepoPath to your local repo clone path" -ForegroundColor Yellow
Write-Host "     - Set GitRemoteUrl if different from origin" -ForegroundColor Yellow
Write-Host "  2. Set environment variable GITHUB_TOKEN for git push auth" -ForegroundColor Yellow
Write-Host "     Use: [System.Environment]::SetEnvironmentVariable('GITHUB_TOKEN', 'your-token', 'Machine')" -ForegroundColor Yellow
Write-Host ""
Write-Host "Then start with: Start-Service '$ServiceName'" -ForegroundColor Cyan
