using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using LevelEditorStub;
using OC2DIYLevel;
using UnityEngine;

namespace DIYLevelFastInit
{
    internal static partial class OnDemandResources
    {
        private static LoadedSet GetLoadedSet(string path, AssetBundle bundle, string infoFile)
        {
            LoadedSet set;
            if (Sets.TryGetValue(path, out set)) return set;
            set = new LoadedSet { Bundle = bundle };
            set.Required.UnionWith(InfoBundleGraph.Required(infoFile, null));
            Sets.Add(path, set);
            return set;
        }

        private static LevelInfoSO CatalogLevel(string path, string scene)
        {
            foreach (KeyValuePair<string, LevelSetInfoSO> set in DIYLevelAssetBundleManager.levelSetInfos)
                if (string.Equals(set.Key, path, StringComparison.OrdinalIgnoreCase))
                    foreach (LevelInfoSO level in set.Value.levelInfos)
                        if (string.Equals(level.sceneName, scene, StringComparison.OrdinalIgnoreCase)) return level;
            throw new IOException("Level no longer in catalog; refresh before loading: " + scene);
        }

        private static bool TryLevelAddress(LoadedSet set, LevelInfoSO metadata, out string address)
        {
            address = null;
            if (!FastInitPlugin.UseIndividualLevelAssets || metadata == null) return false;
            if (!set.AssetNamesRead)
            {
                set.AssetNamesRead = true;
                try { set.AssetIndex = new LevelAssetIndex(set.Bundle.GetAllAssetNames()); }
                catch (Exception e) { FastInitPlugin.Log.LogWarning("individual level asset index unavailable: " + e.Message); }
            }
            return set.AssetIndex != null && set.AssetIndex.TryResolve(metadata.name, out address)
                && !set.InvalidAssets.Contains(address);
        }

        private static bool MatchesLevel(LevelInfoSO level, LevelInfoSO metadata)
        {
            return level != null && string.Equals(level.name, metadata.name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(level.sceneName, metadata.sceneName, StringComparison.OrdinalIgnoreCase);
        }

        private static LevelInfoSO FindFullLevel(LevelSetInfoSO info, string scene)
        {
            if (info != null && info.levelInfos != null)
                foreach (LevelInfoSO level in info.levelInfos)
                    if (level != null && string.Equals(level.sceneName, scene, StringComparison.OrdinalIgnoreCase)) return level;
            throw new IOException("Level no longer in info bundle; refresh the catalog: " + scene);
        }

        private static AssetBundleRequest StartLevelAssetRequest(AssetBundle bundle, string address, int priority)
        {
            try
            {
                AssetBundleRequest request = bundle.LoadAssetAsync(address, typeof(LevelInfoSO));
                if (request != null) request.priority = priority;
                return request;
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogWarning("individual level asset request failed: " + address + ": " + e.Message);
                return null;
            }
        }

        private static LevelInfoSO ReadLevelAsset(AssetBundleRequest request)
        {
            try { return request.asset as LevelInfoSO; }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogWarning("individual level asset unavailable: " + e.Message);
                return null;
            }
        }

        private static bool LevelRequestCurrent(Func<bool> current) { return current == null || current(); }

        private static IEnumerator EnsureLevelInfoAsync(string path, string scene, Box<LevelInfoSO> result, int priority, Func<bool> current)
        {
            if (!LevelRequestCurrent(current)) yield break;
            List<FileInfo> files = FastInitHost.SafeGetFiles(new DirectoryInfo(path), "info*");
            if (files.Count == 0) throw new FileNotFoundException("Missing info bundle", path);
            LoadedSet set = GetLoadedSet(path, InfoBundles[files[0].FullName], files[0].FullName);
            LevelInfoSO full;
            if (set.LoadedLevels.TryGetValue(scene, out full) && full != null) { result.Value = full; yield break; }
            LevelInfoSO metadata = CatalogLevel(path, scene);
            string address;
            if (set.Info == null && TryLevelAddress(set, metadata, out address))
            {
                string key = path + "\n" + address;
                AssetBundleRequest request;
                if (!PendingLevelInfos.TryGetValue(key, out request))
                {
                    request = StartLevelAssetRequest(set.Bundle, address, priority);
                    if (request != null) PendingLevelInfos.Add(key, request);
                }
                if (request != null)
                {
                    try
                    {
                        yield return FastInitHost.WaitOp(request, "level info " + scene);
                        if (!LevelRequestCurrent(current)) yield break;
                        full = ReadLevelAsset(request);
                    }
                    finally
                    {
                        AssetBundleRequest pending;
                        if (request.isDone && PendingLevelInfos.TryGetValue(key, out pending) && ReferenceEquals(pending, request))
                            PendingLevelInfos.Remove(key);
                    }
                    if (MatchesLevel(full, metadata))
                    {
                        set.LoadedLevels[scene] = result.Value = full;
                        FastInitPlugin.Log.LogInfo("individual level info loaded: " + scene + " (" + address + ")");
                        yield break;
                    }
                }
                set.InvalidAssets.Add(address);
                FastInitPlugin.Log.LogWarning("individual level asset did not match catalog; using LevelSetInfo: " + scene);
            }
            if (!LevelRequestCurrent(current)) yield break;
            if (set.Info == null)
            {
                Box<LevelSetInfoSO> info = new Box<LevelSetInfoSO>();
                try
                {
                    yield return EnsureInfoAssetAsync(path, set.Bundle, info, priority);
                    if (!LevelRequestCurrent(current)) yield break;
                    set.Info = info.Value;
                }
                finally { info.Value = null; }
                FastInitPlugin.Log.LogInfo("level info fallback loaded: " + scene + " (LevelSetInfo)");
            }
            full = FindFullLevel(set.Info, scene);
            set.LoadedLevels[scene] = result.Value = full;
        }

        private static LevelSetInfoSO ReadFullInfoSynchronously(string path, AssetBundle bundle)
        {
            AssetBundleRequest pending;
            LevelSetInfoSO info = PendingInfos.TryGetValue(path, out pending)
                ? pending.asset as LevelSetInfoSO : bundle.LoadAsset<LevelSetInfoSO>("LevelSetInfo");
            if (info == null) throw new IOException("Missing LevelSetInfo: " + path);
            return info;
        }

        private static LevelInfoSO ResolveLevelSynchronously(string path, string scene, LoadedSet set)
        {
            LevelInfoSO full;
            if (set.LoadedLevels.TryGetValue(scene, out full) && full != null) return full;
            LevelInfoSO metadata = CatalogLevel(path, scene);
            string address;
            if (set.Info == null && TryLevelAddress(set, metadata, out address))
            {
                AssetBundleRequest pending;
                if (PendingLevelInfos.TryGetValue(path + "\n" + address, out pending))
                {
                    full = ReadLevelAsset(pending);
                    if (!pending.isDone) throw new IOException("Individual level request still pending: " + scene);
                }
                else
                {
                    try { full = set.Bundle.LoadAsset<LevelInfoSO>(address); }
                    catch (Exception e)
                    {
                        FastInitPlugin.Log.LogWarning("individual level asset unavailable: " + e.Message);
                        full = null;
                    }
                }
                if (MatchesLevel(full, metadata)) { set.LoadedLevels[scene] = full; return full; }
                set.InvalidAssets.Add(address);
            }
            if (set.Info == null) set.Info = ReadFullInfoSynchronously(path, set.Bundle);
            full = FindFullLevel(set.Info, scene);
            set.LoadedLevels[scene] = full;
            return full;
        }
    }
}
