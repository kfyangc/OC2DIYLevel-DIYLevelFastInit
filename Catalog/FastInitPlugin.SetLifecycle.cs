using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using LevelEditorStub;
using OC2DIYLevel;

namespace DIYLevelFastInit
{
    public partial class FastInitPlugin
    {

        // Scene bundles release their own dependencies through the vanilla manager.
        private static void UnloadSet(string key)
        {
            List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
            int idx = IndexOfSet(infos, key);
            if (idx < 0)
            {
                return;
            }
            try
            {
                SweepSetBundles(infos[idx].Value);
            }
            catch (Exception e)
            {
                Log.LogWarning("sync: sweep of [" + key + "] failed: " + e.Message);
            }
            LevelCatalogCache.DestroyLightweight(infos[idx].Value);
            Snapshots.Remove(key);
            infos.RemoveAt(idx);
        }

        private static IEnumerator ReloadSetRoutine(DirectoryInfo dir, Box<bool> done)
        {
            List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
            int index = IndexOfSet(infos, dir.FullName);
            if (index < 0) yield break;
            SweepSetBundles(infos[index].Value);
            InfoLoad load = new InfoLoad();
            yield return FastInitHost.Guard(LoadInfoSet(dir, load), "catalog " + dir.Name);
            if (load.So == null)
            {
                Log.LogWarning("sync: [" + dir.Name + "] reload failed, keeping old catalog");
                yield break;
            }
            LevelSetInfoSO old = infos[index].Value;
            infos[index] = new KeyValuePair<string, LevelSetInfoSO>(dir.FullName, load.So);
            Snapshots[dir.FullName] = ReadSnapshot(dir, load.InfoFile);
            LevelCatalogCache.DestroyLightweight(old);
            done.Value = true;
        }

        private static IEnumerator LoadSetRoutine(DirectoryInfo dir, Box<bool> done)
        {
            InfoLoad load = new InfoLoad();
            yield return FastInitHost.Guard(LoadInfoSet(dir, load), "catalog " + dir.Name);
            if (load.So == null) yield break;
            List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
            if (IndexOfSet(infos, dir.FullName) >= 0)
            {
                LevelCatalogCache.DestroyLightweight(load.So);
                yield break;
            }
            infos.Add(new KeyValuePair<string, LevelSetInfoSO>(dir.FullName, load.So));
            Snapshots[dir.FullName] = ReadSnapshot(dir, load.InfoFile);
            done.Value = true;
        }

        private static void SweepSetBundles(LevelSetInfoSO so)
        {
            if (so == null || so.levelInfos == null)
            {
                return;
            }
            List<string> sceneNames = new List<string>();
            foreach (LevelInfoSO info in so.levelInfos)
            {
                if (info == null)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(info.sceneName))
                {
                    sceneNames.Add(info.sceneName);
                }
            }
            // Scene (main) bundles only. NEVER sweep names from info.dependencies:
            // those are VANILLA game bundle names (gua's LoadDependencies expands them
            // via the game's own manifest), and unloading one the frontend still uses
            // is a native access violation. Dependency bundles release correctly via
            // the game's refcounts when the main bundle unloads (UnloadDependencies).
            SweepBundles(sceneNames);
        }

        // Repeatedly UnloadAssetBundle the names (verbatim and lowercase variants: the
        // game lowercases scene bundle keys) until nothing resolves, cap 16 passes for
        // refcounts accumulated by replays.
        private static void SweepBundles(List<string> names)
        {
            for (int pass = 0; pass < 16; pass++)
            {
                bool touched = false;
                foreach (string name in names)
                {
                    touched |= TryUnloadBundle(name);
                    touched |= TryUnloadBundle(name.ToLowerInvariant());
                }
                if (!touched)
                {
                    return;
                }
            }
            foreach (string name in names)
            {
                string err;
                if (AssetBundles.AssetBundleManager.GetLoadedAssetBundle(name, out err) != null
                    || AssetBundles.AssetBundleManager.GetLoadedAssetBundle(name.ToLowerInvariant(), out err) != null)
                {
                    Log.LogWarning("sync: [" + name + "] still resident after 16 sweep passes");
                }
            }
        }

        private static bool TryUnloadBundle(string name)
        {
            try
            {
                string err;
                if (AssetBundles.AssetBundleManager.GetLoadedAssetBundle(name, out err) == null)
                {
                    return false;
                }
                AssetBundles.AssetBundleManager.UnloadAssetBundle(name);
                return true;
            }
            catch (Exception e)
            {
                Log.LogDebug("sync: unload of [" + name + "]: " + e.Message);
                return false;
            }
        }
    }
}
