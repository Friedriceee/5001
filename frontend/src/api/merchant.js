import http from './http'

export function createDish(payload) {
  return http.post('/catalog/merchant/dishes', payload)
}

export function listDishes() {
  return http.get('/catalog/merchant/dishes')
}

export function updateDish(dishId, _merchantId, payload) {
  return http.put(`/catalog/merchant/dishes/${dishId}`, payload)
}

export function deleteDish(dishId) {
  return http.delete(`/catalog/merchant/dishes/${dishId}`)
}

export function createSpecialOffer(payload) {
  return http.post('/merchant/special-offers', payload)
}

export function listSpecialOffers(merchantId) {
  return http.get(`/merchants/${merchantId}/special-offers`)
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
  return http.get('/fulfilment/orders')
}

export function transitionMerchantOrder(orderId, action, reason = null) {
  return http.put(`/fulfilment/orders/${orderId}/status`, { action, reason })
}
