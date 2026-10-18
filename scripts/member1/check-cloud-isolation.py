"""Read-only authentication and database boundary checks on the demo host."""
import json
import subprocess
import urllib.error
import urllib.request
from pathlib import Path

state = Path('/opt/jikeda/shared')
env = dict(line.split('=', 1) for line in (state / 'runtime.env').read_text().splitlines())
compose = ['docker', 'compose', '--env-file', str(state / 'runtime.env'),
           '-f', 'deploy/budget-azure/compose.yml']


def http(path, headers=None, body=None):
    req = urllib.request.Request('http://127.0.0.1:8088' + path,
                                 headers=headers or {},
                                 data=json.dumps(body).encode() if body else None)
    if body:
        req.add_header('Content-Type', 'application/json')
    try:
        with urllib.request.urlopen(req, timeout=15) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()


assert http('/api/accounts')[0] == 401
assert http('/api/accounts', {'Authorization': 'Bearer invalid'})[0] == 401
status, body = http('/api/auth/login', body={'username': 'customer1', 'password': env['DEMO_PASSWORD']})
assert status == 200
token = json.loads(body)['data']['accessToken']
assert http('/api/accounts', {'Authorization': 'Bearer ' + token})[0] == 403
assert http('/internal/catalog/reservations')[0] == 404
assert http('/internal/order/events')[0] == 404
assert http('/customer/merchants')[0] == 200
print('HTTP: 401 without/with invalid token; customer admin access 403; internal routes 404; SPA 200.')

services = ('identity', 'catalog', 'order', 'fulfilment')
for service in services:
    for target in services:
        result = subprocess.run(compose + [
            'exec', '-T', '-e', f'PGPASSWORD={env[service.upper() + "_DB_PASSWORD"]}',
            'foundation-db', 'psql', '-h', '127.0.0.1', '-U', service + '_app',
            '-d', target + '_db', '-v', 'ON_ERROR_STOP=1', '-Atc', 'SELECT current_database()'],
            text=True, capture_output=True)
        if service == target:
            assert result.returncode == 0 and result.stdout.strip() == target + '_db'
        else:
            assert result.returncode != 0 and 'permission denied for database' in result.stderr
    print(f'DB: {service}_app can connect to its own database; three cross-service connections denied.')
print('Cloud isolation checks passed.')
