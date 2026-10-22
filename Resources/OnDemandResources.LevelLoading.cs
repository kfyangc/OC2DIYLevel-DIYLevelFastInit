using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using LevelEditorStub;
using OC2DIYLevel;
using OC2DIYLevel.Extension;
using Team17.Online.Multiplayer;
using UnityEngine;

namespace DIYLevelFastInit
{
    internal static partial class OnDemandResources
    {

        private sealed class ServerPreparation
        {
            private readonly FieldInfo _serverMailbox, _clientMailbox, _peer, _localConnection, _serverConnection;
            private readonly object _mailbox, _client, _serverPeer, _clientPeer, _local, _connection;
            private readonly bool _online, _host;
            private readonly long _generation;

            internal ServerPreparation(long generation)
            {
                Type mailbox = typeof(NetworkUtils).Assembly.GetType("Mailbox", true);
                _serverMailbox = AccessTools.Field(mailbox, "Server");
                _clientMailbox = AccessTools.Field(mailbox, "Client");
                _peer = AccessTools.Field(mailbox, "m_Peer");
                _localConnection = AccessTools.Field(typeof(Server), "m_LocalClientConnection");
                _serverConnection = AccessTools.Field(typeof(Client), "m_ServerConnection");
                if (_serverMailbox == null || _clientMailbox == null || _peer == null || _localConnection == null || _serverConnection == null)
                    throw new MissingMemberException("DIY server preparation connection contract changed");
                _generation = generation;
                _online = ConnectionStatusPublicizer.IsInSession();
                _host = ConnectionStatusPublicizer.IsHost();
                _mailbox = _serverMailbox.GetValue(null);
                _client = _clientMailbox.GetValue(null);
                _serverPeer = _peer.GetValue(_mailbox);
                _clientPeer = _peer.GetValue(_client);
                _local = _serverPeer == null ? null : _localConnection.GetValue(_serverPeer);
                _connection = _clientPeer == null ? null : _serverConnection.GetValue(_clientPeer);
            }

            internal bool IsCurrent(string scene)
            {
                return FastInitPlugin.Instance != null && _serverPreparing && _generation == _serverPreparationGeneration
                    && string.Equals(_pendingScene, scene, StringComparison.OrdinalIgnoreCase)
                    && ConnectionStatusPublicizer.IsInSession() == _online && ConnectionStatusPublicizer.IsHost() == _host
                    && (!_online || _host)
                    && ReferenceEquals(_serverMailbox.GetValue(null), _mailbox)
                    && ReferenceEquals(_clientMailbox.GetValue(null), _client)
                    && ReferenceEquals(_peer.GetValue(_mailbox), _serverPeer)
                    && ReferenceEquals(_peer.GetValue(_client), _clientPeer)
                    && ReferenceEquals(_serverPeer == null ? null : _localConnection.GetValue(_serverPeer), _local)
                    && ReferenceEquals(_clientPeer == null ? null : _serverConnection.GetValue(_clientPeer), _connection);
            }
        }

        private static void LoadPrefix(string sceneName)
        {
            if (FastInitPlugin.Syncing) throw new InvalidOperationException("DIY catalog refresh is in progress; retry after it completes");
            try { PrepareLevel(sceneName); }
            catch (Exception e)
            {
                _pendingScene = null;
                FastInitPlugin.Log.LogError("DIY level preparation failed [" + sceneName + "]: " + e);
                ReleaseUnused();
                throw;
            }
        }

        private static bool ServerLoadPrefix(string sceneName)
        {
            if (_resumeServerLoad) return true;
            if (FastInitPlugin.Syncing) throw new InvalidOperationException("DIY catalog refresh is in progress; retry after it completes");
            if (_serverPreparing)
            {
                FastInitPlugin.Log.LogDebug("DIY level preparation already pending; ignoring duplicate load: " + sceneName);
                return false;
            }
            if (FastInitPlugin.Instance == null) { LoadPrefix(sceneName); return true; }
            string path = FindScenePath(sceneName);
            ServerPreparation request = new ServerPreparation(++_serverPreparationGeneration);
            ClientLoadGate.Cancel("server level entry");
            _pendingScene = sceneName;
            _serverPreparing = true;
            LevelLoadProgress = "...";
            FastInitPlugin.Instance.StartCoroutine(FastInitHost.Guard(PrepareServerLevel(path, sceneName, request), "DIY level preparation [" + sceneName + "]"));
            return false;
        }

