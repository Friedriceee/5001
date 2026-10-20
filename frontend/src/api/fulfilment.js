import http from './http'

// Fulfilment owns courier identity and delivery state. Customer order history
// merges this projection with the Order service response.
export function listCustomerDeliveryTracking() {
  return http.get('/fulfilment/tracking/orders', { silent: true })
}
