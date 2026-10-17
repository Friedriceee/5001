# Project Report: JiKeDa Campus Food Service Platform

Practice Module for Graduate Certificate in Architecting Scalable Systems

**Team 3**
Members: Wang Qinyang, Hao Mingyang, Zhao Haotian, Liu Tongyu, Tang Jinfan

> Sections follow the report template. Items marked **TEAM** need data only the team has (hours, final decisions) and are listed at the end.

---

## 1. Introduction

### 1.1 Background

Campus food service involves four parties: customers who order, merchants who prepare, riders who deliver, and administrators who oversee. JiKeDa is a platform on which all four work on the same order, from menu and checkout to delivery. The proposal (see [docs/proposal](../proposal/)) set out a cloud-native design of four services on Azure. This report describes what was built and where it differs from the proposal (sections 2.2 and 3.4).

### 1.2 Business Needs

- One consistent view of menus, prices, stock, orders and delivery status for all roles.
- No conflicting stock values, double charges or lost orders when many people order at once.
- A platform other applications can use: documented APIs for identity, catalog, ordering and fulfilment.
- A repeatable way to build, test, scan, release and roll back.

### 1.3 Stakeholders

| Stakeholder | Interest |
|---|---|
| Customers | Browse, pay and follow an order |
| Merchants | Publish menu, stock and offers; fulfil orders |
| Riders | Find and complete deliveries |
| Administrators | Manage accounts and coupons; review orders and audit records |
| Platform operators (the team) | Build, release, observe and roll back |
| Lecturers | Assess architecture, scalability evidence and DevSecOps practice |

### 1.4 Project Scope

#### 1.4.1 Functionality in scope (implemented)

- Registration and sign-in for customers, merchants and riders; role-based access with JWT. Built-in administrator without a default password.
- Merchants: dishes (standard, bundle, promotional), stock, special offers.
- Customers: browse, cart, server-side pricing, coupons, addresses, checkout with simulated payment, order history.
- Merchant fulfilment (accept, prepare, ready) and rider dispatch (claim, pick up, deliver, complete).
- Administrator: accounts and roles, platform coupons and their allocation to customers, order search, audit log.
- Pipeline: CI with six gates, container build and scan, staging deployment, health check, smoke test, automatic rollback, and a scale-out and consistency test.

#### 1.4.2 Functionality out of scope

- Real payment settlement and refunds, native mobile apps, real-time GPS navigation, recommendations, multi-region deployment (as in the proposal).
- Architected in the proposal but **not implemented**: separate Identity, Catalog, Order and Fulfilment services, an API gateway (YARP), Azure Service Bus events with an outbox, one database schema per service, deployment to Azure Container Apps with auto-scaling rules, Bicep templates, Application Insights, and the campus menu display. See 3.4.

---

## 2. Project Conduct

### 2.1 Project Plan

Planned effort: 5 members, about 10 person-days each (50 person-days), from the proposal.

| WBS | Work package | Planned days | Outcome |
|---|---|---|---|
| 1 | Requirements and platform modelling | 3 | Done: roles, user stories, acceptance criteria |
| 2 | Service and data design | 7 | Done for one modular API; service split not carried out |
| 3 | Core services | 14 | Done as modules of one API, with unit, endpoint and PostgreSQL tests |
| 4 | Applications, gateway and integration | 7 | Web app for four roles done; nginx edge instead of a gateway; no menu display |
| 5 | Cloud platform and messaging | 7 | Not done: Docker Compose staging instead of Azure; no message broker |
| 6 | CI/CD and DevSecOps | 5 | Done: pipeline, scans, deploy, rollback |
| 7 | Scalability and resilience validation | 4 | Done on Compose: replicas under k6 load, consistency race; no auto-scaling |
| 8 | Documentation and presentation | 3 | In progress |

### 2.2 Project Status

The complete order flow works end to end for all four roles and is verified by a 19-step smoke test that runs in containers on every change. Backend: 289 tests (with PostgreSQL), frontend: 126 tests.

Outstanding issues are listed in section 3.4. The largest gap against the proposal is the architecture: one modular API deployed as containers, instead of four services on Azure.

### 2.3 Project Metrics

Milestones (sprints): 1 foundation and merchant catalog; 2 identity and cart; 3 pricing, checkout and payment, inventory; 4 admin, coupon allocation, rider dispatch, containers, CD and scale test.

