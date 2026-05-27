using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace TLDPubMPPatch
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.reedo.tld.pubmppatch";
        public const string PluginName = "TLD Public MP Patch";
        public const string PluginVersion = "2.11.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool>  CfgForceReliable;
        internal static ConfigEntry<bool>  CfgForceMultiFlag;
        internal static ConfigEntry<bool>  CfgDedupePosUpd;
        internal static ConfigEntry<bool>  CfgDedupeVelocity;
        internal static ConfigEntry<bool>  CfgDedupeRequestData;
        internal static ConfigEntry<float> CfgRequestDataTimeoutSec;
        internal static ConfigEntry<bool>  CfgDedupeCarFloats;
        internal static ConfigEntry<bool>  CfgDedupeCarIgnition;
        internal static ConfigEntry<bool>  CfgDedupeCarGear;
        internal static ConfigEntry<bool>  CfgDriverAuthority;
        internal static ConfigEntry<bool>  CfgSyncBodyPush;
        internal static ConfigEntry<bool>  CfgDedupeTank;
        internal static ConfigEntry<int>   CfgTankRateLimitMs;
        internal static ConfigEntry<bool>  CfgSmoothRemoteCars;
        internal static ConfigEntry<int>   CfgSmoothDurationMs;
        internal static ConfigEntry<float> CfgSmoothMaxJumpMeters;
        internal static ConfigEntry<bool>  CfgSmoothRemoteItems;
        internal static ConfigEntry<bool>  CfgClaimDrivenCars;
        internal static ConfigEntry<bool>  CfgClaimMovingItems;
        internal static ConfigEntry<float> CfgClaimRestSec;
        internal static ConfigEntry<float> CfgClaimVelThreshold;
        internal static ConfigEntry<bool>  CfgVerbose;

        internal static bool announcedMultiFlip;

        internal static int posSkips;
        internal static int velSkips;
        internal static int reqSkips;
        internal static int carFloatSkips;
        internal static int carIgnSkips;
        internal static int carGearSkips;
        internal static int tankSkips;
        internal static float lastSummaryTime;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("TLD Public MP Patch v" + PluginVersion + " loading...");

            CfgForceReliable = Config.Bind("Multiplayer", "ForceReliableSends", false,
                "Upgrade Unreliable Steam P2P sends to Reliable. Breaks cross-version play (public + beta). Leave OFF unless both ends are same branch and you've observed packet loss.");
            CfgForceMultiFlag = Config.Bind("Multiplayer", "ForceMultiFlag", true,
                "Set mainscript.M.multi=true whenever joined to a Steam lobby so the client sends its own player state.");
            CfgDedupePosUpd = Config.Bind("BandwidthDedupe", "DedupeItemPosUpd", true,
                "Skip sns.SItemPosUpd calls where position hasn't moved more than snItemsync.syncMinPos and rotation hasn't changed more than syncMinRot.");
            CfgDedupeVelocity = Config.Bind("BandwidthDedupe", "DedupeItemVelocityUpd", true,
                "Skip sns.SItemVelocityUpd calls where velocity/angularVelocity unchanged within a float epsilon.");
            CfgDedupeRequestData = Config.Bind("BandwidthDedupe", "DedupeRequestData", true,
                "Skip sns.SRequestData calls for the same key within RequestDataTimeoutSec.");
            CfgRequestDataTimeoutSec = Config.Bind("BandwidthDedupe", "RequestDataTimeoutSec", 3f,
                "Suppression window for SRequestData dedupe. Range (0.5, 10).");
            CfgDedupeCarFloats = Config.Bind("BandwidthDedupe", "DedupeCarFloats", true,
                "Skip sns.SCarFloats when all 6 control floats are within 0.005 of the previous send.");
            CfgDedupeCarIgnition = Config.Bind("BandwidthDedupe", "DedupeCarIgnition", true,
                "Skip sns.SCarIgnition when (ignition,starting) unchanged and rpm within 10.");
            CfgDedupeCarGear = Config.Bind("BandwidthDedupe", "DedupeCarGear", true,
                "Skip sns.SCarGear when gear unchanged.");
            CfgDedupeTank = Config.Bind("BandwidthDedupe", "DedupeTank", true,
                "Skip sns.STank when (fluid type, amount) snapshot unchanged vs last send for same (itemid, tankid).");
            CfgTankRateLimitMs = Config.Bind("BandwidthDedupe", "TankRateLimitMs", 250,
                "Max sns.STank rate per (itemid, tankid) pair. 0=disabled. 250ms = 4Hz.");
            CfgDriverAuthority = Config.Bind("Multiplayer", "DriverAuthority", true,
                "Fix carscript.UpdMulti so local driver inputs aren't overwritten by echoed packets. Skips the merge entirely when driving||driving2.");
            CfgSyncBodyPush = Config.Bind("Multiplayer", "SyncBodyPush", true,
                "Broadcast pushablescript.PushLocal calls (stock public only broadcasts pushes from the hand-raycast).");
            CfgSmoothRemoteCars = Config.Bind("Smoothing", "SmoothRemoteCars", true,
                "Lerp remote cars between received position updates instead of snapping.");
            CfgSmoothDurationMs = Config.Bind("Smoothing", "SmoothDurationMs", 250,
                "Lerp window after each position snap, in ms. Should match the host's broadcast interval (4Hz=250ms).");
            CfgSmoothMaxJumpMeters = Config.Bind("Smoothing", "SmoothMaxJumpMeters", 30f,
                "Skip smoothing when position delta exceeds this many meters (treat as teleport).");
            CfgSmoothRemoteItems = Config.Bind("Smoothing", "SmoothRemoteItems", true,
                "Also lerp non-car remote items (loose props, pushable bodies). May look strange on briefly-held items; disable to A/B test.");
            CfgClaimDrivenCars = Config.Bind("Multiplayer", "ClaimDrivenCars", false,
                "EXPERIMENTAL. When entering a car as a client, claim its tosaveitemscript so OUR position broadcasts win and the host's view tracks our local sim. Eliminates snap-on-exit but causes host-side suspension oscillation on the claimed car (host's wheel rigidbodies are independent of the body lerp). Default OFF in 2.11 pending a fix. Snap-on-exit is the lesser evil.");
            CfgClaimMovingItems = Config.Bind("Multiplayer", "ClaimMovingItems", false,
                "EXPERIMENTAL. When a free item is being pushed around on the client, claim it so OUR position broadcasts win. Default OFF in 2.11 because v2.10's version also claimed wheels/parts (any tosaveitemscript with high angular velocity) and broke car physics. v2.11 fixes the wheel skip but still defaults OFF until A/B-validated.");
            CfgClaimRestSec = Config.Bind("Multiplayer", "ClaimRestSec", 2.5f,
                "Seconds of continuous at-rest state before a transiently-claimed item is released. Range (0.5, 10). Too short = item bounces back when physics is still settling. Too long = item stays under client authority unnecessarily.");
            CfgClaimVelThreshold = Config.Bind("Multiplayer", "ClaimVelThreshold", 0.2f,
                "Linear velocity magnitude in m/s above which an item counts as 'moving' for ClaimMovingItems. Angular velocity threshold is the same value in rad/s. Resting items have tiny non-zero velocities from physics jitter.");
            CfgVerbose = Config.Bind("Multiplayer", "VerboseLogging", false,
                "Log every upgraded send + periodic dedupe summaries (chatty).");

            var harm = new Harmony(PluginGuid);
            harm.PatchAll(typeof(SendP2PPacketHook));
            harm.PatchAll(typeof(MultiFlagHook));
            harm.PatchAll(typeof(ItemPosUpdDedupeHook));
            harm.PatchAll(typeof(ItemVelocityUpdDedupeHook));
            harm.PatchAll(typeof(RequestDataDedupeHook));
            harm.PatchAll(typeof(CarFloatsDedupeHook));
            harm.PatchAll(typeof(CarIgnitionDedupeHook));
            harm.PatchAll(typeof(CarGearDedupeHook));
            harm.PatchAll(typeof(CarUpdMultiDriverAuthorityHook));
            harm.PatchAll(typeof(InboundPushTrackHook));
            harm.PatchAll(typeof(PushLocalBroadcastHook));
            harm.PatchAll(typeof(TankDedupeHook));
            harm.PatchAll(typeof(RemoteCarSmoothingHook));
            harm.PatchAll(typeof(GetInClaimHook));
            harm.PatchAll(typeof(GetOutReleaseHook));
            harm.PatchAll(typeof(ClaimMovingItemsHook));

            var worker = new GameObject("TLDPubMPPatchSmoothWorker");
            UnityEngine.Object.DontDestroyOnLoad(worker);
            worker.AddComponent<SmoothingWorker>();

            Log.LogInfo("TLD Public MP Patch v" + PluginVersion + " loaded."
                + " ForceReliable=" + CfgForceReliable.Value
                + " DriverAuth=" + CfgDriverAuthority.Value
                + " ClaimDriven=" + CfgClaimDrivenCars.Value
                + " ClaimItems=" + CfgClaimMovingItems.Value
                + " SmoothCars=" + CfgSmoothRemoteCars.Value
                + " SmoothItems=" + CfgSmoothRemoteItems.Value);
        }

        internal static void MaybeLogSummary()
        {
            if (!CfgVerbose.Value) return;
            float t = Time.realtimeSinceStartup;
            if (t - lastSummaryTime < 30f) return;
            lastSummaryTime = t;
            int total = posSkips + velSkips + reqSkips + carFloatSkips + carIgnSkips + carGearSkips + tankSkips;
            if (total == 0) return;
            Log.LogInfo("[MPPatch] dedupe skips in last 30s — pos:" + posSkips + " vel:" + velSkips + " req:" + reqSkips
                + " carF:" + carFloatSkips + " carI:" + carIgnSkips + " carG:" + carGearSkips + " tank:" + tankSkips);
            posSkips = velSkips = reqSkips = carFloatSkips = carIgnSkips = carGearSkips = tankSkips = 0;
        }
    }

    // ----- P2P upgrade -----
    [HarmonyPatch(typeof(SteamNetworking), nameof(SteamNetworking.SendP2PPacket),
        new Type[] { typeof(CSteamID), typeof(byte[]), typeof(uint), typeof(EP2PSend), typeof(int) })]
    public static class SendP2PPacketHook
    {
        [HarmonyPrefix]
        public static void Prefix(CSteamID steamIDRemote, byte[] pubData, uint cubData, ref EP2PSend eP2PSendType, int nChannel)
        {
            if (!Plugin.CfgForceReliable.Value) return;
            if (eP2PSendType == EP2PSend.k_EP2PSendUnreliable || eP2PSendType == EP2PSend.k_EP2PSendUnreliableNoDelay)
                eP2PSendType = EP2PSend.k_EP2PSendReliable;
        }
    }

    // ----- multi flag -----
    [HarmonyPatch(typeof(mainscript), "Update")]
    public static class MultiFlagHook
    {
        [HarmonyPostfix]
        public static void Postfix(mainscript __instance)
        {
            if (!Plugin.CfgForceMultiFlag.Value || __instance == null || __instance.multi) return;
            try
            {
                if (sns.s != null && sns.s.lobby != null && sns.s.lobby.isJoined)
                {
                    __instance.multi = true;
                    if (!Plugin.announcedMultiFlip)
                    {
                        Plugin.announcedMultiFlip = true;
                        Plugin.Log.LogInfo("[MPPatch] joined lobby — flipped mainscript.M.multi=true");
                    }
                }
            } catch { }
        }
    }

    // ----- dedupes -----
    [HarmonyPatch(typeof(sns), "SItemPosUpd")]
    public static class ItemPosUpdDedupeHook
    {
        static readonly Dictionary<int, Vector3> lastPos = new Dictionary<int, Vector3>();
        static readonly Dictionary<int, Vector3> lastRot = new Dictionary<int, Vector3>();

        [HarmonyPrefix]
        public static bool Prefix(int _category, int _type, int _id, Vector3 _pos, Vector3 _rot)
        {
            if (!Plugin.CfgDedupePosUpd.Value) return true;
            float sp = snItemsync.syncMinPos;
            float sr = snItemsync.syncMinRot;
            if (lastPos.TryGetValue(_id, out var pp) && lastRot.TryGetValue(_id, out var pr))
            {
                Vector3 d = _pos - pp;
                if (d.sqrMagnitude < sp * sp
                    && Mathf.Abs(Mathf.DeltaAngle(_rot.x, pr.x)) < sr
                    && Mathf.Abs(Mathf.DeltaAngle(_rot.y, pr.y)) < sr
                    && Mathf.Abs(Mathf.DeltaAngle(_rot.z, pr.z)) < sr)
                {
                    Plugin.posSkips++;
                    Plugin.MaybeLogSummary();
                    return false;
                }
            }
            lastPos[_id] = _pos;
            lastRot[_id] = _rot;
            return true;
        }
    }

    [HarmonyPatch(typeof(sns), "SItemVelocityUpd")]
    public static class ItemVelocityUpdDedupeHook
    {
        static readonly Dictionary<int, Vector3> lastVel = new Dictionary<int, Vector3>();
        static readonly Dictionary<int, Vector3> lastAng = new Dictionary<int, Vector3>();
        const float EPS_SQR = 0.0001f;

        [HarmonyPrefix]
        public static bool Prefix(int _id, Vector3 _velocity, Vector3 _angularVelocity)
        {
            if (!Plugin.CfgDedupeVelocity.Value) return true;
            if (lastVel.TryGetValue(_id, out var v) && lastAng.TryGetValue(_id, out var a))
            {
                if ((_velocity - v).sqrMagnitude < EPS_SQR && (_angularVelocity - a).sqrMagnitude < EPS_SQR)
                {
                    Plugin.velSkips++;
                    Plugin.MaybeLogSummary();
                    return false;
                }
            }
            lastVel[_id] = _velocity;
            lastAng[_id] = _angularVelocity;
            return true;
        }
    }

    [HarmonyPatch(typeof(sns), "SRequestData")]
    public static class RequestDataDedupeHook
    {
        static readonly Dictionary<int, float> lastReq = new Dictionary<int, float>();

        [HarmonyPrefix]
        public static bool Prefix(int _key)
        {
            if (!Plugin.CfgDedupeRequestData.Value) return true;
            float t = Time.realtimeSinceStartup;
            float win = Mathf.Clamp(Plugin.CfgRequestDataTimeoutSec.Value, 0.5f, 10f);
            if (lastReq.TryGetValue(_key, out var prev) && t - prev < win)
            {
                Plugin.reqSkips++;
                Plugin.MaybeLogSummary();
                return false;
            }
            lastReq[_key] = t;
            return true;
        }
    }

    [HarmonyPatch(typeof(sns), "SCarFloats")]
    public static class CarFloatsDedupeHook
    {
        static readonly Dictionary<int, float[]> lastVals = new Dictionary<int, float[]>();
        const float EPS = 0.005f;

        [HarmonyPrefix]
        public static bool Prefix(int carid, float steer, float horn, float gas, float brake, float clutch, float _bikeGas)
        {
            if (!Plugin.CfgDedupeCarFloats.Value) return true;
            if (lastVals.TryGetValue(carid, out var v)
                && Mathf.Abs(steer - v[0]) < EPS
                && Mathf.Abs(horn  - v[1]) < EPS
                && Mathf.Abs(gas   - v[2]) < EPS
                && Mathf.Abs(brake - v[3]) < EPS
                && Mathf.Abs(clutch- v[4]) < EPS
                && Mathf.Abs(_bikeGas - v[5]) < EPS)
            {
                Plugin.carFloatSkips++;
                Plugin.MaybeLogSummary();
                return false;
            }
            lastVals[carid] = new[] { steer, horn, gas, brake, clutch, _bikeGas };
            return true;
        }
    }

    [HarmonyPatch(typeof(sns), "SCarIgnition")]
    public static class CarIgnitionDedupeHook
    {
        struct LI { public bool ignition, start; public float rpm; }
        static readonly Dictionary<int, LI> lastIgn = new Dictionary<int, LI>();
        const float RPM_EPS = 10f;

        [HarmonyPrefix]
        public static bool Prefix(int carid, bool ignition, bool start, float rpm)
        {
            if (!Plugin.CfgDedupeCarIgnition.Value) return true;
            if (lastIgn.TryGetValue(carid, out var v) && v.ignition == ignition && v.start == start && Mathf.Abs(rpm - v.rpm) < RPM_EPS)
            {
                Plugin.carIgnSkips++;
                Plugin.MaybeLogSummary();
                return false;
            }
            lastIgn[carid] = new LI { ignition = ignition, start = start, rpm = rpm };
            return true;
        }
    }

    [HarmonyPatch(typeof(sns), "SCarGear")]
    public static class CarGearDedupeHook
    {
        static readonly Dictionary<int, int> lastGear = new Dictionary<int, int>();

        [HarmonyPrefix]
        public static bool Prefix(int carid, int gear)
        {
            if (!Plugin.CfgDedupeCarGear.Value) return true;
            if (lastGear.TryGetValue(carid, out var v) && v == gear)
            {
                Plugin.carGearSkips++;
                Plugin.MaybeLogSummary();
                return false;
            }
            lastGear[carid] = gear;
            return true;
        }
    }

    // ----- driver authority -----
    [HarmonyPatch(typeof(carscript), "UpdMulti")]
    public static class CarUpdMultiDriverAuthorityHook
    {
        [HarmonyPrefix]
        public static bool Prefix(carscript __instance, bool tolva)
        {
            if (!Plugin.CfgDriverAuthority.Value) return true;
            if (!mainscript.M.multi || !__instance.setid) return true;
            if (!__instance.driving && !__instance.driving2) return true;

            float bikeGas = Traverse.Create(__instance).Field("bikeGas").GetValue<float>();
            sns.s.SCarFloats(__instance.carid, __instance.isteer, __instance.currenthorn,
                             __instance.ithrottle, __instance.ibrake, __instance.iclutch, bikeGas);
            __instance.msteer = 0f;
            __instance.mhorn = 0f;
            __instance.mgas = 0f;
            __instance.mbrake = 0f;
            __instance.mclutch = 0f;
            __instance.mbikeGas = 0f;
            __instance.multiControlling = false;
            return false;
        }
    }

    // ----- body push -----
    [HarmonyPatch(typeof(snItemsync), "Push")]
    public static class InboundPushTrackHook
    {
        internal static bool inboundActive;
        [HarmonyPrefix]  public static void Prefix()  { inboundActive = true; }
        [HarmonyPostfix] public static void Postfix() { inboundActive = false; }
    }

    [HarmonyPatch(typeof(pushablescript), "PushLocal")]
    public static class PushLocalBroadcastHook
    {
        [HarmonyPostfix]
        public static void Postfix(pushablescript __instance, Vector3 _dir, Vector3 _pos)
        {
            if (!Plugin.CfgSyncBodyPush.Value) return;
            if (InboundPushTrackHook.inboundActive) return;
            if (!mainscript.M.multi) return;
            var ts = __instance.GetComponentInParent<tosaveitemscript>();
            if (ts == null) return;
            sns.s.SPush(ts.idInSave, _dir, _pos);
        }
    }

    // ----- tank dedupe -----
    [HarmonyPatch(typeof(sns), "STank", new Type[] { typeof(int), typeof(int), typeof(List<mainscript.fluid>) })]
    public static class TankDedupeHook
    {
        struct FE { public int type; public float amount; }
        static readonly Dictionary<long, FE[]> lastSnap = new Dictionary<long, FE[]>();
        static readonly Dictionary<long, float> lastSendT = new Dictionary<long, float>();
        const float AMT_EPS = 0.005f;

        [HarmonyPrefix]
        public static bool Prefix(int itemid, int tankid, List<mainscript.fluid> fluids)
        {
            if (!Plugin.CfgDedupeTank.Value) return true;
            if (fluids == null) return true;
            long key = ((long)itemid << 32) | (uint)tankid;
            float t = Time.realtimeSinceStartup;
            float rate = Mathf.Clamp(Plugin.CfgTankRateLimitMs.Value, 0, 5000) / 1000f;
            if (rate > 0f && lastSendT.TryGetValue(key, out var prevT) && t - prevT < rate)
            {
                Plugin.tankSkips++;
                Plugin.MaybeLogSummary();
                return false;
            }
            if (lastSnap.TryGetValue(key, out var prev) && prev.Length == fluids.Count)
            {
                bool same = true;
                for (int i = 0; i < fluids.Count; i++)
                {
                    if ((int)fluids[i].type != prev[i].type || Mathf.Abs(fluids[i].amount - prev[i].amount) > AMT_EPS)
                    { same = false; break; }
                }
                if (same)
                {
                    Plugin.tankSkips++;
                    Plugin.MaybeLogSummary();
                    return false;
                }
            }
            var snap = new FE[fluids.Count];
            for (int i = 0; i < fluids.Count; i++)
            {
                snap[i].type = (int)fluids[i].type;
                snap[i].amount = fluids[i].amount;
            }
            lastSnap[key] = snap;
            lastSendT[key] = t;
            return true;
        }
    }

    // ----- smoothing worker -----
    public class SmoothState
    {
        public Transform target;
        public Vector3 fromPos;
        public Quaternion fromRot;
        public Vector3 toPos;
        public Quaternion toRot;
        public float startTime;
        public float duration;
    }

    public class SmoothingWorker : MonoBehaviour
    {
        internal static readonly Dictionary<int, SmoothState> active = new Dictionary<int, SmoothState>();
        internal static readonly Dictionary<int, SmoothState> pending = new Dictionary<int, SmoothState>();

        private void Update()
        {
            if (active.Count == 0) return;
            float t = Time.realtimeSinceStartup;
            List<int> done = null;
            foreach (var kv in active)
            {
                var s = kv.Value;
                if (s == null || s.target == null) { (done ??= new List<int>()).Add(kv.Key); continue; }
                float dt = t - s.startTime;
                if (dt >= s.duration)
                {
                    s.target.position = s.toPos;
                    s.target.rotation = s.toRot;
                    (done ??= new List<int>()).Add(kv.Key);
                }
                else
                {
                    float u = Mathf.Clamp01(dt / s.duration);
                    s.target.position = Vector3.Lerp(s.fromPos, s.toPos, u);
                    s.target.rotation = Quaternion.Slerp(s.fromRot, s.toRot, u);
                }
            }
            if (done != null) foreach (var k in done) active.Remove(k);
        }
    }

    // ----- remote-position smoothing -----
    [HarmonyPatch(typeof(snItemsync), "UpdInWorld")]
    public static class RemoteCarSmoothingHook
    {
        static bool LocalPlayerIsDrivingThisCar(tosaveitemscript tosave)
        {
            if (tosave == null || tosave.car == null) return false;
            var p = mainscript.M?.player;
            if (p == null) return false;
            if (p.Car != tosave.car) return false;
            return tosave.car.driving || tosave.car.driving2;
        }

        [HarmonyPrefix]
        public static bool Prefix(int _id, Vector3 _pos, Vector3 _rot)
        {
            if (savedatascript.d == null || savedatascript.d.toSaveStuff == null) return true;
            if (!savedatascript.d.toSaveStuff.ContainsKey(_id)) return true;

            var tosave = savedatascript.d.toSaveStuff[_id];
            if (tosave == null || tosave.useThisForDistance == null) return true;

            // If WE are driving this car, do NOT apply remote updates — our local sim is authoritative.
            // (Combined with ClaimDrivenCars, the host's sim also tracks us, so this stops being needed
            //  on the other end, but keeping it here defends against a host that ignores our claim.)
            if (LocalPlayerIsDrivingThisCar(tosave)) return false;

            // Skip smoothing if disabled, or for items the user owns/holds.
            bool isCar = tosave.car != null;
            bool wantSmooth = isCar ? Plugin.CfgSmoothRemoteCars.Value : Plugin.CfgSmoothRemoteItems.Value;
            if (!wantSmooth) return true;

            if (tosave.claimed) return true;
            if (tosave.part != null && tosave.part.slot != null) return true;
            if (tosave.mpequipped || tosave.mppickedup) return true;

            SmoothingWorker.pending[_id] = new SmoothState
            {
                target   = tosave.useThisForDistance,
                fromPos  = tosave.useThisForDistance.position,
                fromRot  = tosave.useThisForDistance.rotation,
                startTime= Time.realtimeSinceStartup,
                duration = Mathf.Clamp(Plugin.CfgSmoothDurationMs.Value, 50, 1000) / 1000f
            };
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(int _id, Vector3 _pos, Vector3 _rot)
        {
            if (!SmoothingWorker.pending.TryGetValue(_id, out var s)) return;
            SmoothingWorker.pending.Remove(_id);
            s.toPos = _pos;
            s.toRot = Quaternion.Euler(_rot);
            Vector3 d = s.toPos - s.fromPos;
            float sm = d.sqrMagnitude;
            float jm = Plugin.CfgSmoothMaxJumpMeters.Value;
            if (sm < 0.0004f) return;          // too small to bother smoothing
            if (sm > jm * jm) return;          // treat as teleport — snap
            SmoothingWorker.active[_id] = s;
        }
    }

    // ----- claim car on enter, release on exit (Option A: client position broadcasts to host while driving) -----
    [HarmonyPatch(typeof(fpscontroller), "GetIn")]
    public static class GetInClaimHook
    {
        [HarmonyPostfix]
        public static void Postfix(fpscontroller __instance, seatscript _u)
        {
            try
            {
                if (!Plugin.CfgClaimDrivenCars.Value) return;
                if (!mainscript.M.multi) return;
                if (_u == null || _u.Car == null) return;
                // Only the local player drives — make sure we are the local player.
                if ((object)mainscript.M.player != (object)__instance) return;
                var ts = _u.Car.GetComponent<tosaveitemscript>() ?? _u.Car.GetComponentInParent<tosaveitemscript>();
                if (ts == null) return;
                if (ts.claimed) return;
                ts.Claim(true);
                if (Plugin.CfgVerbose.Value)
                    Plugin.Log.LogInfo("[MPPatch] claimed driven car id=" + ts.idInSave);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[MPPatch] GetIn claim failed: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(fpscontroller), "GetOut")]
    public static class GetOutReleaseHook
    {
        [HarmonyPrefix]
        public static void Prefix(fpscontroller __instance, ref tosaveitemscript __state)
        {
            __state = null;
            try
            {
                if (!Plugin.CfgClaimDrivenCars.Value) return;
                if (!mainscript.M.multi) return;
                if ((object)mainscript.M.player != (object)__instance) return;
                if (__instance.Car == null) return;
                __state = __instance.Car.GetComponent<tosaveitemscript>() ?? __instance.Car.GetComponentInParent<tosaveitemscript>();
            } catch { }
        }

        [HarmonyPostfix]
        public static void Postfix(fpscontroller __instance, tosaveitemscript __state)
        {
            try
            {
                if (__state == null) return;
                if (!__state.claimed) return;
                __state.Claim(false);
                if (Plugin.CfgVerbose.Value)
                    Plugin.Log.LogInfo("[MPPatch] released driven car id=" + __state.idInSave);
            }
            catch (Exception e) { Plugin.Log.LogWarning("[MPPatch] GetOut release failed: " + e.Message); }
        }
    }

    // Auto-claim free items on the client when they're being pushed around locally
    // (player kicks, walks into, body-pushes, etc). The stock public branch only
    // broadcasts position for items that are claimed or for which we're isServer; an
    // unclaimed pushed item never broadcasts from us, while the host keeps broadcasting
    // its at-rest version every 1s — hence the snap-back. Claiming flips the gate so
    // host stops broadcasting (otherClaimed=true on host) and we take over.
    [HarmonyPatch(typeof(tosaveitemscript), "MultiUpd")]
    public static class ClaimMovingItemsHook
    {
        // idInSave -> realtime at which to release the transient claim
        internal static readonly Dictionary<int, float> releaseAt = new Dictionary<int, float>();

        [HarmonyPostfix]
        public static void Postfix(tosaveitemscript __instance)
        {
            try
            {
                if (!Plugin.CfgClaimMovingItems.Value) return;
                if (!mainscript.M.multi) return;
                if (sns.s == null || sns.s.lobby == null) return;
                if (sns.s.lobby.isServer) return;
                if (__instance == null) return;
                if (__instance.otherClaimed) return;
                if (__instance.car != null) return;
                // Skip anything that lives under a car (wheels, parts, attachables). These have their own
                // tosaveitemscripts and rigidbodies, and a spinning wheel's angular velocity easily exceeds
                // the motion threshold — claiming them was the v2.10 regression that caused suspension chaos.
                if (__instance.GetComponentInParent<carscript>() != null) return;
                if (__instance.GetComponent<wheelscript>() != null) return;
                if (__instance.GetComponent<partscript>() != null) return;
                if (__instance.P != null && __instance.P.pickedUp) return;

                var rb = __instance.RB != null ? __instance.RB : __instance.GetComponent<Rigidbody>();
                if (rb == null) return;
                if (rb.isKinematic) return;

                float vel = Plugin.CfgClaimVelThreshold.Value;
                float velSqr = vel * vel;
                bool moving = rb.velocity.sqrMagnitude > velSqr
                           || rb.angularVelocity.sqrMagnitude > velSqr;

                int id = __instance.idInSave;
                float now = Time.realtimeSinceStartup;
                float restSec = Math.Max(0.5f, Math.Min(10f, Plugin.CfgClaimRestSec.Value));

                if (moving)
                {
                    if (!__instance.claimed)
                    {
                        __instance.Claim(true);
                        if (Plugin.CfgVerbose.Value)
                            Plugin.Log.LogInfo("[MPPatch] auto-claimed moving item id=" + id
                                + " v=" + rb.velocity.magnitude.ToString("0.00"));
                    }
                    releaseAt[id] = now + restSec;
                }
                else if (__instance.claimed)
                {
                    if (releaseAt.TryGetValue(id, out float t))
                    {
                        if (now >= t)
                        {
                            __instance.Claim(false);
                            releaseAt.Remove(id);
                            if (Plugin.CfgVerbose.Value)
                                Plugin.Log.LogInfo("[MPPatch] released claimed item id=" + id + " (at rest)");
                        }
                    }
                    else
                    {
                        // We're claimed but our auto-claim didn't set this timer (e.g., we
                        // claimed somewhere else, or the item came to rest before we noticed).
                        // Arm a release timer so we don't hold the claim forever.
                        releaseAt[id] = now + restSec;
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[MPPatch] ClaimMovingItems failed: " + e.Message); }
        }
    }
}
