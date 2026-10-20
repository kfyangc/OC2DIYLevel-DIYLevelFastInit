using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using LevelEditorStub;
using UnityEngine;

namespace DIYLevelFastInit
{
    internal static partial class OnDemandResources
    {

        private static IEnumerator EnsureInfoAssetAsync(string path, AssetBundle bundle, Box<LevelSetInfoSO> result, int priority)
        {
            AssetBundleRequest request;
            if (!PendingInfos.TryGetValue(path, out request))
            {
                request = bundle.LoadAssetAsync("LevelSetInfo", typeof(LevelSetInfoSO));
                request.priority = priority;
                PendingInfos.Add(path, request);
            }
            try
            {
                yield return FastInitHost.WaitOp(request, "info " + Path.GetFileName(path));
                result.Value = request.asset as LevelSetInfoSO;
                if (result.Value == null) throw new IOException("Missing LevelSetInfo: " + path);
            }
            finally
            {
                AssetBundleRequest pending;
                if (request.isDone && PendingInfos.TryGetValue(path, out pending) && ReferenceEquals(pending, request)) PendingInfos.Remove(path);
            }
        }

        internal static IEnumerator LoadCatalogBundle(string path, Box<AssetBundle> result)
        {
            _catalogBundles = InfoBundleGraph.Required(path, null);
            foreach (string required in _catalogBundles)
                yield return EnsureInfoBundleAsync(required);
            result.Value = InfoBundles[path];
        }

        private static IEnumerator EnsureInfoBundleAsync(string path, int priority = 0)
        {
            if (QichiResources.IsRequired(path)) yield return QichiResources.EnsureAsync();
            if (InfoBundles.ContainsKey(path)) yield break;
            AssetBundleCreateRequest request;
            if (!PendingBundles.TryGetValue(path, out request))
            {
                request = AssetBundle.LoadFromFileAsync(path);
                request.priority = priority;
                PendingBundles.Add(path, request);
            }
            try
            {
                yield return FastInitHost.WaitOp(request, Path.GetFileName(path));
                GetInfoBundle(path);
            }
            finally
            {
                AssetBundleCreateRequest pending;
                if (request.isDone && PendingBundles.TryGetValue(path, out pending) && ReferenceEquals(pending, request))
                    PendingBundles.Remove(path);
            }
        }

        internal static void ReleaseCatalogBundle()
        {
            _catalogBundles = null;
            ReleaseUnused();
        }

        private static AssetBundle GetInfoBundle(string path)
        {
            if (QichiResources.IsRequired(path)) QichiResources.Ensure();
            AssetBundle bundle;
            if (InfoBundles.TryGetValue(path, out bundle)) return bundle;
            AssetBundleCreateRequest request;
            // A synchronous selection joins the existing native request instead of racing it.
            bundle = PendingBundles.TryGetValue(path, out request) ? request.assetBundle : AssetBundle.LoadFromFile(path);
            if (bundle == null && request != null && request.isDone) bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null) throw new IOException("Cannot load info dependency: " + path);
            InfoBundles.Add(path, bundle);
            return bundle;
        }

        private static LoadedSet LoadSet(string path, string scene = null)
        {
            LoadedSet loaded;
            bool exists = Sets.TryGetValue(path, out loaded);
            if (exists && (scene == null || loaded.SceneDependencies.ContainsKey(scene))) return loaded;
            List<FileInfo> files = FastInitHost.SafeGetFiles(new DirectoryInfo(path), "info*");
            if (files.Count == 0) throw new FileNotFoundException("Missing info bundle", path);
            HashSet<string> required = InfoBundleGraph.Required(files[0].FullName,
                scene == null ? null : Path.Combine(path, scene));
            try
            {
                foreach (string dependency in required)
                    GetInfoBundle(dependency);
                if (Sets.TryGetValue(path, out loaded))
                {
                    loaded.SceneDependencies[scene] = required;
                    return loaded;
                }
                AssetBundle bundle = InfoBundles[files[0].FullName];
                AssetBundleRequest pending;
                LevelSetInfoSO info = PendingInfos.TryGetValue(path, out pending)
                    ? pending.asset as LevelSetInfoSO : bundle.LoadAsset<LevelSetInfoSO>("LevelSetInfo");
                if (info == null) throw new IOException("Missing LevelSetInfo: " + path);
                loaded = new LoadedSet { Bundle = bundle, Info = info };
                loaded.Required.UnionWith(scene == null ? required : InfoBundleGraph.Required(files[0].FullName, null));
                if (scene != null) loaded.SceneDependencies[scene] = required;
                Sets.Add(path, loaded);
                FastInitPlugin.Log.LogInfo("on-demand info loaded: " + Path.GetFileName(path) + " (" + InfoBundles.Count + " bundles tracked)");
                return loaded;
            }
            catch
            {
                ReleaseUnused();
                throw;
            }
        }

        private static LevelInfoSO Resolve(string path, string scene)
        {
            foreach (LevelInfoSO info in LoadSet(path).Info.levelInfos)
                if (string.Equals(info.sceneName, scene, StringComparison.OrdinalIgnoreCase)) return info;
            throw new IOException("Level no longer in info bundle; refresh the catalog: " + scene);
        }
    }
}
