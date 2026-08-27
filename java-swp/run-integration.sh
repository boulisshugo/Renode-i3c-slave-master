#!/usr/bin/env bash
#
# End-to-end reliability run with the protocol layers at BOTH ends outside the models:
#
#   Java (CLF ACT + SHDLC)  ->  LPDU bridge  ->  SimpleSWPController (framing/CRC only)
#      ->  InventedSWPTarget (framing/CRC only)  ->  firmware-swp/main.c (target ACT + SHDLC)
#
# Renode contributes the wire and nothing else: every ACT_SYNC, ACT_POWER_MODE, ACT_READY, RSET, UA
# and sequence number in the run is built either by this Java client or by the C firmware.
#
# Environment overrides: RENODE_DIR, PORT, ITER, SIZE, TIMEOUT_MS
#
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
RENODE_DIR="${RENODE_DIR:-$REPO/renode}"
PORT="${PORT:-33672}"
ITER="${ITER:-300}"
SIZE="${SIZE:-16}"
TIMEOUT_MS="${TIMEOUT_MS:-5000}"

# Prefer the files copied into the Renode tree (by setup.sh); fall back to the repo copies.
REPL="$RENODE_DIR/tests/peripherals/SWP-firmware.repl"
ELF="$RENODE_DIR/tests/peripherals/swp-firmware.elf"
[ -f "$REPL" ] || REPL="$REPO/renode-overlay/tests/peripherals/SWP-firmware.repl"
[ -f "$ELF" ] || ELF="$REPO/firmware-swp/swp-firmware.elf"

"$HERE/build.sh"

RESC="$(mktemp --suffix=.resc)"
FIFO="$(mktemp -u)"
mkfifo "$FIFO"
# PowerUp drives S1 without running any protocol - that is the client's job now. The firmware sends
# its ACT_SYNC as soon as the CPU runs, which is before this client connects, so the client recovers
# it with the frame-resend bit (ClfStack.awaitActSync). That race is the normal case, not a defect.
cat > "$RESC" <<EOF
using sysbus
mach create
machine LoadPlatformDescription @$REPL
sysbus LoadELF @$ELF
emulation CreateSWPLpduBridge sysbus.swp 0 $PORT
swp PowerUp 0
start
EOF

echo ">> Starting Renode (firmware UICC + LPDU bridge on port $PORT)"
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

echo ">> Running the Java CLF harness ($ITER iterations, $SIZE bytes each)"
java -cp "$HERE/out" swp.Main 127.0.0.1 "$PORT" "$ITER" "$SIZE" "$TIMEOUT_MS" -reverse
