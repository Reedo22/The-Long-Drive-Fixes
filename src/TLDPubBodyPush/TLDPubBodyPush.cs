using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TLDPubBodyPush
{
    // Minimal, focused fix for the "push something and it snaps back" bug on stock TLD MP.
    //
    // What's wrong in stock public-branch TLD:
    //   pushablescript.PushLocal() is called whenever ANYTHING applies a push to a pushable —
    //   the player's body bumping into a barrel, a car nudging a sign, falling debris hitting
    //   stuff, etc. But the stock game only broadcasts pushes that originated from the player's
    //   hand-raycast push (the deliberate "shove with hand" action). Every other push is local-
    //   only, so the host's view of the item never sees the impulse, the host's MultiUpd keeps
    //   broadcasting the at-rest position once a second, and the client sees the item snap
    //   back.
    //
    // Fix:
    //   Postfix pushablescript.PushLocal — if we're in MP and the push didn't originate from an
    //   inbound network packet (which we track with a Prefix/Postfix on snItemsync.Push, since
    //   that's where the receive-side handler is), call sns.s.SPush(idInSave, dir, pos) so the
    //   impulse propagates. Both ends then apply the same push to their local copy of the item,
    //   and the host's at-rest broadcast lines up with reality.
    //
    // This is one of two patches ported from v2.x MPPatch (the other being DriverAuthority,
    // not included here — it's worth its own evaluation). Self-contained: doesn't touch claim,
    // doesn't touch matchmaking, doesn't touch transport. Just one missing broadcast call.

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.reedo.tld.pubbodypush";
        public const string PluginName = "TLD Public Body Push Sync";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> CfgEnabled;
        internal static ConfigEntry<bool> CfgVerbose;

        internal static int broadcasts;
        internal static int suppressedAsInbound;

        private void Awake()
        {
            Log = Logger;
            CfgEnabled = Config.Bind("BodyPush", "Enabled", true,
                "Master toggle. When on, pushablescript.PushLocal calls that originate locally " +
                "are broadcast via sns.s.SPush. Stock TLD only broadcasts pushes from the hand-" +
                "raycast push action; body-collision / vehicle-hit / dropped-item pushes never " +
                "reach the other end, which is why pushed items 'snap back' to the host's " +
                "at-rest pose every second.");
            CfgVerbose = Config.Bind("BodyPush", "Verbose", false,
                "Log every broadcast (chatty). Useful for confirming the patch is firing.");

            var harm = new Harmony(PluginGuid);
            try { harm.PatchAll(typeof(InboundPushTrackHook)); }
            catch (Exception ex) { Log.LogError("Failed to patch snItemsync.Push: " + ex.Message); }

            try { harm.PatchAll(typeof(PushLocalBroadcastHook)); }
            catch (Exception ex) { Log.LogError("Failed to patch pushablescript.PushLocal: " + ex.Message); }

            Log.LogInfo("TLD Public Body Push Sync v" + PluginVersion + " loaded. Enabled=" + CfgEnabled.Value);
        }

        // Suppression flag so we don't re-broadcast the same push we just received over the wire.
        // snItemsync.Push() is the inbound handler invoked from the receive dispatcher. We wrap
        // it so anything PushLocal() does inside (which it does — it calls PushLocal to apply the
        // received impulse to our local rigidbody) gets seen as "inbound" and skipped.
        [HarmonyPatch(typeof(snItemsync), "Push")]
        public static class InboundPushTrackHook
        {
            internal static bool inboundActive;

            [HarmonyPrefix]
            public static void Prefix() { inboundActive = true; }

            [HarmonyPostfix]
            public static void Postfix() { inboundActive = false; }
        }

        [HarmonyPatch(typeof(pushablescript), "PushLocal")]
        public static class PushLocalBroadcastHook
        {
            [HarmonyPostfix]
            public static void Postfix(pushablescript __instance, Vector3 _dir, Vector3 _pos)
            {
                if (!CfgEnabled.Value) return;

                // If we're currently processing an inbound network push, the call to PushLocal
                // came from snItemsync.Push, not from local physics. Re-broadcasting would echo.
                if (InboundPushTrackHook.inboundActive)
                {
                    suppressedAsInbound++;
                    return;
                }

                // Single-player guard.
                if (mainscript.M == null || !mainscript.M.multi) return;

                // Need an idInSave to send. tosaveitemscript lives on the pushable or its parent.
                var ts = __instance.GetComponentInParent<tosaveitemscript>();
                if (ts == null) return;

                if (sns.s == null) return;

                try
                {
                    sns.s.SPush(ts.idInSave, _dir, _pos);
                    broadcasts++;
                    if (CfgVerbose.Value)
                        Log.LogInfo("[BodyPush] broadcast id=" + ts.idInSave + " dir=" + _dir + " pos=" + _pos);
                }
                catch (Exception ex)
                {
                    Log.LogWarning("[BodyPush] SPush threw: " + ex.Message);
                }
            }
        }
    }
}
