#!/usr/bin/env python3
"""Print a credential-free Azure Run Command bootstrap from this checked-out source."""
import argparse
import base64
from pathlib import Path
import re


def bootstrap(revision, image_tag, operation="deploy"):
    if not re.fullmatch('[a-f0-9]{40}', revision) or not re.fullmatch('[a-f0-9]{40}-[0-9]+', image_tag):
        raise ValueError('Invalid revision or image tag')
    if operation == 'rollback':
        program = f'/opt/jikeda/releases/presentation-{image_tag}/scripts/presentation/rollback.py'
        return f"""#!/usr/bin/env bash
set -euo pipefail
umask 077
python3 - <<'ROLLBACK'
import json, subprocess
from pathlib import Path
state=Path('/opt/jikeda/shared')
current=json.loads((state/'current-release.json').read_text())
assert current['revision']=='{revision}' and current['imageTag']=='{image_tag}'
log=state/'rollback-{image_tag}.log'
with log.open('w') as output:
    result=subprocess.run(['python3','{program}'],stdout=output,stderr=subprocess.STDOUT)
if result.returncode:
    print('ROLLBACK_DRILL_FAILED: inspect the protected server rollback log')
    raise SystemExit(result.returncode)
for line in log.read_text().splitlines():
    if line.startswith('ROLLBACK_DRILL_OK '): print(line)
ROLLBACK
"""
    encoded = base64.b64encode(Path(__file__).with_name('deploy.py').read_bytes()).decode()
    return f'''#!/usr/bin/env bash
set -euo pipefail
umask 077
python3 - <<'BOOTSTRAP'
import base64, os, subprocess
from pathlib import Path
os.umask(0o077)
operations = Path('/opt/jikeda/operations/presentation')
operations.mkdir(parents=True, exist_ok=True)
program = operations / 'bootstrap-deploy.py'
program.write_bytes(base64.b64decode('{encoded}'))
log = Path('/opt/jikeda/shared/deployment-{image_tag}.log')
with log.open('w') as output:
    result = subprocess.run(['python3', str(program), '--revision', '{revision}', '--image-tag', '{image_tag}'], stdout=output, stderr=subprocess.STDOUT)
if result.returncode:
    print('DEPLOYMENT_FAILED: inspect the protected server deployment log')
    raise SystemExit(result.returncode)
for line in log.read_text().splitlines():
    if line.startswith('DEPLOYMENT_OK '): print(line)
BOOTSTRAP
'''


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--operation', choices=['deploy', 'rollback'], default='deploy')
    parser.add_argument('--revision', required=True)
    parser.add_argument('--image-tag', required=True)
    args = parser.parse_args()
    print(bootstrap(args.revision, args.image_tag, args.operation))
