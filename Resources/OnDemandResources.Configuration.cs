using System;
using System.Collections.Generic;
using LevelEditorStub;
using OC2DIYLevel;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DIYLevelFastInit
{
    internal static partial class OnDemandResources
    {

        internal static bool TryFindScenePath(string scene, out string path)
        {
            List<KeyValuePair<string, LevelSetInfoSO>> catalog = DIYLevelAssetBundleManager.levelSetInfos;
            if (catalog != null)
                foreach (KeyValuePair<string, LevelSetInfoSO> set in catalog)
                    foreach (LevelInfoSO info in set.Value.levelInfos)
                        if (string.Equals(info.sceneName, scene, StringComparison.OrdinalIgnoreCase))
                        { path = set.Key; return true; }
            path = null;
            return false;
        }

        private static string FindScenePath(string scene)
        {
            string path;
            if (TryFindScenePath(scene, out path)) return path;
            throw new InvalidOperationException("DIY level not found in catalog: " + scene);
        }

        private static void PrepareLevel(string scene)
        {
            string path = FindScenePath(scene);
            _pendingScene = scene;
            CancelPreview();
            ThumbnailCache.ClearMemory();
            ReleaseUnused();
            LoadSet(path, scene);
            LevelInfoSO full = Resolve(path, scene);
            SceneDirectoryData.SceneDirectoryEntry entry = PrepareSessionEntry(scene);
            entry.LoadScreenOverride = full.screenshot;
            foreach (SceneDirectoryData.PerPlayerCountDirectoryEntry variant in entry.SceneVarients) PrepareVariant(full, variant);
            FastInitPlugin.Log.LogInfo("on-demand config ready: " + scene + " (" + Levels.Count + " levels retained)");
        }

        private static SceneDirectoryData.SceneDirectoryEntry PrepareSessionEntry(string scene)
        {
            SceneDirectoryData directory = SceneDirectoryCache.GetDirectory();
            GameSession session = GameUtils.GetGameSession();
            if (session == null || session.DLC != 15 || !ReferenceEquals(session.Progress.GetSceneDirectory(), directory))
                session = DIYLevelAssetBundleManager.StartEmptySession(session != null && session.DLC == 15 ? session.SaveSlot : -1);
            foreach (SceneDirectoryData.SceneDirectoryEntry entry in directory.Scenes)
            {
                if (!string.Equals(entry.SceneVarients[0].SceneName, scene, StringComparison.OrdinalIgnoreCase)) continue;
                return entry;
            }
            throw new InvalidOperationException("Missing scene directory entry: " + scene);
        }

        private static void PrepareVariant(LevelInfoSO full, SceneDirectoryData.PerPlayerCountDirectoryEntry variant)
        {
            _directoryBuild = true;
            try
            {
                if (variant.LevelConfig == null)
                    variant.LevelConfig = LevelConfigSetup.SetupConfig(FastInitPlugin.FiConfigSo.GetValue(null) as PseudoPrefabSO, full, variant.PlayerCount);
                variant.Screenshot = full.screenshot;
            }
            finally { _directoryBuild = false; }
        }

        private static void ConfigPrefix(ref LevelInfoSO config, ref string __state)
        {
            __state = _buildingScene;
            string path;
            if (FindCatalogLevel(config, out path)) config = Resolve(path, config.sceneName);
            _buildingScene = config.sceneName;
        }

        private static void ConfigPostfix(CampaignLevelConfig __result)
        {
            Record(__result);
            Record(__result.m_recipeMatchingList);
            // SetupConfig clones only the first round's recipe list; other rounds may share assets.
            if (__result.m_rounds.Length != 0) Record(__result.m_rounds[0].m_recipes);
        }

        private static Exception ConfigFinalizer(Exception __exception, string __state)
        {
            _buildingScene = __state;
            return __exception;
        }

        private static void CustomRecipePostfix(object __result, object[] __args)
        {
            if (__args[0] is CustomRecipeSO) Record(__result as UnityEngine.Object);
        }

        private static void OwnedPostfix(object __result) { Record(__result as UnityEngine.Object); }
        private static void ClonePostfix(GameObject __result) { Record(__result); }

        private static void Record(UnityEngine.Object value)
        {
            if (value == null) return;
            string scene = _buildingScene;
            if (scene == null && PseudoPrefabManager.isInCustomLevel && PseudoPrefabManager.stub != null)
                scene = PseudoPrefabManager.stub.levelInfo.sceneName;
            if (scene == null) return;
            LevelResources resources;
            if (!_directoryBuild && PseudoPrefabManager.isInCustomLevel && PseudoPrefabManager.stub != null)
            {
                Scene instance = PseudoPrefabManager.stub.gameObject.scene;
                if (!SceneObjects.TryGetValue(instance, out resources))
                {
                    resources = new LevelResources();
                    SceneObjects.Add(instance, resources);
                }
            }
            else if (!Levels.TryGetValue(scene, out resources))
            {
                resources = new LevelResources();
                Levels.Add(scene, resources);
            }
            resources.Objects.Add(value);
        }
    }
}
