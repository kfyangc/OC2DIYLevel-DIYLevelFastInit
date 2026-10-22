using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OC2DIYLevel;
using OC2DIYLevel.Extension;
using Team17.Online;
using Team17.Online.Multiplayer;
using Team17.Online.Multiplayer.Connection;
using Team17.Online.Multiplayer.Messaging;

namespace DIYLevelFastInit
{

    /// <summary>Defers only remote DIY level messages before any level-load callback runs.</summary>
    internal static class ClientLoadGate
    {
        private static readonly ClientLoadQueue Queue = new ClientLoadQueue();
        private static FieldInfo _clientMailbox, _mailboxPeer;
        private static readonly FieldInfo ServerConnection = AccessTools.Field(typeof(Client), "m_ServerConnection");
        private static MethodInfo _receive;
        private static MethodInfo _sendState;
        private static ClientLoadQueue.Request _active;
        private static string _traceScene;
        private static System.Diagnostics.Stopwatch _traceClock;
        internal static bool Running;
        internal static bool HasCurrentTransition { get { return IsIdentityCurrent(Queue.Transition); } }

        internal static void Install(Harmony harmony)
        {
            Type mailbox = typeof(NetworkUtils).Assembly.GetType("Mailbox", true);
            _clientMailbox = AccessTools.Field(mailbox, "Client");
            _mailboxPeer = AccessTools.Field(mailbox, "m_Peer");
            _receive = AccessTools.Method(mailbox, "OnMessageReceived");
            _sendState = AccessTools.Method(typeof(NetworkUtils).Assembly.GetType("ClientMessenger", true), "GameState", new[] { typeof(GameState) });
            if (_clientMailbox == null || _mailboxPeer == null || _receive == null || ServerConnection == null || _sendState == null)
                throw new MissingMemberException("DIY client mailbox contract changed");
            harmony.Patch(_receive, prefix: new HarmonyMethod(typeof(ClientLoadGate), nameof(ReceivePrefix)));
            harmony.Patch(_sendState, prefix: new HarmonyMethod(typeof(ClientLoadGate), nameof(GameStatePrefix)));
            foreach (string method in new[] { "Reset", "HandleConnectionLost", "HandleLocalLoopbackConnectionLost", "HandleDisconnectMessage" })
                harmony.Patch(AccessTools.Method(typeof(Client), method),
                    prefix: new HarmonyMethod(typeof(ClientLoadGate), nameof(DisconnectPrefix)));
            Type disconnection = typeof(NetworkUtils).Assembly.GetType("DisconnectionHandler", true);
            foreach (string method in new[] { "HandleSessionConnectionLost", "HandleLocalDisconnection", "HandleKickMessage", "OnlineMultiplayerConnectionModeErrorCallback" })
                harmony.Patch(AccessTools.Method(disconnection, method),
                    prefix: new HarmonyMethod(typeof(ClientLoadGate), nameof(DisconnectionTracePrefix)));
        }

        private static bool ReceivePrefix(object __instance, IOnlineMultiplayerSessionUserId sessionUserId,
            MessageType type, Serialisable message, uint sequence, bool bReliable)
        {
            if (type != MessageType.LevelLoadByName && type != MessageType.LevelLoadByIndex) return true;
            if (!ReferenceEquals(__instance, _clientMailbox.GetValue(null))) return true;
            if (Queue.Replaying != null && ReferenceEquals(message, Queue.Replaying.Message)) return true;
            if (ConnectionStatusPublicizer.IsHost() || !ConnectionStatusPublicizer.IsInSession()) return true;
            if (!DIYLevelAssetBundleManager.IsInitialized || !bReliable) return true;
            LevelLoadByNameMessage level = message as LevelLoadByNameMessage;
            string path;
            if (level == null || !OnDemandResources.TryFindScenePath(level.m_Scene, out path))
            {
                Cancel("another scene requested");
                return true;
            }
            if (FastInitPlugin.Instance == null) return true;
            Client peer = _mailboxPeer.GetValue(__instance) as Client;
            NetworkConnection connection = peer == null ? null : ServerConnection.GetValue(peer) as NetworkConnection;
            if (connection == null) return true;
            if (!Queue.Enqueue(__instance, peer, connection, sessionUserId, level, path, sequence, bReliable))
            {
                FastInitPlugin.Log.LogDebug("duplicate DIY client level message ignored: " + level.m_Scene);
                return false;
            }
            OnDemandResources.BeginClientPreparation(level.m_Scene);
            _traceScene = level.m_Scene;
            _traceClock = System.Diagnostics.Stopwatch.StartNew();
            FastInitPlugin.Log.LogInfo("DIY client level message deferred: " + level.m_Scene);
            if (!Running)
            {
                Running = true;
                FastInitPlugin.Instance.StartCoroutine(FastInitHost.Guard(Prepare(), "DIY client level queue"));
            }
            return false;
        }

        private static bool IsCurrent(ClientLoadQueue.Request request)
        {
            return Queue.IsCurrent(request) && IsIdentityCurrent(request);
        }

        private static bool IsIdentityCurrent(ClientLoadQueue.Request request)
        {
            if (request == null || FastInitPlugin.Instance == null) return false;
            object mailbox = _clientMailbox.GetValue(null);
            Client peer = mailbox == null ? null : _mailboxPeer.GetValue(mailbox) as Client;
            object connection = peer == null ? null : ServerConnection.GetValue(peer);
            return ClientLoadQueue.HasIdentity(request, mailbox, peer, connection,
                !ConnectionStatusPublicizer.IsHost() && ConnectionStatusPublicizer.IsInSession());
        }