Commits on `main` by author (a proxy, not effort): Lil-Draky 18, Hao Mingyang 17, Zhao Haotian 16, Wang Qinyang 11, Liu Tongyu 4 (name variants merged; counts are from the previous repository and are not carried into this one).

Effort expended per member: **TEAM** to fill in (hours or person-days).

---

## 3. Solution Overview

### 3.1 Logical Architecture & Design

```
 Vue 3 web app (customer · merchant · rider · admin portals)
        │ REST/JSON + JWT
        ▼
 ASP.NET Core 8 API ── cross-cutting: authentication, role checks, ApiResult wrapper, exception filter, audit
   Auth · Catalog · Cart · Pricing · Checkout · Inventory · Orders · Rider dispatch · Admin and audit
        │ EF Core                              │ IPaymentGateway
        ▼                                      ▼
   PostgreSQL 16                        payment provider (simulated)
```

#### 3.1.1 Key Architectural Decisions

| Id | Description |
|---|---|
| AD-01 | **Modular monolith, one folder per feature, instead of four services.** Checkout must change cart, stock, coupons, the order and the payment together. In one process that is one database transaction with full rollback; across services it needs an outbox, compensation and idempotent consumers. For a five-person team and a short project the single transaction was chosen. Trade-off: scales as one unit; module boundaries are kept (interfaces such as `PricingService`, `InventoryService`) so a split stays possible. This departs from the proposal. |
| AD-02 | **The server is the authority.** Prices, totals and identity come from the database and the JWT, never from the request body. Trade-off: one cart read and a price calculation per checkout. |
| AD-03 | **One login and token for all roles, role checks on every controller.** The token carries role and profile id. Trade-off: no server session to store or scale; a token stays valid until it expires. |
| AD-04 | **Interfaces where change is expected:** `IPaymentGateway`, `IDiscountStrategy`, the menu item factory, rider assignment handlers. Trade-off: some indirection. |
| AD-05 | **Uniform response and error shape** (`ApiResult`, exception filter on the server, interceptor in the web app). |

#### 3.1.2 Tiers and Layers

Presentation (Vue 3, Element Plus, Pinia, Vue Router) → API layer (controllers: routing, validation, role checks) → business layer (services and strategies: pricing, checkout, inventory, dispatch) → data layer (EF Core, PostgreSQL, migrations).

#### 3.1.3 Nodes and Subsystems

| Subsystem | Responsibility |
|---|---|
| Auth | Accounts, registration, sign-in, token issue, roles |
| Catalog | Merchants, dishes (standard, bundle, promotional), special offers |
| Cart | Per-customer cart, server-priced snapshot |
| Pricing | Merchant offers and platform coupons as discount strategies; per-customer coupon allocation |
| Checkout | Idempotent, transactional order creation and payment |
| Inventory | Reservation ledger; no negative stock |
| Orders and fulfilment | Order lifecycle, merchant preparation |
| Rider dispatch | Eligibility chain, claim, pick-up, delivery |
| Admin and audit | Accounts, roles, coupons, order search, audit log |

#### 3.1.4 Platform Design

Platform roles (from the proposal): **seed** is the core platform (identity, catalog, ordering, fulfilment with shared rules and APIs); **producers** are merchants and administrators, who publish menus, prices, stock, promotions and coupons; **consumers** are customers, riders and administrators; **operators** are the team.

Domain-driven design: bounded contexts are Identity and access, Catalog, Ordering (cart, order, payment), Pricing and promotion, Inventory, Fulfilment, plus Admin and audit across them. Aggregates include Order (with its lines, coupon and payment), CustomerCoupon (issued and used quantity) and InventoryReservation. Contexts meet through narrow contracts: `quote` (cart to pricing), `reserve` (checkout to inventory), `charge` (checkout to the payment gateway, an anti-corruption boundary). Producers act through the merchant and admin APIs; consumers through customer and rider APIs and the order status each role may change. Contexts are modules in one deployable and call each other in process; there is no event bus.

### 3.2 Physical Architecture & Design

```
 Browser ─► frontend container (nginx, unprivileged) ─► api container(s) (ASP.NET Core 8) ─► db container (PostgreSQL 16, volume)
                 serves the web app, proxies /api          1..N replicas, stateless
 GitHub Actions ─► builds, scans (Trivy), pushes ─► GHCR (images tagged sha-<commit>) ─► staging host pulls and runs
```

