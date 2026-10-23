using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace DIYLevelFastInit
{
    public partial class FastInitPlugin
    {
        private void ConfigureBundleCache()
        {
            BundleFileCache.Shutdown();
            try
            {
                UseIndividualLevelAssets = Config.Bind("Loading", "EnableIndividualLevelAssets", true,
                    "Load the selected LevelInfo asset when its address is unambiguous; fall back to LevelSetInfo when needed. Restart after changing this option.").Value;
                OptimizeInfoFilesOnStartup = Config.Bind("Loading", "OptimizeInfoBundlesOnStartup", true,
                    "Check custom-level info bundles on startup and replace supported LZMA files with equivalent LZ4 files before discovering levels. Original files are backed up beside each set. Restart after changing this option.").Value;
                RestoreInfoFilesOnStartup = Config.Bind("Loading", "RestoreOriginalInfoBundlesOnStartup", false,
                    "Restore backed-up original info bundles on startup. Restoration takes precedence over optimization. Restart after changing this option.").Value;
                Log.LogInfo("info bundles use source files directly; runtime LZ4 cache is disabled (legacy cache options are ignored)");
            }
            catch (Exception e) { Log.LogWarning("info bundle startup options unavailable; defaults retained: " + e.Message); }
        }

        internal static IEnumerator PrepareInfoBundleFiles(string pluginDir)
        {
            InfoBundleOptimizationBatch batch = null;
            try
            {
                if (!OptimizeInfoFilesOnStartup && !RestoreInfoFilesOnStartup) yield break;
                List<string> paths = new List<string>();
                foreach (DirectoryInfo dir in FastInitHost.SafeGetDirs(Path.Combine(pluginDir, "levels")))
                    foreach (FileInfo file in FastInitHost.SafeGetFiles(dir, "info*")) paths.Add(file.FullName);
                batch = new InfoBundleOptimizationBatch(paths.ToArray(), RestoreInfoFilesOnStartup);
                _infoOptimization = batch;
                InfoFilesPreparing = true;
                InfoOptimizationProgress = (RestoreInfoFilesOnStartup ? "restoring " : "checking ") + "0/" + paths.Count;
                Log.LogInfo("startup info bundle " + (RestoreInfoFilesOnStartup ? "restoration" : "optimization")
                    + " started: " + paths.Count + " files");
                batch.Start();
                while (!batch.Done)
                {
                    UpdateInfoOptimization(batch);
                    yield return null;
                }
                UpdateInfoOptimization(batch);
                Log.LogInfo("startup info bundle preparation complete: " + batch.Completed + "/" + batch.Total);
            }
            finally
            {
                if (batch != null) batch.Dispose();
                if (batch == null || batch.Done)
                {
                    InfoFilesPreparing = false;
                    InfoOptimizationProgress = null;
                    if (ReferenceEquals(_infoOptimization, batch)) _infoOptimization = null;
                }
            }
        }

        private static void UpdateInfoOptimization(InfoBundleOptimizationBatch batch)
        {
            batch.Drain(delegate(string message) { Log.LogInfo(message); });
            if (batch.Done)
            {
                if (ReferenceEquals(_infoOptimization, batch))
                {
                    InfoFilesPreparing = false;
                    InfoOptimizationProgress = null;
                }
                return;
            }
            string current = batch.Current;
            InfoOptimizationProgress = (RestoreInfoFilesOnStartup ? "restoring " : "checking ")
                + batch.Completed + "/" + batch.Total
                + (string.IsNullOrEmpty(current) ? "" : ": " + Path.GetFileName(current));
        }

        private void Update()
        {
            InfoBundleOptimizationBatch batch = _infoOptimization;
            if (!_patchesReady || batch == null) return;
            UpdateInfoOptimization(batch);
            if (batch.Done && ReferenceEquals(_infoOptimization, batch))
            {
                batch.Dispose();
                _infoOptimization = null;
            }
        }
    }
}
