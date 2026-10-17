# JiKeDa Campus Food Service Platform

Team 3 project for the practice module of the Graduate Certificate in Architecting Scalable Systems (SWE5001 / SE34FT).

JiKeDa lets customers, merchants, riders and administrators work on the same order, from menu and checkout to delivery, with prices, stock and payment kept consistent while many people order at once.

## Where to find the deliverables

| Deliverable | Location |
|---|---|
| Working system | `backend/` (API), `frontend/` (web app), run with Docker Compose, see below |
| Code base | this repository |
| Project report | [docs/report/project-report.md](docs/report/project-report.md) |
| DevSecOps pipeline scripts | [.github/workflows/](.github/workflows/), [scripts/deploy/](scripts/deploy/) |
| Test scripts | `backend/tests/`, `frontend/src/**/__tests__/`, [scripts/smoke/](scripts/smoke/), [scripts/load/](scripts/load/) |
| Presentation | [docs/presentation/](docs/presentation/) |
| Proposal | [docs/proposal/](docs/proposal/) |

The full index is in [docs/README.md](docs/README.md).

## Architecture in one picture

```
Browser ─► nginx (serves the Vue app, proxies /api) ─► ASP.NET Core 8 API (1..N replicas) ─► PostgreSQL 16
                                                              └─► payment gateway (simulated, behind an interface)
```

The API is one deployable made of feature modules (identity, catalog, cart, pricing, checkout, inventory, orders, rider dispatch, admin and audit). It keeps no session, so replicas can be added behind the proxy. The proposal describes four services on Azure; what was built differs, and [the project report](docs/report/project-report.md) states how and why.

## Run it

Needs Docker with Compose v2.

```bash
docker compose --profile app up -d --build --wait    # PostgreSQL + API + web app
# app: http://localhost:8080   API: http://localhost:5080
# demo accounts: demo_customer / demo_merchant / demo_rider   password: SEED_PASSWORD (default DemoPass123!)

BASE_URL=http://localhost:8080 SMOKE_PASSWORD='DemoPass123!' node scripts/smoke/staging-smoke.mjs
```

The smoke script walks one order from checkout and simulated payment through merchant fulfilment and rider delivery (19 steps). The administrator account has no default password; see [docs/devops/staging-runbook.md](docs/devops/staging-runbook.md).

### Develop without containers

```bash
docker compose up -d                                         # PostgreSQL only
cd backend/src/TakeoutPlatform.Api
cp appsettings.Development.json.example appsettings.Development.json
dotnet run                                                   # http://localhost:5080/swagger
cd ../../../frontend && npm ci && npm run dev                # http://localhost:5173
```

Backend tests: `dotnet test backend`. Set `JIKEDA_TEST_POSTGRES` to a PostgreSQL connection string to include the integration tests that need a real database. Frontend tests: `npm test` in `frontend/`.

## Scalability evidence

```bash
REPLICAS="1 3" VUS=40 DURATION=45s bash scripts/load/run-scale-test.sh   # needs Docker, k6, Node 20
```

The script starts the stack, compares one API replica with several behind nginx under a k6 load, runs a consistency race (many customers, one scarce dish, duplicate requests) and writes `load-results/summary.md`. The **Scale test** workflow runs it in GitHub Actions. See [docs/devops/scale-test.md](docs/devops/scale-test.md).

## Pipeline

| Workflow | What it does |
|---|---|
| `ci.yml` | backend tests (with PostgreSQL), frontend tests and build, dependency scan, secret scan, container stack smoke test |
| `codeql.yml` | static analysis for C# and JavaScript |
| `cd.yml` | after green CI on `main`: build images, Trivy scan, push to GHCR, deploy to staging, health check, smoke test, automatic rollback |
| `scale-test.yml` | manual: 1 vs N replicas under k6 load and the consistency race |

Branching, commit rules and review: [CONTRIBUTING.md](CONTRIBUTING.md). Pipeline design: [docs/devops/pipeline-design.md](docs/devops/pipeline-design.md).

## Repository layout

```
backend/src/TakeoutPlatform.Api/   API, one folder per feature under Features/
backend/tests/                     NUnit tests (SQLite in memory, plus PostgreSQL integration tests)
frontend/                          Vue 3 web app, one area per role
scripts/                           smoke test, load tests, deploy script
docs/                              report, design, DevOps, proposal, presentation
docker-compose.yml                 local stack; docker-compose.scale.yml adds replicas
docker-compose.staging.yml         release stack used by CD
```
