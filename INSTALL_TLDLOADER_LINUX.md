# TLDLoader on Linux (via Proton) — install notes

Confirmed working as of 2026-05-26. Lets you run TLDLoader-format mods (M-ultiTool,
KolbenLP workshop mods, etc.) on a Proton-launched The Long Drive — the official
`TLDworkshop.exe` installer doesn't realize a Linux install is even possible, so
this is the replacement.

## What it does

Replicates exactly what `TLDworkshop.exe` does on Windows, but driven from a
.NET console app you run with `dotnet run` — no Wine GUI required.

1. Backs up `Assembly-CSharp.dll` next to itself as `.pre-tldloader.bak`.
2. Cecil-patches `mainmenuscript.Start` to call `TLDLoader.ModLoader.InitMainMenu`.
3. Cecil-patches `itemdatabase.Awake` to call `TLDLoader.ModLoader.dbInit`.
4. Copies `TLDLoader.dll`, `Mono.Cecil.dll`, `0Harmony.dll` into the game's
   `TheLongDrive_Data/Managed/` (the last two are sourced from your local
   `BepInEx/core/`).
5. Extracts `TLDPatcher.zip` (the asset bundles) to
   `<proton-prefix>/.../Documents/TheLongDrive/Mods/Assets/`.
6. Creates the `Mods/` and `Mods/Config/Mod Settings/` folders inside the
   Proton prefix.
7. Drops `M-ultiTool.dll` + its Config (if found at the configured path).

The Proton prefix path on Linux is automatically resolved from the AppID:

```
~/.local/share/Steam/steamapps/compatdata/<AppID>/pfx/drive_c/users/steamuser/Documents/TheLongDrive/...
```

## Prerequisites

- `dotnet` SDK (any reasonably recent version — net8.0 is the target).
- BepInEx already installed in the game (the installer copies Cecil + Harmony
  out of `BepInEx/core/`, so make sure that's there first).
- Fetched ahead of time:
  - `TLDLoader.dll` at `/tmp/tldloader/TLDLoader.dll`
  - `TLDPatcher.zip` at `/tmp/tldpatcher/TLDPatcher.zip`

  Both come from the KolbenLP gitlab workshop repo:

  ```
  mkdir -p /tmp/tldloader /tmp/tldpatcher
  curl -fsSL 'https://gitlab.com/KolbenLP/WorkshopTLDMods/-/raw/WorkshopDatabase8.6/Workshop/TLDLoader.dll' -o /tmp/tldloader/TLDLoader.dll
  curl -fsSL 'https://gitlab.com/KolbenLP/WorkshopTLDMods/-/raw/WorkshopDatabase8.6/Workshop/TLDPatcher.zip' -o /tmp/tldpatcher/TLDPatcher.zip
  ```

- (optional) M-ultiTool at the path you pass via `--m-ulti-tool`. Default:
  `/home/reedo/Downloads/M-ultiTool_v4.0.1/`.

## Run it

```
cd src/tld_install_tldloader
dotnet run -- "<game install dir>" <Proton AppID>
```

Examples:

```
# main public install
dotnet run -- "/home/$USER/.local/share/Steam/steamapps/common/The Long Drive" 2920400

# the secondary 'Public' branch install you use for local two-instance MP testing
dotnet run -- "/home/$USER/.local/share/Steam/steamapps/common/The Long Drive Public" 2963147735

# skip dropping M-ultiTool
dotnet run -- "<dir>" <appid> --skip-multitool

# point at a custom M-ultiTool dir
dotnet run -- "<dir>" <appid> --m-ulti-tool /path/to/M-ultiTool_v4.0.1
```

## Reverting

Two options:

- Re-run with `--restore-backup` flag — *(not yet implemented; just copy the
  `.bak` back by hand)*
- `cp TheLongDrive_Data/Managed/Assembly-CSharp.dll.pre-tldloader.bak TheLongDrive_Data/Managed/Assembly-CSharp.dll`
  then delete `TLDLoader.dll`, `Mono.Cecil.dll`, `0Harmony.dll` from Managed if
  you also want to remove TLDLoader entirely (BepInEx-side mods will keep
  working without it).

## Coexistence with BepInEx

Confirmed working side by side. BepInEx hooks via `winhttp.dll` doorstop into
Unity engine load; TLDLoader is loaded by injected `Call` instructions in
game-managed scripts. They operate at different layers and don't fight over
the assembly load order. Both their Harmony plugins coexist (each uses its
own `Harmony(id)` instance, no patch collisions in practice).

## Files

| Path | What |
|---|---|
| `src/tld_install_tldloader/Program.cs` | the installer |
| `src/tld_install_tldloader/tld_install_tldloader.csproj` | net8.0 project, deps on Mono.Cecil |

Idempotent: if the patches are already in `Assembly-CSharp.dll` (we detect by
inspecting the first IL instruction of the target method), re-running just
copies missing files and ensures the Mods folders exist.