#### 3.2.1 Key Architectural Decisions

| Id | Description |
|---|---|
| AD-06 | **Containers on Docker Compose instead of Azure Container Apps.** One definition runs locally, in CI and in staging. Trade-off: no managed auto-scaling, no managed ingress; the same images could run on a managed platform. This departs from the proposal. |
| AD-07 | **nginx as the single entry point**, serving the web app and proxying `/api`, re-resolving the `api` service name so replicas can be added or removed without configuration change. |
| AD-08 | **PostgreSQL as the last line of defence:** unique and CHECK constraints stop races that code misses (see 3.2.3). |
| AD-09 | **Immutable images tagged by commit; moving tags `staging` and `staging-previous`** make rollback a re-deployment of a known image. |
| AD-10 | **Stateless API:** identity is in the token, no in-memory session, so replicas are interchangeable. |

#### 3.2.2 Technology and Services

| Layer | Choice | Rationale |
|---|---|---|
| Web app | Vue 3, Vite, Element Plus, Pinia | Team skills, fast build, Vitest |
| API | .NET 8 (LTS), EF Core 8 | Long-term support, migrations |
| Database | PostgreSQL 16 | Real constraints and transactions, easy to containerise |
| Edge | nginx (unprivileged) | Static files, same-origin API |
| Packaging | Docker, Docker Compose | One artifact for development, CI and staging |
| Pipeline | GitHub Actions, GHCR, Trivy, CodeQL, gitleaks, Dependabot | Integrated with the repository |
| Load testing | k6 | Scripted, thresholds usable as a pipeline gate |

No cloud services are used in the implemented system (see AISS-01).

#### 3.2.3 Persistence Design

Data is relational (19 tables), created from empty by EF Core migrations.

| Data group | Tables | Notes |
|---|---|---|
| Identity | Accounts, Users, UserAddresses, Riders | Passwords stored as salted hashes; the built-in administrator has no usable password until one is configured |
| Catalog | Merchants, Dishes, SpecialOffers | Money is `decimal`, rounded in one place |
| Ordering | CartItems, Orders, OrderDishes, OrderUsers, OrderRiders, Payments | Order lines keep the price paid (snapshot) |
| Promotion | PlatformCoupons, CustomerCoupons, OrderCoupons | Per-customer issued and used quantities |
| Inventory | InventoryReservations | Ledger per order; repeated release does not restore twice |
| Fulfilment | MerchantOrderFulfillments, RiderDeliveries | One rider per order |
| Governance | AuditLogs | Sensitive operations |

Transaction and concurrency rules enforced by the database: unique (UserId, IdempotencyKey) on Payments (one charge per request); unique OrderId on Payments and RiderDeliveries; CHECK inventory >= 0 and reservation quantity > 0 (no overselling); CHECK used <= issued on customer coupons; unique usernames, coupon codes and (Role, ProfileId). Rider claims use a serializable transaction.

Security of data: secrets (JWT key, database password, seed password, administrator password) are injected per environment and never committed; gitleaks scans the history. Trade-off considered: one database instance keeps transactions simple and cheap; it is a single point of failure and the scaling limit (AISS-05).

#### 3.2.4 Detailed Design: checkout (architecturally representative use case)

`POST /api/checkout` with an `Idempotency-Key`, inside one database transaction:

1. Verify the address belongs to the customer and read the cart.
2. Price the cart on the server (`PricingService`: merchant offer or platform coupon).
3. Reject if the cart changed since pricing.
4. Claim the cart rows with an atomic delete; a concurrent checkout of the same cart affects no rows and is refused.
5. Create the order, order lines and rider fee; consume one coupon allocation.
6. Reserve stock (`InventoryService`); fail if any unit is short.
7. Charge through `IPaymentGateway`.
8. Mark the order Paid, store the payment with the idempotency key, commit.

Any failure or a declined payment rolls everything back: cart, stock and coupon are exactly as before (HTTP 402 or 400). A repeated request with the same key returns the stored order (HTTP 200, `replayed: true`) without charging or deducting again; the same key with a different body is rejected.

