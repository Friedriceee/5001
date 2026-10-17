#!/usr/bin/env bash
# Measures what adding API replicas does, and checks consistency under a race.
#
#   REPLICAS="1 3" VUS=40 DURATION=45s bash scripts/load/run-scale-test.sh
#
# Needs Docker with Compose v2.24+, k6 and Node 20. The stack is built and started here.
# Results land in ./load-results (JSON per run and summary.md); in GitHub Actions the summary is
# also written to the job summary.
set -euo pipefail

cd "$(dirname "$0")/../.."
REPLICAS="${REPLICAS:-1 3}"
VUS="${VUS:-40}"
DURATION="${DURATION:-45s}"
OUT="load-results"
COMPOSE=(docker compose -f docker-compose.yml -f docker-compose.scale.yml --profile app)

mkdir -p "$OUT"
: > "$OUT/replicas.log"

if [ ! -f .env ]; then
  echo "JWT_KEY=$(openssl rand -hex 32)" >> .env
  echo "SEED_PASSWORD=Load-$(openssl rand -hex 8)" >> .env
fi
set -a; . ./.env; set +a
BASE_URL="http://localhost:8080"

note() { printf '%s  %s\n' "$(date -u +%H:%M:%S)" "$*" | tee -a "$OUT/replicas.log"; }
running() { "${COMPOSE[@]}" ps --status running --format '{{.Service}}' | grep -c '^api$' || true; }

scale_to() {
  local count="$1"
  note "scaling api to $count replica(s)"
  "${COMPOSE[@]}" up -d --wait --wait-timeout 300 --no-recreate --scale "api=$count"
  note "api replicas running: $(running)"
  # nginx re-resolves the service name every few seconds.
  sleep 12
}

spread() {
  # Which replica answered each of 60 requests, as seen through nginx.
  local file="$1"
  for _ in $(seq 1 60); do
    curl -s -D - -o /dev/null "$BASE_URL/api/health" | tr -d '\r' | awk -F': ' 'tolower($1)=="x-instance"{print $2}'
  done | sort | uniq -c | sort -rn > "$file"
  note "request spread over replicas: $(wc -l < "$file" | tr -d ' ') replica(s) answered"
}

cleanup() { "${COMPOSE[@]}" logs --no-color --tail=100 api > "$OUT/api.log" 2>&1 || true; "${COMPOSE[@]}" down -v >/dev/null 2>&1 || true; }
trap cleanup EXIT

note "starting the stack with one API replica"
"${COMPOSE[@]}" up -d --build --wait --wait-timeout 300
note "api replicas running: $(running)"
sleep 5

last=1
for count in $REPLICAS; do
  scale_to "$count"
  last="$count"
  spread "$OUT/spread-$count.txt"
  note "k6 load run: $count replica(s), $VUS virtual users, $DURATION"
  BASE_URL="$BASE_URL" PASSWORD="$SEED_PASSWORD" SCENARIO=load VUS="$VUS" DURATION="$DURATION" \
    k6 run --quiet --summary-export="$OUT/load-$count.json" scripts/load/k6-platform.js | tee "$OUT/load-$count.txt"
done

note "k6 consistency run at $last replica(s)"
BASE_URL="$BASE_URL" PASSWORD="$SEED_PASSWORD" SCENARIO=consistency \
  k6 run --quiet --summary-export="$OUT/consistency.json" scripts/load/k6-platform.js | tee "$OUT/consistency.txt"

# Scale in again and confirm the platform keeps answering.
scale_to 1
curl -fsS "$BASE_URL/api/health" > /dev/null && note "scale-in: still healthy with $(running) replica"

node scripts/load/summarize.mjs "$OUT" "$REPLICAS" | tee "$OUT/summary.md"
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then cat "$OUT/summary.md" >> "$GITHUB_STEP_SUMMARY"; fi
