#!/usr/bin/env sh
# Creates the local Ollama model the app expects (default name: qwen3-vl:4b-cctv).
#   sh tools/setup-model.sh              -> qwen3-vl:4b-cctv
#   sh tools/setup-model.sh my-model     -> a different name (then pick it in Settings)
set -eu

NAME="${1:-qwen3-vl:4b-cctv}"
DIR="$(cd "$(dirname "$0")" && pwd)"

if ! command -v ollama >/dev/null 2>&1; then
  echo "ollama was not found. Install it from https://ollama.com and run this script again." >&2
  exit 1
fi
if ! ollama list >/dev/null 2>&1; then
  echo "Cannot reach the Ollama server. Start it (ollama serve, or open the Ollama app) and run this script again." >&2
  exit 1
fi

echo "Downloading the base model qwen3-vl:4b (a few GB the first time)..."
ollama pull qwen3-vl:4b
echo "Creating $NAME ..."
ollama create "$NAME" -f "$DIR/Modelfile.qwen3-vl-4b-cctv"
echo "Done. Start the app and choose '$NAME' under Settings > Model."