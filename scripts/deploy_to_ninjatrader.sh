#!/usr/bin/env bash
# ---------------------------------------------------------------------------
#  Copies the strategy into NinjaTrader 8's Custom folder.
#
#  NinjaTrader compiles every .cs file under bin/Custom recursively, so the
#  FPMR/ subfolders are preserved as-is and become part of the same assembly.
#
#  Usage:
#     ./scripts/deploy_to_ninjatrader.sh
#     NT8_CUSTOM="/some/path/NinjaTrader 8/bin/Custom" ./scripts/deploy_to_ninjatrader.sh
#
#  After copying, open the NinjaScript Editor in NinjaTrader and press F5.
# ---------------------------------------------------------------------------
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$REPO_ROOT/src/NinjaTrader8/Strategies"

NT8_CUSTOM="${NT8_CUSTOM:-$HOME/Documents/NinjaTrader 8/bin/Custom}"
DEST="$NT8_CUSTOM/Strategies"

if [ ! -d "$NT8_CUSTOM" ]; then
	echo "NinjaTrader Custom folder not found: $NT8_CUSTOM" >&2
	echo "Set NT8_CUSTOM to the right path, e.g.:" >&2
	echo "  NT8_CUSTOM='/Users/you/Parallels/Documents/NinjaTrader 8/bin/Custom' $0" >&2
	exit 1
fi

echo "Deploying to: $DEST"
mkdir -p "$DEST"

# Remove a previous FPMR module tree so deleted files do not linger and break the build.
rm -rf "$DEST/FPMR"

cp "$SRC"/FairPriceMeanReversion*.cs "$DEST/"
cp -R "$SRC/FPMR" "$DEST/FPMR"

echo "Copied:"
find "$DEST" -name 'FairPriceMeanReversion*.cs' -o -path '*/FPMR/*.cs' | sed "s|$DEST|  Strategies|"
echo
echo "Now press F5 in the NinjaScript Editor to compile."
