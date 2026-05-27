using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace TLDPubBridge
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.reedo.tld.pubbridge";
        public const string PluginName = "TLD Public Debug Bridge";
        public const string PluginVersion = "0.1.1";

        // Diagnostic counters — read by /diag without going through the main-thread queue.
        internal static volatile int pluginUpdateCount;
        internal static volatile int workerUpdateCount;
        internal static volatile int harmonyPumpCount;
        internal static volatile int actionsExecuted;
        internal static volatile int actionsEnqueued;

        internal static ManualLogSource Log;
        internal static ConfigEntry<string> CfgBindAddress;
        internal static ConfigEntry<int> CfgPort;
        internal static ConfigEntry<string> CfgAuthToken;
        internal static ConfigEntry<int> CfgLogBufferSize;
        internal static ConfigEntry<bool> CfgAllowMutations;

        private static HttpListener http;
        private static Thread listenerThread;
        private static volatile bool shuttingDown;

        // Cross-thread: queue Unity-touching actions onto the main thread.
        internal static readonly ConcurrentQueue<Action> mainThreadQueue = new ConcurrentQueue<Action>();

        // In-memory log tail buffer. Filled by BridgeLogListener registered with BepInEx.Logger.Listeners.
        internal static readonly LinkedList<string> logBuffer = new LinkedList<string>();
        internal static readonly object logBufferLock = new object();

        private void Awake()
        {
            Log = Logger;

            CfgBindAddress = Config.Bind("Bridge", "BindAddress", "127.0.0.1",
                "Address to bind the HTTP server. 127.0.0.1 = localhost-only (recommended). " +
                "0.0.0.0 binds all interfaces (dangerous — anyone on your LAN can introspect and " +
                "control your game). Only change if you know why.");
            CfgPort = Config.Bind("Bridge", "Port", 38080,
                "TCP port for the HTTP server. If you run two TLD instances locally for loopback testing, " +
                "give them different ports (e.g. host=38080, client=38081). Default 38080.");
            CfgAuthToken = Config.Bind("Bridge", "AuthToken", "",
                "Shared secret required on every request via ?token=X (or Authorization header). " +
                "Empty = no auth, anything on the bind address can hit the bridge. Set to a random " +
                "string if you bind to anything other than 127.0.0.1.");
            CfgLogBufferSize = Config.Bind("Bridge", "LogBufferSize", 500,
                "Number of recent BepInEx log lines kept in memory and served via /log/tail. " +
                "Range (50, 5000). Bigger = more memory, more historical context for debugging.");
            CfgAllowMutations = Config.Bind("Bridge", "AllowMutations", true,
                "When false, only read-only GET endpoints work; POST endpoints that mutate game state " +
                "(SetLobbyData, PressedJoinLobby, SAskStartStuff, etc.) return 403. Set false if you've " +
                "exposed the bridge non-locally and want a strict read-only diagnostic surface.");

            // Attach to BepInEx logger so /log/tail can serve recent lines.
            try
            {
                BepInEx.Logging.Logger.Listeners.Add(new BridgeLogListener());
            }
            catch (Exception ex)
            {
                Log.LogWarning("Could not attach log listener: " + ex.Message);
            }

            // Start the HTTP server on a background thread.
            string prefix = "http://" + CfgBindAddress.Value + ":" + CfgPort.Value + "/";
            try
            {
                http = new HttpListener();
                http.Prefixes.Add(prefix);
                http.Start();
                Log.LogInfo("TLD Public Debug Bridge v" + PluginVersion + " listening on " + prefix);
            }
            catch (Exception ex)
            {
                Log.LogError("Bridge failed to bind " + prefix + ": " + ex.Message);
                return;
            }

            listenerThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "TLDBridge-Listener"
            };
            listenerThread.Start();

            // Survive scene transitions. BepInEx may destroy the plugin GO when scenes load —
            // we want the HTTP listener up for the whole process lifetime.
            try { UnityEngine.Object.DontDestroyOnLoad(gameObject); } catch { }

            // Spawn a persistent worker GO too so Update() keeps pumping mainThreadQueue
            // even if our own GO is somehow destroyed.
            try
            {
                var worker = new GameObject("TLDPubBridgeWorker");
                UnityEngine.Object.DontDestroyOnLoad(worker);
                worker.AddComponent<BridgeWorker>();
                Log.LogInfo("[Bridge] worker GO spawned + DontDestroyOnLoad'd");
            }
            catch (Exception ex)
            {
                Log.LogWarning("Could not spawn persistent worker: " + ex.Message);
            }

            // Final safety net: patch a method on a stock TLD type that's guaranteed to be alive
            // on the main thread for the whole session and pump the queue from its postfix. This
            // way the queue gets pumped even if both our GOs somehow stop ticking.
            try
            {
                var harm = new Harmony(PluginGuid);
                harm.PatchAll(typeof(MainScriptUpdatePumpHook));
                Log.LogInfo("[Bridge] Harmony pump patch applied to mainscript.Update");
            }
            catch (Exception ex)
            {
                Log.LogWarning("Could not apply Harmony pump patch: " + ex.Message);
            }
        }

        private void OnDestroy()
        {
            // DO NOT stop the HTTP listener here. The plugin GO is destroyed on scene load by
            // BepInEx; the listener thread is a background thread on the process and stays
            // alive. The persistent worker GO continues pumping mainThreadQueue.
            Log.LogInfo("[Bridge] OnDestroy fired on plugin GO (ignored — http listener + worker stay alive)");
        }

        private void Update()
        {
            pluginUpdateCount++;
            PumpQueue();
        }

        internal static void PumpQueue()
        {
            int budget = 32;
            while (budget-- > 0 && mainThreadQueue.TryDequeue(out var action))
            {
                try { action(); actionsExecuted++; }
                catch (Exception ex) { Log.LogWarning("Bridge action threw: " + ex.GetType().Name + ": " + ex.Message); }
            }
        }

        private static void ListenLoop()
        {
            while (!shuttingDown && http != null && http.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = http.GetContext(); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    Log.LogWarning("Bridge listen loop: " + ex.Message);
                    Thread.Sleep(200);
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
            }
        }

        // ----- request dispatch -----

        private static void HandleRequest(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var resp = ctx.Response;
            try
            {
                // Auth check
                if (!string.IsNullOrEmpty(CfgAuthToken.Value))
                {
                    string token = req.QueryString["token"];
                    if (string.IsNullOrEmpty(token))
                    {
                        string auth = req.Headers["Authorization"] ?? "";
                        if (auth.StartsWith("Bearer ")) token = auth.Substring(7);
                    }
                    if (token != CfgAuthToken.Value)
                    {
                        WriteJson(resp, 401, "{\"error\":\"unauthorized\"}");
                        return;
                    }
                }

                string path = req.Url.AbsolutePath;
                string method = req.HttpMethod;

                // -------- read-only endpoints --------
                if (path == "/health" && method == "GET") { WriteJson(resp, 200, "{\"ok\":true,\"version\":\"" + PluginVersion + "\"}"); return; }
                if (path == "/state" && method == "GET") { WriteJson(resp, 200, MainThread(SnapshotState)); return; }
                if (path == "/lobby" && method == "GET") { WriteJson(resp, 200, MainThread(SnapshotLobby)); return; }
                if (path == "/players" && method == "GET") { WriteJson(resp, 200, MainThread(SnapshotPlayers)); return; }
                if (path == "/items" && method == "GET") { WriteJson(resp, 200, MainThread(() => SnapshotItems(req))); return; }
                if (path == "/buildings" && method == "GET") { WriteJson(resp, 200, MainThread(SnapshotBuildings)); return; }
                if (path == "/log/tail" && method == "GET") { WriteJson(resp, 200, SnapshotLogTail(req)); return; }
                if (path == "/diag" && method == "GET")
                {
                    WriteJson(resp, 200,
                        "{\"version\":\"" + PluginVersion + "\""
                        + ",\"pluginUpdates\":" + pluginUpdateCount
                        + ",\"workerUpdates\":" + workerUpdateCount
                        + ",\"harmonyPumps\":" + harmonyPumpCount
                        + ",\"queueDepth\":" + mainThreadQueue.Count
                        + ",\"actionsEnqueued\":" + actionsEnqueued
                        + ",\"actionsExecuted\":" + actionsExecuted
                        + "}");
                    return;
                }

                // -------- mutation endpoints --------
                if (method == "POST")
                {
                    if (!CfgAllowMutations.Value) { WriteJson(resp, 403, "{\"error\":\"mutations disabled\"}"); return; }
                    string body = ReadBody(req);

                    if (path == "/sns/setLobbyData") { WriteJson(resp, 200, MainThread(() => ActSetLobbyData(body))); return; }
                    if (path == "/sns/askStartStuff") { WriteJson(resp, 200, MainThread(ActAskStartStuff)); return; }
                    if (path == "/sns/chat") { WriteJson(resp, 200, MainThread(() => ActChat(body))); return; }
                    if (path == "/menu/joinLobby") { WriteJson(resp, 200, MainThread(() => ActJoinLobby(body))); return; }
                    if (path == "/mp/setMulti") { WriteJson(resp, 200, MainThread(() => ActSetMulti(body))); return; }
                    if (path == "/log/info") { WriteJson(resp, 200, MainThread(() => ActLogInfo(body))); return; }
                }

                WriteJson(resp, 404, "{\"error\":\"not found\",\"path\":\"" + EscapeJson(path) + "\"}");
            }
            catch (Exception ex)
            {
                try { WriteJson(resp, 500, "{\"error\":\"" + EscapeJson(ex.GetType().Name + ": " + ex.Message) + "\"}"); } catch { }
            }
        }

        // ----- main-thread marshalling -----

        private static string MainThread(Func<string> action)
        {
            // If we're already on the main thread (shouldn't happen for ListenLoop callers, but be safe),
            // run inline.
            string result = null;
            Exception capturedEx = null;
            using (var done = new ManualResetEventSlim())
            {
                mainThreadQueue.Enqueue(() =>
                {
                    try { result = action(); }
                    catch (Exception ex) { capturedEx = ex; }
                    finally { done.Set(); }
                });
                actionsEnqueued++;
                if (!done.Wait(5000))
                {
                    return "{\"error\":\"main-thread timeout\",\"queueDepth\":" + mainThreadQueue.Count
                        + ",\"pluginUpdates\":" + pluginUpdateCount
                        + ",\"workerUpdates\":" + workerUpdateCount
                        + ",\"harmonyPumps\":" + harmonyPumpCount + "}";
                }
            }
            if (capturedEx != null) return "{\"error\":\"" + EscapeJson(capturedEx.GetType().Name + ": " + capturedEx.Message) + "\"}";
            return result ?? "null";
        }

        // ----- snapshot helpers (run on main thread) -----

        private static string SnapshotState()
        {
            var j = new JsonBuilder();
            j.Open();
            try { j.Field("scene", UnityEngine.SceneManagement.SceneManager.GetActiveScene().name); } catch { j.Null("scene"); }
            j.FieldBool("hasMain", mainscript.M != null);
            if (mainscript.M != null)
            {
                j.Field("multi", mainscript.M.multi);
                j.Field("seed", mainscript.M.seed);
                try
                {
                    var pp = mainscript.M.player?.transform?.position;
                    if (pp.HasValue) j.Vec("playerPos", pp.Value); else j.Null("playerPos");
                }
                catch { j.Null("playerPos"); }
                try
                {
                    if (mainscript.M.mainWorld != null)
                    {
                        var c = mainscript.M.mainWorld.coord;
                        j.Raw("worldCoord", "{\"x\":" + c.x + ",\"y\":" + c.y + ",\"z\":" + c.z + "}");
                    }
                    else j.Null("worldCoord");
                }
                catch { j.Null("worldCoord"); }
            }
            j.FieldBool("hasSns", sns.s != null);
            if (sns.s != null && sns.s.lobby != null)
            {
                j.Field("isJoined", sns.s.lobby.isJoined);
                j.Field("isServer", sns.s.lobby.isServer);
                try { j.Field("lobbyID", sns.s.lobby.lobbyID.m_SteamID.ToString()); } catch { j.Null("lobbyID"); }
            }
            try { j.Field("steamId", SteamUser.GetSteamID().m_SteamID.ToString()); } catch { j.Null("steamId"); }
            try { j.Field("steamInitialized", SteamManager.Initialized); } catch { j.Null("steamInitialized"); }
            j.Close();
            return j.ToString();
        }

        private static string SnapshotLobby()
        {
            var j = new JsonBuilder();
            j.Open();
            if (sns.s == null || sns.s.lobby == null) { j.Field("error", "sns.s.lobby is null"); j.Close(); return j.ToString(); }
            var lobby = sns.s.lobby;
            j.Field("isJoined", lobby.isJoined);
            j.Field("isServer", lobby.isServer);
            try { j.Field("lobbyID", lobby.lobbyID.m_SteamID.ToString()); } catch { j.Null("lobbyID"); }
            try
            {
                int n = SteamMatchmaking.GetNumLobbyMembers(lobby.lobbyID);
                j.Field("numMembers", n);
                j.Array("members");
                for (int i = 0; i < n; i++)
                {
                    var mid = SteamMatchmaking.GetLobbyMemberByIndex(lobby.lobbyID, i);
                    string name = "?"; try { name = SteamFriends.GetFriendPersonaName(mid); } catch { }
                    string ping = ""; try { ping = SteamMatchmaking.GetLobbyMemberData(lobby.lobbyID, mid, "ping"); } catch { }
                    j.ArrayItem("{\"steamId\":\"" + mid.m_SteamID + "\",\"name\":\"" + EscapeJson(name) + "\",\"ping\":\"" + EscapeJson(ping) + "\"}");
                }
                j.EndArray();
            }
            catch (Exception ex) { j.Field("membersErr", ex.Message); }
            try { j.Field("owner", SteamMatchmaking.GetLobbyOwner(lobby.lobbyID).m_SteamID.ToString()); } catch { j.Null("owner"); }
            try { j.Field("seed", SteamMatchmaking.GetLobbyData(lobby.lobbyID, "seed")); } catch { j.Null("seed"); }
            try { j.Field("name", SteamMatchmaking.GetLobbyData(lobby.lobbyID, "name")); } catch { j.Null("name"); }
            try { j.Field("type", SteamMatchmaking.GetLobbyData(lobby.lobbyID, "type")); } catch { j.Null("type"); }
            try { j.Field("spawntype", SteamMatchmaking.GetLobbyData(lobby.lobbyID, "spawntype")); } catch { j.Null("spawntype"); }
            try { j.Field("physicslock", SteamMatchmaking.GetLobbyData(lobby.lobbyID, "physicslock")); } catch { j.Null("physicslock"); }
            j.Close();
            return j.ToString();
        }

        private static string SnapshotPlayers()
        {
            var j = new JsonBuilder();
            j.Open();
            if (sns.s == null || sns.s.syncplayer == null) { j.Field("error", "no syncplayer"); j.Close(); return j.ToString(); }
            var list = sns.s.syncplayer.players;
            j.Field("count", list != null ? list.Count : 0);
            j.Array("players");
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var p = list[i];
                    var sb = new StringBuilder();
                    sb.Append("{\"steamId\":\"").Append(p.ID.m_SteamID).Append("\"");
                    try { sb.Append(",\"name\":\"").Append(EscapeJson(p.mpscript?.nameText?.Text?[0] ?? "")).Append("\""); } catch { }
                    try { sb.Append(",\"pos\":{\"x\":").Append(p.lastPos.x).Append(",\"y\":").Append(p.lastPos.y).Append(",\"z\":").Append(p.lastPos.z).Append("}"); } catch { }
                    sb.Append(",\"lastTime\":").Append(p.lastTime);
                    sb.Append(",\"sitting\":").Append(p.sitting ? "true" : "false");
                    sb.Append("}");
                    j.ArrayItem(sb.ToString());
                }
            }
            j.EndArray();
            j.Close();
            return j.ToString();
        }

        private static string SnapshotItems(HttpListenerRequest req)
        {
            int limit = 0;
            int.TryParse(req.QueryString["limit"], out limit);
            if (limit <= 0) limit = 25;

            var j = new JsonBuilder();
            j.Open();
            if (savedatascript.d == null || savedatascript.d.toSaveStuff == null) { j.Field("error", "no savedatascript"); j.Close(); return j.ToString(); }
            j.Field("count", savedatascript.d.toSaveStuff.Count);
            j.Array("sample");
            int n = 0;
            foreach (var kv in savedatascript.d.toSaveStuff)
            {
                if (n >= limit) break;
                var ts = kv.Value;
                if (ts == null) continue;
                var sb = new StringBuilder();
                sb.Append("{\"id\":").Append(kv.Key);
                sb.Append(",\"category\":").Append(ts.category);
                sb.Append(",\"prefabId\":").Append(ts.id);
                sb.Append(",\"claimed\":").Append(ts.claimed ? "true" : "false");
                sb.Append(",\"otherClaimed\":").Append(ts.otherClaimed ? "true" : "false");
                sb.Append(",\"hasCar\":").Append(ts.car != null ? "true" : "false");
                try
                {
                    var p = ts.transform.position;
                    sb.Append(",\"pos\":{\"x\":").Append(p.x).Append(",\"y\":").Append(p.y).Append(",\"z\":").Append(p.z).Append("}");
                }
                catch { }
                sb.Append("}");
                j.ArrayItem(sb.ToString());
                n++;
            }
            j.EndArray();
            j.Close();
            return j.ToString();
        }

        private static string SnapshotBuildings()
        {
            var j = new JsonBuilder();
            j.Open();
            if (savedatascript.d == null || savedatascript.d.buildings == null) { j.Field("error", "no buildings list"); j.Close(); return j.ToString(); }
            j.Field("count", savedatascript.d.buildings.Count);
            j.Array("sample");
            int n = 0;
            foreach (var b in savedatascript.d.buildings)
            {
                if (n >= 20) break;
                if (b == null) continue;
                var sb = new StringBuilder();
                sb.Append("{");
                try
                {
                    var p = b.transform.position;
                    sb.Append("\"pos\":{\"x\":").Append(p.x).Append(",\"y\":").Append(p.y).Append(",\"z\":").Append(p.z).Append("}");
                }
                catch { sb.Append("\"pos\":null"); }
                try { sb.Append(",\"name\":\"").Append(EscapeJson(b.gameObject.name)).Append("\""); } catch { }
                sb.Append("}");
                j.ArrayItem(sb.ToString());
                n++;
            }
            j.EndArray();
            j.Close();
            return j.ToString();
        }

        private static string SnapshotLogTail(HttpListenerRequest req)
        {
            int n = 100;
            int.TryParse(req.QueryString["n"], out n);
            if (n <= 0) n = 100;
            if (n > 2000) n = 2000;
            var j = new JsonBuilder();
            j.Open();
            j.Array("lines");
            lock (logBufferLock)
            {
                int skip = Math.Max(0, logBuffer.Count - n);
                int i = 0;
                foreach (var line in logBuffer)
                {
                    if (i++ < skip) continue;
                    j.ArrayItem("\"" + EscapeJson(line) + "\"");
                }
            }
            j.EndArray();
            j.Close();
            return j.ToString();
        }

        // ----- actions (mutations, run on main thread) -----

        private static string ActSetLobbyData(string body)
        {
            var p = ParseJsonObject(body);
            if (!p.TryGetValue("key", out string key) || string.IsNullOrEmpty(key)) return "{\"error\":\"missing key\"}";
            p.TryGetValue("value", out string value); value = value ?? "";
            if (sns.s == null || sns.s.lobby == null) return "{\"error\":\"no lobby\"}";
            try
            {
                bool ok = SteamMatchmaking.SetLobbyData(sns.s.lobby.lobbyID, key, value);
                return "{\"ok\":" + (ok ? "true" : "false") + ",\"key\":\"" + EscapeJson(key) + "\"}";
            }
            catch (Exception ex) { return "{\"error\":\"" + EscapeJson(ex.Message) + "\"}"; }
        }

        private static string ActAskStartStuff()
        {
            if (sns.s == null) return "{\"error\":\"no sns\"}";
            try { sns.s.SAskStartStuff(); return "{\"ok\":true}"; }
            catch (Exception ex) { return "{\"error\":\"" + EscapeJson(ex.Message) + "\"}"; }
        }

        private static string ActChat(string body)
        {
            var p = ParseJsonObject(body);
            if (!p.TryGetValue("msg", out string msg) || string.IsNullOrEmpty(msg)) return "{\"error\":\"missing msg\"}";
            if (sns.s == null || sns.s.lobby == null) return "{\"error\":\"no lobby\"}";
            try
            {
                int n = SteamMatchmaking.GetNumLobbyMembers(sns.s.lobby.lobbyID);
                int sent = 0;
                for (int i = 0; i < n; i++)
                {
                    var mid = SteamMatchmaking.GetLobbyMemberByIndex(sns.s.lobby.lobbyID, i);
                    if (mid == SteamUser.GetSteamID()) continue;
                    sns.s.SChat(mid, msg, false);
                    sent++;
                }
                return "{\"ok\":true,\"sentTo\":" + sent + "}";
            }
            catch (Exception ex) { return "{\"error\":\"" + EscapeJson(ex.Message) + "\"}"; }
        }

        private static string ActJoinLobby(string body)
        {
            var p = ParseJsonObject(body);
            if (!p.TryGetValue("seed", out string seedStr) || string.IsNullOrEmpty(seedStr)) return "{\"error\":\"missing seed\"}";
            if (!int.TryParse(seedStr, out int seed)) return "{\"error\":\"bad seed\"}";
            try
            {
                if (mainmenuscript.mainmenu == null) return "{\"error\":\"mainmenu not active\"}";
                mainmenuscript.mainmenu.PressedJoinLobby(seed);
                return "{\"ok\":true,\"seed\":" + seed + "}";
            }
            catch (Exception ex) { return "{\"error\":\"" + EscapeJson(ex.Message) + "\"}"; }
        }

        private static string ActSetMulti(string body)
        {
            var p = ParseJsonObject(body);
            if (!p.TryGetValue("value", out string vstr)) return "{\"error\":\"missing value\"}";
            if (mainscript.M == null) return "{\"error\":\"no mainscript\"}";
            bool v = vstr == "true" || vstr == "1";
            mainscript.M.multi = v;
            return "{\"ok\":true,\"multi\":" + (v ? "true" : "false") + "}";
        }

        private static string ActLogInfo(string body)
        {
            var p = ParseJsonObject(body);
            p.TryGetValue("msg", out string msg);
            Log.LogInfo("[Bridge:remote] " + (msg ?? ""));
            return "{\"ok\":true}";
        }

        // ----- HTTP plumbing -----

        private static string ReadBody(HttpListenerRequest req)
        {
            if (!req.HasEntityBody) return "";
            using (var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                return sr.ReadToEnd();
        }

        private static void WriteJson(HttpListenerResponse resp, int status, string body)
        {
            try
            {
                resp.StatusCode = status;
                resp.ContentType = "application/json";
                resp.Headers["Access-Control-Allow-Origin"] = "*";
                resp.Headers["Cache-Control"] = "no-store";
                byte[] data = Encoding.UTF8.GetBytes(body);
                resp.ContentLength64 = data.Length;
                resp.OutputStream.Write(data, 0, data.Length);
            }
            catch { }
            finally { try { resp.OutputStream.Close(); } catch { } try { resp.Close(); } catch { } }
        }

        internal static string EscapeJson(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // ----- tiny JSON-object parser (strings only; good enough for {"k":"v","k2":"v2"}) -----
        private static Dictionary<string, string> ParseJsonObject(string body)
        {
            var d = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(body)) return d;
            int i = 0;
            // skip leading ws + {
            while (i < body.Length && (body[i] == ' ' || body[i] == '\n' || body[i] == '\r' || body[i] == '\t')) i++;
            if (i < body.Length && body[i] == '{') i++;
            while (i < body.Length)
            {
                while (i < body.Length && (body[i] == ',' || body[i] == ' ' || body[i] == '\n' || body[i] == '\r' || body[i] == '\t')) i++;
                if (i >= body.Length || body[i] == '}') break;
                if (body[i] != '"') break;
                i++;
                int ks = i;
                while (i < body.Length && body[i] != '"') { if (body[i] == '\\') i++; i++; }
                if (i >= body.Length) break;
                string key = body.Substring(ks, i - ks);
                i++; // closing "
                while (i < body.Length && body[i] != ':') i++;
                i++;
                while (i < body.Length && (body[i] == ' ' || body[i] == '\t')) i++;
                if (i >= body.Length) break;
                string val;
                if (body[i] == '"')
                {
                    i++;
                    int vs = i;
                    while (i < body.Length && body[i] != '"') { if (body[i] == '\\') i++; i++; }
                    if (i >= body.Length) break;
                    val = body.Substring(vs, i - vs);
                    i++; // closing "
                }
                else
                {
                    int vs = i;
                    while (i < body.Length && body[i] != ',' && body[i] != '}' && body[i] != ' ' && body[i] != '\n') i++;
                    val = body.Substring(vs, i - vs);
                }
                d[JsonUnescape(key)] = JsonUnescape(val);
            }
            return d;
        }

        private static string JsonUnescape(string s)
        {
            if (s == null) return null;
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
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

        // ----- tiny JSON object builder (so we don't bring in Newtonsoft for one plugin) -----
        private class JsonBuilder
        {
            private readonly StringBuilder sb = new StringBuilder();
            private bool first = true;
            private bool arrFirst = true;
            private bool inArray = false;

            public void Open() { sb.Append('{'); first = true; }
            public void Close() { sb.Append('}'); }
            private void Sep() { if (!first) sb.Append(','); first = false; }
            public void Field(string k, string v) { Sep(); sb.Append('"').Append(EscapeJson(k)).Append("\":\"").Append(EscapeJson(v ?? "")).Append('"'); }
            public void Field(string k, bool v) { Sep(); sb.Append('"').Append(EscapeJson(k)).Append("\":").Append(v ? "true" : "false"); }
            public void Field(string k, int v) { Sep(); sb.Append('"').Append(EscapeJson(k)).Append("\":").Append(v); }
            public void Field(string k, long v) { Sep(); sb.Append('"').Append(EscapeJson(k)).Append("\":").Append(v); }
            public void FieldBool(string k, bool v) { Field(k, v); }
            public void Null(string k) { Sep(); sb.Append('"').Append(EscapeJson(k)).Append("\":null"); }
            public void Raw(string k, string rawJson) { Sep(); sb.Append('"').Append(EscapeJson(k)).Append("\":").Append(rawJson); }
            public void Vec(string k, Vector3 v) { Raw(k, "{\"x\":" + v.x + ",\"y\":" + v.y + ",\"z\":" + v.z + "}"); }
            public void Array(string k) { Sep(); sb.Append('"').Append(EscapeJson(k)).Append("\":["); inArray = true; arrFirst = true; }
            public void ArrayItem(string rawJson) { if (!arrFirst) sb.Append(','); arrFirst = false; sb.Append(rawJson); }
            public void EndArray() { sb.Append(']'); inArray = false; }
            public override string ToString() { return sb.ToString(); }
        }
    }

    // Persistent worker that pumps the main-thread action queue even if the plugin GO is destroyed.
    internal class BridgeWorker : MonoBehaviour
    {
        private void Update()
        {
            Plugin.workerUpdateCount++;
            Plugin.PumpQueue();
        }
    }

    // Final-safety pump: postfix on mainscript.Update so the queue ticks as long as mainscript
    // is alive, no matter what BepInEx / scene transitions do to our own plugin GOs.
    [HarmonyPatch(typeof(mainscript), "Update")]
    internal static class MainScriptUpdatePumpHook
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            Plugin.harmonyPumpCount++;
            Plugin.PumpQueue();
        }
    }

    internal class BridgeLogListener : ILogListener
    {
        public LogLevel LogLevelFilter { get { return LogLevel.All; } }

        public void LogEvent(object sender, LogEventArgs e)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
            string line = "[" + ts + "][" + e.Source.SourceName + "][" + e.Level + "] " + e.Data;
            lock (Plugin.logBufferLock)
            {
                Plugin.logBuffer.AddLast(line);
                int target = 200;
                try { target = Plugin.CfgLogBufferSize.Value; } catch { }
                if (target < 50) target = 50;
                while (Plugin.logBuffer.Count > target) Plugin.logBuffer.RemoveFirst();
            }
        }

        public void Dispose() { }
    }
}
