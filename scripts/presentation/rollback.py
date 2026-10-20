#!/usr/bin/env python3
"""Exercise application rollback with additive migrations; always attempt current restore.
The database is NOT silently rolled back. Retain both outcomes, including failures.
"""
import datetime
import fcntl
import json
from deploy import STATE, APPLICATION_SERVICES, compose, smoke, install_scaler, run


def infrastructure():
    output=run(['docker','ps','--filter','label=com.docker.compose.project=jikeda5001-cloud',
                '--format','{{.Label "com.docker.compose.service"}} {{.ID}}'])
    names={'foundation-db','grafana','prometheus','web','catalog-router'}
    return {name:identifier for line in output.splitlines() for name,identifier in [line.split()] if name in names}


def drill():
    current=json.loads((STATE/'current-release.json').read_text())
    previous=json.loads((STATE/'previous-release.json').read_text())
    result={'startedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
            'currentRevision':current['revision'],'previousRevision':previous['revision'],
            'rollbackSmokePassed':False,'currentRestoredAndVerified':False,
            'infrastructureBefore':infrastructure()}
    with open(STATE/'deployment.lock','a') as lock:
        fcntl.flock(lock,fcntl.LOCK_EX)
        run(['systemctl','stop','jikeda-autoscaler.service'])
        try:
            compose(previous,'up','-d','--no-build','--no-deps','--scale','catalog=1','--scale','fulfilment=1',
                    '--wait','--wait-timeout','180',*APPLICATION_SERVICES,timeout=300)
            smoke(previous)
            result['rollbackSmokePassed']=True
        except Exception as error:
            result['rollbackErrorType']=type(error).__name__
            raise
        finally:
            try:
                compose(current,'up','-d','--no-build','--no-deps','--scale','catalog=1','--scale','fulfilment=1',
                        '--wait','--wait-timeout','180',*APPLICATION_SERVICES,timeout=300)
                smoke(current)
                result['currentRestoredAndVerified']=True
            except Exception as error:
                result['restoreErrorType']=type(error).__name__
                raise
            finally:
                install_scaler(current)
                result['infrastructureAfter']=infrastructure()
                result['infrastructureUnchanged']=result['infrastructureBefore']==result['infrastructureAfter']
                result['finishedAtUtc']=datetime.datetime.now(datetime.timezone.utc).isoformat()
                (STATE/'rollback-drill.json').write_text(json.dumps(result,indent=2))
                if not result['infrastructureUnchanged']:
                    raise RuntimeError('Application rollback unexpectedly replaced infrastructure')
        print('ROLLBACK_DRILL_OK '+current['revision'])


if __name__=='__main__':
    drill()
