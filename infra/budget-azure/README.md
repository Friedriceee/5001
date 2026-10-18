# Temporary Azure validation host

This is the budget alternative to the managed Container Apps/ACR/PostgreSQL stack in the proposal. It provisions one Linux VM, a 32 GiB Standard SSD, a Standard public IP, and a small private network. It does not create a managed database, paid load balancer, ACR, AKS, or Service Bus. A real single-host release has passed the cloud business smoke and isolation checks; actual scalability evidence is still separate work.

The initial network opens no inbound Internet ports. Bootstrap and the first application validation use Azure Run Command through the authenticated Azure management plane; no third-party public-IP lookup or public SSH is needed. After the private checks, `publicWeb=true` permits only TCP 80 (redirect/certificate validation) and 443 (HTTPS). PostgreSQL, SSH and direct service ports stay closed. Caddy serves the Vue app and proxies API/health requests to the gateway with automatic public TLS.

## Selected host and cost basis (18 October 2026, USD)

The account has no Basv2 quota, and the small Singapore SKUs are restricted for this subscription. East US 2 permits D2als v7 (2 vCPU / 4 GiB), with 10 family vCPUs available and a compute rate of USD 0.0804/hour. This is the selected alternative. Its 96-hour compute/disk/IP base is approximately USD 8.51 (CNY 57 at a planning rate of 6.7), before tax and other usage; reserve CNY 80–90 within the CNY 100 budget. Region and SKU must be taken from the actual deployment outputs when interpreting latency results.

The earlier Singapore comparison was:

| Resource | Rate |
|---|---:|
| B2als v2: 2 vCPU / 4 GiB | 0.0472/hour |
| B2as v2: 2 vCPU / 8 GiB | 0.0944/hour |
| E4 LRS Standard SSD: 32 GiB | 2.40/month, hourly prorated; operations extra |
| Standard static IPv4 | 0.005/hour |

Those Singapore figures correspond to approximately USD 5.33 (4 GiB) or USD 9.86 (8 GiB) for 96 hours, but those B-series SKUs cannot currently be deployed by this account. Reserve money for tax, disk operations, network traffic and any added services. The CNY 100 tag is descriptive and does not enforce a billing cap. Never select a reservation, paid support plan, Marketplace image or larger SKU to resolve a capacity error without recalculating the budget.

Prices: [Microsoft Retail Prices API](https://prices.azure.com/api/retail/prices), [disk pricing](https://azure.microsoft.com/en-us/pricing/details/managed-disks/), [IP pricing](https://azure.microsoft.com/en-us/pricing/details/ip-addresses/).

## Preparation and validation

The helper uses the official Microsoft Azure CLI image pinned to its tested digest. Authentication state is written only to the git-ignored `.azure.local/` directory. Never commit or package that directory.

```bash
bash scripts/member1/az.sh login --use-device-code
bash scripts/member1/az.sh account list --query "[?state=='Enabled'].{name:name,id:id}" -o table
bash scripts/member1/az.sh bicep build --file infra/budget-azure/main.bicep --stdout
```

Choose the subscription explicitly. Store deployment parameters in a git-ignored `*.local/` directory. Supply a dedicated SSH public key for the VM operating-system profile, keeping its private key protected locally; port 22 remains closed. Validate available SKU, Ubuntu image, permissions and current prices in the actual subscription before applying. A successful local template build is not evidence of a cloud deployment.

For a dedicated resource group, run `az deployment group what-if` first and inspect the exact resources before `az deployment group create`. Wait for `cloud-init status --wait` after provisioning. Initial application containers should bind their published ports to loopback and use separate service database credentials and an injected JWT key.

After provisioning, use the full public source commit and hostname from the deployment outputs:

```bash
bash scripts/member1/deploy-from-local.sh RESOURCE_GROUP VM_NAME SOURCE_SHA PUBLIC_HOST
```

This starts a background deployment through Azure Run Command. A successful dispatch is not a successful application release: inspect `/opt/jikeda/shared/deploy.exit` (must be `0`) and `/opt/jikeda/shared/cloud-smoke.log`. Run `scripts/member1/check-cloud-isolation.py` from `/opt/jikeda/source` on the VM, then enable the web rules and verify the real HTTPS certificate and site externally.

The host builds images serially and keeps application state in named Docker volumes. Runtime passwords/JWT keys are generated on the VM and kept outside the public source checkout in `/opt/jikeda/shared/runtime.env` (mode `0600`, parent `0700`). Only the four seeded accounts have their known default password replaced; the temporary bootstrap account/profile is removed. The setup reuses the existing secrets on a retry. Do not package the runtime file, local CLI cache or SSH keys with screenshots or reports.

## Current application status

The tested source baseline is `114a1287000b0db4c83f05d2765045bb7d715921` from `Friedriceee/5001`. Both the local preflight and the real Azure-host smoke passed. Gateway and all four service containers start. The smoke passes login, menu, stock reservation/replay/release, checkout/replay, declined payment compensation and the Order HTTP event feed. The cloud checks also passed missing/invalid-token `401`, customer-to-admin `403`, internal-route `404`, and all twelve denied cross-service database connections. Fulfilment remains a health-only foundation at this SHA. There is no completed delivery workflow or cloud auto-scaling result to claim.

Browser verification found that the customer frontend still called the monolith's singular menu, address and order-list routes and used its quantity/status fields. This branch connects the public menu to `/api/merchants/{id}/dishes`, the authenticated customer to `/api/addresses` and `/api/orders`, and the cart/checkout displays to Order's quantity, total and status fields. Customer ownership comes from the authenticated service principal. Use the release manifest for the exact deployed commit; the baseline above identifies the service business implementation.

All four databases use distinct credentials and owners on one physical PostgreSQL 16 instance. Startup migrations currently use the owning service roles; separate migration and restricted runtime roles are not implemented. This budget host is one failure domain and cannot demonstrate multi-host availability. The release has not received a new image vulnerability scan, so no scan pass is claimed.

The legacy monolith CD workflow does not release these five new components. Per-service CI/CD integration, a verified rollback and HTTP/queue autoscaling remain outstanding. Keep the real cloud smoke separate from the earlier local/monolith pipeline evidence. The local dispatch helper refuses to overwrite an existing checkout with another source SHA until a reviewed migration/rollback path is prepared.

## Stop ongoing charges

Export the required data and evidence before cleanup. Stopping/deallocating the VM does not remove disk/IP charges. Delete only this dedicated demo resource group and verify that its resources are gone when the demonstration environment is no longer needed. Cleanup is not scheduled automatically by this template.
