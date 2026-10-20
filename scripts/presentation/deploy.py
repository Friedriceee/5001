#!/usr/bin/env python3
"""Run as the VM operator; publish SHA-tagged images, migrate once, verify and roll back.

Cloud credentials are acquired from the VM managed identity. Runtime passwords remain
in root-only files outside Git. The Docker credential directory is removed after pulling.
"""
import argparse
import datetime
import fcntl
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import urllib.parse
import urllib.request

STATE = Path('/opt/jikeda/shared')
RELEASES = Path('/opt/jikeda/releases')
OPERATIONS = Path('/opt/jikeda/operations/presentation')
APPLICATION_SERVICES = ('identity', 'catalog', 'order', 'fulfilment', 'gateway', 'frontend')
# Server jobs must report errors directly, without the distribution's interactive
# crash collector delaying Azure Run Command after a failed subprocess.
sys.excepthook = sys.__excepthook__


def stage(message):
    print(datetime.datetime.now(datetime.timezone.utc).isoformat() + ' ' + message, flush=True)


def run(args, **kwargs):
    kwargs.setdefault('timeout', 180)
    try:
        result = subprocess.run(args, text=True, capture_output=True, **kwargs)
    except subprocess.TimeoutExpired as error:
        for output in (error.stdout, error.stderr):
            if output:
                print(output.decode(errors='replace')[-12000:] if isinstance(output, bytes) else output[-12000:], flush=True)
        raise
    if result.returncode:
        # Bootstrap output is retained in the root-only server deployment log.
        # This preserves the failed smoke step without exposing runtime environment values.
        print(result.stdout[-12000:], flush=True)
        print(result.stderr[-12000:], flush=True)
        raise subprocess.CalledProcessError(result.returncode, args)
    return result.stdout


def load_env(path):
    return dict(line.split('=', 1) for line in Path(path).read_text().splitlines()
                if line and not line.startswith('#') and '=' in line)


def compose(release, *args, **kwargs):
    command = ['docker', 'compose', '--env-file', release['envFile']]
    for path in release['composeFiles']:
        command += ['-f', path]
    return run(command + list(args), **kwargs)


def legacy_release():
    return {'revision': 'e226c94d22e7bb5a3dc606aadb91f9fa369198a4',
            'envFile': '/opt/jikeda/shared/main-runtime.env',
            'sourceDir': '/opt/jikeda/releases/main-e226c94d22e7',
            'composeFiles': ['/opt/jikeda/releases/main-e226c94d22e7/docker-compose.microservices.yml',
                             '/opt/jikeda/operations/main-20261019/cloud.override.yml']}


def registry_login(registry, tenant, docker_config):
    url = 'http://169.254.169.254/metadata/identity/oauth2/token?' + urllib.parse.urlencode({
        'api-version': '2018-02-01', 'resource': 'https://management.azure.com/'})
    # IMDS must bypass proxy configuration, including on hosts that use an outbound proxy.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    request = urllib.request.Request(url, headers={'Metadata': 'true'})
    with opener.open(request, timeout=15) as response:
        token = json.load(response)['access_token']
    body = urllib.parse.urlencode({'grant_type': 'access_token', 'service': registry,
                                   'tenant': tenant, 'access_token': token}).encode()
    request = urllib.request.Request(f'https://{registry}/oauth2/exchange', data=body)
    with urllib.request.urlopen(request, timeout=30) as response:
        refresh = json.load(response)['refresh_token']
    environment = {**os.environ, 'DOCKER_CONFIG': docker_config}
    run(['docker', 'login', registry, '--username', '00000000-0000-0000-0000-000000000000',
         '--password-stdin'], input=refresh, env=environment)
    return environment


def telemetry_connection(resource_id):
    metadata = 'http://169.254.169.254/metadata/identity/oauth2/token?' + urllib.parse.urlencode({
        'api-version': '2018-02-01', 'resource': 'https://management.azure.com/'})
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open(urllib.request.Request(metadata, headers={'Metadata': 'true'}), timeout=15) as response:
        token = json.load(response)['access_token']
    request = urllib.request.Request('https://management.azure.com' + resource_id + '?api-version=2020-02-02',
                                     headers={'Authorization': 'Bearer ' + token})
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)['properties']['ConnectionString']


