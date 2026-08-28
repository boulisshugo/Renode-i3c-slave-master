#!/usr/bin/env bash
#
# Compiles the standalone SWP protocol reference and checks it against golden vectors and a
# round-trip fuzz. Nothing here is part of the Renode peripherals - see README.md.
#
# Needs the Mono C# compiler:  apt-get install -y mono-mcs mono-runtime
#
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${TMPDIR:-/tmp}/swp-reference-selftest.exe"

if ! command -v mcs >/dev/null 2>&1 || ! command -v mono >/dev/null 2>&1; then
    echo "mcs/mono not found - install them with: apt-get install -y mono-mcs mono-runtime" >&2
    exit 1
fi

# Misc.HexStringToByteArray / PrettyPrintCollectionHex are the only Renode API this reference uses.
mcs -langversion:latest -out:"$OUT" -nowarn:0169,0414,0067 \
    "$HERE/../swp-selftest/RenodeStubs.cs" \
    "$HERE/SWPFrame.cs" "$HERE/SWPProtocol.cs" "$HERE/ReferenceSelfTest.cs"

exec mono "$OUT"