| If this happens | The system does this | Checked by |
|---|---|---|
| Client sends another price or user id | Ignored; server prices, identity from the token | Smoke test, endpoint tests |
| Same request twice | Stored order returned; no second charge or stock change | Smoke test, endpoint tests, k6 consistency scenario |
| Two checkouts for one cart | Atomic cart claim; the loser conflicts or gets the winner's result | PostgreSQL parallel tests |
| Last unit wanted twice | Reservation fails; CHECK constraint behind it | Inventory tests, k6 consistency scenario |
| Payment declined | Full rollback | Smoke test, endpoint tests |

### 3.3 Other Architectural Decisions

| Id | Description |
|---|---|
| AD-11 | **Patterns:** Strategy (discount rules), Factory method (menu item types), Chain of responsibility (rider eligibility), Gateway (payment), Idempotent receiver (checkout). See [design-patterns](../architecture/design-patterns.md). |
| AD-12 | **API contract first:** each story starts in [api-contract.md](../api-contract.md); web app and API are developed in parallel against it. |
| AD-13 | **Migrations at start-up are opt-in** (`Database__MigrateOnStartup`), and demo data only outside production (`Seed__Enabled`). |

### 3.4 Architectural Limitation

| Id | Issue and impact | Description | Resolution | Owner | Status |
|---|---|---|---|---|---|
| AISS-01 | Not deployed to a cloud; no auto-scaling | Proposal targeted Azure Container Apps with scale rules. Staging is Docker Compose; replicas are scaled by hand. | Deploy the existing images to a managed container platform and add scale rules on concurrency or CPU | TEAM | Open |
| AISS-02 | Concurrent migrations | EF Core 8 migrations are not safe when several replicas start at once. | Run migrations as a separate job before rollout; add replicas only afterwards (as the scale test does) | TEAM | Open |
| AISS-03 | No login rate limit | Brute force is possible, more serious now that the administrator account can be enabled. | Rate limiting or lockout on `/api/auth/login` | TEAM | Open |
| AISS-04 | Legacy `POST /api/orders` | Accepts client prices and reserves stock for an order that is never paid. The web app no longer calls it. | Remove it | TEAM | Open |
| AISS-05 | Single database instance | One PostgreSQL is the scaling and availability limit. | Managed PostgreSQL with a replica; per-context schemas first | TEAM | Open |
| AISS-06 | In-process integration, no events | Fulfilment is triggered by status calls, not events; no async buffering of bursts. | Introduce a broker and an outbox once contexts are split | TEAM | Open |
| AISS-07 | Payment is simulated | `IPaymentGateway` has only a simulator. A real provider needs a pending state and a webhook. | Implement a provider adapter | TEAM | Open |
| AISS-08 | Language and seed data | About 64 server messages are in Chinese; demo merchants are seeded by migration in every environment. | Translate; move demo data to the seeder | TEAM | Open |

---

## 4. Quality Attributes

### 4.1 Performance

Server-side cart and price calculation with bounded queries and pagination on admin lists; the sign-in path uses a salted password hash, which is CPU-heavy by design. A k6 load scenario (see [scale-test](../devops/scale-test.md)) measures throughput and p95 latency per replica count. Measured results are in section 6: about 2.5 times the throughput with three replicas.

### 4.2 Availability

Health endpoints (`/api/health`, `/healthz`) and container health checks gate every deployment; a stateless API runs as several replicas behind nginx; the deployment verifies with a smoke test and rolls back automatically to `staging-previous`; a rollback drill can be run on demand. Limitation: one database instance (AISS-05).

### 4.3 Security

JWT authentication with role checks on every controller; server-side validation; the server never trusts price or identity from the client; idempotent, transactional checkout. No default administrator password (a regression test signs in with the old one and expects 401). Secrets are injected, not committed (gitleaks over the full history). Dependency scan, CodeQL, Trivy image scan before any image is published. Containers run as non-root with all capabilities dropped; no database port is published in staging. Open: login rate limiting (AISS-03).

### 4.4 Extensibility and Maintainability

Feature folders, interfaces at expected change points (new discount rule, menu item type, payment provider, dispatch rule), API contract first, migrations in source control, 289 backend and 126 frontend tests, small pull requests, conventional commits. A new use case on the platform (for example the campus menu display) reuses identity, catalog and the response shape.

---

## 5. DevOps and Development Lifecycle

### 5.1 Source Control Strategy

