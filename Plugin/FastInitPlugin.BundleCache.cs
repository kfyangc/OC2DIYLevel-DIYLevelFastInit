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
                    "Build a disposable LZ4 cache for info_dmm while the frontend is idle. Original maps are never modified. Restart after changing this option.").Value) return;
                string root = Path.Combine(Path.Combine(Paths.CachePath, "DIYLevelFastInit"), "bundles");
                BundleFileCache.Configure(root,
                    delegate(string message) { Log.LogInfo(message); });
                Log.LogInfo("LZ4 bundle cache enabled: " + root + " (info_dmm; waiting for idle frontend)");
            }
            catch (Exception e) { Log.LogWarning("bundle cache unavailable; original bundles retained: " + e.Message); }
        }

        private void Update()
        {
            if (_patchesReady) BundleFileCache.Tick(OnDemandResources.CanBuildBundleCache);
        }
    }
}
