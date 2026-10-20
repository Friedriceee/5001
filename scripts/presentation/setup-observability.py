#!/usr/bin/env python3
"""Create budget-capped Azure logs/traces without printing instrumentation credentials."""
import argparse
import json
from pathlib import Path
import subprocess


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--az', default='az')
    args = parser.parse_args()
    target_path = Path(__file__).resolve().parents[2] / 'deploy/presentation/azure-target.json'
    target = json.loads(target_path.read_text())
    group = f"/subscriptions/{target['subscriptionId']}/resourceGroups/{target['resourceGroup']}"
    workspace = group + '/providers/Microsoft.OperationalInsights/workspaces/jikeda5001-m1-logs'
    component = group + '/providers/Microsoft.Insights/components/jikeda5001-m1-insights'

    def az(*parts):
        return json.loads(subprocess.check_output([args.az, *parts, '-o', 'json'], text=True))

    az('rest', '--method', 'put', '--url', 'https://management.azure.com' + workspace + '?api-version=2023-09-01',
       '--body', json.dumps({'location': 'eastus2', 'properties': {
           'sku': {'name': 'PerGB2018'}, 'retentionInDays': 30, 'workspaceCapping': {'dailyQuotaGb': 0.1}}}))
    az('rest', '--method', 'put', '--url', 'https://management.azure.com' + component + '?api-version=2020-02-02',
       '--body', json.dumps({'location': 'eastus2', 'kind': 'web', 'properties': {
           'Application_Type': 'web', 'WorkspaceResourceId': workspace, 'SamplingPercentage': 20,
           'DisableIpMasking': False}}))
    target['appInsightsResourceId'] = component
    target_path.write_text(json.dumps(target, indent=2) + '\n')
    print(json.dumps({'workspace': workspace, 'dailyQuotaGb': 0.1, 'appInsights': component}))


if __name__ == '__main__':
    main()
