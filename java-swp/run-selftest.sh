#!/usr/bin/env bash
#
# Fast loop for the Java CLF client: no Renode, no firmware, a couple of seconds.
#
#   1. the LPDU encoding checks (swp.SelfTest), against the same golden values the C# side asserts;
#   2. an end-to-end run of the real ClfStack against tools/fake-uicc.py - an independent Python
#      implementation of the target's ACT and SHDLC layers - covering activation, the frame-resend
#      recovery when the client connects after ACT_SYNC has gone out, and sequenced data transfer
#      across the modulo-8 wrap.
#
# What it does NOT cover: the SWP framing, the CRC, and Renode itself. That is run-integration.sh.
#
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
PORT="${PORT:-33690}"
ITER="${ITER:-200}"
SIZE="${SIZE:-16}"

"$HERE/build.sh"

echo
echo ">> LPDU encoding checks"
java -cp "$HERE/out" swp.SelfTest

run_against_fake() {
    local label="$1"; shift
    local port="$1"; shift
    echo
    echo ">> $label"
    python3 "$HERE/tools/fake-uicc.py" "$port" "$@" &
    local fake_pid=$!
    trap 'kill $fake_pid 2>/dev/null || true' RETURN

    for _ in $(seq 1 50); do
        if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then
            exec 3>&- 3<&- 2>/dev/null || true
            break
        fi
        sleep 0.1
    done

    java -cp "$HERE/out" swp.Main 127.0.0.1 "$port" "$ITER" "$SIZE" 2000 -reverse
    kill $fake_pid 2>/dev/null || true
    wait $fake_pid 2>/dev/null || true
}

# The realistic case: Renode powers S1 at startup, so the target's ACT_SYNC is already gone by the
# time the client connects and the client has to ask for it again with FR = 1.
run_against_fake "Activation after a missed ACT_SYNC (frame-resend recovery)" "$PORT"

# And the case where the client is already listening when the target announces itself.
run_against_fake "Activation with the client already connected" "$((PORT + 1))" --eager

echo
echo ">> Java client self-test complete"
