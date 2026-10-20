import http from './http'

export function createDish(payload) {
  return http.post('/merchant/dishes', payload)
}

export function listDishes(merchantId) {
  return http.get(`/merchant/${merchantId}/dishes`)
}

export function updateDish(dishId, merchantId, payload) {
  return http.put(`/merchant/dishes/${dishId}`, payload, { params: { merchantId } })
}

export function deleteDish(dishId, merchantId) {
  return http.delete(`/merchant/dishes/${dishId}`, { params: { merchantId } })
}

export function createSpecialOffer(payload) {
  return http.post('/merchant/special-offers', payload)
}

export function listSpecialOffers(merchantId) {
  return http.get(`/merchant/${merchantId}/special-offers`)
}

export function updateSpecialOffer(offerId, merchantId, payload) {
  return http.put(
    `/merchant/special-offers/${offerId}`,
    payload,
    { params: { merchantId } },
  )
}

export function deleteSpecialOffer(offerId, merchantId) {
  return http.delete(
    `/merchant/special-offers/${offerId}`,
    { params: { merchantId } },
  )
}

export function listMerchantOrders() {
  return withLegacyRoute(() => http.get('/fulfilment/orders', { silent: true }), () => http.get('/merchant/orders'))
}

export function transitionMerchantOrder(orderId, action, reason = null) {
  const payload = { action, reason }
  return withLegacyRoute(
    () => http.put(`/fulfilment/orders/${orderId}/status`, payload, { silent: true }),
    () => http.put(`/merchant/orders/${orderId}/status`, payload),
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
