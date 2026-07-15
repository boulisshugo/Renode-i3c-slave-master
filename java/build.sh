#!/usr/bin/env bash
#
# Compiles the Java I3C bridge client and reliability harness.
#
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${OUT:-$HERE/out}"

mkdir -p "$OUT"
javac -d "$OUT" "$HERE/src/i3c/I3CBridge.java" "$HERE/src/i3c/Main.java"
echo "Built Java classes into $OUT"
