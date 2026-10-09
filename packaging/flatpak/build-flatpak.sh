#!/usr/bin/env bash
#
# Builds the EDNexus Linux artifacts for release:
#   1. A self-contained linux-x64 tarball — the payload the Flathub manifest consumes
#      (Flathub builders have no network, so we publish here rather than `dotnet restore` there).
#   2. A standalone, installable `.flatpak` bundle — a single-file download users sideload with
#      `flatpak install ./EDNexus-<version>.flatpak`. Requires flatpak + flatpak-builder; skipped
#      with a notice if they aren't installed (the tarball is still produced).
#
# Usage:   ./build-flatpak.sh [version]        e.g. ./build-flatpak.sh 0.1.0
# SENTRY_DSN, if exported, is injected into the published app (kept out of the repo).
set -euo pipefail

VERSION="${1:-0.1.0}"
RUNTIME="linux-x64"
APP_ID="io.github.Signal_Thread_LLC.EDNexus"
RUNTIME_VERSION="24.08"
FLATHUB_REPO="https://flathub.org/repo/flathub.flatpakrepo"

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
PUBLISH="$HERE/publish"
OUT="$HERE/out"
MANIFEST="$HERE/$APP_ID.yml"
TARBALL="$OUT/ednexus-${VERSION}-${RUNTIME}.tar.gz"
BUNDLE="$OUT/EDNexus-${VERSION}.flatpak"

rm -rf "$PUBLISH" "$OUT"
mkdir -p "$PUBLISH" "$OUT"

echo "==> Publishing self-contained $RUNTIME build..."
# InvariantGlobalization drops the ICU dependency, keeping the bundle self-contained.
dotnet publish "$ROOT/src/EDNexus.App/EDNexus.App.csproj" \
  --configuration Release \
  --runtime "$RUNTIME" --self-contained true \
  -p:Version="$VERSION" \
  -p:SentryDsn="${SENTRY_DSN:-}" \
  -p:InvariantGlobalization=true \
  --output "$PUBLISH"

echo "==> Creating payload tarball (files at archive root)..."
tar -czf "$TARBALL" -C "$PUBLISH" .
SHA="$(sha256sum "$TARBALL" | cut -d' ' -f1)"

# --- Standalone .flatpak bundle -----------------------------------------------------------
if command -v flatpak-builder >/dev/null 2>&1; then
  echo "==> Building standalone .flatpak bundle..."
  DEV_MANIFEST="$HERE/.dev.$APP_ID.yml"
  REPO="$HERE/repo"
  BUILDDIR="$HERE/build-dir"
  rm -rf "$REPO" "$BUILDDIR" "$DEV_MANIFEST"

  # Build from the freshly published files rather than the release URL (which doesn't exist
  # yet at build time): swap the checksummed `archive` source for a local `dir` source.
  sed -e "s#^\( *\)- type: archive#\1- type: dir#" \
      -e "s#^\( *\)url: .*#\1path: $PUBLISH#" \
      -e "/^ *sha256: /d" \
      "$MANIFEST" > "$DEV_MANIFEST"

  flatpak-builder --force-clean --repo="$REPO" "$BUILDDIR" "$DEV_MANIFEST"
  # --runtime-repo embeds Flathub so `flatpak install` can fetch the platform runtime if the
  # target machine doesn't already have it (Steam Deck ships Flathub preconfigured).
  flatpak build-bundle --runtime-repo="$FLATHUB_REPO" "$REPO" "$BUNDLE" "$APP_ID"
  rm -rf "$REPO" "$BUILDDIR" "$DEV_MANIFEST"
  BUNDLE_BUILT=1
else
  echo "==> flatpak-builder not found — skipping .flatpak bundle (tarball still produced)."
  echo "    Install it on Linux with: sudo apt-get install flatpak-builder"
  BUNDLE_BUILT=0
fi

# --- Summary ------------------------------------------------------------------------------
cat <<EOF

==> Done.
    tarball : $TARBALL
    sha256  : $SHA
EOF
if [ "$BUNDLE_BUILT" = 1 ]; then
  cat <<EOF
    bundle  : $BUNDLE

Install the standalone bundle (Steam Deck / any Flathub-configured system):
  flatpak install --user ./$(basename "$BUNDLE")
  flatpak run $APP_ID
EOF
fi
cat <<EOF

For a Flathub submission, upload the tarball as a GitHub Release asset and set in $APP_ID.yml:
  url:    https://github.com/Signal-Thread/EDNexus/releases/download/v$VERSION/$(basename "$TARBALL")
  sha256: $SHA
EOF
