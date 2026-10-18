import http from 'http'
import https from 'https'

function request(url, { method = 'GET', body, headers = {} } = {}) {
  return new Promise((resolve, reject) => {
    const client = url.protocol === 'https:' ? https : http
    const payload = body === undefined ? undefined : JSON.stringify(body)
    const req = client.request(url, {
      method,
      headers: {
        ...(payload ? { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(payload) } : {}),
        ...headers,
      },
    }, response => {
      let text = ''
      response.setEncoding('utf8')
      response.on('data', chunk => { text += chunk })
      response.on('end', () => {
        let json = null
        try { json = text ? JSON.parse(text) : null } catch { /* not JSON */ }
        resolve({ status: response.statusCode || 0, headers: response.headers, body: text, json })
      })
    })
    req.on('error', reject)
    req.setTimeout(5000, () => req.destroy(new Error(`Timed out calling ${url}`)))
    if (payload) req.write(payload)
    req.end()
  })
}

function expectStatus(response, expected, step) {
  if (response.status !== expected) {
    throw new Error(`${step}: expected HTTP ${expected}, got ${response.status}: ${response.body}`)
  }
  console.log(`${step}: HTTP ${response.status}`)
}

function assertLocal(url, name) {
  if (!['127.0.0.1', 'localhost'].includes(url.hostname)) {
    throw new Error(`${name} must be local; the foundation smoke test is restricted to a local stack.`)
  }
}

async function main() {
  const gateway = new URL(process.env.JIKEDA_GATEWAY_URL || 'http://127.0.0.1:5000')
  // Direct Catalog port: /internal/* routes are deliberately not exposed by the gateway.
  const catalog = new URL(process.env.JIKEDA_CATALOG_URL || 'http://127.0.0.1:5102')
  assertLocal(gateway, 'JIKEDA_GATEWAY_URL')
  assertLocal(catalog, 'JIKEDA_CATALOG_URL')
  // Local seed account from the Identity service (dev/staging only).
  const password = process.env.JIKEDA_SMOKE_PASSWORD || 'Passw0rd!123'

  const checks = [
    ['gateway', '/health'],
    ['identity', '/health/identity'],
    ['catalog', '/health/catalog'],
    ['order', '/health/order'],
    ['fulfilment', '/health/fulfilment'],
  ]

  for (const [service, path] of checks) {
    const response = await request(new URL(path, gateway))
    if (response.status < 200 || response.status >= 300) {
      throw new Error(`${service} health failed: HTTP ${response.status}: ${response.body}`)
    }

    const correlationId = response.headers['x-correlation-id']
    if (!correlationId) {
      throw new Error(`${service} health response is missing X-Correlation-ID.`)
    }

    console.log(`${service}: healthy (${correlationId})`)
  }

  // ---- Catalog through the gateway (public, no token) ----
  const merchants = await request(new URL('/api/merchants', gateway))
  expectStatus(merchants, 200, 'catalog: list merchants')
  if (!merchants.json?.data?.some(m => m.id === 1)) throw new Error('Seeded merchant 1 is missing.')

  const menu = await request(new URL('/api/merchants/1/dishes', gateway))
  expectStatus(menu, 200, 'catalog: merchant 1 menu')
  const dish = menu.json.data.find(d => d.isAvailable && d.inventory >= 2)
  if (!dish) throw new Error('Merchant 1 has no available dish to reserve.')

  const internalViaGateway = await request(new URL('/internal/catalog/reservations', gateway), {
    method: 'POST', body: { reservationId: 'smoke-blocked', items: [{ dishId: dish.id, quantity: 1 }] },
  })
  expectStatus(internalViaGateway, 404, 'gateway does not expose /internal')

  // ---- Internal reservation, as the Order service would call it ----
  const login = await request(new URL('/api/auth/login', gateway), {
    method: 'POST', body: { username: 'customer1', password },
  })
  expectStatus(login, 200, 'identity: customer login')
  const auth = { Authorization: `Bearer ${login.json.data.accessToken}` }

  const reservationId = `smoke-${Date.now()}`
  const body = { reservationId, items: [{ dishId: dish.id, quantity: 2 }] }
  const reserve = await request(new URL('/internal/catalog/reservations', catalog), { method: 'POST', body, headers: auth })
  expectStatus(reserve, 201, 'catalog: reserve stock')
  const replay = await request(new URL('/internal/catalog/reservations', catalog), { method: 'POST', body, headers: auth })
  expectStatus(replay, 200, 'catalog: replayed reservation')

  const afterReserve = await request(new URL(`/api/catalog/dishes/${dish.id}`, gateway))
  if (afterReserve.json.data.inventory !== dish.inventory - 2) {
    throw new Error(`Stock should drop by exactly 2 (was ${dish.inventory}, now ${afterReserve.json.data.inventory}).`)
  }

  const release = await request(new URL(`/internal/catalog/reservations/${reservationId}/release`, catalog), { method: 'POST', headers: auth })
  expectStatus(release, 200, 'catalog: release reservation')
  const afterRelease = await request(new URL(`/api/catalog/dishes/${dish.id}`, gateway))
  if (afterRelease.json.data.inventory !== dish.inventory) {
    throw new Error(`Stock should be restored to ${dish.inventory}, got ${afterRelease.json.data.inventory}.`)
  }
  console.log(`catalog: stock ${dish.inventory} -> ${dish.inventory - 2} -> ${dish.inventory}`)

  console.log('Microservice foundation smoke test passed.')
}

main().catch(error => {
  console.error(error)
  process.exitCode = 1
})
