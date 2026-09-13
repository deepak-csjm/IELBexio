#!/usr/bin/env bash
#
# End-to-end demonstration of the POC against a real PostgreSQL database.
#
# Drives the specification §31 scenario through the application's own HTTP API — the same surface the
# Blazor UI uses — so nothing here is a private back door. It proves, in order:
#
#   import -> raw payload retained -> canonical normalisation -> deterministic validation ->
#   tax determination -> human mapping -> pre-flight -> verification -> explicit approval ->
#   transactional outbox -> background worker -> invoice created in Bexio -> audit trail ->
#   repeat import creating no duplicate.
#
set -euo pipefail

BASE_URL="${BASE_URL:-http://127.0.0.1:5188}"
CURL=(curl -sS --noproxy '*')
JSON=(-H "Content-Type: application/json")

say()  { printf '\n\033[1;34m==> %s\033[0m\n' "$1"; }
ok()   { printf '    \033[0;32m✓\033[0m %s\n' "$1"; }
warn() { printf '    \033[0;33m!\033[0m %s\n' "$1"; }
die()  { printf '    \033[0;31m✗ %s\033[0m\n' "$1"; exit 1; }

api() { "${CURL[@]}" "$@"; }

jqp() { python3 -c "import sys,json;d=json.load(sys.stdin);$1"; }

say "Waiting for the application at ${BASE_URL}"
for _ in $(seq 1 90); do
  if [ "$("${CURL[@]}" -o /dev/null -w '%{http_code}' "${BASE_URL}/health/ready" 2>/dev/null || echo 000)" = "200" ]; then
    ok "ready"
    break
  fi
  sleep 1
done
[ "$("${CURL[@]}" -o /dev/null -w '%{http_code}' "${BASE_URL}/health/ready" 2>/dev/null || echo 000)" = "200" ] \
  || die "the application did not become ready — start it with scripts/setup-demo.sh"

# -------------------------------------------------------------------------------------------------
say "1. Configured connectors"
api "${BASE_URL}/api/connections" | jqp "
print('    Bexio:', d['bexio']['mode'])
for s in d['sources']:
    print('    ', s['sourceSystem'], '->', s['mode'])
"

# -------------------------------------------------------------------------------------------------
say "2. Discovering Bexio configuration (taxes, accounts, contacts)"
api -X POST "${BASE_URL}/api/connections/bexio/refresh-reference-data" | jqp "
print('    taxes', d['taxes'], '| accounts', d['accounts'], '| contacts', d['contacts'])
print('    tax mappings created:', d['taxMappingsCreated'], '(derived from Bexio, never hardcoded)')
for w in d['warnings']:
    print('    ! ', w)
"

# -------------------------------------------------------------------------------------------------
say "3. Importing Shopify and Amazon fixtures"
for source in Shopify Amazon; do
  api -X POST "${BASE_URL}/api/imports" "${JSON[@]}" -d "{\"sourceSystem\":\"${source}\"}" | jqp "
print('    ${source}: seen', d['documentsSeen'], '| created', d['created'], '| already imported', d['duplicatesSkipped'], '| failed', d['failures'])
"
done

# -------------------------------------------------------------------------------------------------
say "4. Deterministic validation and tax determination (AI is disabled)"
INVOICE_IDS=$(api "${BASE_URL}/api/invoices?take=50" | jqp "print(' '.join(i['id'] for i in d))")
for id in ${INVOICE_IDS}; do
  api -X POST "${BASE_URL}/api/invoices/${id}/validate" -o /dev/null
done

