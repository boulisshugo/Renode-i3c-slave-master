#!/usr/bin/env bash
#
# Clones Renode, overlays the agnostic I3C master/slave peripherals from this
# repository, builds Renode headless, and runs the I3C robot test.
#
# Environment overrides:
#   RENODE_DIR     - where to place the Renode checkout (default: ./renode)
#   RENODE_REMOTE  - Renode git remote (default: upstream)
#   RENODE_REV     - branch/tag/commit to check out (default: master)
#
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
RENODE_DIR="${RENODE_DIR:-$HERE/renode}"
RENODE_REMOTE="${RENODE_REMOTE:-https://github.com/renode/renode.git}"
RENODE_REV="${RENODE_REV:-master}"

if [ ! -d "$RENODE_DIR/.git" ]; then
    echo ">> Cloning Renode ($RENODE_REV) into $RENODE_DIR"
    git clone "$RENODE_REMOTE" "$RENODE_DIR"
    git -C "$RENODE_DIR" checkout "$RENODE_REV"
    git -C "$RENODE_DIR" submodule update --init --recursive
else
    echo ">> Reusing existing Renode checkout at $RENODE_DIR"
fi

echo ">> Building the firmware (if a RISC-V toolchain is available)"
if command -v riscv64-unknown-elf-gcc >/dev/null 2>&1; then
    "$HERE/firmware/build.sh"
    cp "$HERE/firmware/i3c-firmware.elf" "$HERE/renode-overlay/tests/peripherals/"
else
    echo "   (skipping - using the pre-built firmware/i3c-firmware.elf committed in the repo)"
fi

echo ">> Overlaying I3C peripherals, tests and firmware"
# The overlay mirrors Renode's directory layout, so this drops each file in place.
cp -rv "$HERE/renode-overlay/." "$RENODE_DIR/"

echo ">> Building Renode (headless)"
( cd "$RENODE_DIR" && ./build.sh --no-gui )

echo ">> Building the Java I3C bridge client (if a JDK is available)"
JAVA_SUITE=""
if command -v javac >/dev/null 2>&1; then
    "$HERE/java/build.sh"
    export I3C_JAVA_CP="$HERE/java/out"
    JAVA_SUITE="tests/peripherals/I3C-java.robot"
else
    echo "   (skipping Java bridge build - JDK not found)"
fi

echo ">> Running the I3C robot suites"
( cd "$RENODE_DIR" && ./renode-test \
    tests/peripherals/I3C.robot \
    tests/peripherals/I3C-consistency.robot \
    tests/peripherals/I3C-firmware.robot \
    ${JAVA_SUITE} )

echo ">> All done."
echo ">> For a standalone Java reliability run: java/run-integration.sh"
