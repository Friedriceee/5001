#!/usr/bin/env bash
# Run as root through Azure Run Command, after cloning the pinned public source
# and copying deploy/budget-azure and scripts/member1 into that checkout.
set -euo pipefail
umask 077
cd /opt/jikeda/source
state=/opt/jikeda/shared
install -d -m 0700 "$state"
# Only public Git source and non-secret deployment files live in this checkout.
# A root bootstrap with umask 077 otherwise produces unreadable appsettings files
# after COPY into the non-root application images. Secrets stay outside the tree.
chmod -R u=rwX,go=rX /opt/jikeda/source
if [ ! -f "$state/runtime.env" ]; then
  python3 - "$state/runtime.env" <<'PY'
import os, secrets, sys
values = {key: secrets.token_hex(32) for key in (
    'DB_ADMIN_PASSWORD', 'IDENTITY_DB_PASSWORD', 'CATALOG_DB_PASSWORD',
    'ORDER_DB_PASSWORD', 'FULFILMENT_DB_PASSWORD', 'JWT_KEY', 'DEMO_PASSWORD')}
values['SOURCE_REVISION'] = os.environ['SOURCE_REVISION']
values['PUBLIC_HOST'] = os.environ['PUBLIC_HOST']
with open(sys.argv[1], 'x') as f:
    for key, value in values.items():
        f.write(f'{key}={value}\n')
os.chmod(sys.argv[1], 0o600)
PY
fi
compose=(docker compose --env-file "$state/runtime.env" -f deploy/budget-azure/compose.yml)
# Serial builds limit the SDK memory peaks on the 4 GiB validation host.
export COMPOSE_PARALLEL_LIMIT=1
echo 'Building application images serially.'
"${compose[@]}" build > "$state/build.log" 2>&1
echo 'Starting application and waiting for health checks.'
"${compose[@]}" up -d --wait --wait-timeout 240 > "$state/start.log" 2>&1
echo 'Rotating seed passwords.'
python3 scripts/member1/prepare-cloud-demo.py
set -a
source "$state/runtime.env"
set +a
catalog_port=$("${compose[@]}" port catalog 8080 | head -n1)
order_port=$("${compose[@]}" port order 8080 | head -n1)
echo 'Running the business smoke on the Azure host.'
docker run --rm --network host \
  --mount "type=bind,src=$PWD,dst=/source,readonly" \
  -e JIKEDA_GATEWAY_URL=http://127.0.0.1:5000 \
  -e "JIKEDA_CATALOG_URL=http://$catalog_port" \
  -e "JIKEDA_ORDER_URL=http://$order_port" \
  -e JIKEDA_SMOKE_PASSWORD="$DEMO_PASSWORD" \
  node:22-alpine node /source/scripts/smoke/foundation-smoke.mjs \
  > "$state/cloud-smoke.log" 2>&1
"${compose[@]}" ps --format json > "$state/containers.json"
docker stats --no-stream --format '{{.Name}} {{.MemUsage}}' > "$state/memory.txt"
printf 'Cloud smoke passed. Source: %s\n' "$SOURCE_REVISION"
cat "$state/cloud-smoke.log"