def backup(revision):
    directory = STATE / 'backups' / ('presentation-' + datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ'))
    directory.mkdir(parents=True, mode=0o700)
    container = run(['docker', 'ps', '-q', '--filter', 'label=com.docker.compose.project=jikeda5001-cloud',
                     '--filter', 'label=com.docker.compose.service=foundation-db']).strip()
    if not container:
        raise RuntimeError('Existing database container is missing')
    for name in ['identity_db', 'catalog_db', 'order_db', 'fulfilment_db']:
        with open(directory / f'{name}.dump', 'wb') as stream:
            subprocess.run(['docker', 'exec', container, 'pg_dump', '-U', 'platform_admin', '-Fc', name],
                           stdout=stream, stderr=subprocess.PIPE, check=True, timeout=120)
    return str(directory)


def smoke(release):
    values = load_env(release['envFile'])
    address = compose(release, 'port', '--index', '1', 'catalog', '8080').strip()
    env = {**os.environ, 'JIKEDA_SMOKE_PASSWORD': values['DEMO_PASSWORD']}
    output = run(['docker', 'run', '--rm', '--network', 'host',
                  '--mount', f'type=bind,src={release["sourceDir"]},dst=/source,readonly',
                  '-e', 'JIKEDA_GATEWAY_URL=http://127.0.0.1:5000',
                  '-e', f'JIKEDA_CATALOG_URL=http://{address}',
                  '-e', 'JIKEDA_ORDER_URL=http://127.0.0.1:5103' if 'presentation' in release['envFile'] else
                        'JIKEDA_ORDER_URL=http://' + compose(release, 'port', 'order', '8080').strip(),
                  '-e', 'JIKEDA_SMOKE_PASSWORD', 'node:22-alpine',
                  'node', '/source/scripts/smoke/foundation-smoke.mjs'], env=env, timeout=180)
    (STATE / 'presentation-smoke.log').write_text(output)
    for path in ['/health', '/health/identity', '/health/catalog', '/health/order', '/health/fulfilment']:
        with urllib.request.urlopen('http://127.0.0.1:5000' + path, timeout=10) as response:
            if response.status != 200:
                raise RuntimeError('Health verification failed')


def install_scaler(release):
    OPERATIONS.mkdir(parents=True, exist_ok=True)
    source = Path(release['sourceDir'])
    # Only Docker bridge clients and localhost can reach the exporter; there is no public route.
    gateway = run(['docker', 'network', 'inspect', 'bridge', '--format', '{{(index .IPAM.Config 0).Gateway}}']).strip()
    config = {'project': 'jikeda5001-cloud', 'envFile': release['envFile'], 'composeFiles': release['composeFiles'],
              'statusUrl': 'http://127.0.0.1:9180/status', 'queueStatusUrl': 'http://127.0.0.1:5103/internal/messaging/status',
              'listenHost': gateway, 'eventsFile': str(STATE / 'scaling-events.jsonl'),
              'lockFile': str(STATE / 'deployment.lock'), 'intervalSeconds': 5}
    (OPERATIONS / 'autoscaler.json').write_text(json.dumps(config, indent=2))
    unit = f'''[Unit]
Description=JiKeDa metric-driven service replica controller
After=docker.service network-online.target
Requires=docker.service
[Service]
Type=simple
ExecStart=/usr/bin/python3 {source}/scripts/presentation/autoscaler.py --config {OPERATIONS}/autoscaler.json
Restart=always
RestartSec=5
NoNewPrivileges=true
ProtectSystem=strict
ReadWritePaths={STATE}
MemoryMax=256M
[Install]
WantedBy=multi-user.target
'''
    Path('/etc/systemd/system/jikeda-autoscaler.service').write_text(unit)
    run(['systemctl', 'daemon-reload'])
    run(['systemctl', 'enable', '--now', 'jikeda-autoscaler.service'])


def publish(revision, image_tag):
    if not re.fullmatch(r'[a-f0-9]{40}', revision) or not re.fullmatch(r'[a-f0-9]{40}-[0-9]+', image_tag):
        raise ValueError('Invalid revision or image tag')
    os.umask(0o077)
    STATE.mkdir(parents=True, exist_ok=True)
    with open(STATE / 'deployment.lock', 'a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        previous = json.loads((STATE / 'current-release.json').read_text()) if (STATE / 'current-release.json').exists() else legacy_release()
        stage('Backing up four databases')
        backup_path = backup(revision)
        source = RELEASES / ('presentation-' + image_tag)
        source.mkdir(parents=True, exist_ok=True)
        source.chmod(0o755)
        os.umask(0o022)
        stage('Fetching exact source revision')
        if not (source / '.git').exists():
            run(['git', 'init', '-q', str(source)])
            run(['git', '-C', str(source), 'remote', 'add', 'origin', 'https://github.com/Friedriceee/5001.git'])
        run(['git', '-C', str(source), 'fetch', '--depth', '1', 'origin', revision])
        run(['git', '-C', str(source), 'checkout', '--detach', '-q', 'FETCH_HEAD'])
        if run(['git', '-C', str(source), 'rev-parse', 'HEAD']).strip() != revision:
            raise RuntimeError('Source revision mismatch')
        os.umask(0o077)
        target = json.loads((source / 'deploy/presentation/azure-target.json').read_text())
        values = load_env(previous['envFile'])
        if (STATE / 'telemetry.env').exists():
            values.update(load_env(STATE / 'telemetry.env'))
        if target.get('appInsightsResourceId'):
            values['APPLICATIONINSIGHTS_CONNECTION_STRING'] = telemetry_connection(target['appInsightsResourceId'])
        values.update(SOURCE_REVISION=revision, IMAGE_TAG=image_tag, REGISTRY=target['registry'],
                      RELEASE_DIR=str(source), SERVICEBUS_NAMESPACE=target['serviceBusNamespace'])
        env_file = STATE / f'presentation-{image_tag}.env'
        env_file.write_text(''.join(f'{key}={value}\n' for key, value in values.items()))
        env_file.chmod(0o600)
        release = {'revision': revision, 'imageTag': image_tag, 'sourceDir': str(source), 'envFile': str(env_file),
                   'composeFiles': [str(source / 'docker-compose.microservices.yml'), str(source / 'deploy/presentation/cloud.override.yml')],
                   'backupDirectory': backup_path}
        changed = False
        try:
            compose(release, 'config', '--quiet')
            stage('Pulling verified release images')
            with tempfile.TemporaryDirectory(prefix='jikeda-registry-') as credentials:
                docker_env = registry_login(target['registry'], target['tenantId'], credentials)
                docker_env['COMPOSE_PARALLEL_LIMIT'] = '2'
                # These are the release's seven scanned ACR images. Existing pinned
                # infrastructure images are reused instead of repulling Docker Hub
                # on every application release.
                compose(release, 'pull', 'foundation-db', 'identity', 'catalog', 'order',
                        'fulfilment', 'gateway', 'frontend', env=docker_env, timeout=600)
            if Path('/etc/systemd/system/jikeda-autoscaler.service').exists():
                run(['systemctl', 'stop', 'jikeda-autoscaler.service'])
            changed = True
            stage('Checking database container')
            compose(release, 'up', '-d', '--no-deps', '--wait', 'foundation-db', timeout=180)
            # One migration job per database before any new replica starts.
            for service in ['identity', 'catalog', 'order', 'fulfilment']:
                stage('Migrating ' + service)
                compose(release, 'run', '--rm', '--no-deps', '-e', 'Database__MigrateOnStartup=true',
                        '-e', 'MigrateOnly=true', '-e', 'APPLICATIONINSIGHTS_CONNECTION_STRING=', service, timeout=90)
            stage('Starting application and monitoring containers')
            compose(release, 'up', '-d', '--no-build', '--scale', 'catalog=1', '--scale', 'fulfilment=1',
                    '--wait', '--wait-timeout', '180', timeout=300)
            stage('Verifying business flow')
            smoke(release)
            stage('Starting metric-driven autoscaler')
            install_scaler(release)
            (STATE / 'previous-release.json').write_text(json.dumps(previous, indent=2))
            (STATE / 'current-release.json').write_text(json.dumps(release, indent=2))
            (STATE / 'presentation-deployment.json').write_text(json.dumps({'status': 'succeeded', **release}, indent=2))
            print('DEPLOYMENT_OK ' + image_tag)
        except Exception as error:
            stage('Deployment failed: ' + type(error).__name__)
            if changed:
                stage('Restoring previous application release')
                if Path('/etc/systemd/system/jikeda-autoscaler.service').exists():
                    run(['systemctl', 'stop', 'jikeda-autoscaler.service'])
                compose(previous, 'up', '-d', '--no-build', '--no-deps', '--scale', 'catalog=1', '--scale', 'fulfilment=1',
                        '--wait', '--wait-timeout', '180', *APPLICATION_SERVICES, timeout=300)
                smoke(previous)
            if (STATE / 'current-release.json').exists() and previous != legacy_release():
                install_scaler(previous)
            (STATE / 'presentation-deployment.json').write_text(json.dumps({'status': 'failed', 'errorType': type(error).__name__,
                'revision': revision, 'rolledBack': changed}, indent=2))
            raise


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--revision', required=True)
    parser.add_argument('--image-tag', required=True)
    args = parser.parse_args()
    publish(args.revision, args.image_tag)
