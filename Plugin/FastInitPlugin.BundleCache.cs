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
                if (!Config.Bind("Loading", "EnableLz4BundleCache", true,
                    "Build a disposable LZ4 cache for info_dmm while the frontend is idle. Original maps are never modified. Restart after changing this option.").Value) return;
                BundleFileCache.Configure(Path.Combine(Path.Combine(Paths.CachePath, "DIYLevelFastInit"), "bundles"),
                    delegate(string message) { Log.LogInfo(message); });
            }
            catch (Exception e) { Log.LogWarning("bundle cache unavailable; original bundles retained: " + e.Message); }
        }

        private void Update()
        {
            if (_patchesReady) BundleFileCache.Tick(OnDemandResources.CanBuildBundleCache);
        }
    }
}
