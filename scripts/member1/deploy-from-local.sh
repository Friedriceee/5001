#!/usr/bin/env bash
# Usage: bash scripts/member1/deploy-from-local.sh RESOURCE_GROUP VM_NAME SOURCE_SHA PUBLIC_HOST
# The source repository is public. This transfers only the listed deployment files,
# never local Azure credentials, SSH keys or runtime.env.
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$root"
if [ "$#" -ne 4 ]; then
  echo 'Usage: deploy-from-local.sh RESOURCE_GROUP VM_NAME SOURCE_SHA PUBLIC_HOST' >&2
  exit 2
fi
umask 077
mkdir -p .cloud.local
chmod 700 .cloud.local
python3 - "$3" "$4" <<'PY'
import base64
import io
import re
import sys
import tarfile
from pathlib import Path

revision, host = sys.argv[1:]
if not re.fullmatch(r'[0-9a-f]{40}', revision):
    raise SystemExit('Source revision must be a full immutable Git SHA.')
if not re.fullmatch(r'[a-z0-9][a-z0-9.-]*[a-z0-9]', host):
    raise SystemExit('Supply the Azure deployment publicHostname output.')
files = [
    'deploy/budget-azure/compose.yml', 'deploy/budget-azure/Caddyfile',
    'deploy/budget-azure/frontend.Dockerfile', 'deploy/budget-azure/init-databases.sh',
    'scripts/member1/cloud-deploy.sh', 'scripts/member1/prepare-cloud-demo.py',
    'scripts/member1/check-cloud-isolation.py',
]
data = io.BytesIO()
with tarfile.open(fileobj=data, mode='w:gz') as archive:
    for file in files:
        archive.add(file, arcname=file)
payload = base64.b64encode(data.getvalue()).decode()
script = '''#!/usr/bin/env bash
set -euo pipefail
umask 077
cloud-init status --wait >/dev/null
install -d -m 0700 /opt/jikeda/shared
if [ ! -d /opt/jikeda/source/.git ]; then
  install -d /opt/jikeda/source
  git -C /opt/jikeda/source init -q
  git -C /opt/jikeda/source remote add origin https://github.com/Friedriceee/5001.git
fi
cd /opt/jikeda/source
# Refuse to overwrite an existing deployment of a different source revision.
existing=$(git rev-parse HEAD 2>/dev/null || true)
if [ -n "$existing" ] && [ "$existing" != 'REVISION' ]; then
  echo 'A different release exists. Prepare a separate checkout and reviewed migration/rollback first.' >&2
  exit 1
fi
git fetch --depth 1 origin REVISION
git checkout -q --detach FETCH_HEAD
printf '%s' 'PAYLOAD' | base64 -d | tar -xz
export SOURCE_REVISION=REVISION
export PUBLIC_HOST=HOSTNAME
if [ -f /opt/jikeda/shared/deploy.pid ] && kill -0 "$(cat /opt/jikeda/shared/deploy.pid)" 2>/dev/null; then
  echo 'A deployment is still running.' >&2
  exit 1
fi
rm -f /opt/jikeda/shared/deploy.exit
nohup bash -c 'set +e; bash scripts/member1/cloud-deploy.sh; result=$?; echo "$result" > /opt/jikeda/shared/deploy.exit; exit "$result"' > /opt/jikeda/shared/deployment.log 2>&1 < /dev/null &
echo "$!" > /opt/jikeda/shared/deploy.pid
echo 'Deployment started; inspect /opt/jikeda/shared/deploy.exit and cloud-smoke.log before enabling ingress.'
'''.replace('REVISION', revision).replace('HOSTNAME', host).replace('PAYLOAD', payload)
Path('.cloud.local/start-cloud.sh').write_text(script)
PY
bash scripts/member1/az.sh vm run-command invoke \
  --resource-group "$1" --name "$2" --command-id RunShellScript \
  --scripts @.cloud.local/start-cloud.sh --query 'value[].message' -o json
