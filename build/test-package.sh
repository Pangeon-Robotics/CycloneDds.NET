#!/usr/bin/env bash
set -euo pipefail

# ===================================================================================
# build/test-package.sh
#
# PURPOSE:
#   Smoke-test the CycloneDDS.NET NuGet package from a local feed — whether the
#   package was built locally (build/pack.ps1 or dotnet pack) or downloaded from a
#   CI run (the 'nuget-packages' artifact). It always picks the NEWEST matching
#   package by modification time, so a cluttered artifacts/nuget with many
#   historical builds is fine.
#
#   It consumes the package as a real PackageReference via examples/PackageSmokeTest:
#   restore -> run the bundled code generator (idlc) -> publish/subscribe round-trip.
#
# USAGE:
#   build/test-package.sh [FEED_DIR]     # default FEED_DIR = artifacts/nuget
# ===================================================================================

FEED="${1:-artifacts/nuget}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"

if [ ! -d "$FEED" ]; then
    echo "ERROR: feed directory not found: $FEED" >&2
    exit 1
fi
FEED_ABS="$(cd "$FEED" && pwd)"

# Newest file matching a glob (by mtime), or empty.
newest() { ls -t "$FEED_ABS"/$1 2>/dev/null | head -n1 || true; }

RT_PKG="$(newest 'CycloneDDS.NET.[0-9]*.nupkg')"
if [ -z "$RT_PKG" ]; then
    echo "ERROR: no CycloneDDS.NET.<version>.nupkg found in $FEED_ABS" >&2
    exit 1
fi
RT_VER="$(basename "$RT_PKG" | sed -E 's/^CycloneDDS\.NET\.(.+)\.nupkg$/\1/')"

echo "============================================================"
echo "  Package smoke test"
echo "  feed:    $FEED_ABS"
echo "  package: $(basename "$RT_PKG")  (version $RT_VER)"
echo "============================================================"

# Force a fresh restore of exactly this build.
rm -rf "$HOME/.nuget/packages/cyclonedds.net" 2>/dev/null || true
rm -rf "$REPO_ROOT/examples/PackageSmokeTest/bin" "$REPO_ROOT/examples/PackageSmokeTest/obj"

echo ""
echo "Running examples/PackageSmokeTest against $RT_VER ..."
dotnet run --project "$REPO_ROOT/examples/PackageSmokeTest" -c Release \
    -p:SmokePkgVersion="$RT_VER" \
    -p:RestoreAdditionalSources="$FEED_ABS"
echo "  [+] runtime package smoke test PASSED"
echo ""
echo "All good."
