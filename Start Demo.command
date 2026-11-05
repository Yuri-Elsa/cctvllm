#!/bin/sh
# Launcher for macOS and Linux. Uses dotnet and ollama from the PATH; nothing here depends on where the project sits.
cd "$(dirname "$0")" || exit 1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export OLLAMA_NO_CLOUD=1
export OLLAMA_CONTEXT_LENGTH="${OLLAMA_CONTEXT_LENGTH:-8192}"
export OLLAMA_NUM_PARALLEL="${OLLAMA_NUM_PARALLEL:-1}"
mkdir -p data
if command -v ollama >/dev/null 2>&1 && ! curl -fsS http://127.0.0.1:11434/api/tags >/dev/null 2>&1; then
  ollama serve > data/ollama.log 2>&1 &
fi
exec dotnet run --no-launch-profile
