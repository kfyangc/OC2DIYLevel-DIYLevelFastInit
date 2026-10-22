using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using LevelEditorStub;
using OC2DIYLevel;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DIYLevelFastInit
{
    internal static partial class OnDemandResources
    {

        private static void ClearPrefix(bool isQuit)
        {
            if (!isQuit) return;
            foreach (string scene in new List<string>(Levels.Keys)) ReleaseLevel(scene);
            foreach (LevelResources resources in SceneObjects.Values) DestroyObjects(resources);
            SceneObjects.Clear();
            SceneDirectoryCache.DropConfigs(null);
            SceneDirectoryCache.CollectRetired();
        }

        private static bool SceneIsLoaded(string name)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && string.Equals(scene.name, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool SetIsNeeded(LoadedSet set)
        {
            foreach (LevelInfoSO level in set.Info.levelInfos)
                if (SceneIsLoaded(level.sceneName) || string.Equals(_pendingScene, level.sceneName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            bool frontend = scene.name == "StartScreen" || scene.name == "Lobbies";
            ClientLoadGate.SceneLoaded(scene.name);
            if (frontend) CancelServerPreparation("frontend scene loaded");
            if (_entryPriorityActive && (frontend || string.Equals(scene.name, _entryPriorityScene, StringComparison.OrdinalIgnoreCase)))
                RestoreEntryPriority("scene ready: " + scene.name);
            if (frontend || string.Equals(scene.name, _pendingScene, StringComparison.OrdinalIgnoreCase))
            {
                _pendingScene = null;
                CancelPreview();
                SaveSelection.Reset();
                _visibleSaveDialog = null;
            }
            if (frontend)
            {
                ClientLoadGate.Cancel("frontend scene loaded");
                _selectionFrame = -1;
            }
            ReleaseUnused();
            if (frontend) RequestCleanup();
            if (Levels.ContainsKey(scene.name) && FastInitPlugin.Instance != null)
                FastInitPlugin.Instance.StartCoroutine(FastInitHost.Guard(AuditSceneMaterials(scene), "scene material audit"));
        }

        private static void SceneUnloaded(Scene scene)
        {
            LevelResources resources;
            if (!SceneObjects.TryGetValue(scene, out resources)) return;
            SceneObjects.Remove(scene);
            int count = DestroyObjects(resources);
            FastInitPlugin.Log.LogInfo("scene runtime objects released: " + scene.name + " (" + count + " objects)");
        }

        internal static void RequestCleanup()
        {
            _cleanupRequested = true;
            if (_cleanupQueued || FastInitPlugin.Instance == null) return;
            _cleanupQueued = true;
            FastInitPlugin.Instance.StartCoroutine(FastInitHost.Guard(CleanupRoutine(), "unused info assets"));
        }

        internal static IEnumerator WaitForCleanup()
        {
            while (_cleanupQueued) yield return null;
        }

        private static IEnumerator CleanupRoutine()
        {
            try
            {
                while (_cleanupRequested)
                {
                    yield return null;
                    while (IsTransitioning || PseudoPrefabManager.isInCustomLevel || _previewBundles != null || _warmBundles != null
                        || Time.realtimeSinceStartup - _lastPreviewTime < 0.5f) yield return null;
                    _cleanupRequested = false;
                    yield return Resources.UnloadUnusedAssets();
                    FastInitPlugin.Log.LogDebug("unused info assets collected; referenced assets preserved");
                }
            }
            finally { _cleanupQueued = false; }
        }

        internal static void ResetFrontend()
        {
            _warmGeneration++;
            _warmQueued = false;
            CancelPreview();
            ReleaseUnused();
        }

        private static void ReleaseUnused()
        {
            foreach (string scene in new List<string>(Levels.Keys))
                if (!SceneIsLoaded(scene) && !string.Equals(scene, _pendingScene, StringComparison.OrdinalIgnoreCase)) ReleaseLevel(scene);
            foreach (string path in new List<string>(Sets.Keys))
            {
                LoadedSet set = Sets[path];
                if (string.Equals(path, _previewPath, StringComparison.OrdinalIgnoreCase) || SetIsNeeded(set)) continue;
                foreach (KeyValuePair<string, LevelSetInfoSO> catalog in DIYLevelAssetBundleManager.levelSetInfos)
                    if (string.Equals(catalog.Key, path, StringComparison.OrdinalIgnoreCase))
                        foreach (LevelInfoSO level in catalog.Value.levelInfos) level.screenshot = null;
                Sets.Remove(path);
                set.Info = null;
                set.Bundle = null;
                FastInitPlugin.Log.LogInfo("on-demand info released: " + Path.GetFileName(path));
            }
            HashSet<string> retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_catalogBundles != null) retained.UnionWith(_catalogBundles);
            if (_previewBundles != null) retained.UnionWith(_previewBundles);
            if (_warmBundles != null) retained.UnionWith(_warmBundles);
            if (_preparingBundles != null) retained.UnionWith(_preparingBundles);
            if (_clientPreparingBundles != null) retained.UnionWith(_clientPreparingBundles);
            foreach (LoadedSet set in Sets.Values)
            {
                retained.UnionWith(set.Required);
                foreach (string scene in new List<string>(set.SceneDependencies.Keys))
                    if (SceneIsLoaded(scene) || string.Equals(_pendingScene, scene, StringComparison.OrdinalIgnoreCase))
                        retained.UnionWith(set.SceneDependencies[scene]);
                    else
                        set.SceneDependencies.Remove(scene);
            }
            foreach (string path in new List<string>(InfoBundles.Keys))
            {
                if (retained.Contains(path)) continue;
                InfoBundles[path].Unload(false);
                InfoBundles.Remove(path);
                RequestCleanup();
                FastInitPlugin.Log.LogInfo("info dependency released: " + Path.GetFileName(path)
                    + " (" + InfoBundles.Count + " bundles tracked)");
            }
        }

        private static void ReleaseLevel(string scene)
        {
            SceneDirectoryCache.DropConfigs(scene);
            LevelResources resources = Levels[scene];
            Levels.Remove(scene);
            int count = DestroyObjects(resources);
            FastInitPlugin.Log.LogInfo("on-demand config released: " + scene + " (" + count + " runtime objects)");
        }

        private static int DestroyObjects(LevelResources resources)
        {
            int count = 0;
            foreach (UnityEngine.Object value in resources.Objects)
                if (value != null) { UnityEngine.Object.DestroyImmediate(value); count++; }
            PruneClones(QuitClones);
            PruneClones(RestartClones);
            return count;
        }

        private static void PruneClones(FieldInfo field)
        {
            List<GameObject> list = field.GetValue(null) as List<GameObject>;
            if (list != null) list.RemoveAll(value => value == null);
        }
    }
}
