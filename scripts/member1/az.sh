#!/usr/bin/env bash
# Official Azure CLI, isolated from the host Python/Homebrew installation.
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
state="$root/.azure.local"
umask 077
mkdir -p "$state"
chmod 700 "$state"
exec docker run --rm -i \
  --mount "type=bind,src=$root,dst=/workspace,readonly" \
  --mount "type=bind,src=$state,dst=/root/.azure" \
  --workdir /workspace \
  mcr.microsoft.com/azure-cli@sha256:933eb8dcb81aecb6f77e03c5b8660f0a1dfa9e1d16525763a27f86ff3b83a044 \
  az "$@"
