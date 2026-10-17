// Turns the k6 summaries of a scale test into a Markdown report.
//   node scripts/load/summarize.mjs load-results "1 3"
import { readFileSync, existsSync } from 'node:fs'

const dir = process.argv[2] || 'load-results'
const counts = (process.argv[3] || '1 3').split(/\s+/).filter(Boolean)
const read = (name) => (existsSync(`${dir}/${name}`) ? JSON.parse(readFileSync(`${dir}/${name}`, 'utf8')).metrics : null)
const n = (value, digits = 0) => (typeof value === 'number' ? value.toFixed(digits) : 'n/a')

const lines = ['## Scale-out test', '']
lines.push('Each API replica is capped at 1 vCPU. Load: 80% sign-in (CPU-heavy password hashing), 20% browsing.', '')
lines.push('| API replicas | Requests/s | Sign-in p95 (ms) | Browse p95 (ms) | Error rate | Replicas that answered |')
lines.push('|---|---|---|---|---|---|')
const rows = []
for (const count of counts) {
  const m = read(`load-${count}.json`)
  if (!m) continue
  const spread = existsSync(`${dir}/spread-${count}.txt`) ? readFileSync(`${dir}/spread-${count}.txt`, 'utf8').trim().split('\n').filter(Boolean).length : 'n/a'
  const row = {
    count,
    rps: m.http_reqs.rate,
    signin: m['http_req_duration{scenario:signin}']?.['p(95)'],
    browse: m['http_req_duration{scenario:browse}']?.['p(95)'],
    errors: m.http_req_failed.value ?? m.http_req_failed.rate,
    spread,
  }
  rows.push(row)
  lines.push(`| ${count} | ${n(row.rps)} | ${n(row.signin)} | ${n(row.browse)} | ${n(row.errors * 100, 2)}% | ${row.spread} |`)
}
if (rows.length > 1) {
  const first = rows[0], last = rows[rows.length - 1]
  lines.push('', `Throughput went from ${n(first.rps)} to ${n(last.rps)} requests/s (x${n(last.rps / first.rps, 2)}) with ${first.count} to ${last.count} replicas.`)
}

const c = read('consistency.json')
if (c) {
  lines.push('', '## Consistency under a race', '')
  lines.push('| Check | Result |', '|---|---|')
  lines.push(`| Orders created (stock was the limit) | ${c.orders_created?.count ?? 'n/a'} |`)
  lines.push(`| Duplicate requests answered with the stored order | ${c.orders_replayed?.count ?? 0} |`)
  lines.push(`| Requests rejected as sold out | ${c.checkout_rejected?.count ?? 0} |`)
  lines.push(`| Invariants held (no oversell, no double charge) | ${n((c.invariants_hold?.value ?? c.invariants_hold?.rate) * 100)}% |`)
  lines.push(`| Server errors | ${n((c.http_req_failed.value ?? c.http_req_failed.rate) * 100, 2)}% |`)
}
console.log(lines.join('\n'))
