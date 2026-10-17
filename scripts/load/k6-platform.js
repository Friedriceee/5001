// Load and consistency tests for the platform API, scripted with k6.
//
//   SCENARIO=load         read and sign-in traffic for a fixed time; compare 1 replica with N replicas
//   SCENARIO=consistency  many customers race for a scarce dish; no overselling, no double charge
//
// Environment
//   BASE_URL   origin that serves the API (default http://localhost:8080, the nginx in front of the replicas)
//   PASSWORD   password of the seeded demo accounts (required)
//   SCENARIO   load | consistency (default load)
//   VUS        virtual users for the load scenario (default 40); 80% sign in, 20% browse
//   DURATION   duration of the load scenario (default 30s)
//   STOCK      units of the dish that is raced for (default 20)
//   BUYERS     customers racing for it (default 30)
//
// Exit code is non-zero when a threshold fails, so the same script gates a pipeline.

import http from 'k6/http'
import { check } from 'k6'
import { Counter, Rate } from 'k6/metrics'

const BASE_URL = (__ENV.BASE_URL || 'http://localhost:8080').replace(/\/$/, '')
const PASSWORD = __ENV.PASSWORD
const SCENARIO = __ENV.SCENARIO || 'load'
const VUS = Number(__ENV.VUS || 40)
const DURATION = __ENV.DURATION || '30s'
const STOCK = Number(__ENV.STOCK || 20)
const BUYERS = Number(__ENV.BUYERS || 30)

const ordersCreated = new Counter('orders_created')
const ordersReplayed = new Counter('orders_replayed')
const checkoutRejected = new Counter('checkout_rejected')
const invariantsHold = new Rate('invariants_hold')

// A business rejection (sold out, conflict) is an expected answer; a 5xx is a failure.
const businessStatuses = http.expectedStatuses(200, 201, 400, 409)

const loadScenarios = {
  // Signing in is the CPU-heavy call (password hashing), so it is what shows capacity growing with replicas.
  signin: { executor: 'constant-vus', vus: Math.max(1, Math.round(VUS * 0.8)), duration: DURATION, exec: 'signin' },
  browse: { executor: 'constant-vus', vus: Math.max(1, Math.round(VUS * 0.2)), duration: DURATION, exec: 'browse' },
}
const consistencyScenarios = {
  race: { executor: 'per-vu-iterations', vus: BUYERS, iterations: 1, maxDuration: '2m', exec: 'race' },
}

export const options = SCENARIO === 'consistency'
  ? {
      scenarios: consistencyScenarios,
      thresholds: {
        // Exactly the stock is sold, however many customers (and duplicate requests) arrive.
        orders_created: [`count==${STOCK}`],
        invariants_hold: ['rate==1'],
        http_req_failed: ['rate<0.01'],
      },
    }
  : {
      scenarios: loadScenarios,
      // Latency is reported, not gated: a single replica is expected to saturate, and the point
      // of the run is to compare it with several. Errors are never acceptable.
      thresholds: {
        'http_req_duration{scenario:browse}': ['p(95)<5000'],
        'http_req_duration{scenario:signin}': ['p(95)<5000'],
        http_req_failed: ['rate<0.01'],
      },
    }

function json(response) {
  try { return response.json() } catch (e) { return null }
}

function api(method, path, body, token, extra = {}) {
  const headers = { 'Content-Type': 'application/json', ...(extra.headers || {}) }
  if (token) headers.Authorization = `Bearer ${token}`
  return http.request(method, `${BASE_URL}${path}`, body === undefined ? null : JSON.stringify(body), { headers, tags: extra.tags })
}

function login(username, password = PASSWORD) {
  const response = api('POST', '/api/auth/login', { username, password })
  const body = json(response)
  return { response, token: body && body.data ? body.data.accessToken : null, profileId: body && body.data ? body.data.profileId : null }
}

// ---------------------------------------------------------------- load
export function setup() {
  if (!PASSWORD) throw new Error('PASSWORD is required')
  const health = http.get(`${BASE_URL}/api/health`)
  if (health.status !== 200) throw new Error(`API is not healthy at ${BASE_URL}: ${health.status}`)
  if (SCENARIO !== 'consistency') return {}

  // Merchant creates a dish with scarce stock; BUYERS customers are registered up front.
  const merchant = login('demo_merchant')
  if (!merchant.token) throw new Error('demo_merchant could not sign in')
  const created = api('POST', '/api/merchant/dishes', {
    name: `Race dish ${Date.now()}`, price: 1, category: 'Load test', inventory: STOCK,
  }, merchant.token)
  const dish = json(created).data
  const run = Date.now()
  const buyers = []
  for (let i = 0; i < BUYERS; i++) {
    const username = `race_${run}_${i}`
    const registered = api('POST', '/api/auth/customer/register', {
      username, password: PASSWORD, displayName: `Racer ${i}`, phoneNumber: '81234567',
    })
    if (registered.status !== 201) throw new Error(`could not register ${username}: ${registered.status} ${registered.body}`)
    buyers.push(username)
  }
  return { dishId: dish.id, merchantId: merchant.profileId, buyers }
}

export function browse() {
  const merchants = api('GET', '/api/merchants')
  check(merchants, { 'merchants listed': (r) => r.status === 200 })
  const menu = api('GET', '/api/merchant/1/dishes')
  check(menu, { 'menu listed': (r) => r.status === 200 })
}

export function signin() {
  const { response, token } = login('demo_customer')
  check(response, { 'signed in': (r) => r.status === 200 && !!token })
}

// ---------------------------------------------------------------- consistency
export function race(data) {
  const username = data.buyers[(__VU - 1) % data.buyers.length]
  const { token } = login(username)
  const address = json(api('POST', '/api/user/addresses', {
    address: 'Race Road ZoneA', contactName: 'Racer', phoneNumber: '13800000009',
  }, token)).data

  api('DELETE', '/api/cart', undefined, token)
  api('POST', '/api/cart/items', { dishId: data.dishId, dishNum: 1 }, token)

  // The same request is sent twice at once, as a retrying client or a double click would.
  const key = `race-${__VU}-${Date.now()}`
  const request = {
    method: 'POST',
    url: `${BASE_URL}/api/checkout`,
    body: JSON.stringify({ addressId: address.id }),
    params: {
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}`, 'Idempotency-Key': key },
      responseCallback: businessStatuses,
    },
  }
  const [first, second] = http.batch([request, request])
  for (const response of [first, second]) {
    if (response.status === 201) ordersCreated.add(1)
    else if (response.status === 200) ordersReplayed.add(1)
    else checkoutRejected.add(1)
  }
  // The pair must never create two orders.
  const created = [first, second].filter((r) => r.status === 201).length
  invariantsHold.add(created <= 1)
}

export function teardown(data) {
  if (SCENARIO !== 'consistency') return
  const dishes = json(api('GET', `/api/merchant/${data.merchantId}/dishes`)).data
  const dish = dishes.find((item) => item.id === data.dishId)
  // Never negative; and because demand exceeds stock, every unit is sold.
  invariantsHold.add(dish.inventory >= 0)
  invariantsHold.add(dish.inventory === Math.max(0, STOCK - Math.min(STOCK, BUYERS)))
  console.log(`dish ${data.dishId}: stock ${STOCK}, buyers ${BUYERS}, inventory left ${dish.inventory}`)
}
