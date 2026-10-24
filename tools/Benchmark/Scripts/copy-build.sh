#!/bin/bash
# Usage: copy-build.sh <name>
# Builds tools/Translate (with the engine) in Release and copies its output to $BENCH/bin-<name>,
# so later builds cannot overwrite the binaries being compared.
set -e
source "$(dirname "$0")/common.sh"
dotnet build "$REPO/tools/Translate/Translate.csproj" -c Release | grep -E "rror\(s\)|error"
rm -rf "$BENCH/bin-$1"
cp -r "$REPO/tools/Translate/bin/Release/net10.0" "$BENCH/bin-$1"
echo "$BENCH/bin-$1 from $(git -C "$REPO" rev-parse --short HEAD)$(git -C "$REPO" diff --quiet || echo ' (with uncommitted changes)')"
