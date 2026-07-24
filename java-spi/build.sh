#!/usr/bin/env bash
#
# Compiles the Java SPI bridge client and reliability harness.
#
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${OUT:-$HERE/out}"

mkdir -p "$OUT"
javac -d "$OUT" "$HERE/src/spi/SPIBridge.java" "$HERE/src/spi/Main.java"
echo "Built Java classes into $OUT"
