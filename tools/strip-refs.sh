#!/usr/bin/env bash
#
# Regenerate refs/ from your local 7 Days to Die 3.3 CLIENT install.
#
# The game DLLs the mod compiles against are proprietary, so they can't be
# committed. refs/ holds stripped reference assemblies made by JetBrains
# Refasmer instead: the same public API with every method body replaced by
# `throw null`. CI compiles against them; the game never loads them.
# Same approach as KitsuneCommand (see its docs/REFERENCE-ASSEMBLIES.md).
#
# Use the client install, not the dedicated server: the server build strips
# the UI types (XUiC_*) the mod patches.
#
# Run this when the game version the mod targets changes, or when you add a
# <Reference> to the csproj (add it to REFS below too).
#
# Prereq: dotnet tool install --global JetBrains.Refasmer.CliTool
#
# Usage: bash tools/strip-refs.sh ["<7 Days To Die install dir>"]

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME_DIR="${1:-G:/SteamLibrary/steamapps/common/7 Days To Die}"
MANAGED="$GAME_DIR/7DaysToDie_Data/Managed"
OUT_DIR="$ROOT/refs"

# Source paths for each reference in KitsuneCraftFromContainers.csproj.
REFS=(
    "$MANAGED/Assembly-CSharp.dll"
    "$MANAGED/UnityEngine.dll"
    "$MANAGED/UnityEngine.CoreModule.dll"
    "$MANAGED/LogLibrary.dll"
    "$GAME_DIR/Mods/0_TFP_Harmony/0Harmony.dll"
)

# The refasmer launcher can be blocked by Windows Application Control; the
# tool's own DLL still runs under the signed dotnet host.
run_refasmer() {
    if refasmer --help >/dev/null 2>&1; then
        refasmer "$@"
        return
    fi
    local dll
    dll=$(find "$HOME/.dotnet/tools/.store/jetbrains.refasmer.clitool" -path "*net6.0*" -name RefasmerCliTool.dll 2>/dev/null | head -1)
    if [ -z "$dll" ]; then
        echo "refasmer not found." >&2
        echo "Install: dotnet tool install --global JetBrains.Refasmer.CliTool" >&2
        exit 1
    fi
    DOTNET_ROLL_FORWARD=Major dotnet "$dll" "$@"
}

MISSING=()
for f in "${REFS[@]}"; do
    [ -f "$f" ] || MISSING+=("$f")
done
if [ ${#MISSING[@]} -gt 0 ]; then
    echo "Missing game DLLs:" >&2
    printf '  %s\n' "${MISSING[@]}" >&2
    exit 1
fi

mkdir -p "$OUT_DIR"
run_refasmer --omit-non-api-members=true -O "$OUT_DIR" "${REFS[@]}"

echo ""
printf "  %-30s  %10s  %10s\n" "name" "original" "stripped"
for f in "${REFS[@]}"; do
    name=$(basename "$f")
    printf "  %-30s  %10d  %10d\n" "$name" "$(stat -c %s "$f")" "$(stat -c %s "$OUT_DIR/$name")"
done
echo ""
echo "Wrote ${#REFS[@]} stripped DLLs to $OUT_DIR. Review the diff and commit."
