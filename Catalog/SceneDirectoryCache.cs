using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LevelEditorStub;
using OC2DIYLevel;
using UnityEngine;

namespace DIYLevelFastInit
{

    internal static class SceneDirectoryCache
    {
        private static readonly FieldInfo StarBoundaries = AccessTools.Field(typeof(SceneDirectoryData.PerPlayerCountDirectoryEntry), "m_PCStarBoundaries");
        private static SceneDirectoryData _cached;
        private static List<KeyValuePair<string, LevelSetInfoSO>> _sets;
        private static readonly List<SceneDirectoryData> Retired = new List<SceneDirectoryData>();

        private static bool Matches()
        {
            List<KeyValuePair<string, LevelSetInfoSO>> now = DIYLevelAssetBundleManager.levelSetInfos;
            if (_cached == null || now == null || _sets == null || now.Count != _sets.Count) return false;
            for (int i = 0; i < now.Count; i++)
                if (now[i].Key != _sets[i].Key || !ReferenceEquals(now[i].Value, _sets[i].Value)) return false;
            return true;
        }

        internal static SceneDirectoryData GetDirectory()
        {
            if (Matches()) return _cached;
            SceneDirectoryData data = ScriptableObject.CreateInstance<SceneDirectoryData>();
            data.name = "DIYLevelSceneDirectory";
            data.hideFlags = HideFlags.DontUnloadUnusedAsset;
            List<SceneDirectoryData.SceneDirectoryEntry> entries = new List<SceneDirectoryData.SceneDirectoryEntry>();
            foreach (KeyValuePair<string, LevelSetInfoSO> set in DIYLevelAssetBundleManager.levelSetInfos)
                foreach (LevelInfoSO info in set.Value.levelInfos)
                {
                    SceneDirectoryData.SceneDirectoryEntry entry = new SceneDirectoryData.SceneDirectoryEntry();
                    entry.Label = "\"" + OC2DIYLevel.UIUtils.GetLocalizedText(info.levelName, info.levelNameZH) + "\"";
                    entry.AvailableInLobby = false;
                    entry.SceneVarients = new SceneDirectoryData.PerPlayerCountDirectoryEntry[4];
                    LevelConfigSetupPerPlayerCountSO[] counts = { info.config_1p, info.config_2p, info.config_3p, info.config_4p };
                    for (int i = 0; i < 4; i++)
                    {
                        SceneDirectoryData.PerPlayerCountDirectoryEntry variant = new SceneDirectoryData.PerPlayerCountDirectoryEntry();
                        variant.PlayerCount = i + 1;
                        variant.SceneName = info.sceneName;
                        StarBoundaries.SetValue(variant, new SceneDirectoryData.StarBoundaries
                        {
                            m_OneStarScore = counts[i].m_OneStarScore, m_TwoStarScore = counts[i].m_TwoStarScore,
                            m_ThreeStarScore = counts[i].m_ThreeStarScore, m_FourStarScore = counts[i].m_FourStarScore
                        });
                        entry.SceneVarients[i] = variant;
                    }
                    entries.Add(entry);
                }
            data.Scenes = entries.ToArray();
            if (_cached != null) Retired.Add(_cached);
            _cached = data;
            _sets = new List<KeyValuePair<string, LevelSetInfoSO>>(DIYLevelAssetBundleManager.levelSetInfos);
            FastInitPlugin.Log.LogInfo("lightweight scene directory: " + entries.Count + " levels, no recipe configs");
            return data;
        }

        internal static void DropConfigs(string scene)
        {
            if (_cached != null) DropConfigs(_cached, scene);
            foreach (SceneDirectoryData data in Retired) if (data != null) DropConfigs(data, scene);
        }

        private static void DropConfigs(SceneDirectoryData data, string scene)
        {
            foreach (SceneDirectoryData.SceneDirectoryEntry entry in data.Scenes)
                if (scene == null || string.Equals(entry.SceneVarients[0].SceneName, scene, StringComparison.OrdinalIgnoreCase))
                {
                    entry.LoadScreenOverride = null;
                    foreach (SceneDirectoryData.PerPlayerCountDirectoryEntry variant in entry.SceneVarients)
                    {
                        variant.LevelConfig = null;
                        variant.Screenshot = null;
                    }
                }
        }

        internal static void CollectRetired()
        {
            foreach (SceneDirectoryData data in Retired) if (data != null) UnityEngine.Object.DestroyImmediate(data);
            Retired.Clear();
        }
    }
}
