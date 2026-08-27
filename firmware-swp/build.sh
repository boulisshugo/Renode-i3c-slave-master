#!/usr/bin/env bash
#
# Cross-compiles the firmware-managed SWP (UICC) firmware into an ELF that Renode can load.
# Requires a RISC-V bare-metal GCC (e.g. gcc-riscv64-unknown-elf, which is multilib and builds rv32).
#
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
CC="${CC:-riscv64-unknown-elf-gcc}"
OUT="${OUT:-$HERE/swp-firmware.elf}"

"$CC" \
    -march=rv32imac -mabi=ilp32 \
    -nostdlib -nostartfiles -ffreestanding \
    -Os -Wall -Wextra \
    -T "$HERE/link.ld" \
    "$HERE/start.S" "$HERE/main.c" \
    -o "$OUT"

echo "Built $OUT"
