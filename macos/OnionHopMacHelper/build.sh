#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
OUTPUT="$ROOT_DIR/onionhop-mac-helper"
MIN_MACOS="${MIN_MACOS_VERSION:-14.0}"

# Build a universal (arm64 + x86_64) helper so it runs on both Apple Silicon and Intel Macs.
# swiftc compiles one target at a time, so build each slice and lipo them together. Without the
# x86_64 slice, TUN/VPN mode (which shells out to this helper) breaks on Intel Macs.
swiftc -O "$ROOT_DIR/main.swift" -target "arm64-apple-macos${MIN_MACOS}"  -o "$OUTPUT.arm64"
swiftc -O "$ROOT_DIR/main.swift" -target "x86_64-apple-macos${MIN_MACOS}" -o "$OUTPUT.x64"
lipo -create "$OUTPUT.arm64" "$OUTPUT.x64" -output "$OUTPUT"
rm -f "$OUTPUT.arm64" "$OUTPUT.x64"
chmod +x "$OUTPUT"

file "$OUTPUT"
lipo -archs "$OUTPUT"
echo "Built $OUTPUT"
