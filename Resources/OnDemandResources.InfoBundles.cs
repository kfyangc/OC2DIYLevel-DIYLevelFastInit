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
                request = StartInfoBundleRequest(path);
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
                {
                    PendingBundles.Remove(path);
                    if (!InfoBundles.ContainsKey(path)) ReleaseInfoBundleLease(path);
                }
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
            try
            {
                if (PendingBundles.TryGetValue(path, out request))
                {
                    bundle = request.assetBundle;
                    if (bundle == null && request.isDone)
                    {
                        RejectCachedInfoBundle(path);
                        ReleaseInfoBundleLease(path);
                        bundle = AssetBundle.LoadFromFile(path);
                    }
                }
                else bundle = LoadInfoBundleFile(path);
                if (bundle == null) throw new IOException("Cannot load info dependency: " + path);
                InfoBundles.Add(path, bundle);
                return bundle;
            }
            catch
            {
                if (!PendingBundles.TryGetValue(path, out request) || request.isDone) ReleaseInfoBundleLease(path);
                throw;
            }
        }

        private static AssetBundleCreateRequest StartInfoBundleRequest(string path)
        {
            BundleFileCache.Lease lease = BundleFileCache.Acquire(path);
            InfoBundleLeases.Add(path, lease);
            try
            {
                FastInitPlugin.Log.LogInfo("info bundle source: " + Path.GetFileName(path) + (lease.IsCached ? " (LZ4 cache)" : " (original)"));
                AssetBundleCreateRequest request = AssetBundle.LoadFromFileAsync(lease.Path);
                if (request == null) throw new IOException("Cannot start info dependency: " + path);
                return request;
            }
            catch
            {
                bool retry = lease.IsCached;
                RejectCachedInfoBundle(path);
                ReleaseInfoBundleLease(path);
                if (!retry) throw;
                AssetBundleCreateRequest request = AssetBundle.LoadFromFileAsync(path);
                if (request == null) throw new IOException("Cannot start original info dependency: " + path);
                return request;
            }
        }

        private static AssetBundle LoadInfoBundleFile(string path)
        {
            BundleFileCache.Lease lease = BundleFileCache.Acquire(path);
            InfoBundleLeases.Add(path, lease);
            AssetBundle bundle;
            try { bundle = AssetBundle.LoadFromFile(lease.Path); }
            catch
            {
                bool retry = lease.IsCached;
                RejectCachedInfoBundle(path);
                ReleaseInfoBundleLease(path);
                if (!retry) throw;
                return AssetBundle.LoadFromFile(path);
            }
            if (bundle != null || !lease.IsCached) return bundle;
            RejectCachedInfoBundle(path);
            ReleaseInfoBundleLease(path);
            return AssetBundle.LoadFromFile(path);
        }

        private static void RejectCachedInfoBundle(string path)
        {
            BundleFileCache.Lease lease;
            if (InfoBundleLeases.TryGetValue(path, out lease) && lease.IsCached)
            {
                BundleFileCache.Reject(path, lease.Path);
                FastInitPlugin.Log.LogWarning("cached info bundle rejected by Unity; retrying original: " + Path.GetFileName(path));
            }
        }

        private static void ReleaseInfoBundleLease(string path)
        {
            BundleFileCache.Lease lease;
            if (!InfoBundleLeases.TryGetValue(path, out lease)) return;
            InfoBundleLeases.Remove(path);
            lease.Dispose();
        }

        private static LoadedSet LoadSet(string path, string scene = null)
        {
            LoadedSet loaded;
            bool exists = Sets.TryGetValue(path, out loaded);
            if (exists && scene != null && loaded.SceneDependencies.ContainsKey(scene) && loaded.LoadedLevels.ContainsKey(scene)) return loaded;
            List<FileInfo> files = FastInitHost.SafeGetFiles(new DirectoryInfo(path), "info*");
            if (files.Count == 0) throw new FileNotFoundException("Missing info bundle", path);
            HashSet<string> required = InfoBundleGraph.Required(files[0].FullName,
                scene == null ? null : Path.Combine(path, scene));
            try
            {
                foreach (string dependency in required)
                    GetInfoBundle(dependency);
                AssetBundle bundle = InfoBundles[files[0].FullName];
                loaded = GetLoadedSet(path, bundle, files[0].FullName);
                if (scene != null)
                {
                    loaded.SceneDependencies[scene] = required;
                    ResolveLevelSynchronously(path, scene, loaded);
                }
                else if (loaded.Info == null)
                    loaded.Info = ReadFullInfoSynchronously(path, bundle);
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
            LoadedSet loaded = LoadSet(path, scene);
            return loaded.LoadedLevels[scene];
        }
    }
}
