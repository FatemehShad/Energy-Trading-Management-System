#!/usr/bin/env bash
set -euo pipefail
cd /workspace/Energy-Trading-Management-System
export DOTNET_ROOT=/workspace/tools/dotnet
export DOTNET_CLI_HOME=/workspace/tools/dotnet-home
export NUGET_PACKAGES=/workspace/tools/nuget
export DOCKER_CONFIG=/workspace/tools/docker
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export PATH="$DOTNET_ROOT:$PATH"
mkdir -p "$DOTNET_ROOT" "$DOCKER_CONFIG"
if ! "$DOTNET_ROOT/dotnet" --list-sdks 2>/dev/null | grep -q '^8\.0\.425 '; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/energy-dotnet-install.sh
  bash /tmp/energy-dotnet-install.sh --version 8.0.425 --install-dir "$DOTNET_ROOT" --no-path
fi
dotnet restore EnergyTrading.sln
dotnet build EnergyTrading.sln -c Release --no-restore
