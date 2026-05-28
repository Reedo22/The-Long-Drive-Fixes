#!/usr/bin/env bash
# TLD Public MP — Linux installer (Steam Proton / native).
# Usage:
#   ./install_linux.sh                              (self-updates from github, then installs)
#   ./install_linux.sh /path/to/The\ Long\ Drive    (custom install path)
#   TLDMP_SKIP_UPDATE=1 ./install_linux.sh          (use bundled files only)
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# ---- self-update from github ----
BUNDLED_VERSION="v2.4"
REPO="Reedo22/The-Long-Drive-Fixes"
API="https://api.github.com/repos/$REPO/releases/latest"

if [ -z "$TLDMP_SKIP_UPDATE" ] && [ -z "$TLDMP_UPDATED" ] && command -v curl >/dev/null 2>&1; then
    echo "Checking github for a newer release..."
    LATEST_JSON=$(curl -fsSL --max-time 8 "$API" 2>/dev/null || true)
    if [ -n "$LATEST_JSON" ]; then
        LATEST_TAG=$(echo "$LATEST_JSON" | grep -m1 '"tag_name"' | sed -E 's/.*"tag_name": *"([^"]+)".*/\1/')
        ZIP_URL=$(echo "$LATEST_JSON"   | grep -m1 'browser_download_url' | sed -E 's/.*"browser_download_url": *"([^"]+)".*/\1/')
        if [ -n "$LATEST_TAG" ] && [ "$LATEST_TAG" != "$BUNDLED_VERSION" ] && [ -n "$ZIP_URL" ]; then
            echo "  bundled = $BUNDLED_VERSION, latest = $LATEST_TAG — fetching newer release"
            TMP="$(mktemp -d)"
            if curl -fsSL --max-time 60 -o "$TMP/release.zip" "$ZIP_URL" \
               && command -v unzip >/dev/null 2>&1 \
               && unzip -q "$TMP/release.zip" -d "$TMP" \
               && [ -f "$TMP/install_linux.sh" ]; then
                chmod +x "$TMP/install_linux.sh"
                echo "  re-executing $LATEST_TAG installer..."
                TLDMP_UPDATED=1 exec "$TMP/install_linux.sh" "$@"
            fi
            echo "  update fetch failed, falling back to bundled $BUNDLED_VERSION"
        else
            echo "  bundled $BUNDLED_VERSION is current"
        fi
    else
        echo "  github check skipped (offline or rate-limited)"
    fi
fi

SRC="$SCRIPT_DIR/files"

DEFAULTS=(
  "$HOME/.local/share/Steam/steamapps/common/The Long Drive"
  "$HOME/.steam/steam/steamapps/common/The Long Drive"
)

TARGET=""
if [ -n "$1" ]; then TARGET="$1"
else
  for p in "${DEFAULTS[@]}"; do
    [ -f "$p/TheLongDrive.exe" ] && TARGET="$p" && break
  done
fi

if [ -z "$TARGET" ] || [ ! -f "$TARGET/TheLongDrive.exe" ]; then
  echo "ERROR: could not find TLD install."
  echo "  Pass the install path as the first argument."
  exit 1
fi

echo "Installing into: $TARGET"

# Backup existing
if [ -e "$TARGET/winhttp.dll" ] || [ -d "$TARGET/BepInEx" ]; then
  STAMP="$(date +%Y%m%d_%H%M%S)"
  BACKUP="$TARGET/.TLDPublicMP_backup_$STAMP"
  echo "Existing BepInEx detected — backing up to $BACKUP"
  mkdir -p "$BACKUP"
  [ -e "$TARGET/winhttp.dll" ]            && cp     "$TARGET/winhttp.dll"           "$BACKUP/"
  [ -e "$TARGET/doorstop_config.ini" ]    && cp     "$TARGET/doorstop_config.ini"   "$BACKUP/"
  [ -e "$TARGET/.doorstop_version" ]      && cp     "$TARGET/.doorstop_version"     "$BACKUP/" 2>/dev/null
  [ -e "$TARGET/steam_appid.txt" ]        && cp     "$TARGET/steam_appid.txt"       "$BACKUP/"
  [ -d "$TARGET/BepInEx" ]                && cp -r  "$TARGET/BepInEx"               "$BACKUP/"
fi

echo "Copying BepInEx framework + plugins + patcher..."
cp "$SRC/winhttp.dll"          "$TARGET/"
cp "$SRC/doorstop_config.ini"  "$TARGET/"
cp "$SRC/.doorstop_version"    "$TARGET/" 2>/dev/null || true
cp "$SRC/steam_appid.txt"      "$TARGET/"

mkdir -p "$TARGET/BepInEx/core" "$TARGET/BepInEx/plugins" "$TARGET/BepInEx/patchers" "$TARGET/BepInEx/config"
cp -r "$SRC/BepInEx/core/"*     "$TARGET/BepInEx/core/"
cp    "$SRC/BepInEx/plugins/"*.dll "$TARGET/BepInEx/plugins/"
cp    "$SRC/BepInEx/patchers/"*.dll "$TARGET/BepInEx/patchers/"
# Configs only if not already present (preserve user customizations)
for cfg in "$SRC/BepInEx/config/"*.cfg; do
  base="$(basename "$cfg")"
  if [ ! -f "$TARGET/BepInEx/config/$base" ]; then
    cp "$cfg" "$TARGET/BepInEx/config/"
  fi
done

cat <<EOF

===============================================================
TLD Public MP v2.4 installed.

Launch The Long Drive (public branch) via Steam normally.
The Multiplayer button should now appear on the main menu.

Set Steam launch options for TLD:
    WINEDLLOVERRIDES="winhttp=n,b" %command%

Auto-update is ON. The first launch will check
  https://raw.githubusercontent.com/Reedo22/The-Long-Drive-Fixes/main/public-mp.txt
and stage any newer plugin versions. The NEXT launch applies them.

Gameplay plugins:
  TLDMPUnlock              — re-enables the MP button
  TLDPubMPPatch            — ForceReliableSends + ForceMultiFlag
  TLDPubMPDiag             — packet diagnostic (passive)
  TLDPubDevMode            — dev menu + CapsLock fly + F4/F8/F3/End/0/\`
  TLDDirectMP              — direct-connect fallback (Mode=Off by default)
  TLDPubBodyPush           — fixes pushed-item snap-back
  TLDPubPlayerStable       — 5s player-destroy timeout (was 1.5s)
  TLDPubFluidDedupe        — kills fluid packet spam (~107/s → ~5/s)
  TLDPubDriverAuthority    — protects driver inputs under latency
  TLDPubCarSync            — 20Hz host car broadcast (NEW in v2.4)
  TLDPubRemoteCarKinematic — kinematic remote cars (NEW in v2.4, kills bounce)

Dev-only (inert until you set [Testing] Enabled = true — see README):
  TLDPubLoopback           — local two-instance file-bridge transport
  TLDPubFakeId             — SteamID swap for loopback testing
  TLDPubMPUpdater          — patcher; checks github manifest, swaps .dll.update

Logs: $TARGET/BepInEx/LogOutput.log
Uninstall: ./uninstall_linux.sh
===============================================================
EOF
