# Shared service contracts

This directory contains transport-level examples shared by the JiKeDa services.
It must not contain Entity Framework entities, DbContext classes, or shared business data models.

Current foundation contracts:

- `catalog-reservation.example.md`: Order-to-Catalog inventory reservation (implemented, internal endpoints).
- `events/order-confirmed.v1.example.json`: the initial Order-to-Fulfilment event example.

During the course project, update the relevant example and `docs/api-contract.md` in the same pull request when an endpoint or event shape changes.
