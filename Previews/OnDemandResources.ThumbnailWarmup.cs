using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using LevelEditorStub;
using OC2DIYLevel;

namespace DIYLevelFastInit
{
    internal static partial class OnDemandResources
    {

        internal static void QueueThumbnailWarmup()
        {
            if (!_thumbnailAdapter) return;
            _warmGeneration++;
            _warmQueued = true;
            if (_warmRunning || FastInitPlugin.Instance == null) return;
            _warmRunning = true;
            FastInitPlugin.Instance.StartCoroutine(FastInitHost.Guard(WarmThumbnails(), "thumbnail prewarm"));
        }

        private static bool WarmupPaused()
        {
            return FastInitPlugin.Loading || FastInitPlugin.Syncing || IsTransitioning
                || PseudoPrefabManager.isInCustomLevel || _previewLoading;
        }

        private static IEnumerator WarmThumbnails()
        {
            try
            {
                while (_warmQueued)
                {
                    _warmQueued = false;
                    int generation = _warmGeneration;
                    List<KeyValuePair<string, LevelSetInfoSO>> catalog = new List<KeyValuePair<string, LevelSetInfoSO>>(DIYLevelAssetBundleManager.levelSetInfos);
                    HashSet<LevelInfoSO> completed = new HashSet<LevelInfoSO>();
                    ThumbnailDone = ThumbnailTotal = 0;
                    foreach (KeyValuePair<string, LevelSetInfoSO> set in catalog) ThumbnailTotal += set.Value.levelInfos.Length;
                    yield return null;
                    foreach (KeyValuePair<string, LevelSetInfoSO> set in catalog)
                    {
                        while (generation == _warmGeneration)
                        {
                            while (WarmupPaused() && generation == _warmGeneration) yield return null;
                            if (generation != _warmGeneration) break;
                            List<LevelInfoSO> missing = new List<LevelInfoSO>();
                            foreach (LevelInfoSO level in set.Value.levelInfos)
                                if (ThumbnailCache.IsReady(set.Key, level.sceneName)) completed.Add(level);
                                else missing.Add(level);
                            ThumbnailDone = completed.Count;
                            if (missing.Count == 0) break;
                            Box<bool> interrupted = new Box<bool>();
                            yield return FastInitHost.Guard(WarmThumbnailSet(set.Key, missing, generation, interrupted), "thumbnail prewarm " + Path.GetFileName(set.Key));
                            if (generation != _warmGeneration) break;
                            yield return WaitForCleanup();
                            if (interrupted.Value) continue;
                            foreach (LevelInfoSO level in missing)
                                if (ThumbnailCache.IsReady(set.Key, level.sceneName)) completed.Add(level);
                            ThumbnailDone = completed.Count;
                            break;
                        }
                        if (generation != _warmGeneration) break;
                        yield return null;
                    }
                    if (generation == _warmGeneration)
                    {
                        string message = "thumbnail prewarm complete: " + ThumbnailDone + "/" + ThumbnailTotal + " disk entries ready";
                        if (ThumbnailDone == ThumbnailTotal) FastInitPlugin.Log.LogInfo(message);
                        else FastInitPlugin.Log.LogWarning(message + "; unavailable entries will load on demand");
                    }
                }
            }
            finally
            {
                _warmRunning = false;
                _warmLoading = false;
                _warmBundles = null;
                ReleaseUnused();
            }
        }

        private static IEnumerator WarmThumbnailSet(string path, List<LevelInfoSO> missing, int generation, Box<bool> interrupted)
        {
            int next = 0;
            while (next < missing.Count)
            {
                if (WarmupPaused() || generation != _warmGeneration) { interrupted.Value = true; yield break; }
                Box<int> completed = new Box<int> { Value = next };
                yield return WarmThumbnailBatch(path, missing, next, generation, completed, interrupted);
                next = completed.Value;
                yield return WaitForCleanup();
                if (interrupted.Value) yield break;
            }
            while (ThumbnailCache.WritesBusy)
            {
                if (WarmupPaused() || generation != _warmGeneration) { interrupted.Value = true; yield break; }
                yield return null;
            }
        }

        private static IEnumerator WarmThumbnailBatch(string path, List<LevelInfoSO> missing, int first, int generation,
            Box<int> completed, Box<bool> interrupted)
        {
            Box<LevelInfoSO> info = new Box<LevelInfoSO>();
            LoadedSet set = null;
            _warmLoading = true;
            try
            {
                List<FileInfo> files = FastInitHost.SafeGetFiles(new DirectoryInfo(path), "info*");
                if (files.Count == 0) throw new FileNotFoundException("Missing info bundle", path);
                _warmBundles = InfoBundleGraph.Required(files[0].FullName, null);
                foreach (string required in _warmBundles)
                {
                    if (WarmupPaused() || generation != _warmGeneration) { interrupted.Value = true; yield break; }
                    yield return EnsureInfoBundleAsync(required, -10);
                }
                if (WarmupPaused() || generation != _warmGeneration) { interrupted.Value = true; yield break; }
                Func<bool> current = () => !WarmupPaused() && generation == _warmGeneration;
                for (int i = first; i < missing.Count; i++)
                {
                    LevelInfoSO catalogLevel = missing[i];
                    while (!ThumbnailCache.HasWriteCapacity && !WarmupPaused() && generation == _warmGeneration) yield return null;
                    if (WarmupPaused() || generation != _warmGeneration) { interrupted.Value = true; yield break; }
                    completed.Value = i + 1;
                    if (ThumbnailCache.IsReady(path, catalogLevel.sceneName)) continue;
                    yield return EnsureLevelInfoAsync(path, catalogLevel.sceneName, info, -10, current);
                    if (!current()) { interrupted.Value = true; yield break; }
                    if (info.Value == null) throw new IOException("Level no longer in thumbnail info: " + catalogLevel.sceneName);
                    try { ThumbnailCache.Capture(path, catalogLevel.sceneName, info.Value.screenshot, false); }
                    catch (Exception e) { FastInitPlugin.Log.LogWarning("thumbnail prewarm skipped [" + catalogLevel.sceneName + "]: " + e.Message); }
                    info.Value = null;
                    yield return null;
                    if (!Sets.TryGetValue(path, out set) || set.Info == null) yield break;
                }
                while (ThumbnailCache.WritesBusy && current()) yield return null;
            }
            finally
            {
                info.Value = null;
                set = null;
                _warmLoading = false;
                _warmBundles = null;
                ReleaseUnused();
            }
        }
    }
}
