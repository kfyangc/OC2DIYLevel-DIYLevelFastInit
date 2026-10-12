using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using LevelEditorStub;
using OC2DIYLevel;
using OC2DIYLevel.CustomArcade;
using UnityEngine;
using UnityEngine.UI;

namespace DIYLevelFastInit
{
    /// <summary>
    /// Replaces DIYLevelAssetBundleManager.Initialize(): the cheap part (common bundle,
    /// static fields, DLC data) stays synchronous, while the common* dependency bundles
    /// and all level-set info bundles load in a background coroutine. Also fixes the
    /// original's missing idempotence guard, adds hot sync (the menu button), and caches
    /// the scene directory so the save-dialog no longer rebuilds a level config for
    /// every level of every set on each open. While that cache is live, the blanket
    /// RuntimePrefabManager clears that pairing depends on are narrowed to superseded
    /// clones, so cached assembly configs keep valid prefab references.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("Overcooked2.exe")]
    [BepInDependency("dev.gua.overcooked.diylevel")]
    public class FastInitPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "oc2.diylevel.fastinit";
        public const string PluginName = "DIYLevel FastInit";
        public const string PluginVersion = "1.5.1";

        internal static ManualLogSource Log;

        private static bool _started;

        private static readonly FieldInfo FiCommonBundle = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "commonBundle");
        private static readonly FieldInfo FiCover = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "diyLevelCover");
        private static readonly FieldInfo FiSessionPrefab = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "diyLevelGameSessionPrefab");
        internal static readonly FieldInfo FiConfigSo = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "configTemplateSO");
        private static readonly FieldInfo FiInstance = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "Instance");
        private static readonly FieldInfo FiSetMenu = AccessTools.Field(typeof(DIYLevelEntryUI), "levelSetSelectionMenu");
        private static readonly MethodInfo MiAddSetButton = AccessTools.Method(typeof(DIYLevelEntryUI), "AddLevelSetButton");

        private static int _builtCount;

        internal static bool Loading;
        internal static int TotalSets;

        internal static FastInitPlugin Instance;

        // Per-directory info bundle refs and on-load snapshots: sync unloads via these
        // and diffs against them, so untouched sets are never reloaded.
        internal static readonly Dictionary<string, AssetBundle> InfoBundles =
            new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);
        internal static readonly Dictionary<string, SetSnapshot> Snapshots =
            new Dictionary<string, SetSnapshot>(StringComparer.OrdinalIgnoreCase);

        private static GUIStyle _progressStyle;

        private void Awake()
        {
            Log = Logger;
            Instance = this;
            MethodInfo original = AccessTools.Method(typeof(DIYLevelAssetBundleManager), "Initialize");
            if (original == null)
            {
                Log.LogError("DIYLevelAssetBundleManager.Initialize not found; FastInit inactive (original behavior kept).");
                return;
            }
            Harmony harmony = new Harmony(PluginGuid);
            harmony.Patch(original, prefix: new HarmonyMethod(typeof(FastInitPlugin), nameof(InitializePrefix)));
            // The original builds the level-set button list only when the menu is first
            // created (if (menu != null) return), so sets that finish loading later never
            // show up; re-sync the buttons after every AddUI call.
            MethodInfo addUi = AccessTools.Method(typeof(DIYLevelEntryUI), "AddUI");
            if (addUi != null && FiSetMenu != null && MiAddSetButton != null)
            {
                harmony.Patch(addUi, postfix: new HarmonyMethod(typeof(FastInitPlugin), nameof(HealLevelSetButtons)));
            }
            else
            {
                Log.LogWarning("AddUI heal patch not applied (member missing): menu may need re-entry to show late-loaded sets");
            }
            // Every save-dialog open runs StartEmptySession -> SetupSceneDirectoryData,
            // which builds a CampaignLevelConfig for EVERY level of EVERY set (x4 player
            // counts) on the main thread: seconds with many sets installed. Serve a
            // cached directory instead; SceneDirectoryCache warms it in the background.
            MethodInfo setupDirectory = AccessTools.Method(typeof(DIYLevelAssetBundleManager), "SetupSceneDirectoryData");
            if (setupDirectory != null)
            {
                harmony.Patch(setupDirectory,
                    prefix: new HarmonyMethod(typeof(FastInitPlugin), nameof(SetupDirectoryPrefix)),
                    postfix: new HarmonyMethod(typeof(FastInitPlugin), nameof(SetupDirectoryPostfix)));
            }
            else
            {
                Log.LogWarning("SetupSceneDirectoryData not found; directory cache inactive");
            }
            // StartEmptySession pairs ClearAllRuntimePrefabs(isQuit:true) with a full
            // directory rebuild: the clear destroys the RuntimePrefabManager clones that
            // custom burger/pizza assembly configs reference (OrderDefinitionNode
            // m_platingPrefab), and the rebuild recreates them. Serving the cache removes
            // the rebuild half of that pairing, so the destroy half must be skipped while
            // the cache is live; SceneDirectoryCache accounts for the clones instead.
            MethodInfo clearAll = AccessTools.Method(typeof(RuntimePrefabManager), "ClearAllRuntimePrefabs");
            if (clearAll != null)
            {
                harmony.Patch(clearAll,
                    prefix: new HarmonyMethod(typeof(FastInitPlugin), nameof(ClearRuntimePrefabsPrefix)),
                    postfix: new HarmonyMethod(typeof(FastInitPlugin), nameof(ClearRuntimePrefabsPostfix)));
            }
            else
            {
                Log.LogWarning("ClearAllRuntimePrefabs not found; runtime-prefab guard inactive");
            }
            MethodInfo clonePrefab = AccessTools.Method(typeof(RuntimePrefabManager), "CloneAsInactivePrefab");
            if (clonePrefab != null)
            {
                harmony.Patch(clonePrefab,
                    postfix: new HarmonyMethod(typeof(FastInitPlugin), nameof(ClonePrefabPostfix)));
            }
            else
            {
                Log.LogWarning("CloneAsInactivePrefab not found; clone tracking inactive");
            }
            Log.LogInfo("DIYLevel FastInit ready: level bundles load async, menu no longer blocks.");
        }

        private void OnGUI()
        {
            if (!Loading || TotalSets <= 0)
            {
                return;
            }
            try
            {
                if (_progressStyle == null)
                {
                    _progressStyle = new GUIStyle(GUI.skin.label);
                    _progressStyle.fontSize = 18;
                    _progressStyle.fontStyle = FontStyle.Bold;
                    _progressStyle.alignment = TextAnchor.UpperRight;
                }
                List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
                int done = (infos != null) ? infos.Count : 0;
                string text = "DIY levels loading " + done + "/" + TotalSets;
                Rect rect = new Rect(Screen.width - 336, 6, 330, 26);
                _progressStyle.normal.textColor = Color.black;
                GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), text, _progressStyle);
                _progressStyle.normal.textColor = Color.white;
                GUI.Label(rect, text, _progressStyle);
            }
            catch (Exception)
            {
            }
        }

        internal static void HealLevelSetButtons()
        {
            try
            {
                List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
                FrontendOptionsMenu menu = TryGetMenu();
                if (infos == null || menu == null || MiAddSetButton == null)
                {
                    return; // menu not created yet; AddUI itself will build it from the current list
                }
                // The frontend rebuilds its menus on every level exit, so a freshly recreated
                // menu can match the cached count while missing the reload button; re-assert
                // it on every heal before the count check can short-circuit.
                EnsureReloadButton(menu);
                if (_builtCount == infos.Count)
                {
                    return;
                }
                OC2DIYLevel.UIUtils.ClearAllMenuContent(menu);
                for (int i = 0; i < infos.Count; i++)
                {
                    MiAddSetButton.Invoke(null, new object[] { infos[i].Value });
                }
                _builtCount = infos.Count;
                Log.LogInfo("level set menu rebuilt: " + infos.Count + " entries");
                UpdateMenuHeaderProgress(menu);
                EnsureReloadButton(menu);
            }
            catch (Exception e)
            {
                Log.LogWarning("HealLevelSetButtons failed: " + e.Message);
            }
        }

        // Incremental counterpart of HealLevelSetButtons: appends only the buttons past
        // the cached count, so the menu fills in live during the initial async load.
        internal static void NotifySetAdded()
        {
            try
            {
                List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
                FrontendOptionsMenu menu = TryGetMenu();
                if (infos == null || menu == null || MiAddSetButton == null)
                {
                    return;
                }
                if (_builtCount < infos.Count)
                {
                    for (int i = _builtCount; i < infos.Count; i++)
                    {
                        MiAddSetButton.Invoke(null, new object[] { infos[i].Value });
                    }
                }
                _builtCount = infos.Count;
                UpdateMenuHeaderProgress(menu);
                EnsureReloadButton(menu);
            }
            catch (Exception e)
            {
                Log.LogDebug("NotifySetAdded: " + e.Message);
            }
        }

        private static FrontendOptionsMenu TryGetMenu()
        {
            return FiSetMenu != null ? FiSetMenu.GetValue(null) as FrontendOptionsMenu : null;
        }

        private static void UpdateMenuHeaderProgress(FrontendOptionsMenu menu)
        {
            try
            {
                Transform header = menu.transform.Find("SettingsBody/HeaderBacker/Header");
                if (header == null)
                {
                    return;
                }
                T17Text text = header.GetComponent<T17Text>();
                if (text == null)
                {
                    return;
                }
                List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
                int done = (infos != null) ? infos.Count : 0;
                if (Loading && TotalSets > 0)
                {
                    OC2DIYLevel.UIUtils.SetText(text, "More Levels (loading " + done + "/" + TotalSets + ")", "更多关卡（加载中 " + done + "/" + TotalSets + "）");
                }
                else
                {
                    OC2DIYLevel.UIUtils.SetText(text, "More Levels", "更多关卡");
                }
            }
            catch (Exception e)
            {
                Log.LogDebug("UpdateMenuHeaderProgress: " + e.Message);
            }
        }

        internal static void FinishLoading()
        {
            Loading = false;
            FrontendOptionsMenu menu = TryGetMenu();
            if (menu != null)
            {
                UpdateMenuHeaderProgress(menu);
                EnsureReloadButton(menu);
            }
            SceneDirectoryCache.TryStartWarm();
        }

        // Harmony prefix on DIYLevelAssetBundleManager.SetupSceneDirectoryData: serve the
        // cached directory when the loaded level-set list is unchanged (order-sensitive:
        // LevelId positions map into the Scenes array). Stale/missing cache falls through
        // to the original build, whose result the postfix then captures.
        private static bool SetupDirectoryPrefix(ref SceneDirectoryData __result)
        {
            try
            {
                List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
                if (infos != null && infos.Count > 0 && SceneDirectoryCache.Matches(infos))
                {
                    __result = SceneDirectoryCache.Cached;
                    return false;
                }
            }
            catch (Exception e)
            {
                Log.LogDebug("SetupDirectoryPrefix: " + e.Message);
            }
            return true;
        }

        private static void SetupDirectoryPostfix(SceneDirectoryData __result)
        {
            try
            {
                List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
                if (__result != null && infos != null)
                {
                    SceneDirectoryCache.Publish(__result, infos, false);
                }
            }
            catch (Exception e)
            {
                Log.LogDebug("SetupDirectoryPostfix: " + e.Message);
            }
        }

        // Harmony prefix on RuntimePrefabManager.ClearAllRuntimePrefabs. isQuit:true runs
        // from StartEmptySession (every save-dialog open) and from the StartScreen scene
        // load; both destroy the clones the cached assembly configs reference, with no
        // rebuild to restore them while the cache is live. Skip the clear in that case
        // and let SceneDirectoryCache collect superseded clones instead. Without a live
        // cache the original runs: the following build recreates everything, exactly the
        // pre-1.5.1 behavior. isQuit:false (level exit) only touches clearOnRestart
        // clones and always runs.
        private static bool ClearRuntimePrefabsPrefix(bool isQuit, ref bool __state)
        {
            __state = false;
            if (!isQuit)
            {
                return true;
            }
            bool skip = false;
            try
            {
                List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
                skip = infos != null && infos.Count > 0 && SceneDirectoryCache.Matches(infos);
            }
            catch (Exception e)
            {
                Log.LogDebug("ClearRuntimePrefabsPrefix: " + e.Message);
            }
            if (skip)
            {
                SceneDirectoryCache.OnBlanketClearSkipped();
                __state = true;
                return false;
            }
            SceneDirectoryCache.OnBlanketClearRunning();
            return true;
        }

        private static void ClearRuntimePrefabsPostfix(bool isQuit, bool __state)
        {
            if (isQuit && !__state)
            {
                // The original clear destroyed every tracked clone.
                SceneDirectoryCache.ResetCloneTracking();
            }
        }

        // Harmony postfix on RuntimePrefabManager.CloneAsInactivePrefab: record each
        // clone so the cache can later destroy exactly the superseded ones and keep the
        // dialog-open backstop the original blanket clear provided for the rest.
        private static void ClonePrefabPostfix(GameObject __result, bool clearOnRestart)
        {
            if (__result == null)
            {
                return;
            }
            try
            {
                SceneDirectoryCache.RecordClone(__result, clearOnRestart);
            }
            catch (Exception e)
            {
                Log.LogDebug("ClonePrefabPostfix: " + e.Message);
            }
        }

        // ---- Hot sync: per-directory diff, only reload what changed ----

        internal static void TrySyncReload()
        {
            if (Loading)
            {
                Log.LogInfo("sync skipped: initial load or previous sync still running");
                return;
            }
            if (!DIYLevelAssetBundleManager.IsInitialized)
            {
                Log.LogInfo("sync skipped: DIY level manager not initialized");
                return;
            }
            if (PseudoPrefabManager.isInCustomLevel)
            {
                Log.LogInfo("sync skipped: inside a DIY level");
                return;
            }
            // No GameSession guard here: gua's session is DontDestroyOnLoad and lingers
            // in the frontend after a level exit, but every level entry runs through
            // StartEmptySession, which destroys it and rebuilds the scene directory from
            // the current list — a stale session is harmless to sync. The button itself
            // only exists in the frontend menu, so no level load can be in flight.
            if (GameObject.Find("/Frontend/FrontendParent/FrontendRootMenu") == null)
            {
                Log.LogInfo("sync skipped: not in the frontend scene");
                return;
            }
            Instance.StartCoroutine(Instance.SyncRoutine());
        }

        private IEnumerator SyncRoutine()
        {
            Loading = true;
            try
            {
                Log.LogInfo("sync started");
                List<DirectoryInfo> dirs = FastInitHost.SafeGetDirs(Path.Combine(GetDiyPluginDir(), "levels"));
                TotalSets = dirs.Count;
                if (dirs.Count == 0 && DIYLevelAssetBundleManager.levelSetInfos != null
                    && DIYLevelAssetBundleManager.levelSetInfos.Count > 0)
                {
                    // A transiently empty scan must not unload every loaded set.
                    Log.LogWarning("sync: levels/ scan returned nothing while sets are loaded; aborting");
                    yield break;
                }
                List<DirectoryInfo> added = new List<DirectoryInfo>();
                List<DirectoryInfo> infoChanged = new List<DirectoryInfo>();
                List<DirectoryInfo> contentChanged = new List<DirectoryInfo>();
                List<string> removed = new List<string>();
                int unchanged = 0;
                bool diffFailed = false;
                // Diff only — no yields allowed inside this try (C# iterators).
                try
                {
                    Dictionary<string, SetSnapshot> fresh = new Dictionary<string, SetSnapshot>(StringComparer.OrdinalIgnoreCase);
                    foreach (DirectoryInfo dir in dirs)
                    {
                        List<FileInfo> infoFiles = FastInitHost.SafeGetFiles(dir, "info*");
                        if (infoFiles.Count == 0)
                        {
                            Log.LogWarning("sync: missing info file: " + dir.Name);
                            continue;
                        }
                        SetSnapshot snap = ReadSnapshot(dir, infoFiles[0]);
                        fresh[dir.FullName] = snap;
                        SetSnapshot old;
                        bool hasSnapshot = Snapshots.TryGetValue(dir.FullName, out old);
                        bool inList = IndexOfSet(DIYLevelAssetBundleManager.levelSetInfos, dir.FullName) >= 0;
                        if (!inList)
                        {
                            added.Add(dir);
                        }
                        else if (!hasSnapshot)
                        {
                            unchanged++; // loaded before snapshots existed; leave it alone
                        }
                        else if (old.InfoFileName != snap.InfoFileName
                            || old.InfoMtimeUtc != snap.InfoMtimeUtc
                            || old.InfoLength != snap.InfoLength)
                        {
                            infoChanged.Add(dir);
                        }
                        else if (old.DirMaxMtimeUtc != snap.DirMaxMtimeUtc)
                        {
                            contentChanged.Add(dir);
                        }
                        else
                        {
                            unchanged++;
                        }
                    }
                    foreach (KeyValuePair<string, LevelSetInfoSO> kv in DIYLevelAssetBundleManager.levelSetInfos)
                    {
                        if (!fresh.ContainsKey(kv.Key))
                        {
                            removed.Add(kv.Key);
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.LogError("sync diff failed: " + e);
                    diffFailed = true;
                }
                if (diffFailed)
                {
                    yield break;
                }

                int removedCount = 0, reloaded = 0, swept = 0, addedCount = 0, failed = 0;
                foreach (string key in removed)
                {
                    try
                    {
                        UnloadSet(key);
                        removedCount++;
                        Log.LogInfo("sync: set removed: " + key);
                    }
                    catch (Exception e)
                    {
                        failed++;
                        Log.LogWarning("sync: unload failed for [" + key + "]: " + e.Message);
                    }
                }
                foreach (DirectoryInfo dir in infoChanged)
                {
                    Box<bool> done = new Box<bool>();
                    yield return ReloadSetRoutine(dir, done);
                    if (done.Value)
                    {
                        reloaded++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                foreach (DirectoryInfo dir in contentChanged)
                {
                    try
                    {
                        List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
                        int idx = IndexOfSet(infos, dir.FullName);
                        if (idx >= 0)
                        {
                            SweepSetBundles(infos[idx].Value);
                            swept++;
                            Log.LogInfo("sync: swept resident bundles of [" + dir.Name + "]");
                        }
                    }
                    catch (Exception e)
                    {
                        Log.LogWarning("sync: content sweep of [" + dir.Name + "] failed: " + e.Message);
                    }
                }
                foreach (DirectoryInfo dir in added)
                {
                    Box<bool> done = new Box<bool>();
                    yield return LoadSetRoutine(dir, done);
                    if (done.Value)
                    {
                        addedCount++;
                    }
                    else
                    {
                        failed++;
                    }
                }
                try
                {
                    DIYLevelAssetBundleManager.levelSetInfos.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception e)
                {
                    Log.LogDebug("sync sort failed: " + e.Message);
                }

                yield return Resources.UnloadUnusedAssets();

                if (addedCount + removedCount + reloaded + swept > 0)
                {
                    _builtCount = 0;
                    HealLevelSetButtons();
                    ResetArcadeMenu();
                }
                Log.LogInfo("sync: " + addedCount + " added, " + removedCount + " removed, "
                    + reloaded + " info-reloaded, " + swept + " content-swept, "
                    + unchanged + " unchanged" + (failed > 0 ? ", " + failed + " failed" : ""));
            }
            finally
            {
                FinishLoading();
            }
        }

        private static int IndexOfSet(List<KeyValuePair<string, LevelSetInfoSO>> infos, string key)
        {
            return infos.FindIndex(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        // The arcade settings menu snapshots selector options at build time; destroy it so
        // the next T17TabPanel.OnTabSelected rebuilds it with fresh options. Nulling the
        // static is not enough: AddCustomArcadeSettingsUI early-returns while the menu
        // GameObject exists, so stale selector rows would survive.
        private static void ResetArcadeMenu()
        {
            try
            {
                FrontendOptionsMenu arcadeMenu = CustomArcadeEntryUI.customArcadeSettingsMenu;
                if (arcadeMenu == null)
                {
                    return;
                }
                if (arcadeMenu.gameObject.activeInHierarchy)
                {
                    Log.LogInfo("sync: arcade settings menu is open, deferring its rebuild");
                    return;
                }
                UnityEngine.Object.Destroy(arcadeMenu.gameObject);
                CustomArcadeEntryUI.customArcadeSettingsMenu = null;
                CustomArcadeEntryUI.selectorOptions.Clear();
                Log.LogInfo("arcade settings menu will rebuild on next open");
            }
            catch (Exception e)
            {
                Log.LogDebug("arcade menu reset failed: " + e.Message);
            }
        }

        // Remove one set: sweep its resident scene/dependency bundles, release the info
        // bundle, drop the list entry and snapshot.
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
            AssetBundle bundle;
            if (InfoBundles.TryGetValue(key, out bundle) && bundle != null)
            {
                bundle.Unload(false);
            }
            InfoBundles.Remove(key);
            Snapshots.Remove(key);
            infos.RemoveAt(idx);
        }

        // Load one directory's info bundle + LevelSetInfo asset (shared by add and reload).
        // Fills result; unloads the bundle itself if the asset is missing. Callers check
        // result.So to detect failure.
        private static IEnumerator LoadInfoSet(DirectoryInfo dir, InfoLoad result)
        {
            List<FileInfo> infoFiles = FastInitHost.SafeGetFiles(dir, "info*");
            if (infoFiles.Count == 0)
            {
                Log.LogWarning("sync: missing info file: " + dir.Name);
                yield break;
            }
            result.InfoFile = infoFiles[0];
            Box<AssetBundle> bundleBox = new Box<AssetBundle>();
            yield return FastInitHost.LoadBundleFile(result.InfoFile.FullName, dir.Name, bundleBox);
            result.Bundle = bundleBox.Value;
            if (result.Bundle == null)
            {
                Log.LogWarning("sync: failed loading info bundle of [" + dir.Name + "]");
                yield break;
            }
            Box<LevelSetInfoSO> soBox = new Box<LevelSetInfoSO>();
            yield return FastInitHost.LoadInfoAsset(result.Bundle, dir.Name + " (LevelSetInfo)", soBox);
            result.So = soBox.Value;
            if (result.So == null)
            {
                Log.LogWarning("sync: missing LevelSetInfo in [" + dir.Name + "]");
                result.Bundle.Unload(false);
            }
        }

        // Reload one set whose info changed: sweep with the OLD scene names, unload the
        // old info bundle, load the new one, replace the entry at the same index.
        private static IEnumerator ReloadSetRoutine(DirectoryInfo dir, Box<bool> done)
        {
            string key = dir.FullName;
            List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
            int idx = IndexOfSet(infos, key);
            if (idx < 0)
            {
                done.Value = false;
                yield break;
            }
            try
            {
                SweepSetBundles(infos[idx].Value);
                AssetBundle oldBundle;
                if (InfoBundles.TryGetValue(key, out oldBundle) && oldBundle != null)
                {
                    oldBundle.Unload(false);
                }
                InfoBundles.Remove(key);
            }
            catch (Exception e)
            {
                Log.LogWarning("sync: sweep of [" + dir.Name + "] failed: " + e.Message);
            }
            InfoLoad load = new InfoLoad();
            yield return LoadInfoSet(dir, load);
            if (load.So == null)
            {
                // Unload(false) kept the old SO alive, so the stale set still renders.
                Log.LogWarning("sync: [" + dir.Name + "] reload failed, keeping old set");
                done.Value = false;
                yield break;
            }
            infos[idx] = new KeyValuePair<string, LevelSetInfoSO>(key, load.So);
            InfoBundles[key] = load.Bundle;
            Snapshots[key] = ReadSnapshot(dir, load.InfoFile);
            Log.LogInfo("sync: level set [" + dir.Name + "] reloaded in place");
            done.Value = true;
        }

        // Load one new directory and insert it at its sorted position (directory order
        // must hold: the arcade mod resolves selections by list position).
        private static IEnumerator LoadSetRoutine(DirectoryInfo dir, Box<bool> done)
        {
            InfoLoad load = new InfoLoad();
            yield return LoadInfoSet(dir, load);
            if (load.So == null)
            {
                done.Value = false;
                yield break;
            }
            string key = dir.FullName;
            List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
            if (IndexOfSet(infos, key) >= 0)
            {
                Log.LogWarning("sync: [" + dir.Name + "] already loaded, skipping insert");
                load.Bundle.Unload(false);
                done.Value = false;
                yield break;
            }
            int insertAt = infos.Count;
            for (int i = 0; i < infos.Count; i++)
            {
                if (string.Compare(infos[i].Key, key, StringComparison.OrdinalIgnoreCase) > 0)
                {
                    insertAt = i;
                    break;
                }
            }
            infos.Insert(insertAt, new KeyValuePair<string, LevelSetInfoSO>(key, load.So));
            InfoBundles[key] = load.Bundle;
            Snapshots[key] = ReadSnapshot(dir, load.InfoFile);
            Log.LogInfo("sync: level set [" + dir.Name + "] added");
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

        // The reload button sits FIRST in the level set menu; find-by-name keeps it
        // from ever duplicating across incremental appends and full rebuilds.
        internal static void EnsureReloadButton(FrontendOptionsMenu menu)
        {
            try
            {
                if (menu == null)
                {
                    return;
                }
                Transform content = menu.transform.Find("SettingsBody/ContentPC/Viewport/Content");
                if (content == null)
                {
                    return;
                }
                T17Button btn;
                Transform existing = content.Find("FastInit_Reload");
                if (existing != null)
                {
                    btn = existing.GetComponent<T17Button>();
                }
                else
                {
                    btn = OC2DIYLevel.UIUtils.AddButton(menu, "FastInit_Reload", "Reload level sets", "刷新关卡列表", TrySyncReload);
                }
                if (btn == null)
                {
                    return;
                }
                btn.transform.SetAsFirstSibling();
                ((Button)btn).interactable = !Loading;
            }
            catch (Exception e)
            {
                Log.LogDebug("EnsureReloadButton: " + e.Message);
            }
        }

        internal static SetSnapshot ReadSnapshot(DirectoryInfo dir, FileInfo infoFile)
        {
            SetSnapshot snap = new SetSnapshot();
            try
            {
                snap.InfoFileName = infoFile.Name;
                snap.InfoMtimeUtc = infoFile.LastWriteTimeUtc;
                snap.InfoLength = infoFile.Length;
                DateTime max = DateTime.MinValue;
                foreach (FileInfo f in FastInitHost.SafeGetFiles(dir, "*"))
                {
                    if (f.LastWriteTimeUtc > max)
                    {
                        max = f.LastWriteTimeUtc;
                    }
                }
                snap.DirMaxMtimeUtc = max;
            }
            catch (Exception e)
            {
                Log.LogDebug("snapshot [" + dir.Name + "]: " + e.Message);
            }
            return snap;
        }

        // Harmony prefix: returns false to skip the original Initialize and run ours instead.
        private static bool InitializePrefix()
        {
            if (_started)
            {
                // The original has no idempotence guard: a second ByteLoad would reload
                // all 353 MB again (and duplicate the info entries in levelSetInfos).
                return false;
            }
            _started = true;
            try
            {
                string pluginDir = GetDiyPluginDir();
                if (!RunCheapInit(pluginDir))
                {
                    // Same failure conditions as the original (missing common file etc.); allow a retry.
                    _started = false;
                    return false;
                }
                GameObject host = new GameObject("DIYLevelFastInitHost");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<FastInitHost>().Run(pluginDir);
            }
            catch (Exception e)
            {
                _started = false;
                Log.LogError("FastInit init failed (DIY levels unavailable this attempt): " + e);
            }
            return false;
        }

        // Must resolve OC2DIYLevel.dll's own location: GetExecutingAssembly() here would
        // point at this plugin's directory instead.
        private static string GetDiyPluginDir()
        {
            return Path.GetDirectoryName(typeof(DIYLevelAssetBundleManager).Assembly.Location);
        }

        // The cheap part of the original Initialize: once done, IsInitialized is true and
        // every consumer patch behaves exactly as with the original.
        private static bool RunCheapInit(string pluginDir)
        {
            DIYLevelAssetBundleManager prevInst = FiInstance.GetValue(null) as DIYLevelAssetBundleManager;
            GameObject go = new GameObject("DIYLevelAssetBundleManager");
            UnityEngine.Object.DontDestroyOnLoad(go);
            DIYLevelAssetBundleManager inst = go.AddComponent<DIYLevelAssetBundleManager>();
            if (prevInst != null && prevInst.gameObject != go)
            {
                UnityEngine.Object.Destroy(prevInst.gameObject);
            }
            FiInstance.SetValue(null, inst);

            string commonPath = Path.Combine(pluginDir, "common");
            if (!File.Exists(commonPath))
            {
                Log.LogWarning("missing file: " + commonPath);
                return false;
            }
            AssetBundle commonBundle = AssetBundle.LoadFromFile(commonPath);
            FiCover.SetValue(null, commonBundle.LoadAsset<Sprite>("diylevelcover"));
            FiSessionPrefab.SetValue(null, commonBundle.LoadAsset<GameObject>("DIYLevelGameSession"));
            FiConfigSo.SetValue(null, commonBundle.LoadAsset<PseudoPrefabSO>("LevelConfigTemplateSO"));
            FiCommonBundle.SetValue(null, commonBundle);

            DIYLevelAssetBundleManager.levelSetInfos = new List<KeyValuePair<string, LevelSetInfoSO>>();

            if (DIYLevelAssetBundleManager.diyDLCFrontendData == null)
            {
                DLCFrontendData data = ScriptableObject.CreateInstance<DLCFrontendData>();
                data.name = "DLC_DIYLevel";
                data.m_NameLocalizationKey = "\"More Levels\"";
                data.m_DescriptionLocalizationKey = OC2DIYLevel.UIUtils.GetLocalizedText("\"Enjoy extra levels from the community!\"", "\"游玩来自玩家社区的更多关卡！\"");
                data.m_PreviewImage = DIYLevelAssetBundleManager.GetCover();
                data.m_DLCID = 15;
                data.m_type = (DLCType)0;
                data.m_IsFreeDLC = true;
                data.m_IsSeasonPassDLC = false;
                DIYLevelAssetBundleManager.diyDLCFrontendData = data;
            }
            return true;
        }
    }

    // One-slot result passed into nested coroutines (iterators cannot have out/ref params).
    internal sealed class Box<T>
    {
        public T Value;
    }

    // Result slots filled by the shared info-set loader.
    internal sealed class InfoLoad
    {
        public AssetBundle Bundle;
        public LevelSetInfoSO So;
        public FileInfo InfoFile;
    }

    // Per-set disk fingerprint taken at load time; sync diffs against it.
    internal sealed class SetSnapshot
    {
        public string InfoFileName;
        public DateTime InfoMtimeUtc;
        public long InfoLength;
        public DateTime DirMaxMtimeUtc;
    }

    /// <summary>
    /// Cache for the scene directory StartEmptySession builds via
    /// DIYLevelAssetBundleManager.SetupSceneDirectoryData. Valid as long as the loaded
    /// level-set list is unchanged (entry-by-entry, order-sensitive); rebuilt in the
    /// background after the initial load or a hot sync. Abandoned directories are
    /// deliberately leaked: lingering sessions may still reference them, and destroying
    /// the tree could take shared bundle assets down with it. The configs reference
    /// RuntimePrefabManager clones (custom burger/pizza assembly prefabs), so while the
    /// cache is live the blanket isQuit:true clears are skipped (see
    /// ClearRuntimePrefabsPrefix) and the clones are accounted here instead; superseded
    /// clones are destroyed at the next blanket-clear point, which is where the original
    /// code destroyed everything.
    /// </summary>
    internal static class SceneDirectoryCache
    {
        private static readonly FieldInfo FiStarBoundaries =
            AccessTools.Field(typeof(SceneDirectoryData.PerPlayerCountDirectoryEntry), "m_PCStarBoundaries");
        private static readonly FieldInfo FiQuitClones =
            AccessTools.Field(typeof(RuntimePrefabManager), "runtimePrefabsClearOnQuit");
        private static readonly FieldInfo FiRestartClones =
            AccessTools.Field(typeof(RuntimePrefabManager), "runtimePrefabsClearOnRestart");

        private static SceneDirectoryData _cached;
        private static List<KeyValuePair<string, LevelSetInfoSO>> _cachedSets;
        private static bool _warming;

        // Clone accounting. clearOnRestart:false clones (the ones only isQuit:true clears
        // destroy): _liveClones back the published cache, the building lists back the two
        // possible in-flight builds, _ephemeralClones come from level-entry builds whose
        // configs die with the level. _garbageClones back superseded caches, destroyed
        // lazily at the next blanket-clear point. _restartClones (clearOnRestart:true)
        // exist only to keep the dialog-open backstop of the original blanket clear.
        private static List<GameObject> _liveClones = new List<GameObject>();
        private static List<GameObject> _warmBuildingClones = new List<GameObject>();
        private static List<GameObject> _syncBuildingClones = new List<GameObject>();
        private static List<GameObject> _garbageClones = new List<GameObject>();
        private static List<GameObject> _ephemeralClones = new List<GameObject>();
        private static List<GameObject> _restartClones = new List<GameObject>();
        private static bool _warmActive;
        // Bumped whenever a blanket isQuit:true clear actually runs; an in-flight warm
        // whose clones it destroyed must discard its partial directory.
        private static int _clearEpoch;

        internal static SceneDirectoryData Cached
        {
            get { return _cached; }
        }

        internal static bool Matches(List<KeyValuePair<string, LevelSetInfoSO>> infos)
        {
            return _cached != null && SameSets(infos, _cachedSets);
        }

        internal static void Publish(SceneDirectoryData data, List<KeyValuePair<string, LevelSetInfoSO>> infos, bool fromWarmBuild)
        {
            if (ReferenceEquals(data, _cached))
            {
                // The postfix after a cache-served call re-publishes the same object.
                return;
            }
            _garbageClones.AddRange(_liveClones);
            _garbageClones.AddRange(fromWarmBuild ? _syncBuildingClones : _warmBuildingClones);
            _liveClones = fromWarmBuild ? _warmBuildingClones : _syncBuildingClones;
            _warmBuildingClones = new List<GameObject>();
            _syncBuildingClones = new List<GameObject>();
            _cached = data;
            _cachedSets = new List<KeyValuePair<string, LevelSetInfoSO>>(infos);
        }

        internal static void RecordClone(GameObject clone, bool clearOnRestart)
        {
            if (clearOnRestart)
            {
                _restartClones.Add(clone);
            }
            else if (PseudoPrefabManager.isInCustomLevel)
            {
                // Level-entry builds (PseudoPrefabManager.SetAssetRef): the original
                // destroyed these at the next blanket clear, keep that timing.
                _ephemeralClones.Add(clone);
            }
            else if (_warmActive)
            {
                _warmBuildingClones.Add(clone);
            }
            else
            {
                _syncBuildingClones.Add(clone);
            }
        }

        // A blanket isQuit:true clear is about to run (cache not live): bump the epoch so
        // an in-flight warm discards entries whose clones just died.
        internal static void OnBlanketClearRunning()
        {
            _clearEpoch++;
        }

        // The blanket clear was skipped because the cache is live: destroy only what
        // nothing references anymore — superseded caches (their sessions are gone by the
        // time StartEmptySession reaches the clear) plus the level-entry and
        // clearOnRestart clones the original clear would have taken.
        internal static void OnBlanketClearSkipped()
        {
            int destroyed = DestroyClones(_garbageClones) + DestroyClones(_ephemeralClones)
                + DestroyClones(_restartClones);
            _garbageClones.Clear();
            _ephemeralClones.Clear();
            _restartClones.Clear();
            PruneRmLists();
            if (destroyed > 0)
            {
                FastInitPlugin.Log.LogDebug("clone gc: destroyed " + destroyed + " superseded runtime prefabs");
            }
        }

        internal static void ResetCloneTracking()
        {
            _liveClones.Clear();
            _warmBuildingClones.Clear();
            _syncBuildingClones.Clear();
            _garbageClones.Clear();
            _ephemeralClones.Clear();
            _restartClones.Clear();
        }

        private static int DestroyClones(List<GameObject> clones)
        {
            int destroyed = 0;
            foreach (GameObject clone in clones)
            {
                if (clone != null)
                {
                    UnityEngine.Object.DestroyImmediate(clone);
                    destroyed++;
                }
            }
            return destroyed;
        }

        // Keep RuntimePrefabManager's own lists free of entries we already destroyed, so
        // a later original clear never DestroyImmediates a dead object.
        private static void PruneRmLists()
        {
            try
            {
                List<GameObject> quitList = FiQuitClones.GetValue(null) as List<GameObject>;
                if (quitList != null)
                {
                    quitList.RemoveAll(delegate(GameObject x) { return x == null; });
                }
                List<GameObject> restartList = FiRestartClones.GetValue(null) as List<GameObject>;
                if (restartList != null)
                {
                    restartList.RemoveAll(delegate(GameObject x) { return x == null; });
                }
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogDebug("clone gc prune: " + e.Message);
            }
        }

        internal static void TryStartWarm()
        {
            if (_warming || FastInitPlugin.Instance == null)
            {
                return;
            }
            _warming = true;
            try
            {
                FastInitPlugin.Instance.StartCoroutine(WarmRoutine());
            }
            catch (Exception e)
            {
                _warming = false;
                FastInitPlugin.Log.LogDebug("warm start: " + e.Message);
            }
        }

        private static IEnumerator WarmRoutine()
        {
            bool failed = false;
            try
            {
                List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
                if (infos == null || infos.Count == 0 || Matches(infos))
                {
                    yield break;
                }
                // A blanket isQuit:true clear (fallback path while the cache is stale)
                // destroys clones already baked into finished entries; discard the whole
                // build if that happens and let the finally chain rebuild.
                int epoch = _clearEpoch;
                PseudoPrefabSO template = FastInitPlugin.FiConfigSo.GetValue(null) as PseudoPrefabSO;
                if (template == null)
                {
                    failed = true;
                    FastInitPlugin.Log.LogWarning("warm: config template missing, skipping");
                    yield break;
                }
                List<LevelInfoSO> levels = new List<LevelInfoSO>();
                foreach (KeyValuePair<string, LevelSetInfoSO> kv in infos)
                {
                    if (kv.Value != null && kv.Value.levelInfos != null)
                    {
                        levels.AddRange(kv.Value.levelInfos);
                    }
                }
                SceneDirectoryData data = ScriptableObject.CreateInstance<SceneDirectoryData>();
                data.name = "DIYLevelSceneDirectory";
                List<SceneDirectoryData.SceneDirectoryEntry> entries =
                    new List<SceneDirectoryData.SceneDirectoryEntry>(levels.Count);
                int index = 0;
                while (index < levels.Count)
                {
                    if (epoch != _clearEpoch)
                    {
                        FastInitPlugin.Log.LogInfo("warm: runtime prefabs cleared mid-build, discarding partial directory");
                        yield break;
                    }
                    // Pause while inside a DIY level so the build never competes with
                    // scene loading; resume in the frontend.
                    while (PseudoPrefabManager.isInCustomLevel)
                    {
                        yield return null;
                    }
                    float deadline = Time.realtimeSinceStartup + 0.008f;
                    while (index < levels.Count && Time.realtimeSinceStartup < deadline)
                    {
                        SceneDirectoryData.SceneDirectoryEntry entry;
                        _warmActive = true;
                        try
                        {
                            entry = BuildEntry(template, levels[index]);
                        }
                        finally
                        {
                            _warmActive = false;
                        }
                        if (entry == null)
                        {
                            failed = true;
                            FastInitPlugin.Log.LogWarning("warm: aborted at level " + (index + 1) + "/" + levels.Count);
                            yield break;
                        }
                        entries.Add(entry);
                        index++;
                    }
                    yield return null;
                }
                data.Scenes = entries.ToArray();
                List<KeyValuePair<string, LevelSetInfoSO>> now = DIYLevelAssetBundleManager.levelSetInfos;
                // Publish only if the set list is unchanged since the build started, no
                // concurrent build already published for it, and no blanket clear
                // destroyed clones baked into the entries (the last yield is a window).
                if (SameSets(infos, now) && !Matches(now) && epoch == _clearEpoch)
                {
                    Publish(data, now, true);
                    FastInitPlugin.Log.LogInfo("scene directory warmed: " + entries.Count + " levels, cache ready");
                }
            }
            finally
            {
                _warming = false;
                // A hot sync may have changed the set list mid-build; chain a fresh
                // warm unless we already failed (retry loops would spin forever).
                if (!failed && !Matches(DIYLevelAssetBundleManager.levelSetInfos))
                {
                    TryStartWarm();
                }
            }
        }

        // Per-level slice of the original LevelConfigSetup.SetupSceneDirectoryData; the
        // heavy SetupConfig itself is reused verbatim. Returns null on failure, which
        // aborts the warm (the next dialog open then surfaces the error synchronously,
        // exactly like the original).
        private static SceneDirectoryData.SceneDirectoryEntry BuildEntry(PseudoPrefabSO template, LevelInfoSO info)
        {
            try
            {
                SceneDirectoryData.SceneDirectoryEntry entry = new SceneDirectoryData.SceneDirectoryEntry();
                entry.Label = "\"" + OC2DIYLevel.UIUtils.GetLocalizedText(info.levelName, info.levelNameZH) + "\"";
                entry.LoadScreenOverride = info.screenshot;
                entry.AvailableInLobby = false;
                entry.SceneVarients = new SceneDirectoryData.PerPlayerCountDirectoryEntry[4];
                for (int playerCount = 1; playerCount <= 4; playerCount++)
                {
                    SceneDirectoryData.PerPlayerCountDirectoryEntry variant =
                        new SceneDirectoryData.PerPlayerCountDirectoryEntry();
                    variant.PlayerCount = playerCount;
                    variant.LevelConfig = (LevelConfigBase)LevelConfigSetup.SetupConfig(template, info, playerCount);
                    variant.SceneName = info.sceneName;
                    variant.Screenshot = info.screenshot;
                    LevelConfigSetupPerPlayerCountSO perCount = GetConfigPerPlayerCount(info, playerCount);
                    FiStarBoundaries.SetValue(variant, new SceneDirectoryData.StarBoundaries
                    {
                        m_OneStarScore = perCount.m_OneStarScore,
                        m_TwoStarScore = perCount.m_TwoStarScore,
                        m_ThreeStarScore = perCount.m_ThreeStarScore,
                        m_FourStarScore = perCount.m_FourStarScore
                    });
                    entry.SceneVarients[playerCount - 1] = variant;
                }
                return entry;
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogWarning("warm: level [" + info.name + "] failed: " + e.Message);
                return null;
            }
        }

        private static LevelConfigSetupPerPlayerCountSO GetConfigPerPlayerCount(LevelInfoSO info, int playerCount)
        {
            return new LevelConfigSetupPerPlayerCountSO[4] { info.config_1p, info.config_2p, info.config_3p, info.config_4p }[playerCount - 1];
        }

        private static bool SameSets(List<KeyValuePair<string, LevelSetInfoSO>> a, List<KeyValuePair<string, LevelSetInfoSO>> b)
        {
            if (a == null || b == null || a.Count != b.Count)
            {
                return false;
            }
            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i].Key, b[i].Key, StringComparison.OrdinalIgnoreCase)
                    || !ReferenceEquals(a[i].Value, b[i].Value))
                {
                    return false;
                }
            }
            return true;
        }
    }

    internal class FastInitHost : MonoBehaviour
    {
        private sealed class PendingSet
        {
            public DirectoryInfo Dir;
            public FileInfo File;
            public AssetBundleCreateRequest Req;
        }

        private const float AsyncTimeoutSeconds = 15f;

        private bool _finished;

        public void Run(string pluginDir)
        {
            StartCoroutine(LoadHeavy(pluginDir));
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

        // Polls an async operation; yield this from a coroutine to await it. Gives up
        // after the timeout — callers then fall back to the sync path.
        internal static IEnumerator WaitOp(AsyncOperation op, string what)
        {
            float start = Time.realtimeSinceStartup;
            while (!op.isDone)
            {
                if (Time.realtimeSinceStartup - start > AsyncTimeoutSeconds)
                {
                    FastInitPlugin.Log.LogWarning(what + ": async did not finish in " + AsyncTimeoutSeconds + "s, falling back to sync");
                    yield break;
                }
                yield return null;
            }
        }

        // Await an existing create request, falling back to a sync load on async failure
        // or timeout; result.Value is null on total failure.
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

            // Parallel prefetch: queue the file reads of all info bundles onto worker threads
            // at once (one request per frame), then consume them in directory order —
            // disk read times overlap, far faster than one-by-one serial loading.
            List<PendingSet> pending = new List<PendingSet>();
            foreach (DirectoryInfo dir in SafeGetDirs(Path.Combine(pluginDir, "levels")))
            {
                List<FileInfo> infoFiles = SafeGetFiles(dir, "info*");
                if (infoFiles.Count == 0)
                {
                    FastInitPlugin.Log.LogWarning("missing info file: " + dir.Name);
                    continue;
                }
                pending.Add(new PendingSet
                {
                    Dir = dir,
                    File = infoFiles[0],
                    Req = AssetBundle.LoadFromFileAsync(infoFiles[0].FullName)
                });
                yield return null;
            }
            FastInitPlugin.TotalSets = pending.Count;
            FastInitPlugin.Log.LogInfo("prefetch queued: " + pending.Count + " info bundles");

            int sets = 0;
            List<KeyValuePair<string, LevelSetInfoSO>> infos = DIYLevelAssetBundleManager.levelSetInfos;
            foreach (PendingSet p in pending)
            {
                FastInitPlugin.Log.LogInfo("consuming level set [" + p.Dir.Name + "]");
                Box<AssetBundle> bundleBox = new Box<AssetBundle>();
                yield return FinishBundleLoad(p.Req, p.File.FullName, p.Dir.Name, bundleBox);
                if (bundleBox.Value == null)
                {
                    FastInitPlugin.Log.LogWarning("failed loading info bundle of " + p.Dir.Name);
                    continue;
                }
                Box<LevelSetInfoSO> soBox = new Box<LevelSetInfoSO>();
                yield return LoadInfoAsset(bundleBox.Value, p.Dir.Name + " (LevelSetInfo)", soBox);
                if (soBox.Value == null)
                {
                    FastInitPlugin.Log.LogWarning("missing LevelSetInfo in " + p.Dir.Name);
                    bundleBox.Value.Unload(false);
                    continue;
                }
                infos.Add(new KeyValuePair<string, LevelSetInfoSO>(p.Dir.FullName, soBox.Value));
                FastInitPlugin.InfoBundles[p.Dir.FullName] = bundleBox.Value;
                FastInitPlugin.Snapshots[p.Dir.FullName] = FastInitPlugin.ReadSnapshot(p.Dir, p.File);
                sets++;
                FastInitPlugin.NotifySetAdded();
                FastInitPlugin.Log.LogInfo("level set [" + p.Dir.Name + "] ready (" + sets + " total)");
            }

            _finished = true;
            FastInitPlugin.FinishLoading();
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
    }
}