        private static IEnumerator Prepare()
        {
            bool handedOff = false;
            try
            {
                while (Queue.Pending != null)
                {
                    handedOff = false;
                    ClientLoadQueue.Request request = Queue.Pending;
                    _active = request;
                    yield return null;
                    if (!IsCurrent(request))
                    {
                        Queue.CancelRequest(request);
                        continue;
                    }
                    NetworkConnection connection = (NetworkConnection)request.Connection;
                    bool paused = connection.GetLatencyTestPaused();
                    MultiplayerController controller = GameUtils.RequestManager<MultiplayerController>();
                    try
                    {
                        if (controller != null)
                        {
                            if (MultiplayerController.IsSynchronisationActive()) controller.StopSynchronisation();
                            controller.SetLatencyMeasurePaused(true);
                        }
                        if (Queue.AnnounceStart(request, state => _sendState.Invoke(null, new object[] { state })))
                            FastInitPlugin.Log.LogInfo("DIY client loading acknowledged before resources: " + request.Message.m_Scene
                                + " (" + request.Message.m_StartLoadGameState + ")");
                        yield return OnDemandResources.FinishSaveSelection(() => IsCurrent(request), "client");
                        if (!IsCurrent(request)) continue;
                        Box<bool> ready = new Box<bool>();
                        yield return FastInitHost.Guard(OnDemandResources.PrepareClientLevel(request.Path, request.Message.m_Scene,
                            () => IsCurrent(request), ready), "DIY client preparation [" + request.Message.m_Scene + "]");
                        if (ready.Value && IsCurrent(request))
                        {
                            handedOff = Queue.Deliver(request, item => _receive.Invoke(item.Mailbox, new object[]
                                { item.Sender, MessageType.LevelLoadByName, item.Message, item.Sequence, item.Reliable }));
                            FastInitPlugin.Log.LogInfo("DIY client level message resumed: " + request.Message.m_Scene);
                        }
                    }
                    finally
                    {
                        if (!handedOff) Queue.CancelRequest(request);
                        if (!handedOff && !paused && controller != null
                            && ReferenceEquals(ServerConnection.GetValue(request.Peer), request.Connection))
                            controller.SetLatencyMeasurePaused(false);
                    }
                }
            }
            finally
            {
                if (!handedOff) Queue.CancelRequest(_active);
                _active = null;
                Running = false;
                OnDemandResources.EndClientPreparation(handedOff);
            }
        }

        private static bool GameStatePrefix(GameState state)
        {
            if ((HasCurrentTransition || IsIdentityCurrent(Queue.Replaying)) && !Queue.AllowsState(state))
            {
                FastInitPlugin.Log.LogInfo("stale/duplicate DIY client state ignored: " + state);
                return false;
            }
            if (Tracing || (state == GameState.LoadedCampaignMapSave && OnDemandResources.HasDiySaveSelection))
                FastInitPlugin.Log.LogInfo("DIY client state sent: " + state + " [" + (_traceScene ?? "frontend")
                    + ", machine=" + ClientUserSystem.s_LocalMachineId + "]");
            return true;
        }

        internal static void RepeatSaveReady()
        {
            if (ConnectionStatusPublicizer.IsHost() || !ConnectionStatusPublicizer.IsInSession()) return;
            _sendState.Invoke(null, new object[] { GameState.LoadedCampaignMapSave });
            FastInitPlugin.Log.LogInfo("DIY client save acknowledgement repeated for an already confirmed choice");
        }

        private static bool Tracing { get { return _traceClock != null && _traceClock.ElapsedMilliseconds < 120000; } }

        private static void DisconnectionTracePrefix(MethodBase __originalMethod)
        {
            if (!Tracing) return;
            List<string> callers = new List<string>();
            System.Diagnostics.StackTrace stack = new System.Diagnostics.StackTrace(1, false);
            for (int i = 0; i < stack.FrameCount && callers.Count < 10; i++)
            {
                MethodBase method = stack.GetFrame(i).GetMethod();
                if (method != null) callers.Add((method.DeclaringType == null ? "" : method.DeclaringType.FullName + ".") + method.Name);
            }
            FastInitPlugin.Log.LogWarning("DIY client disconnect path: " + __originalMethod.Name + " [" + _traceScene
                + ", " + _traceClock.ElapsedMilliseconds + " ms] " + string.Join(" <- ", callers.ToArray()));
        }

        private static void DisconnectPrefix(Client __instance, MethodBase __originalMethod)
        {
            object mailbox = _clientMailbox.GetValue(null);
            if (mailbox != null && ReferenceEquals(_mailboxPeer.GetValue(mailbox), __instance))
            {
                OnDemandResources.CancelServerPreparation("client " + __originalMethod.Name);
                OnDemandResources.ResetSaveSelection();
            }
            if (Tracing) FastInitPlugin.Log.LogInfo("DIY client lifecycle: " + __originalMethod.Name + " [" + _traceScene + "]");
            if ((Queue.Transition != null && ReferenceEquals(Queue.Transition.Peer, __instance))
                || (_active != null && ReferenceEquals(_active.Peer, __instance))) Cancel("client connection reset/lost");
        }

        internal static void SceneLoaded(string scene) { Queue.SceneLoaded(scene); }

        internal static void Cancel(string reason)
        {
            if (Queue.Pending == null && Queue.Transition == null) return;
            FastInitPlugin.Log.LogInfo("DIY client level preparation cancelled: " + reason);
            Queue.Cancel();
        }
    }
}
