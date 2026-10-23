using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using LevelEditorStub;
using OC2DIYLevel;
using OC2DIYLevel.CustomArcade;
using UnityEngine;

namespace DIYLevelFastInit
{
    public partial class FastInitPlugin
    {

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
    }
}
