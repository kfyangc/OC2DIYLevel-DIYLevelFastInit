using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LevelEditorStub;
using OC2DIYLevel;
using OC2DIYLevel.Extension;
using Team17.Online;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

namespace DIYLevelFastInit
{

    internal static partial class OnDemandResources
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
        private static HashSet<string> _clientPreparingBundles;
        private static bool _serverPreparing, _resumeServerLoad;
        private static SelectSaveDialog _visibleSaveDialog;
        private static readonly FieldInfo ClientSavePayload = AccessTools.Field(typeof(T17FrontendFlow), "m_ClientSavePayload");
        private static readonly FieldInfo ClientCountdown = AccessTools.Field(typeof(T17FrontendFlow), "m_bShouldClientCountdown");
        private static readonly FieldInfo OnlineSaveDialog = AccessTools.Field(typeof(T17FrontendFlow), "m_OnlineClientSaveDialog");
        private static readonly FieldInfo LocalSaveDialog = AccessTools.Field(typeof(T17FrontendFlow), "m_saveDialog");
        private static readonly SaveSelectionState SaveSelection = new SaveSelectionState();
        private static int _selectionFrame = -1;
        private static readonly HashSet<SaveSlotElement> SaveReadyCallbacks = new HashSet<SaveSlotElement>();
        private static bool _entryPriorityActive;
        private static UnityEngine.ThreadPriority _previousEntryPriority;
        private static string _entryPriorityScene;
        private static System.Diagnostics.Stopwatch _entryPriorityTimer;
        private static long _entrySceneHandoff;
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
        private static long _serverPreparationGeneration;
        private static readonly Dictionary<string, BundleFileCache.Lease> InfoBundleLeases = new Dictionary<string, BundleFileCache.Lease>(StringComparer.OrdinalIgnoreCase);

        internal static bool IsTransitioning { get { return _pendingScene != null; } }
        internal static bool IsClientTransition { get { return IsTransitioning && !ConnectionStatusPublicizer.IsHost() && ConnectionStatusPublicizer.IsInSession(); } }
        internal static bool HasDiySaveSelection { get { return _visibleSaveDialog != null && _visibleSaveDialog.DLC == 15; } }
        internal static bool CanBuildBundleCache
        {
            get
            {
                return !FastInitPlugin.Loading && !FastInitPlugin.Syncing && !IsTransitioning
                    && !PseudoPrefabManager.isInCustomLevel && !_serverPreparing
                    && _catalogBundles == null && _previewBundles == null && _warmBundles == null
                    && PendingBundles.Count == 0 && PendingInfos.Count == 0
                    && Time.realtimeSinceStartup - _lastPreviewTime >= 0.5f;
            }
        }

        internal static void Install(Harmony harmony)
        {
            if (ClientSavePayload == null || ClientCountdown == null || OnlineSaveDialog == null || LocalSaveDialog == null)
                throw new MissingMemberException("DIY client save transition contract changed");
            ClientLoadGate.Install(harmony);
            Patch(harmony, typeof(DIYLevelAssetBundleManager), "LoadLevelServer", nameof(ServerLoadPrefix), null);
            Patch(harmony, typeof(DIYLevelAssetBundleManager), "LoadLevelClientPatch", nameof(LoadPrefix), null);
            Patch(harmony, typeof(DIYLevelEntryUI), "OnLevelSelected", nameof(SelectionPrefix), null);
            Patch(harmony, typeof(SelectSaveDialog), "Show", nameof(SaveDialogShowPrefix), nameof(SaveDialogShowPostfix));
            Patch(harmony, typeof(SelectSaveDialog), "Hide", nameof(SaveDialogHidePrefix), nameof(SaveDialogHidePostfix));
            Patch(harmony, typeof(SelectSaveDialog), "OnDontSaveSelected", null, nameof(DontSavePostfix));
            Patch(harmony, typeof(T17FrontendFlow), "ShowClientSaveDialog", nameof(ClientSaveDialogPrefix), null);
            Patch(harmony, typeof(T17FrontendFlow), "StartClientCountdown", nameof(ClientCountdownPrefix), null);
            Patch(harmony, typeof(ServerUserSystem), "OnClientGameStateChanged", null, nameof(ServerSaveStatePostfix));
            harmony.Patch(AccessTools.Method(typeof(SaveSlotElement), "OnSaveGameReady"),
                prefix: new HarmonyMethod(typeof(OnDemandResources), nameof(SaveReadyPrefix)),
                finalizer: new HarmonyMethod(typeof(OnDemandResources), nameof(SaveReadyFinalizer)));
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
    }
}
