#!/usr/bin/env python3
"""Run a fixed demo operation on the configured VM using the operator's Azure session."""
import argparse,json,os,subprocess,sys
from pathlib import Path
p=argparse.ArgumentParser()
p.add_argument('operation',choices=['http','queue','smoke','rollback','status'])
p.add_argument('--az',default='az'); args=p.parse_args()
target=json.loads((Path(__file__).resolve().parents[2]/'deploy/presentation/azure-target.json').read_text())
# All inputs are fixed choices; sourceDir is read from the root-owned deployed release manifest.
script='''python3 - <<'OPERATOR'
import base64,gzip,json,subprocess,sys
from pathlib import Path
state=Path('/opt/jikeda/shared')
release=json.loads((state/'current-release.json').read_text())
source=Path(release['sourceDir'])
sys.path.insert(0,str(source/'scripts/presentation'))
'''
if args.operation in ('http','queue'):
 script+="result=subprocess.run(['python3',str(source/'scripts/presentation/verify-scaling.py'),'"+args.operation+"'],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)\n"
 script+="proof=json.loads((state/'scaling-proof-"+args.operation+".json').read_text())\n"
 script+="proof['samples']=[{k:r.get(k) for k in ['atUtc','replicas','rps','queueActive','queueDeadLetters','queueReady']} for r in proof.get('samples',[])]\n"
 script+="print('PROOF_GZIP_BASE64 '+base64.b64encode(gzip.compress(json.dumps(proof).encode())).decode())\nsys.exit(result.returncode)\n"
elif args.operation=='smoke':
 script+="from deploy import smoke\nsmoke(release)\nprint((state/'presentation-smoke.log').read_text())\nprint('SMOKE_OK '+release['revision'])\n"
elif args.operation=='rollback':
 script+="from rollback import drill\ndrill()\nprint((state/'rollback-drill.json').read_text())\n"
else:
 script+="import urllib.request\nwith urllib.request.urlopen('http://172.17.0.1:9109/state',timeout=5) as r: print(r.read().decode())\nprint(json.dumps({k:release[k] for k in ['revision','imageTag']}))\n"
script+='OPERATOR\n'
result=subprocess.run([args.az,'vm','run-command','invoke','--resource-group',target['resourceGroup'],
 '--name',target['vmName'],'--command-id','RunShellScript','--scripts',script,'-o','json'],text=True,capture_output=True)
if result.returncode: print(result.stderr,file=sys.stderr);sys.exit(result.returncode)
data=json.loads(result.stdout); message='\n'.join(v.get('message','') for v in data.get('value',[]))
if args.operation in ('http','queue'):
 import base64,gzip
 line=next((l for l in message.splitlines() if l.startswith('PROOF_GZIP_BASE64 ')),None)
 if not line: raise RuntimeError('VM did not return a complete proof; inspect protected server proof file')
 proof=json.loads(gzip.decompress(base64.b64decode(line.split(' ',1)[1])))
 path=Path('scaling-proof-'+args.operation+'.json');path.write_text(json.dumps(proof,indent=2))
 print(json.dumps({k:v for k,v in proof.items() if k!='samples'},indent=2))
 print('Saved '+str(path.resolve()))
 if not proof.get('passed'):sys.exit(1)
else:
 print(message)
 if args.operation=='smoke' and 'SMOKE_OK ' not in message:sys.exit(1)
 if args.operation=='rollback' and 'ROLLBACK_DRILL_OK ' not in message:sys.exit(1)
