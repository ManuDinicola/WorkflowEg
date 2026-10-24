<#
.SYNOPSIS
  Installs Flowd as a Windows Service (no containers or Kubernetes needed).
.DESCRIPTION
  Run from the extracted release folder in an elevated PowerShell. It copies the files, applies the database
  migrations and registers an auto-start service named "Flowd".
.EXAMPLE
  .\install-service.ps1 -ConnectionString "Host=localhost;Database=flowd;Username=flowd;Password=secret" -Urls "http://0.0.0.0:8080"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ConnectionString,
    [string] $InstallDir = "C:\flowd",
    [string] $Urls = "http://0.0.0.0:8080",
    [int] $NodeId = 0,
    [string] $ServiceName = "Flowd"
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script in an elevated (administrator) PowerShell."
}

$source = $PSScriptRoot
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Stopping existing service $ServiceName"
    Stop-Service -Name $ServiceName -Force
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $source "*") -Destination $InstallDir -Recurse -Force -Exclude "install-service.ps1"

$exe = Join-Path $InstallDir "flowd.exe"
$env:Flowd__ConnectionString = $ConnectionString
Write-Host "Applying database migrations"
& $exe migrate
if ($LASTEXITCODE -ne 0) { throw "flowd migrate failed with exit code $LASTEXITCODE" }

if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    New-Service -Name $ServiceName -DisplayName "Flowd workflow engine" `
        -BinaryPathName "`"$exe`" serve --urls $Urls" -StartupType Automatic | Out-Null
}

# The connection string lives in the service's own environment, not in a file or the machine environment.
$environment = @("Flowd__ConnectionString=$ConnectionString", "Flowd__NodeId=$NodeId")
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment -Value $environment -Type MultiString

Start-Service -Name $ServiceName
Write-Host "Flowd is running. Health check: $($Urls.Replace('0.0.0.0', 'localhost'))/readyz"
