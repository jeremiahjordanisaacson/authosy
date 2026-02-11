#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Uninstalls the Authosy Windows Service.
.DESCRIPTION
    Stops and removes the Authosy Windows service.
    Requires Administrator privileges.
#>

$ErrorActionPreference = "Stop"
$ServiceName = "Authosy News Service"

Write-Host "=== Authosy Service Uninstaller ===" -ForegroundColor Cyan

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "Service '$ServiceName' not found. Nothing to uninstall." -ForegroundColor Yellow
    exit 0
}

# Stop the service if running
if ($existing.Status -eq "Running") {
    Write-Host "Stopping service..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force
    Start-Sleep -Seconds 3
}

# Remove the service
Write-Host "Removing service..." -ForegroundColor Yellow
sc.exe delete $ServiceName

Write-Host ""
Write-Host "Service uninstalled successfully." -ForegroundColor Green
Write-Host "Note: Service binaries were NOT deleted. Remove them manually if desired." -ForegroundColor Yellow
