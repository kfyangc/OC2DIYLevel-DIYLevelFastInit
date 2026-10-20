using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using LevelEditorStub;
using OC2DIYLevel;
using UnityEngine;

namespace DIYLevelFastInit
{

    internal class FastInitHost : MonoBehaviour
    {
        private const float AsyncTimeoutSeconds = 15f;

        private bool _finished;

        public void Run(string pluginDir)
        {
            StartCoroutine(Guard(LoadHeavy(pluginDir), "initial catalog load"));
        }

        internal static IEnumerator Guard(IEnumerator routine, string what)
        {
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            stack.Push(routine);
            try
            {
                while (stack.Count != 0)
                {
                    IEnumerator current = stack.Peek();
                    bool next = false;
                    object value = null;
                    Exception failure = null;
                    try
                    {
                        next = current.MoveNext();
                        if (next) value = current.Current;
                    }
                    catch (Exception e) { failure = e; }
                    if (failure != null)
                    {
                        FastInitPlugin.Log.LogError(what + " failed: " + failure);
                        yield break;
                    }
                    if (!next)
                    {
                        stack.Pop();
                        IDisposable disposable = current as IDisposable;
                        if (disposable != null) disposable.Dispose();
                        continue;
                    }
                    IEnumerator nested = value as IEnumerator;
                    if (nested != null) stack.Push(nested);
                    else yield return value;
                }
            }
            finally
            {
                while (stack.Count != 0)
                {
                    IDisposable disposable = stack.Pop() as IDisposable;
                    if (disposable != null) disposable.Dispose();
                }
            }
        }

        private void OnDestroy()
        {
            if (!_finished)
            {
                FastInitPlugin.Log.LogWarning("FastInitHost destroyed before loading finished — coroutine was killed by something.");
            }
        }

        internal static List<FileInfo> SafeGetFiles(DirectoryInfo dir, string pattern)
        {
            try
            {
                return new List<FileInfo>(dir.GetFiles(pattern));
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogWarning("listing " + dir.FullName + ": " + e.Message);
                return new List<FileInfo>();
            }
        }