        internal static void CancelServerPreparation(string reason)
        {
            if (!_serverPreparing) return;
            _serverPreparationGeneration++;
            FastInitPlugin.Log.LogInfo("DIY server level preparation cancelled: " + reason);
        }

        private static IEnumerator PrepareServerLevel(string path, string scene, ServerPreparation request)
        {
            bool resumed = false;
            try
            {
                yield return null;
                FastInitPlugin.Log.LogInfo("async level preparation started: " + scene);
                Func<bool> current = () => request.IsCurrent(scene);
                if (!current()) yield break;
                yield return FinishSaveSelection(current, "server");
                if (!current()) yield break;
                CancelPreview();
                ThumbnailCache.ClearMemory();
                while (_warmLoading || _previewLoading || _catalogBundles != null || ClientLoadGate.Running)
                {
                    if (!current()) yield break;
                    yield return null;
                }
                if (!current()) yield break;
                BoostEntryPriority(scene);
                yield return PrepareResources(path, scene, false, current);
                if (!current()) yield break;
                try
                {
                    _resumeServerLoad = true;
                    if (_entryPriorityActive) _entrySceneHandoff = _entryPriorityTimer.ElapsedMilliseconds;
                    DIYLevelAssetBundleManager.LoadLevelServer(scene);
                    resumed = true;
                }
                finally { _resumeServerLoad = false; }
            }
            finally
            {
                if (!ClientLoadGate.Running) LevelLoadProgress = null;
                _serverPreparing = false;
                _preparingBundles = null;
                if (!resumed)
                {
                    if (!ClientLoadGate.Running) _pendingScene = null;
                    RestoreEntryPriority("preparation interrupted");
                }
                ReleaseUnused();
            }
        }

        internal static void BeginClientPreparation(string scene)
        {
            _pendingScene = scene;
            CancelPreview();
            LevelLoadProgress = "client ...";
        }

        internal static void EndClientPreparation(bool handedOff)
        {
            if (!_serverPreparing)
            {
                LevelLoadProgress = null;
                if (!handedOff) _pendingScene = null;
            }
            ReleaseUnused();
        }

        internal static IEnumerator PrepareClientLevel(string path, string scene, Func<bool> current, Box<bool> ready)
        {
            yield return null;
            if (!current()) yield break;
            FastInitPlugin.Log.LogInfo("async client level preparation started: " + scene);
            ThumbnailCache.ClearMemory();
            while (_warmLoading || _previewLoading || _catalogBundles != null || _serverPreparing)
            {
                if (!current()) yield break;
                yield return null;
            }
            try
            {
                yield return PrepareResources(path, scene, true, current);
                ready.Value = current();
            }
            finally
            {
                _clientPreparingBundles = null;
                ReleaseUnused();
            }
        }

