// Read-only acceptance workload for the four-service stack, through its gateway.
// Password is supplied at runtime, never embedded in the script or result files.
import http from 'k6/http'
import { check, sleep } from 'k6'

const base = (__ENV.BASE_URL || 'http://127.0.0.1:5000').replace(/\/$/, '')
const duration = __ENV.DURATION || '30s'
export const options = {
  scenarios: {
    catalog: { executor: 'constant-vus', vus: 10, duration, exec: 'catalog' },
    customer: { executor: 'constant-vus', vus: 5, duration, exec: 'customer' },
    merchant: { executor: 'constant-vus', vus: 3, duration, exec: 'merchant' },
    rider: { executor: 'constant-vus', vus: 2, duration, exec: 'rider' },
  },
  thresholds: {
    'http_req_duration{phase:workload}': ['p(95)<2000'],
    'http_req_failed{phase:workload}': ['rate<0.01'],
    checks: ['rate==1'],
  },
}

export function setup() {
  if (!__ENV.PASSWORD) throw new Error('PASSWORD must be supplied at runtime')
  const tokens = {}
  for (const name of ['customer1', 'merchant1', 'rider1']) {
    const response = http.post(`${base}/api/auth/login`, JSON.stringify({
      username: name, password: __ENV.PASSWORD,
    }), { headers: { 'Content-Type': 'application/json' }, tags: { phase: 'setup' } })
    if (response.status !== 200) throw new Error(`${name} setup login: HTTP ${response.status}`)
    tokens[name] = response.json('data.accessToken')
  }
  return tokens
}

function read(path, token, name) {
  const response = http.get(base + path, {
    headers: token ? { Authorization: `Bearer ${token}` } : {},
    tags: { phase: 'workload', endpoint: name },
    timeout: '10s',
  })
  check(response, { [`${name}: HTTP 200 with successful envelope`]: r =>
    r.status === 200 && r.json('success') === true })
  sleep(1)
}
export function catalog() { read('/api/merchants/1/dishes', null, 'catalog-menu') }
export function customer(data) { read('/api/orders', data.customer1, 'customer-orders') }
export function merchant(data) { read('/api/fulfilment/orders', data.merchant1, 'merchant-orders') }
export function rider(data) { read('/api/riders/available', data.rider1, 'rider-tasks') }

export function handleSummary(data) {
  return { 'summary.json': JSON.stringify(data, null, 2) }
}
