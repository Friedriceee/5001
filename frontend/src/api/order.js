import http from './http'

// ---------- 订单 ----------
export function createOrder(payload) {
  return http.post('/orders', payload)
}

export function listOrders(userId) {
  return withLegacyRoute(() => http.get('/orders', { silent: true }), () => http.get(`/orders/user/${userId}`))
}

export function cancelOrder(orderId) {
  return http.delete(`/orders/${orderId}`)
}

// ---------- 地址 ----------
export function createAddress(payload) {
  return http.post('/user/addresses', payload)
}

export function listAddresses(userId) {
  return http.get(`/user/${userId}/addresses`)
}

async function withLegacyRoute(primary, legacy) {
  try {
    return await primary()
  } catch (error) {
    if (![404, 405].includes(error.response?.status)) throw error
    return legacy()
  }
}
