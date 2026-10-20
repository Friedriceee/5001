#!/usr/bin/env python3
"""Metric-driven, bounded Compose replica controller for the budget VM.

The input is nginx's real request counter, NOT a timer that schedules replicas.
The exporter is read-only and should bind to the Docker host bridge, never public ingress.
No credentials are returned in metrics/state. Deployment and scaling share a file lock.
"""
import argparse
import collections
import dataclasses
import datetime
import fcntl
import http.server
import json
import math
import os
from pathlib import Path
import subprocess
import threading
import time
import urllib.request


@dataclasses.dataclass
class Policy:
    minimum: int = 1
    maximum: int = 2
    up_threshold: float = 12
    down_threshold: float = 3
    up_samples: int = 2
    down_samples: int = 6
    cooldown: float = 60
    high: int = 0
    low: int = 0
    last_action: float = -math.inf

    def decide(self, value, current, now):
        if not math.isfinite(value) or value < 0:
            self.high = self.low = 0
            return current
        self.high = self.high + 1 if value >= self.up_threshold else 0
        self.low = self.low + 1 if value <= self.down_threshold else 0
        if now - self.last_action < self.cooldown:
            return current
        if self.high >= self.up_samples and current < self.maximum:
            return current + 1
        if self.low >= self.down_samples and current > self.minimum:
            return current - 1
        return current

    def applied(self, now):
        self.last_action = now
        self.high = self.low = 0


