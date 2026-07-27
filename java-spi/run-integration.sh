#!/usr/bin/env bash
#
# End-to-end reliability run: starts a headless Renode with the firmware-managed SPI slave and the
# forward-on-interrupt TCP bridge, then runs the Java harness which drives the whole chain
# (Java -> TCP bridge -> SPI controller -> firmware slave -> back) and reports reliability/consistency.
#
# Environment overrides: RENODE_DIR, PORT, ITER, SIZE, TIMEOUT_MS
#
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
RENODE_DIR="${RENODE_DIR:-$REPO/renode}"
PORT="${PORT:-33670}"
ITER="${ITER:-500}"
SIZE="${SIZE:-16}"
TIMEOUT_MS="${TIMEOUT_MS:-5000}"

# Prefer the files copied into the Renode tree (by setup.sh); fall back to the repo copies.
REPL="$RENODE_DIR/tests/peripherals/SPI-firmware.repl"
ELF="$RENODE_DIR/tests/peripherals/spi-firmware.elf"
[ -f "$REPL" ] || REPL="$REPO/renode-overlay/tests/peripherals/SPI-firmware.repl"
[ -f "$ELF" ] || ELF="$REPO/firmware-spi/spi-firmware.elf"

"$HERE/build.sh"

RESC="$(mktemp --suffix=.resc)"
FIFO="$(mktemp -u)"
mkfifo "$FIFO"
cat > "$RESC" <<EOF
using sysbus
mach create
machine LoadPlatformDescription @$REPL
sysbus LoadELF @$ELF
emulation CreateSPITCPBridge sysbus.spi 0 $PORT false true
start
EOF

echo ">> Starting Renode (firmware slave + forward-on-interrupt bridge on port $PORT)"
"$RENODE_DIR/renode" --disable-xwt --console -e "include @$RESC" < "$FIFO" &
RENODE_PID=$!
exec 3>"$FIFO"
cleanup() {
    echo "quit" >&3 2>/dev/null || true
    exec 3>&- 2>/dev/null || true
    kill "$RENODE_PID" 2>/dev/null || true
    rm -f "$RESC" "$FIFO"
}
trap cleanup EXIT

echo ">> Waiting for the bridge port to open"
for _ in $(seq 1 120); do
    if (exec 4<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; then
        exec 4>&- 4<&- 2>/dev/null || true
        break
    fi
    sleep 0.5
done

echo ">> Running the Java reliability harness ($ITER iterations, $SIZE bytes each)"
java -cp "$HERE/out" spi.Main 127.0.0.1 "$PORT" "$ITER" "$SIZE" "$TIMEOUT_MS"
