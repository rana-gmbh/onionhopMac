#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
OUTPUT="$ROOT_DIR/onionhop-mac-helper"

swiftc -O "$ROOT_DIR/main.swift" -o "$OUTPUT"
chmod +x "$OUTPUT"

echo "Built $OUTPUT"
