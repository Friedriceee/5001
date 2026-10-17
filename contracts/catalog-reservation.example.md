# Catalog inventory reservation example

The Catalog implementation will own the final validation rules. This foundation records only the fields required for Member 3 and Member 4 to start compatible work.

## Reserve inventory

```http
POST /api/catalog/reservations
Content-Type: application/json
```

```json
{
  "reservationId": "checkout-001",
  "items": [
    {
      "dishId": 10,
      "quantity": 2
    }
  ]
}
```

Successful response:

```json
{
  "success": true,
  "data": {
    "reservationId": "checkout-001",
    "totalAmount": 25.00
  },
  "message": null
}
```

## Release inventory

```http
POST /api/catalog/reservations/checkout-001/release
```

The same `reservationId` must not deduct or restore inventory more than once.