One repository (backend, frontend, scripts, docs). `main` is protected: changes come through pull requests with review and required checks, squash-merged. Short-lived `feat/`, `fix/`, `chore/`, `docs/` branches; conventional commit messages; Dependabot for dependencies. See [CONTRIBUTING.md](../../CONTRIBUTING.md).

### 5.2 Continuous Integration

On every pull request and push to `main` (`.github/workflows/ci.yml`, `codeql.yml`):

| Job | Content |
|---|---|
| backend | build, endpoint and unit tests on SQLite, integration tests on a real PostgreSQL 16 service (schema from the real migrations, parallel-transaction tests) |
| frontend | Vitest, production build |
| security | .NET and npm dependency vulnerability scan |
| secret-scan | gitleaks over the full history |
| stack | `docker compose` build, containers healthy, 19-step smoke test through nginx |
| codeql | static analysis for C# and JavaScript |

### 5.3 Continuous Delivery

After green CI on `main`, `cd.yml` builds both images, scans them with Trivy (a fixable high or critical finding stops the run before anything is pushed), pushes them to GHCR tagged `sha-<commit>`, deploys to staging (over SSH to a server when configured, otherwise a throw-away stack on the runner), runs health checks and the smoke test, and then moves the `staging` tag (keeping `staging-previous`). A failure after deployment redeploys the previous release, checks it, and still fails the run. A manual rollback drill rolls back, smoke-tests the old release, then rolls forward. One staging environment exists; promotion is automatic after the verification passes. See [pipeline-design](../devops/pipeline-design.md) and [staging-runbook](../devops/staging-runbook.md).

---

## 6. Scalability and Consistency Evidence

The **Scale test** workflow compares one API replica with several behind nginx, with k6 (80% sign-in, 20% browsing, each replica capped at 1 vCPU), then runs the consistency race. Method and caveats: [scale-test](../devops/scale-test.md).

Measured in GitHub Actions on 17 Oct 2026 (4 vCPU shared runner; PostgreSQL, nginx and k6 run on the same machine; 40 virtual users, 45 s per step):

| API replicas | Requests/s | Sign-in p95 (ms) | Browse p95 (ms) | Error rate | Replicas that answered |
|---|---|---|---|---|---|
| 1 | 11 | 7087 | 8399 | 0.00% | 1 |
| 2 | 22 | 3204 | 3342 | 0.00% | 2 |
| 3 | 29 | 3811 | 3997 | 0.15% | 3 |

Throughput rose x2.5 with three replicas and p95 latency roughly halved. A first run (1 and 3 replicas) gave 15 and 39 requests/s (x2.7), so the numbers vary between runs on shared hardware; the scaling trend is the evidence, not the absolute values. The gain from two to three replicas is smaller because the runner's four cores are shared with PostgreSQL, nginx and k6. A single replica saturates at about 11 requests/s because each is capped at one vCPU and sign-in hashing is CPU-bound. Scale-in to one replica left the platform healthy.

Consistency race (30 customers, each sending the same checkout twice at once, for a dish with 20 units): exactly 20 orders created, 20 duplicate requests answered with the stored order, 20 requests rejected as sold out, stock exactly 0, no server error, all invariants held.

Not demonstrated: auto-scaling (replicas are scaled by hand) and queue-based scaling (there is no message queue). See AISS-01 and AISS-06.

First continuous-delivery run on this repository: images built and scanned (Trivy), pushed to GHCR, deployed to a throw-away staging stack, health check and 19-step smoke test passed, tags promoted: build 83 s, deploy and verify 50 s. The first attempt was stopped by the image scan on fixable Debian findings (perl) in the base image; the runtime images now apply OS security updates, which shows the gate working.

## 7. Lessons Learned

- A green pipeline only proves what it tests. Two defects (a migration that never ran on a fresh database, and a committed default administrator password) were invisible while CI skipped the PostgreSQL tests; both are now covered by regression tests that fail on the old code.
- Keep authority on the server and let the database enforce what code assumes.
- Turn the demo into an automated test: the smoke test is the demo and the deployment gate.

---

### Open items for the team

1. Effort per member (section 2.3).
2. Owners and dates for the architectural limitations (section 3.4).
3. Re-run the scale test before submission if you want fresh numbers (section 6).
4. The team's own statement of why the architecture changed from the proposal (AD-01, AD-06).
5. Copy this content into the official report template (`.docx`) before submission on 16 Nov.
