#!/usr/bin/env bash
# Builds the plugin zip: Jellyfin.Plugin.Taste.dll + meta.json.
# Runs inside the .NET 10 SDK container unless dotnet 10 is already on PATH (CI).
set -euo pipefail

cd "$(dirname "$0")"
CSPROJ="src/Jellyfin.Plugin.Taste/Jellyfin.Plugin.Taste.csproj"
VERSION="${VERSION:-$(sed -n 's:.*<AssemblyVersion>\(.*\)</AssemblyVersion>.*:\1:p' "$CSPROJ")}"
OWNER="${OWNER:-Bungu5}"
GUID="a405b6d2-6f8d-4f15-bcac-0f4ca3ce59ec"
TARGET_ABI="12.1.0.0"
OUT="dist"

if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "VERSION must look like 1.2.3.4, got '$VERSION'" >&2
    exit 1
fi

if [[ "${1:-}" != "--inside" ]] && ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
    exec docker run --rm \
        -e VERSION="$VERSION" -e OWNER="$OWNER" -e HOST_UID="$(id -u)" -e HOST_GID="$(id -g)" \
        -v "$PWD":/src -v taste-nuget:/root/.nuget -w /src \
        mcr.microsoft.com/dotnet/sdk:10.0 ./build.sh --inside
fi

dotnet test -c Release
dotnet publish "$CSPROJ" -c Release \
    -p:Version="$VERSION" -p:AssemblyVersion="$VERSION" -p:FileVersion="$VERSION" \
    -o "$OUT/publish"

STAGE="$OUT/Taste_$VERSION"
rm -rf "$STAGE" && mkdir -p "$STAGE"
cp "$OUT/publish/Jellyfin.Plugin.Taste.dll" "$STAGE/"
cat > "$STAGE/meta.json" <<EOF
{
  "category": "General",
  "changelog": "See CHANGELOG.md in the source repository.",
  "description": "Asks for stars and a thumb when someone finishes a movie or a series, builds a taste profile per person, and adds recommendation rows to the Jellyfin Web home screen, including titles you can request through Seerr.",
  "guid": "$GUID",
  "name": "Taste",
  "overview": "Finish ratings and personal recommendations",
  "owner": "$OWNER",
  "targetAbi": "$TARGET_ABI",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "$VERSION",
  "status": "Active",
  "autoUpdate": false,
  "assemblies": ["Jellyfin.Plugin.Taste.dll"]
}
EOF

ZIP="$OUT/jellyfin-plugin-taste_$VERSION.zip"
dotnet msbuild build/zip.proj -nologo -v:q -t:Zip -p:Stage="$PWD/$STAGE" -p:ZipFile="$PWD/$ZIP"

if [[ -n "${HOST_UID:-}" ]]; then
    chown -R "$HOST_UID:${HOST_GID:-$HOST_UID}" "$OUT" src tests 2>/dev/null || true
fi

echo "Built $ZIP"
md5sum "$ZIP" 2>/dev/null || true
sha256sum "$ZIP" 2>/dev/null || true
