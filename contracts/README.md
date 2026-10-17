# Shared service contracts

This directory contains transport-level examples shared by the JiKeDa services.
It must not contain Entity Framework entities, DbContext classes, or shared business data models.

Current foundation contracts:

- `catalog-reservation.example.md`: request and response examples for Order-to-Catalog inventory reservation.
- `events/order-confirmed.v1.example.json`: the initial Order-to-Fulfilment event example.

During the course project, update the relevant example and `docs/api-contract.md` in the same pull request when an endpoint or event shape changes.
