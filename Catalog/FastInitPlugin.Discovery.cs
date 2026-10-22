using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using LevelEditorStub;
using UnityEngine;

namespace DIYLevelFastInit
{
    public partial class FastInitPlugin
    {

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
    }
}
