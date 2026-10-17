using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using LevelEditorStub;
using OC2DIYLevel;
using OC2DIYLevel.CustomArcade;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

namespace DIYLevelFastInit
{
    /// <summary>
    /// Keeps a lightweight DIY level catalog, loading info resources and building
    /// configurations only for the selected level. Retains async discovery and hot sync.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("Overcooked2.exe")]
    [BepInDependency("dev.gua.overcooked.diylevel")]
    public class FastInitPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "oc2.diylevel.fastinit";
        public const string PluginName = "DIYLevel FastInit";
        public const string PluginVersion = "1.6.5";

        internal static ManualLogSource Log;

        private static bool _started;
        private bool _patchesReady;

        private static readonly FieldInfo FiCommonBundle = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "commonBundle");
        private static readonly FieldInfo FiCover = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "diyLevelCover");
        private static readonly FieldInfo FiSessionPrefab = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "diyLevelGameSessionPrefab");
        internal static readonly FieldInfo FiConfigSo = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "configTemplateSO");
        private static readonly FieldInfo FiInstance = AccessTools.Field(typeof(DIYLevelAssetBundleManager), "Instance");
        private static readonly FieldInfo FiSetMenu = AccessTools.Field(typeof(DIYLevelEntryUI), "levelSetSelectionMenu");
        private static readonly MethodInfo MiAddSetButton = AccessTools.Method(typeof(DIYLevelEntryUI), "AddLevelSetButton");

        private static int _builtCount;

        internal static bool Loading;
        internal static bool Syncing;
        internal static int TotalSets;

        internal static FastInitPlugin Instance;

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
            try
            {
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
                harmony.Patch(AccessTools.Method(typeof(DIYLevelAssetBundleManager), "SetupSceneDirectoryData"),
                    prefix: new HarmonyMethod(typeof(FastInitPlugin), nameof(SetupDirectoryPrefix)));
                OnDemandResources.Install(harmony);
                _patchesReady = true;
                Log.LogInfo("DIYLevel FastInit ready: lightweight catalog, on-demand level resources.");
            }
            catch (Exception e)
            {
                harmony.UnpatchSelf();
                Log.LogError("FastInit hooks could not be installed; original DIY loader retained: " + e);
            }
        }

        private void Start()
        {
            if (!_patchesReady) return;
            try { OnDemandResources.InstallPreviewHooks(new Harmony(PluginGuid)); }
            catch (Exception e) { Log.LogWarning("thumbnail adapter unavailable: " + e); }
        }

        private void OnGUI()
        {
            bool catalogLoading = Loading && TotalSets > 0;
            string levelLoading = OnDemandResources.LevelLoadProgress;
            bool thumbnails = OnDemandResources.WarmingThumbnails && !OnDemandResources.IsTransitioning && !PseudoPrefabManager.isInCustomLevel;
            if (!catalogLoading && !thumbnails && levelLoading == null)
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
                string text = levelLoading != null ? "DIY level preparing " + levelLoading
                    : catalogLoading ? "DIY levels loading " + done + "/" + TotalSets
                    : "DIY previews caching " + OnDemandResources.ThumbnailDone + "/" + OnDemandResources.ThumbnailTotal;
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
            ThumbnailCache.Invalidate();
            Loading = false;
            FrontendOptionsMenu menu = TryGetMenu();
            if (menu != null)
            {
                UpdateMenuHeaderProgress(menu);
                EnsureReloadButton(menu);
            }
            OnDemandResources.QueueThumbnailWarmup();
        }

        private static bool SetupDirectoryPrefix(ref SceneDirectoryData __result)
        {
            __result = SceneDirectoryCache.GetDirectory();
            return false;
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
            if (OnDemandResources.IsTransitioning)
            {
                Log.LogInfo("sync skipped: a DIY level load is in progress");
                return;
            }
            // Sessions can linger in the frontend; PrepareLevel replaces stale directories.
            if (GameObject.Find("/Frontend/FrontendParent/FrontendRootMenu") == null)
            {
                Log.LogInfo("sync skipped: not in the frontend scene");
                return;
            }
            Instance.StartCoroutine(FastInitHost.Guard(Instance.SyncRoutine(), "catalog sync"));
        }

        private IEnumerator SyncRoutine()
        {
            Loading = true;
            Syncing = true;
            try
            {
                Log.LogInfo("sync started");
                OnDemandResources.ResetFrontend();
                yield return OnDemandResources.WaitForPreview();
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
                            Snapshots[dir.FullName] = ReadSnapshot(dir, FastInitHost.SafeGetFiles(dir, "info*")[0]);
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
                    List<KeyValuePair<string, LevelSetInfoSO>> ordered = new List<KeyValuePair<string, LevelSetInfoSO>>();
                    foreach (DirectoryInfo dir in dirs)
                    {
                        int index = IndexOfSet(DIYLevelAssetBundleManager.levelSetInfos, dir.FullName);
                        if (index >= 0) ordered.Add(DIYLevelAssetBundleManager.levelSetInfos[index]);
                    }
                    DIYLevelAssetBundleManager.levelSetInfos = ordered;
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
                Syncing = false;
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

        internal static IEnumerator LoadInfoSet(DirectoryInfo dir, InfoLoad result)
        {
            List<FileInfo> files = FastInitHost.SafeGetFiles(dir, "info*");
            if (files.Count == 0)
            {
                Log.LogWarning("missing info file: " + dir.Name);
                yield break;
            }
            result.InfoFile = files[0];
            result.So = LevelCatalogCache.TryRead(dir, result.InfoFile);
            if (result.So != null) yield break;

            Box<AssetBundle> bundle = new Box<AssetBundle>();
            Box<LevelSetInfoSO> full = new Box<LevelSetInfoSO>();
            try
            {
                yield return OnDemandResources.LoadCatalogBundle(result.InfoFile.FullName, bundle);
                if (bundle.Value == null) yield break;
                yield return FastInitHost.LoadInfoAsset(bundle.Value, dir.Name, full);
                if (full.Value != null)
                    result.So = LevelCatalogCache.Extract(dir, result.InfoFile, full.Value);
                else
                    Log.LogWarning("missing LevelSetInfo in " + dir.Name);
            }
            finally
            {
                full.Value = null;
                bundle.Value = null;
                OnDemandResources.ReleaseCatalogBundle();
            }
            yield return OnDemandResources.WaitForCleanup();
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
        internal static string GetDiyPluginDir()
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

    internal sealed class PlayerMetadata
    {
        public int orderLifeTime, timeBetweenOrders, plateReturnTime, roundTime;
        public float survivalTimeMultiplier;
        public int one, two, three, four;

        internal static PlayerMetadata From(LevelConfigSetupPerPlayerCountSO value)
        {
            if (value == null) throw new IOException("Missing player-count settings");
            return new PlayerMetadata
            {
                orderLifeTime = value.orderLifeTime, timeBetweenOrders = value.timeBetweenOrders,
                plateReturnTime = value.plateReturnTime, roundTime = value.roundTime,
                survivalTimeMultiplier = value.survivalTimeMultiplier,
                one = value.m_OneStarScore, two = value.m_TwoStarScore,
                three = value.m_ThreeStarScore, four = value.m_FourStarScore
            };
        }

        internal LevelConfigSetupPerPlayerCountSO Create()
        {
            LevelConfigSetupPerPlayerCountSO value = ScriptableObject.CreateInstance<LevelConfigSetupPerPlayerCountSO>();
            value.hideFlags = HideFlags.DontUnloadUnusedAsset;
            value.orderLifeTime = orderLifeTime;
            value.timeBetweenOrders = timeBetweenOrders;
            value.plateReturnTime = plateReturnTime;
            value.roundTime = roundTime;
            value.survivalTimeMultiplier = survivalTimeMultiplier;
            value.m_OneStarScore = one;
            value.m_TwoStarScore = two;
            value.m_ThreeStarScore = three;
            value.m_FourStarScore = four;
            return value;
        }
    }

    internal sealed class LevelMetadata
    {
        public string name, levelName, levelNameZH, sceneName;
        public string[] dependencies;
        public PlayerMetadata[] players;
    }

    internal sealed class SetMetadata
    {
        public int schema;
        public string directory, infoFile, name, levelSetName, levelSetNameZH, author, uid, version;
        public long length, modified;
        public LevelMetadata[] levels;

        internal bool IsValid(DirectoryInfo dir, FileInfo file)
        {
            if (schema != 2 || !string.Equals(directory, dir.FullName, StringComparison.OrdinalIgnoreCase)
                || infoFile != file.Name || length != file.Length || modified != file.LastWriteTimeUtc.Ticks
                || levels == null || levels.Length > 20000) return false;
            foreach (LevelMetadata level in levels)
            {
                if (level == null || string.IsNullOrEmpty(level.sceneName) || level.dependencies == null
                    || level.players == null || level.players.Length != 4) return false;
                foreach (PlayerMetadata player in level.players) if (player == null) return false;
            }
            return true;
        }

        internal LevelSetInfoSO Create()
        {
            LevelSetInfoSO set = ScriptableObject.CreateInstance<LevelSetInfoSO>();
            try
            {
                set.hideFlags = HideFlags.DontUnloadUnusedAsset;
                set.name = name;
                set.levelSetName = levelSetName;
                set.levelSetNameZH = levelSetNameZH;
                set.author = author;
                set.uid = uid;
                set.version = version;
                set.levelInfos = new LevelInfoSO[levels.Length];
                for (int i = 0; i < levels.Length; i++)
                {
                    LevelMetadata data = levels[i];
                    LevelInfoSO level = ScriptableObject.CreateInstance<LevelInfoSO>();
                    set.levelInfos[i] = level;
                    level.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    level.name = data.name;
                    level.levelName = data.levelName;
                    level.levelNameZH = data.levelNameZH;
                    level.sceneName = data.sceneName;
                    level.dependencies = data.dependencies;
                    level.config_1p = data.players[0].Create();
                    level.config_2p = data.players[1].Create();
                    level.config_3p = data.players[2].Create();
                    level.config_4p = data.players[3].Create();
                }
                return set;
            }
            catch
            {
                LevelCatalogCache.DestroyLightweight(set);
                throw;
            }
        }
    }

    internal static class LevelCatalogCache
    {
        private static string CachePath(DirectoryInfo dir)
        {
            using (SHA256 hash = SHA256.Create())
            {
                string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(dir.FullName.ToLowerInvariant()))).Replace("-", "");
                return Path.Combine(Path.Combine(Paths.CachePath, "DIYLevelFastInit"), key + ".catalog");
            }
        }

        internal static LevelSetInfoSO TryRead(DirectoryInfo dir, FileInfo file)
        {
            try
            {
                string path = CachePath(dir);
                if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) return null;
                SetMetadata data;
                using (FileStream stream = File.OpenRead(path))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8)) data = ReadData(reader);
                if (data == null || !data.IsValid(dir, file)) return null;
                LevelSetInfoSO result = data.Create();
                FastInitPlugin.Log.LogInfo("catalog cache hit: " + dir.Name);
                return result;
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogWarning("catalog cache ignored [" + dir.Name + "]: " + e.Message);
                return null;
            }
        }

        internal static LevelSetInfoSO Extract(DirectoryInfo dir, FileInfo file, LevelSetInfoSO full)
        {
            try
            {
                SetMetadata data = new SetMetadata
                {
                    schema = 2, directory = dir.FullName, infoFile = file.Name,
                    length = file.Length, modified = file.LastWriteTimeUtc.Ticks,
                    name = full.name, levelSetName = full.levelSetName, levelSetNameZH = full.levelSetNameZH,
                    author = full.author, uid = full.uid, version = full.version,
                    levels = new LevelMetadata[full.levelInfos.Length]
                };
                for (int i = 0; i < data.levels.Length; i++)
                {
                    LevelInfoSO level = full.levelInfos[i];
                    if (level == null) throw new IOException("Null level in catalog");
                    data.levels[i] = new LevelMetadata
                    {
                        name = level.name, levelName = level.levelName, levelNameZH = level.levelNameZH,
                        sceneName = level.sceneName, dependencies = level.dependencies ?? new string[0],
                        players = new[] { PlayerMetadata.From(level.config_1p), PlayerMetadata.From(level.config_2p),
                            PlayerMetadata.From(level.config_3p), PlayerMetadata.From(level.config_4p) }
                    };
                }
                if (!data.IsValid(dir, file)) throw new IOException("Invalid level catalog");
                LevelSetInfoSO result = data.Create();
                Write(dir, data);
                return result;
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogError("catalog extraction failed [" + dir.Name + "]: " + e);
                return null;
            }
        }

        private static void Write(DirectoryInfo dir, SetMetadata data)
        {
            string temporary = null;
            try
            {
                string path = CachePath(dir);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                temporary = path + ".tmp";
                using (FileStream stream = File.Create(temporary))
                using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8)) WriteData(writer, data);
                SetMetadata restored;
                using (FileStream stream = File.OpenRead(temporary))
                using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8)) restored = ReadData(reader);
                if (!restored.IsValid(dir, new FileInfo(Path.Combine(dir.FullName, data.infoFile))))
                    throw new IOException("Catalog binary did not preserve metadata");
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogWarning("catalog cache write skipped: " + e.Message);
            }
            finally
            {
                if (temporary != null && File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static void WriteText(BinaryWriter writer, string value)
        {
            if (value == null) { writer.Write(-1); return; }
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length > 1024 * 1024) throw new IOException("Catalog string too long");
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadText(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length == -1) return null;
            if (length < 0 || length > 1024 * 1024 || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new IOException("Invalid catalog string length");
            return Encoding.UTF8.GetString(reader.ReadBytes(length));
        }

        private static int ReadCount(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > 20000) throw new IOException("Invalid catalog array length");
            return count;
        }

        internal static void WriteData(BinaryWriter writer, SetMetadata data)
        {
            writer.Write(0x32434944);
            writer.Write(data.schema);
            WriteText(writer, data.directory);
            WriteText(writer, data.infoFile);
            writer.Write(data.length);
            writer.Write(data.modified);
            WriteText(writer, data.name);
            WriteText(writer, data.levelSetName);
            WriteText(writer, data.levelSetNameZH);
            WriteText(writer, data.author);
            WriteText(writer, data.uid);
            WriteText(writer, data.version);
            writer.Write(data.levels.Length);
            foreach (LevelMetadata level in data.levels)
            {
                WriteText(writer, level.name);
                WriteText(writer, level.levelName);
                WriteText(writer, level.levelNameZH);
                WriteText(writer, level.sceneName);
                writer.Write(level.dependencies.Length);
                foreach (string dependency in level.dependencies) WriteText(writer, dependency);
                foreach (PlayerMetadata player in level.players)
                {
                    writer.Write(player.orderLifeTime);
                    writer.Write(player.timeBetweenOrders);
                    writer.Write(player.plateReturnTime);
                    writer.Write(player.roundTime);
                    writer.Write(player.survivalTimeMultiplier);
                    writer.Write(player.one);
                    writer.Write(player.two);
                    writer.Write(player.three);
                    writer.Write(player.four);
                }
            }
        }

        internal static SetMetadata ReadData(BinaryReader reader)
        {
            if (reader.ReadInt32() != 0x32434944 || reader.ReadInt32() != 2)
                throw new IOException("Unsupported catalog format");
            SetMetadata data = new SetMetadata();
            data.schema = 2;
            data.directory = ReadText(reader);
            data.infoFile = ReadText(reader);
            data.length = reader.ReadInt64();
            data.modified = reader.ReadInt64();
            data.name = ReadText(reader);
            data.levelSetName = ReadText(reader);
            data.levelSetNameZH = ReadText(reader);
            data.author = ReadText(reader);
            data.uid = ReadText(reader);
            data.version = ReadText(reader);
            data.levels = new LevelMetadata[ReadCount(reader)];
            for (int i = 0; i < data.levels.Length; i++)
            {
                LevelMetadata level = new LevelMetadata();
                data.levels[i] = level;
                level.name = ReadText(reader);
                level.levelName = ReadText(reader);
                level.levelNameZH = ReadText(reader);
                level.sceneName = ReadText(reader);
                level.dependencies = new string[ReadCount(reader)];
                for (int j = 0; j < level.dependencies.Length; j++) level.dependencies[j] = ReadText(reader);
                level.players = new PlayerMetadata[4];
                for (int j = 0; j < 4; j++)
                {
                    level.players[j] = new PlayerMetadata
                    {
                        orderLifeTime = reader.ReadInt32(), timeBetweenOrders = reader.ReadInt32(),
                        plateReturnTime = reader.ReadInt32(), roundTime = reader.ReadInt32(),
                        survivalTimeMultiplier = reader.ReadSingle(),
                        one = reader.ReadInt32(), two = reader.ReadInt32(), three = reader.ReadInt32(), four = reader.ReadInt32()
                    };
                }
            }
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new IOException("Trailing catalog data");
            return data;
        }

        internal static void DestroyLightweight(LevelSetInfoSO set)
        {
            if (set == null) return;
            if (set.levelInfos != null)
                foreach (LevelInfoSO level in set.levelInfos)
                {
                    if (level == null) continue;
                    UnityEngine.Object.DestroyImmediate(level.config_1p);
                    UnityEngine.Object.DestroyImmediate(level.config_2p);
                    UnityEngine.Object.DestroyImmediate(level.config_3p);
                    UnityEngine.Object.DestroyImmediate(level.config_4p);
                    UnityEngine.Object.DestroyImmediate(level);
                }
            UnityEngine.Object.DestroyImmediate(set);
        }
    }

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

    internal static class OnDemandResources
    {
        private sealed class LoadedSet
        {
            internal AssetBundle Bundle;
            internal LevelSetInfoSO Info;
            internal readonly HashSet<string> Required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, HashSet<string>> SceneDependencies = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class LevelResources
        {
            internal readonly HashSet<UnityEngine.Object> Objects = new HashSet<UnityEngine.Object>();
        }

        private sealed class PreviewRequest
        {
            internal Image Image;
            internal LevelInfoSO Level;
            internal string Path;
            internal bool Started;
        }

        private static readonly Dictionary<string, LoadedSet> Sets = new Dictionary<string, LoadedSet>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, AssetBundle> InfoBundles = new Dictionary<string, AssetBundle>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, AssetBundleCreateRequest> PendingBundles = new Dictionary<string, AssetBundleCreateRequest>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, AssetBundleRequest> PendingInfos = new Dictionary<string, AssetBundleRequest>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> _catalogBundles;
        private static HashSet<string> _previewBundles;
        private static PreviewRequest _previewRequest;
        private static bool _previewRunning;
        private static bool _previewLoading, _warmRunning, _warmLoading, _warmQueued, _thumbnailAdapter;
        private static int _warmGeneration;
        private static HashSet<string> _warmBundles;
        private static HashSet<string> _preparingBundles;
        private static bool _serverPreparing, _resumeServerLoad;
        internal static string LevelLoadProgress;
        private static float _lastPreviewTime;
        internal static int ThumbnailDone, ThumbnailTotal;
        internal static bool WarmingThumbnails { get { return _warmRunning; } }
        private static readonly Dictionary<string, LevelResources> Levels = new Dictionary<string, LevelResources>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<Scene, LevelResources> SceneObjects = new Dictionary<Scene, LevelResources>();
        private static readonly FieldInfo QuitClones = AccessTools.Field(typeof(RuntimePrefabManager), "runtimePrefabsClearOnQuit");
        private static readonly FieldInfo RestartClones = AccessTools.Field(typeof(RuntimePrefabManager), "runtimePrefabsClearOnRestart");
        private static string _pendingScene, _previewPath, _buildingScene;
        private static bool _directoryBuild;
        private static bool _cleanupQueued, _cleanupRequested;

        internal static bool IsTransitioning { get { return _pendingScene != null; } }

        internal static void Install(Harmony harmony)
        {
            Patch(harmony, typeof(DIYLevelAssetBundleManager), "LoadLevelServer", nameof(ServerLoadPrefix), null);
            Patch(harmony, typeof(DIYLevelAssetBundleManager), "LoadLevelClientPatch", nameof(LoadPrefix), null);
            Patch(harmony, typeof(DIYLevelEntryUI), "OnLevelSelected", nameof(SelectionPrefix), null);
            Patch(harmony, typeof(RuntimePrefabManager), "ClearAllRuntimePrefabs", nameof(ClearPrefix), null);
            Patch(harmony, typeof(RuntimePrefabManager), "CloneAsInactivePrefab", null, nameof(ClonePostfix));
            harmony.Patch(AccessTools.Method(typeof(LevelConfigSetup), "SetupConfig"),
                prefix: new HarmonyMethod(typeof(OnDemandResources), nameof(ConfigPrefix)),
                postfix: new HarmonyMethod(typeof(OnDemandResources), nameof(ConfigPostfix)),
                finalizer: new HarmonyMethod(typeof(OnDemandResources), nameof(ConfigFinalizer)));
            Patch(harmony, typeof(RecipeHelper), "GetOrderDefinitionNodeCustomRecipe", null, nameof(CustomRecipePostfix));
            Patch(harmony, typeof(RecipeHelper), "GetOrderDefinitionNodeCustomRecipeOptional", null, nameof(CustomRecipePostfix));
            foreach (MethodInfo method in typeof(RecipeHelper).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                if (method.Name == "GetOrderToPrefabLookup")
                    harmony.Patch(method, postfix: new HarmonyMethod(typeof(OnDemandResources), nameof(OwnedPostfix)));
            SceneManager.sceneLoaded += SceneLoaded;
            SceneManager.sceneUnloaded += SceneUnloaded;
        }

        private static void Patch(Harmony harmony, Type type, string name, string prefix, string postfix)
        {
            MethodInfo method = AccessTools.Method(type, name);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            harmony.Patch(method,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(OnDemandResources), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(OnDemandResources), postfix));
        }

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
            Box<LevelSetInfoSO> info = new Box<LevelSetInfoSO>();
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
                yield return EnsureInfoAssetAsync(path, InfoBundles[files[0].FullName], info, -10);
                foreach (LevelInfoSO catalogLevel in missing)
                {
                    while (!ThumbnailCache.HasWriteCapacity && !WarmupPaused() && generation == _warmGeneration) yield return null;
                    if (WarmupPaused() || generation != _warmGeneration) { interrupted.Value = true; yield break; }
                    if (ThumbnailCache.IsReady(path, catalogLevel.sceneName)) continue;
                    foreach (LevelInfoSO level in info.Value.levelInfos)
                        if (string.Equals(level.sceneName, catalogLevel.sceneName, StringComparison.OrdinalIgnoreCase))
                        {
                            try { ThumbnailCache.Capture(path, catalogLevel.sceneName, level.screenshot, false); }
                            catch (Exception e) { FastInitPlugin.Log.LogWarning("thumbnail prewarm skipped [" + catalogLevel.sceneName + "]: " + e.Message); }
                            break;
                        }
                    yield return null;
                }
                while (ThumbnailCache.WritesBusy) yield return null;
            }
            finally
            {
                info.Value = null;
                _warmLoading = false;
                _warmBundles = null;
                ReleaseUnused();
            }
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

        internal static IEnumerator LoadCatalogBundle(string path, Box<AssetBundle> result)
        {
            _catalogBundles = InfoBundleGraph.Required(path, null);
            foreach (string required in _catalogBundles)
                yield return EnsureInfoBundleAsync(required);
            result.Value = InfoBundles[path];
        }

        private static IEnumerator EnsureInfoBundleAsync(string path, int priority = 0)
        {
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

        private static void LoadPrefix(string sceneName)
        {
            if (FastInitPlugin.Syncing) throw new InvalidOperationException("DIY catalog refresh is in progress; retry after it completes");
            try { PrepareLevel(sceneName); }
            catch (Exception e)
            {
                _pendingScene = null;
                FastInitPlugin.Log.LogError("DIY level preparation failed [" + sceneName + "]: " + e);
                ReleaseUnused();
                throw;
            }
        }

        private static bool ServerLoadPrefix(string sceneName)
        {
            if (_resumeServerLoad) return true;
            if (FastInitPlugin.Syncing) throw new InvalidOperationException("DIY catalog refresh is in progress; retry after it completes");
            if (_serverPreparing)
            {
                FastInitPlugin.Log.LogDebug("DIY level preparation already pending; ignoring duplicate load: " + sceneName);
                return false;
            }
            if (FastInitPlugin.Instance == null) { LoadPrefix(sceneName); return true; }
            string path = FindScenePath(sceneName);
            _pendingScene = sceneName;
            _serverPreparing = true;
            LevelLoadProgress = "...";
            FastInitPlugin.Instance.StartCoroutine(FastInitHost.Guard(PrepareServerLevel(path, sceneName), "DIY level preparation [" + sceneName + "]"));
            return false;
        }

        private static IEnumerator PrepareServerLevel(string path, string scene)
        {
            bool resumed = false;
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                yield return null;
                FastInitPlugin.Log.LogInfo("async level preparation started: " + scene);
                CancelPreview();
                ThumbnailCache.ClearMemory();
                while (_warmLoading || _previewLoading || _catalogBundles != null) yield return null;
                ReleaseUnused();
                List<FileInfo> files = FastInitHost.SafeGetFiles(new DirectoryInfo(path), "info*");
                if (files.Count == 0) throw new FileNotFoundException("Missing info bundle", path);
                _preparingBundles = InfoBundleGraph.Required(files[0].FullName, Path.Combine(path, scene));
                foreach (string required in _preparingBundles) yield return EnsureInfoBundleAsync(required, 10);
                LoadedSet set;
                if (!Sets.TryGetValue(path, out set))
                {
                    Box<LevelSetInfoSO> info = new Box<LevelSetInfoSO>();
                    try
                    {
                        AssetBundle bundle = InfoBundles[files[0].FullName];
                        yield return EnsureInfoAssetAsync(path, bundle, info, 10);
                        set = new LoadedSet { Bundle = bundle, Info = info.Value };
                        set.Required.UnionWith(InfoBundleGraph.Required(files[0].FullName, null));
                        Sets.Add(path, set);
                        FastInitPlugin.Log.LogInfo("on-demand info loaded: " + Path.GetFileName(path) + " (" + InfoBundles.Count + " bundles tracked, async level entry)");
                    }
                    finally { info.Value = null; }
                }
                set.SceneDependencies[scene] = _preparingBundles;
                FastInitPlugin.Log.LogInfo("async level resources ready: " + scene + " (" + timer.ElapsedMilliseconds + " ms elapsed)");
                LevelLoadProgress = "0/4";
                LevelInfoSO full = Resolve(path, scene);
                SceneDirectoryData.SceneDirectoryEntry entry = PrepareSessionEntry(scene);
                entry.LoadScreenOverride = full.screenshot;
                for (int i = 0; i < entry.SceneVarients.Length; i++)
                {
                    System.Diagnostics.Stopwatch configTimer = System.Diagnostics.Stopwatch.StartNew();
                    PrepareVariant(full, entry.SceneVarients[i]);
                    LevelLoadProgress = (i + 1) + "/" + entry.SceneVarients.Length;
                    string timing = "level config prepared: " + scene + " (" + (i + 1) + "p, " + configTimer.ElapsedMilliseconds + " ms)";
                    if (configTimer.ElapsedMilliseconds >= 50) FastInitPlugin.Log.LogInfo(timing);
                    else FastInitPlugin.Log.LogDebug(timing);
                    yield return null;
                }
                FastInitPlugin.Log.LogInfo("on-demand config ready: " + scene + " (" + Levels.Count + " levels retained, async level entry, " + timer.ElapsedMilliseconds + " ms elapsed)");
                try
                {
                    _resumeServerLoad = true;
                    DIYLevelAssetBundleManager.LoadLevelServer(scene);
                    resumed = true;
                }
                finally { _resumeServerLoad = false; }
            }
            finally
            {
                LevelLoadProgress = null;
                _serverPreparing = false;
                _preparingBundles = null;
                if (!resumed) _pendingScene = null;
                ReleaseUnused();
            }
        }

        private static bool SelectionPrefix() { return !FastInitPlugin.Syncing && !IsTransitioning && _catalogBundles == null; }

        private static string FindScenePath(string scene)
        {
            foreach (KeyValuePair<string, LevelSetInfoSO> set in DIYLevelAssetBundleManager.levelSetInfos)
                foreach (LevelInfoSO info in set.Value.levelInfos)
                    if (string.Equals(info.sceneName, scene, StringComparison.OrdinalIgnoreCase))
                        return set.Key;
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

        private static void ClearPrefix(bool isQuit)
        {
            if (!isQuit) return;
            foreach (string scene in new List<string>(Levels.Keys)) ReleaseLevel(scene);
            foreach (LevelResources resources in SceneObjects.Values) DestroyObjects(resources);
            SceneObjects.Clear();
            SceneDirectoryCache.DropConfigs(null);
            SceneDirectoryCache.CollectRetired();
        }

        private static bool SceneIsLoaded(string name)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && string.Equals(scene.name, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool SetIsNeeded(LoadedSet set)
        {
            foreach (LevelInfoSO level in set.Info.levelInfos)
                if (SceneIsLoaded(level.sceneName) || string.Equals(_pendingScene, level.sceneName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            bool frontend = scene.name == "StartScreen" || scene.name == "Lobbies";
            if (frontend || string.Equals(scene.name, _pendingScene, StringComparison.OrdinalIgnoreCase))
            {
                _pendingScene = null;
                CancelPreview();
            }
            ReleaseUnused();
            if (frontend) RequestCleanup();
            if (Levels.ContainsKey(scene.name) && FastInitPlugin.Instance != null)
                FastInitPlugin.Instance.StartCoroutine(FastInitHost.Guard(AuditSceneMaterials(scene), "scene material audit"));
        }

        private static void SceneUnloaded(Scene scene)
        {
            LevelResources resources;
            if (!SceneObjects.TryGetValue(scene, out resources)) return;
            SceneObjects.Remove(scene);
            int count = DestroyObjects(resources);
            FastInitPlugin.Log.LogInfo("scene runtime objects released: " + scene.name + " (" + count + " objects)");
        }

        internal static void RequestCleanup()
        {
            _cleanupRequested = true;
            if (_cleanupQueued || FastInitPlugin.Instance == null) return;
            _cleanupQueued = true;
            FastInitPlugin.Instance.StartCoroutine(FastInitHost.Guard(CleanupRoutine(), "unused info assets"));
        }

        internal static IEnumerator WaitForCleanup()
        {
            while (_cleanupQueued) yield return null;
        }

        private static IEnumerator CleanupRoutine()
        {
            try
            {
                while (_cleanupRequested)
                {
                    yield return null;
                    while (IsTransitioning || PseudoPrefabManager.isInCustomLevel || _previewBundles != null || _warmBundles != null
                        || Time.realtimeSinceStartup - _lastPreviewTime < 0.5f) yield return null;
                    _cleanupRequested = false;
                    yield return Resources.UnloadUnusedAssets();
                    FastInitPlugin.Log.LogDebug("unused info assets collected; referenced assets preserved");
                }
            }
            finally { _cleanupQueued = false; }
        }

        private static IEnumerator AuditSceneMaterials(Scene scene)
        {
            yield return null;
            yield return null;
            if (!scene.isLoaded) yield break;
            HashSet<int> seen = new HashSet<int>();
            List<string> descriptions = new List<string>();
            int broken = 0;
            foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (renderer == null || !renderer.gameObject.scene.Equals(scene)
                    || !renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null)
                    {
                        if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                        broken++;
                        FastInitPlugin.Log.LogWarning("scene material missing [" + scene.name + "]: " + renderer.name);
                        continue;
                    }
                    if (!seen.Add(material.GetInstanceID())) continue;
                    Shader shader = material.shader;
                    string shaderName = shader == null ? "<missing>" : shader.name;
                    bool supported = shader != null && shader.isSupported;
                    bool invalid = !supported || shaderName.IndexOf("InternalErrorShader", StringComparison.OrdinalIgnoreCase) >= 0;
                    string description = material.name + " -> " + shaderName + " (supported=" + supported + ", object=" + renderer.name + ")";
                    if (invalid)
                    {
                        broken++;
                        FastInitPlugin.Log.LogWarning("scene material invalid [" + scene.name + "]: " + description);
                    }
                    if (descriptions.Count < 64) descriptions.Add(description);
                }
            }
            FastInitPlugin.Log.LogInfo("scene material audit [" + scene.name + "]: " + seen.Count + " materials, "
                + broken + " invalid; " + string.Join("; ", descriptions.ToArray()));
        }

        internal static void ResetFrontend()
        {
            _warmGeneration++;
            _warmQueued = false;
            CancelPreview();
            ReleaseUnused();
        }

        private static void ReleaseUnused()
        {
            foreach (string scene in new List<string>(Levels.Keys))
                if (!SceneIsLoaded(scene) && !string.Equals(scene, _pendingScene, StringComparison.OrdinalIgnoreCase)) ReleaseLevel(scene);
            foreach (string path in new List<string>(Sets.Keys))
            {
                LoadedSet set = Sets[path];
                if (string.Equals(path, _previewPath, StringComparison.OrdinalIgnoreCase) || SetIsNeeded(set)) continue;
                foreach (KeyValuePair<string, LevelSetInfoSO> catalog in DIYLevelAssetBundleManager.levelSetInfos)
                    if (string.Equals(catalog.Key, path, StringComparison.OrdinalIgnoreCase))
                        foreach (LevelInfoSO level in catalog.Value.levelInfos) level.screenshot = null;
                Sets.Remove(path);
                set.Info = null;
                set.Bundle = null;
                FastInitPlugin.Log.LogInfo("on-demand info released: " + Path.GetFileName(path));
            }
            HashSet<string> retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_catalogBundles != null) retained.UnionWith(_catalogBundles);
            if (_previewBundles != null) retained.UnionWith(_previewBundles);
            if (_warmBundles != null) retained.UnionWith(_warmBundles);
            if (_preparingBundles != null) retained.UnionWith(_preparingBundles);
            foreach (LoadedSet set in Sets.Values)
            {
                retained.UnionWith(set.Required);
                foreach (string scene in new List<string>(set.SceneDependencies.Keys))
                    if (SceneIsLoaded(scene) || string.Equals(_pendingScene, scene, StringComparison.OrdinalIgnoreCase))
                        retained.UnionWith(set.SceneDependencies[scene]);
                    else
                        set.SceneDependencies.Remove(scene);
            }
            foreach (string path in new List<string>(InfoBundles.Keys))
            {
                if (retained.Contains(path)) continue;
                InfoBundles[path].Unload(false);
                InfoBundles.Remove(path);
                RequestCleanup();
                FastInitPlugin.Log.LogInfo("info dependency released: " + Path.GetFileName(path)
                    + " (" + InfoBundles.Count + " bundles tracked)");
            }
        }

        private static void ReleaseLevel(string scene)
        {
            SceneDirectoryCache.DropConfigs(scene);
            LevelResources resources = Levels[scene];
            Levels.Remove(scene);
            int count = DestroyObjects(resources);
            FastInitPlugin.Log.LogInfo("on-demand config released: " + scene + " (" + count + " runtime objects)");
        }

        private static int DestroyObjects(LevelResources resources)
        {
            int count = 0;
            foreach (UnityEngine.Object value in resources.Objects)
                if (value != null) { UnityEngine.Object.DestroyImmediate(value); count++; }
            PruneClones(QuitClones);
            PruneClones(RestartClones);
            return count;
        }

        private static void PruneClones(FieldInfo field)
        {
            List<GameObject> list = field.GetValue(null) as List<GameObject>;
            if (list != null) list.RemoveAll(value => value == null);
        }
    }

    internal static class ThumbnailCache
    {
        internal sealed class Entry
        {
            internal string Key, File;
            internal Texture2D Texture;
            internal Sprite Sprite;
            internal long Used;
        }

        private sealed class PendingWrite
        {
            internal string File;
            internal byte[] Bytes;
        }

        private const int Magic = 0x314D4854, Version = 1;
        private const int MaxWidth = 640, MaxHeight = 360, MaxBytes = 2 * 1024 * 1024;
        private const long MaxDiskBytes = 128L * 1024 * 1024;
        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        private static readonly Dictionary<string, Entry> Memory = new Dictionary<string, Entry>();
        private static readonly Queue<PendingWrite> Writes = new Queue<PendingWrite>();
        private static readonly object WriteLock = new object();
        private static byte[] _stamp;
        private static long _use;
        private static bool _writerRunning;
        private static Image _image;
        private static string _boundKey;

        internal static void Invalidate() { _stamp = null; }

        internal static bool HasWriteCapacity { get { lock (WriteLock) return Writes.Count < 4; } }
        internal static bool WritesBusy { get { lock (WriteLock) return _writerRunning || Writes.Count != 0; } }

        private static string Root()
        {
            return Path.Combine(Path.Combine(Paths.CachePath, "DIYLevelFastInit"), "thumbnails");
        }

        private static string Key(string path, string scene)
        {
            using (SHA256 hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
                    Path.GetFullPath(path).ToLowerInvariant() + "\n" + scene.ToLowerInvariant()))).Replace("-", "");
        }

        private static byte[] Stamp()
        {
            if (_stamp != null) return _stamp;
            string plugin = FastInitPlugin.GetDiyPluginDir();
            List<string> paths = new List<string>();
            foreach (FileInfo file in new DirectoryInfo(plugin).GetFiles("common*")) paths.Add(file.FullName);
            foreach (DirectoryInfo dir in new DirectoryInfo(Path.Combine(plugin, "levels")).GetDirectories())
                foreach (FileInfo file in dir.GetFiles("info*")) paths.Add(file.FullName);
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
            using (SHA256 hash = SHA256.Create())
            {
                foreach (string path in paths)
                {
                    FileInfo file = new FileInfo(path);
                    writer.Write(path.ToLowerInvariant());
                    writer.Write(file.Length);
                    writer.Write(file.LastWriteTimeUtc.Ticks);
                }
                writer.Flush();
                _stamp = hash.ComputeHash(stream.ToArray());
            }
            return _stamp;
        }

        internal static Entry TryGet(string path, string scene, out bool memoryHit)
        {
            memoryHit = false;
            try
            {
                string key = Key(path, scene), memoryKey = key + BitConverter.ToString(Stamp());
                Entry entry;
                if (Memory.TryGetValue(memoryKey, out entry))
                {
                    entry.Used = ++_use;
                    memoryHit = true;
                    Enqueue(entry.File, null);
                    return entry;
                }
                string file = Path.Combine(Root(), key + ".thumb");
                if (!File.Exists(file)) return null;
                if (new FileInfo(file).Length > MaxBytes + 84) throw new IOException("Thumbnail cache exceeds size limit");
                byte[] png;
                using (FileStream stream = File.OpenRead(file))
                using (BinaryReader reader = new BinaryReader(stream)) png = ReadData(reader, Stamp());
                Texture2D texture = null;
                try
                {
                    if (png.Length != 0)
                    {
                        texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (!ImageConversion.LoadImage(texture, png, true)) throw new IOException("Cannot decode cached thumbnail");
                    }
                    entry = Store(memoryKey, file, texture);
                    texture = null;
                }
                finally { if (texture != null) UnityEngine.Object.DestroyImmediate(texture); }
                Enqueue(file, null);
                return entry;
            }
            catch (Exception e)
            {
                FastInitPlugin.Log.LogDebug("thumbnail cache miss [" + scene + "]: " + e.Message);
                return null;
            }
        }

        internal static bool IsReady(string path, string scene)
        {
            try
            {
                string file = Path.Combine(Root(), Key(path, scene) + ".thumb");
                if (!File.Exists(file)) return false;
                using (FileStream stream = File.OpenRead(file))
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version
                        || !Equal(reader.ReadBytes(32), Stamp())) return false;
                    int width = reader.ReadInt32(), height = reader.ReadInt32(), length = reader.ReadInt32();
                    return length >= 0 && length <= MaxBytes && stream.Length == 84L + length
                        && (length == 0 ? width == 0 && height == 0
                            : width > 0 && width <= MaxWidth && height > 0 && height <= MaxHeight);
                }
            }
            catch (Exception) { return false; }
        }

        internal static Entry Capture(string path, string scene, Sprite source, bool retain = true)
        {
            byte[] stamp = Stamp();
            string key = Key(path, scene), file = Path.Combine(Root(), key + ".thumb");
            Texture2D texture = null;
            try
            {
                byte[] png = new byte[0];
                if (source != null)
                {
                    if (source.packed && source.packingRotation != SpritePackingRotation.None)
                        throw new IOException("Rotated packed screenshot cannot be cached");
                    Rect rect = source.textureRect;
                    if (rect.width <= 0 || rect.height <= 0) throw new IOException("Empty screenshot rectangle");
                    float scale = Mathf.Min(1f, Mathf.Min(MaxWidth / rect.width, MaxHeight / rect.height));
                    int width = Math.Max(1, Mathf.RoundToInt(rect.width * scale));
                    int height = Math.Max(1, Mathf.RoundToInt(rect.height * scale));
                    RenderTexture previous = RenderTexture.active;
                    RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                    try
                    {
                        Texture2D original = source.texture;
                        Graphics.Blit(original, target, new Vector2(rect.width / original.width, rect.height / original.height),
                            new Vector2(rect.x / original.width, rect.y / original.height));
                        RenderTexture.active = target;
                        texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                        texture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                        texture.Apply(false, false);
                        png = ImageConversion.EncodeToPNG(texture);
                        texture.Apply(false, true);
                    }
                    finally
                    {
                        RenderTexture.active = previous;
                        RenderTexture.ReleaseTemporary(target);
                    }
                }
                byte[] bytes;
                using (MemoryStream stream = new MemoryStream())
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    WriteData(writer, stamp, png);
                    writer.Flush();
                    bytes = stream.ToArray();
                }
                Entry entry = null;
                if (retain)
                {
                    entry = Store(key + BitConverter.ToString(stamp), file, texture);
                    texture = null;
                }
                Enqueue(file, bytes);
                return entry;
            }
            finally { if (texture != null) UnityEngine.Object.DestroyImmediate(texture); }
        }

        private static Entry Store(string key, string file, Texture2D texture)
        {
            Sprite sprite = null;
            if (texture != null)
            {
                texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
                sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
                sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            }
            Entry entry = new Entry { Key = key, File = file, Texture = texture, Sprite = sprite, Used = ++_use };
            Entry replaced;
            if (Memory.TryGetValue(key, out replaced)) Destroy(replaced);
            Memory[key] = entry;
            TrimMemory();
            return entry;
        }

        internal static void Show(Entry entry, Image image)
        {
            if (_image != null && _image != image) Hide(_image);
            _image = image;
            _boundKey = entry.Key;
            image.sprite = entry.Sprite;
            image.gameObject.SetActive(entry.Sprite != null);
            entry.Used = ++_use;
            TrimMemory();
        }

        internal static void Hide(Image image)
        {
            if (image == null) image = _image;
            if (image != null)
            {
                image.sprite = null;
                image.gameObject.SetActive(false);
            }
            if (image == _image) { _image = null; _boundKey = null; }
        }

        private static void TrimMemory()
        {
            while (Memory.Count > 8)
            {
                Entry oldest = null;
                foreach (Entry entry in Memory.Values)
                    if (entry.Key != _boundKey && (oldest == null || entry.Used < oldest.Used)) oldest = entry;
                if (oldest == null) break;
                Memory.Remove(oldest.Key);
                Destroy(oldest);
            }
        }

        internal static void ClearMemory()
        {
            Hide(null);
            foreach (Entry entry in Memory.Values) Destroy(entry);
            Memory.Clear();
        }

        private static void Destroy(Entry entry)
        {
            if (entry.Sprite != null) UnityEngine.Object.DestroyImmediate(entry.Sprite);
            if (entry.Texture != null) UnityEngine.Object.DestroyImmediate(entry.Texture);
        }

        internal static void WriteData(BinaryWriter writer, byte[] stamp, byte[] png)
        {
            int width, height;
            Dimensions(png, out width, out height);
            if (stamp.Length != 32) throw new IOException("Invalid thumbnail source stamp");
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(stamp);
            writer.Write(width);
            writer.Write(height);
            writer.Write(png.Length);
            using (SHA256 hash = SHA256.Create()) writer.Write(hash.ComputeHash(png));
            writer.Write(png);
        }

        internal static byte[] ReadData(BinaryReader reader, byte[] stamp)
        {
            if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version) throw new IOException("Invalid thumbnail cache version");
            if (!Equal(reader.ReadBytes(32), stamp)) throw new IOException("Thumbnail source fingerprint changed");
            int width = reader.ReadInt32(), height = reader.ReadInt32(), length = reader.ReadInt32();
            if (length < 0 || length > MaxBytes) throw new IOException("Invalid thumbnail payload size");
            byte[] digest = reader.ReadBytes(32), png = reader.ReadBytes(length);
            if (png.Length != length || reader.BaseStream.Position != reader.BaseStream.Length) throw new IOException("Truncated thumbnail or trailing data");
            using (SHA256 hash = SHA256.Create())
                if (!Equal(hash.ComputeHash(png), digest)) throw new IOException("Thumbnail checksum mismatch");
            int actualWidth, actualHeight;
            Dimensions(png, out actualWidth, out actualHeight);
            if (width != actualWidth || height != actualHeight) throw new IOException("Thumbnail dimensions do not match PNG");
            return png;
        }

        private static void Dimensions(byte[] png, out int width, out int height)
        {
            width = height = 0;
            if (png.Length == 0) return;
            if (png.Length < 33 || png.Length > MaxBytes) throw new IOException("Invalid thumbnail PNG size");
            for (int i = 0; i < 8; i++) if (png[i] != PngSignature[i]) throw new IOException("Invalid thumbnail PNG signature");
            if (png[8] != 0 || png[9] != 0 || png[10] != 0 || png[11] != 13
                || png[12] != 73 || png[13] != 72 || png[14] != 68 || png[15] != 82) throw new IOException("Invalid PNG header");
            width = png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19];
            height = png[20] << 24 | png[21] << 16 | png[22] << 8 | png[23];
            if (width < 1 || width > MaxWidth || height < 1 || height > MaxHeight) throw new IOException("Thumbnail dimensions exceed limit");
        }

        private static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void Enqueue(string file, byte[] bytes)
        {
            lock (WriteLock)
            {
                if (Writes.Count == 4)
                {
                    if (bytes == null) return;
                    Writes.Dequeue();
                }
                Writes.Enqueue(new PendingWrite { File = file, Bytes = bytes });
                if (_writerRunning) return;
                _writerRunning = true;
                System.Threading.ThreadPool.QueueUserWorkItem(WriteLoop);
            }
        }

        private static void WriteLoop(object unused)
        {
            while (true)
            {
                PendingWrite write;
                lock (WriteLock)
                {
                    if (Writes.Count == 0) { _writerRunning = false; return; }
                    write = Writes.Dequeue();
                }
                string temporary = null;
                try
                {
                    if (write.Bytes == null)
                    {
                        if (File.Exists(write.File)) File.SetLastWriteTimeUtc(write.File, DateTime.UtcNow);
                        continue;
                    }
                    string root = Path.GetDirectoryName(write.File);
                    Directory.CreateDirectory(root);
                    temporary = write.File + ".tmp-" + Guid.NewGuid().ToString("N");
                    File.WriteAllBytes(temporary, write.Bytes);
                    if (File.Exists(write.File)) File.Replace(temporary, write.File, null);
                    else File.Move(temporary, write.File);
                    temporary = null;
                    TrimDisk(root);
                    FastInitPlugin.Log.LogDebug("thumbnail cache stored: " + Path.GetFileName(write.File));
                }
                catch (Exception e) { FastInitPlugin.Log.LogDebug("thumbnail cache write skipped: " + e.Message); }
                finally
                {
                    if (temporary != null)
                        try { File.Delete(temporary); } catch (Exception) { }
                }
            }
        }

        private static void TrimDisk(string root)
        {
            List<FileInfo> files = new List<FileInfo>(new DirectoryInfo(root).GetFiles("*.thumb"));
            long size = 0;
            foreach (FileInfo file in files) size += file.Length;
            files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            foreach (FileInfo file in files)
            {
                if (size <= MaxDiskBytes) break;
                long length = file.Length;
                file.Delete();
                size -= length;
            }
        }
    }

    internal static class InfoBundleGraph
    {
        private sealed class Block
        {
            internal long Offset;
            internal int Size, Packed, Compression;
        }

        private sealed class Node
        {
            internal long Offset, Size;
            internal string Name;
            internal int Flags;
        }

        private sealed class Source
        {
            internal string Path;
            internal long Length, Modified;
            internal readonly List<Block> Blocks = new List<Block>();
            internal readonly List<Node> Nodes = new List<Node>();
            internal string[] References;
        }

        private static readonly Dictionary<string, Source> Sources = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);
        private const int MaxMetadata = 8 * 1024 * 1024;
        private const int MaxPrefix = 64 * 1024 * 1024;

        internal static HashSet<string> Required(string info, string scene)
        {
            info = Path.GetFullPath(info);
            if (scene != null) scene = Path.GetFullPath(scene);
            Dictionary<string, string> owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string levels = Directory.GetParent(Directory.GetParent(info).FullName).FullName;
            foreach (DirectoryInfo dir in FastInitHost.SafeGetDirs(levels))
            {
                List<FileInfo> files = FastInitHost.SafeGetFiles(dir, "info*");
                if (files.Count == 0) continue;
                Source source;
                try { source = Open(files[0].FullName); }
                catch (IOException e)
                {
                    if (string.Equals(files[0].FullName, info, StringComparison.OrdinalIgnoreCase)) throw;
                    FastInitPlugin.Log.LogWarning("info dependency index skipped [" + dir.Name + "]: " + e.Message);
                    continue;
                }
                foreach (Node node in source.Nodes)
                    if (node.Name.StartsWith("CAB-", StringComparison.OrdinalIgnoreCase))
                        if (!owners.ContainsKey(node.Name)) owners.Add(node.Name, source.Path);
            }
            HashSet<string> required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Queue<string> pending = new Queue<string>();
            required.Add(info);
            pending.Enqueue(info);
            if (scene != null) pending.Enqueue(scene);
            while (pending.Count != 0)
                foreach (string reference in References(Open(pending.Dequeue())))
                {
                    string dependency;
                    if (owners.TryGetValue(reference, out dependency) && required.Add(dependency))
                        pending.Enqueue(dependency);
                }
            if (scene != null)
                FastInitPlugin.Log.LogInfo("info dependency graph [" + Path.GetFileName(scene) + "]: "
                    + string.Join(", ", new List<string>(required).ConvertAll(Path.GetFileName).ToArray()));
            return required;
        }

        private static Source Open(string path)
        {
            FileInfo file = new FileInfo(path);
            Source source;
            if (Sources.TryGetValue(path, out source) && source.Length == file.Length
                && source.Modified == file.LastWriteTimeUtc.Ticks) return source;
            source = new Source { Path = path, Length = file.Length, Modified = file.LastWriteTimeUtc.Ticks };
            using (FileStream stream = File.OpenRead(path))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                if (CString(reader) != "UnityFS" || U32(reader) != 6) throw new IOException("Unsupported UnityFS header: " + path);
                CString(reader);
                CString(reader);
                if (I64(reader) != file.Length) throw new IOException("Invalid UnityFS size: " + path);
                int packed = Size(U32(reader), MaxMetadata), size = Size(U32(reader), MaxMetadata);
                uint flags = U32(reader);
                long dataStart = stream.Position + ((flags & 128) == 0 ? packed : 0);
                if ((flags & ~255U) != 0 || (flags & 64) == 0) throw new IOException("Unsupported UnityFS flags: " + path);
                if ((flags & 128) != 0) stream.Position = file.Length - packed;
                byte[] bytes = Decode(reader, packed, size, (int)(flags & 63), size);
                using (BinaryReader table = new BinaryReader(new MemoryStream(bytes, false)))
                {
                    table.ReadBytes(16);
                    int count = Size(U32(table), 100000);
                    long offset = dataStart;
                    for (int i = 0; i < count; i++)
                    {
                        int plain = Size(U32(table), int.MaxValue), compressed = Size(U32(table), int.MaxValue);
                        int compression = (table.ReadByte() << 8 | table.ReadByte()) & 63;
                        if (offset > file.Length - compressed) throw new IOException("Invalid UnityFS block");
                        source.Blocks.Add(new Block { Offset = offset, Size = plain, Packed = compressed, Compression = compression });
                        offset += compressed;
                    }
                    count = Size(U32(table), 20000);
                    for (int i = 0; i < count; i++)
                    {
                        long start = I64(table), length = I64(table);
                        int nodeFlags = (int)U32(table);
                        string name = CString(table);
                        if (start < 0 || length < 0) throw new IOException("Invalid UnityFS node");
                        source.Nodes.Add(new Node { Offset = start, Size = length, Flags = nodeFlags, Name = name });
                    }
                }
            }
            Sources[path] = source;
            return source;
        }

        private static string[] References(Source source)
        {
            if (source.References != null) return source.References;
            HashSet<string> references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Node node in source.Nodes)
            {
                if ((node.Flags & 4) == 0) continue;
                byte[] header = Range(source, node.Offset, 20);
                using (BinaryReader reader = new BinaryReader(new MemoryStream(header, false)))
                {
                    U32(reader);
                    U32(reader);
                    uint version = U32(reader);
                    int end = Size(U32(reader), MaxMetadata);
                    if (version < 9 || version > 21 || end < 20 || end > node.Size)
                        throw new IOException("Unsupported serialized metadata: " + source.Path);
                    string metadata = Encoding.ASCII.GetString(Range(source, node.Offset, end));
                    foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(metadata,
                        @"archive:/(CAB-[0-9a-f]{32})/\1\x00", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        references.Add(match.Groups[1].Value);
                }
            }
            source.References = new List<string>(references).ToArray();
            return source.References;
        }

        private static byte[] Range(Source source, long start, int count)
        {
            byte[] result = new byte[count];
            long position = 0;
            int written = 0;
            using (FileStream stream = File.OpenRead(source.Path))
            using (BinaryReader reader = new BinaryReader(stream))
                foreach (Block block in source.Blocks)
                {
                    long end = position + block.Size;
                    if (start < end && start + count > position)
                    {
                        int from = (int)Math.Max(0, start - position);
                        int to = (int)Math.Min(block.Size, start + count - position);
                        if (to > MaxPrefix) throw new IOException("UnityFS metadata prefix too large");
                        stream.Position = block.Offset;
                        byte[] bytes = Decode(reader, block.Packed, block.Size, block.Compression, to);
                        Buffer.BlockCopy(bytes, from, result, written, to - from);
                        written += to - from;
                        if (written == count) return result;
                    }
                    position = end;
                }
            throw new IOException("Truncated UnityFS metadata");
        }

        private static byte[] Decode(BinaryReader reader, int packed, int size, int compression, int prefix)
        {
            if (prefix < 0 || prefix > size || prefix > MaxPrefix) throw new IOException("Invalid UnityFS prefix");
            if (compression == 0) return Exact(reader, prefix);
            if (compression == 1)
            {
                if (packed < 5) throw new IOException("Invalid LZMA block");
                byte[] properties = Exact(reader, 5);
                uint dictionary = BitConverter.ToUInt32(properties, 1);
                byte[] bounded = BitConverter.GetBytes(Math.Min(dictionary, (uint)Math.Max(4096, prefix + 273)));
                Buffer.BlockCopy(bounded, 0, properties, 1, 4);
                BundleCodecs.Compression.LZMA.Decoder decoder = new BundleCodecs.Compression.LZMA.Decoder();
                decoder.SetDecoderProperties(properties);
                using (MemoryStream output = new MemoryStream(prefix + 273))
                using (Stream input = new LimitedInput(reader.BaseStream, packed - 5))
                {
                    decoder.Code(input, output, packed - 5, prefix, null);
                    if (output.Length < prefix) throw new IOException("Truncated LZMA metadata");
                    byte[] result = new byte[prefix];
                    Buffer.BlockCopy(output.GetBuffer(), 0, result, 0, prefix);
                    return result;
                }
            }
            if (compression != 2 && compression != 3) throw new IOException("Unsupported UnityFS compression");
            if (packed > MaxPrefix) throw new IOException("LZ4 block too large");
            byte[] inputBytes = Exact(reader, packed), outputBytes = new byte[prefix];
            int source = 0, target = 0;
            while (target < prefix)
            {
                if (source >= inputBytes.Length) throw new IOException("Truncated LZ4 block");
                int token = inputBytes[source++], literal = token >> 4;
                if (literal == 15) literal += ExtendedLength(inputBytes, ref source);
                if (literal > inputBytes.Length - source) throw new IOException("Invalid LZ4 literal");
                int length = Math.Min(literal, prefix - target);
                Buffer.BlockCopy(inputBytes, source, outputBytes, target, length);
                target += length;
                source += literal;
                if (target == prefix) break;
                if (source > inputBytes.Length - 2) throw new IOException("Invalid LZ4 offset");
                int distance = inputBytes[source] | inputBytes[source + 1] << 8;
                source += 2;
                if (distance == 0 || distance > target) throw new IOException("Invalid LZ4 distance");
                int match = (token & 15) + 4;
                if ((token & 15) == 15) match += ExtendedLength(inputBytes, ref source);
                int until = target + Math.Min(match, prefix - target);
                while (target < until) { outputBytes[target] = outputBytes[target - distance]; target++; }
            }
            return outputBytes;
        }

        private static int ExtendedLength(byte[] bytes, ref int index)
        {
            int count = 0, value;
            do
            {
                if (index == bytes.Length || count > MaxPrefix) throw new IOException("Invalid LZ4 length");
                value = bytes[index++];
                count += value;
            } while (value == 255);
            return count;
        }

        private static byte[] Exact(BinaryReader reader, int count)
        {
            byte[] bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new IOException("Truncated UnityFS data");
            return bytes;
        }

        private static uint U32(BinaryReader reader)
        {
            return (uint)(reader.ReadByte() << 24 | reader.ReadByte() << 16 | reader.ReadByte() << 8 | reader.ReadByte());
        }

        private static long I64(BinaryReader reader)
        {
            return ((long)U32(reader) << 32) | U32(reader);
        }

        private static int Size(uint value, int limit)
        {
            if (value > limit) throw new IOException("UnityFS size exceeds limit");
            return (int)value;
        }

        private static string CString(BinaryReader reader)
        {
            List<byte> bytes = new List<byte>();
            byte value;
            while ((value = reader.ReadByte()) != 0)
            {
                if (bytes.Count == 4096) throw new IOException("UnityFS name too long");
                bytes.Add(value);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private sealed class LimitedInput : Stream
        {
            private readonly Stream _source;
            private long _remaining;
            internal LimitedInput(Stream source, long remaining) { _source = source; _remaining = remaining; }
            public override int ReadByte()
            {
                if (_remaining-- <= 0) throw new EndOfStreamException();
                int value = _source.ReadByte();
                if (value < 0) throw new EndOfStreamException();
                return value;
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                count = (int)Math.Min(count, _remaining);
                int read = _source.Read(buffer, offset, count);
                _remaining -= read;
                return read;
            }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }

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

// Decoder subset adapted from Igor Pavlov's public-domain LZMA SDK 23.01.
// https://www.7-zip.org/sdk.html; see THIRD_PARTY_NOTICES.md.
namespace DIYLevelFastInit.BundleCodecs.Compression.RangeCoder
{
    class Decoder
    {
        public const uint kTopValue = (1 << 24);
        public uint Range;
        public uint Code;

        public System.IO.Stream Stream;

        public void Init(System.IO.Stream stream)
        {

            Stream = stream;

            Code = 0;
            Range = 0xFFFFFFFF;
            for (int i = 0; i < 5; i++)
                Code = (Code << 8) | (byte)Stream.ReadByte();
        }

        public void ReleaseStream()
        {

            Stream = null;
        }

        public void CloseStream()
        {
            Stream.Close();
        }

        public void Normalize()
        {
            while (Range < kTopValue)
            {
                Code = (Code << 8) | (byte)Stream.ReadByte();
                Range <<= 8;
            }
        }

        public void Normalize2()
        {
            if (Range < kTopValue)
            {
                Code = (Code << 8) | (byte)Stream.ReadByte();
                Range <<= 8;
            }
        }

        public uint GetThreshold(uint total)
        {
            return Code / (Range /= total);
        }

        public void Decode(uint start, uint size, uint total)
        {
            Code -= start * Range;
            Range *= size;
            Normalize();
        }

        public uint DecodeDirectBits(int numTotalBits)
        {
            uint range = Range;
            uint code = Code;
            uint result = 0;
            for (int i = numTotalBits; i > 0; i--)
            {
                range >>= 1;

                uint t = (code - range) >> 31;
                code -= range & (t - 1);
                result = (result << 1) | (1 - t);

                if (range < kTopValue)
                {
                    code = (code << 8) | (byte)Stream.ReadByte();
                    range <<= 8;
                }
            }
            Range = range;
            Code = code;
            return result;
        }

        public uint DecodeBit(uint size0, int numTotalBits)
        {
            uint newBound = (Range >> numTotalBits) * size0;
            uint symbol;
            if (Code < newBound)
            {
                symbol = 0;
                Range = newBound;
            }
            else
            {
                symbol = 1;
                Code -= newBound;
                Range -= newBound;
            }
            Normalize();
            return symbol;
        }

    }
}

namespace DIYLevelFastInit.BundleCodecs.Compression.RangeCoder
{
    struct BitDecoder
    {
        public const int kNumBitModelTotalBits = 11;
        public const uint kBitModelTotal = (1 << kNumBitModelTotalBits);
        const int kNumMoveBits = 5;

        uint Prob;

        public void UpdateModel(int numMoveBits, uint symbol)
        {
            if (symbol == 0)
                Prob += (kBitModelTotal - Prob) >> numMoveBits;
            else
                Prob -= (Prob) >> numMoveBits;
        }

        public void Init() { Prob = kBitModelTotal >> 1; }

        public uint Decode(RangeCoder.Decoder rangeDecoder)
        {
            uint newBound = (uint)(rangeDecoder.Range >> kNumBitModelTotalBits) * (uint)Prob;
            if (rangeDecoder.Code < newBound)
            {
                rangeDecoder.Range = newBound;
                Prob += (kBitModelTotal - Prob) >> kNumMoveBits;
                if (rangeDecoder.Range < Decoder.kTopValue)
                {
                    rangeDecoder.Code = (rangeDecoder.Code << 8) | (byte)rangeDecoder.Stream.ReadByte();
                    rangeDecoder.Range <<= 8;
                }
                return 0;
            }
            else
            {
                rangeDecoder.Range -= newBound;
                rangeDecoder.Code -= newBound;
                Prob -= (Prob) >> kNumMoveBits;
                if (rangeDecoder.Range < Decoder.kTopValue)
                {
                    rangeDecoder.Code = (rangeDecoder.Code << 8) | (byte)rangeDecoder.Stream.ReadByte();
                    rangeDecoder.Range <<= 8;
                }
                return 1;
            }
        }
    }
}

namespace DIYLevelFastInit.BundleCodecs.Compression.RangeCoder
{
    struct BitTreeDecoder
    {
        BitDecoder[] Models;
        int NumBitLevels;

        public BitTreeDecoder(int numBitLevels)
        {
            NumBitLevels = numBitLevels;
            Models = new BitDecoder[1 << numBitLevels];
        }

        public void Init()
        {
            for (uint i = 1; i < (1 << NumBitLevels); i++)
                Models[i].Init();
        }

        public uint Decode(RangeCoder.Decoder rangeDecoder)
        {
            uint m = 1;
            for (int bitIndex = NumBitLevels; bitIndex > 0; bitIndex--)
                m = (m << 1) + Models[m].Decode(rangeDecoder);
            return m - ((uint)1 << NumBitLevels);
        }

        public uint ReverseDecode(RangeCoder.Decoder rangeDecoder)
        {
            uint m = 1;
            uint symbol = 0;
            for (int bitIndex = 0; bitIndex < NumBitLevels; bitIndex++)
            {
                uint bit = Models[m].Decode(rangeDecoder);
                m <<= 1;
                m += bit;
                symbol |= (bit << bitIndex);
            }
            return symbol;
        }

        public static uint ReverseDecode(BitDecoder[] Models, UInt32 startIndex,
            RangeCoder.Decoder rangeDecoder, int NumBitLevels)
        {
            uint m = 1;
            uint symbol = 0;
            for (int bitIndex = 0; bitIndex < NumBitLevels; bitIndex++)
            {
                uint bit = Models[startIndex + m].Decode(rangeDecoder);
                m <<= 1;
                m += bit;
                symbol |= (bit << bitIndex);
            }
            return symbol;
        }
    }
}

namespace DIYLevelFastInit.BundleCodecs.Compression.LZ
{
    internal class OutWindow
    {
        byte[] _buffer = null;
        uint _pos;
        uint _windowSize = 0;
        uint _streamPos;
        System.IO.Stream _stream;

        public uint TrainSize = 0;

        public void Create(uint windowSize)
        {
            if (_windowSize != windowSize)
            {

                _buffer = new byte[windowSize];
            }
            _windowSize = windowSize;
            _pos = 0;
            _streamPos = 0;
        }

        public void Init(System.IO.Stream stream, bool solid)
        {
            ReleaseStream();
            _stream = stream;
            if (!solid)
            {
                _streamPos = 0;
                _pos = 0;
                TrainSize = 0;
            }
        }

        public bool Train(System.IO.Stream stream)
        {
            long len = stream.Length;
            uint size = (len < _windowSize) ? (uint)len : _windowSize;
            TrainSize = size;
            stream.Position = len - size;
            _streamPos = _pos = 0;
            while (size > 0)
            {
                uint curSize = _windowSize - _pos;
                if (size < curSize)
                    curSize = size;
                int numReadBytes = stream.Read(_buffer, (int)_pos, (int)curSize);
                if (numReadBytes == 0)
                    return false;
                size -= (uint)numReadBytes;
                _pos += (uint)numReadBytes;
                _streamPos += (uint)numReadBytes;
                if (_pos == _windowSize)
                    _streamPos = _pos = 0;
            }
            return true;
        }

        public void ReleaseStream()
        {
            Flush();
            _stream = null;
        }

        public void Flush()
        {
            uint size = _pos - _streamPos;
            if (size == 0)
                return;
            _stream.Write(_buffer, (int)_streamPos, (int)size);
            if (_pos >= _windowSize)
                _pos = 0;
            _streamPos = _pos;
        }

        public void CopyBlock(uint distance, uint len)
        {
            uint pos = _pos - distance - 1;
            if (pos >= _windowSize)
                pos += _windowSize;
            for (; len > 0; len--)
            {
                if (pos >= _windowSize)
                    pos = 0;
                _buffer[_pos++] = _buffer[pos++];
                if (_pos >= _windowSize)
                    Flush();
            }
        }

        public void PutByte(byte b)
        {
            _buffer[_pos++] = b;
            if (_pos >= _windowSize)
                Flush();
        }

        public byte GetByte(uint distance)
        {
            uint pos = _pos - distance - 1;
            if (pos >= _windowSize)
                pos += _windowSize;
            return _buffer[pos];
        }
    }
}

namespace DIYLevelFastInit.BundleCodecs.Compression.LZMA
{
    class Base
    {
        public const uint kNumRepDistances = 4;
        public const uint kNumStates = 12;

        public struct State
        {
            public uint Index;
            public void Init() { Index = 0; }
            public void UpdateChar()
            {
                if (Index < 4) Index = 0;
                else if (Index < 10) Index -= 3;
                else Index -= 6;
            }
            public void UpdateMatch() { Index = (uint)(Index < 7 ? 7 : 10); }
            public void UpdateRep() { Index = (uint)(Index < 7 ? 8 : 11); }
            public void UpdateShortRep() { Index = (uint)(Index < 7 ? 9 : 11); }
            public bool IsCharState() { return Index < 7; }
        }

        public const int kNumPosSlotBits = 6;
        public const int kDicLogSizeMin = 0;

        public const int kNumLenToPosStatesBits = 2;
        public const uint kNumLenToPosStates = 1 << kNumLenToPosStatesBits;

        public const uint kMatchMinLen = 2;

        public static uint GetLenToPosState(uint len)
        {
            len -= kMatchMinLen;
            if (len < kNumLenToPosStates)
                return len;
            return (uint)(kNumLenToPosStates - 1);
        }

        public const int kNumAlignBits = 4;
        public const uint kAlignTableSize = 1 << kNumAlignBits;
        public const uint kAlignMask = (kAlignTableSize - 1);

        public const uint kStartPosModelIndex = 4;
        public const uint kEndPosModelIndex = 14;
        public const uint kNumPosModels = kEndPosModelIndex - kStartPosModelIndex;

        public const uint kNumFullDistances = 1 << ((int)kEndPosModelIndex / 2);

        public const uint kNumLitPosStatesBitsEncodingMax = 4;
        public const uint kNumLitContextBitsMax = 8;

        public const int kNumPosStatesBitsMax = 4;
        public const uint kNumPosStatesMax = (1 << kNumPosStatesBitsMax);
        public const int kNumPosStatesBitsEncodingMax = 4;
        public const uint kNumPosStatesEncodingMax = (1 << kNumPosStatesBitsEncodingMax);

        public const int kNumLowLenBits = 3;
        public const int kNumMidLenBits = 3;
        public const int kNumHighLenBits = 8;
        public const uint kNumLowLenSymbols = 1 << kNumLowLenBits;
        public const uint kNumMidLenSymbols = 1 << kNumMidLenBits;
        public const uint kNumLenSymbols = kNumLowLenSymbols + kNumMidLenSymbols +
                (1 << kNumHighLenBits);
        public const uint kMatchMaxLen = kMatchMinLen + kNumLenSymbols - 1;
    }
}

namespace DIYLevelFastInit.BundleCodecs.Compression.LZMA
{
    using RangeCoder;

    internal class Decoder
    {
        class LenDecoder
        {
            BitDecoder m_Choice = new BitDecoder();
            BitDecoder m_Choice2 = new BitDecoder();
            BitTreeDecoder[] m_LowCoder = new BitTreeDecoder[Base.kNumPosStatesMax];
            BitTreeDecoder[] m_MidCoder = new BitTreeDecoder[Base.kNumPosStatesMax];
            BitTreeDecoder m_HighCoder = new BitTreeDecoder(Base.kNumHighLenBits);
            uint m_NumPosStates = 0;

            public void Create(uint numPosStates)
            {
                for (uint posState = m_NumPosStates; posState < numPosStates; posState++)
                {
                    m_LowCoder[posState] = new BitTreeDecoder(Base.kNumLowLenBits);
                    m_MidCoder[posState] = new BitTreeDecoder(Base.kNumMidLenBits);
                }
                m_NumPosStates = numPosStates;
            }

            public void Init()
            {
                m_Choice.Init();
                for (uint posState = 0; posState < m_NumPosStates; posState++)
                {
                    m_LowCoder[posState].Init();
                    m_MidCoder[posState].Init();
                }
                m_Choice2.Init();
                m_HighCoder.Init();
            }

            public uint Decode(RangeCoder.Decoder rangeDecoder, uint posState)
            {
                if (m_Choice.Decode(rangeDecoder) == 0)
                    return m_LowCoder[posState].Decode(rangeDecoder);
                else
                {
                    uint symbol = Base.kNumLowLenSymbols;
                    if (m_Choice2.Decode(rangeDecoder) == 0)
                        symbol += m_MidCoder[posState].Decode(rangeDecoder);
                    else
                    {
                        symbol += Base.kNumMidLenSymbols;
                        symbol += m_HighCoder.Decode(rangeDecoder);
                    }
                    return symbol;
                }
            }
        }

        class LiteralDecoder
        {
            struct Decoder2
            {
                BitDecoder[] m_Decoders;
                public void Create() { m_Decoders = new BitDecoder[0x300]; }
                public void Init() { for (int i = 0; i < 0x300; i++) m_Decoders[i].Init(); }

                public byte DecodeNormal(RangeCoder.Decoder rangeDecoder)
                {
                    uint symbol = 1;
                    do
                        symbol = (symbol << 1) | m_Decoders[symbol].Decode(rangeDecoder);
                    while (symbol < 0x100);
                    return (byte)symbol;
                }

                public byte DecodeWithMatchByte(RangeCoder.Decoder rangeDecoder, byte matchByte)
                {
                    uint symbol = 1;
                    do
                    {
                        uint matchBit = (uint)(matchByte >> 7) & 1;
                        matchByte <<= 1;
                        uint bit = m_Decoders[((1 + matchBit) << 8) + symbol].Decode(rangeDecoder);
                        symbol = (symbol << 1) | bit;
                        if (matchBit != bit)
                        {
                            while (symbol < 0x100)
                                symbol = (symbol << 1) | m_Decoders[symbol].Decode(rangeDecoder);
                            break;
                        }
                    }
                    while (symbol < 0x100);
                    return (byte)symbol;
                }
            }

            Decoder2[] m_Coders;
            int m_NumPrevBits;
            int m_NumPosBits;
            uint m_PosMask;

            public void Create(int numPosBits, int numPrevBits)
            {
                if (m_Coders != null && m_NumPrevBits == numPrevBits &&
                    m_NumPosBits == numPosBits)
                    return;
                m_NumPosBits = numPosBits;
                m_PosMask = ((uint)1 << numPosBits) - 1;
                m_NumPrevBits = numPrevBits;
                uint numStates = (uint)1 << (m_NumPrevBits + m_NumPosBits);
                m_Coders = new Decoder2[numStates];
                for (uint i = 0; i < numStates; i++)
                    m_Coders[i].Create();
            }

            public void Init()
            {
                uint numStates = (uint)1 << (m_NumPrevBits + m_NumPosBits);
                for (uint i = 0; i < numStates; i++)
                    m_Coders[i].Init();
            }

            uint GetState(uint pos, byte prevByte)
            { return ((pos & m_PosMask) << m_NumPrevBits) + (uint)(prevByte >> (8 - m_NumPrevBits)); }

            public byte DecodeNormal(RangeCoder.Decoder rangeDecoder, uint pos, byte prevByte)
            { return m_Coders[GetState(pos, prevByte)].DecodeNormal(rangeDecoder); }

            public byte DecodeWithMatchByte(RangeCoder.Decoder rangeDecoder, uint pos, byte prevByte, byte matchByte)
            { return m_Coders[GetState(pos, prevByte)].DecodeWithMatchByte(rangeDecoder, matchByte); }
        };

        LZ.OutWindow m_OutWindow = new LZ.OutWindow();
        RangeCoder.Decoder m_RangeDecoder = new RangeCoder.Decoder();

        BitDecoder[] m_IsMatchDecoders = new BitDecoder[Base.kNumStates << Base.kNumPosStatesBitsMax];
        BitDecoder[] m_IsRepDecoders = new BitDecoder[Base.kNumStates];
        BitDecoder[] m_IsRepG0Decoders = new BitDecoder[Base.kNumStates];
        BitDecoder[] m_IsRepG1Decoders = new BitDecoder[Base.kNumStates];
        BitDecoder[] m_IsRepG2Decoders = new BitDecoder[Base.kNumStates];
        BitDecoder[] m_IsRep0LongDecoders = new BitDecoder[Base.kNumStates << Base.kNumPosStatesBitsMax];

        BitTreeDecoder[] m_PosSlotDecoder = new BitTreeDecoder[Base.kNumLenToPosStates];
        BitDecoder[] m_PosDecoders = new BitDecoder[Base.kNumFullDistances - Base.kEndPosModelIndex];

        BitTreeDecoder m_PosAlignDecoder = new BitTreeDecoder(Base.kNumAlignBits);

        LenDecoder m_LenDecoder = new LenDecoder();
        LenDecoder m_RepLenDecoder = new LenDecoder();

        LiteralDecoder m_LiteralDecoder = new LiteralDecoder();

        uint m_DictionarySize;
        uint m_DictionarySizeCheck;

        uint m_PosStateMask;

        public Decoder()
        {
            m_DictionarySize = 0xFFFFFFFF;
            for (int i = 0; i < Base.kNumLenToPosStates; i++)
                m_PosSlotDecoder[i] = new BitTreeDecoder(Base.kNumPosSlotBits);
        }

        void SetDictionarySize(uint dictionarySize)
        {
            if (m_DictionarySize != dictionarySize)
            {
                m_DictionarySize = dictionarySize;
                m_DictionarySizeCheck = Math.Max(m_DictionarySize, 1);
                uint blockSize = Math.Max(m_DictionarySizeCheck, (1 << 12));
                m_OutWindow.Create(blockSize);
            }
        }

        void SetLiteralProperties(int lp, int lc)
        {
            if (lp > 8)
                throw new IOException("Invalid LZMA properties");
            if (lc > 8)
                throw new IOException("Invalid LZMA properties");
            m_LiteralDecoder.Create(lp, lc);
        }

        void SetPosBitsProperties(int pb)
        {
            if (pb > Base.kNumPosStatesBitsMax)
                throw new IOException("Invalid LZMA properties");
            uint numPosStates = (uint)1 << pb;
            m_LenDecoder.Create(numPosStates);
            m_RepLenDecoder.Create(numPosStates);
            m_PosStateMask = numPosStates - 1;
        }

        bool _solid = false;
        void Init(System.IO.Stream inStream, System.IO.Stream outStream)
        {
            m_RangeDecoder.Init(inStream);
            m_OutWindow.Init(outStream, _solid);

            uint i;
            for (i = 0; i < Base.kNumStates; i++)
            {
                for (uint j = 0; j <= m_PosStateMask; j++)
                {
                    uint index = (i << Base.kNumPosStatesBitsMax) + j;
                    m_IsMatchDecoders[index].Init();
                    m_IsRep0LongDecoders[index].Init();
                }
                m_IsRepDecoders[i].Init();
                m_IsRepG0Decoders[i].Init();
                m_IsRepG1Decoders[i].Init();
                m_IsRepG2Decoders[i].Init();
            }

            m_LiteralDecoder.Init();
            for (i = 0; i < Base.kNumLenToPosStates; i++)
                m_PosSlotDecoder[i].Init();

            for (i = 0; i < Base.kNumFullDistances - Base.kEndPosModelIndex; i++)
                m_PosDecoders[i].Init();

            m_LenDecoder.Init();
            m_RepLenDecoder.Init();
            m_PosAlignDecoder.Init();
        }

        public void Code(System.IO.Stream inStream, System.IO.Stream outStream,
            Int64 inSize, Int64 outSize, object progress)
        {
            Init(inStream, outStream);

            Base.State state = new Base.State();
            state.Init();
            uint rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;

            UInt64 nowPos64 = 0;
            UInt64 outSize64 = (UInt64)outSize;
            if (nowPos64 < outSize64)
            {
                if (m_IsMatchDecoders[state.Index << Base.kNumPosStatesBitsMax].Decode(m_RangeDecoder) != 0)
                    throw new IOException("Invalid LZMA data");
                state.UpdateChar();
                byte b = m_LiteralDecoder.DecodeNormal(m_RangeDecoder, 0, 0);
                m_OutWindow.PutByte(b);
                nowPos64++;
            }
            while (nowPos64 < outSize64)
            {

                {
                    uint posState = (uint)nowPos64 & m_PosStateMask;
                    if (m_IsMatchDecoders[(state.Index << Base.kNumPosStatesBitsMax) + posState].Decode(m_RangeDecoder) == 0)
                    {
                        byte b;
                        byte prevByte = m_OutWindow.GetByte(0);
                        if (!state.IsCharState())
                            b = m_LiteralDecoder.DecodeWithMatchByte(m_RangeDecoder,
                                (uint)nowPos64, prevByte, m_OutWindow.GetByte(rep0));
                        else
                            b = m_LiteralDecoder.DecodeNormal(m_RangeDecoder, (uint)nowPos64, prevByte);
                        m_OutWindow.PutByte(b);
                        state.UpdateChar();
                        nowPos64++;
                    }
                    else
                    {
                        uint len;
                        if (m_IsRepDecoders[state.Index].Decode(m_RangeDecoder) == 1)
                        {
                            if (m_IsRepG0Decoders[state.Index].Decode(m_RangeDecoder) == 0)
                            {
                                if (m_IsRep0LongDecoders[(state.Index << Base.kNumPosStatesBitsMax) + posState].Decode(m_RangeDecoder) == 0)
                                {
                                    state.UpdateShortRep();
                                    m_OutWindow.PutByte(m_OutWindow.GetByte(rep0));
                                    nowPos64++;
                                    continue;
                                }
                            }
                            else
                            {
                                UInt32 distance;
                                if (m_IsRepG1Decoders[state.Index].Decode(m_RangeDecoder) == 0)
                                {
                                    distance = rep1;
                                }
                                else
                                {
                                    if (m_IsRepG2Decoders[state.Index].Decode(m_RangeDecoder) == 0)
                                        distance = rep2;
                                    else
                                    {
                                        distance = rep3;
                                        rep3 = rep2;
                                    }
                                    rep2 = rep1;
                                }
                                rep1 = rep0;
                                rep0 = distance;
                            }
                            len = m_RepLenDecoder.Decode(m_RangeDecoder, posState) + Base.kMatchMinLen;
                            state.UpdateRep();
                        }
                        else
                        {
                            rep3 = rep2;
                            rep2 = rep1;
                            rep1 = rep0;
                            len = Base.kMatchMinLen + m_LenDecoder.Decode(m_RangeDecoder, posState);
                            state.UpdateMatch();
                            uint posSlot = m_PosSlotDecoder[Base.GetLenToPosState(len)].Decode(m_RangeDecoder);
                            if (posSlot >= Base.kStartPosModelIndex)
                            {
                                int numDirectBits = (int)((posSlot >> 1) - 1);
                                rep0 = ((2 | (posSlot & 1)) << numDirectBits);
                                if (posSlot < Base.kEndPosModelIndex)
                                    rep0 += BitTreeDecoder.ReverseDecode(m_PosDecoders,
                                            rep0 - posSlot - 1, m_RangeDecoder, numDirectBits);
                                else
                                {
                                    rep0 += (m_RangeDecoder.DecodeDirectBits(
                                        numDirectBits - Base.kNumAlignBits) << Base.kNumAlignBits);
                                    rep0 += m_PosAlignDecoder.ReverseDecode(m_RangeDecoder);
                                }
                            }
                            else
                                rep0 = posSlot;
                        }
                        if (rep0 >= m_OutWindow.TrainSize + nowPos64 || rep0 >= m_DictionarySizeCheck)
                        {
                            if (rep0 == 0xFFFFFFFF)
                                break;
                            throw new IOException("Invalid LZMA data");
                        }
                        m_OutWindow.CopyBlock(rep0, len);
                        nowPos64 += len;
                    }
                }
            }
            m_OutWindow.Flush();
            m_OutWindow.ReleaseStream();
            m_RangeDecoder.ReleaseStream();
        }

        public void SetDecoderProperties(byte[] properties)
        {
            if (properties.Length < 5)
                throw new IOException("Invalid LZMA properties");
            int lc = properties[0] % 9;
            int remainder = properties[0] / 9;
            int lp = remainder % 5;
            int pb = remainder / 5;
            if (pb > Base.kNumPosStatesBitsMax)
                throw new IOException("Invalid LZMA properties");
            UInt32 dictionarySize = 0;
            for (int i = 0; i < 4; i++)
                dictionarySize += ((UInt32)(properties[1 + i])) << (i * 8);
            SetDictionarySize(dictionarySize);
            SetLiteralProperties(lp, lc);
            SetPosBitsProperties(pb);
        }

        public bool Train(System.IO.Stream stream)
        {
            _solid = true;
            return m_OutWindow.Train(stream);
        }

    }
}
