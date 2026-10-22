using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using OC2DIYLevel;
using UnityEngine;

namespace DIYLevelFastInit
{
    /// <summary>
    /// Keeps a lightweight DIY level catalog, loading info resources and building
    /// configurations only for the selected level. Retains async discovery and hot sync.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("Overcooked2.exe")]
    [BepInDependency("dev.gua.overcooked.diylevel")]
    public partial class FastInitPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "oc2.diylevel.fastinit";
        public const string PluginName = "DIYLevel FastInit";
        public const string PluginVersion = "1.6.16";

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
        internal static bool UseIndividualLevelAssets = true;

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
                ConfigureBundleCache();
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

        private void OnDestroy()
        {
            ClientLoadGate.Cancel("plugin shutdown");
            OnDemandResources.CancelServerPreparation("plugin shutdown");
            OnDemandResources.RestoreEntryPriority("plugin shutdown");
            BundleFileCache.Shutdown();
        }
    }
}