class Controller:
    def __init__(self, config):
        self.config = config
        self.policy = Policy(**config.get('policy', {}))
        self.queue_policy = Policy(up_threshold=5, down_threshold=0, up_samples=1, down_samples=6, cooldown=60)
        self.state = {'ready': False, 'replicas': {}, 'events': [], 'rps': 0, 'request_total': 0}
        self.guard = threading.Lock()
        self.previous = None
        self.actions = collections.Counter()

    def compose(self, *args):
        command = ['docker', 'compose', '--env-file', self.config['envFile']]
        for path in self.config['composeFiles']:
            command += ['-f', path]
        return subprocess.run(command + list(args), check=True, capture_output=True, text=True, timeout=180).stdout

    def counts(self):
        output = subprocess.check_output(['docker', 'ps', '--filter',
            'label=com.docker.compose.project=' + self.config['project'],
            '--format', '{{.Label "com.docker.compose.service"}}'], text=True, timeout=15)
        return dict(collections.Counter(output.splitlines()))

    def counter(self):
        with urllib.request.urlopen(self.config['statusUrl'], timeout=3) as response:
            lines = response.read().decode().splitlines()
        # nginx stub_status: accepts handled requests, all monotonic counters.
        return int(lines[2].split()[2])

    def event(self, **fields):
        row = {'atUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(), **fields}
        with self.guard:
            self.state['events'] = (self.state['events'] + [row])[-100:]
        with open(self.config['eventsFile'], 'a') as stream:
            stream.write(json.dumps(row) + '\n')
        print(json.dumps(row), flush=True)

    def tick(self):
        now = time.monotonic()
        total = self.counter()  # Failure stops decisions; a missing sample is NOT zero load.
        counts = self.counts()
        current = counts.get('catalog', 0)
        previous = self.previous
        self.previous = (now, total)
        if previous is None or total < previous[1]:
            return
        rps = (total - previous[1]) / (now - previous[0])
        with self.guard:
            self.state.update(ready=True, replicas=counts, rps=round(rps, 3), request_total=total,
                              atUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
        self.scale('catalog', self.policy, rps)
        if self.config.get('queueStatusUrl'):
            try:
                with urllib.request.urlopen(self.config['queueStatusUrl'], timeout=5) as response:
                    queue = json.load(response)
                observed = datetime.datetime.fromisoformat(queue['observedAtUtc'].replace('Z', '+00:00'))
                if observed.tzinfo is None:
                    observed = observed.replace(tzinfo=datetime.timezone.utc)
                if (datetime.datetime.now(datetime.timezone.utc) - observed).total_seconds() > 20:
                    raise RuntimeError('Queue observation is stale')
                with self.guard:
                    self.state.update(queueActive=queue['active'], queueDeadLetters=queue['deadLetters'], queueReady=True)
                self.scale('fulfilment', self.queue_policy, queue['active'])
            except Exception:
                self.queue_policy.high = self.queue_policy.low = 0
                with self.guard:
                    self.state['queueReady'] = False

    def scale(self, service, policy, value):
        now = time.monotonic()
        counts = self.counts()
        current = counts.get(service, 0)
        if current < policy.minimum:
            # Do not silently restart a deliberately stopped deployment.
            return
        target = policy.decide(value, current, now)
        if target == current:
            return
        with open(self.config['lockFile'], 'a') as lock:
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                return
            if self.counts().get(service, 0) != current:
                return
            self.compose('up', '-d', '--no-deps', '--no-recreate', '--scale', f'{service}={target}',
                         '--wait', '--wait-timeout', '120', service)
            after = self.counts()
            if after.get(service) != target:
                raise RuntimeError('Replica count did not reach target')
            others = {s: n for s, n in counts.items() if s != service}
            if any(after.get(s) != n for s, n in others.items()):
                raise RuntimeError('Unrelated service replica counts changed during scaling')
            direction = 'out' if target > current else 'in'
            policy.applied(time.monotonic())
            self.actions[(service, direction)] += 1
            self.event(service=service, direction=direction, before=current, after=target,
                       measuredValue=round(value, 3), reason='request_rate' if service == 'catalog' else 'queue_depth', replicas=after)
            with self.guard:
                self.state['replicas'] = after

    def snapshot(self):
        with self.guard:
            return json.loads(json.dumps(self.state))

    def metrics(self):
        state = self.snapshot()
        lines = ['# TYPE jikeda_catalog_requests_total counter',
                 f'jikeda_catalog_requests_total {state["request_total"]}',
                 '# TYPE jikeda_catalog_requests_per_second gauge',
                 f'jikeda_catalog_requests_per_second {state["rps"]}',
                 '# TYPE jikeda_autoscaler_ready gauge', f'jikeda_autoscaler_ready {int(state["ready"])}',
                 f'jikeda_scale_out_threshold {self.policy.up_threshold}',
                 f'jikeda_scale_in_threshold {self.policy.down_threshold}',
                 '# TYPE jikeda_service_replicas gauge']
        for service, count in sorted(state['replicas'].items()):
            lines.append(f'jikeda_service_replicas{{service="{service}"}} {count}')
        lines += [f'jikeda_queue_active_messages {state.get("queueActive", 0)}',
                  f'jikeda_queue_dead_letter_messages {state.get("queueDeadLetters", 0)}',
                  f'jikeda_queue_observation_ready {int(state.get("queueReady", False))}']
        for service in ('catalog', 'fulfilment'):
            for direction in ('in', 'out'):
                lines.append(f'jikeda_scaling_actions_total{{service="{service}",direction="{direction}"}} {self.actions[(service, direction)]}')
        return '\n'.join(lines) + '\n'

    def run(self):
        controller = self
        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                if self.path == '/metrics':
                    body, kind = controller.metrics().encode(), 'text/plain; version=0.0.4'
                elif self.path == '/state':
                    body, kind = json.dumps(controller.snapshot()).encode(), 'application/json'
                else:
                    self.send_error(404)
                    return
                self.send_response(200)
                self.send_header('Content-Type', kind)
                self.send_header('Content-Length', str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            def log_message(self, *args):
                pass
        server = http.server.ThreadingHTTPServer((self.config.get('listenHost', '127.0.0.1'), 9109), Handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        while True:
            try:
                self.tick()
            except Exception as error:
                self.previous = None
                self.policy.high = self.policy.low = 0
                with self.guard:
                    self.state['ready'] = False
                # No command/environment output: it may contain credentials.
                self.event(error=type(error).__name__, reason='sample_or_action_failed')
            time.sleep(self.config.get('intervalSeconds', 5))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--config', required=True)
    args = parser.parse_args()
    Controller(json.loads(Path(args.config).read_text())).run()
