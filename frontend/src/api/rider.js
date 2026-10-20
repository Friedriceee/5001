import http from './http'

// 可接任务池（待派送订单）
export function listAvailableDeliveries() {
  return withLegacyRoute(() => http.get('/riders/available', { silent: true }), () => http.get('/rider/available'))
}

// 当前骑手自己的配送任务
export function listMyDeliveries() {
  return withLegacyRoute(() => http.get('/riders/deliveries', { silent: true }), () => http.get('/rider/deliveries'))
}

// 接单
export function acceptDelivery(orderId) {
  return withLegacyRoute(
    () => http.post(`/deliveries/${orderId}/accept`, null, { silent: true }),
    () => http.post(`/rider/deliveries/${orderId}/accept`),
  )
}

// 推进配送状态：action = PickUp / StartDelivering / Complete
export function advanceDelivery(orderId, action) {
  return withLegacyRoute(
    () => http.put(`/deliveries/${orderId}/status`, { action }, { silent: true }),
    () => http.put(`/rider/deliveries/${orderId}/status`, { action }),
  )
}

async function withLegacyRoute(primary, legacy) {
  try {
    return await primary()
  } catch (error) {
    if (![404, 405].includes(error.response?.status)) throw error
    return legacy()
  }
}
