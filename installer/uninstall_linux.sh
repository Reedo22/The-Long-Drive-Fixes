#!/usr/bin/env bash
# Uninstall TLD Public MP. Leaves backups alone.
set -e
DEFAULTS=(
  "$HOME/.local/share/Steam/steamapps/common/The Long Drive Public"
  "$HOME/.local/share/Steam/steamapps/common/The Long Drive"
  "$HOME/.steam/steam/steamapps/common/The Long Drive Public"
  "$HOME/.steam/steam/steamapps/common/The Long Drive"
)
TARGET=""
if [ -n "$1" ]; then TARGET="$1"
else
  for p in "${DEFAULTS[@]}"; do
    if [ -d "$p/BepInEx" ] || [ -f "$p/winhttp.dll" ]; then TARGET="$p" && break; fi
  done
fi
if [ -z "$TARGET" ] || [ ! -f "$TARGET/TheLongDrive.exe" ]; then
  echo "ERROR: could not find TLD install with TLD Public MP installed."
  exit 1
fi
echo "Removing TLD Public MP from: $TARGET"
rm -f "$TARGET/winhttp.dll" "$TARGET/doorstop_config.ini" "$TARGET/.doorstop_version" "$TARGET/steam_appid.txt"
rm -rf "$TARGET/BepInEx"
echo "Done. Backups (if any) in $TARGET/.TLDPublicMP_backup_*"
