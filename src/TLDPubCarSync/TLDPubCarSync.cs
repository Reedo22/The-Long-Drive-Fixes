using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TLDPubCarSync
{
    // Bumps the host's position-broadcast rate for cars from ~1Hz baseline (with 4Hz peak via
    // MorePosUpd while a player's in them) to a configurable target rate (default 10Hz). Only
    // cars get this treatment — every other item stays at the stock 1Hz cadence, so bandwidth
    // cost is bounded by however many cars exist in the world.
    //
    // Why this matters:
    //   Stock TLD broadcasts each item's position+velocity once per second via
    //   itemPlaceRemoveScript.DoUpd -> tosaveitemscript.MultiUpd. For static items that's fine.
    //   For cars in motion it's not — between updates, the receiving end's Unity physics
    //   simulates the car locally using its rigidbody velocity and the carfloats inputs it last
    //   received. That local sim drifts from the authoritative sim because Unity PhysX isn't
    //   bit-deterministic across machines. When the 1-second position update arrives, the car
    //   snaps to the authoritative position. Visually this looks like "sideways / forward
    //   weirdness" — the car momentarily heading in a slightly wrong direction, then correcting.
    //
    //   Bumping the broadcast rate shrinks the gap during which divergence can accumulate.
    //   At 10Hz the gap is 100ms instead of 1000ms; the visible drift is 10x smaller.
    //
    // What we hook:
    //   - tosaveitemscript.PreLoadStuff postfix: detect cars when they're first prepared, attach
    //     a per-car coroutine driver to the same MonoBehaviour. We use PreLoadStuff because
    //     tosaveitemscript has no public Start; the existing PreLoadStuff is called once per
    //     item at spawn time. (If we miss any, we also do a SecondPassOnLoad-style scan in our
    //     own Update tick.)
    //   - The per-car coroutine calls tosaveitemscript.ForcePosUpd(sendPosIfHasParent: false)
    //     at the configured rate, gated by the same condition the stock MultiUpd uses:
    //     (sns.s.lobby.isServer || claimed) && !otherClaimed. So only the authoritative side
    //     broadcasts.
    //
    // Bandwidth optimization:
    //   By default we skip the broadcast when the rigidbody is essentially at rest (velocity
    //   below CfgIdleSpeedThreshold). Stock 1Hz updates still cover the car staying put. This
    //   keeps a parked fleet from each pushing 10 packets/sec for no reason.

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.reedo.tld.pubcarsync";
        public const string PluginName = "TLD Public Car Sync";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> CfgEnabled;
        internal static ConfigEntry<float> CfgRateHz;
        internal static ConfigEntry<float> CfgIdleSpeedThreshold;
        internal static ConfigEntry<bool> CfgAlsoIdle;
        internal static ConfigEntry<bool> CfgVerbose;

        internal static readonly HashSet<int> attachedItems = new HashSet<int>();
        internal static int broadcasts;
        internal static int skippedIdle;

        private void Awake()
        {
            Log = Logger;
            CfgEnabled = Config.Bind("CarSync", "Enabled", true,
                "Master toggle. When on, items with a carscript get position+velocity " +
                "broadcast at the configured rate instead of the stock 1Hz baseline. " +
                "Off = stock behavior (which is what causes the 'car drifts sideways then snaps' " +
                "look on the receiving end).");
            CfgRateHz = Config.Bind("CarSync", "RateHz", 10f,
                "Position broadcast rate per car, in Hz. Range (1, 30). 10 = a snap every 100ms; " +
                "between snaps the receiver's local sim runs for at most 100ms before correction. " +
                "Higher = smoother but more bandwidth.");
            CfgIdleSpeedThreshold = Config.Bind("CarSync", "IdleSpeedThreshold", 0.5f,
                "Linear velocity in m/s below which a car is considered idle and falls back to " +
                "stock 1Hz broadcast cadence. Avoids spending bandwidth on parked vehicles.");
            CfgAlsoIdle = Config.Bind("CarSync", "BroadcastIdleAtRate", false,
                "When true, ignore IdleSpeedThreshold and broadcast every car at full rate even " +
                "when stationary. Useful only for diagnostics — default false saves a lot of " +
                "bandwidth in worlds with many parked cars.");
            CfgVerbose = Config.Bind("CarSync", "Verbose", false,
                "Log when a car attaches our coroutine + per-5-second broadcast summary. Chatty.");

            var harm = new Harmony(PluginGuid);
            try { harm.PatchAll(typeof(PreLoadStuffHook)); }
            catch (Exception ex) { Log.LogError("Failed to patch tosaveitemscript.PreLoadStuff: " + ex.Message); }

            // Backup attacher: scan for cars in a background tick in case some came up before
            // our patch loaded, or were swapped in via M-ultiTool's spawn.
            var worker = new GameObject("TLDPubCarSyncWorker");
            UnityEngine.Object.DontDestroyOnLoad(worker);
            worker.AddComponent<Worker>();

            Log.LogInfo("TLD Public Car Sync v" + PluginVersion + " loaded. Enabled=" + CfgEnabled.Value
                + " RateHz=" + CfgRateHz.Value + " IdleThreshold=" + CfgIdleSpeedThreshold.Value);
        }

        // Per-frame supervisor that scans for new tosaveitemscripts with car attached and
        // ensures our coroutine is running on each.
        internal class Worker : MonoBehaviour
        {
            private float lastScan = -1f;
            private float lastSummary = -1f;

            void Update()
            {
                if (!CfgEnabled.Value) return;
                if (savedatascript.d == null || savedatascript.d.toSaveStuff == null) return;
                float now = Time.realtimeSinceStartup;

                if (now - lastScan > 2f)
                {
                    lastScan = now;
                    foreach (var kv in savedatascript.d.toSaveStuff)
                    {
                        var ts = kv.Value;
                        if (ts == null || ts.car == null) continue;
                        if (attachedItems.Contains(kv.Key)) continue;
                        try
                        {
                            ts.StartCoroutine(FastBroadcast(ts));
                            attachedItems.Add(kv.Key);
                            if (CfgVerbose.Value)
                                Log.LogInfo("[CarSync] attached fast-sync to car id=" + kv.Key);
                        }
                        catch (Exception ex)
                        {
                            Log.LogWarning("[CarSync] attach failed for id=" + kv.Key + ": " + ex.Message);
                        }
                    }
                }

                if (CfgVerbose.Value && now - lastSummary > 5f)
                {
                    lastSummary = now;
                    Log.LogInfo("[CarSync] last 5s: " + broadcasts + " broadcasts, " + skippedIdle + " idle-skipped, cars-attached=" + attachedItems.Count);
                    broadcasts = 0; skippedIdle = 0;
                }
            }
        }

        // Hook on PreLoadStuff fires once at spawn for each tosaveitemscript. If it's a car,
        // attach our broadcaster. This handles the normal-flow case; Worker.Update covers the
        // gaps.
        [HarmonyPatch(typeof(tosaveitemscript), "PreLoadStuff")]
        public static class PreLoadStuffHook
        {
            [HarmonyPostfix]
            public static void Postfix(tosaveitemscript __instance)
            {
                if (!CfgEnabled.Value) return;
                if (__instance == null || __instance.car == null) return;
                if (attachedItems.Contains(__instance.idInSave)) return;
                try
                {
                    __instance.StartCoroutine(FastBroadcast(__instance));
                    attachedItems.Add(__instance.idInSave);
                    if (CfgVerbose.Value)
                        Log.LogInfo("[CarSync] attached fast-sync to car id=" + __instance.idInSave + " (PreLoadStuff)");
                }
                catch (Exception ex)
                {
                    Log.LogWarning("[CarSync] PreLoadStuff attach failed: " + ex.Message);
                }
            }
        }

        // Per-car coroutine: runs forever, broadcasts position at the configured rate when
        // gates pass and the car isn't idle.
        private static IEnumerator FastBroadcast(tosaveitemscript ts)
        {
            while (true)
            {
                float rate = Mathf.Clamp(CfgRateHz.Value, 1f, 30f);
                yield return new WaitForSeconds(1f / rate);

                if (!CfgEnabled.Value) continue;
                if (ts == null || ts.gameObject == null) yield break;
                if (!ts.gameObject.activeInHierarchy) continue;
                if (mainscript.M == null || !mainscript.M.multi) continue;
                if (sns.s == null || sns.s.lobby == null) continue;

                // same gate as stock MultiUpd
                if (!(sns.s.lobby.isServer || ts.claimed)) continue;
                if (ts.otherClaimed) continue;

                // Idle skip — falls back to stock 1Hz coverage for parked cars
                if (!CfgAlsoIdle.Value)
                {
                    var rb = ts.RB ?? ts.GetComponent<Rigidbody>();
                    if (rb != null && !rb.isKinematic)
                    {
                        float t = CfgIdleSpeedThreshold.Value * CfgIdleSpeedThreshold.Value;
                        if (rb.velocity.sqrMagnitude < t && rb.angularVelocity.sqrMagnitude < t)
                        {
                            skippedIdle++;
                            continue;
                        }
                    }
                }

                try { ts.ForcePosUpd(sendPosIfHasParent: false); broadcasts++; }
                catch (Exception ex)
                {
                    if (CfgVerbose.Value)
                        Log.LogWarning("[CarSync] ForcePosUpd threw on id=" + ts.idInSave + ": " + ex.Message);
                }
            }
        }
    }
}
