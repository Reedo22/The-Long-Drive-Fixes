================================================================
  TLD Public MP v2.4
  Multiplayer fixes for The Long Drive (public branch)
================================================================

WHAT'S NEW vs v2.3
------------------
Fixes the "client can't keep the engine running" symptom from v2.3.
With v2.3 shipped, real-MP testing surfaced: when a friend (client)
tried to drive a car, the engine would stall every time they pressed
the gas. v2.4 adds the missing piece:

  * TLDPubClaimDrivenCar 0.1.0 (NEW) — when the local player gets in
    a car as driver, calls Claim(true) so this side becomes physics-
    authoritative. GetOut releases. Driving inputs, physics, and
    engine state all run on the same machine — no 200ms round-trip
    that was dropping host's mgas/mbrake/mclutch shadows between
    SCarFloats packets and stalling the engine.

  * TLDPubRemoteCarKinematic 0.5.0 (UPDATED from 0.1.0) — authority
    check now respects tosaveitemscript.otherClaimed. When a client
    claims a car the host is also rendering, host goes kinematic on
    that car too instead of double-simulating and fighting the
    client's authoritative position broadcasts.

v2.3 carry-over (unchanged): TLDPubCarSync at 20Hz host car
broadcast.

WHAT IT DOES
------------
The public branch of The Long Drive ships with multiplayer code and
dev menu intact but hidden by scene-level toggles. This package
installs BepInEx 5 plus twelve gameplay plugins + two dev-only
plugins (off by default) that re-enable Multiplayer, stabilize stock
TLD's MP under network latency, add dev-menu access, and provide an
optional local two-instance testing rig.

Bundled plugins (each is a separate DLL you can disable
individually via its config file in BepInEx/config/):

  TLDMPUnlock                Unlocks Multiplayer button on main menu
  TLDPubMPPatch       1.0.0  Baseline: ForceReliableSends, ForceMultiFlag
  TLDPubMPDiag        1.0.0  Per-msgType packet rate counters (passive)
  TLDPubDevMode       1.2.0  Forces dev menu visible; F4/F8/F3/End/0/` keys
  TLDDirectMP         0.2.0  TCP-fallback transport (off by default)
  TLDPubBodyPush      0.1.0  Pushed-item snap-back fix
  TLDPubPlayerStable  0.1.0  Player-destroy timeout 1.5s -> 5s
  TLDPubFluidDedupe   0.1.0  Kills the fluid-state packet flood
  TLDPubDriverAuthority 0.1.0 Protects driver inputs under latency
  TLDPubCarSync       0.1.0  20Hz host car position broadcast
  TLDPubRemoteCarKinematic 0.5.0 Kinematic remote cars (now also otherClaimed)
  TLDPubClaimDrivenCar 0.1.0 NEW: claim car on get-in (fixes engine-stall)

Dev-only (inert unless you enable [Testing] Enabled = true):

  TLDPubLoopback      0.15.0 Two-instance file-bridge + NetSim + lobby fake
  TLDPubFakeId        1.0.0  SteamID swap for two-instance testing

AUTO-UPDATE
-----------
The bundled TLDPubMPUpdater.dll patcher checks
  https://raw.githubusercontent.com/Reedo22/The-Long-Drive-Fixes/main/public-mp.txt
on every game launch. If newer plugin versions are listed, they
download to `*.dll.update` and apply on the NEXT launch.

HOW TO INSTALL (WINDOWS)
------------------------
1. Close The Long Drive if it's running.
2. Double-click `install_windows.bat`.
3. The installer will auto-detect your TLD install. If it can't
   find it, it will ask you to paste the path.
4. Launch the game via Steam normally.

HOW TO INSTALL (LINUX / PROTON)
-------------------------------
1. Close The Long Drive if it's running.
2. Open a terminal in this folder.
3. Run `./install_linux.sh`.
4. Set Steam launch options for TLD:
     WINEDLLOVERRIDES="winhttp=n,b" %command%
5. Launch the game.

HOW TO UNINSTALL
----------------
Run `uninstall_windows.bat` or `./uninstall_linux.sh`.

ENABLING LOCAL TWO-INSTANCE TESTING (advanced)
----------------------------------------------
Set up TWO separate TLD installs (e.g., a second Steam library
entry). Install this package on both. On both:

  BepInEx/config/com.reedo.tld.publoopback.cfg:
    [Testing]
    Enabled = true
    [Loopback]
    Mode = Host       (or Client on the second instance)

  BepInEx/config/com.reedo.tld.pubfakeid.cfg:
    Enabled = true
    FakeLocalSteamID = 1    (or 2 on the second; any two distinct values)

Always clear /tmp/tld-loopback/ between test sessions — stale
bridge data causes "second attempt always works" symptoms.

CREDITS
-------
Reedo (https://github.com/Reedo22/The-Long-Drive-Fixes)
The Long Drive (c) Genesz
BepInEx (c) Bepis & contributors
