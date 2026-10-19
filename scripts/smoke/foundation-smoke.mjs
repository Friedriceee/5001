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

async function waitFor(check, description, timeoutMs = 30000) {
  const deadline = Date.now() + timeoutMs
  while (Date.now() < deadline) {
    const value = await check()
    if (value) return value
    await new Promise(resolve => setTimeout(resolve, 500))
  }
  throw new Error(`Timed out waiting for ${description}.`)
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
  const secondMenu = await request(new URL('/api/merchants/2/dishes', gateway))
  expectStatus(secondMenu, 200, 'catalog: merchant 2 menu')
  const secondDish = secondMenu.json.data.find(d => d.isAvailable && d.inventory >= 1)
  if (!secondDish) throw new Error('Merchant 2 has no available dish to reserve.')

  const internalViaGateway = await request(new URL('/internal/catalog/reservations', gateway), {
    method: 'POST', body: { reservationId: 'smoke-blocked', items: [{ dishId: dish.id, quantity: 1 }] },
  })
  expectStatus(internalViaGateway, 404, 'gateway does not expose /internal')

  // ---- Internal reservation, as the Order service would call it ----
  const loginAs = async username => {
    const response = await request(new URL('/api/auth/login', gateway), {
      method: 'POST', body: { username, password },
    })
    expectStatus(response, 200, `identity: ${username} login`)
    return { Authorization: `Bearer ${response.json.data.accessToken}` }
  }
  const auth = await loginAs('customer1')
  const merchant1Auth = await loginAs('merchant1')
  const merchant2Auth = await loginAs('merchant2')
  const rider1Auth = await loginAs('rider1')
  const rider2Auth = await loginAs('rider2')
  const adminAuth = await loginAs('admin')
  const accounts = await request(new URL('/api/accounts?pageSize=20', gateway), { headers: adminAuth })
  expectStatus(accounts, 200, 'identity: administrator lists accounts')
  for (const username of ['customer1', 'merchant1', 'merchant2', 'rider1', 'rider2', 'admin']) {
    if (!accounts.json.data.items.some(account => account.username === username)) {
      throw new Error(`Administrator account list is missing ${username}.`)
    }
  }

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

  // ---- Order through the gateway: cart, checkout (reserves in Catalog), replay, decline ----
  const stockOf = async () => (await request(new URL(`/api/catalog/dishes/${dish.id}`, gateway))).json.data.inventory
  const secondStockOf = async () => (await request(new URL(`/api/catalog/dishes/${secondDish.id}`, gateway))).json.data.inventory
  const checkout = (key, paymentMethod) => request(new URL('/api/checkout', gateway), {
    method: 'POST',
    headers: { ...auth, 'Idempotency-Key': key },
    body: { addressId, needUtensils: 1, paymentMethod },
  })

  expectStatus(await request(new URL('/api/cart', gateway), { method: 'DELETE', headers: auth }), 200, 'order: clear cart')
  const addresses = await request(new URL('/api/addresses', gateway), { headers: auth })
  expectStatus(addresses, 200, 'order: list addresses')
  const addressId = addresses.json.data[0]?.id
  if (!addressId) throw new Error('customer1 has no delivery address.')

  const before = await stockOf()
  const secondBefore = await secondStockOf()
  expectStatus(await request(new URL('/api/cart/items', gateway), { method: 'POST', headers: auth, body: { dishId: dish.id, quantity: 2 } }), 201, 'order: add to cart')
  expectStatus(await request(new URL('/api/cart/items', gateway), { method: 'POST', headers: auth, body: { dishId: secondDish.id, quantity: 1 } }), 201, 'order: add merchant 2 item')
  const key = `smoke-${Date.now()}`
  const paid = await checkout(key, 'SimulatedCard')
  expectStatus(paid, 201, 'order: checkout')
  const orderId = paid.json.data.orderId
  const replayed = await checkout(key, 'SimulatedCard')
  expectStatus(replayed, 200, 'order: replayed checkout')
  if (replayed.json.data.orderId !== orderId || !replayed.json.data.replayed) throw new Error('Replay must return the same order.')
  if (await stockOf() !== before - 2) throw new Error('Checkout should reserve exactly 2 units in Catalog.')
  if (await secondStockOf() !== secondBefore - 1) throw new Error('Checkout should reserve merchant 2 stock.')

  const mine = await request(new URL('/api/orders', gateway), { headers: auth })
  expectStatus(mine, 200, 'order: my orders')
  if (!mine.json.data.some(o => o.id === orderId)) throw new Error(`Order ${orderId} is missing from my orders.`)

  expectStatus(await request(new URL('/api/cart/items', gateway), { method: 'POST', headers: auth, body: { dishId: dish.id, quantity: 1 } }), 201, 'order: add to cart again')
  const declined = await checkout(`smoke-declined-${Date.now()}`, 'SimulatedCardDeclined')
  expectStatus(declined, 402, 'order: declined payment')
  if (await stockOf() !== before - 2) throw new Error('A declined payment must release its reservation.')
  expectStatus(await request(new URL('/api/cart', gateway), { method: 'DELETE', headers: auth }), 200, 'order: clear cart')

  const orderService = new URL(process.env.JIKEDA_ORDER_URL || 'http://127.0.0.1:5103')
  assertLocal(orderService, 'JIKEDA_ORDER_URL')
  expectStatus(await request(new URL('/internal/order/events', gateway), { headers: auth }), 404, 'gateway does not expose order events')
  const events = await request(new URL('/internal/order/events?after=0&limit=200', orderService), { headers: auth })
  expectStatus(events, 200, 'order: event feed')
  if (!events.json.data.some(e => e.payload.orderId === orderId)) throw new Error(`No OrderConfirmed event for order ${orderId}.`)
  console.log(`order: order ${orderId} paid, stock reserved, decline released, OrderConfirmed published`)

  // ---- Full business loop: both merchants -> rider -> customer sees Completed ----
  const waitForMerchantOrder = (headers, name) => waitFor(async () => {
    const response = await request(new URL('/api/fulfilment/orders', gateway), { headers })
    expectStatus(response, 200, `fulfilment: ${name} orders`)
    return response.json.data.find(order => order.orderId === orderId)
  }, `${name} to receive order ${orderId}`)

  await Promise.all([
    waitForMerchantOrder(merchant1Auth, 'merchant1'),
    waitForMerchantOrder(merchant2Auth, 'merchant2'),
  ])

  for (const [name, headers] of [['merchant1', merchant1Auth], ['merchant2', merchant2Auth]]) {
    for (const action of ['Accept', 'StartPreparing', 'MarkReady']) {
      const response = await request(new URL(`/api/fulfilment/orders/${orderId}/status`, gateway), {
        method: 'PUT', headers, body: { action },
      })
      expectStatus(response, 200, `fulfilment: ${name} ${action}`)
    }
  }

  await waitFor(async () => {
    const response = await request(new URL('/api/riders/available', gateway), { headers: rider1Auth })
    expectStatus(response, 200, 'fulfilment: rider1 available tasks')
    const availableOrder = response.json.data.find(order => order.orderId === orderId)
    if (availableOrder && (!availableOrder.deliveryAddress || !availableOrder.contactName || !availableOrder.phoneNumber)) {
      throw new Error('The rider delivery task is missing its address or customer contact snapshot.')
    }
    return availableOrder
  }, `order ${orderId} to become available to riders`)

  expectStatus(await request(new URL(`/api/deliveries/${orderId}/accept`, gateway), {
    method: 'POST', headers: rider1Auth,
  }), 200, 'fulfilment: rider1 accepts delivery')

  const rider2Available = await request(new URL('/api/riders/available', gateway), { headers: rider2Auth })
  expectStatus(rider2Available, 200, 'fulfilment: rider2 checks available tasks')
  if (rider2Available.json.data.some(order => order.orderId === orderId)) {
    throw new Error('An accepted delivery must no longer be available to another rider.')
  }

  for (const action of ['PickUp', 'StartDelivering', 'Complete']) {
    expectStatus(await request(new URL(`/api/deliveries/${orderId}/status`, gateway), {
      method: 'PUT', headers: rider1Auth, body: { action },
    }), 200, `fulfilment: rider1 ${action}`)
  }

  await waitFor(async () => {
    const response = await request(new URL('/api/orders', gateway), { headers: auth })
    expectStatus(response, 200, 'order: customer refreshes order history')
    return response.json.data.find(order => order.id === orderId)?.status === 'Completed'
  }, `customer order ${orderId} to show Completed`)
  console.log(`end-to-end: order ${orderId} completed through two merchants and rider1; customer sees Completed`)

  console.log('Microservice end-to-end smoke test passed.')
}

main().catch(error => {
  console.error(error)
  process.exitCode = 1
})
