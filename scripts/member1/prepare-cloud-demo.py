"""Rotate all six known seed accounts before opening Internet ingress.

The temporary registration lets Identity generate its own PasswordHasher hash;
neither hashes nor access tokens are logged. Reruns verify the existing password.
"""
import json
import secrets
import subprocess
import urllib.error
import urllib.request
from pathlib import Path

state = Path('/opt/jikeda/shared')
env = dict(line.split('=', 1) for line in (state / 'runtime.env').read_text().splitlines())
compose = ['docker', 'compose', '--env-file', str(state / 'runtime.env'),
           '-f', 'deploy/budget-azure/compose.yml']


def request(path, body):
    req = urllib.request.Request('http://127.0.0.1:5000' + path,
                                 data=json.dumps(body).encode(),
                                 headers={'Content-Type': 'application/json'})
    try:
        with urllib.request.urlopen(req, timeout=15) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, None


if not (state / 'seed-passwords-rotated').exists():
    username = 'bootstrap_' + secrets.token_hex(8)
    status, result = request('/api/auth/customer/register', {
        'username': username, 'password': env['DEMO_PASSWORD'],
        'displayName': 'Temporary deployment bootstrap', 'phoneNumber': '10000000'})
    if status != 201:
        raise RuntimeError(f'Bootstrap registration failed: HTTP {status}')
    account_id = int(result['data']['accountId'])
    profile_id = int(result['data']['customerId'])
    sql = f'''
BEGIN;
DO $$ BEGIN
IF (SELECT COUNT(*) FROM "Accounts" WHERE "Id" IN (1000,1001,1002,1003)
    AND "Username" IN ('admin','merchant1','rider1','customer1')) <> 4 THEN
  RAISE EXCEPTION 'Unexpected demo seed accounts';
END IF;
END $$;
UPDATE "Accounts" SET "PasswordHash" =
  (SELECT "PasswordHash" FROM "Accounts" WHERE "Id" = {account_id})
WHERE "Id" IN (1000,1001,1002,1003);
DELETE FROM "Accounts" WHERE "Id" = {account_id};
DELETE FROM "UserProfiles" WHERE "Id" = {profile_id};
COMMIT;
'''
    subprocess.run(compose + ['exec', '-T', 'foundation-db', 'psql', '-v', 'ON_ERROR_STOP=1',
                             '-U', 'platform_admin', '-d', 'identity_db'],
                   input=sql, text=True, check=True, stdout=subprocess.DEVNULL)
    (state / 'seed-passwords-rotated').touch(mode=0o600)

# Preserve the existing private demo password when new migrations add seed accounts.
# Only known seed rows are updated; real registrations retain their own passwords.
assert request('/api/auth/login', {'username': 'customer1', 'password': env['DEMO_PASSWORD']})[0] == 200
subprocess.run(compose + ['exec', '-T', 'foundation-db', 'psql', '-v', 'ON_ERROR_STOP=1',
                         '-U', 'platform_admin', '-d', 'identity_db'], input='''
BEGIN;
DO $$ BEGIN
IF (SELECT COUNT(*) FROM "Accounts" WHERE
    ("Id", "Username") IN ((1000,'admin'),(1001,'merchant1'),(1002,'rider1'),
                           (1003,'customer1'),(1004,'merchant2'),(1005,'rider2'))) <> 6 THEN
  RAISE EXCEPTION 'Unexpected demo seed accounts';
END IF;
END $$;
UPDATE "Accounts" SET "PasswordHash" =
  (SELECT "PasswordHash" FROM "Accounts" WHERE "Id" = 1003 AND "Username" = 'customer1')
WHERE ("Id", "Username") IN ((1000,'admin'),(1001,'merchant1'),(1002,'rider1'),
                             (1003,'customer1'),(1004,'merchant2'),(1005,'rider2'));
COMMIT;
''', text=True, check=True, stdout=subprocess.DEVNULL)
for username in ('admin', 'merchant1', 'merchant2', 'rider1', 'rider2', 'customer1'):
    assert request('/api/auth/login', {'username': username, 'password': env['DEMO_PASSWORD']})[0] == 200
    assert request('/api/auth/login', {'username': username, 'password': 'Passw0rd!123'})[0] == 401
print('Six seed accounts rotated; default passwords rejected.')
