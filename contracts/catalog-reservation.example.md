# Catalog inventory reservation

Implemented by the Catalog service (Member 3). Full rules: `docs/api-contract.md`, section "SWE5001 Catalog Service".

These endpoints are internal: the gateway does not route `/internal/*`. Inside the Compose network the
Order service calls `http://catalog:8080` and forwards the customer's `Authorization: Bearer` token.

## Reserve inventory

```http
POST /internal/catalog/reservations
Authorization: Bearer <token>
Content-Type: application/json
```

```json
{
  "reservationId": "checkout-001",
  "items": [
    { "dishId": 1, "quantity": 2 }
  ]
}
```

`201 Created` (first call) or `200 OK` with `"replayed": true` (same id and items again; nothing is deducted twice):

```json
{
  "success": true,
  "data": {
    "reservationId": "checkout-001",
    "items": [
      {
        "dishId": 1,
        "merchantId": 1,
        "dishName": "Braised Noodles",
        "unitPrice": 12.00,
        "quantity": 2,
        "lineTotal": 24.00
      }
    ],
    "totalAmount": 24.00,
    "replayed": false
  },
  "message": null,
  "code": null
}
```

Failures use `success: false` with a `code`: `DISH_NOT_FOUND` (404), `INSUFFICIENT_STOCK` (400),
`UNAVAILABLE` (400), `RESERVATION_CONFLICT` (409, same id with different items or already released),
`INVALID_REQUEST` (400). Nothing is reserved when any line fails.

## Release inventory

```http
POST /internal/catalog/reservations/checkout-001/release
POST /internal/catalog/reservations/checkout-001/release?merchantId=1
Authorization: Bearer <token>
```

```json
{
  "success": true,
  "data": { "reservationId": "checkout-001", "releasedLines": 1, "alreadyReleased": false },
  "message": null,
  "code": null
}
```

The same `reservationId` never deducts or restores inventory more than once. `?merchantId=` releases
only that merchant's lines (merchant rejection). Unknown id: `404 RESERVATION_NOT_FOUND`.
