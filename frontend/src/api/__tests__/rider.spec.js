import { beforeEach, describe, expect, it, vi } from 'vitest'

const http = vi.hoisted(() => ({
  get: vi.fn(),
  post: vi.fn(),
  put: vi.fn(),
}))

vi.mock('@/api/http', () => ({ default: http }))

import {
  acceptDelivery,
  advanceDelivery,
  listAvailableDeliveries,
  listMyDeliveries,
} from '@/api/rider'

describe('rider delivery API', () => {
  beforeEach(() => vi.clearAllMocks())

  it('lists available deliveries', async () => {
    http.get.mockResolvedValue([])
    await listAvailableDeliveries()
    expect(http.get).toHaveBeenCalledWith('/riders/available', { silent: true })
  })

  it('lists the signed-in rider deliveries', async () => {
    http.get.mockResolvedValue([])
    await listMyDeliveries()
    expect(http.get).toHaveBeenCalledWith('/riders/deliveries', { silent: true })
  })

  it('accepts a delivery by order id', async () => {
    http.post.mockResolvedValue({ orderId: 7, status: 'Assigned' })
    await acceptDelivery(7)
    expect(http.post).toHaveBeenCalledWith('/deliveries/7/accept', null, { silent: true })
  })

  it('advances a delivery with an explicit action', async () => {
    http.put.mockResolvedValue({ orderId: 7, status: 'PickedUp' })
    await advanceDelivery(7, 'PickUp')
    expect(http.put).toHaveBeenCalledWith('/deliveries/7/status', { action: 'PickUp' }, { silent: true })
  })
})
