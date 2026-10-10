#!/usr/bin/env bash
#
# Packages the built plugin into Smoothie_<version>.zip next to this script.
#
# Usage:
#   ./package.sh
#
# SPT_PATH comes from .env next to this script (see .env.example), or from the
# environment, which wins over the file.
#
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Read KEY=VALUE out of .env without executing it. Anything already set in the
# environment wins, so SPT_PATH=... ./package.sh still overrides the file.
ENV_FILE="${ENV_FILE:-$HERE/.env}"
if [[ -f "$ENV_FILE" ]]; then
    while IFS= read -r line || [[ -n "$line" ]]; do
        [[ "$line" =~ ^[[:space:]]*(#|$) ]] && continue
        [[ "$line" =~ ^[[:space:]]*(export[[:space:]]+)?([A-Za-z_][A-Za-z0-9_]*)[[:space:]]*=(.*)$ ]] || continue

        key="${BASH_REMATCH[2]}"
        value="${BASH_REMATCH[3]}"
        value="${value#"${value%%[![:space:]]*}"}"
        value="${value%"${value##*[![:space:]]}"}"
        if [[ ${#value} -ge 2 && ( "$value" == \"*\" || "$value" == \'*\' ) ]]; then
            value="${value:1:${#value}-2}"
        fi

        [[ -n "${!key:-}" ]] || export "$key=$value"
    done < "$ENV_FILE"
fi

if [[ -z "${SPT_PATH:-}" ]]; then
    echo "package.sh: SPT_PATH is not set. Copy .env.example to .env and point it at" >&2
    echo "            your SPT client folder, or export SPT_PATH before running." >&2
    exit 1
fi
PROJECT="$HERE/src/Smoothie"
CONFIGURATION="${CONFIGURATION:-Release}"

# Where the plugin lands when the zip is extracted into the SPT folder.
DEST="${DEST:-BepInEx/plugins/Smoothie}"

VERSION="$(sed -n 's:.*<Version>\([0-9][^<]*\)</Version>.*:\1:p' "$PROJECT/Smoothie.csproj" | head -1)"

if [[ -z "$VERSION" ]]; then
    echo "package.sh: could not read <Version> out of $PROJECT/Smoothie.csproj" >&2
    exit 1
fi

# BepInPlugin needs a compile-time constant, so the version lives in two places.
if ! grep -q "BepInPlugin(.*\"$VERSION\")" "$PROJECT/Plugin.cs"; then
    echo "package.sh: Plugin.cs [BepInPlugin] version doesn't match csproj <Version> $VERSION" >&2
    exit 1
fi

echo "==> building $CONFIGURATION"
dotnet build "$PROJECT" -c "$CONFIGURATION" -v q --nologo

# Ask MSBuild for the path rather than assuming the TFM folder.
DLL="$(dotnet msbuild "$PROJECT" -getProperty:TargetPath -p:Configuration="$CONFIGURATION" -v:q --nologo | tr -d '\r')"

[[ -f "$DLL" ]] || { echo "package.sh: missing $DLL" >&2; exit 1; }

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

mkdir -p "$STAGE/$DEST"
cp "$DLL" "$STAGE/$DEST/"

ZIP="$HERE/Smoothie_${VERSION}.zip"
rm -f "$ZIP"
( cd "$STAGE" && zip -r -q -X "$ZIP" . )

echo "==> $ZIP"
unzip -l "$ZIP" | sed '1,3d;$d'