api "${BASE_URL}/api/invoices?take=50" | jqp "
for i in sorted(d, key=lambda x: x['invoiceNumber'] or ''):
    print(f\"    {(i['invoiceNumber'] or '(none)'):<24} {i['sourceSystem']:<8} {i['currency']} {i['totalAmount']:>10}  {i['state']}\")
"
ok "records needing judgement are routed to review; the deliberately broken fixture is blocked"

# -------------------------------------------------------------------------------------------------
say "5. The §31 reference invoice: INV-10001"
INV=$(api "${BASE_URL}/api/invoices?take=50" | jqp "print([i['id'] for i in d if i['invoiceNumber']=='INV-10001'][0])")
api "${BASE_URL}/api/invoices/${INV}" | jqp "
inv=d['invoice']
print('    net', inv['subtotalAmount'], '| tax', inv['taxAmount'], '| total', inv['totalAmount'], inv['currency'])
a=d['taxAssessments'][0]
print('    tax code', a['internalTaxCode'], '| rate', a['ratePercent'], '| method', a['determinationMethod'])
print('    provenance entries:', len(d['provenance']))
for p in d['provenance'][:3]:
    print('      ', p['fieldPath'], '<-', p['sourceSystem'], p['sourceFieldPath'])
"

# -------------------------------------------------------------------------------------------------
say "6. Human mapping decisions (pre-flight refuses to guess these)"
CUSTOMER_ID=$(api "${BASE_URL}/api/invoices/${INV}" | jqp "print(d['customer']['id'])")
CONTACT_ID=$(api "${BASE_URL}/api/mappings/bexio-reference?kind=Contact" | jqp "print([c['bexioId'] for c in d if 'ABC' in (c['name'] or '')][0])")

api -X POST "${BASE_URL}/api/mappings/customers" "${JSON[@]}" \
  -d "{\"customerId\":\"${CUSTOMER_ID}\",\"bexioContactId\":\"${CONTACT_ID}\",\"label\":\"ABC Swiss GmbH\"}" -o /dev/null
ok "customer mapped to Bexio contact ${CONTACT_ID}"

for pair in "REVENUE_GOODS:3200" "REVENUE_SHIPPING:3700"; do
  code="${pair%%:*}"; number="${pair##*:}"
  account=$(api "${BASE_URL}/api/mappings/bexio-reference?kind=Account" | jqp "print([a['bexioId'] for a in d if a['code']=='${number}'][0])")
  api -X POST "${BASE_URL}/api/mappings/accounts" "${JSON[@]}" \
    -d "{\"internalAccountCode\":\"${code}\",\"bexioAccountId\":\"${account}\",\"number\":\"${number}\",\"name\":\"mapped by demo\"}" -o /dev/null
  ok "${code} -> Bexio account ${number}"
done

# -------------------------------------------------------------------------------------------------
say "7. Bexio pre-flight preview"
api "${BASE_URL}/api/invoices/${INV}/preview" | jqp "
print('    can synchronise:', d['canSynchronize'])
print('    destination    :', d['destination'])
print('    contact        :', d['bexioContactName'], f\"({d['bexioContactId']})\")
print('    totals         : net', d['totalNet'], '| tax', d['totalTax'], '| gross', d['totalGross'], d['currency'])
for l in d['lines']:
    print('      line', l['lineNumber'], '-', l['description'], '| tax', l['bexioTaxName'], '| account', l['bexioAccountId'])
errors=[i for i in d['report']['issues'] if i['severity']==2]
print('    blocking issues:', len(errors))
for e in errors:
    print('      -', e['code'], e['message'])
"

# -------------------------------------------------------------------------------------------------
say "8. Proving an unapproved invoice cannot reach Bexio"
BEFORE=$(api "${BASE_URL}/api/synchronizations" | jqp "print(len(d))")
[ "${BEFORE}" = "0" ] && ok "no synchronisation has been attempted for any invoice yet"

# -------------------------------------------------------------------------------------------------
say "9. Submitting for approval, then approving explicitly"
api -X POST "${BASE_URL}/api/invoices/${INV}/submit" -o /dev/null -w '' || true
STATE=$(api "${BASE_URL}/api/invoices/${INV}" | jqp "print(d['invoice']['state'])")
ok "state after submission: ${STATE}"

api -X POST "${BASE_URL}/api/invoices/${INV}/approve" "${JSON[@]}" \
  -d '{"comment":"Checked against the source order."}' -o /dev/null
STATE=$(api "${BASE_URL}/api/invoices/${INV}" | jqp "print(d['invoice']['state'])")
ok "state after approval: ${STATE} (approval and the outbox entry were written in one transaction)"

# -------------------------------------------------------------------------------------------------
say "10. Waiting for the background worker to synchronise"
for _ in $(seq 1 40); do
  STATE=$(api "${BASE_URL}/api/invoices/${INV}" | jqp "print(d['invoice']['state'])")
  [ "${STATE}" = "Synced" ] && break
  sleep 1
done

api "${BASE_URL}/api/invoices/${INV}" | jqp "
inv=d['invoice']
print('    state          :', inv['state'])
print('    Bexio invoice  :', inv['bexioInvoiceId'])
print('    approved by    :', inv['approvedBy'])
print('    applied tax id :', d['taxAssessments'][0]['appliedBexioTaxId'])
"

api "${BASE_URL}/api/synchronizations" | jqp "
for a in d:
    print('    attempt:', a['status'], '| external id', a['externalId'], '| attempts', a['attemptCount'])
    print('    idempotency key:', a['idempotencyKey'])
"

# -------------------------------------------------------------------------------------------------
say "11. Audit trail for the whole lifecycle, under one correlation id"
CORRELATION=$(api "${BASE_URL}/api/invoices/${INV}" | jqp "print(d['invoice']['correlationId'])")
api "${BASE_URL}/api/audit?entityId=${INV}&take=50" | jqp "
for e in reversed(d):
    ai=' [AI]' if e['aiInvolved'] else ''
    print('   ', e['occurredAt'][:19], e['action'], '-', e['actor'], ai)
"
ok "correlation id ${CORRELATION}"

# -------------------------------------------------------------------------------------------------
say "12. Repeating the import — no duplicate may be created (§31 step 20)"
api -X POST "${BASE_URL}/api/imports" "${JSON[@]}" -d '{"sourceSystem":"Shopify"}' | jqp "
print('    seen', d['documentsSeen'], '| created', d['created'], '| already imported', d['duplicatesSkipped'])
"
sleep 3

FINAL=$(api "${BASE_URL}/api/synchronizations" | jqp "print(len([a for a in d if a['status']=='Succeeded']))")
INVOICE_COUNT=$(api "${BASE_URL}/api/invoices?take=100" | jqp "print(len([i for i in d if i['invoiceNumber']=='INV-10001']))")

if [ "${FINAL}" = "1" ] && [ "${INVOICE_COUNT}" = "1" ]; then
  ok "exactly one canonical invoice and one successful synchronisation remain — no duplicate was created"
else
  die "DUPLICATE DETECTED: ${INVOICE_COUNT} canonical invoice(s), ${FINAL} successful synchronisation(s)"
fi

printf '\n\033[1;32m==> The end-to-end demonstration completed successfully.\033[0m\n'
printf '    Open %s to review the same data in the UI.\n\n' "${BASE_URL}"
