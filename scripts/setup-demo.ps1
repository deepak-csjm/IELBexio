<#
.SYNOPSIS
    Prepares and starts everything the demonstration needs.

.DESCRIPTION
    The PowerShell counterpart to setup-demo.sh, for Windows developers. Brings up the database,
    applies migrations, seeds the demo tenant and starts the application.

    Idempotent: safe to re-run.

.PARAMETER Fresh
    Drop and recreate the demo database first.

.EXAMPLE
    ./scripts/setup-demo.ps1 -Fresh
#>
[CmdletBinding()]
param(
    [switch]$Fresh,
    [string]$DbHost     = $(if ($env:IELBEXIO_DB_HOST)     { $env:IELBEXIO_DB_HOST }     else { '127.0.0.1' }),
    [int]   $DbPort     = $(if ($env:IELBEXIO_DB_PORT)     { $env:IELBEXIO_DB_PORT }     else { 5432 }),
    [string]$DbUser     = $(if ($env:IELBEXIO_DB_USER)     { $env:IELBEXIO_DB_USER }     else { 'postgres' }),
    [string]$DbPassword = $(if ($env:IELBEXIO_DB_PASSWORD) { $env:IELBEXIO_DB_PASSWORD } else { 'postgres' }),
    [string]$DbName     = $(if ($env:IELBEXIO_DEMO_DB)     { $env:IELBEXIO_DEMO_DB }     else { 'ielbexio_demo' }),
    [string]$AppUrl     = $(if ($env:BASE_URL)             { $env:BASE_URL }             else { 'http://127.0.0.1:5188' })
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Write-Step($message) { Write-Host "`n==> $message" -ForegroundColor Cyan }
function Write-Ok($message)   { Write-Host "    OK $message" -ForegroundColor Green }

Write-Step "Preparing the database '$DbName'"
$env:PGPASSWORD = $DbPassword

if ($Fresh) {
    psql -h $DbHost -p $DbPort -U $DbUser -d postgres -c "DROP DATABASE IF EXISTS `"$DbName`" WITH (FORCE);" | Out-Null
    Write-Ok "dropped the existing demo database"
}

$exists = psql -h $DbHost -p $DbPort -U $DbUser -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='$DbName'"
if (-not $exists) {
    psql -h $DbHost -p $DbPort -U $DbUser -d postgres -c "CREATE DATABASE `"$DbName`";" | Out-Null
    Write-Ok "created $DbName"
} else {
    Write-Ok "$DbName already exists"
}

Write-Step "Building"
dotnet build $repoRoot -v quiet --nologo
if ($LASTEXITCODE -ne 0) { throw "The build failed." }
Write-Ok "build succeeded"

Write-Step "Starting the application"
Get-Process -Name 'IelBexio.Web' -ErrorAction SilentlyContinue | Stop-Process -Force

# Migrations are applied and the demo tenant seeded by the host at startup.
$env:ASPNETCORE_ENVIRONMENT              = 'Development'
$env:ASPNETCORE_URLS                     = $AppUrl
$env:IELBEXIO_ConnectionStrings__Postgres = "Host=$DbHost;Port=$DbPort;Database=$DbName;Username=$DbUser;Password=$DbPassword"
$env:IELBEXIO_Shopify__FixtureDirectory   = Join-Path $repoRoot 'fixtures/shopify'
$env:IELBEXIO_Amazon__FixtureDirectory    = Join-Path $repoRoot 'fixtures/amazon'
$env:IELBEXIO_BlobStorage__LocalRootPath  = Join-Path $repoRoot 'local-blobs'

Start-Process -FilePath 'dotnet' `
    -ArgumentList 'run', '--no-launch-profile', '--no-build' `
    -WorkingDirectory (Join-Path $repoRoot 'src/IelBexio.Web') `
    -WindowStyle Hidden

foreach ($attempt in 1..90) {
    try {
        $response = Invoke-WebRequest -Uri "$AppUrl/health/ready" -TimeoutSec 2 -UseBasicParsing
        if ($response.StatusCode -eq 200) {
            Write-Ok "the application is ready at $AppUrl"
            Write-Host "`n    UI:      $AppUrl"
            Write-Host "    OpenAPI: $AppUrl/openapi/v1.json"
            Write-Host "`n    Next: ./scripts/run-demo.ps1`n"
            exit 0
        }
    } catch {
        Start-Sleep -Seconds 1
    }
}

throw "The application did not become ready."
