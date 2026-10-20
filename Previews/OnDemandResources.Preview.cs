using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using LevelEditorStub;
using OC2DIYLevel;
using UnityEngine;
using UnityEngine.UI;

namespace DIYLevelFastInit
{
    internal static partial class OnDemandResources
    {

        internal static void InstallPreviewHooks(Harmony harmony)
        {
            Type type = AccessTools.TypeByName("OC2StageThumbnailPreview.Patches.PreviewController");
            if (type == null) return;
            MethodInfo method = AccessTools.Method(type, "UpdatePreview", new[] { typeof(Image), typeof(LevelInfoSO) });
            if (method == null) return;
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(OnDemandResources), nameof(PreviewPrefix)));
            _thumbnailAdapter = true;
            FastInitPlugin.Log.LogInfo("on-demand thumbnails: asynchronous StageThumbnailPreview adapter installed");
        }

        private static bool PreviewPrefix(Image __0, LevelInfoSO level)
        {
            if (__0 == null) return true;
            if (level == null)
            {
                if (_previewRequest != null && ReferenceEquals(_previewRequest.Image, __0)) _previewRequest = null;
                ThumbnailCache.Hide(__0);
                return true;
            }
            if (FastInitPlugin.Syncing || IsTransitioning || PseudoPrefabManager.isInCustomLevel) return true;
            string path;
            if (!FindCatalogLevel(level, out path)) return true;
            if (_previewRequest != null && ReferenceEquals(_previewRequest.Image, __0)
                && ReferenceEquals(_previewRequest.Level, level)) return false;
            ThumbnailCache.Hide(__0);
            _previewRequest = new PreviewRequest { Image = __0, Level = level, Path = path };
            _previewPath = path;
            _lastPreviewTime = Time.realtimeSinceStartup;
            if (_previewRunning || FastInitPlugin.Instance == null) return false;
            _previewRunning = true;
            FastInitPlugin.Instance.StartCoroutine(FastInitHost.Guard(PreviewRoutine(), "thumbnail queue"));
            return false;
        }

        private static bool PreviewIsCurrent(PreviewRequest request)
        {
            return ReferenceEquals(_previewRequest, request) && request.Image != null && request.Level != null
                && request.Image.transform.parent != null && request.Image.transform.parent.gameObject.activeInHierarchy
                && !FastInitPlugin.Syncing && !IsTransitioning && !PseudoPrefabManager.isInCustomLevel;
        }

        private static IEnumerator PreviewRoutine()
        {
            try
            {
                while (_previewRequest != null)
                {
                    yield return null;
                    PreviewRequest request = _previewRequest;
                    if (request == null) break;
                    if (!PreviewIsCurrent(request))
                    {
                        _previewRequest = null;
                        break;
                    }
                    if (request.Started) continue;
                    if (_catalogBundles != null) continue;
                    request.Started = true;
                    if (TryShowCachedPreview(request)) continue;
                    _previewLoading = true;
                    try
                    {
                        while (_warmLoading && PreviewIsCurrent(request)) yield return null;
                        if (!PreviewIsCurrent(request)) continue;
                        ReleaseUnused();
                        while (_cleanupQueued && PreviewIsCurrent(request)) yield return null;
                        while (_catalogBundles != null && PreviewIsCurrent(request)) yield return null;
                        if (!PreviewIsCurrent(request)) continue;
                        yield return FastInitHost.Guard(LoadPreview(request), "thumbnail " + Path.GetFileName(request.Path));
                    }
                    finally { _previewLoading = false; }
                }
            }
            finally
            {
                _previewRunning = false;
                _previewBundles = null;
                ReleaseUnused();
            }
        }

        private static IEnumerator LoadPreview(PreviewRequest request)
        {
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (TryShowCachedPreview(request)) yield break;
                LoadedSet set;
                if (!Sets.TryGetValue(request.Path, out set))
                {
                    List<FileInfo> files = FastInitHost.SafeGetFiles(new DirectoryInfo(request.Path), "info*");
                    if (files.Count == 0) throw new FileNotFoundException("Missing info bundle", request.Path);
                    _previewBundles = InfoBundleGraph.Required(files[0].FullName, null);
                    ReleaseUnused();
                    foreach (string path in _previewBundles)
                    {
                        if (!PreviewIsCurrent(request)) yield break;
                        yield return EnsureInfoBundleAsync(path);
                    }
                    if (!PreviewIsCurrent(request)) yield break;
                    if (!Sets.TryGetValue(request.Path, out set))
                    {
                        AssetBundle bundle = InfoBundles[files[0].FullName];
                        Box<LevelSetInfoSO> loadedInfo = new Box<LevelSetInfoSO>();
                        try
                        {
                            yield return EnsureInfoAssetAsync(request.Path, bundle, loadedInfo, 0);
                            if (!PreviewIsCurrent(request)) yield break;
                            if (!Sets.TryGetValue(request.Path, out set))
                            {
                                set = new LoadedSet { Bundle = bundle, Info = loadedInfo.Value };
                                set.Required.UnionWith(_previewBundles);
                                Sets.Add(request.Path, set);
                                FastInitPlugin.Log.LogInfo("on-demand preview info loaded: " + Path.GetFileName(request.Path)
                                    + " (" + InfoBundles.Count + " bundles tracked)");
                            }
                        }
                        finally { loadedInfo.Value = null; }
                    }
                }
                if (!PreviewIsCurrent(request)) yield break;
                LevelInfoSO full = null;
                foreach (LevelInfoSO level in set.Info.levelInfos)
                    if (string.Equals(level.sceneName, request.Level.sceneName, StringComparison.OrdinalIgnoreCase)) { full = level; break; }
                if (full == null) throw new IOException("Level no longer in preview info: " + request.Level.sceneName);
                ThumbnailCache.Entry thumbnail = null;
                try { thumbnail = ThumbnailCache.Capture(request.Path, request.Level.sceneName, full.screenshot); }
                catch (Exception e) { FastInitPlugin.Log.LogWarning("thumbnail cache capture skipped: " + e.Message); }
                if (thumbnail != null)
                {
                    ThumbnailCache.Show(thumbnail, request.Image);
                    _previewPath = null;
                }
                else
                {
                    request.Image.sprite = full.screenshot;
                    request.Image.gameObject.SetActive(full.screenshot != null);
                }
                FastInitPlugin.Log.LogInfo("thumbnail ready: " + request.Level.sceneName + " (async, " + timer.ElapsedMilliseconds + " ms elapsed)");
            }
            finally
            {
                _previewBundles = null;
                ReleaseUnused();
            }
        }

        private static void CancelPreview()
        {
            _previewRequest = null;
            _previewPath = null;
            ThumbnailCache.Hide(null);
        }

        private static bool TryShowCachedPreview(PreviewRequest request)
        {
            if (!PreviewIsCurrent(request)) return false;
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            bool memoryHit;
            ThumbnailCache.Entry cached = ThumbnailCache.TryGet(request.Path, request.Level.sceneName, out memoryHit);
            if (cached == null) return false;
            ThumbnailCache.Show(cached, request.Image);
            _previewPath = null;
            ReleaseUnused();
            FastInitPlugin.Log.LogInfo("thumbnail cache hit: " + request.Level.sceneName + " ("
                + (memoryHit ? "memory" : "disk") + ", " + timer.ElapsedMilliseconds + " ms)");
            return true;
        }

        internal static IEnumerator WaitForPreview()
        {
            while (_previewRunning || _warmRunning) yield return null;
        }

        private static bool FindCatalogLevel(LevelInfoSO level, out string path)
        {
            List<KeyValuePair<string, LevelSetInfoSO>> catalog = DIYLevelAssetBundleManager.levelSetInfos;
            if (catalog != null)
                foreach (KeyValuePair<string, LevelSetInfoSO> set in catalog)
                    foreach (LevelInfoSO item in set.Value.levelInfos)
                        if (ReferenceEquals(item, level)) { path = set.Key; return true; }
            path = null;
            return false;
        }
    }
}
