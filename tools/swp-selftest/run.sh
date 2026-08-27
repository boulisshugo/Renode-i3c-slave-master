#!/usr/bin/env bash
#
# Compiles the SWP models against a small set of Renode API stubs and runs them through the protocol
# scenarios in SWPSelfTest.cs. This does NOT replace the robot suites - it does not exercise Renode
# itself, the .repl loader or the monitor - but it type-checks the real sources and verifies the frame
# codec, both state machines and the hardware/firmware split in a couple of seconds, with no Renode
# checkout and no .NET SDK.
#
# Needs the Mono C# compiler:  apt-get install -y mono-mcs mono-runtime
#
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
SRC="$HERE/../../renode-overlay/src/Infrastructure/src/Emulator"
OUT="${TMPDIR:-/tmp}/swp-selftest.exe"

if ! command -v mcs >/dev/null 2>&1 || ! command -v mono >/dev/null 2>&1; then
    echo "mcs/mono not found - install them with: apt-get install -y mono-mcs mono-runtime" >&2
    exit 1
fi

mcs -langversion:latest -out:"$OUT" -nowarn:0169,0414,0067 \
    "$HERE/RenodeStubs.cs" \
    "$HERE/SWPSelfTest.cs" \
    "$SRC/Main/Peripherals/SWP/ISWPPeripheral.cs" \
    "$SRC/Peripherals/Peripherals/SWP/"*.cs \
    "$SRC/Peripherals/Peripherals/Mocks/DummySWPTarget.cs" \
    "$SRC/Peripherals/Peripherals/Mocks/EchoSWPDevice.cs"

exec mono "$OUT"
