<#
.SYNOPSIS
    Runs the end-to-end demonstration.

.DESCRIPTION
    The PowerShell counterpart to run-demo.sh. Drives the specification §31 scenario through the
    application's own HTTP API — the same surface the UI uses — and finishes by proving that repeating
    the import creates no duplicate Bexio invoice.

.EXAMPLE
    ./scripts/run-demo.ps1
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = $(if ($env:BASE_URL) { $env:BASE_URL } else { 'http://127.0.0.1:5188' })
)

$ErrorActionPreference = 'Stop'

function Write-Step($message) { Write-Host "`n==> $message" -ForegroundColor Cyan }
function Write-Ok($message)   { Write-Host "    OK $message" -ForegroundColor Green }
function Write-Fail($message) { Write-Host "    FAILED $message" -ForegroundColor Red }

function Invoke-Api {
    param([string]$Path, [string]$Method = 'GET', $Body)

    $parameters = @{ Uri = "$BaseUrl$Path"; Method = $Method; ContentType = 'application/json' }
    if ($PSBoundParameters.ContainsKey('Body')) { $parameters.Body = ($Body | ConvertTo-Json -Depth 10) }
    Invoke-RestMethod @parameters
}

Write-Step "Waiting for the application at $BaseUrl"
$ready = $false
foreach ($attempt in 1..90) {
    try {
        if ((Invoke-WebRequest -Uri "$BaseUrl/health/ready" -TimeoutSec 2 -UseBasicParsing).StatusCode -eq 200) {
            $ready = $true; break
        }
    } catch { Start-Sleep -Seconds 1 }
}
if (-not $ready) { throw "The application is not running. Start it with scripts/setup-demo.ps1" }
Write-Ok "ready"

Write-Step "1. Discovering Bexio configuration"
$reference = Invoke-Api -Path '/api/connections/bexio/refresh-reference-data' -Method POST
Write-Host "    taxes $($reference.taxes) | accounts $($reference.accounts) | contacts $($reference.contacts)"
Write-Host "    tax mappings created: $($reference.taxMappingsCreated) (derived from Bexio, never hardcoded)"

Write-Step "2. Importing fixtures"
foreach ($source in 'Shopify', 'Amazon') {
    $import = Invoke-Api -Path '/api/imports' -Method POST -Body @{ sourceSystem = $source }
    Write-Host "    ${source}: seen $($import.documentsSeen) | created $($import.created) | already imported $($import.duplicatesSkipped)"
}

Write-Step "3. Deterministic validation (AI is disabled)"
$invoices = Invoke-Api -Path '/api/invoices?take=50'
foreach ($invoice in $invoices) { Invoke-Api -Path "/api/invoices/$($invoice.id)/validate" -Method POST | Out-Null }

Invoke-Api -Path '/api/invoices?take=50' | Sort-Object invoiceNumber | ForEach-Object {
    '    {0,-24} {1,-8} {2} {3,10}  {4}' -f $_.invoiceNumber, $_.sourceSystem, $_.currency, $_.totalAmount, $_.state
}

Write-Step "4. Mapping decisions a human must make"
$invoiceId = (Invoke-Api -Path '/api/invoices?take=50' | Where-Object invoiceNumber -eq 'INV-10001').id
$detail    = Invoke-Api -Path "/api/invoices/$invoiceId"
$contact   = (Invoke-Api -Path '/api/mappings/bexio-reference?kind=Contact' | Where-Object { $_.name -like '*ABC*' })[0]

Invoke-Api -Path '/api/mappings/customers' -Method POST -Body @{
    customerId = $detail.customer.id; bexioContactId = $contact.bexioId; label = $contact.name
} | Out-Null
Write-Ok "customer mapped to $($contact.bexioId)"

foreach ($pair in @(@{ Code = 'REVENUE_GOODS'; Number = '3200' }, @{ Code = 'REVENUE_SHIPPING'; Number = '3700' })) {
    $account = (Invoke-Api -Path '/api/mappings/bexio-reference?kind=Account' | Where-Object code -eq $pair.Number)[0]
    Invoke-Api -Path '/api/mappings/accounts' -Method POST -Body @{
        internalAccountCode = $pair.Code; bexioAccountId = $account.bexioId; number = $account.code; name = $account.name
    } | Out-Null
    Write-Ok "$($pair.Code) -> Bexio account $($pair.Number)"
}

Write-Step "5. Bexio pre-flight preview"
$preview = Invoke-Api -Path "/api/invoices/$invoiceId/preview"
Write-Host "    can synchronise: $($preview.canSynchronize)"
Write-Host "    totals: net $($preview.totalNet) | tax $($preview.totalTax) | gross $($preview.totalGross) $($preview.currency)"

Write-Step "6. Explicit approval"
Invoke-Api -Path "/api/invoices/$invoiceId/submit" -Method POST | Out-Null
Invoke-Api -Path "/api/invoices/$invoiceId/approve" -Method POST -Body @{ comment = 'Checked against the source order.' } | Out-Null
Write-Ok "approved; the approval and the outbox entry were written in one transaction"

Write-Step "7. Waiting for the background worker"
foreach ($attempt in 1..40) {
    $state = (Invoke-Api -Path "/api/invoices/$invoiceId").invoice.state
    if ($state -eq 'Synced') { break }
    Start-Sleep -Seconds 1
}

$final = Invoke-Api -Path "/api/invoices/$invoiceId"
Write-Host "    state         : $($final.invoice.state)"
Write-Host "    Bexio invoice : $($final.invoice.bexioInvoiceId)"

Write-Step "8. Repeating the import — no duplicate may be created"
$repeat = Invoke-Api -Path '/api/imports' -Method POST -Body @{ sourceSystem = 'Shopify' }
Write-Host "    seen $($repeat.documentsSeen) | created $($repeat.created) | already imported $($repeat.duplicatesSkipped)"
Start-Sleep -Seconds 3

$succeeded   = @(Invoke-Api -Path '/api/synchronizations' | Where-Object status -eq 'Succeeded').Count
$canonical   = @(Invoke-Api -Path '/api/invoices?take=100' | Where-Object invoiceNumber -eq 'INV-10001').Count

if ($succeeded -eq 1 -and $canonical -eq 1) {
    Write-Ok "exactly one canonical invoice and one successful synchronisation — no duplicate was created"
    Write-Host "`n==> The end-to-end demonstration completed successfully.`n" -ForegroundColor Green
} else {
    Write-Fail "DUPLICATE DETECTED: $canonical canonical invoice(s), $succeeded successful synchronisation(s)"
    exit 1
}
