using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace TLDPubLoopback
{
    public enum LoopbackMode
    {
        Off,
        Host,
        Client
    }

    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.reedo.tld.publoopback";
        public const string PluginName = "TLD Public Loopback";
        public const string PluginVersion = "0.11.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<LoopbackMode> CfgMode;
        internal static ConfigEntry<string> CfgBridgeDir;
        internal static ConfigEntry<int> CfgPollMs;
        internal static ConfigEntry<ulong> CfgFakePeerSteamID;
        internal static ConfigEntry<bool> CfgForceLobbyState;
        internal static ConfigEntry<bool> CfgForceAskStartStuff;
        internal static ConfigEntry<float> CfgForceAskDelaySec;
        internal static ConfigEntry<bool> CfgSyncSeed;
        internal static ConfigEntry<bool> CfgFakeMatchmaking;
        internal static ConfigEntry<bool> CfgAutoJoin;
        internal static ConfigEntry<ulong> CfgFakeLobbyID;

        private static FileStream outStream;
        private static long inboundPos;
        private static Thread readThread;
        private static volatile bool shuttingDown;

        internal static readonly LinkedList<byte[]> inboundQueue = new LinkedList<byte[]>();
        internal static readonly object inboundLock = new object();

        internal static long sentBytes;
        internal static long recvBytes;
        internal static int sentCount;
        internal static int recvCount;
        internal static float lastSummary;

        internal static volatile float lastPeerActivity = -1f;
        internal const float PEER_STALE_SEC = 6f;

        internal static bool announcedPeerUp;
        internal static bool announcedPeerDown;
        internal static int undersizedPeekCount;
        internal static float lastUndersizedLog;
        internal static float peerUpAt = -1f;
        internal static bool forcedAskStartStuff;

        internal static CSteamID fakePeerID;

        internal static readonly object outLock = new object();
        internal static string outPath;
        private static Thread heartbeatThread;
        private static long lastFileLength;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("TLD Public Loopback v" + PluginVersion + " loading...");

            CfgMode = Config.Bind("Loopback", "Mode", LoopbackMode.Off,
                "Off = passthrough (do nothing). Host or Client = participate in the bridge. " +
                "Set one instance to Host, the other to Client. The 'host' role just designates " +
                "one as isServer=true for the game's MP logic; the transport itself is bidirectional via file bridge.");
            CfgBridgeDir = Config.Bind("Loopback", "BridgeDir", "/tmp/tld-loopback",
                "Filesystem path for the cross-instance file bridge. Must be visible to BOTH Proton instances. " +
                "/tmp and /home/$USER are passthrough-mounted by Steam Linux Runtime so they work; " +
                "the Proton prefix's internal directories do not.");
            CfgPollMs = Config.Bind("Loopback", "PollMs", 20,
                "Receive-side polling interval in ms. Lower = less inbound latency but more CPU. " +
                "20ms is a good default for ~50Hz throughput.");
            CfgFakePeerSteamID = Config.Bind<ulong>("Loopback", "FakePeerSteamID", 76561197960265730uL,
                "Synthetic CSteamID to report as the 'sender' of inbound packets. Pick any value != your real SteamID. " +
                "Both instances should use DIFFERENT values, ideally swapped.");
            CfgForceLobbyState = Config.Bind("Loopback", "ForceLobbyState", true,
                "While Mode != Off, force mainscript.M.multi = true and sns.s.lobby.isJoined = true " +
                "so MP code paths activate without needing a real Steam lobby. Host instance also gets isServer=true.");
            CfgForceAskStartStuff = Config.Bind("Loopback", "ForceAskStartStuff", true,
                "Client only: after the peer goes active, explicitly call sns.s.SAskStartStuff() so the host " +
                "broadcasts initial world state. The stock game does this 20 frames after multi=true, but in " +
                "loopback mode multi may flip true before the peer is reachable, so the askStartstuff goes " +
                "into the void. Re-triggering once the peer is up fixes the empty-world symptom.");
            CfgForceAskDelaySec = Config.Bind("Loopback", "ForceAskDelaySec", 1.5f,
                "How long after peer-up to wait before forcing SAskStartStuff. Gives the host time to finish " +
                "any pending init. Range (0.1, 30).");
            CfgSyncSeed = Config.Bind("Loopback", "SyncSeed", true,
                "Fake the Steam-lobby seed handshake via a shared metadata file in BridgeDir/lobby.json. " +
                "Host writes its mainscript.M.seed when in-world; client reads it while in the main menu and " +
                "pre-fills DFMS.seed so a New Game on the client generates the same procedural terrain as the " +
                "host (= same houses, roads, gas stations). Without this, loopback test mode shows no buildings " +
                "because TLD's MP protocol doesn't broadcast building positions — it assumes both sides have the " +
                "same procedural seed.");
            CfgFakeMatchmaking = Config.Bind("Loopback", "FakeMatchmaking", true,
                "Patch SteamMatchmaking.{GetNumLobbyMembers,GetLobbyMemberByIndex,GetLobbyOwner,GetLobbyData,SetLobbyData} " +
                "so stock TLD code sees a real 2-member lobby instead of an empty Steam matchmaking response. " +
                "Fixes: 'host doesn't see client' (player-list iterations now find the peer), the snl.cs:1339 " +
                "self-message drop check (since _id now lands in the lobby-member set normally), and gives us a " +
                "stock-code path for seed handshake.");
            CfgAutoJoin = Config.Bind("Loopback", "AutoJoin", true,
                "Client only: once peer is up and the host has published a seed via fake lobby data, synthesize a " +
                "LobbyEnter_t and call snl.OnReallyJoin() directly. That runs the stock seed handoff " +
                "(GetLobbyData('seed') → mainmenuscript.PressedJoinLobby → LoadScene). Skips needing the lobby " +
                "browser UI to discover our fake lobby (it never appears in real Steam matchmaking).");
            CfgFakeLobbyID = Config.Bind<ulong>("Loopback", "FakeLobbyID", 109775240999999999uL,
                "Synthetic CSteamID to report as sns.s.lobby.lobbyID. Any non-zero ulong distinct from real " +
                "Steam lobby IDs works — only used as an identifier in stock code paths that take lobbyID as a " +
                "parameter (most of which our patches intercept anyway).");

            fakePeerID = new CSteamID(CfgFakePeerSteamID.Value);

            if (CfgMode.Value != LoopbackMode.Off)
            {
                StartTransport();
                var h = new Harmony(PluginGuid);
                try { h.PatchAll(typeof(SendHook)); }            catch (Exception ex) { Log.LogError("SendHook: " + ex.Message); }
                try { h.PatchAll(typeof(IsAvailableHook)); }     catch (Exception ex) { Log.LogError("IsAvailableHook: " + ex.Message); }
                try { h.PatchAll(typeof(ReadPacketHook)); }      catch (Exception ex) { Log.LogError("ReadPacketHook: " + ex.Message); }

                if (CfgFakeMatchmaking.Value)
                {
                    try { h.PatchAll(typeof(NumLobbyMembersHook)); }    catch (Exception ex) { Log.LogError("NumLobbyMembersHook: " + ex.Message); }
                    try { h.PatchAll(typeof(LobbyMemberByIndexHook)); } catch (Exception ex) { Log.LogError("LobbyMemberByIndexHook: " + ex.Message); }
                    try { h.PatchAll(typeof(LobbyOwnerHook)); }         catch (Exception ex) { Log.LogError("LobbyOwnerHook: " + ex.Message); }
                    try { h.PatchAll(typeof(GetLobbyDataHook)); }       catch (Exception ex) { Log.LogError("GetLobbyDataHook: " + ex.Message); }
                    try { h.PatchAll(typeof(SetLobbyDataHook)); }       catch (Exception ex) { Log.LogError("SetLobbyDataHook: " + ex.Message); }
                    Log.LogInfo("[Loopback] FakeMatchmaking patches applied — SteamMatchmaking calls now see a synthetic 2-member lobby.");
                }
            }

            Log.LogInfo("TLD Public Loopback loaded. Mode=" + CfgMode.Value
                + " BridgeDir=" + CfgBridgeDir.Value
                + " FakePeer=" + CfgFakePeerSteamID.Value);
        }

        private void OnDestroy()
        {
            Log.LogInfo("[Loopback] OnDestroy fired (ignored — threads stay alive for the process)");
        }

        internal static bool IsPeerConnected()
        {
            if (CfgMode.Value == LoopbackMode.Off) return false;
            if (lastPeerActivity < 0f) return false;
            return Time.realtimeSinceStartup - lastPeerActivity < PEER_STALE_SEC;
        }

        private void Update()
        {
            if (CfgMode.Value == LoopbackMode.Off) return;

            bool connected = IsPeerConnected();
            if (connected && !announcedPeerUp)
            {
                announcedPeerUp = true;
                announcedPeerDown = false;
                peerUpAt = Time.realtimeSinceStartup;
                forcedAskStartStuff = false;
                Log.LogInfo("[Loopback] peer ACTIVE — hijacking sns.Send + SteamNetworking.IsP2PPacketAvailable/ReadP2PPacket");
            }
            else if (!connected && announcedPeerUp && !announcedPeerDown)
            {
                announcedPeerDown = true;
                announcedPeerUp = false;
                peerUpAt = -1f;
                forcedAskStartStuff = false;
                Log.LogInfo("[Loopback] peer STALE — passing through to real Steam P2P");
            }

            // Client-only: nudge the host to re-broadcast all item state. The stock game
            // triggers this 20 frames after mainscript.M.multi=true, but our loopback may
            // have flipped multi=true before the peer was reachable, so the askStartstuff
            // packet was lost.
            if (connected && !forcedAskStartStuff
                && CfgForceAskStartStuff.Value
                && CfgMode.Value == LoopbackMode.Client
                && peerUpAt > 0f
                && Time.realtimeSinceStartup - peerUpAt >= Math.Max(0.1f, Math.Min(30f, CfgForceAskDelaySec.Value)))
            {
                try
                {
                    if (sns.s != null)
                    {
                        sns.s.SAskStartStuff();
                        Log.LogInfo("[Loopback] forced sns.s.SAskStartStuff() — host should broadcast initial world state now");
                    }
                    else
                    {
                        Log.LogWarning("[Loopback] sns.s is null, cannot force SAskStartStuff yet");
                    }
                }
                catch (Exception ex)
                {
                    Log.LogWarning("[Loopback] SAskStartStuff threw: " + ex.Message);
                }
                forcedAskStartStuff = true;
            }

            if (connected && CfgForceLobbyState.Value)
            {
                try
                {
                    if (mainscript.M != null) mainscript.M.multi = true;
                    if (sns.s != null && sns.s.lobby != null)
                    {
                        sns.s.lobby.isJoined = true;
                        sns.s.lobby.isServer = (CfgMode.Value == LoopbackMode.Host);
                    }
                }
                catch { }
            }

            if (CfgSyncSeed.Value)
            {
                try { TickLobbyMetadata(); } catch (Exception ex) { Log.LogWarning("[Loopback] lobby-meta tick: " + ex.Message); }
            }

            if (Time.realtimeSinceStartup - lastSummary > 5f)
            {
                lastSummary = Time.realtimeSinceStartup;
                Log.LogInfo("[Loopback] last 5s — sent " + sentCount + " (" + sentBytes + "B)  recv "
                    + recvCount + " (" + recvBytes + "B)   queue=" + InboundQueueDepth());
                sentBytes = recvBytes = 0L;
                sentCount = recvCount = 0;
            }
        }

        private static int InboundQueueDepth()
        {
            lock (inboundLock) { return inboundQueue.Count; }
        }

        private static string JoinPath(string dir, string file)
        {
            if (dir.EndsWith("/") || dir.EndsWith("\\")) return dir + file;
            return dir + "/" + file;
        }

        private static string OutboundPath()
        {
            return JoinPath(CfgBridgeDir.Value, (CfgMode.Value == LoopbackMode.Host ? "host" : "client") + "-out.bin");
        }

        private static string InboundPath()
        {
            return JoinPath(CfgBridgeDir.Value, (CfgMode.Value == LoopbackMode.Host ? "client" : "host") + "-out.bin");
        }

        private static void StartTransport()
        {
            shuttingDown = false;
            try
            {
                Directory.CreateDirectory(CfgBridgeDir.Value);
                outPath = OutboundPath();
                lock (outLock)
                {
                    outStream = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                }
                Log.LogInfo("[Loopback] mode=" + CfgMode.Value + "  bridge=" + CfgBridgeDir.Value);
                Log.LogInfo("[Loopback] our outbound -> " + outPath);
                Log.LogInfo("[Loopback] peer inbound  <- " + InboundPath());
            }
            catch (Exception ex)
            {
                Log.LogError("[Loopback] failed to open outbound file: " + ex.Message);
                return;
            }

            readThread = new Thread(FileReadLoop) { IsBackground = true, Name = "TLDLoopback-Read" };
            readThread.Start();
            heartbeatThread = new Thread(HeartbeatLoop) { IsBackground = true, Name = "TLDLoopback-Heartbeat" };
            heartbeatThread.Start();
        }

        private static void EnsureOutStream()
        {
            lock (outLock)
            {
                if (outStream != null)
                {
                    try { _ = outStream.Position; return; }
                    catch (ObjectDisposedException) { outStream = null; }
                    catch { }
                }
                try
                {
                    Directory.CreateDirectory(CfgBridgeDir.Value);
                    outStream = new FileStream(outPath ?? OutboundPath(), FileMode.Append, FileAccess.Write, FileShare.Read);
                    Log.LogInfo("[Loopback] reopened outbound stream " + (outPath ?? OutboundPath()));
                }
                catch (Exception ex)
                {
                    Log.LogError("[Loopback] reopen failed: " + ex.Message);
                }
            }
        }

        private static void HeartbeatLoop()
        {
            byte[] payload = new byte[1] { 250 };
            while (!shuttingDown)
            {
                SafeSleep(2000);
                if (CfgMode.Value != LoopbackMode.Off) SendOutbound(payload);
            }
        }

        private static void FileReadLoop()
        {
            int ms = Math.Max(5, CfgPollMs.Value);
            byte[] lenBuf = new byte[4];
            string path = InboundPath();
            int iter = 0;
            while (!shuttingDown)
            {
                iter++;
                try
                {
                    if (!File.Exists(path))
                    {
                        if (iter == 1 || iter % 50 == 0)
                            Log.LogInfo("[Loopback] waiting for peer file " + path);
                        SafeSleep(500);
                        continue;
                    }
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        if (fs.Length < lastFileLength)
                        {
                            Log.LogInfo("[Loopback] peer file truncated (was " + lastFileLength + ", now " + fs.Length + ") — resetting read position to 0");
                            inboundPos = 0L;
                            lock (inboundLock) { inboundQueue.Clear(); }
                        }
                        lastFileLength = fs.Length;
                        if (fs.Length <= inboundPos) { SafeSleep(ms); continue; }
                        fs.Seek(inboundPos, SeekOrigin.Begin);
                        while (fs.Position < fs.Length)
                        {
                            long startPos = fs.Position;
                            if (ReadExact(fs, lenBuf, 4) < 4) { fs.Seek(startPos, SeekOrigin.Begin); break; }
                            int len = BitConverter.ToInt32(lenBuf, 0);
                            if (len < 1 || len > 1048576)
                            {
                                Log.LogError("[Loopback] bad inbound length " + len + " at pos " + startPos + " — bridge file corrupted, stopping read");
                                return;
                            }
                            byte[] payload = new byte[len];
                            if (ReadExact(fs, payload, len) < len) { fs.Seek(startPos, SeekOrigin.Begin); break; }
                            inboundPos = fs.Position;
                            lastPeerActivity = Time.realtimeSinceStartup;
                            if (len != 1 || payload[0] != 250)
                            {
                                lock (inboundLock) { inboundQueue.AddLast(payload); }
                                Interlocked.Add(ref recvBytes, len);
                                Interlocked.Increment(ref recvCount);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.LogWarning("[Loopback] read loop transient: " + ex.GetType().Name + ": " + ex.Message);
                    SafeSleep(500);
                }
            }
        }

        private static int ReadExact(FileStream fs, byte[] buf, int count)
        {
            int i;
            for (i = 0; i < count;)
            {
                int n = fs.Read(buf, i, count - i);
                if (n <= 0) break;
                i += n;
            }
            return i;
        }

        private static void SafeSleep(int ms)
        {
            try { Thread.Sleep(ms); } catch (ThreadInterruptedException) { }
        }

        internal static void SendOutbound(byte[] payload)
        {
            EnsureOutStream();
            FileStream fs;
            lock (outLock) { fs = outStream; }
            if (fs == null) return;
            try
            {
                byte[] bytes = BitConverter.GetBytes(payload.Length);
                lock (outLock)
                {
                    fs.Write(bytes, 0, 4);
                    fs.Write(payload, 0, payload.Length);
                    fs.Flush();
                }
                Interlocked.Add(ref sentBytes, payload.Length);
                Interlocked.Increment(ref sentCount);
            }
            catch (ObjectDisposedException)
            {
                lock (outLock) { outStream = null; }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[Loopback] send failed: " + ex.Message);
            }
        }

        // ---- fake lobby (KV map + matchmaking patches) ----
        // TLD MP is built around a Steam lobby (snl.cs): host CreateLobby + SetLobbyData("seed", ...)
        // → client JoinLobby + GetLobbyData("seed") + GetLobbyMemberByIndex iteration. In loopback there
        // IS no real Steam lobby, so all the SteamMatchmaking lookups return empty/zero values. This
        // breaks two things:
        //   1. The seed handshake (snl.OnReallyJoin needs GetLobbyData("seed") to parse to an int) →
        //      client's terrain seed differs from host's → no shared houses.
        //   2. Host's player-list iterations (sns.cs:2189 ForwardMessageU, snl.cs:380 UpdPlayersList) →
        //      GetNumLobbyMembers returns 1 (just host) → host doesn't even know the client exists in
        //      its lobby data, so it never renders the client model.
        //
        // We fake both with a shared KV map persisted to bridge/lobby.json:
        //   - Host writes every lobby data key it Sets, plus its own SteamID (for "lobby owner").
        //   - Client reads on demand and answers GetLobbyData/GetLobbyOwner queries from it.
        //   - GetNumLobbyMembers / GetLobbyMemberByIndex are synthesised based on peer-up state.

        private static readonly Dictionary<string, string> FakeLobbyData = new Dictionary<string, string>();
        private static readonly object FakeLobbyLock = new object();
        private static ulong FakeLobbyHostId;
        private static float lastLobbyWrite = -1f;
        private static float lastLobbyRead = -1f;
        private static long lastLobbyJsonMTime;
        private static bool clientAutoJoined;

        private static string LobbyMetaPath()
        {
            return JoinPath(CfgBridgeDir.Value, "lobby.json");
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            var sb = new System.Text.StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        private static void HostWriteLobbyJson()
        {
            if (CfgMode.Value != LoopbackMode.Host) return;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("{\"hostId\":").Append(FakeLobbyHostId).Append(",\"data\":{");
                lock (FakeLobbyLock)
                {
                    bool first = true;
                    foreach (var kv in FakeLobbyData)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append('"').Append(EscapeJson(kv.Key)).Append("\":\"")
                          .Append(EscapeJson(kv.Value)).Append('"');
                    }
                }
                sb.Append("}}");
                File.WriteAllText(LobbyMetaPath(), sb.ToString());
                lastLobbyWrite = Time.realtimeSinceStartup;
            }
            catch (Exception ex)
            {
                Log.LogWarning("[Loopback] lobby.json write failed: " + ex.Message);
            }
        }

        private static void ClientReadLobbyJson()
        {
            if (CfgMode.Value != LoopbackMode.Client) return;
            string path = LobbyMetaPath();
            if (!File.Exists(path)) return;
            try
            {
                long mtime = File.GetLastWriteTimeUtc(path).Ticks;
                if (mtime == lastLobbyJsonMTime) return;
                lastLobbyJsonMTime = mtime;

                string json = File.ReadAllText(path);

                // Quick-and-dirty parse for {"hostId":N,"data":{"k1":"v1","k2":"v2",...}}
                int hidIdx = json.IndexOf("\"hostId\":");
                if (hidIdx >= 0)
                {
                    int s = hidIdx + 9, e = s;
                    while (e < json.Length && (char.IsDigit(json[e]) || json[e] == '-')) e++;
                    ulong.TryParse(json.Substring(s, e - s), out FakeLobbyHostId);
                }

                int dataIdx = json.IndexOf("\"data\":{");
                if (dataIdx < 0) return;
                int p = dataIdx + 8;
                lock (FakeLobbyLock)
                {
                    FakeLobbyData.Clear();
                    while (p < json.Length)
                    {
                        // skip ws/commas
                        while (p < json.Length && (json[p] == ' ' || json[p] == ',' || json[p] == '\n')) p++;
                        if (p >= json.Length || json[p] == '}') break;
                        if (json[p] != '"') break;
                        p++;
                        int ks = p;
                        while (p < json.Length && json[p] != '"') { if (json[p] == '\\') p++; p++; }
                        string key = JsonUnescape(json.Substring(ks, p - ks));
                        p++; // closing "
                        while (p < json.Length && json[p] != ':') p++; p++;
                        while (p < json.Length && json[p] == ' ') p++;
                        if (p >= json.Length || json[p] != '"') break;
                        p++;
                        int vs = p;
                        while (p < json.Length && json[p] != '"') { if (json[p] == '\\') p++; p++; }
                        string val = JsonUnescape(json.Substring(vs, p - vs));
                        p++; // closing "
                        FakeLobbyData[key] = val;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[Loopback] lobby.json read failed: " + ex.Message);
            }
        }

        private static string JsonUnescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    if (n == '"' || n == '\\' || n == '/') { sb.Append(n); i++; }
                    else if (n == 'n') { sb.Append('\n'); i++; }
                    else if (n == 'r') { sb.Append('\r'); i++; }
                    else if (n == 't') { sb.Append('\t'); i++; }
                    else sb.Append(s[i]);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private static void TickLobbyMetadata()
        {
            float now = Time.realtimeSinceStartup;

            if (CfgMode.Value == LoopbackMode.Host)
            {
                // Capture our own SteamID + seed once available, then write lobby.json on a ~2s cadence.
                if (mainscript.M != null && mainscript.M.seed != 0)
                {
                    string seedStr = mainscript.M.seed.ToString();
                    bool changed = false;
                    lock (FakeLobbyLock)
                    {
                        string cur;
                        if (!FakeLobbyData.TryGetValue("seed", out cur) || cur != seedStr)
                        {
                            FakeLobbyData["seed"] = seedStr;
                            changed = true;
                        }
                    }
                    if (FakeLobbyHostId == 0)
                    {
                        try { FakeLobbyHostId = SteamUser.GetSteamID().m_SteamID; changed = true; } catch { }
                    }
                    if (changed)
                    {
                        Log.LogInfo("[Loopback] host published seed=" + seedStr + " hostId=" + FakeLobbyHostId);
                        HostWriteLobbyJson();
                    }
                    else if (now - lastLobbyWrite > 5f)
                    {
                        HostWriteLobbyJson(); // keepalive
                    }
                }
                return;
            }

            if (CfgMode.Value == LoopbackMode.Client)
            {
                if (now - lastLobbyRead < 1f) return;
                lastLobbyRead = now;
                ClientReadLobbyJson();

                // Auto-join: if we have a seed in the fake lobby data and we're still at the main menu,
                // synthesise a LobbyEnter event so the stock snl.OnReallyJoin runs (and triggers the seed
                // → DFMS → LoadScene pipeline). Skips the lobby-browser UI entirely.
                if (clientAutoJoined) return;
                if (!CfgAutoJoin.Value) return;
                if (mainscript.M != null) return; // already in-world; too late
                string seedStr;
                lock (FakeLobbyLock) { FakeLobbyData.TryGetValue("seed", out seedStr); }
                if (string.IsNullOrEmpty(seedStr)) return;
                if (!int.TryParse(seedStr, out int seed)) return;

                if (sns.s == null || sns.s.lobby == null)
                {
                    // sns hasn't booted yet — give it another tick
                    return;
                }
                if (mainmenuscript.mainmenu == null) return;

                try
                {
                    // First make sure GetLobbyData("seed") (which OnReallyJoin calls) will succeed by
                    // setting our snl.lobbyID so the stock code has a non-zero CSteamID to query with.
                    sns.s.lobby.lobbyID = new CSteamID(CfgFakeLobbyID.Value);
                    // Construct a synthetic LobbyEnter_t. m_EChatRoomEnterResponse must be 1 (success).
                    var enter = new LobbyEnter_t
                    {
                        m_ulSteamIDLobby = CfgFakeLobbyID.Value,
                        m_rgfChatPermissions = 0,
                        m_bLocked = false,
                        m_EChatRoomEnterResponse = 1u
                    };
                    Log.LogInfo("[Loopback] auto-join: synthesising OnReallyJoin with seed=" + seed);
                    sns.s.lobby.OnReallyJoin(enter);
                    clientAutoJoined = true;
                }
                catch (Exception ex)
                {
                    Log.LogWarning("[Loopback] auto-join failed: " + ex.Message);
                }
            }
        }

        // Drop any heartbeat frames from the front of the queue. Caller must already hold inboundLock.
        private static void DrainHeartbeatsLocked()
        {
            while (inboundQueue.Count > 0)
            {
                byte[] head = inboundQueue.First.Value;
                if (head.Length >= 1 && head[0] == 250) inboundQueue.RemoveFirst();
                else break;
            }
        }

        [HarmonyPatch(typeof(sns), "Send", new Type[] { typeof(byte[]), typeof(EP2PSend) })]
        public static class SendHook
        {
            [HarmonyPrefix]
            public static bool Prefix(byte[] _bytes, EP2PSend sendType)
            {
                if (!IsPeerConnected()) return true;
                if (_bytes == null || _bytes.Length == 0) return false;
                SendOutbound(_bytes);
                return false;
            }
        }

        [HarmonyPatch]
        public static class IsAvailableHook
        {
            public static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(SteamNetworking), "IsP2PPacketAvailable",
                    new Type[2] { typeof(uint).MakeByRefType(), typeof(int) }, null);
            }

            [HarmonyPrefix]
            public static bool Prefix(ref uint pcubMsgSize, int nChannel, ref bool __result)
            {
                if (!IsPeerConnected()) return true;
                lock (inboundLock)
                {
                    DrainHeartbeatsLocked();
                    if (inboundQueue.Count > 0)
                    {
                        pcubMsgSize = (uint)inboundQueue.First.Value.Length;
                        __result = true;
                        return false;
                    }
                }
                // Empty queue — report no packet (don't fall through to real Steam, which would return
                // false anyway but also burns a real syscall and might surface unrelated cached state).
                pcubMsgSize = 0u;
                __result = false;
                return false;
            }
        }

        [HarmonyPatch]
        public static class ReadPacketHook
        {
            public static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(SteamNetworking), "ReadP2PPacket",
                    new Type[5]
                    {
                        typeof(byte[]),
                        typeof(uint),
                        typeof(uint).MakeByRefType(),
                        typeof(CSteamID).MakeByRefType(),
                        typeof(int)
                    }, null);
            }

            [HarmonyPrefix]
            public static bool Prefix(byte[] pubDest, uint cubDest, ref uint pcubMsgSize, ref CSteamID psteamIDRemote, int nChannel, ref bool __result)
            {
                if (!IsPeerConnected()) return true;

                byte[] payload = null;
                lock (inboundLock)
                {
                    DrainHeartbeatsLocked();
                    if (inboundQueue.Count == 0)
                    {
                        // No packet — tell the game "no read".
                        pcubMsgSize = 0u;
                        __result = false;
                        return false;
                    }

                    byte[] head = inboundQueue.First.Value;
                    // The destination buffer the game gave us might be smaller than the next packet.
                    // Real Steamworks doesn't drop the packet in that case — neither should we.
                    // Leave it queued; the game's next IsAvailable call returns head.Length so the
                    // game can size up its buffer.
                    int destLen = (pubDest != null) ? pubDest.Length : 0;
                    int effective = (int)Math.Min((long)destLen, (long)cubDest);
                    if (head.Length > effective)
                    {
                        undersizedPeekCount++;
                        float now = Time.realtimeSinceStartup;
                        if (now - lastUndersizedLog > 1f)
                        {
                            lastUndersizedLog = now;
                            Log.LogWarning("[Loopback] head " + head.Length + " > buffer " + effective
                                + " — leaving in queue (count this 1s: " + undersizedPeekCount + ")");
                            undersizedPeekCount = 0;
                        }
                        pcubMsgSize = (uint)head.Length;
                        __result = false;
                        return false;
                    }

                    payload = head;
                    inboundQueue.RemoveFirst();
                }

                Buffer.BlockCopy(payload, 0, pubDest, 0, payload.Length);
                pcubMsgSize = (uint)payload.Length;
                psteamIDRemote = fakePeerID;
                __result = true;
                return false;
            }
        }

        // ---- SteamMatchmaking patches: make stock TLD code see a real 2-member lobby ----

        [HarmonyPatch(typeof(SteamMatchmaking), "GetNumLobbyMembers")]
        public static class NumLobbyMembersHook
        {
            [HarmonyPrefix]
            public static bool Prefix(CSteamID steamIDLobby, ref int __result)
            {
                if (!IsPeerConnected()) return true;
                __result = 2;
                return false;
            }
        }

        [HarmonyPatch(typeof(SteamMatchmaking), "GetLobbyMemberByIndex")]
        public static class LobbyMemberByIndexHook
        {
            [HarmonyPrefix]
            public static bool Prefix(CSteamID steamIDLobby, int iMember, ref CSteamID __result)
            {
                if (!IsPeerConnected()) return true;
                // Index 0 = self, index 1 = peer. The "peer" identity depends on which side we are:
                //   - On host: peer is whatever ID the client reports for itself. With FakeId enabled on
                //     client (LocalSteamID=2), client's outbound packets carry cSteamID=2, AddOrUpdOne
                //     creates player(ID=2). So host should expose lobby member 1 = 2.
                //     We approximate by using fakePeerID (the config value, default 1) but the truth is
                //     it must match whatever appears in the playerPosUpd payload's cSteamID. Use the
                //     client's claimed ID (CfgFakePeerSteamID is set to that on the host side).
                //   - On client: peer is host's real SteamID, read from lobby.json (FakeLobbyHostId).
                ulong peer;
                if (CfgMode.Value == LoopbackMode.Host)
                {
                    peer = CfgFakePeerSteamID.Value;
                }
                else
                {
                    peer = FakeLobbyHostId != 0 ? FakeLobbyHostId : CfgFakePeerSteamID.Value;
                }

                if (iMember == 0)
                {
                    try { __result = SteamUser.GetSteamID(); }
                    catch { __result = new CSteamID(peer); }
                }
                else
                {
                    __result = new CSteamID(peer);
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(SteamMatchmaking), "GetLobbyOwner")]
        public static class LobbyOwnerHook
        {
            [HarmonyPrefix]
            public static bool Prefix(CSteamID steamIDLobby, ref CSteamID __result)
            {
                if (!IsPeerConnected()) return true;
                if (CfgMode.Value == LoopbackMode.Host)
                {
                    try { __result = SteamUser.GetSteamID(); }
                    catch { __result = new CSteamID(FakeLobbyHostId); }
                }
                else
                {
                    __result = FakeLobbyHostId != 0
                        ? new CSteamID(FakeLobbyHostId)
                        : new CSteamID(CfgFakePeerSteamID.Value);
                }
                return false;
            }
        }

        [HarmonyPatch(typeof(SteamMatchmaking), "GetLobbyData")]
        public static class GetLobbyDataHook
        {
            [HarmonyPrefix]
            public static bool Prefix(CSteamID steamIDLobby, string pchKey, ref string __result)
            {
                if (!IsPeerConnected() && CfgMode.Value != LoopbackMode.Host) return true;
                string val;
                lock (FakeLobbyLock)
                {
                    if (!FakeLobbyData.TryGetValue(pchKey ?? "", out val)) val = "";
                }
                __result = val;
                return false;
            }
        }

        [HarmonyPatch(typeof(SteamMatchmaking), "SetLobbyData")]
        public static class SetLobbyDataHook
        {
            [HarmonyPrefix]
            public static bool Prefix(CSteamID steamIDLobby, string pchKey, string pchValue, ref bool __result)
            {
                if (CfgMode.Value != LoopbackMode.Host) return true;
                if (string.IsNullOrEmpty(pchKey)) return true;
                lock (FakeLobbyLock)
                {
                    FakeLobbyData[pchKey] = pchValue ?? "";
                }
                // Persist immediately so a client polling lobby.json picks it up on its next tick.
                HostWriteLobbyJson();
                __result = true;
                if (pchKey == "seed" || pchKey == "name")
                    Log.LogInfo("[Loopback] SetLobbyData(" + pchKey + "=" + pchValue + ") -> lobby.json");
                return false;
            }
        }
    }
}
