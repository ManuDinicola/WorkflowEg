<#
.SYNOPSIS
  Backs up or restores the Flowd database with pg_dump / pg_restore (custom format, compressed).
.DESCRIPTION
  All Flowd state lives in PostgreSQL, so a database backup is a complete backup. Stop the service (or accept a
  crash-consistent copy: pg_dump takes a consistent snapshot) and keep the dump together with the Flowd version.
.EXAMPLE
  .\backup.ps1 -Action Backup  -ConnectionString "Host=localhost;Database=flowd;Username=flowd;Password=secret" -File C:\backups\flowd.dump
.EXAMPLE
  .\backup.ps1 -Action Restore -ConnectionString "Host=localhost;Database=flowd_restored;Username=flowd;Password=secret" -File C:\backups\flowd.dump
#>
param(
  [Parameter(Mandatory)][ValidateSet('Backup', 'Restore')][string]$Action,
  [Parameter(Mandatory)][string]$ConnectionString,
  [Parameter(Mandatory)][string]$File,
  [string]$PgBin = ''
)

$ErrorActionPreference = 'Stop'

function Get-Part([string]$name) {
  $match = [regex]::Match($ConnectionString, "(?i)(?:^|;)\s*$name\s*=\s*([^;]*)")
  if ($match.Success) { return $match.Groups[1].Value.Trim() }
  return ''
}

$dbHost = Get-Part 'Host'
$port = Get-Part 'Port'
$database = Get-Part 'Database'
$user = Get-Part 'Username'
$password = Get-Part 'Password'
if (-not $dbHost) { $dbHost = 'localhost' }
if (-not $port) { $port = '5432' }
if (-not $database -or -not $user) { throw 'The connection string needs Database and Username.' }

function Get-Tool([string]$name) {
  if ($PgBin) { return Join-Path $PgBin "$name.exe" }
  return $name
}

$env:PGPASSWORD = $password
try {
  if ($Action -eq 'Backup') {
    & (Get-Tool 'pg_dump') --host $dbHost --port $port --username $user --format custom --compress 6 --no-owner --file $File $database
  }
  else {
    & (Get-Tool 'pg_restore') --host $dbHost --port $port --username $user --dbname $database --no-owner --clean --if-exists $File
  }
  if ($LASTEXITCODE -ne 0) { throw "$Action failed with exit code $LASTEXITCODE." }
  Write-Host "$Action finished: $File"
}
finally {
  Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}
