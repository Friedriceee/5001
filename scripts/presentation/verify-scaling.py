#!/usr/bin/env python3
"""Operator-run real scaling checks on the existing demo VM; no public mutating API.
Run HTTP first; queue mode briefly pauses a consumer to accumulate a real backlog.
All checks restore paused consumers. Failures are retained rather than labelled passes.
"""
import argparse, collections, concurrent.futures, datetime, json, math
from pathlib import Path
import subprocess, threading, time, urllib.request
from deploy import STATE, compose, run


def status():
    with urllib.request.urlopen('http://172.17.0.1:9109/state',timeout=5) as response:
        return json.load(response)


def sample(rows):
    row=status(); rows.append(row)
    return row


def wait_for(rows,predicate,seconds):
    deadline=time.monotonic()+seconds
    while time.monotonic()<deadline:
        row=sample(rows)
        if predicate(row): return row
        time.sleep(5)
    raise RuntimeError('Expected scaling state was not observed')


def count_sql(database,sql):
    container=run(['docker','ps','-q','--filter','label=com.docker.compose.project=jikeda5001-cloud',
                   '--filter','label=com.docker.compose.service=foundation-db']).strip()
    return run(['docker','exec',container,'psql','-U','platform_admin','-d',database,'-Atc',sql]).strip()


def verify_http(release,result):
    samples=result['samples']
    wait_for(samples,lambda s:s['replicas'].get('catalog')==1,150)
    before=status()['replicas']; result['before']=before
    results=[]; guard=threading.Lock(); finish=time.monotonic()+90
    def worker():
        while time.monotonic()<finish:
            began=time.monotonic()
            try:
                with urllib.request.urlopen('http://127.0.0.1:5000/api/merchants/1/dishes',timeout=10) as response:
                    response.read(); record=(response.status,time.monotonic()-began,response.headers.get('X-Catalog-Instance'))
            except Exception as error: record=(0,time.monotonic()-began,type(error).__name__)
            with guard: results.append(record)
            time.sleep(max(0,0.5-(time.monotonic()-began)))
    with concurrent.futures.ThreadPoolExecutor(max_workers=20) as pool:
        jobs=[pool.submit(worker) for _ in range(20)]
        scaled=wait_for(samples,lambda s:s['replicas'].get('catalog')==2,75)
        result['during']=scaled['replicas']
        for name in ['identity','order','fulfilment']:
            if result['during'].get(name)!=before.get(name): raise RuntimeError('Unrelated service scaled')
        while time.monotonic()<finish:
            sample(samples); time.sleep(5)
        for job in jobs: job.result()
    idle=wait_for(samples,lambda s:s['replicas'].get('catalog')==1,150)
    latencies=sorted(r[1] for r in results)
    result.update(requests=len(results),failed=sum(r[0]!=200 for r in results),
                  p95Seconds=latencies[math.ceil(len(latencies)*0.95)-1],
                  responseInstances=dict(collections.Counter(r[2] for r in results if r[0]==200)),after=idle['replicas'])
    if result['failed'] or len(result['responseInstances'])<2: raise RuntimeError('Load balancing or availability check failed')


def verify_queue(release,result):
    samples=result['samples']
    wait_for(samples,lambda s:s.get('queueReady') and s['replicas'].get('fulfilment')==1 and s.get('queueActive')==0,150)
    before=count_sql('fulfilment_db','SELECT count(*) FROM "Orders";')
    consumer=compose(release,'ps','-q','fulfilment').strip()
    if not consumer or '\n' in consumer: raise RuntimeError('Queue test requires one baseline consumer')
    result.update(controlledPause=True,replayMessages=1000,ordersBefore=int(before))
    # Prevent the scaler observing transient one-off Order container counts during replay.
    import fcntl
    with open(STATE/'deployment.lock','a') as lock:
        fcntl.flock(lock,fcntl.LOCK_EX)
        run(['docker','pause',consumer])
        try:
            compose(release,'run','--rm','--no-deps','-e','QueueReplayCount=1000','order',timeout=180)
            wait_for(samples,lambda s:s.get('queueReady') and s.get('queueActive',0)>=5,40)
        except Exception:
            run(['docker','unpause',consumer]); raise
    def resume_consumer():
        if run(['docker','inspect','--format','{{.State.Paused}}',consumer]).strip()=='true':
            run(['docker','unpause',consumer])
    resume_timer=threading.Timer(15,resume_consumer)
    resume_timer.start()
    try:
        scaled=wait_for(samples,lambda s:s['replicas'].get('fulfilment')==2,90)
        result['during']=scaled['replicas']
    finally:
        resume_timer.cancel()
        resume_consumer()
    idle=wait_for(samples,lambda s:s.get('queueReady') and s.get('queueActive')==0 and s['replicas'].get('fulfilment')==1,180)
    result.update(after=idle['replicas'],deadLetters=idle.get('queueDeadLetters'),
                  ordersAfter=int(count_sql('fulfilment_db','SELECT count(*) FROM "Orders";')))
    if result['ordersBefore']!=result['ordersAfter'] or result['deadLetters']!=0:
        raise RuntimeError('Replay changed business order count or created dead letters')


if __name__=='__main__':
    parser=argparse.ArgumentParser(); parser.add_argument('scenario',choices=['http','queue']); args=parser.parse_args()
    release=json.loads((STATE/'current-release.json').read_text())
    result={'scenario':args.scenario,'revision':release['revision'],'samples':[],
            'startedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat()}
    try:
        (verify_http if args.scenario=='http' else verify_queue)(release,result)
        result['passed']=True
    except Exception as error:
        result.update(passed=False,errorType=type(error).__name__,error=str(error)); raise
    finally:
        result['finishedAtUtc']=datetime.datetime.now(datetime.timezone.utc).isoformat()
        (STATE/f'scaling-proof-{args.scenario}.json').write_text(json.dumps(result,indent=2))
    print('SCALING_PROOF_OK '+args.scenario)
