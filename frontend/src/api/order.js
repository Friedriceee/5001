import http from './http'

// ---------- 订单 ----------
export function createOrder(payload) {
  return http.post('/orders', payload)
}

export function listOrders() {
  return http.get('/orders')
}

export function cancelOrder(orderId) {
  return http.delete(`/orders/${orderId}`)
}

// ---------- 地址 ----------
export function createAddress(payload) {
  return http.post('/addresses', payload)
}

export function listAddresses() {
  return http.get('/addresses')
}
