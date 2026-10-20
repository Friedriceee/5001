#!/usr/bin/env python3
"""Exercise an application rollback against additive, backward-compatible migrations.
The database is NOT silently rolled back. Always restore the current release afterwards.
"""
import datetime
import fcntl
import json
from pathlib import Path
from deploy import STATE, compose, smoke, install_scaler, run


def drill():
    current = json.loads((STATE / 'current-release.json').read_text())
    previous = json.loads((STATE / 'previous-release.json').read_text())
    result = {'startedAtUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
              'currentRevision': current['revision'], 'previousRevision': previous['revision']}
    with open(STATE / 'deployment.lock', 'a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        run(['systemctl', 'stop', 'jikeda-autoscaler.service'])
        try:
            compose(previous, 'up', '-d', '--no-build', '--scale', 'catalog=1', '--scale', 'fulfilment=1',
                    '--remove-orphans', '--wait', '--wait-timeout', '180', timeout=300)
            smoke(previous)
            result['rollbackSmokePassed'] = True
        finally:
            compose(current, 'up', '-d', '--no-build', '--scale', 'catalog=1', '--scale', 'fulfilment=1',
                    '--wait', '--wait-timeout', '180', timeout=300)
            smoke(current)
            install_scaler(current)
            result['currentRestoredAndVerified'] = True
            result['finishedAtUtc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
            (STATE / 'rollback-drill.json').write_text(json.dumps(result, indent=2))
        print('ROLLBACK_DRILL_OK ' + current['revision'])


if __name__ == '__main__':
    drill()
