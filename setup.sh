#!/usr/bin/env bash
#
# Clones Renode, overlays the agnostic I3C, SPI and SWP master/slave peripherals
# from this repository, builds Renode headless, and runs the robot tests.
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
    "$HERE/firmware-spi/build.sh"
    cp "$HERE/firmware-spi/spi-firmware.elf" "$HERE/renode-overlay/tests/peripherals/"
else
    echo "   (skipping - using the pre-built *-firmware.elf committed in the repo)"
fi

echo ">> Overlaying I3C, SPI and SWP peripherals, tests and firmware"
# The overlay mirrors Renode's directory layout, so this drops each file in place.
cp -rv "$HERE/renode-overlay/." "$RENODE_DIR/"

echo ">> Building Renode (headless)"
( cd "$RENODE_DIR" && ./build.sh --no-gui )

echo ">> Building the Java bridge clients (if a JDK is available)"
JAVA_SUITES=""
if command -v javac >/dev/null 2>&1; then
    "$HERE/java/build.sh"
    "$HERE/java-spi/build.sh"
    export I3C_JAVA_CP="$HERE/java/out"
    export I3C_SPI_JAVA_CP="$HERE/java-spi/out"
    JAVA_SUITES="tests/peripherals/I3C-java.robot tests/peripherals/SPI-java.robot"
else
    echo "   (skipping Java bridge build - JDK not found)"
fi

if command -v mcs >/dev/null 2>&1 && command -v mono >/dev/null 2>&1; then
    echo ">> Running the SWP self-tests (transport + protocol reference)"
    "$HERE/tools/swp-selftest/run.sh" >/dev/null
    "$HERE/tools/swp-reference/selftest.sh" >/dev/null
    echo "   both passed"
else
    echo "   (skipping the SWP self-tests - mono-mcs not found)"
fi

echo ">> Running the I3C, SPI and SWP robot suites"
( cd "$RENODE_DIR" && ./renode-test \
    tests/peripherals/I3C.robot \
    tests/peripherals/I3C-consistency.robot \
    tests/peripherals/I3C-firmware.robot \
    tests/peripherals/SPI.robot \
    tests/peripherals/SPI-consistency.robot \
    tests/peripherals/SPI-firmware.robot \
    tests/peripherals/SWP.robot \
    tests/peripherals/SWP-consistency.robot \
    ${JAVA_SUITES} )

echo ">> All done."
echo ">> For standalone Java reliability runs: java/run-integration.sh and java-spi/run-integration.sh"
