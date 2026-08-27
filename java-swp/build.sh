#!/usr/bin/env bash
#
# Compiles the Java SWP client: the CLF-side ACT and SHDLC layers, the LPDU bridge transport, the
# reliability harness and the encoding self-test.
#
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${OUT:-$HERE/out}"

mkdir -p "$OUT"
javac -d "$OUT" "$HERE/src/swp/"*.java
echo "Built Java classes into $OUT"
