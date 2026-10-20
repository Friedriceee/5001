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

## SWE5001 microservices and cloud demo

The four services now contain the migrated business flow: Identity, Catalog, Order,
and Fulfilment/Delivery. YARP routes browser API requests, while each service owns
its PostgreSQL logical database and credentials. Local development retains the
HTTP event-feed fallback; the cloud presentation configuration uses Azure Service
Bus with a transactional outbox and idempotent inbox.

```sh
docker compose -f docker-compose.microservices.yml up -d --build --wait
node scripts/smoke/foundation-smoke.mjs
docker compose -f docker-compose.microservices.yml down
```

Local Gateway is `http://localhost:5000`; direct service ports are 5101–5104 and
PostgreSQL is 5433. These are local debugging ports. The cloud override exposes
only HTTPS/HTTP ingress, with operator service ports bound to loopback.

The budget deployment reuses one Azure VM, ACR, Service Bus and Application Insights.
A host controller scales Catalog on actual request rate and Fulfilment on actual
queue depth, independently from one to two replicas, with cooldown and scale-in.
Prometheus/Grafana display the real observations. This is container-level scaling
inside one host; ACA, managed PostgreSQL and cross-host high availability remain
outside this deployment.

- [Presentation and operator runbook](docs/presentation/5001-cloud-demo-runbook.md)
- Cloud Compose and dashboard: `deploy/presentation/`
- Release, rollback and live validation: `scripts/presentation/`
- The `CD` workflow builds/scans seven versioned ACR images and deploys the exact
  commit through OIDC scoped to the named demo VM. The previous monolith workflow
  is preserved as `Legacy monolith CD` for manual use.
- Keep runtime passwords and Azure credentials outside Git. The checked-in Azure
  target contains resource IDs and names only.

The commands below describe the original monolith for compatibility. Use the
microservices configuration above for the SWE5001 cloud presentation.

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
| `cd.yml` | after green CI on `main` or manual feature release: build seven microservice/database images, Trivy gate, push ACR, exact-commit cloud deployment, health/business checks and application rollback |
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
