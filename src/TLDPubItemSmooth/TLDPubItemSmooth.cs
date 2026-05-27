using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TLDPubItemSmooth
{
    // Standalone item-position smoother. Patches snItemsync.UpdInWorld so received remote-item
    // position updates lerp between samples instead of snapping.
    //
    // Why this matters:
    //   Stock TLD broadcasts each top-level item's position once per second (itemPlaceRemoveScript
    //   .DoUpd ticks at 1Hz, calls MultiUpd -> ForcePosUpd), with a brief 4Hz burst when the item
    //   is held or in a car (the MorePosUpd coroutine). The receive-side snItemsync.UpdInWorld
    //   directly snaps `useThisForDistance.position` to the new value — every snap is a visible
    //   teleport on the client. Result: a car driving past on the host's end appears to teleport
    //   in 1-second jumps on the client, even though the host's local sim is perfectly smooth.
    //
    //   Stock TLD already has a SmoothMove coroutine but it only fires when the item is
    //   `mppickedup || mpequipped` — i.e., held by a player. Everything else snaps.
    //
    // What this does:
    //   Prefix on snItemsync.UpdInWorld. When the item is not claimed, not in a slot, has a
    //   parent transform of null on its useThisForDistance (i.e., it's a top-level item — which
    //   matches the only branch in ForcePosUpd that actually sends position over the wire), and
    //   we're in MP, we start a private lerp coroutine that interpolates position + rotation
    //   over CfgSmoothDurationMs (default 1000ms, matching the 1Hz cadence). Subsequent updates
    //   for the same item cancel the running lerp and start a new one targeted at the latest
    //   position. We then return false so the stock snap doesn't also fire.
    //
    //   If the position delta is larger than CfgSmoothMaxJump meters, we let the stock snap
    //   happen — treat it as a teleport / respawn, not interpolatable motion.
    //
    // Self-contained: no claim, no transport, no dedupe. Only changes what happens between the
    // moment a position packet arrives and the next physics frame.

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.reedo.tld.pubitemsmooth";
        public const string PluginName = "TLD Public Item Smooth";
        public const string PluginVersion = "0.3.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> CfgEnabled;
        internal static ConfigEntry<float> CfgSmoothDurationMs;
        internal static ConfigEntry<bool> CfgAdaptive;
        internal static ConfigEntry<float> CfgAdaptiveFraction;
        internal static ConfigEntry<float> CfgAdaptiveMinMs;
        internal static ConfigEntry<float> CfgAdaptiveMaxMs;
        internal static ConfigEntry<bool> CfgExtrapolate;
        internal static ConfigEntry<float> CfgExtrapolateMaxMs;
        internal static ConfigEntry<float> CfgSmoothMaxJump;
        internal static ConfigEntry<bool> CfgVerbose;

        // Per-item running lerp tracking, keyed by idInSave.
        internal static readonly Dictionary<int, ItemLerp> active = new Dictionary<int, ItemLerp>();
        internal static readonly object activeLock = new object();

        // Per-item last update timestamp, used to compute adaptive duration.
        internal static readonly Dictionary<int, float> lastUpdateAt = new Dictionary<int, float>();
        internal static readonly object lastUpdateLock = new object();

        internal static int started;
        internal static int superseded;
        internal static int snapsForBigJump;

        private void Awake()
        {
            Log = Logger;
            CfgEnabled = Config.Bind("Smoothing", "Enabled", true,
                "Master toggle. When on, remote item position/rotation updates lerp between received samples " +
                "over SmoothDurationMs instead of snapping. Off = stock behavior (snaps).");
            CfgSmoothDurationMs = Config.Bind("Smoothing", "SmoothDurationMs", 1000f,
                "Fixed fallback lerp duration in ms. Only used if Adaptive=false. With Adaptive=true (default), " +
                "this is ignored.");
            CfgAdaptive = Config.Bind("Smoothing", "Adaptive", true,
                "When true, each item's lerp duration is computed from the actual interval between received " +
                "position updates for that item, multiplied by AdaptiveFraction. So if updates arrive every " +
                "250ms (held / in-car items), we lerp over ~50ms (= 0.2 * 250). If updates arrive every 1000ms " +
                "(loose items, push cadence), we lerp over ~200ms. This keeps render lag low (you're never more " +
                "than fraction*interval behind) while still smoothing the actual snap.");
            CfgAdaptiveFraction = Config.Bind("Smoothing", "AdaptiveFraction", 0.20f,
                "Fraction of the observed inter-update interval used as the lerp duration. Lower = less render " +
                "lag, more visible 'step then settle' between samples. Higher = smoother but more lag. 0.20 " +
                "means the lerp completes in the first 20%% of the interval, then the item sits still until " +
                "the next update arrives.");
            CfgAdaptiveMinMs = Config.Bind("Smoothing", "AdaptiveMinMs", 60f,
                "Lower bound on lerp duration (ms). Below this we'd be approximating a snap, so keep some " +
                "visible smoothing for the user.");
            CfgAdaptiveMaxMs = Config.Bind("Smoothing", "AdaptiveMaxMs", 400f,
                "Upper bound on lerp duration (ms). Cap to keep things responsive even if the host suddenly " +
                "goes quiet for several seconds.");
            CfgExtrapolate = Config.Bind("Smoothing", "Extrapolate", true,
                "Dead-reckoning. When a position packet arrives, the item's rigidbody.velocity is host's " +
                "last-broadcast velocity (via stock SItemVelocityUpd handler). Stock TLD's local physics has " +
                "been simulating with that velocity since the previous update — so the actual current position " +
                "on the host is ahead of the position in the packet. Lerping to the packet's position pulls " +
                "the item BACKWARDS, looking like a snap-back. With Extrapolate=true we instead lerp toward " +
                "(packet_pos + velocity * dt_extrapolate), so the lerp target is closer to where the host " +
                "actually thinks the item is RIGHT NOW.");
            CfgExtrapolateMaxMs = Config.Bind("Smoothing", "ExtrapolateMaxMs", 1000f,
                "Cap on how far forward to extrapolate (ms). The extrapolation amount is the inter-update " +
                "interval (so we project to roughly where the next update will be), clamped to this max. " +
                "A high cap can over-shoot during sudden velocity changes (collisions); the ensuing snap " +
                "would feel like a bounce. 1000ms covers the 1Hz cadence without being absurd.");
            CfgSmoothMaxJump = Config.Bind("Smoothing", "SmoothMaxJumpMeters", 30f,
                "When the position delta from current to received exceeds this many meters, fall back to a " +
                "snap. Lerping a 100m teleport over 1s would look like a comet streak. 30m covers normal " +
                "vehicle motion at 100+ km/h for one update window.");
            CfgVerbose = Config.Bind("Smoothing", "Verbose", false,
                "Log every lerp start (chatty). Useful to confirm the patch is firing on the items you expect.");

            var harm = new Harmony(PluginGuid);
            try { harm.PatchAll(typeof(UpdInWorldHook)); }
            catch (Exception ex) { Log.LogError("Failed to patch snItemsync.UpdInWorld: " + ex.Message); }

            Log.LogInfo("TLD Public Item Smooth v" + PluginVersion + " loaded. Enabled=" + CfgEnabled.Value
                + " Duration=" + CfgSmoothDurationMs.Value + "ms MaxJump=" + CfgSmoothMaxJump.Value + "m");
        }

        // Per-item lerp state. Holds the running coroutine handle so we can stop it when a newer
        // update arrives.
        internal class ItemLerp
        {
            public Coroutine running;
            public Vector3 targetPos;
            public Quaternion targetRot;
            public float durationSec;
        }

        [HarmonyPatch(typeof(snItemsync), "UpdInWorld")]
        public static class UpdInWorldHook
        {
            [HarmonyPrefix]
            public static bool Prefix(snItemsync __instance, int _id, Vector3 _pos, Vector3 _rot)
            {
                if (!CfgEnabled.Value) return true;
                if (mainscript.M == null || !mainscript.M.multi) return true;
                if (savedatascript.d == null || savedatascript.d.toSaveStuff == null) return true;
                if (!savedatascript.d.toSaveStuff.ContainsKey(_id)) return true;

                var ts = savedatascript.d.toSaveStuff[_id];
                if (ts == null) return true;

                // The original UpdInWorld guard. We only proceed when stock would have applied
                // its snap branch; if stock would route to its own SmoothMove (held items), let it.
                if (ts.claimed) return true;
                if (ts.part != null && ts.part.slot != null) return true;
                if (ts.mpequipped || ts.mppickedup) return true; // stock SmoothMove already handles
                if (ts.useThisForDistance == null) return true;

                // Big-jump bailout (treat as teleport).
                var newPos = _pos;
                var cur = ts.useThisForDistance.position;
                if ((cur - newPos).sqrMagnitude > CfgSmoothMaxJump.Value * CfgSmoothMaxJump.Value)
                {
                    snapsForBigJump++;
                    return true; // let stock snap
                }

                // Compute adaptive duration based on time since last update for this item.
                float now = Time.realtimeSinceStartup;
                float dt = 0f;
                lock (lastUpdateLock)
                {
                    if (lastUpdateAt.TryGetValue(_id, out var prev)) dt = now - prev;
                    lastUpdateAt[_id] = now;
                }

                float durationMs;
                if (CfgAdaptive.Value && dt > 0f)
                {
                    durationMs = dt * 1000f * CfgAdaptiveFraction.Value;
                    if (durationMs < CfgAdaptiveMinMs.Value) durationMs = CfgAdaptiveMinMs.Value;
                    if (durationMs > CfgAdaptiveMaxMs.Value) durationMs = CfgAdaptiveMaxMs.Value;
                }
                else
                {
                    durationMs = CfgSmoothDurationMs.Value;
                }

                // Dead-reckoning: project the received position forward by the inter-update interval
                // (clamped) using the item's rigidbody velocity, which stock TLD has already set to
                // host's last broadcast velocity. The lerp target then approximates where host thinks
                // the item is RIGHT NOW, not where it was when host sent the packet.
                Vector3 effectiveTarget = newPos;
                Vector3 vel = Vector3.zero;
                if (CfgExtrapolate.Value && dt > 0f)
                {
                    try
                    {
                        if (ts.RB != null && !ts.RB.isKinematic) vel = ts.RB.velocity;
                    }
                    catch { }
                    float extrapSec = Mathf.Min(dt, CfgExtrapolateMaxMs.Value / 1000f);
                    effectiveTarget = newPos + vel * extrapSec;
                }

                // Cancel any running lerp for this item and start a new one targeted at the new sample.
                lock (activeLock)
                {
                    if (active.TryGetValue(_id, out var existing))
                    {
                        if (existing.running != null)
                        {
                            try { __instance.StopCoroutine(existing.running); }
                            catch { }
                        }
                        superseded++;
                    }
                    var lerp = new ItemLerp
                    {
                        targetPos = effectiveTarget,
                        targetRot = Quaternion.Euler(_rot),
                        durationSec = durationMs / 1000f
                    };
                    lerp.running = __instance.StartCoroutine(SmoothMoveLoop(__instance, _id, ts, lerp));
                    active[_id] = lerp;
                }

                started++;
                if (CfgVerbose.Value)
                    Log.LogInfo("[Smooth] start id=" + _id + " delta=" + (cur - newPos).magnitude.ToString("0.00")
                        + "m dt=" + (dt * 1000f).ToString("0") + "ms duration=" + durationMs.ToString("0") + "ms"
                        + " vel=" + vel.magnitude.ToString("0.0") + "m/s extrap=" + (effectiveTarget - newPos).magnitude.ToString("0.00") + "m");

                return false; // skip stock snap — we own the position writes from here
            }
        }

        private static IEnumerator SmoothMoveLoop(snItemsync host, int id, tosaveitemscript ts, ItemLerp lerp)
        {
            float duration = Math.Max(0.03f, Math.Min(5f, lerp.durationSec));
            float elapsed = 0f;
            Vector3 startPos;
            Quaternion startRot;
            try
            {
                startPos = ts.useThisForDistance.position;
                startRot = ts.useThisForDistance.rotation;
            }
            catch
            {
                ClearActive(id);
                yield break;
            }

            while (elapsed < duration)
            {
                // Bail if the item or its sync target got destroyed mid-lerp.
                if (ts == null || ts.useThisForDistance == null) { ClearActive(id); yield break; }

                // Bail if state changed under us — claim, pickup, etc. — let stock handlers take over.
                if (ts.claimed || ts.mpequipped || ts.mppickedup) { ClearActive(id); yield break; }

                float t = elapsed / duration;
                // Lerp with unscaledDeltaTime so we don't choke when time scale is paused / slowed.
                try
                {
                    ts.useThisForDistance.position = Vector3.Lerp(startPos, lerp.targetPos, t);
                    ts.useThisForDistance.rotation = Quaternion.Lerp(startRot, lerp.targetRot, t);
                }
                catch { ClearActive(id); yield break; }

                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            // Final snap to exact target so we don't accumulate float error.
            try
            {
                if (ts != null && ts.useThisForDistance != null)
                {
                    ts.useThisForDistance.position = lerp.targetPos;
                    ts.useThisForDistance.rotation = lerp.targetRot;
                }
            }
            catch { }

            ClearActive(id);
        }

        private static void ClearActive(int id)
        {
            lock (activeLock)
            {
                if (active.TryGetValue(id, out var l))
                {
                    active.Remove(id);
                }
            }
        }
    }
}