        internal static List<DirectoryInfo> SafeGetDirs(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return new List<DirectoryInfo>();
                }
                return new List<DirectoryInfo>(new DirectoryInfo(path).GetDirectories());
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogWarning("listing " + path + ": " + e.Message);
                return new List<DirectoryInfo>();
            }
        }

        // Unity requests cannot be cancelled; keep waiting rather than racing a duplicate load.
        internal static IEnumerator WaitOp(AsyncOperation op, string what)
        {
            float start = Time.realtimeSinceStartup;
            while (!op.isDone)
            {
                if (Time.realtimeSinceStartup - start > AsyncTimeoutSeconds)
                {
                    FastInitPlugin.Log.LogWarning(what + ": async still pending after " + AsyncTimeoutSeconds + "s; waiting without a duplicate request");
                    start = Time.realtimeSinceStartup;
                }
                yield return null;
            }
        }

        // A completed failed request can safely fall back to a synchronous load.
        internal static IEnumerator FinishBundleLoad(AssetBundleCreateRequest req, string path, string what, Box<AssetBundle> result)
        {
            yield return WaitOp(req, what);
            AssetBundle bundle = req.assetBundle;
            if (bundle == null)
            {
                FastInitPlugin.Log.LogWarning(what + ": async failed (" + (req.isDone ? "null bundle" : "timeout") + "), trying sync");
                bundle = AssetBundle.LoadFromFile(path);
            }
            result.Value = bundle;
        }

        // Start an async file load and await it (see FinishBundleLoad).
        internal static IEnumerator LoadBundleFile(string path, string what, Box<AssetBundle> result)
        {
            return FinishBundleLoad(AssetBundle.LoadFromFileAsync(path), path, what, result);
        }

        // Async LevelSetInfo load with a sync LoadAsset fallback; null on failure.
        internal static IEnumerator LoadInfoAsset(AssetBundle bundle, string what, Box<LevelSetInfoSO> result)
        {
            AssetBundleRequest ar = bundle.LoadAssetAsync("LevelSetInfo", typeof(LevelSetInfoSO));
            yield return WaitOp(ar, what);
            LevelSetInfoSO so = ar.isDone ? ar.asset as LevelSetInfoSO : null;
            if (so == null)
            {
                FastInitPlugin.Log.LogWarning(what + ": LoadAssetAsync incomplete, trying sync LoadAsset");
                so = bundle.LoadAsset("LevelSetInfo", typeof(LevelSetInfoSO)) as LevelSetInfoSO;
            }
            result.Value = so;
        }

        private IEnumerator LoadHeavy(string pluginDir)
        {
            FastInitPlugin.Loading = true;
            try
            {
                FastInitPlugin.Log.LogInfo("LoadHeavy started");
                // Plugins like WLoader may already keep commonW1/commonW2 resident in memory;
                // skipping them saves the 1-2 seconds of reading the whole file just to be
                // rejected as a duplicate by Unity.
                HashSet<string> alreadyLoaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (AssetBundle ab in AssetBundle.GetAllLoadedAssetBundles())
                {
                    alreadyLoaded.Add(ab.name);
                }

                int loaded = 0, skipped = 0, failed = 0;
                foreach (FileInfo file in SafeGetFiles(new DirectoryInfo(pluginDir), "common*"))
                {
                    if (file.Name == "common")
                    {
                        continue;
                    }
                    if (alreadyLoaded.Contains(file.Name))
                    {
                        skipped++;
                        FastInitPlugin.Log.LogInfo("skip " + file.Name + ": already loaded by another plugin");
                        continue;
                    }
                    Box<AssetBundle> done = new Box<AssetBundle>();
                    yield return LoadBundleFile(file.FullName, file.Name, done);
                    if (done.Value != null)
                    {
                        loaded++;
                        alreadyLoaded.Add(file.Name);
                    }
                    else
                    {
                        failed++;
                    }
                }
                FastInitPlugin.Log.LogInfo("common bundles: " + loaded + " loaded, " + skipped + " skipped (dup), " + failed + " failed");

                List<DirectoryInfo> dirs = SafeGetDirs(Path.Combine(pluginDir, "levels"));
                FastInitPlugin.TotalSets = dirs.Count;
                int sets = 0;
                foreach (DirectoryInfo dir in dirs)
                {
                    while (OnDemandResources.IsTransitioning || PseudoPrefabManager.isInCustomLevel)
                        yield return null;
                    InfoLoad load = new InfoLoad();
                    yield return Guard(FastInitPlugin.LoadInfoSet(dir, load), "catalog " + dir.Name);
                    if (load.So == null) continue;
                    DIYLevelAssetBundleManager.levelSetInfos.Add(
                        new KeyValuePair<string, LevelSetInfoSO>(dir.FullName, load.So));
                    FastInitPlugin.Snapshots[dir.FullName] = FastInitPlugin.ReadSnapshot(dir, load.InfoFile);
                    sets++;
                    FastInitPlugin.NotifySetAdded();
                    FastInitPlugin.Log.LogInfo("level set [" + dir.Name + "] ready (" + sets + " total, metadata only)");
                    yield return null;
                }

                FastInitPlugin.Log.LogInfo("FastInit complete: " + sets + " level sets available");

                // If we are in the frontend scene when loading completes, call AddUI once so the
                // menu picks up the full list immediately; the postfix heal rebuilds the stale
                // button snapshot. Every later DLC-menu entry self-checks as well.
                try
                {
                    GameObject frontend = GameObject.Find("/Frontend/FrontendParent/FrontendRootMenu");
                    if (frontend != null)
                    {
                        DIYLevelEntryUI.AddUI();
                    }
                }
                catch (Exception e)
                {
                    FastInitPlugin.Log.LogDebug("AddUI refresh skipped: " + e.Message);
                }
            }
            finally
            {
                _finished = true;
                FastInitPlugin.FinishLoading();
            }
        }
    }
}