        private static IEnumerator PrepareResources(string path, string scene, bool client, Func<bool> current)
        {
            string context = client ? "client " : "";
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            if (!current()) yield break;
            ReleaseUnused();
            List<FileInfo> files = FastInitHost.SafeGetFiles(new DirectoryInfo(path), "info*");
            if (files.Count == 0) throw new FileNotFoundException("Missing info bundle", path);
            HashSet<string> requiredBundles = InfoBundleGraph.Required(files[0].FullName, Path.Combine(path, scene));
            if (client) _clientPreparingBundles = requiredBundles;
            else _preparingBundles = requiredBundles;
            foreach (string required in requiredBundles)
            {
                if (!current()) yield break;
                bool resident = InfoBundles.ContainsKey(required);
                System.Diagnostics.Stopwatch bundleTimer = System.Diagnostics.Stopwatch.StartNew();
                yield return EnsureInfoBundleAsync(required, 10);
                if (!current()) yield break;
                FastInitPlugin.Log.LogInfo(context + "level bundle ready: " + Path.GetFileName(required) + " ("
                    + (resident ? "resident" : "async") + ", " + bundleTimer.ElapsedMilliseconds + " ms elapsed)");
            }
            Box<LevelInfoSO> info = new Box<LevelInfoSO>();
            LevelInfoSO full;
            try
            {
                System.Diagnostics.Stopwatch assetTimer = System.Diagnostics.Stopwatch.StartNew();
                yield return EnsureLevelInfoAsync(path, scene, info, 10, current);
                if (!current()) yield break;
                full = info.Value;
                if (full == null) throw new IOException("Missing selected LevelInfo: " + scene);
                FastInitPlugin.Log.LogInfo(context + "level info asset ready: " + scene + " (" + assetTimer.ElapsedMilliseconds + " ms elapsed)");
            }
            finally { info.Value = null; }
            Sets[path].SceneDependencies[scene] = requiredBundles;
            FastInitPlugin.Log.LogInfo("async " + context + "level resources ready: " + scene + " (" + timer.ElapsedMilliseconds + " ms elapsed)");
            LevelLoadProgress = context + "0/4";
            SceneDirectoryData.SceneDirectoryEntry entry = PrepareSessionEntry(scene);
            entry.LoadScreenOverride = full.screenshot;
            for (int i = 0; i < entry.SceneVarients.Length; i++)
            {
                if (!current()) yield break;
                System.Diagnostics.Stopwatch configTimer = System.Diagnostics.Stopwatch.StartNew();
                PrepareVariant(full, entry.SceneVarients[i]);
                LevelLoadProgress = context + (i + 1) + "/" + entry.SceneVarients.Length;
                string timing = context + "level config prepared: " + scene + " (" + (i + 1) + "p, " + configTimer.ElapsedMilliseconds + " ms)";
                if (configTimer.ElapsedMilliseconds >= 50) FastInitPlugin.Log.LogInfo(timing);
                else FastInitPlugin.Log.LogDebug(timing);
                yield return null;
            }
            if (!current()) yield break;
            FastInitPlugin.Log.LogInfo("on-demand config ready: " + scene + " (" + Levels.Count + " levels retained, async "
                + context + "level entry, " + timer.ElapsedMilliseconds + " ms elapsed)");
        }

        private static void BoostEntryPriority(string scene)
        {
            RestoreEntryPriority("new level request");
            _previousEntryPriority = Application.backgroundLoadingPriority;
            Application.backgroundLoadingPriority = UnityEngine.ThreadPriority.High;
            _entryPriorityScene = scene;
            _entryPriorityTimer = System.Diagnostics.Stopwatch.StartNew();
            _entrySceneHandoff = -1;
            _entryPriorityActive = true;
            FastInitPlugin.Log.LogInfo("level loading priority: " + _previousEntryPriority + " -> High [" + scene + "]");
        }

        internal static void RestoreEntryPriority(string reason)
        {
            if (!_entryPriorityActive) return;
            _entryPriorityActive = false;
            if (Application.backgroundLoadingPriority == UnityEngine.ThreadPriority.High)
                Application.backgroundLoadingPriority = _previousEntryPriority;
            long elapsed = _entryPriorityTimer.ElapsedMilliseconds;
            FastInitPlugin.Log.LogInfo("level loading priority released: " + Application.backgroundLoadingPriority + ", " + reason + " [" + _entryPriorityScene
                + "] (" + elapsed + " ms total" + (_entrySceneHandoff < 0 ? "" : ", " + (elapsed - _entrySceneHandoff) + " ms since scene handoff") + ")");
            _entryPriorityScene = null;
            _entryPriorityTimer = null;
        }
    }
}
