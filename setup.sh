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

echo ">> Overlaying I3C peripherals and tests"
# The overlay mirrors Renode's directory layout, so this drops each file in place.
cp -rv "$HERE/renode-overlay/." "$RENODE_DIR/"

echo ">> Building Renode (headless)"
( cd "$RENODE_DIR" && ./build.sh --no-gui )

echo ">> Running the I3C robot test"
( cd "$RENODE_DIR" && ./renode-test tests/peripherals/I3C.robot )

echo ">> All done."
