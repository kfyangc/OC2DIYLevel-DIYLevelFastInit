using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using LevelEditorStub;
using OC2DIYLevel;
using OC2DIYLevel.Extension;
using UnityEngine;

namespace DIYLevelFastInit
{
    internal static partial class OnDemandResources
    {

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
            ClientLoadGate.Cancel("server level entry");
            _pendingScene = sceneName;
            _serverPreparing = true;
            LevelLoadProgress = "...";
            FastInitPlugin.Instance.StartCoroutine(FastInitHost.Guard(PrepareServerLevel(path, sceneName), "DIY level preparation [" + sceneName + "]"));
            return false;
        }

        private static IEnumerator PrepareServerLevel(string path, string scene)
        {
            bool resumed = false;
            try
            {
                yield return null;
                FastInitPlugin.Log.LogInfo("async level preparation started: " + scene);
                Func<bool> current = () => FastInitPlugin.Instance != null && _serverPreparing && _pendingScene == scene
                    && (!ConnectionStatusPublicizer.IsInSession() || ConnectionStatusPublicizer.IsHost());
                yield return FinishSaveSelection(current, "server");
                if (!current()) yield break;
                CancelPreview();
                ThumbnailCache.ClearMemory();
                while (_warmLoading || _previewLoading || _catalogBundles != null || ClientLoadGate.Running)
                {
                    if (ConnectionStatusPublicizer.IsInSession() && !ConnectionStatusPublicizer.IsHost()) yield break;
                    yield return null;
                }
                BoostEntryPriority(scene);
                yield return PrepareResources(path, scene, null);
                if (ConnectionStatusPublicizer.IsInSession() && !ConnectionStatusPublicizer.IsHost()) yield break;
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
                yield return PrepareResources(path, scene, current);
                ready.Value = current();
            }
            finally
            {
                _clientPreparingBundles = null;
                ReleaseUnused();
            }
        }

        private static IEnumerator PrepareResources(string path, string scene, Func<bool> current)
        {
            bool client = current != null;
            string context = client ? "client " : "";
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            if (client && !current()) yield break;
            ReleaseUnused();
            List<FileInfo> files = FastInitHost.SafeGetFiles(new DirectoryInfo(path), "info*");
            if (files.Count == 0) throw new FileNotFoundException("Missing info bundle", path);
            HashSet<string> requiredBundles = InfoBundleGraph.Required(files[0].FullName, Path.Combine(path, scene));
            if (client) _clientPreparingBundles = requiredBundles;
            else _preparingBundles = requiredBundles;
            foreach (string required in requiredBundles)
            {
                if (client && !current()) yield break;
                bool resident = InfoBundles.ContainsKey(required);
                System.Diagnostics.Stopwatch bundleTimer = System.Diagnostics.Stopwatch.StartNew();
                yield return EnsureInfoBundleAsync(required, 10);
                if (client && !current()) yield break;
                FastInitPlugin.Log.LogInfo(context + "level bundle ready: " + Path.GetFileName(required) + " ("
                    + (resident ? "resident" : "async") + ", " + bundleTimer.ElapsedMilliseconds + " ms elapsed)");
            }
            LoadedSet set;
            if (!Sets.TryGetValue(path, out set))
            {
                Box<LevelSetInfoSO> info = new Box<LevelSetInfoSO>();
                try
                {
                    AssetBundle bundle = InfoBundles[files[0].FullName];
                    System.Diagnostics.Stopwatch assetTimer = System.Diagnostics.Stopwatch.StartNew();
                    yield return EnsureInfoAssetAsync(path, bundle, info, 10);
                    if (client && !current()) yield break;
                    FastInitPlugin.Log.LogInfo(context + "level info asset ready: " + scene + " (" + assetTimer.ElapsedMilliseconds + " ms elapsed)");
                    set = new LoadedSet { Bundle = bundle, Info = info.Value };
                    set.Required.UnionWith(InfoBundleGraph.Required(files[0].FullName, null));
                    Sets.Add(path, set);
                    FastInitPlugin.Log.LogInfo("on-demand info loaded: " + Path.GetFileName(path) + " ("
                        + InfoBundles.Count + " bundles tracked, async " + context + "level entry)");
                }
                finally { info.Value = null; }
            }
            set.SceneDependencies[scene] = requiredBundles;
            FastInitPlugin.Log.LogInfo("async " + context + "level resources ready: " + scene + " (" + timer.ElapsedMilliseconds + " ms elapsed)");
            LevelLoadProgress = context + "0/4";
            LevelInfoSO full = Resolve(path, scene);
            SceneDirectoryData.SceneDirectoryEntry entry = PrepareSessionEntry(scene);
            entry.LoadScreenOverride = full.screenshot;
            for (int i = 0; i < entry.SceneVarients.Length; i++)
            {
                if (client && !current()) yield break;
                System.Diagnostics.Stopwatch configTimer = System.Diagnostics.Stopwatch.StartNew();
                PrepareVariant(full, entry.SceneVarients[i]);
                LevelLoadProgress = context + (i + 1) + "/" + entry.SceneVarients.Length;
                string timing = context + "level config prepared: " + scene + " (" + (i + 1) + "p, " + configTimer.ElapsedMilliseconds + " ms)";
                if (configTimer.ElapsedMilliseconds >= 50) FastInitPlugin.Log.LogInfo(timing);
                else FastInitPlugin.Log.LogDebug(timing);
                yield return null;
            }
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
