<#
.SYNOPSIS
    Runs the Authosy service pipeline once in console mode for debugging.
.DESCRIPTION
    Executes a single pipeline run, then exits. Useful for testing
    the full workflow without installing as a Windows service.
.PARAMETER RepoPath
    Override the repo path. Defaults to the parent directory of this script's location.
#>
param(
    [string]$RepoPath = ""
)

$ErrorActionPreference = "Stop"
$ProjectPath = Join-Path $PSScriptRoot "..\service\Authosy.Service"

if (-not $RepoPath) {
    $RepoPath = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

Write-Host "=== Authosy RunOnce ===" -ForegroundColor Cyan
Write-Host "Repo path: $RepoPath" -ForegroundColor Gray
Write-Host ""

# Check prerequisites
if (-not (Get-Command "claude" -ErrorAction SilentlyContinue)) {
    Write-Host "WARNING: 'claude' CLI not found in PATH. LLM calls will fail." -ForegroundColor Yellow
}

if (-not (Get-Command "git" -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: 'git' not found in PATH." -ForegroundColor Red
    exit 1
}

# Set environment for the run
$env:Authosy__RepoPath = $RepoPath

Write-Host "Running single pipeline cycle..." -ForegroundColor Green
Write-Host ""

Push-Location $ProjectPath
try {
    dotnet run -- --RunOnce true
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "RunOnce complete." -ForegroundColor Green
