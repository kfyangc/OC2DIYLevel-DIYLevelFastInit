using System;
using System.IO;
using BepInEx;

namespace DIYLevelFastInit
{
    public partial class FastInitPlugin
    {
        private void ConfigureBundleCache()
        {
            try
            {
                UseIndividualLevelAssets = Config.Bind("Loading", "EnableIndividualLevelAssets", true,
                    "Load the selected LevelInfo asset when its address is unambiguous; fall back to LevelSetInfo when needed. Restart after changing this option.").Value;
                if (!Config.Bind("Loading", "EnableLz4BundleCache", true,
                    "Build disposable LZ4 caches for compatible custom-level info bundles while the frontend is idle. Original maps are never modified. Restart after changing this option.").Value) return;
                int budgetMiB = Config.Bind("Loading", "Lz4BundleCacheSizeMiB", 2048,
                    new BepInEx.Configuration.ConfigDescription("Maximum published LZ4 cache size in MiB. Old unpinned entries are evicted first. Restart after changing this option.",
                        new BepInEx.Configuration.AcceptableValueRange<int>(128, 16384))).Value;
                string root = Path.Combine(Path.Combine(Paths.CachePath, "DIYLevelFastInit"), "bundles");
                BundleFileCache.Configure(root,
                    delegate(string message) { Log.LogInfo(message); }, (long)budgetMiB * 1024 * 1024);
                Log.LogInfo("LZ4 bundle cache enabled: " + root + " (format " + BundleFileCache.FormatVersion
                    + "; info*; " + budgetMiB + " MiB; waiting for idle frontend)");
            }
            catch (Exception e) { Log.LogWarning("bundle cache unavailable; original bundles retained: " + e.Message); }
        }

        private void Update()
        {
            if (_patchesReady) BundleFileCache.Tick(OnDemandResources.CanBuildBundleCache);
        }
    }
}
