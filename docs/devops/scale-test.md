# Scale-out and consistency test

Script: [`scripts/load/k6-platform.js`](../../scripts/load/k6-platform.js) (k6). Runner: [`scripts/load/run-scale-test.sh`](../../scripts/load/run-scale-test.sh). Workflow: `.github/workflows/scale-test.yml` (manual).

## What it shows

1. **Capacity grows with replicas.** The stack starts with one API replica, k6 runs the load scenario, the API is scaled to N replicas (`docker compose up --scale api=N`) and k6 runs again. The frontend's nginx re-resolves the `api` service name every few seconds, so traffic spreads over all replicas without a configuration change. The run records how many replicas answered (`X-Instance` header), requests per second, p95 latency and error rate for each replica count.
2. **Scale-in.** The API is scaled back to one replica and must keep answering.
3. **Consistency under a race.** Thirty customers each send the same checkout request twice, at once, for a dish with 20 units. Required: exactly 20 orders, every duplicate answered with the stored order, the rest rejected as sold out, stock exactly 0, no server error.

## Design choices you should know about

- Each replica is capped at **1 vCPU and 768 MB** in `docker-compose.scale.yml`, the way a managed container platform sizes a replica. Without a cap one container already uses every core of the host, and replicas on a single machine would show nothing.
- The load is **80% sign-in** (password hashing is CPU-heavy) and 20% browsing. Browsing alone is too cheap to saturate an API.
- Everything runs on one machine, including PostgreSQL, nginx and k6, so absolute numbers are modest and the scaling factor is limited by the host. The relative change is the evidence.
- This is **manual scaling**, not auto-scaling. A managed platform would add replicas from a rule; the API is stateless, so the same rule would work.
- The first replica runs migrations and seeds demo data; further replicas are added afterwards. EF Core 8 migrations are not safe to run from several replicas at the same moment (limitation AISS-02 in the report).

## Run it

```bash
REPLICAS="1 3" VUS=40 DURATION=45s bash scripts/load/run-scale-test.sh
```

Or run the **Scale test** workflow in GitHub Actions; the summary appears on the run page and `scale-test-results` holds the raw k6 output.

Each scenario can also be run alone against any running stack:

```bash
BASE_URL=http://localhost:8080 PASSWORD='DemoPass123!' SCENARIO=consistency k6 run scripts/load/k6-platform.js
BASE_URL=http://localhost:8080 PASSWORD='DemoPass123!' SCENARIO=load VUS=40 DURATION=30s k6 run scripts/load/k6-platform.js
```
