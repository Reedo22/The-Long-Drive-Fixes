================================================================
  TLD Performance Patch v2.3
  Performance mod for The Long Drive (Genesz, 2026 beta build)
================================================================

WHAT IT DOES
------------
A BepInEx + Harmony plugin that runtime-patches Assembly-CSharp.dll
to fix 13 classes of performance bugs in The Long Drive. Tested on
build "2024.11.26b_test" (Steam beta channel). 42 method patches
in total. Should also work on the stable branch and most future
versions, since the patches target well-named methods that change
rarely.

Highlights of what it fixes:
  * Per-frame "Not allowed to access mesh" error spam from broken
    Outline shader code (single biggest CPU saving)
  * Per-frame Material clones in rear lights / headlights / radio
    (massive GC pressure on cars in view)
  * Default CollisionDetectionMode set to ContinuousDynamic on
    every spawned rigidbody (the "spawn car frames -> game freezes
    while solving" symptom)
  * Redundant GetComponent calls in player / car / AI / melee
    weapon Update methods (typically 4-12 duplicate lookups per
    frame each)
  * Animator string-based parameter setters (re-hashes every call)
  * The in-game FPS counter snapshotting 1/deltaTime on a single
    frame every half second (jumpy reading)
  * Singleplayer doing the full multiplayer-byte-array serialization
    pipeline for every game event when there are no peers

Reported impact on a single test machine (RTX 4080 SUPER):
  Before patch:   MP with 3 players ~30 fps, GPU at 40%
  After patch:    Solid 144 fps @ 1080p, 112 @ 4K, no log spam,
                  much smoother frame pacing

Results will vary by hardware and content.


HOW TO INSTALL (WINDOWS)
------------------------
1. Close The Long Drive if it's running.
2. Double-click "install_windows.bat".
3. The installer will try to auto-detect your game folder. If it
   can't, it will ask you to paste the path.
4. Done. Launch the game normally.

You can verify the patch loaded by checking that the file
   <game folder>\BepInEx\LogOutput.log
exists after you launch the game. It should contain a banner
that says "TLD Performance Patch v2.3.0 loaded."


HOW TO INSTALL (LINUX + PROTON)
-------------------------------
1. Close the game.
2. Open a terminal in this folder.
3. Run:    ./install_linux.sh
4. Set Steam launch option (see below).

CRITICAL: Linux / Proton users must set this Steam launch option,
otherwise BepInEx will silently not load:

   Steam -> right-click The Long Drive -> Properties ->
   Launch Options -> paste exactly:

       WINEDLLOVERRIDES="winhttp=n,b" %command%

Without this, Proton uses its built-in winhttp.dll instead of the
BepInEx one, and the patch does nothing.

Windows users do NOT need to set a launch option.


RUNTIME CONFIGURATION
---------------------
The plugin has one runtime-toggleable setting:

  ForceReliableSends (default: false)

When TRUE, every multiplayer message uses Steam P2P Reliable mode
(retransmits + ordering). Fixes "cars/items in different positions
on different clients" desync caused by dropped unreliable updates.
Costs more bandwidth. Recommended OFF on fast connections, ON if
you notice position drift.

To toggle:
  Windows: run "tldperfpatch_config.bat"
  Linux:   run "./tldperfpatch_config.sh"

These tools open a small menu to edit the setting. BepInEx
auto-detects the config file change and applies it live — you do
NOT need to restart the game (although a restart works too).

Advanced: you can also edit the file directly:
  <game folder>/BepInEx/config/com.reedo.tld.perfpatch.cfg


HOW TO UNINSTALL
----------------
Run "uninstall_windows.bat" (Windows) or "uninstall_linux.sh"
(Linux). Removes everything the installer added.

You can also just delete these files/folders from your game
directory:
   winhttp.dll
   .doorstop_version
   doorstop_config.ini
   BepInEx/    (the whole folder)


COMPATIBILITY NOTES
-------------------
* Multiplayer: ALL patches are wire-format-compatible with vanilla
  players. You can play with friends who do not have the patch.
* The mod does NOT change game files on disk. Assembly-CSharp.dll
  stays untouched. The patches are applied at runtime by Harmony.
* Steam "Verify integrity of game files" will NOT revert anything,
  because no game files are modified.
* A game update can theoretically break a patch if the developer
  rewrites a patched method, but the worst case is that specific
  patch silently no-ops and you get vanilla behavior. Game will
  still load and run.


CREDITS
-------
BepInEx 5.4.23.5 (LGPL-2.1) - https://github.com/BepInEx/BepInEx
Harmony 2.x (MIT)            - https://github.com/pardeike/Harmony
The Long Drive               - Genesz

This patch ships as-is, with no warranty. The developer of The
Long Drive has no affiliation with this mod.

AUTOMATIC UPDATES (v2.3+)
-------------------------
The plugin can check for newer versions on launch and auto-download
them. Mechanism:

  1. On launch, plugin fetches the URL in UpdateManifestUrl (BepInEx
     config). Manifest format is plain text:
         Line 1: version string (e.g. "2.4.0")
         Line 2: direct download URL for TLDPerfPatch.dll
  2. If the manifest version differs from the installed version, the
     new DLL is downloaded to BepInEx/plugins/TLDPerfPatch.dll.update
     and a flag file TLDPerfPatch.update_pending is written.
  3. On the NEXT game launch, a small BepInEx patcher
     (BepInEx/patchers/TLDPerfPatchUpdater.dll) detects the staged
     update and renames it into place BEFORE the plugin loads.
     (Required because Windows file-locks loaded DLLs.)
  4. The previous version is preserved as TLDPerfPatch.dll.previous
     in case you need to roll back manually.

Defaults:
  AutoUpdate = true   (but disabled in practice because:)
  UpdateManifestUrl = ""   (empty -> no check runs)

To enable auto-updates for your group:
  - Pick somewhere to host two files publicly:
      latest.txt   (the manifest, two lines as above)
      TLDPerfPatch.dll   (the new plugin binary)
    GitHub repo + Releases work great. Raw gist URLs also fine.
  - In tldperfpatch_config.bat, press U to paste the URL, OR edit
    BepInEx/config/com.reedo.tld.perfpatch.cfg and set:
        UpdateManifestUrl = https://your-host/latest.txt
  - Friends running the plugin will auto-update on the launch after
    you push a new version.

To disable update checks entirely, set AutoUpdate = false in the
config (or in the config tool, option 7).
